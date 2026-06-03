module Avelia.Vcs.GitHub.Tests.OctokitHttpStub

open System
open System.Collections.Generic
open System.Net
open System.Threading
open System.Threading.Tasks
open Octokit
open Octokit.Internal

// ----------------------------------------------------------------------------
//  Octokit IHttpClient stub
//
//  Octokit's whole pipeline (Connection → ApiConnection → typed clients)
//  funnels into <see cref="IHttpClient.Send"/>. Octokit's concrete
//  <c>Response</c> + <c>Request</c> types are <c>internal</c>, so the
//  stub implements <see cref="IResponse"/> + <see cref="IRequest"/>
//  directly — public interfaces, no reflection needed.
// ----------------------------------------------------------------------------

/// Concrete <see cref="IResponse"/> for tests. Carries the body Octokit
/// will hand to its deserializer, the status code, and the headers
/// (used by <c>ApiInfoParser</c> internally to populate <c>ApiInfo</c>
/// — but we expose an explicit override too).
type TestResponse
    (
        statusCode: HttpStatusCode,
        body: obj,
        headers: IReadOnlyDictionary<string, string>,
        contentType: string,
        apiInfo: ApiInfo
    ) =
    interface IResponse with
        member _.Body = body
        member _.Headers = headers
        member _.ApiInfo = apiInfo
        member _.StatusCode = statusCode
        member _.ContentType = contentType

/// Mutable IRequest impl that lets tests set every field individually.
/// Octokit's own <c>Request</c> class is internal; this is the public
/// stand-in for tests that need to build requests bottom-up (e.g.
/// cache-key derivation, ETag round-trip).
type MutableRequest() =
    let headers = Dictionary<string, string>()
    let parameters = Dictionary<string, string>()
    let mutable body: obj | null = null
    let mutable method' = System.Net.Http.HttpMethod.Get
    let mutable baseAddress: Uri | null = null
    let mutable endpoint: Uri | null = null
    let mutable timeout = TimeSpan.Zero
    let mutable contentType = ""

    member _.BodyValue
        with get () = body
        and set v = body <- v

    member _.MethodValue
        with get () = method'
        and set v = method' <- v

    member _.BaseAddressValue
        with get () = baseAddress
        and set v = baseAddress <- v

    member _.EndpointValue
        with get () = endpoint
        and set v = endpoint <- v

    member _.TimeoutValue
        with get () = timeout
        and set v = timeout <- v

    member _.ContentTypeValue
        with get () = contentType
        and set v = contentType <- v

    interface IRequest with
        member _.Body
            with get () = body
            and set v = body <- v

        member _.Headers = headers
        member _.Method = method'
        member _.Parameters = parameters
        member _.BaseAddress = baseAddress
        member _.Endpoint = endpoint
        member _.Timeout = timeout
        member _.ContentType = contentType

/// Build a stand-in <see cref="ApiInfo"/> exposing only the headers tests
/// care about (typically just ETag). <see cref="Octokit.ApiInfo"/> has a
/// public ctor, so this is straightforward.
let freshApiInfo (etag: string) : ApiInfo =
    ApiInfo(
        Dictionary<string, Uri>(),
        ResizeArray<string>(),
        ResizeArray<string>(),
        etag,
        RateLimit(Dictionary<string, string>())
    )

/// Build an ApiInfo from a header bag. Uses the public ApiInfo ctor;
/// keeps the test stub honest about what response headers downstream
/// code reads.
let private buildApiInfo (headers: IReadOnlyDictionary<string, string>) : ApiInfo =
    let links = Dictionary<string, Uri>()
    let oauthScopes = ResizeArray<string>()
    let acceptedOauthScopes = ResizeArray<string>()

    let pick (key: string) : string =
        match headers.TryGetValue key with
        | true, v -> v
        | _ -> ""

    let etag = pick "ETag"

    // Build a header dictionary keyed in the shape RateLimit's
    // constructor expects (X-RateLimit-* keys, case-insensitive).
    let rateLimitHeaders = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

    for kvp in headers do
        rateLimitHeaders.[kvp.Key] <- kvp.Value

    let rateLimit = RateLimit rateLimitHeaders
    ApiInfo(links, oauthScopes, acceptedOauthScopes, etag, rateLimit, TimeSpan.Zero)

