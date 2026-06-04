namespace Avelia.Agent.Copilot

open System.Threading
open System.Threading.Tasks
open Avelia.Core.Abstractions

// ----------------------------------------------------------------------------
//  Copilot driver — shared types
//
//  The driver wraps GitHub.Copilot.SDK 1.0.0 (the .NET client for the Copilot
//  CLI server) behind the core's IAgentSession contracts. Per backend.md
//  decision 2 we take the SDK directly — it manages its own JSON-RPC subprocess
//  to the Copilot CLI internally, so there's no sidecar to bundle.
// ----------------------------------------------------------------------------

/// Static configuration for the Copilot driver. Distinct from the per-session
/// <c>AgentSessionConfig</c> — this is process-level (which CLI binary to host
/// in interactive mode).
type CopilotSettings =
    {
        /// Command spawned in a ConPTY for interactive mode. The SDK is bypassed
        /// in interactive mode (the terminal IS the UI), so we launch the raw
        /// CLI. Resolved off <c>PATH</c> by default; absolute path acceptable.
        CliCommand: string
    }

module CopilotSettings =
    let defaults = { CliCommand = "copilot" }

/// Source of a GitHub token for the Copilot SDK. Decouples this driver from the
/// <c>Avelia.Vcs.GitHub</c> auth project (no project reference): composition
/// (B-12) wires this to <c>GitHubAuth</c>'s stored token from B-3, reusing the
/// user's GitHub App / PAT credential. A <c>Failure</c> surfaces as
/// <c>Unauthorized</c> at <c>StartHeadlessAsync</c> rather than a crash.
type IGitHubTokenSource =
    abstract GetTokenAsync: CancellationToken -> Task<OperationResult<string>>
