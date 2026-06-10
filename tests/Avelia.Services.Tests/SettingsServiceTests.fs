module Avelia.Services.Tests.SettingsServiceTests

open System.Threading
open Xunit
open Avelia.Core
open Avelia.Core.Abstractions
open Avelia.Persistence
open Avelia.Services

let private ct = CancellationToken.None

[<Fact>]
let ``GetAsync returns the stored snapshot`` () =
    let store = InMemorySettingsStore(DesignData.defaultAppearance)

    let svc =
        SettingsService(store, FakeCredentialStore(), FakeGitHubTokenSource(Failure AveliaError.Unauthorized))
        :> ISettingsService

    Assert.Equal(DesignData.defaultAppearance, (svc.GetAsync ct).Result)

[<Fact>]
let ``setters persist through the store`` () =
    let store = InMemorySettingsStore(DesignData.defaultAppearance)

    let svc =
        SettingsService(store, FakeCredentialStore(), FakeGitHubTokenSource(Failure AveliaError.Unauthorized))
        :> ISettingsService

    svc.SetAccentAsync(AccentChoice.Violet, ct).Wait()
    svc.SetDensityAsync(Density.Compact, ct).Wait()

    let snap = (svc.GetAsync ct).Result
    Assert.Equal(AccentChoice.Violet, snap.Accent)
    Assert.Equal(Density.Compact, snap.Density)

[<Fact>]
let ``SetGitHubToken writes the PAT to the credential vault`` () =
    let store = InMemorySettingsStore(DesignData.defaultAppearance)
    let cred = FakeCredentialStore() :> ICredentialStore

    let svc =
        SettingsService(store, cred, FakeGitHubTokenSource(Failure AveliaError.Unauthorized)) :> ISettingsService

    (svc.SetGitHubTokenAsync("ghp_secret", ct)).Result |> ignore
    Assert.Equal(Success "ghp_secret", (cred.GetAsync(GitHubTokenKeys.Pat, ct)).Result)

[<Fact>]
let ``HasGitHubToken reflects the token source`` () =
    let store = InMemorySettingsStore(DesignData.defaultAppearance)

    let connected =
        SettingsService(store, FakeCredentialStore(), FakeGitHubTokenSource(Success "tok")) :> ISettingsService

    let disconnected =
        SettingsService(store, FakeCredentialStore(), FakeGitHubTokenSource(Failure AveliaError.Unauthorized))
        :> ISettingsService

    Assert.True((connected.HasGitHubTokenAsync ct).Result)
    Assert.False((disconnected.HasGitHubTokenAsync ct).Result)
