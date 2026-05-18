namespace Avelia.Vcs.Git

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks

/// Per-repository mutual-exclusion registry. Mutating <c>git</c> operations
/// share <c>.git/packed-refs</c> and the object DB across worktrees of the
/// same repo, so two concurrent commits in different worktrees can race the
/// ref update. We serialize all mutations of a repo behind one
/// <see cref="SemaphoreSlim"/>.
///
/// The lock key is the canonical, case-insensitive form of the path returned
/// by <c>git rev-parse --git-common-dir</c> — i.e. the shared <c>.git</c>
/// directory, not the working tree. Locks are weak-cached: the registry
/// holds one semaphore per key for the life of the process, but they're
/// cheap (a few hundred bytes each) and there's no realistic upper bound on
/// distinct repos in a long-running app.
[<RequireQualifiedAccess>]
module RepoLocks =

    // Values are <c>Lazy</c>-wrapped so a racy <c>GetOrAdd</c> on the same
    // key never constructs (and leaks — <c>SemaphoreSlim</c> is
    // <c>IDisposable</c>) more than one semaphore: the factory may be invoked
    // by multiple threads, but only one <c>Lazy.Value</c> is materialized.
    let private locks =
        ConcurrentDictionary<string, Lazy<SemaphoreSlim>>()

    /// Normalize a filesystem path for use as a dictionary key. Windows is
    /// case-insensitive; we lowercase to make
    /// <c>"C:\Foo\.git"</c> and <c>"c:\foo\.git"</c> share a semaphore.
    let canonicalize (path: string) : string =
        let full =
            try
                Path.GetFullPath path
            with _ ->
                path

        full.TrimEnd([| '\\'; '/' |]).ToLowerInvariant()

    /// Get (or lazily create) the semaphore for the given key. Exactly one
    /// <see cref="SemaphoreSlim"/> per key, even under concurrent first-touch
    /// — the value is a <c>Lazy</c> so duplicate <c>GetOrAdd</c> factory
    /// invocations never materialize a second semaphore.
    let getOrCreate (key: string) : SemaphoreSlim =
        locks
            .GetOrAdd(key, fun _ -> Lazy<SemaphoreSlim>(fun () -> new SemaphoreSlim(1, 1)))
            .Value

    /// Acquire the lock identified by <paramref name="key"/>, run
    /// <paramref name="work"/>, then release. Cancellation surfaces from the
    /// semaphore wait <em>or</em> from inside the work block; either way the
    /// release runs in a <c>finally</c>.
    let withLockAsync (key: string) (ct: CancellationToken) (work: unit -> Task<'T>) : Task<'T> =
        task {
            let sem = getOrCreate key
            do! sem.WaitAsync ct

            try
                return! work ()
            finally
                sem.Release() |> ignore
        }

    /// Test-only: clear the registry. Real code should never call this —
    /// dropping a semaphore mid-flight would let two writers in.
    let internal resetForTests () = locks.Clear()
