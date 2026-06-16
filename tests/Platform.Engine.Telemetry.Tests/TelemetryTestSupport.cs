// Platform.Engine.Telemetry.Tests — shared support (S10-G-04).
//
// Two helpers used across the privacy tests:
//   • TempPaths: an ITelemetryPaths rooted at a throwaway temp directory + IDisposable
//     that deletes it.  EVERY test uses this so it NEVER touches the real %APPDATA%.
//   • SyntheticEvents: builds buffered v1 JSON Lines event streams (the SAME shape the
//     runner emits) so the builder/aggregation tests need no run and no container.

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
