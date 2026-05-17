module Avelia.Core.Tests.WorkspaceTests

open Xunit
open FsCheck
open FsCheck.Xunit
open Avelia.Core
open Avelia.Core.Abstractions

[<Fact>]
let ``Draft can transition to Active`` () =
    Assert.True(Workspace.canTransition WorkspaceStatus.Draft WorkspaceStatus.Active)

[<Fact>]
let ``Draft cannot transition directly to Ready`` () =
    Assert.False(Workspace.canTransition WorkspaceStatus.Draft WorkspaceStatus.Ready)

[<Fact>]
let ``Active can transition to Ready`` () =
    Assert.True(Workspace.canTransition WorkspaceStatus.Active WorkspaceStatus.Ready)

[<Fact>]
let ``Active can transition to Conflict`` () =
    Assert.True(Workspace.canTransition WorkspaceStatus.Active WorkspaceStatus.Conflict)

[<Fact>]
let ``Conflict can resolve back to Active`` () =
    Assert.True(Workspace.canTransition WorkspaceStatus.Conflict WorkspaceStatus.Active)

[<Fact>]
let ``Archived workspace can be un-archived`` () =
    Assert.True(Workspace.canTransition WorkspaceStatus.Archived WorkspaceStatus.Active)

[<Property(Arbitrary = [| typeof<Generators.Arbs> |])>]
let ``Workspace.canTransition is reflexive`` (s: WorkspaceStatus) =
    Workspace.canTransition s s

[<Property(Arbitrary = [| typeof<Generators.Arbs> |])>]
let ``Workspace.canTransition is total (every pair has a defined answer)``
    (a: WorkspaceStatus)
    (b: WorkspaceStatus)
    =
    // Just call the function — totality means no exception, no unhandled case.
    let _ = Workspace.canTransition a b
    true
