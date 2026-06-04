namespace Avelia.Services

open System
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot
open Avelia.Vcs.GitHub.Auth

/// Credential-store key under which a manually-entered PAT is saved
/// (Settings → Agents). Distinct from the per-login keys the device/PAT
/// sign-in flow writes (<c>avelia:github:&lt;login&gt;</c>).
[<RequireQualifiedAccess>]
module GitHubTokenKeys =
    [<Literal>]
    let Pat = "avelia:github:_pat"

/// Resolves a GitHub token for the Copilot SDK, first-non-empty-wins:
///   1. environment — <c>COPILOT_GITHUB_TOKEN</c>, <c>GH_TOKEN</c>, <c>GITHUB_TOKEN</c>;
///   2. a token stored by the sign-in flow (first stored account);
///   3. a PAT pasted into Settings → Agents (credential store);
///   else <c>Unauthorized</c> (the Copilot factory renders that, and the
///   orchestrator surfaces it as an error message in chat).
type GitHubTokenSource(auth: IGitHubAuth, credentials: ICredentialStore, getEnv: string -> string) =

    let envToken () =
        [ "COPILOT_GITHUB_TOKEN"; "GH_TOKEN"; "GITHUB_TOKEN" ]
        |> List.tryPick (fun name ->
            let v = getEnv name
            if String.IsNullOrWhiteSpace v then None else Some v)

    /// Production convenience: reads real process environment variables
    /// (normalizing the nullable BCL result to <c>""</c>).
    new(auth: IGitHubAuth, credentials: ICredentialStore) =
        GitHubTokenSource(
            auth,
            credentials,
            fun name ->
                match Environment.GetEnvironmentVariable name with
                | null -> ""
                | v -> v
        )

    interface IGitHubTokenSource with
        member _.GetTokenAsync(ct) =
            task {
                match envToken () with
                | Some t -> return Success t
                | None ->
                    // Stored account token (device-flow / PAT sign-in).
                    let! stored =
                        task {
                            match! auth.ListStoredAccountsAsync ct with
                            | Success logins when logins.Count > 0 ->
                                match! auth.LoadStoredTokenAsync(logins.[0], ct) with
                                | Success tok -> return (if String.IsNullOrWhiteSpace tok.Token then None else Some tok.Token)
                                | Failure _ -> return None
                            | _ -> return None
                        }

                    match stored with
                    | Some t -> return Success t
                    | None ->
                        // Manually-pasted PAT in the credential vault.
                        match! credentials.GetAsync(GitHubTokenKeys.Pat, ct) with
                        | Success pat when not (String.IsNullOrWhiteSpace pat) -> return Success pat
                        | _ -> return Failure AveliaError.Unauthorized
            }
