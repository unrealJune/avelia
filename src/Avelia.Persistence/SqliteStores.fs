namespace Avelia.Persistence

open System
open System.IO
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Avelia.Core.Abstractions

// ----------------------------------------------------------------------------
//  SQLite-backed persistence stores (B-11)
//
//  Same four interfaces as the in-memory stores, so composition swaps one
//  constructor. A single connection is shared across the four stores and
//  guarded by a lock — SQLite connections aren't thread-safe and a desktop app
//  has low write contention, so serializing is simpler than pooling. Stores are
//  the source of truth (CLAUDE.md rule 7); in-memory state is a cache hydrated
//  from here on startup.
// ----------------------------------------------------------------------------

module private Schema =
    let sql =
        """
CREATE TABLE IF NOT EXISTS repositories (
  id TEXT PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL,
  default_base TEXT NOT NULL, is_open INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS workspaces (
  id TEXT PRIMARY KEY, repo_id TEXT NOT NULL, branch TEXT NOT NULL, base TEXT NOT NULL,
  status TEXT NOT NULL, diff_add INTEGER NOT NULL, diff_del INTEGER NOT NULL,
  agent TEXT NOT NULL, last_updated TEXT NOT NULL, last_updated_display TEXT NOT NULL,
  pr_number INTEGER NOT NULL, worktree_path TEXT NOT NULL, conversation_id TEXT NOT NULL,
  reasoning_effort TEXT NOT NULL DEFAULT '', context_tier TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS conversations (
  id TEXT PRIMARY KEY, workspace_id TEXT NOT NULL, title TEXT NOT NULL, last_sequence INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS messages (
  conversation_id TEXT NOT NULL, sequence INTEGER NOT NULL, event_json TEXT NOT NULL,
  PRIMARY KEY (conversation_id, sequence));
CREATE INDEX IF NOT EXISTS ix_workspaces_repo ON workspaces(repo_id);
CREATE INDEX IF NOT EXISTS ix_conversations_ws ON conversations(workspace_id);
CREATE TABLE IF NOT EXISTS settings (
  id INTEGER PRIMARY KEY CHECK (id = 1), accent TEXT NOT NULL, density TEXT NOT NULL,
  transparency INTEGER NOT NULL, open_with_right_panel INTEGER NOT NULL,
  default_model TEXT NOT NULL, reasoning_effort TEXT NOT NULL, context_tier TEXT NOT NULL);
"""

    /// Idempotent column reconciliations for databases created before the
    /// current schema. <c>ADD COLUMN</c> throws if the column is already present
    /// (fresh DBs get it from <c>CREATE TABLE</c>) and <c>DROP COLUMN</c> throws
    /// if it is already gone, so each runs best-effort.
    let migrations =
        [ "ALTER TABLE workspaces ADD COLUMN reasoning_effort TEXT NOT NULL DEFAULT ''"
          "ALTER TABLE workspaces ADD COLUMN context_tier TEXT NOT NULL DEFAULT ''"
          "ALTER TABLE settings ADD COLUMN reasoning_effort TEXT NOT NULL DEFAULT ''"
          "ALTER TABLE settings ADD COLUMN context_tier TEXT NOT NULL DEFAULT ''"
          // The unified model bar replaced the boolean `extended_thinking` with
          // `reasoning_effort`. Drop the orphaned NOT NULL column or every
          // settings upsert fails its INSERT arm with a NOT NULL violation.
          "ALTER TABLE settings DROP COLUMN extended_thinking" ]

