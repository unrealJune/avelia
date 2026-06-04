namespace Avelia.Agent.Copilot

open Avelia.Core.Abstractions

/// Maps Avelia's <c>ModelChoice</c> onto Copilot's model-catalog ids.
///
/// Best-effort: Avelia's preset choices are Claude-centric (the shell's model
/// picker), and Copilot hosts those models under its own catalog ids. The exact
/// ids drift with Copilot's catalog, so composition (B-12) can reconcile these
/// against <c>CopilotClient.ListModelsAsync</c> at runtime; here we pick the
/// stable public ids. <c>CustomModel</c> passes through verbatim. An empty
/// result means "let the SDK pick its default model" (we leave
/// <c>SessionConfig.Model</c> unset).
[<RequireQualifiedAccess>]
module ModelMapping =

    let toCopilotModelId (m: ModelChoice) : string =
        match m with
        | Sonnet45 -> "claude-sonnet-4.5"
        | Opus41 -> "claude-opus-4.1"
        | Haiku45 -> "claude-haiku-4.5"
        | CustomModel name -> if System.String.IsNullOrWhiteSpace name then "" else name
