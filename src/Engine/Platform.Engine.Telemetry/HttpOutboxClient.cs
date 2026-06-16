// Platform.Engine.Telemetry — HttpOutboxClient (S12-G-01, Phase A).
//
// The production IOutboxHttpClient over System.Net.Http.HttpClient (in-box; no NuGet).
// It is modelled on HttpVaultKvClient's posture (§17): an in-box HttpClient, a
// header-only token NEVER logged or echoed, and a hard fail-silent contract — a network
// or HTTP fault becomes a non-delivered ack, never an exception that reaches the run.
//
// Wire shape (Phase A; Phase B is the deployed backend):
//   • batch  : POST {endpoint}/v1/telemetry
//              Content-Type: application/x-ndjson  (body = the EXACT outbox lines joined
//              by '\n' — never re-serialised, so nothing outside the allowlist is sent)
//              Authorization: Bearer <token>
//              Idempotency-Key: <key>
//              any 2xx  => delivered
//   • forget : POST {endpoint}/v1/telemetry/forget
//              Content-Type: application/json  body = {"installId":"<guid>"}
//              Authorization: Bearer <token>
//
// A SHORT per-attempt timeout (~3s) keeps a black-holed endpoint from delaying the run;
// the drain is already gated + fail-silent above this, so a timeout simply ends the
// drain for this run and lets the cross-run back-off take over.

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Platform.Engine.Telemetry;

/// <summary>
/// An <see cref="IOutboxHttpClient"/> backed by <see cref="HttpClient"/> (S12-G-01).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the §17 posture of <c>HttpVaultKvClient</c>: an in-box <see cref="HttpClient"/>
/// (no NuGet), the bearer token carried ONLY on the <c>Authorization</c> request header
/// and never logged/echoed, and a strict fail-silent contract — a transport or HTTP
/// fault is classified as a non-delivered <see cref="DrainAck"/> (or a swallowed forget),
/// never an exception that could disrupt the run.
/// </para>
/// <para>
/// The batch body is the RAW outbox NDJSON lines joined by <c>\n</c> — the bytes the
/// local sink already wrote — so the transport NEVER re-serialises a
/// <see cref="TelemetryEvent"/> and physically cannot widen what is sent beyond the
/// privacy allowlist.
/// </para>
/// </remarks>
public sealed class HttpOutboxClient : IOutboxHttpClient
{
    /// <summary>
    /// The batch ingest path, RELATIVE to the configured endpoint base.  Has no leading
    /// slash so an operator base path (e.g. <c>https://host/api</c>) is preserved when it
    /// is resolved against the (slash-normalised) base — see <see cref="Combine"/>.
    /// </summary>
    public const string BatchPath = "v1/telemetry";

    /// <summary>
    /// The forget path, RELATIVE to the configured endpoint base (no leading slash; an
    /// operator base path is preserved when resolved against the base — see <see cref="Combine"/>).
    /// </summary>
    public const string ForgetPath = "v1/telemetry/forget";

    /// <summary>The NDJSON media type used for the batch body.</summary>
    public const string NdjsonMediaType = "application/x-ndjson";

    /// <summary>The HTTP header carrying the per-batch idempotency key.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>The default short per-attempt timeout (~3s) for a single HTTP send.</summary>
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly TimeSpan _attemptTimeout;

    /// <summary>
    /// Creates a client over <paramref name="httpClient"/> targeting
    /// <paramref name="endpoint"/> with the supplied bearer <paramref name="token"/>.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used for the sends.  Ownership is NOT taken — the caller owns its
    /// lifetime.
    /// </param>
    /// <param name="endpoint">The absolute ingest base URI (e.g. <c>https://t.vouchfx.dev</c>).</param>
    /// <param name="token">The bearer token (carried only on the Authorization header).</param>
    /// <param name="attemptTimeout">
    /// The per-attempt timeout; defaults to <see cref="DefaultAttemptTimeout"/> when null.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public HttpOutboxClient(
        HttpClient httpClient,
        Uri endpoint,
        string token,
        TimeSpan? attemptTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(endpoint);
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;