/// Shared connection + lock. All store access funnels through <c>run</c>.
type private Db(connectionString: string) =
    let conn = new SqliteConnection(connectionString)
    let gate = obj ()

    do
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- Schema.sql
        cmd.ExecuteNonQuery() |> ignore

        for alter in Schema.migrations do
            try
                use mcmd = conn.CreateCommand()
                mcmd.CommandText <- alter
                mcmd.ExecuteNonQuery() |> ignore
            with _ ->
                () // column already exists

    /// Run <paramref name="f"/> against the connection under the lock.
    member _.run(f: SqliteConnection -> 'a) : 'a = lock gate (fun () -> f conn)

    interface IDisposable with
        member _.Dispose() = conn.Dispose()

// -- helpers -----------------------------------------------------------------

[<AutoOpen>]
module private SqliteHelpers =
    let param (cmd: SqliteCommand) (name: string) (value: obj) =
        cmd.Parameters.AddWithValue(name, value) |> ignore

    let exec (conn: SqliteConnection) (sql: string) (binds: (string * obj) list) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql

        for (n, v) in binds do
            param cmd n v

        cmd.ExecuteNonQuery() |> ignore

    let boolToInt (b: bool) : obj = (if b then 1 else 0) :> obj

    let readRepo (r: SqliteDataReader) : Repository =
        { Id = RepositoryId(Guid.Parse(r.GetString 0))
          Name = r.GetString 1
          Path = RepoPath.Create(r.GetString 2)
          DefaultBase = BranchName.Create(r.GetString 3)
          IsOpen = r.GetInt32 4 <> 0 }

    let readWorkspaceRecord (r: SqliteDataReader) : WorkspaceRecord =
        let ws: Workspace =
            { Id = WorkspaceId(Guid.Parse(r.GetString 0))
              RepoId = RepositoryId(Guid.Parse(r.GetString 1))
              Branch = BranchName.Create(r.GetString 2)
              Base = BranchName.Create(r.GetString 3)
              Status = Codec.statusOfString (r.GetString 4)
              DiffAdd = r.GetInt32 5
              DiffDel = r.GetInt32 6
              Agent = Codec.modelOfString (r.GetString 7)
              LastUpdated = Codec.dtoOfString (r.GetString 8)
              LastUpdatedDisplay = r.GetString 9
              PrNumber = r.GetInt32 10
              ReasoningEffort = r.GetString 13
              ContextTier = r.GetString 14 }

        { Workspace = ws
          WorktreePath = RepoPath.Create(r.GetString 11)
          ConversationId = ConversationId(Guid.Parse(r.GetString 12)) }

    /// Load a conversation row + its message events. Returns None if absent.
    let loadConversation (conn: SqliteConnection) (idSql: string) (binds: (string * obj) list) : Conversation option =
        let header =
            use cmd = conn.CreateCommand()
            cmd.CommandText <- idSql

            for (n, v) in binds do
                param cmd n v

            use r = cmd.ExecuteReader()

            if r.Read() then
                Some(r.GetString 0, Guid.Parse(r.GetString 1), r.GetString 2)
            else
                None

        match header with
        | None -> None
        | Some(convId, workspaceId, title) ->
            let events = ResizeArray<MessageEvent>()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT event_json FROM messages WHERE conversation_id = $c ORDER BY sequence"
            param cmd "$c" convId
            use r = cmd.ExecuteReader()

            while r.Read() do
                events.Add(Codec.messageEventOfJson (r.GetString 0))

            Some
                { Id = ConversationId(Guid.Parse convId)
                  WorkspaceId = WorkspaceId workspaceId
                  Title = title
                  Messages = events.ToArray()
                  LastSequence = events.Count }

// -- stores ------------------------------------------------------------------

type private SqliteRepositoryStore(db: Db) =
    interface IRepositoryStore with
        member _.ListAsync(ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT id,name,path,default_base,is_open FROM repositories"
                use r = cmd.ExecuteReader()
                let xs = ResizeArray<Repository>()

                while r.Read() do
                    xs.Add(readRepo r)

                xs.ToArray() :> IReadOnlyList<_>)
            |> Task.FromResult

        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                use cmd = conn.CreateCommand()
                cmd.CommandText <- "SELECT id,name,path,default_base,is_open FROM repositories WHERE id = $id"
                param cmd "$id" ((RepositoryId.value id).ToString())
                use r = cmd.ExecuteReader()

                if r.Read() then
                    Success(readRepo r)
                else
                    Failure(AveliaError.NotFound(sprintf "repository:%O" id)))
            |> Task.FromResult

        member _.UpsertAsync(repo, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                exec
                    conn
                    "INSERT INTO repositories (id,name,path,default_base,is_open) VALUES ($id,$name,$path,$base,$open)
                     ON CONFLICT(id) DO UPDATE SET name=$name,path=$path,default_base=$base,is_open=$open"
                    [ "$id", (RepositoryId.value repo.Id).ToString() :> obj
                      "$name", repo.Name :> obj
                      "$path", repo.Path.Value :> obj
                      "$base", repo.DefaultBase.Value :> obj
                      "$open", boolToInt repo.IsOpen ])

            Task.FromResult(Success())

        member _.RemoveAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                exec
                    conn
                    "DELETE FROM repositories WHERE id = $id"
                    [ "$id", (RepositoryId.value id).ToString() :> obj ])

            Task.FromResult(Success())

