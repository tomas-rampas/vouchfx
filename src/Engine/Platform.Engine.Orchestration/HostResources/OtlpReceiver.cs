// Platform.Engine.Orchestration — OtlpReceiver (Phase C, §5).
//
// A minimal host-owned ephemeral HTTP listener backing the trace-expect.otlp family: it accepts
// OTLP/HTTP JSON trace-export requests from a REAL instrumented OpenTelemetry SDK running inside
// the system under test, parses the exported spans, and buffers them for a later
// trace-expect.otlp assertion step to scan.
//
// Kestrel (not HttpListener) by deliberate choice — EXACTLY the WebhookListener rationale
// (S07-F-01a): binds 0.0.0.0:0 (ephemeral, ALL interfaces) WITHOUT a Windows admin URL-ACL, so a
// containerised SUT can reach the host via host.docker.internal (a non-loopback host IP) with no
// elevated `netsh http add urlacl` reservation.
//
// Lifecycle (§5): the receiver + its buffer live in the Default ALC, owned by the
// topology/runner.  The emitted script (collectible ALC) never sees this object — it only reads
// captures through ScriptGlobalVariables.Traces.  IAsyncDisposable stops it cleanly.
//
// ── DECISION: OTLP/HTTP JSON ENCODING ONLY IN v1 (documented limitation) ───────────────────────
// The OTLP/HTTP protocol defines two wire encodings: protobuf (application/x-protobuf, the
// production default for most SDKs) and JSON (application/json). v1 of this receiver accepts
// ONLY the JSON encoding — a request with any other Content-Type (in particular
// application/x-protobuf) is rejected 415 with a body naming the limitation explicitly, rather
// than silently dropping spans or crashing on an unparsable body. This is a deliberate MVP
// scope cut, not an oversight: implementing the protobuf wire format needs a schema/codegen
// dependency (e.g. Google.Protobuf + the OTLP .proto definitions) this engine does not otherwise
// carry, whereas JSON parses with the BCL's System.Text.Json the engine already depends on
// everywhere. A test author who wants trace-expect.otlp coverage configures their OTel exporter
// with OTEL_EXPORTER_OTLP_PROTOCOL=http/json (every major OTel SDK supports this).
//
// ── DECISION: NO UNGUESSABLE-TOKEN PATH GATE (deliberate divergence from WebhookListener) ─────
// WebhookListener gates every request behind a random per-listener path token because an
// on-network onlooker who finds the (0.0.0.0) port could otherwise forge a captured webhook
// callback and produce a false Pass. The OTLP receiver does NOT add an equivalent token segment
// to its path, for a concrete compatibility reason: the intended configuration surface is the
// STANDARD OpenTelemetry environment variable OTEL_EXPORTER_OTLP_ENDPOINT (or
// OTEL_EXPORTER_OTLP_TRACES_ENDPOINT), which every OTel SDK treats as a plain base URL and to
// which the SDK itself appends the fixed path "/v1/traces". Splicing an unguessable path segment
// into that base URL would either break the SDK's own path-join logic or require the test author
// to hand-configure a non-standard endpoint shape, defeating this family's core value
// proposition — "point a REAL, UNMODIFIED OTel SDK at {svc::<receiver>} and it just works".
// The narrowed blast radius that makes this an acceptable trade-off: (1) EPHEMERAL PORT +
// PER-SCENARIO LIFETIME (identical to WebhookListener) bounds the exposure window; (2) BOUNDED
// BODY (4 MiB) + BOUNDED RING BUFFER (TraceCaptureBuffer.MaxBufferedSpans) bound the memory a
// flood of forged exports can consume; (3) MOST IMPORTANTLY, a trace-expect.otlp assertion
// requires the observed span to carry a SPECIFIC 128-bit trace id extracted from THIS scenario's
// own traceparent (see TraceExpectOtlpProvider) — an off-network onlooker who cannot observe
// that trace id cannot forge a matching span, so a forged export can pollute the buffer but
// cannot manufacture a false Pass. This trade-off is documented here precisely so a future
// reviewer does not "fix" the asymmetry with WebhookListener without re-reading this rationale.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Engine.Abstractions.Traces;

