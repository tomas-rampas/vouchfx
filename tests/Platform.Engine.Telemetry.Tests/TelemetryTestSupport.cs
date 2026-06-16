// Platform.Engine.Telemetry.Tests — shared support (S10-G-04).
//
// Two helpers used across the privacy tests:
//   • TempPaths: an ITelemetryPaths rooted at a throwaway temp directory + IDisposable
//     that deletes it.  EVERY test uses this so it NEVER touches the real %APPDATA%.
//   • SyntheticEvents: builds buffered v1 JSON Lines event streams (the SAME shape the
//     runner emits) so the builder/aggregation tests need no run and no container.

using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;

namespace Platform.Engine.Telemetry.Tests;

/// <summary>
/// An <see cref="ITelemetryPaths"/> rooted at a freshly-created throwaway temp directory,
/// deleted on <see cref="Dispose"/>.  Every test injects this so the real per-user
/// <c>%APPDATA%/vouchfx</c> config is never read, written, or mutated.
/// </summary>
internal sealed class TempPaths : ITelemetryPaths, IDisposable
{
    private readonly string _baseDir;
    private readonly DefaultTelemetryPaths _inner;

    public TempPaths()
    {
        _baseDir = Path.Combine(
            Path.GetTempPath(), "vouchfx-telemetry-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_baseDir);
        _inner = new DefaultTelemetryPaths(_baseDir);
    }

    public string ConsentStorePath => _inner.ConsentStorePath;

    public string OutboxPath => _inner.OutboxPath;

    public string DrainStatePath => _inner.DrainStatePath;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDir))
            {
                Directory.Delete(_baseDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file must not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A telemetry sink that records the events it is handed in memory, for tests that need
/// to assert a sink was (or was not) invoked.
/// </summary>
internal sealed class RecordingSink : ITelemetrySink
{
    public List<TelemetryEvent> Sent { get; } = new();

    public Task SendAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
    {
        Sent.Add(telemetryEvent);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A captured outgoing HTTP request (method, URI, headers, body) recorded by
/// <see cref="FakeHttpHandler"/> so a test can assert the over-the-wire shape.
/// </summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri? RequestUri,
    string? Authorization,
    string? IdempotencyKey,
    string? ContentType,
    string Body);

/// <summary>
/// A mock <see cref="HttpMessageHandler"/> that records every request and replies with a
/// scripted sequence of status codes (the last is reused once exhausted).  No real
/// network is touched.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
    private Func<HttpRequestMessage, HttpResponseMessage> _last =
        _ => new HttpResponseMessage(HttpStatusCode.OK);

    public List<CapturedRequest> Requests { get; } = new();

    /// <summary>Queues a fixed status code for the next request(s).</summary>
    public FakeHttpHandler RespondWith(params HttpStatusCode[] statuses)
    {
        foreach (var status in statuses)
        {
            var captured = status;
            Func<HttpRequestMessage, HttpResponseMessage> responder =
                _ => new HttpResponseMessage(captured);
            _responders.Enqueue(responder);
            _last = responder;
        }

        return this;
    }

    /// <summary>Queues a responder that throws to simulate a transport fault.</summary>
    public FakeHttpHandler RespondWithThrow(Exception toThrow)
    {
        Func<HttpRequestMessage, HttpResponseMessage> responder = _ => throw toThrow;
        _responders.Enqueue(responder);
        _last = responder;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        string? idempotency = null;
        if (request.Headers.TryGetValues(HttpOutboxClient.IdempotencyKeyHeader, out var values))
        {
            idempotency = string.Join(",", values);
        }

        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization?.ToString(),
            idempotency,
            request.Content?.Headers.ContentType?.MediaType,
            body));

        var responder = _responders.Count > 0 ? _responders.Dequeue() : _last;
        return responder(request);
    }
}

/// <summary>
/// An in-memory <see cref="IOutboxHttpClient"/> that records each posted batch and the
/// forget calls, and replies per a scripted sequence of <see cref="DrainAck"/> values
/// (the last is reused once exhausted).  Lets the drain tests assert batching/ordering
/// without any HTTP.
/// </summary>
internal sealed class RecordingOutboxHttpClient : IOutboxHttpClient
{
    private readonly Queue<DrainAck> _acks = new();
    private DrainAck _lastAck = new(Delivered: true, AcceptedCount: 0);

    public List<IReadOnlyList<string>> PostedBatches { get; } = new();

