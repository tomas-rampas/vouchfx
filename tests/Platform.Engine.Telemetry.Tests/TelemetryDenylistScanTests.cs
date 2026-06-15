// Platform.Engine.Telemetry.Tests — the DENYLIST serialisation scan (S10-G-04).
//
// The companion to the allowlist gate: build a TelemetryEvent from a synthetic run whose
// SOURCE event stream is deliberately seeded with every class of sensitive data — a SUT
// URL, a container image name, a secret reference, a captured value, a scenario name, and
// raw step text — serialise the event, and assert that NONE of those sensitive sample
// substrings appear in the JSON.  This proves the builder physically drops everything
// outside the allowlist, end to end.

using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Xunit;

namespace Platform.Engine.Telemetry.Tests;

public sealed class TelemetryDenylistScanTests
{
    // The sensitive sample values seeded into the SOURCE stream.  None may survive into
    // the serialised TelemetryEvent.
    private const string SutUrl = "http://sut.internal:8080";
    private const string ImageName = "postgres:18.3";
    private const string SecretRef = "${secret:vault/db}";
    private const string CapturedValue = "captured-order-id-42-SENSITIVE";
    private const string ScenarioName = "Top-Secret Checkout Flow";
    private const string RawStepText = "POST /api/orders body={\"card\":\"4111-1111-1111-1111\"}";

