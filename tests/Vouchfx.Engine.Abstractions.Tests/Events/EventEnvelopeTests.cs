// Tests for S01-G-01: Event-stream schema skeleton.
// Written RED-first (BDD loop) — these tests drive the implementation in
// Vouchfx.Engine.Abstractions/Events/EventEnvelope.cs and EventStreamJson.cs.

using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

/// <summary>
/// Verifies the wire shape, round-trip fidelity, forward-compatibility, and
/// JSON Lines discipline of the versioned event-stream envelope (§14.4).
/// </summary>
public sealed class EventEnvelopeTests
{
    // -------------------------------------------------------------------------
    // Test 1 — Exact wire shape
    // -------------------------------------------------------------------------

    [Fact]
    public void ToLine_SuiteStarted_EmitsExactPropertyNames()
    {
        // Arrange
        var envelope = new EventEnvelope
        {
            Type = EventTypes.SuiteStarted,
            RunId = "r-7f3a",
            Timestamp = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.Zero),
        };

        // Act
        var line = EventStreamJson.ToLine(envelope);

        // Assert — parse back and verify property names on the wire exactly match §14.4
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("v", out var vProp),
            "Expected property \"v\" (not \"Version\" or \"version\")");
        Assert.Equal(1, vProp.GetInt32());

        Assert.True(root.TryGetProperty("schemaVersion", out var svProp),
            "Expected property \"schemaVersion\"");
        Assert.Equal("v1", svProp.GetString());

        Assert.True(root.TryGetProperty("type", out var typeProp),
            "Expected property \"type\"");
        Assert.Equal("suite-started", typeProp.GetString());

        Assert.True(root.TryGetProperty("runId", out var runIdProp),
            "Expected property \"runId\" (not \"run_id\" or \"RunId\")");
        Assert.Equal("r-7f3a", runIdProp.GetString());

        Assert.True(root.TryGetProperty("ts", out _),
            "Expected property \"ts\" for Timestamp");

        // Null CorrelationIds must be absent (WhenWritingNull)
        Assert.False(root.TryGetProperty("correlationIds", out _),
            "Null CorrelationIds should be omitted from the wire (WhenWritingNull)");
    }

    // -------------------------------------------------------------------------
    // Test 2 — Round-trip with CorrelationIds + rich Extra payload
    // -------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_WithCorrelationIdsAndRichExtra_PreservesAllFields()
    {
        // Arrange — build the extra payload that mimics a real step-attempt event
        // whose "observation" contains a nested object and an array.
        const string rawJson = """
            {
                "v": 1,
                "schemaVersion": "v1",
                "type": "step-attempt",
                "ts": "2024-01-15T10:00:00+00:00",
                "runId": "r-7f3a",
                "correlationIds": { "trace": "00-9e1c-abc" },
                "stepId": "expect-billing-event",
                "attempt": 2,
                "tMs": 1530,
                "observation": {
                    "matched": 1,
                    "diff": [
                        {
                            "path": "payload.status",
                            "expected": "PENDING",
                            "observed": "PROCESSING"
                        }
                    ]
                },
                "providers": [
                    { "kind": "mq-expect.kafka", "version": "1.0.0" }
                ]
            }
            """;

        // Act — deserialise then re-serialise
        var envelope = EventStreamJson.FromLine(rawJson);
        var serialised = EventStreamJson.ToLine(envelope);

        // Assert typed fields
        Assert.Equal(1, envelope.Version);
        Assert.Equal("v1", envelope.SchemaVersion);
        Assert.Equal("step-attempt", envelope.Type);
        Assert.Equal("r-7f3a", envelope.RunId);

        Assert.NotNull(envelope.CorrelationIds);
        Assert.Equal("00-9e1c-abc", envelope.CorrelationIds!["trace"]);

        // Assert Extra captured the unmapped fields
        Assert.NotNull(envelope.Extra);
        Assert.True(envelope.Extra!.ContainsKey("stepId"), "Extra must contain stepId");
        Assert.True(envelope.Extra.ContainsKey("attempt"), "Extra must contain attempt");
        Assert.True(envelope.Extra.ContainsKey("tMs"), "Extra must contain tMs");
        Assert.True(envelope.Extra.ContainsKey("observation"), "Extra must contain observation");
        Assert.True(envelope.Extra.ContainsKey("providers"), "Extra must contain providers");

        // Deep-equal validation: parse both as JsonDocument and compare
        using var originalDoc = JsonDocument.Parse(rawJson);
        using var serialisedDoc = JsonDocument.Parse(serialised);

        AssertJsonEqual(originalDoc.RootElement, serialisedDoc.RootElement,
            "Re-serialised envelope must be semantically identical to the original");
    }

    // -------------------------------------------------------------------------
    // Test 3 — Unknown-field tolerance (forward compatibility)
    // -------------------------------------------------------------------------

    [Fact]
    public void FromLine_WithFutureFields_DoesNotThrowAndCapturesUnknownsInExtra()
    {
        // Arrange — a hypothetical future event with fields not yet in the typed model
        const string futureJson = """
            {
                "v": 1,
                "schemaVersion": "v1",
                "type": "step-completed",
                "ts": "2024-01-15T10:00:00+00:00",
                "runId": "r-abc1",
                "severity": "warn",
                "attempt": 3,
                "verdict": "FAIL",
                "durationMs": 30024
            }
            """;

        // Act — must not throw
        EventEnvelope envelope;
        var exception = Record.Exception(() => envelope = EventStreamJson.FromLine(futureJson));
        Assert.Null(exception);

        envelope = EventStreamJson.FromLine(futureJson);

        // Assert typed fields are bound correctly
        Assert.Equal("step-completed", envelope.Type);
        Assert.Equal("r-abc1", envelope.RunId);
        Assert.Equal(1, envelope.Version);

        // Assert unknown fields land in Extra
        Assert.NotNull(envelope.Extra);
        Assert.True(envelope.Extra!.ContainsKey("severity"), "\"severity\" must be in Extra");
        Assert.True(envelope.Extra.ContainsKey("attempt"), "\"attempt\" must be in Extra");
        Assert.True(envelope.Extra.ContainsKey("verdict"), "\"verdict\" must be in Extra");
        Assert.True(envelope.Extra.ContainsKey("durationMs"), "\"durationMs\" must be in Extra");

        // Assert re-serialise is lossless (unknown fields survive the round-trip)
        var reSerialised = EventStreamJson.ToLine(envelope);
        using var reDoc = JsonDocument.Parse(reSerialised);

        Assert.True(reDoc.RootElement.TryGetProperty("severity", out var sv) && sv.GetString() == "warn",
            "\"severity\" must survive re-serialisation");
        Assert.True(reDoc.RootElement.TryGetProperty("verdict", out var vd) && vd.GetString() == "FAIL",
            "\"verdict\" must survive re-serialisation");
        Assert.True(reDoc.RootElement.TryGetProperty("durationMs", out var dm) && dm.GetInt32() == 30024,
            "\"durationMs\" must survive re-serialisation");
    }

    // -------------------------------------------------------------------------
    // Test 4 — JSON Lines discipline: no embedded newlines
    // -------------------------------------------------------------------------

    [Fact]
    public void ToLine_Output_ContainsNoEmbeddedNewlines()
    {
        // Arrange — an envelope with correlationIds and extra payload so the
        // serialiser is exercised fully.
        var envelope = new EventEnvelope
        {
            Type = EventTypes.StepCompleted,
            RunId = "r-newline-check",
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationIds = new Dictionary<string, string>
            {
                ["trace"] = "00-9e1c-abc",
            },
        };

        // Act
        var line = EventStreamJson.ToLine(envelope);

        // Assert — a JSON Lines record MUST be exactly one line
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);

        // Also assert the output is non-empty and valid JSON
        Assert.NotEmpty(line);
        using var doc = JsonDocument.Parse(line); // must not throw
        _ = doc;
    }

    // -------------------------------------------------------------------------
    // Test 5 — EventTypes constants match the §14.4 wire strings
    // -------------------------------------------------------------------------

    [Fact]
    public void EventTypes_Constants_MatchWireStrings()
    {
        Assert.Equal("suite-started", EventTypes.SuiteStarted);
        Assert.Equal("suite-completed", EventTypes.SuiteCompleted);
        Assert.Equal("scenario-started", EventTypes.ScenarioStarted);
        Assert.Equal("scenario-completed", EventTypes.ScenarioCompleted);
        Assert.Equal("step-started", EventTypes.StepStarted);
        Assert.Equal("step-attempt", EventTypes.StepAttempt);
        Assert.Equal("step-completed", EventTypes.StepCompleted);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Recursively asserts that two <see cref="JsonElement"/> values are semantically
    /// equal regardless of property order.
    /// </summary>
    private static void AssertJsonEqual(JsonElement expected, JsonElement actual, string context)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProps = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var actualProps = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

                foreach (var key in expectedProps.Keys)
                {
                    Assert.True(actualProps.ContainsKey(key), $"{context}: missing property \"{key}\"");
                    AssertJsonEqual(expectedProps[key], actualProps[key], $"{context}.{key}");
                }
                break;

            case JsonValueKind.Array:
                var expectedArr = expected.EnumerateArray().ToArray();
                var actualArr = actual.EnumerateArray().ToArray();
                Assert.Equal(expectedArr.Length, actualArr.Length);
                for (var i = 0; i < expectedArr.Length; i++)
                    AssertJsonEqual(expectedArr[i], actualArr[i], $"{context}[{i}]");
                break;

            default:
                Assert.Equal(expected.ToString(), actual.ToString());
                break;
        }
    }
}
