module Avelia.Core.Tests.Generators

open System
open FsCheck
open FsCheck.FSharp
open Avelia.Core.Abstractions

/// Domain-aware FsCheck generators. The defaults FsCheck infers for our types
/// would produce nonsense (e.g. negative diff counts, empty branch names), so
/// we curate generators here and register them as the canonical arbitraries
/// for property tests.

module Gen =

    /// Branch-name strings constrained to characters BranchName.TryCreate accepts.
    let branchName: Gen<BranchName> =
        let allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_./"

        let allowedNoSlash =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"

        gen {
            let! len = Gen.choose (1, 24)
            let! head = Gen.elements allowedNoSlash
            let! mid = Gen.listOfLength (max 0 (len - 2)) (Gen.elements allowed)
            let! tail = Gen.elements allowedNoSlash

            let s =
                System.String(Array.append (Array.append [| head |] (List.toArray mid)) [| tail |])
            // Reject if validation fails (defence — generator should be tight).
            match BranchName.TryCreate s with
            | Ok b -> return b
            | Error _ ->
                // Fall back: trivial branch name.
                return BranchName.Create "main"
        }

    let workspaceStatus: Gen<WorkspaceStatus> =
        Gen.elements
            [ WorkspaceStatus.Draft
              WorkspaceStatus.Active
              WorkspaceStatus.Ready
              WorkspaceStatus.Conflict
              WorkspaceStatus.Archived
              WorkspaceStatus.Open ]

    let modelChoice: Gen<ModelChoice> = Gen.elements [ Sonnet45; Opus41; Haiku45 ]

    let messageId: Gen<MessageId> =
        Gen.constant () |> Gen.map (fun () -> MessageId.create ())

    let userMessage: Gen<UserMessage> =
        gen {
            let! id = messageId
            let! text = ArbMap.defaults |> ArbMap.generate<string>

            return
                { Id = id
                  Text = if isNull text then "" else text
                  Refs = [||]
                  Timestamp = DateTimeOffset.UnixEpoch }
        }

    let agentMessage: Gen<AgentMessage> =
        gen {
            let! id = messageId
            let! text = ArbMap.defaults |> ArbMap.generate<string>

            return
                { Id = id
                  Text = if isNull text then "" else text
                  Timestamp = DateTimeOffset.UnixEpoch }
        }

    let messageEvent: Gen<MessageEvent> =
        Gen.oneof
            [ userMessage |> Gen.map UserMessageAppended
              agentMessage |> Gen.map AgentMessageAppended ]

/// Arbitraries registered as the default for property tests in this assembly.
type Arbs =
    static member BranchName() = Arb.fromGen Gen.branchName
    static member WorkspaceStatus() = Arb.fromGen Gen.workspaceStatus
    static member ModelChoice() = Arb.fromGen Gen.modelChoice
    static member MessageEvent() = Arb.fromGen Gen.messageEvent
