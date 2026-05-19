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
//  This implementation is in-memory and process-scoped with bounded
//  capacity + time-based expiry — a long-running app polling many PRs
//  would otherwise accumulate cache entries indefinitely (each entry
//  carries the full deserialised body, not just the ETag). A disk-backed
//  variant lands in B-11 (persistence) so 304 hits survive app restarts.
// ============================================================================

/// Eviction policy for <see cref="InMemoryResponseCache"/>. Carried as a
/// record so callers configure capacity + age in one place and the
/// defaults document the chosen trade-offs.
type ResponseCacheLimits =
    {
        /// Hard upper bound on the number of cached entries. When the
        /// cache grows past this, oldest entries (by last-access time)
        /// are evicted until the size falls back under the bound.
        ///
        /// <para>Default 512: rough sizing for a user watching ~10
        /// repos with ~20 PRs each (200 distinct URLs) plus the
        /// dashboard's batched-PR refresh path, doubled for headroom.</para>
        MaxEntries: int
        /// How long a cached entry stays eligible for serving before
        /// it's dropped, regardless of last-access time. ETags don't
        /// have an explicit max age, but an entry older than this is
        /// unlikely to still match the upstream resource — re-fetch.
        ///
        /// <para>Default 1 hour: matches the typical lifetime of
        /// GitHub's CDN cache for unauthenticated reads; conservative
        /// for the authenticated-poll case where users update PRs
        /// faster than that anyway.</para>
        MaxAge: TimeSpan
    }

    static member Defaults: ResponseCacheLimits =
        { MaxEntries = 512
          MaxAge = TimeSpan.FromHours 1.0 }

/// Internal cache slot: the response Octokit handed us plus its
/// timestamps. Mutable <c>LastAccessUtc</c> so a read can refresh the
/// LRU position without dictionary churn.
type private CacheEntry =
    { Response: CachedResponse.V1
      mutable LastAccessUtc: DateTime
      InsertedUtc: DateTime }

/// In-memory <see cref="IResponseCache"/>. Keys requests by the absolute
/// URL <c>(BaseAddress + Endpoint)</c> — the same string GitHub's CDN
/// uses for its ETag/If-None-Match round-trip. Query parameters are part
/// of the endpoint so requests with different filters cache independently.
///
/// <para>Bounded by <see cref="ResponseCacheLimits"/>: an entry older
/// than <c>MaxAge</c> is treated as a miss and dropped lazily on access;
/// when the entry count exceeds <c>MaxEntries</c> the least-recently-used
/// entries are evicted under a short lock. Thread-safe via
/// <see cref="ConcurrentDictionary"/>; eviction takes a coarse lock
/// (acceptable because eviction is infrequent compared to reads).</para>
type InMemoryResponseCache(limits: ResponseCacheLimits, clock: Func<DateTime>) =
    let store = ConcurrentDictionary<string, CacheEntry>(StringComparer.Ordinal)
    let evictionLock = obj ()

    /// Convenience constructor: defaults + system clock. Production
    /// callers use this.
    new() = InMemoryResponseCache(ResponseCacheLimits.Defaults, Func<DateTime>(fun () -> DateTime.UtcNow))

    /// Canonical cache key for a request. Public for tests that need to
    /// pre-seed or assert on cache contents without driving live HTTP.
    static member Key(request: IRequest) : string =
        if isNull (box request) then
            ""
        else
            let baseUri = request.BaseAddress
            let endpoint = request.Endpoint

            if isNull endpoint then ""
            elif endpoint.IsAbsoluteUri then endpoint.AbsoluteUri
            elif isNull baseUri then endpoint.OriginalString
            else Uri(baseUri, endpoint).AbsoluteUri

    /// Test/diagnostic hook: how many entries are cached. The shell
    /// might surface this in a Settings → Diagnostics row eventually.
    member _.Count = store.Count

    /// Test hook: drop the cache.
    member _.Clear() = store.Clear()

    /// Trim the cache down to <c>MaxEntries</c> by evicting the
    /// least-recently-used entries. The lock keeps eviction atomic
    /// against concurrent inserts — without it two writers could each
    /// evict the same victim and miss other candidates.
    member private _.EvictLru() =
        lock evictionLock (fun () ->
            let excess = store.Count - limits.MaxEntries

            if excess > 0 then
                let victims =
                    store
                    |> Seq.sortBy (fun kvp -> kvp.Value.LastAccessUtc)
                    |> Seq.truncate excess
                    |> Seq.map (fun kvp -> kvp.Key)
                    |> Seq.toArray

                for key in victims do
                    store.TryRemove key |> ignore)

    interface IResponseCache with
        member _.GetAsync(request: IRequest) : Task<CachedResponse.V1> =
            let key = InMemoryResponseCache.Key request

            match store.TryGetValue key with
            | true, entry ->
                let now = clock.Invoke()

                if (now - entry.InsertedUtc) > limits.MaxAge then
                    // Stale by age — drop and treat as a miss. The HTTP
                    // layer will re-fetch and re-cache.
                    store.TryRemove key |> ignore
                    Task.FromResult<CachedResponse.V1>(Unchecked.defaultof<_>)
                else
                    entry.LastAccessUtc <- now
                    Task.FromResult entry.Response
            // Octokit's CachingHttpClient treats null as "cache miss",
            // so returning a null-bearing Task is the correct shape.
            | _ -> Task.FromResult<CachedResponse.V1>(Unchecked.defaultof<_>)

        member this.SetAsync(request: IRequest, cachedResponse: CachedResponse.V1) : Task =
            let key = InMemoryResponseCache.Key request

            if not (String.IsNullOrEmpty key) && not (isNull (box cachedResponse)) then
                let now = clock.Invoke()

                store.[key] <-
                    { Response = cachedResponse
                      LastAccessUtc = now
                      InsertedUtc = now }

                if store.Count > limits.MaxEntries then
                    this.EvictLru()

            Task.CompletedTask
