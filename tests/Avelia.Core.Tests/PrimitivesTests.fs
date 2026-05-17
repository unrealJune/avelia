module Avelia.Core.Tests.PrimitivesTests

open Xunit
open Avelia.Core.Abstractions

// ----- BranchName -----

[<Fact>]
let ``BranchName.TryCreate accepts simple name`` () =
    match BranchName.TryCreate "feature-x" with
    | Ok _ -> ()
    | Error msg -> Assert.Fail $"Expected success, got: {msg}"

[<Fact>]
let ``BranchName.TryCreate rejects empty`` () =
    match BranchName.TryCreate "" with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Expected failure on empty branch name."

[<Fact>]
let ``BranchName.TryCreate rejects whitespace-only`` () =
    match BranchName.TryCreate "   " with
    | Error _ -> ()
    | Ok _ -> Assert.Fail "Expected failure on whitespace-only branch name."

[<Theory>]
[<InlineData("with space")>]
[<InlineData("has\ttab")>]
[<InlineData("colon:bad")>]
[<InlineData("brack[et")>]
[<InlineData("ques?mark")>]
[<InlineData("star*char")>]
[<InlineData("back\\slash")>]
[<InlineData("til~de")>]
[<InlineData("car^et")>]
let ``BranchName.TryCreate rejects metacharacters`` (s: string) =
    match BranchName.TryCreate s with
    | Error _ -> ()
    | Ok _ -> Assert.Fail $"Expected failure for input: {s}"

[<Fact>]
let ``BranchName.TryCreate rejects leading slash`` () =
    Assert.True(match BranchName.TryCreate "/leading" with Error _ -> true | _ -> false)

[<Fact>]
let ``BranchName.TryCreate rejects trailing slash`` () =
    Assert.True(match BranchName.TryCreate "trailing/" with Error _ -> true | _ -> false)

[<Fact>]
let ``BranchName.TryCreate rejects double-dot`` () =
    Assert.True(match BranchName.TryCreate "weird..segment" with Error _ -> true | _ -> false)

[<Fact>]
let ``BranchName.Create throws on invalid input`` () =
    Assert.Throws<System.ArgumentException>(fun () -> BranchName.Create "" |> ignore)
    |> ignore

[<Fact>]
let ``BranchName.Value round-trips`` () =
    let b = BranchName.Create "kampala-v3"
    Assert.Equal("kampala-v3", b.Value)

// ----- RepoPath -----

[<Fact>]
let ``RepoPath rejects parent-traversal`` () =
    Assert.True(match RepoPath.TryCreate "C:/work/../etc" with Error _ -> true | _ -> false)

[<Fact>]
let ``RepoPath accepts absolute path`` () =
    match RepoPath.TryCreate "C:/work/conductor" with
    | Ok _ -> ()
    | Error msg -> Assert.Fail $"Expected success, got: {msg}"

// ----- RelativePath -----

[<Fact>]
let ``RelativePath rejects leading slash`` () =
    Assert.True(match RelativePath.TryCreate "/abs/path" with Error _ -> true | _ -> false)

[<Fact>]
let ``RelativePath normalizes backslashes`` () =
    let p = RelativePath.Create "src\\foo\\bar.tsx"
    Assert.Equal("src/foo/bar.tsx", p.Value)

[<Fact>]
let ``RelativePath Folder + FileName split correctly`` () =
    let p = RelativePath.Create "src/ui/components/Dialog.tsx"
    Assert.Equal("src/ui/components/", p.Folder)
    Assert.Equal("Dialog.tsx", p.FileName)

[<Fact>]
let ``RelativePath top-level file has empty folder`` () =
    let p = RelativePath.Create "README.md"
    Assert.Equal("", p.Folder)
    Assert.Equal("README.md", p.FileName)

[<Fact>]
let ``RelativePath rejects traversal`` () =
    Assert.True(match RelativePath.TryCreate "src/../etc/passwd" with Error _ -> true | _ -> false)
