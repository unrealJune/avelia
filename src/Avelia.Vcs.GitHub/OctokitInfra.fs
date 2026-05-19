namespace Avelia.Vcs.GitHub

open System
open System.Net.Http
open System.Threading
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
//
//  Socket-pool discipline: <see cref="HttpClientAdapter"/> wraps an
//  <see cref="HttpClient"/> with its own socket pool. Constructing one
//  per call would leak sockets across the process lifetime (per
//  Microsoft's HttpClient guidance). The <c>SharedHttpClient</c>
//  singleton therefore lazily materialises ONE <see cref="HttpMessageHandler"/>
//  and reuses it across every Octokit client this process builds — auth
//  flows, API calls, and the caching layer all share the same connection
//  pool. The default factory hands out the same <see cref="IHttpClient"/>
//  reference on every invocation.
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

/// Lazily-initialised process-wide <see cref="IHttpClient"/>. Held in a
/// <see cref="Lazy"/> so concurrent first-touch never materialises two
/// pools, and exposed as a function so tests that need their own pool
/// (e.g. integration tests against a sandbox endpoint) can pass an
/// alternative factory.
[<RequireQualifiedAccess>]
module internal SharedHttpClient =

    let private instance =
        Lazy<IHttpClient>(fun () ->
            // Single HttpMessageHandler instance wraps the socket pool.
            // <see cref="HttpClientAdapter"/> takes a factory rather than
            // an instance, so we close over a singleton via Lazy too — the
            // factory is invoked at most once even if Octokit asks more
            // than once.
            let handler = lazy (new HttpClientHandler() :> HttpMessageHandler)
            new HttpClientAdapter(Func<HttpMessageHandler>(fun () -> handler.Value)) :> IHttpClient)

    /// Get the shared <see cref="IHttpClient"/>. Same reference on every
    /// call; safe to pass into multiple Octokit <c>Connection</c>s.
    let get () : IHttpClient = instance.Value

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
    /// Returns the SAME process-wide instance on every call so all
    /// Octokit clients share one connection pool — see
    /// <see cref="SharedHttpClient"/> for the rationale.
    ///
    /// <para>Tests substitute their own <see cref="IHttpClient"/>
    /// implementations (scripted responses, asserting recorders) by
    /// passing a different factory to <c>GitHubAuth</c> /
    /// <c>GitHubClient</c>. Each call to a test factory may return a new
    /// stub — tests don't need pool sharing.</para>
    let defaultHttpClientFactory: Func<IHttpClient> =
        Func<IHttpClient>(fun () -> SharedHttpClient.get ())

    /// Build an Octokit <c>Connection</c>. The caller picks the base
    /// address (<c>https://api.github.com</c> for public GitHub,
    /// <c>https://ghe.example/api/v3</c> for GHES), the
    /// <see cref="Octokit.ICredentialStore"/> (anonymous for the
    /// device-flow handshake; bearer for everything else), and the
    /// underlying <see cref="IHttpClient"/> — which already has any
    /// caching layer (<c>CachingHttpClient</c>) wrapped around it.
    ///
    /// <para>Uses <see cref="SimpleJsonSerializer"/> — Octokit 14's
    /// default and recommended <see cref="IJsonSerializer"/> backed by
    /// System.Text.Json. The Newtonsoft variant was deprecated in
    /// Octokit 12.</para>
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
