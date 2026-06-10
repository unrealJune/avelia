module Avelia.Persistence.Tests.CodecTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Avelia.Core.Abstractions
open Avelia.Persistence

let private mid () = MessageId(Guid.NewGuid())
let private ts = DateTimeOffset(2026, 6, 4, 10, 30, 0, TimeSpan.FromHours 2.0)

// -- enum-ish DU round-trips -------------------------------------------------

[<Fact>]
let ``model choice round-trips through string`` () =
    for m in [ Sonnet45; Opus41; Haiku45; CustomModel "gpt-5"; CustomModel "x:y" ] do
        Assert.Equal(m, Codec.modelOfString (Codec.modelToString m))

[<Fact>]
let ``accent, density and status round-trip through string`` () =
    for a in AccentChoice.All do
        Assert.Equal(a, Codec.accentOfString (Codec.accentToString a))

    for d in [ Density.Compact; Density.Comfortable ] do
        Assert.Equal(d, Codec.densityOfString (Codec.densityToString d))

    for s in
        [ WorkspaceStatus.Draft
          WorkspaceStatus.Active
          WorkspaceStatus.Ready
          WorkspaceStatus.Conflict
          WorkspaceStatus.Archived
          WorkspaceStatus.Open ] do
        Assert.Equal(s, Codec.statusOfString (Codec.statusToString s))

[<Fact>]
let ``unknown model string degrades to a custom model`` () =
    Assert.Equal(CustomModel "mystery", Codec.modelOfString "mystery")

[<Fact>]
let ``reasoning effort and context tier round-trip through string`` () =
    for e in ReasoningEffort.All do
        Assert.Equal(e, Codec.reasoningEffortOfString (Codec.reasoningEffortToString e))

    for t in ContextTier.All do
        Assert.Equal(t, Codec.contextTierOfString (Codec.contextTierToString t))

[<Fact>]
let ``unknown reasoning/context strings degrade to safe defaults`` () =
    Assert.Equal(ReasoningEffort.Medium, Codec.reasoningEffortOfString "mystery")
    Assert.Equal(ContextTier.Default, Codec.contextTierOfString "mystery")

// -- MessageEvent JSON round-trips (one per case) ----------------------------

let private roundtrip (ev: MessageEvent) =
    Assert.Equal(ev, Codec.messageEventOfJson (Codec.messageEventToJson ev))

[<Fact>]
let ``user message round-trips`` () =
    roundtrip (
        UserMessageAppended
            { Id = mid ()
              Text = "hello @file"
              Refs = [| "a.fs"; "b.fs" |]
              Timestamp = ts }
    )

[<Fact>]
let ``agent and error messages round-trip`` () =
    roundtrip (
        AgentMessageAppended
            { Id = mid ()
              Text = "working"
              Timestamp = ts }
    )

    roundtrip (
        AgentErrorAppended
            { Id = mid ()
              Text = "boom"
              Timestamp = ts }
    )

[<Fact>]
let ``title-changed rename round-trips`` () =
    roundtrip (TitleChanged "Refactor Auth Module")

[<Fact>]
let ``tool batch round-trips`` () =
    roundtrip (
        ToolBatchAppended
            { Id = mid ()
              ToolCount = 3
              MessageCount = 1
              ToolKinds = [| "files"; "search" |]
              Timestamp = ts }
    )

[<Fact>]
let ``change note round-trips`` () =
    roundtrip (
        ChangeNoteAppended
            { Id = mid ()
              File = RelativePath.Create "src/app.fs"
              Add = 12
              Del = 3
              Timestamp = ts }
    )

[<Fact>]
let ``agent markdown round-trips`` () =
    roundtrip (
        AgentMarkdownAppended
            { Id = mid ()
              Heading = "Plan"
              Body = "Steps:"
              Items = [| { Bold = "1"; Detail = "do x" }; { Bold = "2"; Detail = "do y" } |]
              Timestamp = ts }
    )

[<Property>]
let ``any user-message text and refs survive the round-trip`` (text: NonNull<string>) (refs: NonNull<string>[]) =
    let cleaned = refs |> Array.map (fun r -> r.Get)

    let ev =
        UserMessageAppended
            { Id = mid ()
              Text = text.Get
              Refs = cleaned
              Timestamp = ts }

    Codec.messageEventOfJson (Codec.messageEventToJson ev) = ev
