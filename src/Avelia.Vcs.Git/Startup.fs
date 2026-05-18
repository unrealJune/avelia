namespace Avelia.Vcs.Git

open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions

/// Outcome of <see cref="Startup.checkLongPathsAsync"/>. Distinct from a
/// generic <c>bool</c> so the shell can render the "needs fix" state with
/// confidence the check actually ran (vs. crashed without a verdict).
[<RequireQualifiedAccess>]
type LongPathsState =
    /// Git's <c>core.longpaths</c> is <c>true</c>. Worktree paths plus
    /// <c>.git/worktrees/&lt;n&gt;/</c> plus deep source files won't blow
    /// the legacy MAX_PATH limit.
    | Enabled
    /// Git was reached but reports <c>false</c> or empty for <c>core.longpaths</c>.
    /// Avelia worktree paths are likely to be deep; the shell should surface
    /// the offer-to-fix dialog from the design.
    | Disabled
    /// The check itself failed (git not on PATH, permission denied, etc.).
    /// Treat as "unknown" — neither rejecting startup nor silently assuming OK.
    | Unknown of detail: string

    member this.Match<'TResult>
        (onEnabled: System.Func<'TResult>, onDisabled: System.Func<'TResult>, onUnknown: System.Func<string, 'TResult>)
        : 'TResult =
        match this with
        | Enabled -> onEnabled.Invoke()
        | Disabled -> onDisabled.Invoke()
        | Unknown detail -> onUnknown.Invoke detail

/// Startup-time environment checks for the local git layer. Each function
/// is idempotent and read-only; nothing here mutates the user's config.
[<RequireQualifiedAccess>]
module Startup =

    /// Read <c>core.longpaths</c> from the *global* git config (we don't want
    /// to depend on running inside a repo). Returns <c>LongPathsState</c>
    /// describing the result. The shell calls this once at startup and
    /// renders a Settings → Performance nudge if disabled.
    let checkLongPathsAsync (ct: CancellationToken) : Task<LongPathsState> =
        task {
            try
                let! r = GitProcess.runAsync "." [ "config"; "--global"; "--get"; "core.longpaths" ] ct
                // Exit 1 with empty output means the key isn't set in --global.
                // Treat that as Disabled (the user has never opted in).
                if r.ExitCode <> 0 && r.ExitCode <> 1 then
                    return LongPathsState.Unknown $"exit {r.ExitCode}: {r.StdErr.Trim()}"
                else
                    let value =
                        GitProcess.trimmedStdOut r
                        |> fun s -> s.Trim()
                        |> fun s -> s.ToLowerInvariant()

                    if value = "true" then
                        return LongPathsState.Enabled
                    else
                        return LongPathsState.Disabled
            with ex ->
                return LongPathsState.Unknown ex.Message
        }

    /// Apply the recommended setting — <c>git config --global core.longpaths true</c>.
    /// Distinct from the check so the shell can prompt before mutating user
    /// config. Returns an <c>OperationResult</c> so a failed write surfaces as
    /// UI rather than a crash.
    let enableLongPathsAsync (ct: CancellationToken) : Task<OperationResult<unit>> =
        task {
            try
                let! r = GitProcess.runAsync "." [ "config"; "--global"; "core.longpaths"; "true" ] ct

                if r.ExitCode = 0 then
                    return Success()
                else
                    return Failure(AveliaError.External("git", $"exit {r.ExitCode}: {r.StdErr.Trim()}"))
            with ex ->
                return Failure(AveliaError.External("git", ex.Message))
        }
