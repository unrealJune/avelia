namespace Avelia.Persistence

open System
open System.Text
open System.Text.Json
open Avelia.Core.Abstractions

/// Stable string / JSON encodings for the domain values that cross the SQLite
/// boundary. Kept separate from the store so the round-trip is unit-testable
/// without a database. Decoders are total — unrecognized tags fall back to a
/// safe default rather than throwing, so a forward-compatible row never crashes
/// hydration.
[<RequireQualifiedAccess>]
module Codec =

    // -- ModelChoice ---------------------------------------------------------

    let modelToString (m: ModelChoice) : string =
        match m with
        | Sonnet45 -> "sonnet45"
        | Opus41 -> "opus41"
        | Haiku45 -> "haiku45"
        | CustomModel name -> "custom:" + name

    let modelOfString (s: string) : ModelChoice =
        match s with
        | "sonnet45" -> Sonnet45
        | "opus41" -> Opus41
        | "haiku45" -> Haiku45
        | _ when s.StartsWith "custom:" -> CustomModel(s.Substring 7)
        | other -> CustomModel other

    // -- AccentChoice --------------------------------------------------------

    let accentToString (a: AccentChoice) : string =
        match a with
        | AccentChoice.SkyBlue -> "skyblue"
        | AccentChoice.Violet -> "violet"
        | AccentChoice.Magenta -> "magenta"
        | AccentChoice.Yellow -> "yellow"
        | AccentChoice.Orange -> "orange"
        | AccentChoice.Sage -> "sage"

    let accentOfString (s: string) : AccentChoice =
        match s with
        | "violet" -> AccentChoice.Violet
        | "magenta" -> AccentChoice.Magenta
        | "yellow" -> AccentChoice.Yellow
        | "orange" -> AccentChoice.Orange
        | "sage" -> AccentChoice.Sage
        | _ -> AccentChoice.SkyBlue

    // -- Density -------------------------------------------------------------

    let densityToString (d: Density) : string =
        match d with
        | Density.Compact -> "compact"
        | Density.Comfortable -> "comfortable"

    let densityOfString (s: string) : Density =
        match s with
        | "compact" -> Density.Compact
        | _ -> Density.Comfortable

    // -- WorkspaceStatus -----------------------------------------------------

    let statusToString (s: WorkspaceStatus) : string =
        match s with
        | WorkspaceStatus.Draft -> "draft"
        | WorkspaceStatus.Active -> "active"
        | WorkspaceStatus.Ready -> "ready"
        | WorkspaceStatus.Conflict -> "conflict"
        | WorkspaceStatus.Archived -> "archived"
        | WorkspaceStatus.Open -> "open"

    let statusOfString (s: string) : WorkspaceStatus =
        match s with
        | "active" -> WorkspaceStatus.Active
        | "ready" -> WorkspaceStatus.Ready
        | "conflict" -> WorkspaceStatus.Conflict
        | "archived" -> WorkspaceStatus.Archived
        | "open" -> WorkspaceStatus.Open
        | _ -> WorkspaceStatus.Draft

    // -- DateTimeOffset (round-trip "o") -------------------------------------

    let dtoToString (d: DateTimeOffset) = d.ToString("o")

    let dtoOfString (s: string) =
        match DateTimeOffset.TryParse(s, null, Globalization.DateTimeStyles.RoundtripKind) with
        | true, v -> v
        | _ -> DateTimeOffset.UnixEpoch

    // -- MessageEvent (JSON) -------------------------------------------------
    //
    //  Each case is one JSON object: { "kind": "...", ...payload }. Ids are
    //  Guid strings, timestamps round-trip "o", arrays are JSON arrays. The
    //  decoder pulls fields defensively so a partially-written row degrades
    //  rather than throwing.

    let private writeMsgId (w: Utf8JsonWriter) (name: string) (id: MessageId) =
        w.WriteString(name, (MessageId.value id).ToString())

    let private writeTs (w: Utf8JsonWriter) (ts: DateTimeOffset) = w.WriteString("ts", dtoToString ts)

    let private writeStrings (w: Utf8JsonWriter) (name: string) (xs: string[]) =
        w.WriteStartArray name
        for x in xs do
            w.WriteStringValue x
        w.WriteEndArray()

    let messageEventToJson (ev: MessageEvent) : string =
        use ms = new IO.MemoryStream()
        use w = new Utf8JsonWriter(ms)
        w.WriteStartObject()

        match ev with
        | UserMessageAppended m ->
            w.WriteString("kind", "user")
            writeMsgId w "id" m.Id
            w.WriteString("text", m.Text)
            writeStrings w "refs" m.Refs
            writeTs w m.Timestamp
        | AgentMessageAppended m ->
            w.WriteString("kind", "agent")
            writeMsgId w "id" m.Id
            w.WriteString("text", m.Text)
            writeTs w m.Timestamp
        | AgentErrorAppended m ->
            w.WriteString("kind", "error")
            writeMsgId w "id" m.Id
            w.WriteString("text", m.Text)
            writeTs w m.Timestamp
        | ToolBatchAppended m ->
            w.WriteString("kind", "tools")
            writeMsgId w "id" m.Id
            w.WriteNumber("toolCount", m.ToolCount)
            w.WriteNumber("messageCount", m.MessageCount)
            writeStrings w "toolKinds" m.ToolKinds
            writeTs w m.Timestamp
        | ChangeNoteAppended m ->
            w.WriteString("kind", "change")
            writeMsgId w "id" m.Id
            w.WriteString("file", m.File.Value)
            w.WriteNumber("add", m.Add)
            w.WriteNumber("del", m.Del)
            writeTs w m.Timestamp
        | AgentMarkdownAppended m ->
            w.WriteString("kind", "markdown")
            writeMsgId w "id" m.Id
            w.WriteString("heading", m.Heading)
            w.WriteString("body", m.Body)
            w.WriteStartArray "items"

            for item in m.Items do
                w.WriteStartObject()
                w.WriteString("bold", item.Bold)
                w.WriteString("detail", item.Detail)
                w.WriteEndObject()

            w.WriteEndArray()
            writeTs w m.Timestamp

        w.WriteEndObject()
        w.Flush()
        Encoding.UTF8.GetString(ms.ToArray())

    let private getStr (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String ->
            match v.GetString() with
            | null -> ""
            | s -> s
        | _ -> ""

    let private getInt (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
        | _ -> 0

    let private getStrings (e: JsonElement) (name: string) =
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            [| for x in v.EnumerateArray() do
                   if x.ValueKind = JsonValueKind.String then
                       match x.GetString() with
                       | null -> ()
                       | s -> yield s |]
        | _ -> [||]

    let private msgId (e: JsonElement) =
        match Guid.TryParse(getStr e "id") with
        | true, g -> MessageId g
        | _ -> MessageId Guid.Empty

    let private ts (e: JsonElement) = dtoOfString (getStr e "ts")

    let messageEventOfJson (json: string) : MessageEvent =
        use doc = JsonDocument.Parse json
        let e = doc.RootElement
        let kind = getStr e "kind"

        match kind with
        | "agent" ->
            AgentMessageAppended
                { Id = msgId e
                  Text = getStr e "text"
                  Timestamp = ts e }
        | "error" ->
            AgentErrorAppended
                { Id = msgId e
                  Text = getStr e "text"
                  Timestamp = ts e }
        | "tools" ->
            ToolBatchAppended
                { Id = msgId e
                  ToolCount = getInt e "toolCount"
                  MessageCount = getInt e "messageCount"
                  ToolKinds = getStrings e "toolKinds"
                  Timestamp = ts e }
        | "change" ->
            let file =
                match RelativePath.TryCreate(getStr e "file") with
                | Ok p -> p
                | Error _ -> RelativePath.Create "unknown"

            ChangeNoteAppended
                { Id = msgId e
                  File = file
                  Add = getInt e "add"
                  Del = getInt e "del"
                  Timestamp = ts e }
        | "markdown" ->
            let items =
                match e.TryGetProperty "items" with
                | true, arr when arr.ValueKind = JsonValueKind.Array ->
                    [| for x in arr.EnumerateArray() ->
                           { Bold = getStr x "bold"
                             Detail = getStr x "detail" } |]
                | _ -> [||]

            AgentMarkdownAppended
                { Id = msgId e
                  Heading = getStr e "heading"
                  Body = getStr e "body"
                  Items = items
                  Timestamp = ts e }
        | _ -> // "user" and anything unrecognized degrade to a user message
            UserMessageAppended
                { Id = msgId e
                  Text = getStr e "text"
                  Refs = getStrings e "refs"
                  Timestamp = ts e }
