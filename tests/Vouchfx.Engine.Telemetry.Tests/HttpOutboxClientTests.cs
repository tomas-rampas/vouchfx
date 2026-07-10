// Vouchfx.Engine.Telemetry.Tests — HttpOutboxClient (S12-G-01, Phase A).
//
// Asserts the OVER-THE-WIRE shape of the HTTP transport via a fake HttpMessageHandler:
//   • POST /v1/telemetry, Authorization: Bearer, Idempotency-Key set, Content-Type ndjson;
//   • the body is the EXACT outbox lines (the raw NDJSON, never re-serialised);
//   • the OVER-THE-WIRE DENYLIST SCAN (criterion 5): a real allowlisted event built from a
//     synthetic run seeded with every class of sensitive data, then POSTed, carries NONE of
//     those sample substrings in the request body;
//   • 2xx vs non-2xx classification (Delivered);
//   • ForgetAsync posts the right shape to /v1/telemetry/forget;
//   • the bearer token never appears anywhere except the Authorization header value.

using System.Net;
using System.Net.Http;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Xunit;

namespace Vouchfx.Engine.Telemetry.Tests;

public sealed class HttpOutboxClientTests
{
    private static readonly Uri Endpoint = new("https://telemetry.vouchfx.test");
    private const string Token = "tok-SECRET-bearer-value-123";

    // A single-line batch reused across cases — a static field rather than an inline
    // constant array argument (CA1861).
    private static readonly string[] SingleLineBatch = { "{\"x\":1}" };