namespace Platform.Engine.Orchestration.HostResources;

/// <summary>
/// A host-owned ephemeral HTTP listener (Kestrel) that accepts OTLP/HTTP JSON trace-export
/// requests, parses their spans into a <see cref="TraceCaptureBuffer"/>, and responds per the
/// OTLP/HTTP specification — backing the <c>trace-expect.otlp</c> family (Phase C).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Binding:</strong> the receiver binds <c>http://0.0.0.0:0</c> — port 0 lets the OS
/// assign a free ephemeral port, and <c>0.0.0.0</c> (all interfaces) makes the receiver
/// reachable from a containerised system under test via <c>host.docker.internal</c> without a
/// Windows admin URL-ACL reservation (identical rationale to <see cref="WebhookListener"/>).
/// The bound base URL (no trailing slash — see <see cref="Url"/>) is exposed after
/// <see cref="StartAsync"/> completes.
/// </para>
/// <para>
/// <strong>Endpoint:</strong> only <c>POST /v1/traces</c> with <c>Content-Type: application/json</c>
/// (the OTLP/HTTP JSON encoding) is accepted. Any other path or method is rejected <c>404</c>/
/// <c>405</c>; a non-JSON Content-Type (in particular <c>application/x-protobuf</c>) is rejected
/// <c>415</c> with a body explaining the v1 JSON-only limitation (see the file header). A
/// successful export responds <c>200</c> with the OTLP/HTTP <c>ExportTraceServiceResponse</c>
/// shape <c>{"partialSuccess":{}}</c> (an empty <c>partialSuccess</c> signals no rejected spans).
/// </para>
/// <para>
/// <strong>Capture:</strong> the request body is bounded to <see cref="MaxRequestBodyBytes"/>
/// (4 MiB) by Kestrel — an oversize body is rejected <c>413</c> before it is read. The body is
/// parsed as OTLP/HTTP JSON (<c>resourceSpans[].scopeSpans[].spans[]</c>); a malformed body is
/// rejected <c>400</c> and captures nothing (never a crash). Each parsed span becomes a
/// <see cref="CapturedSpan"/> appended to the supplied <see cref="TraceCaptureBuffer"/> (itself
/// bounded — see <see cref="TraceCaptureBuffer.MaxBufferedSpans"/>, a RING buffer unlike
/// <see cref="WebhookCaptureBuffer"/>'s drop-the-newest policy — see that type's remarks for
/// why).
/// </para>
/// <para>
/// <strong>Memory model (§5):</strong> the receiver and its buffer live in the Default
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, owned by the topology/runner. The
/// emitted script never references this type; it reads captures only by-reference through
/// <c>ScriptGlobalVariables.Traces</c>. <see cref="DisposeAsync"/> stops the Kestrel host
/// cleanly and releases the port.
/// </para>
/// </remarks>
public sealed class OtlpReceiver : IAsyncDisposable
{
    /// <summary>
    /// The number of bytes an OTLP export request body may carry before Kestrel rejects it 413.
    /// 4 MiB (the value the task calls out explicitly) is generous headroom for a batch of spans
    /// from a single business transaction while bounding the memory a single malicious/large
    /// request can force the host to read.
    /// </summary>
    private const long MaxRequestBodyBytes = 4 * 1024 * 1024;

    /// <summary>Cap concurrent connections so a flood cannot exhaust host sockets/threads.</summary>
    private const long MaxConcurrentConnections = 100;

    /// <summary>The one path this receiver accepts, per the OTLP/HTTP specification.</summary>
    private const string TracesPath = "/v1/traces";

    private readonly WebApplication _app;
    private bool _disposed;

