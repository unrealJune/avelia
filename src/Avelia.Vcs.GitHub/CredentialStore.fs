namespace Avelia.Vcs.GitHub.Auth

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions
open Meziantou.Framework.Win32

// ============================================================================
//  Credential-store layer
//
//  The <see cref="ICredentialStore"/> interface is declared in
//  <c>Avelia.Core.Abstractions</c> so future macOS Keychain / Linux libsecret
//  backends slot in. This file holds:
//
//    * <c>WindowsCredentialStore</c> — Win32 Credential Manager backend via
//      <see cref="CredentialManager"/> from
//      <c>Meziantou.Framework.Win32.CredentialManager</c>. Persistence scope is
//      <c>LocalMachine</c> (user profile-scoped, survives logon sessions).
//
//    * <c>TokenSerializer</c> — JSON encode/decode for
//      <see cref="GitHubAccessToken"/> values stored in the vault. Pulled out
//      so the (property-tested) round-trip lives in one place independent of
//      the secret-store backend.
//
//    * <c>TokenStore</c> — composes a credential store with the serializer and
//      a key convention so callers say "save this GitHub token for login X"
//      without re-spelling the <c>"avelia:github:..."</c> prefix everywhere.
// ============================================================================

// ---------------------------------------------------------------------------
//  Token serializer — JSON, property-tested round-trip
// ---------------------------------------------------------------------------

/// JSON-on-disk shape of an <see cref="AuthMethod"/>. We avoid leaking the
/// F# DU discriminator into the on-disk format because case names are an
/// API and renaming them shouldn't break stored tokens.
[<RequireQualifiedAccess>]
module internal AuthMethodWire =
    let toWire (m: AuthMethod) : string =
        match m with
        | AuthMethod.GitHubApp -> "github-app"
        | AuthMethod.OAuthApp -> "oauth-app"
        | AuthMethod.Pat -> "pat"

    let tryFromWire (s: string) : AuthMethod voption =
        match s with
        | "github-app" -> ValueSome AuthMethod.GitHubApp
        | "oauth-app" -> ValueSome AuthMethod.OAuthApp
        | "pat" -> ValueSome AuthMethod.Pat
        | _ -> ValueNone

/// JSON DTO mirroring <see cref="GitHubAccessToken"/>. Stable, on-disk,
/// versioned (<c>"v"</c> integer is reserved for future migrations even
/// though it is unused today — adding the field later would invalidate every
/// stored secret).
///
/// Field types are nullable (<c>string | null</c>, <c>string array | null</c>)
/// because the JSON deserializer can set them to <c>null</c> for any
/// property the wire payload omits or sets to <c>null</c> explicitly. The
/// serializer normalizes back to the empty-sentinel convention at the
/// <see cref="TokenSerializer.deserialize"/> boundary.
///
/// Type is <c>public</c> (not <c>internal</c>) so System.Text.Json's
/// reflection-based contract resolver can enumerate the public properties.
/// Hiding it would silently produce <c>{}</c> at serialization time —
/// property enumeration walks public types only.
type TokenDto() =
    member val V: int = 1 with get, set
    member val Account: string | null = "" with get, set
    member val Token: string | null = "" with get, set
    member val Method: string | null = "" with get, set
    member val ScopesGranted: (string array) | null = Array.empty with get, set
    member val ExpiresAt: DateTimeOffset = DateTimeOffset.MaxValue with get, set
    member val RefreshToken: string | null = "" with get, set
    member val RefreshExpiresAt: DateTimeOffset = DateTimeOffset.MaxValue with get, set

