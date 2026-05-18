module Avelia.Vcs.Git.Tests.RepoLocksTests

open System.IO
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Vcs.Git

[<Fact>]
let ``canonicalize lowercases and trims trailing separators`` () =
    let a = RepoLocks.canonicalize "C:\\Repos\\Foo\\"
    let b = RepoLocks.canonicalize "c:/repos/foo"
    Assert.Equal(a, b)

[<Fact>]
let ``canonicalize collapses different separator styles`` () =
    let a = RepoLocks.canonicalize @"C:\Work\thing"
    let b = RepoLocks.canonicalize "C:/Work/thing"
    Assert.Equal(a, b)

[<Fact>]
let ``getOrCreate returns the same semaphore for equal keys`` () =
    let key = $"test-{System.Guid.NewGuid()}"
    let s1 = RepoLocks.getOrCreate key
    let s2 = RepoLocks.getOrCreate key
    Assert.Same(s1, s2)

[<Fact>]
let ``withLockAsync serializes overlapping calls`` () =
    let key = $"serialize-{System.Guid.NewGuid()}"
    let mutable inside = 0
    let mutable maxOverlap = 0
    let gate = obj ()

    let run () =
        RepoLocks.withLockAsync key CancellationToken.None (fun () ->
            task {
                let n =
                    lock gate (fun () ->
                        inside <- inside + 1

                        if inside > maxOverlap then
                            maxOverlap <- inside

                        inside)
                // Yield so a second waiter has a real chance to interleave
                // if the lock weren't honoured.
                do! Task.Delay 10
                lock gate (fun () -> inside <- inside - 1)
                return n
            })

    let t1 = run ()
    let t2 = run ()
    let t3 = run ()
    Task.WhenAll([| t1; t2; t3 |]).GetAwaiter().GetResult() |> ignore
    Assert.Equal(1, maxOverlap)
