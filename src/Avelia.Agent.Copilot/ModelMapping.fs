namespace Avelia.Agent.Copilot

open Avelia.Core.Abstractions

/// Maps Avelia's <c>ModelChoice</c> onto Copilot's model-catalog ids.
///
/// Defers to the canonical <c>ModelCatalog.idOfChoice</c> (in
/// <c>Avelia.Core.Abstractions</c>) so this outbound mapping and the inbound
/// id→choice mapping the model picker uses share one source of truth and can't
/// drift. <c>CustomModel</c> passes through verbatim; a blank custom name maps
/// to <c>""</c> meaning "let the SDK pick its default" (we leave
/// <c>SessionConfig.Model</c> unset).
[<RequireQualifiedAccess>]
module ModelMapping =

    let toCopilotModelId (m: ModelChoice) : string = ModelCatalog.idOfChoice m
