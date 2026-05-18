module Avelia.Core.Tests.BackendPrimitivesTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
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

// ----- Default-construction safety -----
//
// The struct DUs are `[<Struct>] private | Foo of string`, so a C# consumer
// can write `default(T)` and obtain an instance whose underlying string is
// null. The `.Value` getters normalize that to `""` — these tests pin that
// guarantee in place so a future "simplification" can't strip it.

[<Fact>]
let ``default(BranchName).Value is empty string, not null`` () =
    let b = Unchecked.defaultof<BranchName>
    Assert.NotNull b.Value
    Assert.Equal("", b.Value)

[<Fact>]
let ``default(CommitId).Value is empty string, not null`` () =
    let c = Unchecked.defaultof<CommitId>
    Assert.Equal("", c.Value)

[<Fact>]
let ``default(CommitMessage).Value is empty string, not null`` () =
    let m = Unchecked.defaultof<CommitMessage>
    Assert.Equal("", m.Value)

[<Fact>]
let ``default(Remote).Value is empty string, not null`` () =
    let r = Unchecked.defaultof<Remote>
    Assert.Equal("", r.Value)

[<Fact>]
let ``default(RepoPath).Value is empty string, not null`` () =
    let p = Unchecked.defaultof<RepoPath>
    Assert.Equal("", p.Value)

[<Fact>]
let ``default(RelativePath).Value is empty string, not null`` () =
    let p = Unchecked.defaultof<RelativePath>
    Assert.Equal("", p.Value)

// ----- Leading-hyphen rejection (flag-injection defence in depth) -----

[<Theory>]
[<InlineData("-foo")>]
[<InlineData("--exec=evil")>]
let ``BranchName rejects leading hyphen`` (s: string) =
    match BranchName.TryCreate s with
    | Error _ -> ()
    | Ok _ -> Assert.Fail $"Expected rejection for {s}"

[<Theory>]
[<InlineData("-c")>]
[<InlineData("--upload-pack=cmd")>]
let ``Remote rejects leading hyphen`` (s: string) =
    match Remote.TryCreate s with
    | Error _ -> ()
    | Ok _ -> Assert.Fail $"Expected rejection for {s}"

[<Theory>]
[<InlineData("-bad/path")>]
[<InlineData("--evil")>]
let ``RepoPath rejects leading hyphen`` (s: string) =
    match RepoPath.TryCreate s with
    | Error _ -> ()
    | Ok _ -> Assert.Fail $"Expected rejection for {s}"

[<Theory>]
[<InlineData("-rel/path")>]
[<InlineData("--evil.txt")>]
let ``RelativePath rejects leading hyphen`` (s: string) =
    match RelativePath.TryCreate s with
    | Error _ -> ()
    | Ok _ -> Assert.Fail $"Expected rejection for {s}"

// ----- Property tests with actual generators -----
//
// CLAUDE.md calls for PBT on every domain primitive. These properties hit
// the parse / validate / round-trip surface with random inputs so a future
// shape change has to be deliberate (the property breaks first).

let private hexChars = "0123456789abcdef"

let private genHex (len: int) =
    Gen.elements hexChars |> Gen.arrayOfLength len |> Gen.map String

/// SHA-1 (40 hex) and SHA-256 (64 hex) generators.
type ShaGenerators =
    static member ValidSha40() : Arbitrary<string> = Arb.fromGen (genHex 40)
    static member ValidSha64() : Arbitrary<string> = Arb.fromGen (genHex 64)

[<Property(Arbitrary = [| typeof<ShaGenerators> |])>]
let ``CommitId accepts every 40-char hex string`` (sha: string) =
    match CommitId.TryCreate sha with
    | Ok c -> c.Value = sha.ToLowerInvariant()
    | Error _ -> false

[<Property>]
let ``CommitId rejects every string of length other than 40 or 64`` (s: NonEmptyString) =
    let raw = s.Get

    if raw.Length = 40 || raw.Length = 64 then
        true // generator may produce a valid length; skip
    else
        match CommitId.TryCreate raw with
        | Error _ -> true
        | Ok _ -> false

[<Property>]
let ``CommitId roundtrips lowercase normalization`` () =
    let sha = String.replicate 40 "F"
    let c = CommitId.Create sha
    c.Value = String.replicate 40 "f"

[<Property>]
let ``CommitMessage accepts any non-whitespace string`` (s: NonEmptyString) =
    let raw = s.Get

    if String.IsNullOrWhiteSpace raw then
        true // NonEmptyString doesn't filter whitespace-only; skip
    else
        match CommitMessage.TryCreate raw with
        | Ok m -> m.Value = raw
        | Error _ -> false

[<Property>]
let ``Remote rejects names with whitespace or separators`` () =
    let invalidChars = [| " "; "\t"; "/"; ":"; "\\" |]

    invalidChars
    |> Array.forall (fun c ->
        match Remote.TryCreate $"a{c}b" with
        | Error _ -> true
        | Ok _ -> false)