    [Fact]
    public async Task PostBatchAsync_SendsPostToV1Telemetry_WithBearerIdempotencyAndNdjsonBody()
    {
        var handler = new FakeHttpHandler().RespondWith(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new HttpOutboxClient(http, Endpoint, Token);

        var lines = new List<string> { "{\"a\":1}", "{\"b\":2}" };

        var ack = await client.PostBatchAsync(lines, "idem-key-abc", CancellationToken.None);

        Assert.True(ack.Delivered);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://telemetry.vouchfx.test/v1/telemetry", req.RequestUri!.ToString());
        Assert.Equal($"Bearer {Token}", req.Authorization);
        Assert.Equal("idem-key-abc", req.IdempotencyKey);
        Assert.Equal(HttpOutboxClient.NdjsonMediaType, req.ContentType);

        // The body is the EXACT outbox lines joined by '\n' — byte-identical, not re-serialised.
        Assert.Equal("{\"a\":1}\n{\"b\":2}", req.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.Accepted, true)]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task PostBatchAsync_ClassifiesTwoXxAsDelivered_AndEverythingElseAsNotDelivered(
        HttpStatusCode status, bool expectedDelivered)
    {
        var handler = new FakeHttpHandler().RespondWith(status);
        using var http = new HttpClient(handler);
        var client = new HttpOutboxClient(http, Endpoint, Token);

        var ack = await client.PostBatchAsync(
            SingleLineBatch, "k", CancellationToken.None);

        Assert.Equal(expectedDelivered, ack.Delivered);
    }

    [Fact]
    public async Task PostBatchAsync_TransportFault_ReturnsNotDelivered_NeverThrows()
    {
        var handler = new FakeHttpHandler()
            .RespondWithThrow(new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);
        var client = new HttpOutboxClient(http, Endpoint, Token);

        var ack = await client.PostBatchAsync(
            SingleLineBatch, "k", CancellationToken.None);

        Assert.False(ack.Delivered);
    }

    [Fact]
    public async Task PostBatchAsync_EmptyBatch_IsDeliveredNoOp_AndSendsNothing()
    {
        var handler = new FakeHttpHandler().RespondWith(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new HttpOutboxClient(http, Endpoint, Token);

        var ack = await client.PostBatchAsync(
            Array.Empty<string>(), "k", CancellationToken.None);

        Assert.True(ack.Delivered);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ForgetAsync_PostsInstallIdToForgetEndpoint_WithBearer()
    {
        var handler = new FakeHttpHandler().RespondWith(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new HttpOutboxClient(http, Endpoint, Token);

        var installId = Guid.NewGuid();
        await client.ForgetAsync(installId, CancellationToken.None);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(
            "https://telemetry.vouchfx.test/v1/telemetry/forget", req.RequestUri!.ToString());
        Assert.Equal($"Bearer {Token}", req.Authorization);
        Assert.Equal("application/json", req.ContentType);

        using var doc = JsonDocument.Parse(req.Body);
        Assert.Equal(installId.ToString("d"), doc.RootElement.GetProperty("installId").GetString());
    }

    [Fact]
    public async Task ForgetAsync_TransportFault_DoesNotThrow()
    {
        var handler = new FakeHttpHandler()
            .RespondWithThrow(new HttpRequestException("offline"));
        using var http = new HttpClient(handler);
        var client = new HttpOutboxClient(http, Endpoint, Token);

        // Must not throw — the disable path depends on this being fail-silent.
        await client.ForgetAsync(Guid.NewGuid(), CancellationToken.None);
    }

    [Fact]
    public async Task PostBatchAsync_RealAllowlistedEvent_BodyContainsNoneOfTheSensitiveSubstrings()
    {
        // Criterion 5 — the OVER-THE-WIRE denylist scan.  Build a REAL allowlisted
        // TelemetryEvent from a synthetic run seeded with a SUT URL, an image name, a secret
        // reference, a captured value, a scenario name, and raw step text; serialise it to a
        // single outbox line exactly as the local sink would; POST it; then assert the
        // request BODY (what actually leaves the machine) carries none of those substrings.
        const string SutUrl = "http://sut.internal:8080";
        const string ImageName = "postgres:18.3";
        const string SecretRef = "${secret:vault/db}";
        const string CapturedValue = "captured-order-id-42-SENSITIVE";
        const string ScenarioName = "Top-Secret Checkout Flow";
        const string RawStepText = "POST /api/orders body={\"card\":\"4111-1111-1111-1111\"}";

        var t0 = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var sourceLines = new List<string>
        {
            SyntheticEvents.ScenarioStarted(ScenarioName, t0),
            SyntheticEvents.StepStarted("step-1", "http.rest", t0.AddMilliseconds(10)),
            SyntheticEvents.StepCompleted("step-1", Verdict.Pass, 12, t0.AddMilliseconds(22)),
            SyntheticEvents.ScenarioCompleted(
                ScenarioName, Verdict.Pass, new VerdictCounts { Pass = 1 }, t0.AddMilliseconds(30)),

            // A probe line carrying every sensitive sample value (the builder ignores it).
            EventStreamJson.ToLine(new SensitiveProbeEvent
            {
                Timestamp = t0.AddMilliseconds(5),
                SutUrl = SutUrl,
                Image = ImageName,
                SecretRef = SecretRef,
                CapturedValue = CapturedValue,
                ScenarioName = ScenarioName,
                RawStepText = RawStepText,
            }),
        };

        var telemetryEvent = TelemetryEventBuilder.Build(
            sourceLines,
            installId: Guid.NewGuid(),
            toolVersion: "1.0.0",
            engineVersion: "1.0.0",
            dotnetVersion: ".NET 8.0.7",
            timestamp: t0.AddSeconds(1));

        // Serialise to a single outbox line exactly as the local sink does, then write it to
        // a temp outbox and read it back so the bytes are precisely what would be drained.
        using var paths = new TempPaths();
        await new LocalFileTelemetrySink(paths).SendAsync(telemetryEvent);
        var outboxLines = await File.ReadAllLinesAsync(paths.OutboxPath);

        var handler = new FakeHttpHandler().RespondWith(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new HttpOutboxClient(http, Endpoint, Token);

        await client.PostBatchAsync(outboxLines, "k", CancellationToken.None);

        var body = Assert.Single(handler.Requests).Body;
        foreach (var sensitive in new[]
                 {
                     SutUrl, ImageName, SecretRef, CapturedValue, ScenarioName, RawStepText,
                     "sut.internal", "4111-1111", "vault/db", "SENSITIVE", "Checkout",
                 })
        {
            Assert.DoesNotContain(sensitive, body, StringComparison.OrdinalIgnoreCase);
        }

        // Positive control: the allowlisted data IS on the wire (so the scan is not vacuous).
        Assert.Contains("http.rest", body, StringComparison.Ordinal);
    }

    [Theory]
    // Bare-origin endpoint: paths sit directly under the host.
    [InlineData("https://telemetry.vouchfx.test",
        "https://telemetry.vouchfx.test/v1/telemetry",
        "https://telemetry.vouchfx.test/v1/telemetry/forget")]
    [InlineData("https://telemetry.vouchfx.test/",
        "https://telemetry.vouchfx.test/v1/telemetry",
        "https://telemetry.vouchfx.test/v1/telemetry/forget")]
    // Operator base path: the base path MUST be preserved (not dropped) — with or without a
    // trailing slash it resolves to the same base-relative target.
    [InlineData("https://host/api",
        "https://host/api/v1/telemetry",
        "https://host/api/v1/telemetry/forget")]
    [InlineData("https://host/api/",
        "https://host/api/v1/telemetry",
        "https://host/api/v1/telemetry/forget")]
    public async Task Endpoint_BasePathIsPreserved_ForBatchAndForget(
        string endpoint, string expectedBatchUri, string expectedForgetUri)
    {
        var handler = new FakeHttpHandler().RespondWith(HttpStatusCode.OK, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new HttpOutboxClient(http, new Uri(endpoint), Token);

        await client.PostBatchAsync(SingleLineBatch, "k", CancellationToken.None);
        await client.ForgetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(expectedBatchUri, handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(expectedForgetUri, handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Token_NeverAppearsOutsideTheAuthorizationHeader()
    {
        // §17: the bearer token is header-only; it must not leak into the body or the URI.
        var handler = new FakeHttpHandler().RespondWith(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new HttpOutboxClient(http, Endpoint, Token);

        await client.PostBatchAsync(SingleLineBatch, "k", CancellationToken.None);
        await client.ForgetAsync(Guid.NewGuid(), CancellationToken.None);

        foreach (var req in handler.Requests)
        {
            Assert.DoesNotContain(Token, req.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, req.RequestUri!.ToString(), StringComparison.Ordinal);
            // The token may ONLY appear inside the Authorization header value.
            Assert.Equal($"Bearer {Token}", req.Authorization);
        }
    }
}
