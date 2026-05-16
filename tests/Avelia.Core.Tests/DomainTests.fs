module Avelia.Core.Tests.DomainTests

open System
open Xunit
open Avelia.Core
open Avelia.Core.Abstractions

[<Fact>]
let ``Drafting can transition to Active`` () =
    Assert.True(Task.canTransition Drafting Active)

[<Fact>]
let ``Active cannot transition back to Drafting`` () =
    Assert.False(Task.canTransition Active Drafting)

[<Fact>]
let ``InReview can transition to Merged`` () =
    let merged = Merged DateTimeOffset.UtcNow
    let inReview = InReview(PullRequestId 1)
    Assert.True(Task.canTransition inReview merged)
