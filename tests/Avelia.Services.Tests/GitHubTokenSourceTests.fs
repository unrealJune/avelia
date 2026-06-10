module Avelia.Services.Tests.GitHubTokenSourceTests

open System.Threading
open Xunit
open FsCheck
open FsCheck.Xunit
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot
open Avelia.Services

let private ct = CancellationToken.None

let private make (env: Map<string, string>) (accounts) (pat: string option) =
    let cred = FakeCredentialStore() :> ICredentialStore

    match pat with
    | Some p -> (cred.SetAsync(GitHubTokenKeys.Pat, p, ct)).Result |> ignore
    | None -> ()

    let getEnv name =
        match Map.tryFind name env with
        | Some v -> v
        | None -> ""

    GitHubTokenSource(FakeGitHubAuth accounts, cred, getEnv) :> IGitHubTokenSource

[<Fact>]
let ``environment token wins over everything`` () =
    let src =
        make (Map [ "GH_TOKEN", "env-tok" ]) [ storedToken "octocat" "stored-tok" ] (Some "pat-tok")

    Assert.Equal(Success "env-tok", (src.GetTokenAsync ct).Result)

[<Fact>]
let ``COPILOT_GITHUB_TOKEN takes precedence over GH_TOKEN and GITHUB_TOKEN`` () =
    let env = Map [ "COPILOT_GITHUB_TOKEN", "a"; "GH_TOKEN", "b"; "GITHUB_TOKEN", "c" ]
    Assert.Equal(Success "a", ((make env [] None).GetTokenAsync ct).Result)

[<Fact>]
let ``stored account is used when no env token`` () =
    let src = make Map.empty [ storedToken "octocat" "stored-tok" ] (Some "pat-tok")
    Assert.Equal(Success "stored-tok", (src.GetTokenAsync ct).Result)

[<Fact>]
let ``PAT is used when no env and no stored account`` () =
    let src = make Map.empty [] (Some "pat-tok")
    Assert.Equal(Success "pat-tok", (src.GetTokenAsync ct).Result)

[<Fact>]
let ``no source yields Unauthorized`` () =
    match ((make Map.empty [] None).GetTokenAsync ct).Result with
    | Failure AveliaError.Unauthorized -> ()
    | other -> failwithf "unexpected %A" other

[<Property>]
let ``precedence holds: env > stored > pat`` (hasEnv: bool) (hasStored: bool) (hasPat: bool) =
    let env = if hasEnv then Map [ "GH_TOKEN", "ENV" ] else Map.empty
    let accounts = if hasStored then [ storedToken "octocat" "STORED" ] else []
    let pat = if hasPat then Some "PAT" else None
    let result = ((make env accounts pat).GetTokenAsync ct).Result

    let expected =
        if hasEnv then Success "ENV"
        elif hasStored then Success "STORED"
        elif hasPat then Success "PAT"
        else Failure AveliaError.Unauthorized

    result = expected