    private OtlpReceiver(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>
    /// Gets the bound base URL of the receiver (with the OS-assigned ephemeral port), e.g.
    /// <c>http://127.0.0.1:54321</c>. Available only after <see cref="StartAsync"/> returns.
    /// Deliberately carries NO trailing slash and NO path segment (unlike
    /// <see cref="WebhookListener.Url"/>'s token path) — an OTel SDK's
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> convention appends <c>/v1/traces</c> to this value
    /// itself, so a trailing slash here would produce a double slash on the wire.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Starts an ephemeral Kestrel receiver bound to <c>http://0.0.0.0:0</c> that accepts OTLP/
    /// HTTP JSON trace exports at <c>POST /v1/traces</c>, buffers their spans into
    /// <paramref name="buffer"/>, and responds per the OTLP/HTTP specification.
    /// </summary>
    /// <param name="buffer">
    /// The per-receiver capture buffer parsed spans are appended to; must not be
    /// <see langword="null"/>. Owned by the caller (the runner/topology) in the Default ALC.
    /// </param>
    /// <param name="cancellationToken">Propagated to the underlying host start.</param>
    /// <returns>
    /// A started <see cref="OtlpReceiver"/> whose <see cref="Url"/> is populated with the
    /// OS-assigned port.
    /// </returns>
    /// <exception cref="System.ArgumentNullException">
    /// Thrown when <paramref name="buffer"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when the started host reports no bound address.
    /// </exception>
    public static async Task<OtlpReceiver> StartAsync(
        TraceCaptureBuffer buffer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        // Slim builder: no MVC, no static files, no config providers we don't need — just the
        // minimal Kestrel host required to accept and parse OTLP export requests.
        var builder = WebApplication.CreateSlimBuilder();

        // Quieten the host: this is an internal capture sink, not an application server.
        builder.Logging.ClearProviders();

        // Bind 0.0.0.0:0 — all interfaces, OS-assigned ephemeral port (no Windows URL-ACL) —
        // and bound the request body (4 MiB) and concurrent connections (100) so a flood of
        // large/forged exports cannot exhaust host memory. Kestrel returns 413 on an oversize
        // body BEFORE the body is read.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, 0);
            options.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            options.Limits.MaxConcurrentConnections = MaxConcurrentConnections;
        });

        var app = builder.Build();

