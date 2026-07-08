// Tests for Phase C (Orchestration layer): the host-owned ephemeral OTLP/HTTP receiver, its
// capture buffer, and the concrete capture accessor. Mirrors WebhookListenerTests.cs exactly
// (S07-F-01a precedent) for the equivalent trace-expect.otlp host resource.
//
// Covers (all in-process, NO Docker):
//   • TraceCaptureBuffer: concurrent appends + snapshot read; RING-BUFFER eviction beyond the
//     cap (oldest-out — the OPPOSITE policy from WebhookCaptureBuffer's drop-newest); snapshot
//     is an isolated copy.
//   • OtlpReceiver: start it; POST a valid OTLP/HTTP JSON export from the same process
//     (HttpClient); assert the buffer captured the span's fields (including every AnyValue
//     variant: stringValue/intValue/boolValue/doubleValue) and the resource's service.name;
//     malformed JSON -> 400, not captured; non-JSON Content-Type -> 415, not captured; wrong
//     path/method -> 404/405, not captured; DisposeAsync stops it.
//   • TraceCaptureAccessor: GetCaptured returns a registered receiver's spans; empty for an
//     unknown name; empty over an empty buffer map.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Platform.Engine.Abstractions.Traces;
using Platform.Engine.Orchestration.HostResources;
using Xunit;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Non-docker, in-process unit tests for <see cref="TraceCaptureBuffer"/>,
/// <see cref="OtlpReceiver"/>, and <see cref="TraceCaptureAccessor"/> (Phase C).
/// </summary>
public sealed class OtlpReceiverTests
{
    // ── TraceCaptureBuffer ────────────────────────────────────────────────────

    [Fact]
    public void Buffer_AppendThenSnapshot_ReturnsAppendedSpans()
    {
        var buffer = new TraceCaptureBuffer();

        buffer.Append(MakeSpan("t1", "s1", "a"));
        buffer.Append(MakeSpan("t1", "s2", "b"));

        var snapshot = buffer.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("a", snapshot[0].Name);
        Assert.Equal("b", snapshot[1].Name);
    }

    [Fact]
    public void Buffer_Append_NullSpan_Throws()
    {
        var buffer = new TraceCaptureBuffer();

        Assert.Throws<ArgumentNullException>(() => buffer.Append(null!));
    }

