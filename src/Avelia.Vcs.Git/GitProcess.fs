namespace Avelia.Vcs.Git

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks

/// Captured stdout/stderr + exit code from one <c>git.exe</c> invocation.
type GitProcessResult =
    { ExitCode: int
      StdOut: string
      StdErr: string }

/// Subprocess driver for <c>git.exe</c>. Discipline:
///  - <c>UseShellExecute = false</c>, both pipes redirected, UTF-8 encoding.
///  - Force <c>LC_ALL=C.UTF-8</c> so machine output is locale-stable.
///  - Force <c>GIT_TERMINAL_PROMPT=0</c> so a misconfigured credential helper
///    can never block us on a TTY prompt; the user's Git Credential Manager
///    handles auth out-of-band.
///  - Arguments are passed via <c>ArgumentList</c> (not a joined string) so
///    no shell-quoting bugs.
///  - Process group is killed on cancellation so a hung <c>git fetch</c>
///    against a dead remote doesn't leak.
[<RequireQualifiedAccess>]
module GitProcess =

    /// Path to the <c>git</c> executable to invoke. Resolved off <c>PATH</c>.
    /// Held as an immutable <c>let</c> rather than <c>mutable</c> so tests
    /// can't race each other by reassigning it mid-run; if we ever need a
    /// per-call override we'll add an explicit parameter to <c>runAsync</c>.
    let Executable = "git"

    let private startInfo (cwd: string) (args: string seq) : ProcessStartInfo =
        let psi =
            ProcessStartInfo(
                FileName = Executable,
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            )

        for a in args do
            psi.ArgumentList.Add a

        // C.UTF-8 keeps porcelain output ASCII and stable across user locales;
        // GIT_TERMINAL_PROMPT=0 disables interactive auth prompts so a hang
        // becomes an error.
        psi.Environment.["LC_ALL"] <- "C.UTF-8"
        psi.Environment.["GIT_TERMINAL_PROMPT"] <- "0"
        psi

    /// Spawn <c>git.exe</c> in <paramref name="cwd"/> with <paramref name="args"/>
    /// and read both pipes to completion. Throws <see cref="OperationCanceledException"/>
    /// if <paramref name="ct"/> fires before the child exits — the process tree
    /// is killed first.
    ///
    /// <b>Output bounds.</b> Captured stdout/stderr live in unbounded
    /// <c>StringBuilder</c>s; callers are responsible for limiting volume
    /// (e.g. <c>git log -n 100</c> rather than an unbounded walk). Streaming
    /// large outputs through this function will allocate proportionally and
    /// can OOM the process — use a dedicated streaming variant for that
    /// case if/when we need it.
    let runAsync (cwd: string) (args: string seq) (ct: CancellationToken) : Task<GitProcessResult> =
        task {
            use proc = new Process(StartInfo = startInfo cwd args)
            proc.EnableRaisingEvents <- true

            let stdOut = StringBuilder()
            let stdErr = StringBuilder()

            // Buffer both streams asynchronously so a process that writes
            // megabytes to stdout (e.g. `log --oneline` against linux.git)
            // doesn't deadlock on a full pipe.
            proc.OutputDataReceived.Add(fun e ->
                if not (isNull e.Data) then
                    stdOut.AppendLine(e.Data) |> ignore)

            proc.ErrorDataReceived.Add(fun e ->
                if not (isNull e.Data) then
                    stdErr.AppendLine(e.Data) |> ignore)

            if not (proc.Start()) then
                invalidOp "Failed to start git process."

            proc.BeginOutputReadLine()
            proc.BeginErrorReadLine()

            try
                do! proc.WaitForExitAsync ct
            with :? OperationCanceledException as ex ->
                // Kill the process tree so an interrupted `git fetch` doesn't
                // leak. Swallow the kill's own errors — the child may already
                // be dead — but re-raise the original cancellation.
                try
                    proc.Kill(entireProcessTree = true)
                with _ ->
                    ()

                raise ex

            // WaitForExitAsync returns when the process tree exits, but the
            // async stream callbacks may still be flushing — WaitForExit() with
            // no arg blocks until those finish (and is a no-op now the process
            // is dead).
            proc.WaitForExit()

            return
                { ExitCode = proc.ExitCode
                  StdOut = stdOut.ToString()
                  StdErr = stdErr.ToString() }
        }

    /// Convenience: trim trailing whitespace from <c>StdOut</c>. Used for
    /// single-value commands like <c>git rev-parse HEAD</c> where the
    /// trailing newline isn't meaningful.
    let trimmedStdOut (r: GitProcessResult) : string = r.StdOut.TrimEnd('\r', '\n', ' ', '\t')
