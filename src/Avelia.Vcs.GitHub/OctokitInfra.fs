namespace Avelia.Vcs.GitHub

open System
open System.Net.Http
open System.Threading.Tasks
open Octokit.Internal

// ============================================================================
//  Shared Octokit wiring
//
//  Both <c>Auth.fs</c> (device-flow + PAT) and <c>ApiClient.fs</c> (REST
//  surface) need to construct Octokit clients. Keeping the constructor
//  shape in one place stops the User-Agent / serializer / base-address
//  choices from drifting between layers, and gives tests a single seam:
//  pass an alternative <c>IHttpClient</c> factory and the rest of the
//  graph follows.
// ============================================================================

/// Holds a single static <see cref="Octokit.Credentials"/> value. Octokit
/// calls <c>GetCredentials</c> on every request, so a static instance is
/// fine — token rotation is handled by recreating the client when a new
/// token comes in (one credential per process for v1; B-12 onboarding may
/// add per-account credentials).
///
/// <para><c>token = ""</c> ⇒ <see cref="Octokit.Credentials.Anonymous"/>;
/// used for the device-flow handshake where no bearer is yet available.</para>
type internal StaticOctokitCredentials(token: string) =
    let cached =
        if String.IsNullOrEmpty token then
            Octokit.Credentials.Anonymous
        else
            Octokit.Credentials(token, Octokit.AuthenticationType.Bearer)

    interface Octokit.ICredentialStore with
        member _.GetCredentials() = Task.FromResult cached

/// Construction helpers for Octokit's <see cref="Octokit.Connection"/> /
/// <see cref="Octokit.GitHubClient"/>. Internal — production callers go
/// through <c>GitHubAuth</c> or <c>GitHubClient.CreateAsync</c>.
[<RequireQualifiedAccess>]
module internal OctokitFactory =

    /// Product header GitHub sees in the <c>User-Agent</c>. Per
    /// GitHub's API policy every request needs a UA; consistent labels
    /// help us debug noisy clients on their telemetry side.
    let productInfo = Octokit.ProductHeaderValue("Avelia", "0.1")

    /// Default factory for the production Octokit <see cref="IHttpClient"/>.
    /// Tests substitute their own <see cref="IHttpClient"/> implementations
    /// (scripted responses, asserting recorders) by passing a different
    /// factory to <c>GitHubAuth</c> / <c>GitHubClient</c>.
    let defaultHttpClientFactory: Func<IHttpClient> =
        Func<IHttpClient>(fun () ->
            new HttpClientAdapter(Func<HttpMessageHandler>(fun () -> new HttpClientHandler())) :> IHttpClient)

    /// Build an Octokit <c>Connection</c>. The caller picks the base
    /// address (<c>https://api.github.com</c> for public GitHub,
    /// <c>https://ghe.example/api/v3</c> for GHES), the
    /// <see cref="Octokit.ICredentialStore"/> (anonymous for the
    /// device-flow handshake; bearer for everything else), and the
    /// underlying <see cref="IHttpClient"/> — which already has any
    /// caching layer (<c>CachingHttpClient</c>) wrapped around it.
    let buildConnection
        (baseAddress: Uri)
        (credentials: Octokit.ICredentialStore)
        (httpClient: IHttpClient)
        : Octokit.Connection =
        Octokit.Connection(productInfo, baseAddress, credentials, httpClient, SimpleJsonSerializer())

    /// Build an Octokit <see cref="Octokit.GitHubClient"/> wrapping a
    /// Connection. Convenience for callers who want the high-level
    /// REST surface (<c>client.User</c>, <c>client.Oauth</c>, etc.).
    let buildClient
        (baseAddress: Uri)
        (credentials: Octokit.ICredentialStore)
        (httpClient: IHttpClient)
        : Octokit.GitHubClient =
        Octokit.GitHubClient(buildConnection baseAddress credentials httpClient)

    /// Default public-GitHub API base address. GHES callers pass
    /// their own <c>https://ghe.example/api/v3</c> URI.
    let defaultApiBaseAddress: Uri = Octokit.GitHubClient.GitHubApiUrl
