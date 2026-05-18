module Avelia.Vcs.GitHub.Tests.CredentialStoreTests

open System
open System.Threading
open Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub.Auth

// ----------------------------------------------------------------------------
//  Integration tests against the real Windows Credential Manager.
//
//  These create/read/delete a credential under a unique, test-scoped key so
//  parallel runs don't trample on one another; the key carries a GUID so a
//  crashed test doesn't pollute the developer's vault permanently. The
//  fixture's cleanup deletes the key in all cases.
//
//  Categorised "Integration" so test-fast.ps1 skips them — Credential Manager
//  is not available in non-Windows CI environments and pulling it in for the
//  unit tier would gate the inner loop on platform.
// ----------------------------------------------------------------------------

let private ct = CancellationToken.None

/// Generate a guaranteed-unique key for one test. The fixture cleans up
/// after itself, but the GUID gives us belt-and-braces isolation.
let private uniqueKey () =
    sprintf "avelia:test:credstore:%s" (Guid.NewGuid().ToString("N"))

let private assertSuccess (label: string) (r: OperationResult<'T>) : 'T =
    match r with
    | Success v -> v
    | Failure e -> failwithf "%s: %A" label e

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Set then Get round-trips a secret`` () =
    let store = WindowsCredentialStore() :> ICredentialStore
    let key = uniqueKey ()
    let secret = "the-quick-brown-fox-" + Guid.NewGuid().ToString("N")

    try
        store.SetAsync(key, secret, ct).GetAwaiter().GetResult()
        |> assertSuccess "SetAsync"

        let loaded =
            store.GetAsync(key, ct).GetAwaiter().GetResult() |> assertSuccess "GetAsync"

        Assert.Equal(secret, loaded)
    finally
        // Best-effort cleanup so a failed assertion doesn't leak the
        // credential in the developer's vault.
        store.DeleteAsync(key, ct).GetAwaiter().GetResult() |> ignore

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Get on a missing key returns NotFound`` () =
    let store = WindowsCredentialStore() :> ICredentialStore
    let key = uniqueKey ()
    // Guarantee absence — best-effort delete before the assertion.
    store.DeleteAsync(key, ct).GetAwaiter().GetResult() |> ignore

    match store.GetAsync(key, ct).GetAwaiter().GetResult() with
    | Failure(AveliaError.NotFound resource) -> Assert.Contains(key, resource)
    | other -> Assert.Fail $"Expected NotFound, got {other}"

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Delete on a missing key is idempotent`` () =
    let store = WindowsCredentialStore() :> ICredentialStore
    let key = uniqueKey ()
    // Two deletes back to back — first one ensures absence, second one
    // exercises the idempotency contract.
    store.DeleteAsync(key, ct).GetAwaiter().GetResult()
    |> assertSuccess "first DeleteAsync"

    store.DeleteAsync(key, ct).GetAwaiter().GetResult()
    |> assertSuccess "second DeleteAsync"

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Set overwrites an existing value`` () =
    let store = WindowsCredentialStore() :> ICredentialStore
    let key = uniqueKey ()

    try
        store.SetAsync(key, "first", ct).GetAwaiter().GetResult()
        |> assertSuccess "first SetAsync"

        store.SetAsync(key, "second", ct).GetAwaiter().GetResult()
        |> assertSuccess "second SetAsync"

        let loaded =
            store.GetAsync(key, ct).GetAwaiter().GetResult() |> assertSuccess "GetAsync"

        Assert.Equal("second", loaded)
    finally
        store.DeleteAsync(key, ct).GetAwaiter().GetResult() |> ignore

[<Trait("Category", "Integration")>]
[<Fact>]
let ``Delete removes the credential`` () =
    let store = WindowsCredentialStore() :> ICredentialStore
    let key = uniqueKey ()

    store.SetAsync(key, "x", ct).GetAwaiter().GetResult()
    |> assertSuccess "SetAsync"

    store.DeleteAsync(key, ct).GetAwaiter().GetResult()
    |> assertSuccess "DeleteAsync"

    match store.GetAsync(key, ct).GetAwaiter().GetResult() with
    | Failure(AveliaError.NotFound _) -> ()
    | other -> Assert.Fail $"Expected NotFound after delete, got {other}"

[<Trait("Category", "Integration")>]
[<Fact>]
let ``TokenStore round-trips a full GitHubAccessToken`` () =
    let store = WindowsCredentialStore() :> ICredentialStore
    let tokenStore = TokenStore store
    let login = "avelia-test-" + Guid.NewGuid().ToString("N").Substring(0, 8)

    let token =
        { Account = login
          Token = "ghp_secret_value"
          Method = AuthMethod.Pat
          ScopesGranted = [| "repo"; "read:user" |]
          ExpiresAt = DateTimeOffset.MaxValue
          RefreshToken = ""
          RefreshExpiresAt = DateTimeOffset.MaxValue }

    try
        tokenStore.SaveAsync(token, ct).GetAwaiter().GetResult()
        |> assertSuccess "SaveAsync"

        let loaded =
            tokenStore.LoadAsync(login, ct).GetAwaiter().GetResult()
            |> assertSuccess "LoadAsync"

        Assert.Equal(token.Account, loaded.Account)
        Assert.Equal(token.Token, loaded.Token)
        Assert.Equal(token.Method, loaded.Method)
        Assert.Equal<string[]>(token.ScopesGranted, loaded.ScopesGranted)
    finally
        tokenStore.DeleteAsync(login, ct).GetAwaiter().GetResult() |> ignore
