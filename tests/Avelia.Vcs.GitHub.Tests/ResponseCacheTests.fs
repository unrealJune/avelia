module Avelia.Vcs.GitHub.Tests.ResponseCacheTests

open System
open System.Collections.Generic
open System.Net
open System.Threading
open System.Threading.Tasks
open Xunit
open Avelia.Vcs.GitHub
open Avelia.Vcs.GitHub.Tests.OctokitHttpStub
open Octokit
open Octokit.Caching
open Octokit.Internal

// ----------------------------------------------------------------------------
//  Unit tests for InMemoryResponseCache + an end-to-end through
//  Octokit's CachingHttpClient asserting the ETag round-trip.
//
//  CachingHttpClient.Send:
//    * On a GET with no cached entry: pass through, cache the response.
//    * On a GET with cached entry: attach If-None-Match, if server
//      returns 304 hand back the cached body.
//    * On non-GET: pass through unconditionally (no caching).
// ----------------------------------------------------------------------------

[<Fact>]
let ``Key derives stable absolute URL`` () =
    let req = MutableRequest()
    req.BaseAddressValue <- Uri "https://api.github.com"
    req.EndpointValue <- Uri("/repos/o/r", UriKind.Relative)
    req.MethodValue <- System.Net.Http.HttpMethod.Get
    let key = InMemoryResponseCache.Key(req :> IRequest)
    Assert.Equal("https://api.github.com/repos/o/r", key)

[<Fact>]
let ``Set then Get returns the stored response`` () =
    let cache = InMemoryResponseCache() :> IResponseCache
    let req = MutableRequest()
    req.BaseAddressValue <- Uri "https://api.github.com"
    req.EndpointValue <- Uri("/x", UriKind.Relative)
    req.MethodValue <- System.Net.Http.HttpMethod.Get

    let headers = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>

    let resp =
        CachedResponse.V1("body", headers, freshApiInfo "etag-1", HttpStatusCode.OK, "application/json")

    cache.SetAsync(req :> IRequest, resp).GetAwaiter().GetResult()

    let loaded = cache.GetAsync(req :> IRequest).GetAwaiter().GetResult()
    Assert.NotNull loaded
    Assert.Equal("body" :> obj, loaded.Body)

[<Fact>]
let ``Get on a missing key returns null (cache miss convention)`` () =
    let cache = InMemoryResponseCache() :> IResponseCache
    let req = MutableRequest()
    req.BaseAddressValue <- Uri "https://api.github.com"
    req.EndpointValue <- Uri("/never", UriKind.Relative)
    req.MethodValue <- System.Net.Http.HttpMethod.Get
    let loaded = cache.GetAsync(req :> IRequest).GetAwaiter().GetResult()
    Assert.Null loaded

[<Fact>]
let ``Different URLs cache independently`` () =
    let cache = InMemoryResponseCache()
    let store = cache :> IResponseCache

    let mkReq path =
        let r = MutableRequest()
        r.BaseAddressValue <- Uri "https://api.github.com"
        r.EndpointValue <- Uri(path, UriKind.Relative)
        r.MethodValue <- System.Net.Http.HttpMethod.Get
        r :> IRequest

    let r1 = mkReq "/a"
    let r2 = mkReq "/b"

    let headers = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>

    let v1 =
        CachedResponse.V1("A", headers, freshApiInfo "e-a", HttpStatusCode.OK, "application/json")

    let v2 =
        CachedResponse.V1("B", headers, freshApiInfo "e-b", HttpStatusCode.OK, "application/json")

    store.SetAsync(r1, v1).GetAwaiter().GetResult()
    store.SetAsync(r2, v2).GetAwaiter().GetResult()

    Assert.Equal(2, cache.Count)
    Assert.Equal("A" :> obj, store.GetAsync(r1).GetAwaiter().GetResult().Body)
    Assert.Equal("B" :> obj, store.GetAsync(r2).GetAwaiter().GetResult().Body)

// ----------------------------------------------------------------------------
//  End-to-end via Octokit's CachingHttpClient — assert the
//  If-None-Match round-trip and 304-as-cached behaviour.
// ----------------------------------------------------------------------------

/// Inner stub returning a queued sequence of IResponses while recording
/// the request headers Octokit produced.
type private RecordingInnerHttp(responses: IResponse seq) =
    let queue = Queue(responses)
    let recorded = ResizeArray<IReadOnlyDictionary<string, string>>()
    member _.Recorded = recorded :> IReadOnlyList<_>

    interface IHttpClient with
        member _.Send(req, _ct, _pre) =
            let snap = Dictionary<string, string>(req.Headers)
            recorded.Add(snap :> IReadOnlyDictionary<_, _>)

            if queue.Count = 0 then
                Task.FromException<IResponse>(InvalidOperationException "out of responses")
            else
                Task.FromResult(queue.Dequeue())

        member _.SetRequestTimeout(_t: TimeSpan) = ()
        member _.Dispose() = ()

[<Fact>]
let ``CachingHttpClient round-trip: 200 then 304 serves cached body`` () =
    // First response: 200 with ETag header. CachingHttpClient stores it.
    let first = okWithHeaders HttpStatusCode.OK "payload-1" [ "ETag", "\"abc123\"" ]
    // Second response: 304 Not Modified. CachingHttpClient should
    // ignore this and return the cached body from the first call.
    let second = okJson HttpStatusCode.NotModified ""

    let inner = new RecordingInnerHttp([ first; second ])
    let cache = InMemoryResponseCache() :> IResponseCache
    let caching = new CachingHttpClient(inner, cache) :> IHttpClient

    let mkReq () =
        let r = MutableRequest()
        r.BaseAddressValue <- Uri "https://api.github.com"
        r.EndpointValue <- Uri("/cached", UriKind.Relative)
        r.MethodValue <- System.Net.Http.HttpMethod.Get
        r :> IRequest

    let r1 =
        caching.Send(mkReq (), CancellationToken.None, null).GetAwaiter().GetResult()

    Assert.Equal(HttpStatusCode.OK, r1.StatusCode)
    Assert.Equal("payload-1" :> obj, r1.Body)

    let r2 =
        caching.Send(mkReq (), CancellationToken.None, null).GetAwaiter().GetResult()

    Assert.Equal("payload-1" :> obj, r2.Body)
    Assert.Equal(HttpStatusCode.OK, r2.StatusCode)

    // The recorder confirms the second request carried If-None-Match.
    Assert.True(inner.Recorded.[1].ContainsKey "If-None-Match")
    Assert.Equal("\"abc123\"", inner.Recorded.[1].["If-None-Match"])

[<Fact>]
let ``CachingHttpClient does NOT cache non-GET requests`` () =
    let resp = okWithHeaders HttpStatusCode.OK "created" [ "ETag", "\"post-etag\"" ]
    let inner = new RecordingInnerHttp([ resp ])
    let cache = InMemoryResponseCache()
    let caching = new CachingHttpClient(inner, cache :> IResponseCache) :> IHttpClient

    let req = MutableRequest()
    req.BaseAddressValue <- Uri "https://api.github.com"
    req.EndpointValue <- Uri("/new-thing", UriKind.Relative)
    req.MethodValue <- System.Net.Http.HttpMethod.Post

    let _ =
        caching.Send(req :> IRequest, CancellationToken.None, null).GetAwaiter().GetResult()

    // POST responses aren't stored.
    Assert.Equal(0, cache.Count)
