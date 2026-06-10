namespace Avelia.Agent.Copilot

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open GitHub.Copilot
open Avelia.Core.Abstractions

/// SDK model type, aliased to avoid the name clash with Avelia's neutral
/// <see cref="Avelia.Core.Abstractions.ModelInfo"/> (both namespaces are open).
type private SdkModelInfo = GitHub.Copilot.ModelInfo

/// Live <see cref="IModelCatalogService"/> backed by the Copilot SDK's
/// <c>ListModelsAsync</c>. Builds a short-lived <see cref="CopilotClient"/> with
/// the user's token, queries the catalog, maps to the neutral
/// <see cref="ModelInfo"/> shape, and disposes.
///
/// Any failure — no token, offline, SDK error, or an empty catalog — falls back
/// to <c>ModelCatalog.presets</c> so the Settings → Agents picker is never
/// empty. The working directory is irrelevant to a catalog read, so a temp dir
/// is used by default.
type CopilotModelCatalog(tokenSource: IGitHubTokenSource, workingDirectory: string) =

    let presets () =
        Success(ModelCatalog.presets :> IReadOnlyList<ModelInfo>)

    /// Best-effort one-line capability blurb from the SDK metadata: context
    /// window first, then a premium-cost hint. The SDK's nested metadata
    /// (C#, no nullable annotations) is accessed directly and guarded by a
    /// catch-all — any missing joint just yields an empty blurb rather than a
    /// crash or a pile of nullness checks.
    let describe (m: SdkModelInfo) : string =
        try
            let parts = ResizeArray<string>()
            let ctx = m.Capabilities.Limits.MaxContextWindowTokens

            if ctx > 0 then
                parts.Add(sprintf "%dK context" (ctx / 1000))

            match m.Billing with
            | null -> ()
            | billing ->
                if billing.Multiplier.HasValue && billing.Multiplier.Value > 1.0 then
                    parts.Add(sprintf "%g× cost" billing.Multiplier.Value)

            String.Join(" · ", parts)
        with _ ->
            ""

    let toModelInfo (m: SdkModelInfo) : ModelInfo =
        let efforts: IReadOnlyList<string> =
            match m.SupportedReasoningEfforts with
            | null -> Array.empty :> IReadOnlyList<_>
            | list -> list |> Seq.filter (fun s -> not (String.IsNullOrWhiteSpace s)) |> Seq.toArray :> IReadOnlyList<_>

        // Id is guaranteed non-blank by the caller's filter.
        { Id = m.Id
          DisplayName = (if String.IsNullOrWhiteSpace m.Name then m.Id else m.Name)
          Description = describe m
          ReasoningEfforts = efforts }

    /// Production convenience: catalog reads don't touch the working tree, so a
    /// temp directory satisfies the SDK's required <c>WorkingDirectory</c>.
    new(tokenSource: IGitHubTokenSource) = CopilotModelCatalog(tokenSource, IO.Path.GetTempPath())

    interface IModelCatalogService with
        member _.ListModelsAsync(ct) =
            task {
                try
                    match! tokenSource.GetTokenAsync ct with
                    | Failure _ -> return presets ()
                    | Success token when String.IsNullOrEmpty token -> return presets ()
                    | Success token ->
                        let opts =
                            CopilotClientOptions(
                                Mode = CopilotClientMode.CopilotCli,
                                GitHubToken = token,
                                WorkingDirectory = workingDirectory
                            )

                        use client = new CopilotClient(opts)
                        do! client.StartAsync ct
                        let! models = client.ListModelsAsync ct

                        let mapped =
                            models
                            |> Seq.filter (fun m -> not (isNull (box m)) && not (String.IsNullOrWhiteSpace m.Id))
                            |> Seq.map toModelInfo
                            |> Seq.toArray

                        if mapped.Length = 0 then
                            return presets ()
                        else
                            return Success(mapped :> IReadOnlyList<ModelInfo>)
                with _ ->
                    // Offline / SDK / transport failure: never leave the picker
                    // empty — the user can still run on a preset.
                    return presets ()
            }
