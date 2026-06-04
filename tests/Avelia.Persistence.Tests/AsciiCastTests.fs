module Avelia.Persistence.Tests.AsciiCastTests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open FsCheck.Xunit
open Avelia.Core.Abstractions
open Avelia.Persistence

// ---------------------------------------------------------------------------
//  completeUtf8PrefixLength — the seam that keeps every event valid UTF-8
// ---------------------------------------------------------------------------

[<Fact>]
let ``ascii bytes are wholly complete`` () =
    let b = Encoding.UTF8.GetBytes "ABC"
    Assert.Equal(3, AsciiCast.completeUtf8PrefixLength b b.Length)

[<Fact>]
let ``a dangling lead byte is held back`` () =
    // 'é' = 0xC3 0xA9; with only the lead byte present nothing is complete.
    let b = [| 0xC3uy |]
    Assert.Equal(0, AsciiCast.completeUtf8PrefixLength b b.Length)

[<Fact>]
let ``a split multibyte code point cuts before the dangling lead`` () =
    // "Aé" with the trailing continuation byte missing: 'A' is complete, the
    // 0xC3 lead is held back.
    let b = [| 0x41uy; 0xC3uy |]
    Assert.Equal(1, AsciiCast.completeUtf8PrefixLength b b.Length)

[<Fact>]
let ``a complete multibyte code point is included`` () =
    let b = Encoding.UTF8.GetBytes "é" // 0xC3 0xA9
    Assert.Equal(2, AsciiCast.completeUtf8PrefixLength b b.Length)

// ---------------------------------------------------------------------------
//  Header + event line shape
// ---------------------------------------------------------------------------

[<Fact>]
let ``header line is a valid asciicast v2 object`` () =
    let line = AsciiCast.headerLine (DateTimeOffset.FromUnixTimeSeconds 100L) 80 24
    use doc = JsonDocument.Parse line
    Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt32())
    Assert.Equal(80, doc.RootElement.GetProperty("width").GetInt32())
    Assert.Equal(24, doc.RootElement.GetProperty("height").GetInt32())
    Assert.Equal(100L, doc.RootElement.GetProperty("timestamp").GetInt64())

[<Fact>]
let ``an output event line decodes back to its text`` () =
    let text = "hi[0m\nthere"
    let line = AsciiCast.eventLine (TimeSpan.FromSeconds 1.5) text

    match AsciiCast.tryDecodeOutputEvent line with
    | Some bytes -> Assert.Equal(text, Encoding.UTF8.GetString bytes)
    | None -> Assert.Fail "expected an output event"

[<Fact>]
let ``a non-output event line is ignored`` () =
    Assert.Equal(None, AsciiCast.tryDecodeOutputEvent "[0.5,\"i\",\"keystroke\"]")
    Assert.Equal(None, AsciiCast.tryDecodeOutputEvent "not json")

// ---------------------------------------------------------------------------
//  Record/replay round-trip (in-memory, pure — fast tier)
// ---------------------------------------------------------------------------

