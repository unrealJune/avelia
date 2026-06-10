namespace Avelia.Services.Tests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open FSharp.Control
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot
open Avelia.Vcs.GitHub.Auth

/// A token source returning a fixed result.
type FakeGitHubTokenSource(result: OperationResult<string>) =
    interface IGitHubTokenSource with
        member _.GetTokenAsync(_ct) = Task.FromResult result

// ---------------------------------------------------------------------------
//  Shared fakes for the service unit tests. Real stores (InMemory*) are used
//  where convenient; these fakes stand in for the git / agent boundaries that
//  would otherwise need a subprocess.
// ---------------------------------------------------------------------------

/// A configurable <c>IGitInspection</c>. <c>StatusResult</c> drives the
/// validate-on-add gate in RepositoryService; the rest return empty success.
type FakeGitInspection(?statusResult: OperationResult<WorktreeStatus>) =
    let okStatus: WorktreeStatus =
        { Branch = BranchName.Create "main"
          AheadBehind = { Ahead = 0; Behind = 0 }
          Files = [||]
          HasUncommittedChanges = false }

    let status = defaultArg statusResult (Success okStatus)
    member val StatusCalls = 0 with get, set

    interface IGitInspection with
        member this.StatusAsync(_worktree, _ct) =
            this.StatusCalls <- this.StatusCalls + 1
            Task.FromResult status

        member _.LogAsync(_worktree, _limit, _ct) =
            Task.FromResult(Success(([||]: CommitInfo[]) :> IReadOnlyList<_>))

        member _.ListBranchesAsync(_repo, _ct) =
            Task.FromResult(Success(([||]: BranchName[]) :> IReadOnlyList<_>))

        member _.ListWorktreesAsync(_repo, _ct) =
            Task.FromResult(Success(([||]: Worktree[]) :> IReadOnlyList<_>))

/// A configurable <c>IGitOperations</c> recording the last worktree-add request
/// and returning a scripted result.
type FakeGitOperations(?worktreeAddResult: OperationResult<Worktree>) =
    member val LastWorktreeRepo = "" with get, set
    member val LastWorktreeBranch = "" with get, set
    member val LastWorktreePath = "" with get, set
    member val WorktreeAddCalls = 0 with get, set

    interface IGitOperations with
        member this.WorktreeAddAsync(repo, branch, worktree, _ct) =
            this.WorktreeAddCalls <- this.WorktreeAddCalls + 1
            this.LastWorktreeRepo <- repo.Value
            this.LastWorktreeBranch <- branch.Value
            this.LastWorktreePath <- worktree.Value

            let result =
                defaultArg
                    worktreeAddResult
                    (Success
                        { Path = worktree
                          Branch = branch
                          Head = CommitId.Create(System.String('a', 40))
                          IsLocked = false })

            Task.FromResult result

        member _.WorktreeRemoveAsync(_worktree, _force, _ct) = Task.FromResult(Success())

        member _.CommitAsync(_worktree, _msg, _ct) =
            Task.FromResult(Success(CommitId.Create(System.String('b', 40))))

        member _.PushAsync(_worktree, _remote, _ct) = Task.FromResult(Success())
        member _.FetchAsync(_worktree, _remote, _ct) = Task.FromResult(Success())
        member _.CheckoutAsync(_worktree, _branch, _ct) = Task.FromResult(Success())
        member _.BranchCreateAsync(_repo, _branch, _baseRef, _ct) = Task.FromResult(Success())
        member _.BranchDeleteAsync(_repo, _branch, _force, _ct) = Task.FromResult(Success())

/// In-memory <c>ICredentialStore</c> (no Windows Credential Manager dependency).
type FakeCredentialStore() =
    let store = Dictionary<string, string>()

    interface ICredentialStore with
        member _.GetAsync(key, _ct) =
            match store.TryGetValue key with
            | true, v -> Task.FromResult(Success v)
            | _ -> Task.FromResult(Failure(AveliaError.NotFound("credential:" + key)))

        member _.SetAsync(key, secret, _ct) =
            store.[key] <- secret
            Task.FromResult(Success())

        member _.DeleteAsync(key, _ct) =
            store.Remove key |> ignore
            Task.FromResult(Success())

[<AutoOpen>]
module FakeAuthHelpers =
    /// A token a stored account would yield. Helper for building a fake auth.
    let storedToken (login: string) (token: string) : GitHubAccessToken =
        { Account = GitHubLogin.Create login
          Token = token
          Method = AuthMethod.Pat
          ScopesGranted = [||]
          ExpiresAt = DateTimeOffset.MaxValue
          RefreshToken = ""
          RefreshExpiresAt = DateTimeOffset.MaxValue }

