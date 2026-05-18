namespace Avelia.Vcs.GitHub

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Octokit.Caching
open Octokit.Internal

// ============================================================================
//  Response cache — backs Octokit's CachingHttpClient
//
//  Octokit ships <see cref="CachingHttpClient"/> which already implements
//  the ETag round-trip (adds If-None-Match on GET, serves cached body on
//  304). All we need is an <see cref="IResponseCache"/> that maps
//  Octokit's <see cref="IRequest"/> to a cached <see cref="CachedResponse.V1"/>.
//
//  This implementation is in-memory and process-scoped. A disk-backed
//  variant lands in B-11 (persistence) so 304 hits survive app restarts;
//  for v1 the warm-up cost of a cold cache is irrelevant against the
//  polling cadence (PR detail every 30-60s).
// ============================================================================

/// In-memory <see cref="IResponseCache"/>. Keys requests by the absolute
/// URL <c>(BaseAddress + Endpoint)</c> — the same string GitHub's CDN uses
/// for its ETag/If-None-Match round-trip. Query parameters are part of the
/// endpoint so requests with different filters cache independently.
///
/// <para>Thread-safety: backed by <see cref="ConcurrentDictionary"/>.
/// Concurrent first-touch on the same URL may race two backend fetches
/// (the second one wins the dictionary slot); this is acceptable for v1
/// — both calls 200 and serve fresh data, just paying the ETag tax twice.
/// A future variant could deduplicate via per-key <c>SemaphoreSlim</c>
/// when polling cadence makes the wasted fetches measurable.</para>
type InMemoryResponseCache() =
    let store = ConcurrentDictionary<string, CachedResponse.V1>(StringComparer.Ordinal)

    /// Canonical cache key for a request. Public for tests that need to
    /// pre-seed or assert on cache contents without driving live HTTP.
    static member Key(request: IRequest) : string =
        if isNull (box request) then
            ""
        else
            // Endpoint is absolute when BaseAddress is null, otherwise
            // we form the full URL the same way Octokit's adapter does.
            // The absolute URL is what GitHub keys ETags on.
            let baseUri = request.BaseAddress
            let endpoint = request.Endpoint

            if isNull endpoint then ""
            elif endpoint.IsAbsoluteUri then endpoint.AbsoluteUri
            elif isNull baseUri then endpoint.OriginalString
            else Uri(baseUri, endpoint).AbsoluteUri

    /// Test/diagnostic hook: how many entries are cached. The shell
    /// might surface this in a Settings → Diagnostics row eventually.
    member _.Count = store.Count

    /// Test hook: drop the cache. Production callers never need this —
    /// the process-scoped lifetime is short enough that staleness is
    /// bounded by ETag round-trips, not by cache growth.
    member _.Clear() = store.Clear()

    interface IResponseCache with
        member _.GetAsync(request: IRequest) : Task<CachedResponse.V1> =
            let key = InMemoryResponseCache.Key request

            match store.TryGetValue key with
            | true, cached -> Task.FromResult cached
            // Octokit's CachingHttpClient treats null as "cache miss",
            // so returning a null-bearing Task is the correct shape.
            // Using FromResult<V1>(null) keeps the type explicit.
            | _ -> Task.FromResult<CachedResponse.V1>(Unchecked.defaultof<_>)

        member _.SetAsync(request: IRequest, cachedResponse: CachedResponse.V1) : Task =
            let key = InMemoryResponseCache.Key request

            if not (String.IsNullOrEmpty key) && not (isNull (box cachedResponse)) then
                store.[key] <- cachedResponse

            Task.CompletedTask
