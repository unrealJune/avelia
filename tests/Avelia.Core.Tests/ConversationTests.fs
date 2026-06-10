module Avelia.Core.Tests.ConversationTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Avelia.Core
open Avelia.Core.Abstractions

let private emptyConv () =
    Conversation.empty (ConversationId.create ()) (WorkspaceId.create ()) "test-thread"

[<Fact>]
let ``Empty conversation has zero messages and sequence 0`` () =
    let c = emptyConv ()
    Assert.Equal(0, c.Messages.Length)
    Assert.Equal(0, c.LastSequence)

[<Fact>]
let ``applyEvent appends and bumps sequence`` () =
    let c0 = emptyConv ()

    let msg =
        { Id = MessageId.create ()
          Text = "hello"
          Refs = [||]
          Timestamp = DateTimeOffset.UnixEpoch }

    let c1 = Conversation.applyEvent c0 (UserMessageAppended msg)
    Assert.Equal(1, c1.Messages.Length)
    Assert.Equal(1, c1.LastSequence)

[<Property(Arbitrary = [| typeof<Generators.Arbs> |])>]
let ``Replay produces conversation whose LastSequence equals event count`` (events: MessageEvent list) =
    let c =
        Conversation.replay (ConversationId.create ()) (WorkspaceId.create ()) "test" events

    c.LastSequence = events.Length && c.Messages.Length = events.Length

[<Property(Arbitrary = [| typeof<Generators.Arbs> |])>]
let ``Replay preserves event order`` (events: MessageEvent list) =
    let c =
        Conversation.replay (ConversationId.create ()) (WorkspaceId.create ()) "test" events

    Array.toList c.Messages = events

[<Property(Arbitrary = [| typeof<Generators.Arbs> |])>]
let ``applyEvent then replay subset matches incremental fold`` (events: MessageEvent list) =
    let id = ConversationId.create ()
    let wsId = WorkspaceId.create ()
    let title = "t"
    let viaReplay = Conversation.replay id wsId title events

    let viaFold =
        events |> List.fold Conversation.applyEvent (Conversation.empty id wsId title)

    viaReplay = viaFold

[<Fact>]
let ``applyEvent TitleChanged renames without appending or bumping sequence`` () =
    let c0 = emptyConv ()
    let c1 = Conversation.applyEvent c0 (TitleChanged "New Title")

    Assert.Equal("New Title", c1.Title)
    Assert.Equal(0, c1.Messages.Length)
    Assert.Equal(0, c1.LastSequence)

[<Fact>]
let ``replay folds TitleChanged into Title and keeps only real messages`` () =
    let id = ConversationId.create ()
    let wsId = WorkspaceId.create ()

    let u1 =
        UserMessageAppended
            { Id = MessageId.create ()
              Text = "a"
              Refs = [||]
              Timestamp = DateTimeOffset.UnixEpoch }

    let a1 =
        AgentMessageAppended
            { Id = MessageId.create ()
              Text = "b"
              Timestamp = DateTimeOffset.UnixEpoch }

    let c =
        Conversation.replay id wsId "orig" [ u1; TitleChanged "First"; a1; TitleChanged "Second" ]

    // Last rename wins; renames don't appear in the transcript or shift sequence.
    Assert.Equal("Second", c.Title)
    Assert.Equal(2, c.LastSequence)
    Assert.Equal<MessageEvent[]>([| u1; a1 |], c.Messages)
