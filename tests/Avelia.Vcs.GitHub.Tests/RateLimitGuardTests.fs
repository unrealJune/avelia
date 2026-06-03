module Avelia.Vcs.GitHub.Tests.RateLimitGuardTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Avelia.Core.Abstractions
open Avelia.Vcs.GitHub

// ----------------------------------------------------------------------------
//  Pure state-machine + property tests for RateLimitGuard.
//
//  Boundaries:
//    * remaining > 500 → Healthy
//    * 1 ≤ remaining ≤ 499 → Low (back off)
//    * remaining ≤ 0 → Exhausted
//
//  waitUntilOk is always ≥ 0 (never negative even for past ResetAt).
// ----------------------------------------------------------------------------

let private snap remaining resetAt =
    { Limit = 5000
      Remaining = remaining
      ResetAt = resetAt
      LastUpdated = DateTimeOffset.UtcNow }

[<Fact>]
let ``classify ValueNone is Healthy`` () =
    Assert.Equal(RateLimitTier.Healthy, RateLimitGuard.classify ValueNone)

[<Fact>]
let ``classify above threshold is Healthy`` () =
    let s = snap 501 (DateTimeOffset.UtcNow.AddMinutes 30.0)
    Assert.Equal(RateLimitTier.Healthy, RateLimitGuard.classify (ValueSome s))

[<Fact>]
let ``classify exactly at threshold is Low (strict less-than)`` () =
    // The plan calls out "Below 500 remaining, back off"; the guard
    // implements that as `remaining < 500` so exactly 500 stays Healthy.
    let s = snap 499 (DateTimeOffset.UtcNow.AddMinutes 10.0)
    Assert.Equal(RateLimitTier.Low, RateLimitGuard.classify (ValueSome s))

[<Fact>]
let ``classify boundary at 500 stays Healthy`` () =
    let s = snap 500 (DateTimeOffset.UtcNow.AddMinutes 10.0)
    Assert.Equal(RateLimitTier.Healthy, RateLimitGuard.classify (ValueSome s))

[<Fact>]
let ``classify zero remaining is Exhausted`` () =
    let s = snap 0 (DateTimeOffset.UtcNow.AddMinutes 1.0)
    Assert.Equal(RateLimitTier.Exhausted, RateLimitGuard.classify (ValueSome s))

[<Fact>]
let ``classify negative remaining is Exhausted (defensive)`` () =
    let s = snap -1 (DateTimeOffset.UtcNow.AddMinutes 1.0)
    Assert.Equal(RateLimitTier.Exhausted, RateLimitGuard.classify (ValueSome s))

// ============================================================================
//  waitUntilOk
// ============================================================================

[<Fact>]
let ``waitUntilOk is Zero for Healthy snapshot`` () =
    let now = DateTimeOffset.UtcNow
    let s = snap 1000 (now.AddMinutes 30.0)
    Assert.Equal(TimeSpan.Zero, RateLimitGuard.waitUntilOk (ValueSome s) now)

[<Fact>]
let ``waitUntilOk is Zero when no snapshot has been captured`` () =
    Assert.Equal(TimeSpan.Zero, RateLimitGuard.waitUntilOk ValueNone DateTimeOffset.UtcNow)

[<Fact>]
let ``waitUntilOk returns gap-to-reset for Low snapshot`` () =
    let now = DateTimeOffset.UtcNow
    let reset = now.AddMinutes 5.0
    let s = snap 100 reset
    let wait = RateLimitGuard.waitUntilOk (ValueSome s) now
    Assert.Equal(TimeSpan.FromMinutes 5.0, wait)

[<Fact>]
let ``waitUntilOk clamps to Zero for a past ResetAt`` () =
    let now = DateTimeOffset.UtcNow
    let s = snap 0 (now.AddMinutes -1.0)
    let wait = RateLimitGuard.waitUntilOk (ValueSome s) now
    Assert.Equal(TimeSpan.Zero, wait)

// ============================================================================
//  Property tests
// ============================================================================

module private Gen =
    let remaining: Gen<int> = Gen.choose (-100, 6000)
    let secondsFromNow: Gen<int> = Gen.choose (-3600, 3600)

    let snapshot: Gen<RateLimitSnapshot> =
        gen {
            let! r = remaining
            let! s = secondsFromNow

            return
                { Limit = 5000
                  Remaining = r
                  ResetAt = DateTimeOffset.UtcNow.AddSeconds(float s)
                  LastUpdated = DateTimeOffset.UtcNow }
        }

type Arbs =
    static member Snapshot() = Arb.fromGen Gen.snapshot

[<Property(Arbitrary = [| typeof<Arbs> |])>]
let ``waitUntilOk is never negative`` (s: RateLimitSnapshot) =
    let wait = RateLimitGuard.waitUntilOk (ValueSome s) DateTimeOffset.UtcNow
    wait >= TimeSpan.Zero

[<Property(Arbitrary = [| typeof<Arbs> |])>]
let ``classify is total — every snapshot lands in exactly one tier`` (s: RateLimitSnapshot) =
    let tier = RateLimitGuard.classify (ValueSome s)

    match tier with
    | RateLimitTier.Healthy
    | RateLimitTier.Low
    | RateLimitTier.Exhausted -> true

// ============================================================================
//  exceptionToError
// ============================================================================

[<Fact>]
let ``exceptionToError returns None for a non-rate-limit exception`` () =
    let ex = InvalidOperationException "nope"

    match RateLimitGuard.exceptionToError ex DateTimeOffset.UtcNow with
    | ValueNone -> ()
    | ValueSome err -> Assert.Fail $"Unexpected error: {err}"

// Octokit's RateLimitExceededException is constructed from an IResponse;
// building a stand-in is awkward outside an integration test. The
// happy-path mapping (exception → External("github-ratelimit", _)) is
// exercised in the ApiClientTests when Octokit synthesises the
// exception from a 403 response with X-RateLimit-Remaining: 0.
