module Avelia.Core.Tests.BackendPrimitivesTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Avelia.Core.Abstractions

// ----- CommitId -----

[<Fact>]
let ``CommitId.TryCreate accepts 40-char hex`` () =
    let sha = String.replicate 40 "a"

    match CommitId.TryCreate sha with
    | Ok c -> Assert.Equal(sha, c.Value)
    | Error msg -> Assert.Fail $"Expected success, got: {msg}"

[<Fact>]
let ``CommitId.TryCreate accepts 64-char SHA-256 hex`` () =
    let sha = String.replicate 64 "f"

    match CommitId.TryCreate sha with
    | Ok _ -> ()
    | Error msg -> Assert.Fail $"Expected success, got: {msg}"

[<Fact>]
let ``CommitId.TryCreate normalizes to lowercase`` () =
    let sha = String.replicate 40 "A"
    let c = CommitId.Create sha
    Assert.Equal(String.replicate 40 "a", c.Value)

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("notasha")>]
[<InlineData("ggggggggggggggggggggggggggggggggggggggggg")>] // 40 g's, not hex
let ``CommitId.TryCreate rejects invalid input`` (s: string) =
    match CommitId.TryCreate s with
    | Error _ -> ()
    | Ok _ -> Assert.Fail $"Expected failure for input: {s}"

[<Fact>]
let ``CommitId.TryCreate rejects 39-char and 41-char hex`` () =
    let s39 = String.replicate 39 "a"
    let s41 = String.replicate 41 "a"

    match CommitId.TryCreate s39, CommitId.TryCreate s41 with
    | Error _, Error _ -> ()
    | _ -> Assert.Fail "Both 39 and 41 char strings should be rejected"

[<Fact>]
let ``CommitId.Create throws on invalid input`` () =
    Assert.Throws<ArgumentException>(fun () -> CommitId.Create "" |> ignore)
    |> ignore

// ----- CommitMessage -----

[<Fact>]
let ``CommitMessage.TryCreate accepts ordinary text`` () =
    match CommitMessage.TryCreate "fix(core): wire up the foo" with
    | Ok m -> Assert.Equal("fix(core): wire up the foo", m.Value)
    | Error msg -> Assert.Fail $"Expected success, got: {msg}"

[<Fact>]
let ``CommitMessage.TryCreate accepts multi-line`` () =
    let body = "feat: thing\n\nThis paragraph explains why."

    match CommitMessage.TryCreate body with
    | Ok m -> Assert.Equal(body, m.Value)
    | Error msg -> Assert.Fail $"Expected success, got: {msg}"

[<Theory>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData("\n\n")>]
let ``CommitMessage.TryCreate rejects empty / whitespace`` (s: string) =
    match CommitMessage.TryCreate s with
    | Error _ -> ()
    | Ok _ -> Assert.Fail $"Expected failure for input: '{s}'"

// ----- Remote -----

[<Fact>]
let ``Remote.TryCreate accepts simple name`` () =
    match Remote.TryCreate "origin" with
    | Ok r -> Assert.Equal("origin", r.Value)
    | Error msg -> Assert.Fail $"Expected success, got: {msg}"

[<Fact>]
let ``Remote.Origin matches manually-constructed value`` () =
    Assert.Equal("origin", Remote.Origin.Value)
    Assert.Equal(Remote.Create "origin", Remote.Origin)

[<Theory>]
[<InlineData("with space")>]
[<InlineData("ori/gin")>]
[<InlineData("colon:bad")>]
[<InlineData("back\\slash")>]
[<InlineData("")>]
let ``Remote.TryCreate rejects invalid names`` (s: string) =
    match Remote.TryCreate s with
    | Error _ -> ()
    | Ok _ -> Assert.Fail $"Expected failure for input: {s}"

[<Property>]
let ``CommitId roundtrips through Value`` () =
    let sha = String.replicate 40 "0"
    let c = CommitId.Create sha
    c.Value = sha
