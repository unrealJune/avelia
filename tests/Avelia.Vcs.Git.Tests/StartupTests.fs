module Avelia.Vcs.Git.Tests.StartupTests

open System
open Xunit
open Avelia.Vcs.Git

[<Fact>]
let ``LongPathsState.Match routes Enabled`` () =
    let s = LongPathsState.Enabled

    let label =
        s.Match(
            onEnabled = Func<_>(fun () -> "enabled"),
            onDisabled = Func<_>(fun () -> "disabled"),
            onUnknown = Func<_, _>(fun _ -> "unknown")
        )

    Assert.Equal("enabled", label)

[<Fact>]
let ``LongPathsState.Match routes Disabled`` () =
    let s = LongPathsState.Disabled

    let label =
        s.Match(
            onEnabled = Func<_>(fun () -> "enabled"),
            onDisabled = Func<_>(fun () -> "disabled"),
            onUnknown = Func<_, _>(fun _ -> "unknown")
        )

    Assert.Equal("disabled", label)

[<Fact>]
let ``LongPathsState.Match passes detail to Unknown`` () =
    let s = LongPathsState.Unknown "git not on PATH"

    let label =
        s.Match(
            onEnabled = Func<_>(fun () -> "enabled"),
            onDisabled = Func<_>(fun () -> "disabled"),
            onUnknown = Func<_, _>(fun d -> $"unknown:{d}")
        )

    Assert.Equal("unknown:git not on PATH", label)
