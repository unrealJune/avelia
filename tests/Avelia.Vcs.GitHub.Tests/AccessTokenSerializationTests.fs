module Avelia.Vcs.GitHub.Tests.AccessTokenSerializationTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub.Auth

// ----------------------------------------------------------------------------
//  Property: serialize -> deserialize is the identity (modulo our normalisation
//  rules — null -> "", null array -> empty array). These rules apply to BOTH
//  the input and the round-tripped output so we normalise inputs once and
//  compare like-for-like.
//
//  Per CLAUDE.md the codebase rule is "every serializer must round-trip" —
//  this is that property for GitHubAccessToken.
// ----------------------------------------------------------------------------

module private Gen =
    // FsCheck's default string generator includes the empty string and
    // arbitrary unicode; both are valid token blobs. We reject null only
    // because the F# record fields don't admit null (the production code
    // would treat null as "" anyway, but the generator can be precise).
    let nonNullString: Gen<string> =
        ArbMap.defaults
        |> ArbMap.generate<string>
        |> Gen.map (fun s -> if isNull s then "" else s)

    let nonNullStringArray: Gen<string array> =
        Gen.listOf nonNullString |> Gen.map List.toArray

    let authMethod: Gen<AuthMethod> =
        Gen.elements [ AuthMethod.GitHubApp; AuthMethod.OAuthApp; AuthMethod.Pat ]

    let dateTimeOffset: Gen<DateTimeOffset> =
        // Stick within a sensible range — DateTimeOffset.MinValue / MaxValue
        // round-trip cleanly through System.Text.Json, but uncommonly-stored
        // edge values aren't what we care about. Cover the full
        // "wall-clock + max" range so the MaxValue sentinel is exercised.
        Gen.frequency
            [ 1, Gen.constant DateTimeOffset.MaxValue
              1, Gen.constant DateTimeOffset.MinValue
              8,
              (gen {
                  let! secsFromEpoch = Gen.choose (0, 2_000_000_000)
                  return DateTimeOffset.FromUnixTimeSeconds(int64 secsFromEpoch)
              }) ]

    /// Generator for valid GitHub logins. Must round-trip through
    /// <see cref="GitHubLogin.TryCreate"/>, so we synthesise from a
    /// constrained character class instead of using FsCheck's default
    /// string generator (which produces empty / whitespace / control
    /// characters that the smart constructor rejects).
    let login: Gen<GitHubLogin> =
        let allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"

        gen {
            let! len = Gen.choose (1, 24)
            let! chars = Gen.listOfLength len (Gen.elements allowed)
            let s = System.String(chars |> List.toArray)
            return GitHubLogin.Create s
        }

    let token: Gen<GitHubAccessToken> =
        gen {
            let! account = login
            let! tok = nonNullString
            let! method' = authMethod
            let! scopes = nonNullStringArray
            let! expires = dateTimeOffset
            let! refresh = nonNullString
            let! refreshExpires = dateTimeOffset

            return
                { Account = account
                  Token = tok
                  Method = method'
                  ScopesGranted = scopes
                  ExpiresAt = expires
                  RefreshToken = refresh
                  RefreshExpiresAt = refreshExpires }
        }

type Arbs =
    static member Token() = Arb.fromGen Gen.token
    static member AuthMethod() = Arb.fromGen Gen.authMethod
    static member GitHubLogin() = Arb.fromGen Gen.login

[<Property(Arbitrary = [| typeof<Arbs> |])>]
let ``serialize then deserialize is identity`` (token: GitHubAccessToken) =
    let json = TokenSerializer.serialize token

    match TokenSerializer.deserialize json with
    | Success roundtripped ->
        roundtripped.Account = token.Account
        && roundtripped.Token = token.Token
        && roundtripped.Method = token.Method
        && (roundtripped.ScopesGranted = token.ScopesGranted)
        && roundtripped.ExpiresAt = token.ExpiresAt
        && roundtripped.RefreshToken = token.RefreshToken
        && roundtripped.RefreshExpiresAt = token.RefreshExpiresAt
    | Failure e -> failwithf "Round-trip failed: %A\nJSON: %s" e json

[<Property(Arbitrary = [| typeof<Arbs> |])>]
let ``serialize emits valid JSON parseable by System.Text.Json`` (token: GitHubAccessToken) =
    let json = TokenSerializer.serialize token
    // System.Text.Json.JsonDocument.Parse will throw if the output isn't
    // valid JSON. Catching here lets us surface a clean test failure
    // rather than a cryptic exception.
    let _ = System.Text.Json.JsonDocument.Parse json
    true

[<Fact>]
let ``deserialize rejects unknown auth method`` () =
    let json =
        """{"v":1,"Account":"a","Token":"t","Method":"weird-method","ScopesGranted":[],"ExpiresAt":"9999-12-31T23:59:59.9999999+00:00","RefreshToken":"","RefreshExpiresAt":"9999-12-31T23:59:59.9999999+00:00"}"""

    match TokenSerializer.deserialize json with
    | Success _ -> Assert.Fail "Expected Failure on unknown method tag."
    | Failure(AveliaError.Validation msg) -> Assert.Contains("weird-method", msg)
    | Failure other -> Assert.Fail $"Wrong failure shape: {other}"

[<Fact>]
let ``deserialize rejects malformed JSON`` () =
    match TokenSerializer.deserialize "{not valid" with
    | Success _ -> Assert.Fail "Expected Failure on malformed JSON."
    | Failure(AveliaError.Validation _) -> ()
    | Failure other -> Assert.Fail $"Wrong failure shape: {other}"

[<Fact>]
let ``deserialize normalises missing string fields to empty sentinels`` () =
    // Wire format where most fields are omitted but Account + Method
    // are present (the two required fields after the smart-ctor
    // tightening — Account must round-trip through GitHubLogin.TryCreate).
    let json = """{"v":1,"Account":"octocat","Method":"pat"}"""

    match TokenSerializer.deserialize json with
    | Success token ->
        Assert.Equal("octocat", token.Account.Value)
        Assert.Equal("", token.Token)
        Assert.Equal(AuthMethod.Pat, token.Method)
        Assert.Empty(token.ScopesGranted)
        Assert.Equal("", token.RefreshToken)
    | Failure e -> Assert.Fail $"Unexpected failure: {e}"

[<Fact>]
let ``deserialize rejects an empty Account in the stored blob`` () =
    // Account = "" would mean "I don't know who this token belongs to",
    // which the type system (GitHubLogin smart-ctor) refuses to
    // construct. A corrupt vault entry surfaces as Validation rather
    // than a synthetic placeholder login.
    let json = """{"v":1,"Account":"","Method":"pat"}"""

    match TokenSerializer.deserialize json with
    | Success _ -> Assert.Fail "Expected Failure on empty Account."
    | Failure(AveliaError.Validation msg) -> Assert.Contains("Account", msg)
    | Failure other -> Assert.Fail $"Wrong failure shape: {other}"
