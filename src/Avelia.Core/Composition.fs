namespace Avelia.Core

open System.Collections.Generic
open Avelia.Core.Abstractions
open Avelia.Core.Stubs

/// Bundle of every service interface the shell consumes. A single record makes
/// the composition root a one-liner and gives ViewModels a typed object to
/// destructure rather than threading 7+ ctor parameters.
type AveliaServices =
    { Repositories: IRepositoryService
      Workspaces: IWorkspaceService
      Conversations: IConversationService
      Diffs: IDiffService
      PullRequests: IPullRequestService
      Runs: IRunService
      Inbox: IInboxService
      Settings: ISettingsService
      /// Agent-session factory for the interactive terminal panel (and reused by
      /// the real conversation service). The stub path supplies a no-op factory.
      Agents: IAgentSessionFactory
      /// Launches an interactive terminal session for a workspace.
      Terminals: ITerminalLaunchService }

module Composition =

    /// Build the entire service graph backed by <see cref="DesignData"/>.
    /// Returns a snapshot the shell can wire ViewModels against. Until the real
    /// persistence/VCS/agent adapters land, this is the only available variant.
    let buildStubServices () : AveliaServices =
        let workspaceConversation (wsId: WorkspaceId) : Conversation option =
            if wsId = DesignData.archiveWorkspaceId then
                Some DesignData.archiveConversation
            else
                None

        let workspaceFiles (wsId: WorkspaceId) : IReadOnlyList<DiffFile> =
            if wsId = DesignData.archiveWorkspaceId then
                DesignData.diffFiles
            else
                upcast Array.empty<DiffFile>

        let prFiles (prId: PullRequestId) : IReadOnlyList<DiffFile> =
            if prId = DesignData.archivePrId then
                DesignData.diffFiles
            else
                upcast Array.empty<DiffFile>

        let prHunks (prId: PullRequestId, file: RelativePath) : IReadOnlyList<DiffHunk> =
            if prId = DesignData.archivePrId then
                DesignData.diffHunks |> Seq.filter (fun h -> h.File = file) |> Seq.toArray :> IReadOnlyList<_>
            else
                upcast Array.empty<DiffHunk>

        let prById = Dictionary<PullRequestId, PullRequest>()
        prById.[DesignData.archivePullRequest.Id] <- DesignData.archivePullRequest

        let prsByWorkspace (wsId: WorkspaceId) =
            if wsId = DesignData.archiveWorkspaceId then
                Some DesignData.archivePullRequest
            else
                None

        { Repositories = StubRepositoryService(DesignData.repositories) :> IRepositoryService
          Workspaces = StubWorkspaceService(DesignData.workspaces) :> IWorkspaceService
          Conversations =
            StubConversationService([ DesignData.archiveConversation ], workspaceConversation) :> IConversationService
          Diffs = StubDiffService(workspaceFiles, prFiles, prHunks) :> IDiffService
          PullRequests = StubPullRequestService(prsByWorkspace, prById) :> IPullRequestService
          Runs = StubRunService() :> IRunService
          Inbox = StubInboxService(DesignData.inboxItems) :> IInboxService
          Settings = StubSettingsService(DesignData.defaultAppearance) :> ISettingsService
          Agents = StubAgentSessionFactory() :> IAgentSessionFactory
          Terminals = StubTerminalLaunchService() :> ITerminalLaunchService }
