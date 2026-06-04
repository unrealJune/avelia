namespace Avelia.Services

open System
open System.Threading.Tasks
open Avelia.Core.Abstractions
open Avelia.Agent.Copilot

/// Real <c>ISettingsService</c> backed by an <c>ISettingsStore</c>. Mirrors the
/// stub's "setter clones the record" shape but persists each change. Settings
/// writes are user-driven and serial, so the load-modify-save isn't guarded as
/// a single transaction (the store locks each call individually).
///
/// The GitHub-token methods route to the credential vault (never the settings
/// record) and report resolvability via the shared <see cref="IGitHubTokenSource"/>.
type SettingsService(store: ISettingsStore, credentials: ICredentialStore, tokenSource: IGitHubTokenSource) =

    let update (ct: System.Threading.CancellationToken) (f: AppearanceSettings -> AppearanceSettings) : Task =
        task {
            let! current = store.LoadAsync ct
            let! _ = store.SaveAsync(f current, ct)
            return ()
        }
        :> Task

    interface ISettingsService with
        member _.GetAsync(ct) = store.LoadAsync ct
        member _.SetAccentAsync(accent, ct) = update ct (fun s -> { s with Accent = accent })
        member _.SetDensityAsync(density, ct) = update ct (fun s -> { s with Density = density })
        member _.SetTransparencyAsync(enabled, ct) = update ct (fun s -> { s with Transparency = enabled })

        member _.SetOpenWithRightPanelAsync(enabled, ct) =
            update ct (fun s -> { s with OpenWithRightPanel = enabled })

        member _.SetDefaultModelAsync(model, ct) = update ct (fun s -> { s with DefaultModel = model })
        member _.SetExtendedThinkingAsync(enabled, ct) = update ct (fun s -> { s with ExtendedThinking = enabled })

        member _.SetGitHubTokenAsync(token, ct) =
            if String.IsNullOrWhiteSpace token then
                credentials.DeleteAsync(GitHubTokenKeys.Pat, ct)
            else
                credentials.SetAsync(GitHubTokenKeys.Pat, token, ct)

        member _.HasGitHubTokenAsync(ct) =
            task {
                let! result = tokenSource.GetTokenAsync ct
                return result.IsSuccess
            }
