module Avelia.Core.Tests.IdsTests

open System.Collections.Generic
open Xunit
open Avelia.Core.Abstractions

let private uniqueOver n create =
    let seen = HashSet()
    let mutable collisions = 0
    for _ in 1 .. n do
        let id = create ()
        if not (seen.Add id) then collisions <- collisions + 1
    collisions

[<Fact>]
let ``RepositoryId.create produces unique values over 10k`` () =
    Assert.Equal(0, uniqueOver 10_000 RepositoryId.create)

[<Fact>]
let ``WorkspaceId.create produces unique values over 10k`` () =
    Assert.Equal(0, uniqueOver 10_000 WorkspaceId.create)

[<Fact>]
let ``ConversationId.create produces unique values over 10k`` () =
    Assert.Equal(0, uniqueOver 10_000 ConversationId.create)

[<Fact>]
let ``MessageId.create produces unique values over 10k`` () =
    Assert.Equal(0, uniqueOver 10_000 MessageId.create)

[<Fact>]
let ``RunId.create produces unique values over 10k`` () =
    Assert.Equal(0, uniqueOver 10_000 RunId.create)