    [Fact]
    public void BuiltEvent_Serialised_ContainsNoneOfTheSensitiveSampleSubstrings()
    {
        var t0 = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // A realistic source stream: the legitimate v1 events the builder reads, PLUS a
        // "sensitive probe" line carrying every sensitive sample value.  The scenario id
        // itself is the (sensitive) scenario NAME — proving even a real field the builder
        // touches does not leak, because the builder only counts, never copies.
        var lines = new List<string>
        {
            SyntheticEvents.ScenarioStarted(ScenarioName, t0),
            SyntheticEvents.StepStarted("step-1", "http.rest", t0.AddMilliseconds(10)),
            SyntheticEvents.StepCompleted("step-1", Verdict.Pass, 12, t0.AddMilliseconds(22)),
            SyntheticEvents.ScenarioCompleted(
                ScenarioName, Verdict.Pass, new VerdictCounts { Pass = 1 }, t0.AddMilliseconds(30)),

            // The probe line: arbitrary sensitive fields the engine MIGHT carry elsewhere.
            // The builder must ignore this line entirely (unknown type → not measured).
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
            lines,
            installId: Guid.NewGuid(),
            toolVersion: "1.0.0",
            engineVersion: "1.0.0",
            dotnetVersion: ".NET 8.0.7",
            timestamp: t0.AddSeconds(1));

        var json = JsonSerializer.Serialize(telemetryEvent);

        // NONE of the sensitive sample substrings may appear anywhere in the JSON.
        foreach (var sensitive in new[]
                 {
                     SutUrl, ImageName, SecretRef, CapturedValue, ScenarioName, RawStepText,
                     // Distinctive fragments that would betray a partial leak.
                     "sut.internal", "4111-1111", "vault/db", "SENSITIVE", "Checkout",
                 })
        {
            Assert.DoesNotContain(sensitive, json, StringComparison.OrdinalIgnoreCase);
        }

        // Positive control: the JSON DOES carry the allowlisted, non-identifying data, so
        // the assertion above is not vacuously passing because the event is empty.
        Assert.Contains("\"stepProviders\"", json, StringComparison.Ordinal);
        Assert.Contains("http.rest", json, StringComparison.Ordinal);
        Assert.Contains("\"scenarioCount\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltEvent_Serialised_LeaksNothingFromTheEventTypesTheBuilderActuallyMeasures()
    {
        // The companion test above proves the builder ignores an UNMEASURED probe line.
        // This test goes further: it seeds the sensitive markers onto the very event
        // TYPES the builder READS — a StepStartedEvent and the ScenarioCompletedEvent
        // whose Counts the builder consumes — placing the markers on every payload field
        // the builder does NOT read.  This proves the builder copies no sensitive value
        // even out of the events it measures: from StepStarted it reads ONLY `kind` (a
        // closed taxonomy token, deliberately allowlisted), and from ScenarioCompleted it
        // reads ONLY `verdict` (an enum) + `counts` (four ints) — never stepId, runId,
        // verifyMode, or scenarioId.
        var t0 = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // A benign, taxonomy-shaped `kind` so the positive control still finds it.  The
        // sensitive markers go on the OTHER StepStartedEvent fields (stepId, verifyMode,
        // correlationIds) — the ones the builder must never touch.
        var stepStarted = EventStreamJson.ToLine(new StepStartedEvent
        {
            RunId = SecretRef,                 // runId is not read by the builder.
            Timestamp = t0.AddMilliseconds(10),
            StepId = CapturedValue,            // stepId is not read by the builder.
            Kind = "http.rest",               // the ONLY field the builder reads — benign.
            VerifyMode = RawStepText,          // verifyMode is not read by the builder.
            CorrelationIds = new Dictionary<string, string>
            {
                ["trace"] = SutUrl,
                [ImageName] = ScenarioName,
            },
        });

        // The ScenarioCompletedEvent the builder consumes for its `counts`: the markers
        // sit on scenarioId + runId (not read).  The verdict (enum) and counts (ints) are
        // typed numerics on the FROZEN record and cannot carry an arbitrary sensitive
        // string without violating that contract — so they are intentionally left as the
        // legitimate values rather than forced; the comment records why.
        var scenarioCompleted = EventStreamJson.ToLine(new ScenarioCompletedEvent
        {
            RunId = SecretRef,                 // runId is not read by the builder.
            Timestamp = t0.AddMilliseconds(30),
            ScenarioId = ScenarioName,         // scenarioId is not read by the builder.
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        });

        var stepCompleted = EventStreamJson.ToLine(new StepCompletedEvent
        {
            RunId = ImageName,                 // runId is not read by the builder.
            Timestamp = t0.AddMilliseconds(22),
            StepId = CapturedValue,            // stepId is not read by the builder.
            Verdict = Verdict.Pass,            // read as a timing anchor only, not copied.
            DurationMs = 12,
        });

        var lines = new List<string>
        {
            EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = SutUrl,                // runId is not read by the builder.
                Timestamp = t0,
                ScenarioId = ScenarioName,     // scenarioId is not read by the builder.
            }),
            stepStarted,
            stepCompleted,
            scenarioCompleted,
        };

        var telemetryEvent = TelemetryEventBuilder.Build(
            lines,
            installId: Guid.NewGuid(),
            toolVersion: "1.0.0",
            engineVersion: "1.0.0",
            dotnetVersion: ".NET 8.0.7",
            timestamp: t0.AddSeconds(1));

        var json = JsonSerializer.Serialize(telemetryEvent);

        // NONE of the sensitive markers — seeded onto the MEASURED event types — may
        // survive into the serialised TelemetryEvent.
        foreach (var sensitive in new[]
                 {
                     SutUrl, ImageName, SecretRef, CapturedValue, ScenarioName, RawStepText,
                     "sut.internal", "4111-1111", "vault/db", "SENSITIVE", "Checkout",
                 })
        {
            Assert.DoesNotContain(sensitive, json, StringComparison.OrdinalIgnoreCase);
        }

        // Positive control: the allowlisted, non-identifying data IS present, so the
        // scan above is not vacuously passing on an empty event.
        Assert.Contains("\"stepProviders\"", json, StringComparison.Ordinal);
        Assert.Contains("http.rest", json, StringComparison.Ordinal);
        Assert.Contains("\"scenarioCount\":1", json, StringComparison.Ordinal);
    }
}