/// A configurable <c>IGitHubAuth</c>: only the stored-account read paths are
/// meaningful; the device-flow / PAT sign-in paths return Unauthorized.
type FakeGitHubAuth(accounts: GitHubAccessToken list) =
    let byLogin = accounts |> List.map (fun t -> t.Account.Value, t) |> dict

    interface IGitHubAuth with
        member _.ListStoredAccountsAsync(_ct) =
            Task.FromResult(Success(accounts |> List.map (fun t -> t.Account) |> List.toArray :> IReadOnlyList<_>))

        member _.LoadStoredTokenAsync(login, _ct) =
            match byLogin.TryGetValue login.Value with
            | true, t -> Task.FromResult(Success t)
            | _ -> Task.FromResult(Failure(AveliaError.NotFound("credential:" + login.Value)))

        member _.BeginDeviceFlowAsync(_config, _ct) =
            Task.FromResult(Failure AveliaError.Unauthorized)

        member _.CompleteDeviceFlowAsync(_config, _challenge, _ct) =
            Task.FromResult(Failure AveliaError.Unauthorized)

        member _.SignInWithPatAsync(_config, _pat, _ct) =
            Task.FromResult(Failure AveliaError.Unauthorized)

        member _.SignOutAsync(_login, _ct) = Task.FromResult(Success())

/// A driveable headless session: tests push <c>AgentEvent</c>s via <c>Emit</c>
/// and complete the stream via <c>Complete</c>.
type FakeHeadlessSession(sessionId: SessionId, workspace: RepoPath) =
    let channel = Channel.CreateUnbounded<AgentEvent>()
    let mutable consumed = 0
    member val Sent = ResizeArray<string>()
    member val Disposed = 0 with get, set
    member _.Emit(ev: AgentEvent) = channel.Writer.TryWrite ev |> ignore
    member _.Complete() = channel.Writer.TryComplete() |> ignore

    interface IAgentSession with
        member _.SessionId = sessionId
        member _.Workspace = workspace
        member _.InterruptAsync(_ct) = Task.CompletedTask

        member _.WaitForExitAsync(_ct) =
            Task.FromResult { ExitCode = 0; IsClean = true }

    interface IHeadlessAgentSession with
        member _.Events(ct) =
            if Interlocked.Exchange(&consumed, 1) = 1 then
                invalidOp "Events is single-consumer"

            taskSeq {
                for ev in channel.Reader.ReadAllAsync ct do
                    yield ev
            }

        member this.SendUserMessageAsync(text, _refs, _ct) =
            this.Sent.Add text
            Task.FromResult(Success())

        member _.RespondToPermissionAsync(_id, _decision, _ct) = Task.FromResult(Success())

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            this.Disposed <- this.Disposed + 1
            channel.Writer.TryComplete() |> ignore
            ValueTask.CompletedTask

/// A trivial terminal session (no ConPTY).
type FakeTerminalSessionMin() =
    interface ITerminalSession with
        member _.Size = { Cols = 80; Rows = 24 }
        member _.WriteAsync(_b, _ct) = Task.CompletedTask
        member _.ReadAllAsync(_ct) = taskSeq { () }
        member _.ResizeAsync(_s, _ct) = Task.CompletedTask
        member _.SendInterruptAsync(_ct) = Task.CompletedTask

        member _.WaitForExitAsync(_ct) =
            Task.FromResult { ExitCode = 0; IsClean = true }

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask.CompletedTask

type FakeInteractiveSession(workspace: RepoPath) =
    let terminal = FakeTerminalSessionMin() :> ITerminalSession

    interface IAgentSession with
        member _.SessionId = SessionId.create ()
        member _.Workspace = workspace
        member _.InterruptAsync(_ct) = Task.CompletedTask

        member _.WaitForExitAsync(_ct) =
            Task.FromResult { ExitCode = 0; IsClean = true }

    interface IInteractiveAgentSession with
        member _.Terminal = terminal

    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask.CompletedTask

/// A factory that hands back a fresh <c>FakeHeadlessSession</c> per start (so
/// per-conversation isolation is observable), or a fixed failure. Interactive
/// starts record the working directory and return a fake session.
type FakeAgentSessionFactory(?failure: AveliaError) =
    member val Sessions = ResizeArray<FakeHeadlessSession>()
    member val StartCount = 0 with get, set
    member val LastInteractiveWorkspace = "" with get, set
    member val LastHeadlessConfig: AgentSessionConfig option = None with get, set
    member val LastInteractiveConfig: AgentSessionConfig option = None with get, set

    interface IAgentSessionFactory with
        member this.StartHeadlessAsync(config, _ct) =
            this.StartCount <- this.StartCount + 1
            this.LastHeadlessConfig <- Some config

            match failure with
            | Some e -> Task.FromResult(Failure e)
            | None ->
                let s = FakeHeadlessSession(SessionId.create (), config.Workspace)
                this.Sessions.Add s
                Task.FromResult(Success(s :> IHeadlessAgentSession))

        member this.StartInteractiveAsync(config, _ct) =
            this.LastInteractiveWorkspace <- config.Workspace.Value
            this.LastInteractiveConfig <- Some config

            match failure with
            | Some e -> Task.FromResult(Failure e)
            | None -> Task.FromResult(Success(FakeInteractiveSession config.Workspace :> IInteractiveAgentSession))
