namespace Avelia.Services

open System
open System.Collections.Generic
open Avelia.Core
open Avelia.Core.Abstractions
open Avelia.Core.Stubs
open Avelia.Persistence
open Avelia.Vcs.Git
open Avelia.Vcs.GitHub
open Avelia.Vcs.GitHub.Auth
open Avelia.Agent.Copilot

/// The real composition root: assembles the production service graph backed by
/// in-memory stores (B-11 swaps these for SQLite behind the same interfaces),
/// real git + GitHub-auth + Copilot adapters.
///
/// <paramref name="terminalFactory"/> is supplied by the shell because the
/// ConPTY implementation is Windows P/Invoke living in the shell — this
/// platform-agnostic layer can't reference it.
module RealComposition =

    let private emptyList<'T> () = [||] :> IReadOnlyList<'T>

    let buildServices (terminalFactory: ITerminalSessionFactory) : AveliaServices =
        // SQLite-backed stores are the source of truth (CLAUDE.md rule 7). The
        // store set keeps the connection alive via the store closures, so it
        // survives for the app lifetime without an explicit handle. The .db
        // file persists across runs, so startup "hydration" is just the stores
        // reading the existing file.
        let stores =
            (SqliteStores.create (Storage.defaultDbPath ()) DesignData.defaultAppearance).Stores

        let now () = DateTimeOffset.UtcNow

        // Auth + agent driver.
        let credentials = WindowsCredentialStore() :> ICredentialStore
        let auth = GitHubAuth(credentials) :> IGitHubAuth
        let tokenSource = GitHubTokenSource(auth, credentials) :> IGitHubTokenSource

        // Lazy GitHub API client (built on first use from the first signed-in
        // account; re-tried — and cached on success — so signing in after
        // startup works without a restart).
        let ghProvider = GitHubClientProvider(auth)

        let agentFactory =
            CopilotAgentSessionFactory(tokenSource, terminalFactory, CopilotSettings.defaults) :> IAgentSessionFactory

        // Local git.
        let inspection = GitInspector() :> IGitInspection
        let gitOps = GitCli() :> IGitOperations

        // Orchestrator first (the workspace service needs its teardown delegate).
        let conversations =
            new AgentConversationService(agentFactory, stores.Conversations, stores.Workspaces, stores.Settings, now)

        let workspaces =
            WorkspaceService(
                stores.Workspaces,
                stores.Repositories,
                stores.Conversations,
                stores.Settings,
                gitOps,
                inspection,
                Storage.worktreesRoot (),
                now,
                conversations.DisposeConversationAsync
            )

        // Surfaces not yet wired to a real backend keep the stub behaviour
        // (empty data, no crashes) until their own chunks land.
        let diffs = DiffService(stores.Workspaces, inspection)

        let pullRequests =
            PullRequestService((fun ct -> ghProvider.GetAsync ct), stores.Workspaces, stores.Repositories, inspection)

        { Repositories = RepositoryService(stores.Repositories, inspection) :> IRepositoryService
          Workspaces = workspaces :> IWorkspaceService
          Conversations = conversations :> IConversationService
          Diffs = diffs :> IDiffService
          PullRequests = pullRequests :> IPullRequestService
          Runs = StubRunService() :> IRunService
          Inbox = StubInboxService(Seq.empty<InboxItem>) :> IInboxService
          Settings = SettingsService(stores.Settings, credentials, tokenSource) :> ISettingsService
          ModelCatalog = CachingModelCatalog(CopilotModelCatalog(tokenSource)) :> IModelCatalogService
          Agents = agentFactory
          Terminals =
            InteractiveTerminalService(stores.Workspaces, stores.Settings, agentFactory) :> ITerminalLaunchService }
