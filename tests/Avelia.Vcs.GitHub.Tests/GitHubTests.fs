module Avelia.Vcs.GitHub.Tests.GitHubTests

open Xunit
open Avelia.Vcs.GitHub

[<Fact>]
let ``parse owner-slash-repo returns Some`` () =
    let result = RepoCoordinate.parse "owner/repo"
    Assert.Equal(Some { Owner = "owner"; Name = "repo" }, result)

[<Fact>]
let ``parse malformed returns None`` () =
    Assert.Equal(None, RepoCoordinate.parse "no-slash")
