namespace Avelia.Persistence

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Avelia.Core.Abstractions

/// asciicast v2 record/replay (backend plan chunk B-7).
///
/// Format: a JSON header line, then newline-delimited <c>[time, "o", data]</c>
/// arrays — append-only, real-time safe. <c>time</c> is seconds since session
/// start, <c>data</c> is a UTF-8 string. We record terminal output only
/// (code <c>"o"</c>); input/resize codes are not produced and are ignored on
/// replay. On session reopen the cast is replayed into xterm.js as fast as the
/// renderer will take it, rebuilding scrollback before the live ConPTY attaches.
module AsciiCast =

    [<Literal>]
    let Version = 2

    /// Geometry stamped into the header when a session opens without a known
    /// size. The live ConPTY resizes on attach, so this is only the seed value a
    /// standalone player would use before the first resize event.
    [<Literal>]
    let DefaultCols = 80

    [<Literal>]
    let DefaultRows = 24

    let private jsonOptions =
        JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    /// Length of the longest prefix of the first <paramref name="count"/> bytes
    /// of <paramref name="buf"/> that forms only complete UTF-8 code points.
    ///
    /// ConPTY can split a multibyte code point across two reads. Emitting only
    /// the complete prefix and carrying the incomplete tail to the next event
    /// keeps every event's <c>data</c> valid UTF-8 (so the file is a genuine
    /// asciicast) without losing a single byte across the seam. For a stream
    /// that is valid UTF-8 overall, the tail is always empty once its final
    /// chunk arrives.
    let completeUtf8PrefixLength (buf: byte[]) (count: int) : int =
        if count <= 0 then
            0
        else
            // Walk back over continuation bytes (10xxxxxx) to the last lead byte.
            let mutable i = count - 1

            while i >= 0 && (buf[i] &&& 0xC0uy) = 0x80uy do
                i <- i - 1

            if i < 0 then
                0 // begins mid-sequence: hold everything until a lead byte arrives
            else
                let lead = buf[i]

                let expected =
                    if lead < 0x80uy then 1
                    elif lead < 0xE0uy then 2
                    elif lead < 0xF0uy then 3
                    else 4

                // Last sequence complete -> whole buffer is complete; else cut
                // before the dangling lead byte.
                if count - i >= expected then count else i

    /// Render the asciicast v2 header object as a single line (no trailing newline).
    let headerLine (timestamp: DateTimeOffset) (cols: int) (rows: int) : string =
        StringBuilder()
            .Append("{\"version\":")
            .Append(Version)
            .Append(",\"width\":")
            .Append(cols)
            .Append(",\"height\":")
            .Append(rows)
            .Append(",\"timestamp\":")
            .Append(timestamp.ToUnixTimeSeconds())
            .Append('}')
            .ToString()

    /// Render one output event line (no trailing newline).
    let eventLine (elapsed: TimeSpan) (text: string) : string =
        let timeStr =
            elapsed.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)

        let data = JsonSerializer.Serialize(text, jsonOptions)
        String.Concat("[", timeStr, ",\"o\",", data, "]")

    /// Parse one cast line; return the decoded bytes for an output (<c>"o"</c>)
    /// event, or <c>None</c> for any other code / malformed line.
    let tryDecodeOutputEvent (line: string) : byte[] option =
        try
            use doc = JsonDocument.Parse(line)
            let root = doc.RootElement

            if root.ValueKind = JsonValueKind.Array && root.GetArrayLength() >= 3 then
                match root[1].GetString() with
                | null -> None
                | code ->
                    match root[2].GetString() with
                    | null -> None
                    | data when code = "o" -> Some(Encoding.UTF8.GetBytes data)
                    | _ -> None
            else
                None
        with _ ->
            None

    let private newline = [| 0x0auy |]

    /// <c>IAsciiCastWriter</c> over an arbitrary stream. Single header already
    /// written by the caller; each <c>AppendAsync</c> emits at most one output
    /// event (the complete-UTF-8 prefix of the buffered bytes) and flushes.
    type private Writer(stream: Stream, leaveOpen: bool) =
        let pending = List<byte>()
        let gate = new SemaphoreSlim(1, 1)
        let mutable disposed = false

        interface IAsciiCastWriter with
            member _.AppendAsync(bytes: ReadOnlyMemory<byte>, elapsed: TimeSpan, ct: CancellationToken) : Task =
                task {
                    do! gate.WaitAsync(ct)

                    try
                        pending.AddRange(bytes.ToArray())
                        let arr = pending.ToArray()
                        let prefix = completeUtf8PrefixLength arr arr.Length

                        if prefix > 0 then
                            let text = Encoding.UTF8.GetString(arr, 0, prefix)
                            let lineBytes = Encoding.UTF8.GetBytes(eventLine elapsed text)
                            do! stream.WriteAsync(ReadOnlyMemory(lineBytes), ct)
                            do! stream.WriteAsync(ReadOnlyMemory(newline), ct)
                            do! stream.FlushAsync(ct)
                            pending.RemoveRange(0, prefix)
                    finally
                        gate.Release() |> ignore
                }
                :> Task

        interface IAsyncDisposable with
            member _.DisposeAsync() : ValueTask =
                ValueTask(
                    task {
                        do! gate.WaitAsync()

                        try
                            if not disposed then
                                disposed <- true
                                // A non-empty tail here is an incomplete trailing
                                // code point (truncated stream) — genuinely not
                                // renderable text, so it is dropped, not guessed.
                                do! stream.FlushAsync()

                                if not leaveOpen then
                                    stream.Dispose()
                        finally
                            gate.Release() |> ignore
                    }
                    :> Task
                )

    /// Create a writer over <paramref name="stream"/>. When
    /// <paramref name="writeHeader"/> is set, the asciicast header line is
    /// written first (new session); reopening an existing cast appends without a
    /// second header.
    let createWriterAsync
        (stream: Stream)
        (leaveOpen: bool)
        (writeHeader: bool)
        (ct: CancellationToken)
        : Task<IAsciiCastWriter> =
        task {
            if writeHeader then
                let header = headerLine DateTimeOffset.UtcNow DefaultCols DefaultRows
                let bytes = Encoding.UTF8.GetBytes(header)
                do! stream.WriteAsync(ReadOnlyMemory(bytes), ct)
                do! stream.WriteAsync(ReadOnlyMemory(newline), ct)
                do! stream.FlushAsync(ct)

            return new Writer(stream, leaveOpen) :> IAsciiCastWriter
        }

    /// Replay a cast stream as the decoded output-event byte chunks, in order.
    /// The stream is disposed when enumeration completes or is abandoned.
    let replay (stream: Stream) : IAsyncEnumerable<ReadOnlyMemory<byte>> =
        taskSeq {
            use reader = new StreamReader(stream, Encoding.UTF8)
            // First line is the header; geometry is reapplied by the live session.
            let! _header = reader.ReadLineAsync()
            let mutable finished = false

            while not finished do
                let! line = reader.ReadLineAsync()

                if isNull line then
                    finished <- true
                elif line.Length > 0 then
                    match tryDecodeOutputEvent line with
                    | Some bytes -> yield ReadOnlyMemory(bytes)
                    | None -> ()
        }

    /// Default on-disk session store: <c>%LOCALAPPDATA%/Avelia/sessions</c>.
    let sessionsDirectory () : string =
        let appData =
            Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData

        Path.Combine(appData, "Avelia", "sessions")

    let castPath (rootDirectory: string) (SessionId id) : string =
        Path.Combine(rootDirectory, id.ToString("N") + ".cast")