        // Terminal middleware: route on path/method/content-type, parse OTLP JSON, buffer
        // spans, respond per the OTLP/HTTP spec.
        app.Run(async context => await HandleAsync(context, buffer).ConfigureAwait(false));

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var url = ResolveBoundUrl(app);
        return new OtlpReceiver(app, url);
    }

    /// <summary>
    /// Routes a single inbound request: only <c>POST /v1/traces</c> with a JSON Content-Type is
    /// accepted; every other shape is rejected with the appropriate status code and no spans are
    /// buffered. A syntactically valid JSON export request has its spans parsed and appended to
    /// <paramref name="buffer"/>, then the OTLP/HTTP success response is written.
    /// </summary>
    private static async Task HandleAsync(HttpContext context, TraceCaptureBuffer buffer)
    {
        var request = context.Request;

        // ── Path/method gate ────────────────────────────────────────────────────────────────
        if (!string.Equals(request.Path.Value, TracesPath, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpMethods.IsPost(request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        // ── Encoding gate: OTLP/HTTP JSON only in v1 (see the file header) ─────────────────
        var contentType = request.ContentType ?? string.Empty;
        if (!contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"trace-expect.otlp v1 accepts OTLP/HTTP JSON encoding only " +
                "(Content-Type: application/json); protobuf (application/x-protobuf) is not " +
                "supported. Configure your OTel exporter with " +
                "OTEL_EXPORTER_OTLP_PROTOCOL=http/json.\"}",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // ── Body read (bounded + guarded) ──────────────────────────────────────────────────
        // Kestrel enforces MaxRequestBodySize: reading the body throws BadHttpRequestException
        // (HTTP 413) when the client sends more than the limit.
        string body;
        try
        {
            using var reader = new StreamReader(
                request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);
            body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = ex.StatusCode;
            }

            return;
        }
        catch (OperationCanceledException)
        {
            // Client aborted: nothing to capture, nothing to send.
            return;
        }

        // ── Parse + buffer ──────────────────────────────────────────────────────────────────
        var capturedAt = DateTimeOffset.UtcNow;
        try
        {
            foreach (var span in ParseSpans(body, capturedAt))
            {
                buffer.Append(span);
            }
        }
        catch (JsonException)
        {
            // Malformed JSON: reject 400, capture nothing (never a crash).
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"malformed OTLP/HTTP JSON export request body\"}",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // ── Success: OTLP/HTTP ExportTraceServiceResponse shape ────────────────────────────
        // An empty partialSuccess signals the collector accepted every span with no rejections.
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            "{\"partialSuccess\":{}}", context.RequestAborted).ConfigureAwait(false);
    }

    // ── OTLP/HTTP JSON parsing ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses an OTLP/HTTP JSON export request body's
    /// <c>resourceSpans[].scopeSpans[].spans[]</c> shape into <see cref="CapturedSpan"/>
    /// records. Tolerant of missing/absent arrays at any level (yields nothing for that
    /// branch); throws <see cref="JsonException"/> only when the body is not valid JSON at all.
    /// </summary>
    private static IEnumerable<CapturedSpan> ParseSpans(string body, DateTimeOffset capturedAt)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("resourceSpans", out var resourceSpans)
            || resourceSpans.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var resourceSpan in resourceSpans.EnumerateArray())
        {
            var serviceName = ReadServiceName(resourceSpan);

            if (!resourceSpan.TryGetProperty("scopeSpans", out var scopeSpans)
                || scopeSpans.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var scopeSpan in scopeSpans.EnumerateArray())
            {
                if (!scopeSpan.TryGetProperty("spans", out var spans)
                    || spans.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var span in spans.EnumerateArray())
                {
                    yield return ParseSpan(span, serviceName, capturedAt);
                }
            }
        }
    }

    /// <summary>
    /// Reads the exporting resource's <c>service.name</c> attribute, or an empty string when
    /// absent.
    /// </summary>
    private static string ReadServiceName(JsonElement resourceSpan)
    {
        if (!resourceSpan.TryGetProperty("resource", out var resource)
            || resource.ValueKind != JsonValueKind.Object
            || !resource.TryGetProperty("attributes", out var attributes)
            || attributes.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            if (TryGetAttributeKeyValue(attribute, out var key, out var value)
                && string.Equals(key, "service.name", StringComparison.Ordinal))
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Parses a single OTLP JSON span object into a <see cref="CapturedSpan"/>. Trace/span/
    /// parent-span ids are stored lower-cased exactly as received (the OTLP/HTTP JSON mapping
    /// encodes them as lower-case hex strings, matching the W3C traceparent hex segments — see
    /// <see cref="Platform.Steps.TraceExpect.Otlp.TraceExpectOtlpProvider"/> for the
    /// traceparent-derived trace-id extraction this is designed to match against).
    /// </summary>
    private static CapturedSpan ParseSpan(JsonElement span, string serviceName, DateTimeOffset capturedAt)
    {
        var traceId = GetString(span, "traceId").ToLowerInvariant();
        var spanId = GetString(span, "spanId").ToLowerInvariant();
        var parentSpanId = GetString(span, "parentSpanId").ToLowerInvariant();
        var name = GetString(span, "name");

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (span.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var attribute in attrs.EnumerateArray())
            {
                if (TryGetAttributeKeyValue(attribute, out var key, out var value))
                {
                    attributes[key] = value;
                }
            }
        }

        var statusCode = string.Empty;
        if (span.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("code", out var code))
        {
            statusCode = code.ValueKind switch
            {
                JsonValueKind.String => code.GetString() ?? string.Empty,
                JsonValueKind.Number => code.GetRawText(),
                _ => string.Empty,
            };
        }

        return new CapturedSpan(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            Name: name,
            ServiceName: serviceName,
            Attributes: attributes,
            StatusCode: statusCode,
            At: capturedAt);
    }

    private static string GetString(JsonElement obj, string propertyName) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Reads one OTLP <c>{"key":"...","value":{...}}</c> attribute entry, stringifying whichever
    /// <c>value</c> variant (<c>stringValue</c>/<c>intValue</c>/<c>boolValue</c>/
    /// <c>doubleValue</c>) is present.  Returns <see langword="false"/> only when the entry
    /// itself is not a well-formed <c>{key, value}</c> object (a missing/unrecognised value
    /// variant still yields <see langword="true"/> with an empty string, so one malformed
    /// attribute never aborts the whole span).
    /// </summary>
    private static bool TryGetAttributeKeyValue(JsonElement attribute, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        if (attribute.ValueKind != JsonValueKind.Object
            || !attribute.TryGetProperty("key", out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        key = keyElement.GetString() ?? string.Empty;

        if (attribute.TryGetProperty("value", out var valueElement)
            && valueElement.ValueKind == JsonValueKind.Object)
        {
            value = StringifyAttributeValue(valueElement);
        }

        return true;
    }

    /// <summary>
    /// Stringifies an OTLP <c>AnyValue</c> JSON object, invariant-culture, regardless of which
    /// wire variant is present. Unrecognised/complex variants (<c>arrayValue</c>,
    /// <c>kvlistValue</c>, <c>bytesValue</c>) fall back to their raw JSON text — sufficient for
    /// the equality matching <c>trace-expect.otlp</c> performs; no v1 caller needs a structured
    /// read-back of a nested value.
    /// </summary>
    private static string StringifyAttributeValue(JsonElement valueElement)
    {
        if (valueElement.TryGetProperty("stringValue", out var stringValue))
        {
            return stringValue.ValueKind == JsonValueKind.String
                ? stringValue.GetString() ?? string.Empty
                : stringValue.GetRawText();
        }

        if (valueElement.TryGetProperty("intValue", out var intValue))
        {
            // Per the proto3/OTLP JSON mapping, a 64-bit int is typically encoded as a JSON
            // string (JSON numbers cannot safely represent the full int64 range); tolerate a
            // bare JSON number too.
            return intValue.ValueKind switch
            {
                JsonValueKind.String => intValue.GetString() ?? string.Empty,
                JsonValueKind.Number => intValue.GetRawText(),
                _ => string.Empty,
            };
        }

        if (valueElement.TryGetProperty("boolValue", out var boolValue))
        {
            return boolValue.ValueKind switch
            {
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty,
            };
        }

        if (valueElement.TryGetProperty("doubleValue", out var doubleValue))
        {
            return doubleValue.ValueKind switch
            {
                JsonValueKind.Number => doubleValue.GetRawText(),
                JsonValueKind.String => doubleValue.GetString() ?? string.Empty,
                _ => string.Empty,
            };
        }

        // arrayValue / kvlistValue / bytesValue / unknown: fall back to the raw JSON text.
        return valueElement.GetRawText();
    }

    /// <summary>
    /// Reads the host's bound address (from <see cref="IServerAddressesFeature"/>) and rewrites
    /// the wildcard host to loopback so in-process callers can dial it directly.
    /// </summary>
    private static string ResolveBoundUrl(WebApplication app)
    {
        var addressesFeature = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>();

        var bound = addressesFeature?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The OTLP receiver started but reported no bound address.");

        // Kestrel reports the wildcard bind as e.g. "http://0.0.0.0:54321" or
        // "http://[::]:54321".  Rewrite the host part to 127.0.0.1 so in-process HttpClient
        // calls (and the staged svc:: URL) target a dialable loopback address; the OS-assigned
        // PORT is the load-bearing part and is preserved.  A containerised SUT reaches the same
        // port via host.docker.internal:<port> (mirrors WebhookListener / RewriteHostForContainer
        // in ScenarioRunner).
        if (Uri.TryCreate(bound, UriKind.Absolute, out var uri))
        {
            var builder = new UriBuilder(uri) { Host = "127.0.0.1" };
            // No trailing slash: the base URL an OTel SDK is handed via
            // OTEL_EXPORTER_OTLP_ENDPOINT must join cleanly with the SDK's own "/v1/traces"
            // suffix (see the Url property remarks).
            return builder.Uri.GetLeftPart(UriPartial.Authority);
        }

        return bound.TrimEnd('/');
    }

    /// <summary>
    /// Stops the Kestrel host and releases the bound port. Idempotent — safe to call multiple
    /// times.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort stop: disposal must not throw on teardown.
        }

        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