type private SqliteWorkspaceStore(db: Db) =
    let cols =
        "id,repo_id,branch,base,status,diff_add,diff_del,agent,last_updated,last_updated_display,pr_number,worktree_path,conversation_id,reasoning_effort,context_tier"

    let readAll (conn: SqliteConnection) (sql: string) (binds: (string * obj) list) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql

        for (n, v) in binds do
            param cmd n v

        use r = cmd.ExecuteReader()
        let xs = ResizeArray<WorkspaceRecord>()

        while r.Read() do
            xs.Add(readWorkspaceRecord r)

        xs

    interface IWorkspaceStore with
        member _.ListAllAsync(ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                (readAll conn (sprintf "SELECT %s FROM workspaces" cols) []).ToArray() :> IReadOnlyList<_>)
            |> Task.FromResult

        member _.ListByRepoAsync(repoId, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                (readAll
                    conn
                    (sprintf "SELECT %s FROM workspaces WHERE repo_id = $r" cols)
                    [ "$r", (RepositoryId.value repoId).ToString() :> obj ])
                    .ToArray()
                :> IReadOnlyList<_>)
            |> Task.FromResult

        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                let xs =
                    readAll
                        conn
                        (sprintf "SELECT %s FROM workspaces WHERE id = $id" cols)
                        [ "$id", (WorkspaceId.value id).ToString() :> obj ]

                if xs.Count > 0 then
                    Success xs.[0]
                else
                    Failure(AveliaError.NotFound(sprintf "workspace:%O" id)))
            |> Task.FromResult

        member _.UpsertAsync(record, ct) =
            ct.ThrowIfCancellationRequested()
            let ws = record.Workspace

            db.run (fun conn ->
                exec
                    conn
                    "INSERT INTO workspaces (id,repo_id,branch,base,status,diff_add,diff_del,agent,last_updated,last_updated_display,pr_number,worktree_path,conversation_id,reasoning_effort,context_tier)
                     VALUES ($id,$repo,$branch,$base,$status,$da,$dd,$agent,$lu,$lud,$pr,$wt,$conv,$re,$ctx)
                     ON CONFLICT(id) DO UPDATE SET repo_id=$repo,branch=$branch,base=$base,status=$status,diff_add=$da,diff_del=$dd,agent=$agent,last_updated=$lu,last_updated_display=$lud,pr_number=$pr,worktree_path=$wt,conversation_id=$conv,reasoning_effort=$re,context_tier=$ctx"
                    [ "$id", (WorkspaceId.value ws.Id).ToString() :> obj
                      "$repo", (RepositoryId.value ws.RepoId).ToString() :> obj
                      "$branch", ws.Branch.Value :> obj
                      "$base", ws.Base.Value :> obj
                      "$status", Codec.statusToString ws.Status :> obj
                      "$da", ws.DiffAdd :> obj
                      "$dd", ws.DiffDel :> obj
                      "$agent", Codec.modelToString ws.Agent :> obj
                      "$lu", Codec.dtoToString ws.LastUpdated :> obj
                      "$lud", ws.LastUpdatedDisplay :> obj
                      "$pr", ws.PrNumber :> obj
                      "$wt", record.WorktreePath.Value :> obj
                      "$conv", (ConversationId.value record.ConversationId).ToString() :> obj
                      "$re", ws.ReasoningEffort :> obj
                      "$ctx", ws.ContextTier :> obj ])

            Task.FromResult(Success())

        member _.RemoveAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                exec conn "DELETE FROM workspaces WHERE id = $id" [ "$id", (WorkspaceId.value id).ToString() :> obj ])

            Task.FromResult(Success())

