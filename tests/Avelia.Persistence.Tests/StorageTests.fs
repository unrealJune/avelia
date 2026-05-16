module Avelia.Persistence.Tests.StorageTests

open Xunit
open Avelia.Persistence

[<Fact>]
let ``defaultDbPath returns a non-empty path`` () =
    let p = Storage.defaultDbPath ()
    Assert.False(System.String.IsNullOrWhiteSpace p)
