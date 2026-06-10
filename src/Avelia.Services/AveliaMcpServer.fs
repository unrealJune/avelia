namespace Avelia.Services

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions

/// In-process MCP (Model Context Protocol) server that exposes Avelia's own
/// workspace operations to the agent, so the agent can name its workspace and
/// open pull requests itself instead of relying on out-of-band heuristics.
///
/// <para>Transport is the SDK's streamable-HTTP MCP variant: the server binds an
/// ephemeral loopback port and each headless session is pointed at
/// <c>http://127.0.0.1:&lt;port&gt;/mcp/&lt;workspaceGuid&gt;</c>. The workspace
/// is therefore carried in the URL path — the agent never has to know or pass an
/// id, and one server multiplexes every session. Binding a specific
/// <c>127.0.0.1</c> host (not <c>+</c>/<c>*</c>) needs no URL ACL / admin on
/// Windows.</para>
///
/// <para>Because the server runs inside the same process as the services, the
/// two tools call straight into the injected handlers — no IPC, and the rename
/// reuses the live conversation broadcast so the UI updates immediately.</para>
///
/// <para>Only the minimal request/response slice of the protocol is implemented
/// (<c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>, <c>ping</c>, plus a
/// <c>202</c> ack for notifications); server-initiated SSE (<c>GET</c>) is
/// answered <c>405</c>, which compliant clients treat as "no server stream".</para>
type AveliaMcpServer
    (
        renameWorkspace: WorkspaceId -> string -> CancellationToken -> Task<OperationResult<unit>>,
        createPullRequest:
            WorkspaceId -> string -> string -> bool -> CancellationToken -> Task<OperationResult<PullRequest>>
    ) =

    let protocolVersion = "2025-06-18"
    let cts = new CancellationTokenSource()
    let listener = new HttpListener()

    // Reserve an ephemeral loopback port via a throwaway TcpListener, then hand
    // it to the HttpListener. The tiny window between probe-close and bind is
    // acceptable for a local dev tool.
    let port =
        let probe = new TcpListener(IPAddress.Loopback, 0)
        probe.Start()
        let p = (probe.LocalEndpoint :?> IPEndPoint).Port
        probe.Stop()
        p

    let describe (e: AveliaError) =
        match e with
        | AveliaError.Unauthorized -> "Not authorized — sign in to GitHub or set a token in Settings."
        | AveliaError.Network m -> "Network error: " + m
        | AveliaError.External(src, d) -> sprintf "%s error: %s" src d
        | AveliaError.NotFound r -> "Not found: " + r
        | AveliaError.Validation m -> m
        | AveliaError.Conflict m -> m
        | AveliaError.Internal m -> "Internal error: " + m

    // ---- JSON helpers ----------------------------------------------------

    let envelope (idNode: JsonNode option) : JsonObject =
        let o = JsonObject()
        o["jsonrpc"] <- JsonValue.Create "2.0"

        match idNode with
        | Some n -> o["id"] <- n.DeepClone()
        | None -> o["id"] <- null

        o

    let resultEnvelope (idNode: JsonNode option) (res: JsonNode) : JsonObject =
        let o = envelope idNode
        o["result"] <- res
        o

    let errorEnvelope (idNode: JsonNode option) (code: int) (message: string) : JsonObject =
        let o = envelope idNode
        let e = JsonObject()
        e["code"] <- JsonValue.Create code
        e["message"] <- JsonValue.Create message
        o["error"] <- e
        o

    let textContent (text: string) (isError: bool) : JsonObject =
        let item = JsonObject()
        item["type"] <- JsonValue.Create "text"
        item["text"] <- JsonValue.Create text
        let arr = JsonArray()
        arr.Add item
        let c = JsonObject()
        c["content"] <- arr
        c["isError"] <- JsonValue.Create isError
        c

    let toolOk text = textContent text false
    let toolError text = textContent text true

    let stringProp (name: string) (description: string) : JsonObject =
        let p = JsonObject()
        p["type"] <- JsonValue.Create "string"
        p["description"] <- JsonValue.Create description
        p

    let boolProp (description: string) : JsonObject =
        let p = JsonObject()
        p["type"] <- JsonValue.Create "boolean"
        p["description"] <- JsonValue.Create description
        p

    let toolDescriptor
        (name: string)
        (description: string)
        (properties: (string * JsonObject) list)
        (required: string list)
        =
        let props = JsonObject()

        for (k, v) in properties do
            props[k] <- v

        let schema = JsonObject()
        schema["type"] <- JsonValue.Create "object"
        schema["properties"] <- props

        let req = JsonArray()

        for r in required do
            req.Add(JsonValue.Create r)

        schema["required"] <- req

        let t = JsonObject()
        t["name"] <- JsonValue.Create name
        t["description"] <- JsonValue.Create description
        t["inputSchema"] <- schema
        t

    let toolsList () : JsonObject =
        let tools = JsonArray()

        tools.Add(
            toolDescriptor
                "rename_workspace"
                ("Set this workspace's short display title (3-6 words, Title Case, no quotes). "
                 + "Call this once you understand the task, e.g. \"Add MCP Server For Naming\".")
                [ "title", stringProp "title" "The new short workspace title." ]
                [ "title" ]
        )

        tools.Add(
            toolDescriptor
                "create_pull_request"
                ("Open a GitHub pull request for this workspace's branch. Pushes the branch to "
                 + "origin first. Fails if the workspace already has a PR.")
                [ "title", stringProp "title" "The pull-request title."
                  "body", stringProp "body" "The pull-request body / description (optional)."
                  "draft", boolProp "Open the PR as a draft (optional, default false)." ]
                [ "title" ]

        )

        let r = JsonObject()
        r["tools"] <- tools
        r

    let initializeResult () : JsonObject =
        let caps = JsonObject()
        caps["tools"] <- JsonObject()
        let info = JsonObject()
        info["name"] <- JsonValue.Create "avelia"
        info["version"] <- JsonValue.Create "1.0.0"
        let r = JsonObject()
        r["protocolVersion"] <- JsonValue.Create protocolVersion
        r["capabilities"] <- caps
        r["serverInfo"] <- info
        r

    let strArg (args: JsonObject) (key: string) : string =
        match args[key] with
        | null -> ""
        | v ->
            try
                v.GetValue<string>()
            with _ ->
                ""

    let boolArg (args: JsonObject) (key: string) : bool =
        match args[key] with
        | null -> false
        | v ->
            try
                v.GetValue<bool>()
            with _ ->
                false

    // ---- tool dispatch ---------------------------------------------------

    let handleToolCall (wsOpt: WorkspaceId option) (paramsNode: JsonNode) : Task<JsonObject> =
        task {
            let p = paramsNode.AsObject()

            let name =
                match p["name"] with
                | null -> ""
                | v ->
                    try
                        v.GetValue<string>()
                    with _ ->
                        ""

            let args =
                match p["arguments"] with
                | :? JsonObject as a -> a
                | _ -> JsonObject()

            match wsOpt with
            | None -> return toolError "No workspace is bound to this MCP session."
            | Some ws ->
                match name with
                | "rename_workspace" ->
                    let title = strArg args "title"

                    if String.IsNullOrWhiteSpace title then
                        return toolError "A non-empty 'title' argument is required."
                    else
                        match! renameWorkspace ws title cts.Token with
                        | Success() -> return toolOk (sprintf "Workspace renamed to \"%s\"." (title.Trim()))
                        | Failure e -> return toolError (describe e)

                | "create_pull_request" ->
                    let title = strArg args "title"
                    let body = strArg args "body"
                    let draft = boolArg args "draft"

                    if String.IsNullOrWhiteSpace title then
                        return toolError "A non-empty 'title' argument is required."
                    else
                        match! createPullRequest ws title body draft cts.Token with
                        | Success pr -> return toolOk (sprintf "Opened pull request #%d: %s" pr.Number pr.Title)
                        | Failure e -> return toolError (describe e)

                | other -> return toolError (sprintf "Unknown tool: %s" other)
        }

    /// Dispatch one JSON-RPC message. Returns <c>None</c> for notifications
    /// (no <c>id</c>), which get a bodyless <c>202</c>.
    let dispatch (wsOpt: WorkspaceId option) (node: JsonNode) : Task<JsonObject option> =
        task {
            let obj = node.AsObject()

            let idNode =
                match obj["id"] with
                | null -> None
                | v -> Some v

            let methodName =
                match obj["method"] with
                | null -> ""
                | v ->
                    try
                        v.GetValue<string>()
                    with _ ->
                        ""

            match methodName with
            | "initialize" -> return Some(resultEnvelope idNode (initializeResult ()))
            | "ping" -> return Some(resultEnvelope idNode (JsonObject()))
            | "tools/list" -> return Some(resultEnvelope idNode (toolsList ()))
            | "tools/call" ->
                let paramsNode =
                    match obj["params"] with
                    | null -> JsonObject() :> JsonNode
                    | v -> v

                let! content = handleToolCall wsOpt paramsNode
                return Some(resultEnvelope idNode content)
            | m when m.StartsWith "notifications/" -> return None
            | "" ->
                match idNode with
                | Some _ -> return Some(errorEnvelope idNode -32600 "Invalid Request: missing method.")
                | None -> return None
            | m ->
                match idNode with
                | Some _ -> return Some(errorEnvelope idNode -32601 (sprintf "Method not found: %s" m))
                | None -> return None
        }

    // ---- HTTP plumbing ---------------------------------------------------

    let parseWorkspace (url: Uri) : WorkspaceId option =
        url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun s ->
            match Guid.TryParse s with
            | true, g -> Some(WorkspaceId g)
            | _ -> None)

    let writeJson (resp: HttpListenerResponse) (status: int) (json: string) =
        let bytes = Encoding.UTF8.GetBytes json
        resp.StatusCode <- status
        resp.ContentType <- "application/json"
        resp.ContentLength64 <- int64 bytes.Length
        resp.OutputStream.Write(bytes, 0, bytes.Length)
        resp.OutputStream.Close()

    let handleContext (ctx: HttpListenerContext) : Task =
        task {
            let req = ctx.Request
            let resp = ctx.Response

            try
                try
                    if req.HttpMethod = "POST" then
                        let wsOpt =
                            match req.Url with
                            | null -> None
                            | u -> parseWorkspace u

                        use reader = new StreamReader(req.InputStream, req.ContentEncoding)
                        let! body = reader.ReadToEndAsync()

                        match JsonNode.Parse body with
                        | null ->
                            resp.StatusCode <- 400
                            resp.Close()
                        | :? JsonArray as arr ->
                            // JSON-RPC batch: collect every response that isn't a notification.
                            let responses = JsonArray()

                            for el in arr do
                                match el with
                                | null -> ()
                                | n ->
                                    let! r = dispatch wsOpt n

                                    match r with
                                    | Some o -> responses.Add o
                                    | None -> ()

                            if responses.Count = 0 then
                                resp.StatusCode <- 202
                                resp.Close()
                            else
                                writeJson resp 200 (responses.ToJsonString())
                        | node ->
                            match! dispatch wsOpt node with
                            | Some o -> writeJson resp 200 (o.ToJsonString())
                            | None ->
                                resp.StatusCode <- 202
                                resp.Close()
                    elif req.HttpMethod = "DELETE" then
                        // Session teardown — nothing to clean up (stateless).
                        resp.StatusCode <- 200
                        resp.Close()
                    else
                        // GET (server-initiated SSE) is unsupported; 405 tells the
                        // client there is no server-to-client stream.
                        resp.StatusCode <- 405
                        resp.Close()
                with _ ->
                    try
                        resp.StatusCode <- 500
                        resp.Close()
                    with _ ->
                        ()
            finally
                ()
        }
        :> Task

    let acceptLoop () : Task =
        task {
            while not cts.IsCancellationRequested do
                let! ctx =
                    task {
                        try
                            let! c = listener.GetContextAsync()
                            return Some c
                        with _ ->
                            return None
                    }

                match ctx with
                | Some c -> Task.Run(fun () -> handleContext c) |> ignore
                | None -> ()
        }
        :> Task

    do
        listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
        listener.Start()
        Task.Run(fun () -> acceptLoop ()) |> ignore

    /// The loopback port the server is listening on.
    member _.Port = port

    /// The MCP-servers map to attach to a session running against
    /// <paramref name="workspaceId"/>. The single <c>"avelia"</c> server's URL
    /// embeds the workspace id so tool calls resolve to the right workspace.
    member _.McpServersFor(workspaceId: WorkspaceId) : IReadOnlyDictionary<string, McpServerConfig> =
        let url = sprintf "http://127.0.0.1:%d/mcp/%O" port (WorkspaceId.value workspaceId)

        let headers = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>

        let d = Dictionary<string, McpServerConfig>()
        d["avelia"] <- McpServerConfig.Http(url, headers)
        d :> IReadOnlyDictionary<string, McpServerConfig>

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()

            try
                listener.Stop()
            with _ ->
                ()

            try
                listener.Close()
            with _ ->
                ()

            cts.Dispose()