type private SqliteConversationStore(db: Db) =
    interface IConversationStore with
        member _.GetAsync(id, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                match
                    loadConversation
                        conn
                        "SELECT id,workspace_id,title FROM conversations WHERE id = $k"
                        [ "$k", (ConversationId.value id).ToString() :> obj ]
                with
                | Some c -> Success c
                | None -> Failure(AveliaError.NotFound(sprintf "conversation:%O" id)))
            |> Task.FromResult

        member _.GetByWorkspaceAsync(workspaceId, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                match
                    loadConversation
                        conn
                        "SELECT id,workspace_id,title FROM conversations WHERE workspace_id = $k"
                        [ "$k", (WorkspaceId.value workspaceId).ToString() :> obj ]
                with
                | Some c -> Success c
                | None -> Failure(AveliaError.NotFound(sprintf "conversation-for-workspace:%O" workspaceId)))
            |> Task.FromResult

        member _.CreateAsync(conversation, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                use tx = conn.BeginTransaction()
                let convId = (ConversationId.value conversation.Id).ToString()

                exec
                    conn
                    "INSERT INTO conversations (id,workspace_id,title,last_sequence) VALUES ($id,$ws,$title,$seq)
                     ON CONFLICT(id) DO UPDATE SET workspace_id=$ws,title=$title,last_sequence=$seq"
                    [ "$id", convId :> obj
                      "$ws", (WorkspaceId.value conversation.WorkspaceId).ToString() :> obj
                      "$title", conversation.Title :> obj
                      "$seq", conversation.Messages.Length :> obj ]

                conversation.Messages
                |> Array.iteri (fun i ev ->
                    exec
                        conn
                        "INSERT OR REPLACE INTO messages (conversation_id,sequence,event_json) VALUES ($c,$s,$j)"
                        [ "$c", convId :> obj
                          "$s", (i + 1) :> obj
                          "$j", Codec.messageEventToJson ev :> obj ])

                tx.Commit())

            Task.FromResult(Success())

        member _.AppendEventAsync(id, event, ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                let convId = (ConversationId.value id).ToString()

                // Current sequence (and existence) under the same lock.
                let current =
                    use cmd = conn.CreateCommand()
                    cmd.CommandText <- "SELECT last_sequence FROM conversations WHERE id = $id"
                    param cmd "$id" convId
                    use r = cmd.ExecuteReader()
                    if r.Read() then Some(r.GetInt32 0) else None

                match current with
                | None -> Failure(AveliaError.NotFound(sprintf "conversation:%O" id))
                | Some seq ->
                    use tx = conn.BeginTransaction()

                    // TitleChanged is a metadata rename: update the persisted
                    // title column (so it survives restart) without appending a
                    // transcript row or advancing the sequence — keeping the
                    // SQLite message stream consistent with the in-memory fold.
                    // Every other event appends to the transcript.
                    match event with
                    | TitleChanged title ->
                        exec
                            conn
                            "UPDATE conversations SET title = $t WHERE id = $id"
                            [ "$t", title :> obj; "$id", convId :> obj ]
                    | _ ->
                        let newSeq = seq + 1

                        exec
                            conn
                            "INSERT INTO messages (conversation_id,sequence,event_json) VALUES ($c,$s,$j)"
                            [ "$c", convId :> obj
                              "$s", newSeq :> obj
                              "$j", Codec.messageEventToJson event :> obj ]

                        exec
                            conn
                            "UPDATE conversations SET last_sequence = $s WHERE id = $id"
                            [ "$s", newSeq :> obj; "$id", convId :> obj ]

                    tx.Commit()

                    match
                        loadConversation
                            conn
                            "SELECT id,workspace_id,title FROM conversations WHERE id = $k"
                            [ "$k", convId :> obj ]
                    with
                    | Some c -> Success c
                    | None -> Failure(AveliaError.NotFound(sprintf "conversation:%O" id)))
            |> Task.FromResult