[<RequireQualifiedAccess>]
module TokenSerializer =

    let private options =
        let o = JsonSerializerOptions(WriteIndented = false)
        o.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
        o

    /// Helper: collapse a possibly-null string to its empty-sentinel.
    /// Keeps the call sites readable when normalising deserialised data.
    let inline private orEmpty (s: string | null) : string = if isNull s then "" else nonNull s

    let inline private orEmptyArray (a: (string array) | null) : string array =
        if isNull a then Array.empty else nonNull a

    /// Encode a token as a single-line JSON string. Output is canonical: no
    /// indentation, no field ordering variation across runs.
    let serialize (token: GitHubAccessToken) : string =
        let dto = TokenDto()
        dto.V <- 1
        dto.Account <- token.Account
        dto.Token <- token.Token
        dto.Method <- AuthMethodWire.toWire token.Method
        dto.ScopesGranted <- token.ScopesGranted
        dto.ExpiresAt <- token.ExpiresAt
        dto.RefreshToken <- token.RefreshToken
        dto.RefreshExpiresAt <- token.RefreshExpiresAt
        JsonSerializer.Serialize(dto, options)

    /// Decode. Returns <c>Failure (AveliaError.Validation _)</c> on a
    /// malformed blob or unknown <c>method</c> tag — keeps the caller's
    /// <c>match</c> total without surfacing JSON-parser exceptions.
    let deserialize (json: string) : OperationResult<GitHubAccessToken> =
        try
            let dto = JsonSerializer.Deserialize<TokenDto>(json, options)

            match dto with
            | null -> Failure(AveliaError.Validation "Token blob deserialized to null.")
            | dto ->
                let methodTag = orEmpty dto.Method

                match AuthMethodWire.tryFromWire methodTag with
                | ValueNone -> Failure(AveliaError.Validation $"Unknown auth method tag '{methodTag}' in stored token.")
                | ValueSome m ->
                    Success
                        { Account = orEmpty dto.Account
                          Token = orEmpty dto.Token
                          Method = m
                          ScopesGranted = orEmptyArray dto.ScopesGranted
                          ExpiresAt = dto.ExpiresAt
                          RefreshToken = orEmpty dto.RefreshToken
                          RefreshExpiresAt = dto.RefreshExpiresAt }
        with
        | :? JsonException as ex -> Failure(AveliaError.Validation $"Token blob is not valid JSON: {ex.Message}")
        | ex -> Failure(AveliaError.Internal $"Token blob decode failed: {ex.Message}")

// ---------------------------------------------------------------------------
//  Credential keys — central place to spell the "avelia:github:..." convention
// ---------------------------------------------------------------------------

/// Construction helpers for credential-store keys. The shape
/// (<c>"avelia:&lt;facet&gt;:..."</c>) is replicated by anything that scans
/// the vault for known accounts, so we keep it in one place.
[<RequireQualifiedAccess>]
module CredentialKey =
    /// Prefix all GitHub credentials share. Used to list known accounts.
    [<Literal>]
    let GitHubPrefix = "avelia:github:"

    let forGitHubAccount (login: string) : string = GitHubPrefix + login.ToLowerInvariant()

    /// Inverse of <see cref="forGitHubAccount"/>. Returns <c>ValueNone</c> if
    /// the key doesn't carry the GitHub prefix.
    let tryParseGitHubAccount (key: string | null) : string voption =
        match key with
        | null -> ValueNone
        | k when k.StartsWith(GitHubPrefix, StringComparison.OrdinalIgnoreCase) ->
            ValueSome(k.Substring(GitHubPrefix.Length))
        | _ -> ValueNone

// ---------------------------------------------------------------------------
//  Windows credential store
// ---------------------------------------------------------------------------

