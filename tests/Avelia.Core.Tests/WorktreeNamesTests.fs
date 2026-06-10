module Avelia.Core.Tests.WorktreeNamesTests

open System
open Xunit
open FsCheck.Xunit
open Avelia.Core
open Avelia.Core.Abstractions

[<Fact>]
let ``pool is non-empty`` () = Assert.NotEmpty WorktreeNames.all

[<Fact>]
let ``every pool name is a valid branch name`` () =
    // Justifies BranchName.Create on pool names in WorkspaceService.CreateAsync.
    for name in WorktreeNames.all do
        match BranchName.TryCreate name with
        | Ok _ -> ()
        | Error msg -> failwithf "Pool name '%s' is not a valid branch name: %s" name msg

[<Fact>]
let ``pool names are unique`` () =
    let dupes =
        WorktreeNames.all |> Array.countBy id |> Array.filter (fun (_, c) -> c > 1)

    Assert.Empty dupes

[<Fact>]
let ``pickUnused suffixes a valid, unused name when the pool is exhausted`` () =
    let used = Set.ofArray WorktreeNames.all
    let picked = WorktreeNames.pickUnused used (Random 42)
    Assert.False(used.Contains picked)
    Assert.False(String.IsNullOrWhiteSpace picked)

    match BranchName.TryCreate picked with
    | Ok _ -> ()
    | Error msg -> failwithf "Suffixed name '%s' is not a valid branch name: %s" picked msg

[<Property>]
let ``pickUnused never collides with the used set and is never empty`` (seed: int) =
    let rng = Random(seed)
    // Use a random subset of the pool (sometimes all of it) as the "used" set.
    let n = rng.Next(0, WorktreeNames.all.Length + 3)

    let used =
        WorktreeNames.all
        |> Array.sortBy (fun _ -> rng.Next())
        |> Array.truncate n
        |> Set.ofArray

    let picked = WorktreeNames.pickUnused used rng
    not (used.Contains picked) && not (String.IsNullOrWhiteSpace picked)