        // Normalise the base so an operator base path is PRESERVED: BatchPath/ForgetPath are
        // relative (no leading slash) and resolved against this base, so the base MUST end in
        // a '/' for its trailing path segment to survive (RFC 3986 reference resolution drops
        // the last segment of a base that does not end in '/').  Thus `https://host/api` and
        // `https://host/api/` both yield `https://host/api/v1/telemetry`, and a bare-origin
        // `https://host` yields `https://host/v1/telemetry`.
        _endpoint = EnsureTrailingSlash(endpoint);
    }

    /// <inheritdoc />
    public async Task<DrainAck> PostBatchAsync(
        IReadOnlyList<string> ndjsonLines,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ndjsonLines);
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        if (ndjsonLines.Count == 0)
        {
            // Nothing to deliver: treat an empty batch as a successful no-op so the
            // caller's "drop delivered lines" path stays simple.
            return new DrainAck(Delivered: true, AcceptedCount: 0);
        }

        // The body is the RAW outbox lines joined by '\n' — byte-identical to what the
        // local sink wrote; the lines are forwarded verbatim and never re-serialised.
        var body = string.Join('\n', ndjsonLines);

        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(NdjsonMediaType);

        using var request = new HttpRequestMessage(HttpMethod.Post, Combine(BatchPath))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, idempotencyKey);

        return await SendClassifyingAsync(request, ndjsonLines.Count, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ForgetAsync(Guid installId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new ForgetRequest(installId.ToString("d")), s_jsonOptions);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, Combine(ForgetPath))
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        // Fail-silent: a forget that cannot reach the server must not throw out of the
        // `telemetry disable` path — the local install id is deleted regardless.
        await SendClassifyingAsync(request, expectedCount: 0, cancellationToken)
            .ConfigureAwait(false);
    }

    // Sends a request under a bounded per-attempt timeout and classifies the result:
    // any 2xx => delivered; any non-2xx or any transport/timeout fault => not delivered.
    // NEVER throws for an ordinary fault and NEVER includes the token or the response
    // body in any surfaced detail (there is no surfaced detail — the result is a bool).
    private async Task<DrainAck> SendClassifyingAsync(
        HttpRequestMessage request,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        // A linked CTS bounds THIS attempt without mutating the shared HttpClient.Timeout
        // (which would race other in-flight sends on a reused client).  Either the caller's
        // cancellation OR the short attempt timeout ends the send.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(_attemptTimeout);

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? new DrainAck(Delivered: true, AcceptedCount: expectedCount)
                : DrainAck.NotDelivered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER cancelled (not our attempt timeout): honour cooperative
            // cancellation by propagating it, so a cancelled run does not hang here.
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or OperationCanceledException
                or InvalidOperationException
                or System.Net.Sockets.SocketException)
        {
            // Our attempt timeout fired, the host is unreachable, or the send faulted:
            // a transport problem is NOT a defect — classify it as "not delivered" and
            // let the cross-run back-off take over.  No token/body is ever surfaced.
            return DrainAck.NotDelivered;
        }
    }

    // Resolve a FIXED, relative path constant against the slash-normalised endpoint base.
    // The path is a compile-time constant (no author/test data influences it), so this can
    // never be steered to a different host or an absolute override.
    private Uri Combine(string relativePath) => new(_endpoint, relativePath);

    // Returns `uri` unchanged when its path already ends in '/', else a copy whose path has a
    // single trailing '/' appended.  Only the path is touched; a base endpoint carrying a
    // query/fragment is rejected upstream (TelemetryTransportOptions.ParseEndpoint fails
    // closed), so none reaches here.  UriBuilder rebuilds a well-formed Uri.
    private static Uri EnsureTrailingSlash(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith('/'))
        {
            return uri;
        }

        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath + "/" };
        return builder.Uri;
    }

    // The forget request body shape: { "installId": "<guid>" }.
    private sealed record ForgetRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("installId")] string InstallId);
}