/// Windows Credential Manager-backed <see cref="ICredentialStore"/>. Reads
/// and writes generic credentials under whatever key the caller supplies; the
/// key convention (<c>"avelia:github:..."</c>) is a caller concern.
///
/// <para>Behaviour contract per <see cref="ICredentialStore"/>:
/// missing-key <c>Get</c> → <c>Failure (NotFound "credential:&lt;key&gt;")</c>;
/// missing-key <c>Delete</c> → <c>Success ()</c> (idempotent);
/// empty-string secrets are legal values and round-trip as themselves.</para>
///
/// <para>The Meziantou wrapper marshals everything synchronously via Win32
/// APIs; we wrap calls in <c>Task.Run</c> so the call site can <c>await</c>
/// without blocking a UI dispatcher thread. The token cancels the
/// <em>waiter</em>; the Win32 call itself completes anyway.</para>
type WindowsCredentialStore() =

    /// Username slot in the credential record. Credential Manager requires
    /// non-empty UserName for generic credentials; we use a fixed sentinel
    /// since the meaningful identifier is the target name (the caller's key).
    let userName = "avelia"

    interface ICredentialStore with
        member _.GetAsync(key: string, ct: CancellationToken) : Task<OperationResult<string>> =
            Task.Run(
                (fun () ->
                    try
                        match CredentialManager.ReadCredential key with
                        | null -> Failure(AveliaError.NotFound $"credential:{key}")
                        | cred ->
                            // Credential Manager stores the secret in
                            // <c>Password</c> for generic credentials.
                            let secret =
                                match cred.Password with
                                | null -> ""
                                | s -> s

                            Success secret
                    with ex ->
                        Failure(AveliaError.External("credential-manager", ex.Message))),
                ct
            )

        member _.SetAsync(key: string, secret: string, ct: CancellationToken) : Task<OperationResult<unit>> =
            Task.Run(
                (fun () ->
                    try
                        let safeSecret =
                            match (box secret) with
                            | null -> ""
                            | _ -> secret

                        CredentialManager.WriteCredential(
                            applicationName = key,
                            userName = userName,
                            secret = safeSecret,
                            persistence = CredentialPersistence.LocalMachine
                        )

                        Success()
                    with ex ->
                        Failure(AveliaError.External("credential-manager", ex.Message))),
                ct
            )

        member _.DeleteAsync(key: string, ct: CancellationToken) : Task<OperationResult<unit>> =
            Task.Run(
                (fun () ->
                    try
                        // Meziantou throws <see cref="Win32Exception"/> with
                        // ERROR_NOT_FOUND (1168) when the credential is
                        // missing. We treat that as success (idempotent
                        // delete is the documented contract).
                        try
                            CredentialManager.DeleteCredential key
                            Success()
                        with :? System.ComponentModel.Win32Exception as ex when ex.NativeErrorCode = 1168 ->
                            Success()
                    with ex ->
                        Failure(AveliaError.External("credential-manager", ex.Message))),
                ct
            )

// ---------------------------------------------------------------------------
//  TokenStore — credential-store-aware GitHub token persistence
// ---------------------------------------------------------------------------

/// Token-store helper layered on top of any <see cref="ICredentialStore"/>.
/// Knows the <c>"avelia:github:&lt;login&gt;"</c> key shape and the
/// <see cref="TokenSerializer"/> wire format; abstracts both away from the
/// auth flow.
///
/// Constructed against the <c>ICredentialStore</c> the caller picked
/// (production: <see cref="WindowsCredentialStore"/>; tests: in-memory fake)
/// so this stays free of platform code.
type TokenStore(store: ICredentialStore) =

    /// Save a token under the canonical GitHub-account key. The account is
    /// taken from <c>token.Account</c>; storing a token whose
    /// <c>Account</c> is empty surfaces as
    /// <c>Failure (Validation _)</c> — callers must resolve the login
    /// (typically via <c>GET /user</c>) before persisting.
    member _.SaveAsync(token: GitHubAccessToken, ct: CancellationToken) : Task<OperationResult<unit>> =
        task {
            if String.IsNullOrWhiteSpace token.Account then
                return
                    Failure(AveliaError.Validation "Cannot store a token without an account login; resolve it first.")
            else
                let key = CredentialKey.forGitHubAccount token.Account
                let payload = TokenSerializer.serialize token
                return! store.SetAsync(key, payload, ct)
        }

    /// Load a previously-stored token for <paramref name="login"/>.
    /// A missing entry surfaces as <c>Failure (NotFound _)</c> per the
    /// credential-store contract; a malformed entry surfaces as
    /// <c>Failure (Validation _)</c>.
    member _.LoadAsync(login: string, ct: CancellationToken) : Task<OperationResult<GitHubAccessToken>> =
        task {
            if String.IsNullOrWhiteSpace login then
                return Failure(AveliaError.Validation "Account login is required.")
            else
                let key = CredentialKey.forGitHubAccount login
                let! raw = store.GetAsync(key, ct)

                match raw with
                | Failure e -> return Failure e
                | Success json -> return TokenSerializer.deserialize json
        }

    /// Delete the token for <paramref name="login"/>. Idempotent — deleting
    /// a missing entry is a no-op success.
    member _.DeleteAsync(login: string, ct: CancellationToken) : Task<OperationResult<unit>> =
        task {
            if String.IsNullOrWhiteSpace login then
                return Failure(AveliaError.Validation "Account login is required.")
            else
                let key = CredentialKey.forGitHubAccount login
                return! store.DeleteAsync(key, ct)
        }