    public List<string> IdempotencyKeys { get; } = new();

    public List<Guid> Forgotten { get; } = new();

    public Func<IReadOnlyList<string>, string, DrainAck>? OnPost { get; set; }

    public RecordingOutboxHttpClient QueueAck(params bool[] delivered)
    {
        foreach (var d in delivered)
        {
            var ack = new DrainAck(d, d ? 1 : 0);
            _acks.Enqueue(ack);
            _lastAck = ack;
        }

        return this;
    }

    public Task<DrainAck> PostBatchAsync(
        IReadOnlyList<string> ndjsonLines,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        PostedBatches.Add(ndjsonLines.ToList());
        IdempotencyKeys.Add(idempotencyKey);

        if (OnPost is { } onPost)
        {
            return Task.FromResult(onPost(ndjsonLines, idempotencyKey));
        }

        var ack = _acks.Count > 0 ? _acks.Dequeue() : _lastAck;
        return Task.FromResult(ack);
    }

    public Task ForgetAsync(Guid installId, CancellationToken cancellationToken)
    {
        Forgotten.Add(installId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Builds synthetic buffered v1 JSON Lines event streams for the aggregation / privacy
/// tests — the SAME records the runner emits, serialised with the SAME
/// <see cref="EventStreamJson"/> helpers.
/// </summary>
internal static class SyntheticEvents
{
    private const string RunId = "run0000000000000000000000000000";

    /// <summary>
    /// Serialises a scenario-started event line at the given timestamp.
    /// </summary>
    public static string ScenarioStarted(
        string scenarioId, DateTimeOffset ts, string? runId = null) =>
        EventStreamJson.ToLine(new ScenarioStartedEvent
        {
            RunId = runId ?? RunId,
            Timestamp = ts,
            ScenarioId = scenarioId,
        });

    /// <summary>
    /// Serialises a step-started event line carrying the step <c>kind</c> (e.g. "http.rest").
    /// </summary>
    public static string StepStarted(
        string stepId, string kind, DateTimeOffset ts, string? runId = null) =>
        EventStreamJson.ToLine(new StepStartedEvent
        {
            RunId = runId ?? RunId,
            Timestamp = ts,
            StepId = stepId,
            Kind = kind,
        });

    /// <summary>
    /// Serialises a step-completed event line with a verdict + duration.
    /// </summary>
    public static string StepCompleted(
        string stepId, Verdict verdict, long durationMs, DateTimeOffset ts, string? runId = null) =>
        EventStreamJson.ToLine(new StepCompletedEvent
        {
            RunId = runId ?? RunId,
            Timestamp = ts,
            StepId = stepId,
            Verdict = verdict,
            DurationMs = durationMs,
        });

    /// <summary>
    /// Serialises a scenario-completed event line with a verdict + nested step counts.
    /// </summary>
    public static string ScenarioCompleted(
        string scenarioId,
        Verdict verdict,
        VerdictCounts counts,
        DateTimeOffset ts,
        string? runId = null) =>
        EventStreamJson.ToLine(new ScenarioCompletedEvent
        {
            RunId = runId ?? RunId,
            Timestamp = ts,
            ScenarioId = scenarioId,
            Verdict = verdict,
            Counts = counts,
        });
}

/// <summary>
/// A throwaway event record carrying arbitrary string fields, used by the denylist
/// serialisation scan to seed the synthetic stream with SENSITIVE sample substrings
/// (a SUT URL, an image name, a secret reference, a captured value, a scenario name,
/// raw step text).  It is serialised into the buffered stream so the builder sees a
/// realistic "data is present in the source" situation; the assertion then proves NONE
/// of those substrings survive into the TelemetryEvent JSON.
/// </summary>
internal sealed record SensitiveProbeEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "sensitive-probe";

    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("runId")]
    public string RunId { get; init; } = "run0000000000000000000000000000";

    [JsonPropertyName("sutUrl")]
    public string? SutUrl { get; init; }

    [JsonPropertyName("image")]
    public string? Image { get; init; }

    [JsonPropertyName("secretRef")]
    public string? SecretRef { get; init; }

    [JsonPropertyName("capturedValue")]
    public string? CapturedValue { get; init; }

    [JsonPropertyName("scenarioName")]
    public string? ScenarioName { get; init; }

    [JsonPropertyName("rawStepText")]
    public string? RawStepText { get; init; }
}