type private SqliteSettingsStore(db: Db, initial: AppearanceSettings) =
    let read (conn: SqliteConnection) : AppearanceSettings option =
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "SELECT accent,density,transparency,open_with_right_panel,default_model,reasoning_effort,context_tier FROM settings WHERE id = 1"

        use r = cmd.ExecuteReader()

        if r.Read() then
            Some
                { Accent = Codec.accentOfString (r.GetString 0)
                  Density = Codec.densityOfString (r.GetString 1)
                  Transparency = r.GetInt32 2 <> 0
                  OpenWithRightPanel = r.GetInt32 3 <> 0
                  DefaultModel = Codec.modelOfString (r.GetString 4)
                  ReasoningEffort = Codec.reasoningEffortOfString (r.GetString 5)
                  ContextTier = Codec.contextTierOfString (r.GetString 6) }
        else
            None

    let write (conn: SqliteConnection) (s: AppearanceSettings) =
        exec
            conn
            "INSERT INTO settings (id,accent,density,transparency,open_with_right_panel,default_model,reasoning_effort,context_tier)
             VALUES (1,$a,$d,$t,$o,$m,$r,$c)
             ON CONFLICT(id) DO UPDATE SET accent=$a,density=$d,transparency=$t,open_with_right_panel=$o,default_model=$m,reasoning_effort=$r,context_tier=$c"
            [ "$a", Codec.accentToString s.Accent :> obj
              "$d", Codec.densityToString s.Density :> obj
              "$t", boolToInt s.Transparency
              "$o", boolToInt s.OpenWithRightPanel
              "$m", Codec.modelToString s.DefaultModel :> obj
              "$r", Codec.reasoningEffortToString s.ReasoningEffort :> obj
              "$c", Codec.contextTierToString s.ContextTier :> obj ]

    // Seed the row from the initial defaults if the table is empty.
    do
        db.run (fun conn ->
            match read conn with
            | Some _ -> ()
            | None -> write conn initial)

    interface ISettingsStore with
        member _.LoadAsync(ct) =
            ct.ThrowIfCancellationRequested()

            db.run (fun conn ->
                match read conn with
                | Some s -> s
                | None -> initial)
            |> Task.FromResult

        member _.SaveAsync(settings, ct) =
            ct.ThrowIfCancellationRequested()
            db.run (fun conn -> write conn settings)
            Task.FromResult(Success())

/// Owns the SQLite connection and exposes the four stores as a <c>Stores</c>
/// bundle. Dispose to close the connection (and release the file); the app
/// holds one for its lifetime, tests dispose per-test.
type SqliteStoreSet(dbPath: string, initialSettings: AppearanceSettings) =
    let db =
        match Path.GetDirectoryName dbPath with
        | null
        | "" -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

        let connectionString = SqliteConnectionStringBuilder(DataSource = dbPath).ToString()
        new Db(connectionString)

    member val Stores: Stores =
        { Repositories = SqliteRepositoryStore db
          Workspaces = SqliteWorkspaceStore db
          Conversations = SqliteConversationStore db
          Settings = SqliteSettingsStore(db, initialSettings) }

    interface IDisposable with
        member _.Dispose() = (db :> IDisposable).Dispose()

[<RequireQualifiedAccess>]
module SqliteStores =
    /// Open (creating if needed) a SQLite database at <paramref name="dbPath"/>
    /// and return its store set. The caller keeps the handle alive for as long
    /// as the stores are used.
    let create (dbPath: string) (initialSettings: AppearanceSettings) : SqliteStoreSet =
        new SqliteStoreSet(dbPath, initialSettings)