    [Fact]
    public async Task Buffer_ConcurrentAppends_AllCaptured()
    {
        var buffer = new TraceCaptureBuffer();
        const int writers = 16;
        // Keep the total below MaxBufferedSpans so this test exercises CONCURRENCY (every
        // append accepted), not the ring-eviction path (covered separately).
        const int perWriter = 50;

        var tasks = new Task[writers];
        for (int w = 0; w < writers; w++)
        {
            int writerId = w;
            tasks[w] = Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    buffer.Append(MakeSpan("t1", $"w{writerId}-{i}", "span"));
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(writers * perWriter, buffer.Count);
        Assert.Equal(writers * perWriter, buffer.Snapshot().Count);
    }

    [Fact]
    public void Buffer_Snapshot_IsIsolatedCopy()
    {
        var buffer = new TraceCaptureBuffer();
        buffer.Append(MakeSpan("t1", "s1", "a"));

        var snapshot = buffer.Snapshot();
        buffer.Append(MakeSpan("t1", "s2", "b"));

        // The earlier snapshot must not see the later append.
        Assert.Single(snapshot);
    }

    [Fact]
    public void Buffer_Append_BeyondCap_EvictsOldestAndKeepsNewest()
    {
        var buffer = new TraceCaptureBuffer();
        const int cap = TraceCaptureBuffer.MaxBufferedSpans;

        // Fill exactly to the cap.
        for (int i = 0; i < cap; i++)
        {
            buffer.Append(MakeSpan("t1", $"early-{i}", "span"));
        }

        Assert.Equal(cap, buffer.Count);

        // 50 more appends: RING BUFFER — the OLDEST 50 are evicted, not the newest (the
        // OPPOSITE of WebhookCaptureBuffer's drop-the-new policy — see TraceCaptureBuffer's
        // remarks for the rationale: real OTel SDKs export continuously for the whole SUT
        // process lifetime, so the MOST RECENT spans stay relevant to the current scenario).
        for (int i = 0; i < 50; i++)
        {
            buffer.Append(MakeSpan("t1", $"late-{i}", "span"));
        }

        var snapshot = buffer.Snapshot();

        // Count stays capped, and the LATE (newest) entries are the ones retained.
        Assert.Equal(cap, snapshot.Count);
        Assert.DoesNotContain(snapshot, s => s.SpanId.StartsWith("early-0", StringComparison.Ordinal));
        Assert.Contains(snapshot, s => s.SpanId == "late-49");
        // The last surviving "early" span is early-50 (the first 50 were evicted).
        Assert.Equal("early-50", snapshot[0].SpanId);

        // TotalReceived is MONOTONIC — it counts every append ever made, including the 50
        // evicted ones — so TotalReceived - Count is exactly the number evicted so far. This
        // is the diagnostic trace-expect.otlp's emitted helper surfaces as "evicted" on a Fail
        // observation.
        Assert.Equal(cap + 50, buffer.TotalReceived);
        Assert.Equal(50, buffer.TotalReceived - snapshot.Count);
    }

    [Fact]
    public void Buffer_TotalReceived_TracksAppendsBeforeAnyEviction()
    {
        var buffer = new TraceCaptureBuffer();

        Assert.Equal(0, buffer.TotalReceived);

        buffer.Append(MakeSpan("t1", "s1", "a"));
        buffer.Append(MakeSpan("t1", "s2", "b"));

        // Below the cap: TotalReceived equals the retained Count exactly (nothing evicted yet).
        Assert.Equal(2, buffer.TotalReceived);
        Assert.Equal(buffer.Count, buffer.TotalReceived);
    }

    // ── OtlpReceiver ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Receiver_ValidJsonExport_CapturesSpanAndRespondsPartialSuccess()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        Assert.StartsWith("http://", receiver.Url, StringComparison.Ordinal);
        // No trailing slash and no path segment (unlike WebhookListener's token path) — an
        // OTel SDK's OTEL_EXPORTER_OTLP_ENDPOINT convention appends "/v1/traces" itself.
        Assert.False(receiver.Url.EndsWith('/'));

        using var client = new HttpClient();
        var payload = BuildExportPayload(
            traceId: "4bf92f3577b34da6a3ce929d0e0e4736",
            spanId: "00f067aa0ba902b7",
            parentSpanId: "",
            name: "POST /orders",
            serviceName: "orders-api",
            extraAttributes:
                "{\"key\":\"http.status_code\",\"value\":{\"intValue\":\"201\"}}," +
                "{\"key\":\"retry\",\"value\":{\"boolValue\":true}}," +
                "{\"key\":\"duration_ms\",\"value\":{\"doubleValue\":12.5}}",
            statusCode: "STATUS_CODE_OK");

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(receiver.Url + "/v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("partialSuccess", body, StringComparison.Ordinal);

        var captured = buffer.Snapshot();
        Assert.Single(captured);

        var span = captured[0];
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", span.TraceId);
        Assert.Equal("00f067aa0ba902b7", span.SpanId);
        Assert.Equal(string.Empty, span.ParentSpanId);
        Assert.Equal("POST /orders", span.Name);
        Assert.Equal("orders-api", span.ServiceName);
        Assert.Equal("STATUS_CODE_OK", span.StatusCode);
        Assert.Equal("201", span.Attributes["http.status_code"]);
        Assert.Equal("true", span.Attributes["retry"]);
        Assert.Equal("12.5", span.Attributes["duration_ms"]);
        Assert.True(span.At <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Receiver_TraceIdAndSpanId_AreLowerCased()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        var payload = BuildExportPayload(
            traceId: "4BF92F3577B34DA6A3CE929D0E0E4736",
            spanId: "00F067AA0BA902B7",
            parentSpanId: "",
            name: "op",
            serviceName: "svc",
            extraAttributes: string.Empty,
            statusCode: string.Empty);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        await client.PostAsync(receiver.Url + "/v1/traces", content);

        var span = Assert.Single(buffer.Snapshot());
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", span.TraceId);
        Assert.Equal("00f067aa0ba902b7", span.SpanId);
    }

    [Fact]
    public async Task Receiver_MultipleSpansAcrossResourceSpansAndScopeSpans_AllCaptured()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        const string payload = """
            {
              "resourceSpans": [
                {
                  "resource": { "attributes": [{"key":"service.name","value":{"stringValue":"svc-a"}}] },
                  "scopeSpans": [
                    { "spans": [
                        {"traceId":"aa000000000000000000000000000001","spanId":"aa00000000000001","parentSpanId":"","name":"span-1"},
                        {"traceId":"aa000000000000000000000000000001","spanId":"aa00000000000002","parentSpanId":"aa00000000000001","name":"span-2"}
                    ] }
                  ]
                },
                {
                  "resource": { "attributes": [{"key":"service.name","value":{"stringValue":"svc-b"}}] },
                  "scopeSpans": [
                    { "spans": [
                        {"traceId":"bb000000000000000000000000000002","spanId":"bb00000000000001","parentSpanId":"","name":"span-3"}
                    ] }
                  ]
                }
              ]
            }
            """;

        using var client = new HttpClient();
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(receiver.Url + "/v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var captured = buffer.Snapshot();
        Assert.Equal(3, captured.Count);
        Assert.Equal("svc-a", captured[0].ServiceName);
        Assert.Equal("svc-a", captured[1].ServiceName);
        Assert.Equal("svc-b", captured[2].ServiceName);
    }

    [Fact]
    public async Task Receiver_MalformedJson_Returns400AndCapturesNothing()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        using var content = new StringContent("{not valid json", Encoding.UTF8, "application/json");
        var response = await client.PostAsync(receiver.Url + "/v1/traces", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public async Task Receiver_ArrayValueAndKvListValueAttributes_FallBackToRawJsonText()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        // Two attributes whose "value" is neither stringValue/intValue/boolValue/doubleValue:
        // an arrayValue and a kvlistValue. StringifyAttributeValue falls back to GetRawText()
        // on the WHOLE "value" JSON object for both — written compactly (no whitespace) below
        // so the expected raw text is a byte-exact, unambiguous prediction.
        const string payload =
            "{\"resourceSpans\":[{" +
            "\"resource\":{\"attributes\":[]}," +
            "\"scopeSpans\":[{\"spans\":[{" +
            "\"traceId\":\"aa000000000000000000000000000009\",\"spanId\":\"aa00000000000009\"," +
            "\"parentSpanId\":\"\",\"name\":\"op\"," +
            "\"attributes\":[" +
            "{\"key\":\"tags\",\"value\":{\"arrayValue\":{\"values\":[{\"stringValue\":\"a\"},{\"stringValue\":\"b\"}]}}}," +
            "{\"key\":\"meta\",\"value\":{\"kvlistValue\":{\"values\":[{\"key\":\"x\",\"value\":{\"stringValue\":\"y\"}}]}}}" +
            "]}]}]}]}";

        using var client = new HttpClient();
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(receiver.Url + "/v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var span = Assert.Single(buffer.Snapshot());
        Assert.Equal(
            "{\"arrayValue\":{\"values\":[{\"stringValue\":\"a\"},{\"stringValue\":\"b\"}]}}",
            span.Attributes["tags"]);
        Assert.Equal(
            "{\"kvlistValue\":{\"values\":[{\"key\":\"x\",\"value\":{\"stringValue\":\"y\"}}]}}",
            span.Attributes["meta"]);
    }

    [Fact]
    public async Task Receiver_MissingResourceSpansArray_CapturesNothingButStillSucceeds()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync(receiver.Url + "/v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public async Task Receiver_ProtobufContentType_Returns415AndCapturesNothing()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        using var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        var response = await client.PostAsync(receiver.Url + "/v1/traces", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("JSON", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(buffer.Snapshot());
    }

    /// <summary>
    /// The Content-Type gate is a strict media-type parse, not a substring match: a media type
    /// that merely CONTAINS the text "application/json" — e.g. the unrelated
    /// <c>application/jsonp</c> vendor type — must still be rejected 415, exactly like
    /// <c>application/x-protobuf</c> above.
    /// </summary>
    [Fact]
    public async Task Receiver_JsonpContentType_Returns415AndCapturesNothing()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/jsonp");
        var response = await client.PostAsync(receiver.Url + "/v1/traces", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("JSON", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(buffer.Snapshot());
    }

    /// <summary>
    /// A legitimate <c>application/json; charset=utf-8</c> Content-Type — the media type plus a
    /// charset parameter, as many real OTel SDKs/HTTP stacks send — must still be accepted: the
    /// strict media-type parse compares only the media type itself and ignores parameters.
    /// </summary>
    [Fact]
    public async Task Receiver_JsonContentTypeWithCharset_Returns200AndCapturesSpan()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        var payload = BuildExportPayload(
            traceId: "dd000000000000000000000000000001",
            spanId: "dd00000000000001",
            parentSpanId: "",
            name: "with-charset",
            serviceName: "svc",
            extraAttributes: string.Empty,
            statusCode: string.Empty);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        var response = await client.PostAsync(receiver.Url + "/v1/traces", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(buffer.Snapshot());
    }

    [Fact]
    public async Task Receiver_OversizeBody_Returns413AndCapturesNothing()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();

        // A normal small export is captured first, proving the receiver is otherwise healthy.
        var smallPayload = BuildExportPayload(
            traceId: "cc000000000000000000000000000001",
            spanId: "cc00000000000001",
            parentSpanId: "",
            name: "small",
            serviceName: "svc",
            extraAttributes: string.Empty,
            statusCode: string.Empty);
        using (var smallContent = new StringContent(smallPayload, Encoding.UTF8, "application/json"))
        {
            var smallResponse = await client.PostAsync(receiver.Url + "/v1/traces", smallContent);
            Assert.Equal(HttpStatusCode.OK, smallResponse.StatusCode);
        }

        // A body larger than the receiver's 4 MiB Kestrel limit (MaxRequestBodyBytes) is
        // rejected BEFORE the body is read — Kestrel may return a clean 413 or reset the
        // connection mid-upload; either way the request must NOT be captured.
        var oversizePayload = "{\"pad\":\"" + new string('x', (4 * 1024 * 1024) + 1) + "\"}";
        HttpStatusCode? statusIfClean = null;
        try
        {
            using var oversizeContent = new StringContent(oversizePayload, Encoding.UTF8, "application/json");
            var oversizeResponse = await client.PostAsync(receiver.Url + "/v1/traces", oversizeContent);
            statusIfClean = oversizeResponse.StatusCode;
        }
        catch (HttpRequestException)
        {
            // Connection reset on an over-limit upload — acceptable; the body was rejected.
        }

        if (statusIfClean is not null)
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, statusIfClean);
        }

        // Only the small export was ever captured.
        Assert.Single(buffer.Snapshot());
    }

    [Fact]
    public async Task Receiver_WrongPath_Returns404AndCapturesNothing()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        var response = await client.GetAsync(receiver.Url + "/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var wrongPath = await client.PostAsync(receiver.Url + "/v1/metrics", content);
        Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);

        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public async Task Receiver_WrongMethod_Returns405AndCapturesNothing()
    {
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer);

        using var client = new HttpClient();
        var response = await client.GetAsync(receiver.Url + "/v1/traces");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public async Task Receiver_AfterDispose_UrlNoLongerAnswers()
    {
        var buffer = new TraceCaptureBuffer();
        var receiver = await OtlpReceiver.StartAsync(buffer);
        var url = receiver.Url;

        await receiver.DisposeAsync();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.GetAsync(url + "/v1/traces", cts.Token);
        });
    }

    [Fact]
    public async Task Receiver_DisposeAsync_IsIdempotent()
    {
        var buffer = new TraceCaptureBuffer();
        var receiver = await OtlpReceiver.StartAsync(buffer);

        await receiver.DisposeAsync();
        await receiver.DisposeAsync(); // second call must not throw
    }

    [Fact]
    public async Task Receiver_StartAsync_NullBuffer_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await OtlpReceiver.StartAsync(null!));
    }

    // ── TraceCaptureAccessor ──────────────────────────────────────────────────

    [Fact]
    public void Accessor_GetCaptured_ReturnsRegisteredReceiverSpans()
    {
        var buffer = new TraceCaptureBuffer();
        buffer.Append(MakeSpan("t1", "s1", "op"));

        var accessor = new TraceCaptureAccessor(
            new Dictionary<string, TraceCaptureBuffer>(StringComparer.Ordinal)
            {
                ["traces"] = buffer,
            });

        var captured = accessor.GetCaptured("traces");

        Assert.Single(captured);
        Assert.Equal("op", captured[0].Name);
    }

    [Fact]
    public void Accessor_GetCaptured_UnknownName_ReturnsEmpty()
    {
        var accessor = new TraceCaptureAccessor(
            new Dictionary<string, TraceCaptureBuffer>(StringComparer.Ordinal)
            {
                ["known"] = new TraceCaptureBuffer(),
            });

        Assert.Empty(accessor.GetCaptured("unknown"));
        Assert.Empty(accessor.GetCaptured(string.Empty));
    }

    [Fact]
    public void Accessor_NullBuffers_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new TraceCaptureAccessor(null!));
    }

    [Fact]
    public void Accessor_EmptyBufferMap_AllLookupsEmpty()
    {
        var accessor = new TraceCaptureAccessor(
            new Dictionary<string, TraceCaptureBuffer>(StringComparer.Ordinal));

        Assert.Empty(accessor.GetCaptured("anything"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CapturedSpan MakeSpan(string traceId, string spanId, string name) =>
        new(
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: string.Empty,
            Name: name,
            ServiceName: "svc",
            Attributes: new Dictionary<string, string>(StringComparer.Ordinal),
            StatusCode: string.Empty,
            At: DateTimeOffset.UtcNow);

    private static string BuildExportPayload(
        string traceId,
        string spanId,
        string parentSpanId,
        string name,
        string serviceName,
        string extraAttributes,
        string statusCode)
    {
        var attributesJson = string.IsNullOrEmpty(extraAttributes) ? "[]" : "[" + extraAttributes + "]";
        var statusJson = string.IsNullOrEmpty(statusCode)
            ? ""
            : ",\"status\":{\"code\":\"" + statusCode + "\"}";

        return "{\"resourceSpans\":[{" +
            "\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"" + serviceName + "\"}}]}," +
            "\"scopeSpans\":[{\"spans\":[{" +
            "\"traceId\":\"" + traceId + "\"," +
            "\"spanId\":\"" + spanId + "\"," +
            "\"parentSpanId\":\"" + parentSpanId + "\"," +
            "\"name\":\"" + name + "\"," +
            "\"attributes\":" + attributesJson +
            statusJson +
            "}]}]" +
            "}]}";
    }
}