let private runSync (work: Task<'T>) : 'T = work.GetAwaiter().GetResult()

let private drain (source: System.Collections.Generic.IAsyncEnumerable<ReadOnlyMemory<byte>>) : Task<byte[]> =
    task {
        let acc = ResizeArray<byte>()
        let e = source.GetAsyncEnumerator CancellationToken.None
        let mutable go = true

        while go do
            let! has = e.MoveNextAsync()

            if has then
                acc.AddRange(e.Current.ToArray())
            else
                go <- false

        do! e.DisposeAsync()
        return acc.ToArray()
    }

/// Record the chunks through a writer, then replay the resulting cast.
let private recordAndReplay (chunks: byte[] list) : byte[] =
    task {
        use ms = new MemoryStream()
        // leaveOpen so we can read ms back after disposing the writer.
        let! writer = AsciiCast.createWriterAsync ms true true CancellationToken.None
        let mutable elapsed = 0.0

        for c in chunks do
            elapsed <- elapsed + 0.01
            do! writer.AppendAsync(ReadOnlyMemory c, TimeSpan.FromSeconds elapsed, CancellationToken.None)

        do! writer.DisposeAsync()

        use replayStream = new MemoryStream(ms.ToArray())
        return! drain (AsciiCast.replay replayStream)
    }
    |> runSync

let private chunkBySizes (bytes: byte[]) (sizes: int list) : byte[] list =
    let result = ResizeArray<byte[]>()
    let mutable offset = 0

    for sz in sizes do
        if offset < bytes.Length then
            // Small chunks (1..7) maximise the odds of splitting a multibyte
            // code point across the seam.
            let take = min (abs sz % 7 + 1) (bytes.Length - offset)
            result.Add(bytes[offset .. offset + take - 1])
            offset <- offset + take

    if offset < bytes.Length then
        result.Add bytes[offset..]

    List.ofSeq result

[<Property>]
let ``replay reproduces the recorded byte stream`` (s: string) (splits: int list) =
    let text = if String.IsNullOrEmpty s then "" else s
    // Encoding sanitises lone surrogates to U+FFFD, so the bytes are always
    // valid UTF-8 — exactly the domain ConPTY output lives in.
    let bytes = Encoding.UTF8.GetBytes text
    let replayed = recordAndReplay (chunkBySizes bytes splits)
    replayed = bytes

[<Fact>]
let ``replaying an empty session yields no bytes`` () = Assert.Empty(recordAndReplay [])

// ---------------------------------------------------------------------------
//  File-backed SessionPersistence (integration — touches the filesystem)
// ---------------------------------------------------------------------------

let private tempDir () =
    Path.Combine(Path.GetTempPath(), "avelia-cast-" + Guid.NewGuid().ToString("N"))

[<Trait("Category", "Integration")>]
[<Fact>]
let ``SessionPersistence records and replays a session`` () =
    task {
        let dir = tempDir ()

        try
            let persistence = SessionPersistence(dir) :> ISessionPersistence
            let sid = SessionId(Guid.NewGuid())

            let! w = persistence.OpenWriterAsync(sid, CancellationToken.None)
            Assert.True w.IsSuccess
            let writer = w.Value

            do!
                writer.AppendAsync(
                    ReadOnlyMemory(Encoding.UTF8.GetBytes "hello "),
                    TimeSpan.FromSeconds 0.1,
                    CancellationToken.None
                )

            do!
                writer.AppendAsync(
                    ReadOnlyMemory(Encoding.UTF8.GetBytes "wörld"),
                    TimeSpan.FromSeconds 0.2,
                    CancellationToken.None
                )

            do! writer.DisposeAsync()

            let! r = persistence.OpenReplayAsync(sid, CancellationToken.None)
            Assert.True r.IsSuccess
            let! bytes = drain r.Value
            Assert.Equal("hello wörld", Encoding.UTF8.GetString bytes)
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)
    }
    |> runSync

[<Trait("Category", "Integration")>]
[<Fact>]
let ``reopening a session appends without a second header`` () =
    task {
        let dir = tempDir ()

        try
            let persistence = SessionPersistence(dir) :> ISessionPersistence
            let sid = SessionId(Guid.NewGuid())

            let! w1 = persistence.OpenWriterAsync(sid, CancellationToken.None)

            do!
                w1.Value.AppendAsync(
                    ReadOnlyMemory(Encoding.UTF8.GetBytes "first "),
                    TimeSpan.FromSeconds 0.1,
                    CancellationToken.None
                )

            do! w1.Value.DisposeAsync()

            let! w2 = persistence.OpenWriterAsync(sid, CancellationToken.None)

            do!
                w2.Value.AppendAsync(
                    ReadOnlyMemory(Encoding.UTF8.GetBytes "second"),
                    TimeSpan.FromSeconds 0.2,
                    CancellationToken.None
                )

            do! w2.Value.DisposeAsync()

            let! r = persistence.OpenReplayAsync(sid, CancellationToken.None)
            let! bytes = drain r.Value
            Assert.Equal("first second", Encoding.UTF8.GetString bytes)
        finally
            if Directory.Exists dir then
                Directory.Delete(dir, true)
    }
    |> runSync

[<Trait("Category", "Integration")>]
[<Fact>]
let ``OpenReplayAsync returns NotFound for an unknown session`` () =
    task {
        let dir = tempDir ()
        let persistence = SessionPersistence(dir) :> ISessionPersistence
        let! r = persistence.OpenReplayAsync(SessionId(Guid.NewGuid()), CancellationToken.None)
        Assert.True r.IsFailure
    }
    |> runSync
