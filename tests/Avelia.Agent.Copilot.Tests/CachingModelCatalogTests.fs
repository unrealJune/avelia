module Avelia.Agent.Copilot.Tests.CachingModelCatalogTests

open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot

/// Counting fake whose result can be toggled between success and failure so the
/// cache's "only memoise success" behaviour is observable.
type private CountingCatalog(succeed: bool) =
    let mutable calls = 0
    member _.Calls = calls

    interface IModelCatalogService with
        member _.ListModelsAsync(_: CancellationToken) =
            Interlocked.Increment(&calls) |> ignore

            if succeed then
                Task.FromResult(Success(ModelCatalog.presets :> IReadOnlyList<ModelInfo>))
            else
                Task.FromResult(Failure(AveliaError.Internal "boom"))

[<Fact>]
let ``a successful read is cached and the inner is hit once`` () =
    let inner = CountingCatalog(succeed = true)
    let caching = CachingModelCatalog(inner) :> IModelCatalogService

    let first = caching.ListModelsAsync(CancellationToken.None).Result
    let second = caching.ListModelsAsync(CancellationToken.None).Result

    Assert.True(first.IsSuccess)
    Assert.True(second.IsSuccess)
    Assert.Equal(1, inner.Calls)

[<Fact>]
let ``a failed read is not cached so a later call retries`` () =
    let inner = CountingCatalog(succeed = false)
    let caching = CachingModelCatalog(inner) :> IModelCatalogService

    caching.ListModelsAsync(CancellationToken.None).Result |> ignore
    caching.ListModelsAsync(CancellationToken.None).Result |> ignore

    Assert.Equal(2, inner.Calls)