/// File-backed <c>ISessionPersistence</c>: one <c>.cast</c> per session under
/// <paramref name="rootDirectory"/>. Reopening a session appends to its existing
/// cast (no duplicate header); replay streams the existing file.
type SessionPersistence(rootDirectory: string) =
    new() = SessionPersistence(AsciiCast.sessionsDirectory ())

    member _.RootDirectory = rootDirectory

    interface ISessionPersistence with
        member _.OpenWriterAsync
            (sessionId: SessionId, ct: CancellationToken)
            : Task<OperationResult<IAsciiCastWriter>> =
            task {
                try
                    Directory.CreateDirectory rootDirectory |> ignore
                    let path = AsciiCast.castPath rootDirectory sessionId
                    let isNew = not (File.Exists path) || FileInfo(path).Length = 0L
                    let stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
                    let! writer = AsciiCast.createWriterAsync stream false isNew ct
                    return Success writer
                with ex ->
                    return Failure(AveliaError.External("asciicast", ex.Message))
            }

        member _.OpenReplayAsync
            (sessionId: SessionId, _ct: CancellationToken)
            : Task<OperationResult<IAsyncEnumerable<ReadOnlyMemory<byte>>>> =
            task {
                let path = AsciiCast.castPath rootDirectory sessionId

                if not (File.Exists path) then
                    let (SessionId id) = sessionId
                    return Failure(AveliaError.NotFound(sprintf "session:%s" (id.ToString("N"))))
                else
                    try
                        let stream =
                            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) :> Stream

                        return Success(AsciiCast.replay stream)
                    with ex ->
                        return Failure(AveliaError.External("asciicast", ex.Message))
            }