/// Record of a single HTTP request Octokit made. Tests inspect these to
/// assert that the right endpoint was hit with the right body.
type RecordedRequest =
    {
        Method: System.Net.Http.HttpMethod
        Url: string
        /// Body Octokit produced. Either a JSON string (own DTOs), a
        /// <see cref="System.Net.Http.HttpContent"/> (OAuth form bodies),
        /// or null (GETs).
        Body: obj
        Headers: IReadOnlyDictionary<string, string>
    }

/// JSON helper — builds an <see cref="IResponse"/> carrying a
/// JSON-string body. Mirrors what HttpClientAdapter produces from a
/// real GitHub response.
let okJson (status: HttpStatusCode) (body: string) : IResponse =
    let headers = Dictionary<string, string>() :> IReadOnlyDictionary<_, _>
    TestResponse(status, (body :> obj), headers, "application/json", buildApiInfo headers) :> IResponse

let ok (body: string) : IResponse = okJson HttpStatusCode.OK body

let okWithHeaders (status: HttpStatusCode) (body: string) (headers: (string * string) seq) : IResponse =
    let dict = Dictionary<string, string>()

    for (k, v) in headers do
        dict.[k] <- v

    let ro = dict :> IReadOnlyDictionary<_, _>
    TestResponse(status, (body :> obj), ro, "application/json", buildApiInfo ro) :> IResponse

/// Scripted IHttpClient. Each call dequeues the next response. The test
/// can then read <c>Recorded</c> to assert on the URLs / bodies Octokit
/// produced for the calls under test.
type ScriptedHttpClient(responses: IResponse seq) =
    let queue = Queue(responses)
    let recorded = ResizeArray<RecordedRequest>()

    member _.Recorded = recorded :> IReadOnlyList<_>

    /// Append more scripted responses after construction.
    member _.Enqueue(resp: IResponse) = queue.Enqueue resp

    interface IHttpClient with
        member _.Send(request: IRequest, _ct: CancellationToken, _preprocess: Func<obj, obj>) =
            let url =
                match request.Endpoint with
                | null -> ""
                | ep when ep.IsAbsoluteUri -> ep.AbsoluteUri
                | ep ->
                    match request.BaseAddress with
                    | null -> ep.OriginalString
                    | baseUri -> Uri(baseUri, ep).AbsoluteUri

            recorded.Add
                { Method = request.Method
                  Url = url
                  Body = request.Body
                  Headers = request.Headers :> IReadOnlyDictionary<_, _> }

            if queue.Count = 0 then
                Task.FromException<IResponse>(
                    InvalidOperationException(
                        sprintf "ScriptedHttpClient ran out of responses; %d already served" recorded.Count
                    )
                )
            else
                Task.FromResult(queue.Dequeue())

        member _.SetRequestTimeout(_t: TimeSpan) = ()
        member _.Dispose() = ()

/// Read a form-urlencoded request body as a string. Octokit posts
/// device-flow requests with <see cref="System.Net.Http.FormUrlEncodedContent"/>;
/// reading it lets the test assert on the <c>client_id</c> /
/// <c>device_code</c> / <c>grant_type</c> parameters Octokit produced.
let readFormBodyAsync (body: obj | null) : Task<string> =
    task {
        match body with
        | :? System.Net.Http.HttpContent as content ->
            let! str = content.ReadAsStringAsync()
            return str
        | :? string as s -> return s
        | null -> return ""
        | other ->
            return
                match other.ToString() with
                | null -> ""
                | s -> s
    }
