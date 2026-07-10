// Tests for S02-G-01: Full event-stream schema — typed event payloads and
// the Verdict type.  Written RED-first (BDD loop) — these tests drive the
// implementation in Vouchfx.Engine.Abstractions.
//
// Covered concerns:
//   • Round-trip fidelity for every payload record.
//   • Wire names match §14.4 exactly (flat shape, sibling fields).
//   • VerdictJsonConverter maps each enum value to its exact canonical token.
//   • Unknown token → JsonException (no silent defaulting).
//   • Forward-compat / envelope-only renderer: payload fields appear in
//     EventEnvelope.Extra so a renderer that only knows the envelope still
//     sees everything (§14 guarantee).
//   • JSON Lines discipline: single-line output, no embedded newlines.
//   • VerdictCounts serialises with the correct lower-camel wire keys.

using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

/// <summary>
/// Verifies typed event-payload records, the <see cref="Verdict"/> converter,
/// and cross-cutting wire-format concerns for the structured JSON Lines event
/// stream (§14.4).
/// </summary>
public sealed class EventPayloadTests
{
    // =========================================================================
    // Verdict converter
    // =========================================================================

    [Theory]
    [InlineData(Verdict.Pass, "PASS")]
    [InlineData(Verdict.Fail, "FAIL")]
    [InlineData(Verdict.EnvironmentError, "ENV_ERROR")]
    [InlineData(Verdict.Inconclusive, "INCONCLUSIVE")]
    public void VerdictConverter_Serialises_ToExactToken(Verdict verdict, string expectedToken)
    {
        // Act — serialise a value type wrapped in an anonymous-style record so
        // we go through the converter rather than the default integer path.
        var json = JsonSerializer.Serialize(verdict, EventStreamJson.Options);

        // Assert — must be the quoted token, e.g. "\"PASS\""
        Assert.Equal($"\"{expectedToken}\"", json);
    }

    [Theory]
    [InlineData("\"PASS\"", Verdict.Pass)]
    [InlineData("\"FAIL\"", Verdict.Fail)]
    [InlineData("\"ENV_ERROR\"", Verdict.EnvironmentError)]
    [InlineData("\"INCONCLUSIVE\"", Verdict.Inconclusive)]
    public void VerdictConverter_Deserialises_FromExactToken(string json, Verdict expectedVerdict)
    {
        // Act
        var verdict = JsonSerializer.Deserialize<Verdict>(json, EventStreamJson.Options);

        // Assert
        Assert.Equal(expectedVerdict, verdict);
    }

    [Fact]
    public void VerdictConverter_UnknownToken_ThrowsJsonException()
    {
        // Arrange — "UNKNOWN" is not a valid verdict token
        const string badJson = "\"UNKNOWN\"";

        // Act & Assert
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Verdict>(badJson, EventStreamJson.Options));
    }

    [Fact]
    public void VerdictConverter_LowercaseToken_ThrowsJsonException()
    {
        // Converter is case-sensitive — "pass" is not the canonical token "PASS"
        const string badJson = "\"pass\"";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Verdict>(badJson, EventStreamJson.Options));
    }

    // =========================================================================
    // ScenarioStartedEvent — round-trip and wire names
    // =========================================================================

    [Fact]
    public void ScenarioStartedEvent_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var ts = new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var evt = new ScenarioStartedEvent
        {
            RunId = "run-001",
            Timestamp = ts,
            ScenarioId = "scenario-login",
            File = "tests/login.e2e.yaml",
            ContentHash = "sha256:abc123",
        };

        // Act
        var line = EventStreamJson.ToLine(evt);
        var restored = EventStreamJson.FromLine<ScenarioStartedEvent>(line);

        // Assert
        Assert.Equal(1, restored.Version);
        Assert.Equal("v1", restored.SchemaVersion);
        Assert.Equal(EventTypes.ScenarioStarted, restored.Type);
        Assert.Equal("run-001", restored.RunId);
        Assert.Equal(ts, restored.Timestamp);
        Assert.Equal("scenario-login", restored.ScenarioId);
        Assert.Equal("tests/login.e2e.yaml", restored.File);
        Assert.Equal("sha256:abc123", restored.ContentHash);
    }

    [Fact]
    public void ScenarioStartedEvent_WireNames_AreFlat()
    {
        // Arrange
        var evt = new ScenarioStartedEvent
        {
            RunId = "run-002",
            ScenarioId = "scenario-checkout",
            File = "tests/checkout.e2e.yaml",
            ContentHash = "sha256:def456",
        };

        // Act
        var line = EventStreamJson.ToLine(evt);

        // Assert — all fields must be siblings at the root object level (flat wire shape)
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("type", out var typeProp));
        Assert.Equal("scenario-started", typeProp.GetString());
        Assert.True(root.TryGetProperty("runId", out _));
        Assert.True(root.TryGetProperty("scenarioId", out var sid));
        Assert.Equal("scenario-checkout", sid.GetString());
        Assert.True(root.TryGetProperty("file", out var fileProp));
        Assert.Equal("tests/checkout.e2e.yaml", fileProp.GetString());
        Assert.True(root.TryGetProperty("contentHash", out var hash));
        Assert.Equal("sha256:def456", hash.GetString());

        // Must NOT have a nested "payload" object
        Assert.False(root.TryGetProperty("payload", out _),
            "Wire shape must be flat — no nested payload object");
    }

    [Fact]
    public void ScenarioStartedEvent_NullOptionalFields_OmittedFromWire()
    {
        // Arrange
        var evt = new ScenarioStartedEvent
        {
            RunId = "run-003",
            ScenarioId = "scenario-only",
        };

        // Act
        var line = EventStreamJson.ToLine(evt);

        // Assert — null File and ContentHash must be absent (WhenWritingNull)
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("file", out _),
            "Null File must be omitted from the wire");
        Assert.False(root.TryGetProperty("contentHash", out _),
            "Null ContentHash must be omitted from the wire");
    }

    // =========================================================================
    // StepStartedEvent — round-trip and wire names
    // =========================================================================

    [Fact]
    public void StepStartedEvent_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var evt = new StepStartedEvent
        {
            RunId = "run-010",
            StepId = "step-call-api",
            Kind = "http.rest",
            VerifyMode = "IMMEDIATE",
            TimeoutMs = 5000L,
        };

        // Act
        var line = EventStreamJson.ToLine(evt);
        var restored = EventStreamJson.FromLine<StepStartedEvent>(line);

        // Assert
        Assert.Equal(EventTypes.StepStarted, restored.Type);
        Assert.Equal("step-call-api", restored.StepId);
        Assert.Equal("http.rest", restored.Kind);
        Assert.Equal("IMMEDIATE", restored.VerifyMode);
        Assert.Equal(5000L, restored.TimeoutMs);
    }

    [Fact]
    public void StepStartedEvent_WireNames_AreCorrect()
    {
        // Arrange
        var evt = new StepStartedEvent
        {
            RunId = "run-011",
            StepId = "step-publish",
            Kind = "mq-publish.kafka",
            VerifyMode = "RETRY",
            TimeoutMs = 30000L,
        };

        // Act
        var line = EventStreamJson.ToLine(evt);

        // Assert wire property names
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("stepId", out var sid));
        Assert.Equal("step-publish", sid.GetString());
        Assert.True(root.TryGetProperty("kind", out var kind));
        Assert.Equal("mq-publish.kafka", kind.GetString());
        Assert.True(root.TryGetProperty("verifyMode", out var vm));
        Assert.Equal("RETRY", vm.GetString());
        Assert.True(root.TryGetProperty("timeoutMs", out var tms));
        Assert.Equal(30000L, tms.GetInt64());
    }

    // =========================================================================
    // StepAttemptEvent — round-trip, attempt/tMs/outcome wire names
    // =========================================================================

    [Fact]
    public void StepAttemptEvent_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var observation = JsonDocument.Parse("""{"matched":1}""").RootElement.Clone();
        var evt = new StepAttemptEvent
        {
            RunId = "run-020",
            StepId = "step-db-check",
            Attempt = 3,
            TMs = 1530L,
            Outcome = Verdict.Fail,
            Observation = observation,
        };

        // Act
        var line = EventStreamJson.ToLine(evt);
        var restored = EventStreamJson.FromLine<StepAttemptEvent>(line);

        // Assert
        Assert.Equal(EventTypes.StepAttempt, restored.Type);
        Assert.Equal("step-db-check", restored.StepId);
        Assert.Equal(3, restored.Attempt);
        Assert.Equal(1530L, restored.TMs);
        Assert.Equal(Verdict.Fail, restored.Outcome);
        Assert.NotNull(restored.Observation);
    }

    [Fact]
    public void StepAttemptEvent_WireNames_AreAttemptTMsOutcome()
    {
        // Arrange — this is the critical assertion: the wire names must match
        // the documented §14.4 shape exactly so all renderers can parse them.
        var evt = new StepAttemptEvent
        {
            RunId = "run-021",
            StepId = "step-expect-event",
            Attempt = 2,
            TMs = 750L,
            Outcome = Verdict.Inconclusive,
        };

        // Act
        var line = EventStreamJson.ToLine(evt);

        // Assert wire names explicitly
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("attempt", out var attempt),
            "Wire name for Attempt must be \"attempt\"");
        Assert.Equal(2, attempt.GetInt32());

        Assert.True(root.TryGetProperty("tMs", out var tMs),
            "Wire name for TMs must be \"tMs\"");
        Assert.Equal(750L, tMs.GetInt64());

        Assert.True(root.TryGetProperty("outcome", out var outcome),
            "Wire name for Outcome must be \"outcome\"");
        Assert.Equal("INCONCLUSIVE", outcome.GetString());
    }

    [Fact]
    public void StepAttemptEvent_NullOutcomeAndObservation_OmittedFromWire()
    {
        // Arrange
        var evt = new StepAttemptEvent
        {
            RunId = "run-022",
            StepId = "step-no-outcome",
            Attempt = 1,
            TMs = 100L,
        };

        // Act
        var line = EventStreamJson.ToLine(evt);

        // Assert
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("outcome", out _),
            "Null Outcome must be omitted (WhenWritingNull)");
        Assert.False(root.TryGetProperty("observation", out _),
            "Null Observation must be omitted (WhenWritingNull)");
    }

    // =========================================================================
    // StepCompletedEvent — round-trip and wire names
    // =========================================================================

    [Fact]
    public void StepCompletedEvent_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var evt = new StepCompletedEvent
        {
            RunId = "run-030",
            StepId = "step-final",
            Verdict = Verdict.Pass,
            DurationMs = 2048L,
        };

        // Act
        var line = EventStreamJson.ToLine(evt);
        var restored = EventStreamJson.FromLine<StepCompletedEvent>(line);

        // Assert
        Assert.Equal(EventTypes.StepCompleted, restored.Type);
        Assert.Equal("step-final", restored.StepId);
        Assert.Equal(Verdict.Pass, restored.Verdict);
        Assert.Equal(2048L, restored.DurationMs);
    }

    [Fact]
    public void StepCompletedEvent_VerdictToken_SerialisesProperly()
    {
        // Arrange
        var evt = new StepCompletedEvent
        {
            RunId = "run-031",
            StepId = "step-fail",
            Verdict = Verdict.EnvironmentError,
            DurationMs = 99L,
        };

        // Act
        var line = EventStreamJson.ToLine(evt);

        // Assert — verdict must be the string token, not an integer
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("verdict", out var v));
        Assert.Equal(JsonValueKind.String, v.ValueKind);
        Assert.Equal("ENV_ERROR", v.GetString());

        Assert.True(root.TryGetProperty("durationMs", out var dur));
        Assert.Equal(99L, dur.GetInt64());
    }

    // =========================================================================
    // ScenarioCompletedEvent — round-trip and VerdictCounts wire keys
    // =========================================================================

    [Fact]
    public void ScenarioCompletedEvent_RoundTrip_PreservesAllFields()
    {
        // Arrange
        var counts = new VerdictCounts
        {
            Pass = 10,
            Fail = 2,
            EnvError = 1,
            Inconclusive = 0,
        };
        var evt = new ScenarioCompletedEvent
        {
            RunId = "run-040",
            ScenarioId = "scenario-billing",
            Verdict = Verdict.Fail,
            Counts = counts,
        };

        // Act
        var line = EventStreamJson.ToLine(evt);
        var restored = EventStreamJson.FromLine<ScenarioCompletedEvent>(line);

        // Assert
        Assert.Equal(EventTypes.ScenarioCompleted, restored.Type);
        Assert.Equal("scenario-billing", restored.ScenarioId);
        Assert.Equal(Verdict.Fail, restored.Verdict);
        Assert.Equal(10, restored.Counts.Pass);
        Assert.Equal(2, restored.Counts.Fail);
        Assert.Equal(1, restored.Counts.EnvError);
        Assert.Equal(0, restored.Counts.Inconclusive);
    }

    [Fact]
    public void ScenarioCompletedEvent_CountsWireKeys_AreCorrect()
    {
        // Arrange
        var evt = new ScenarioCompletedEvent
        {
            RunId = "run-041",
            ScenarioId = "scenario-checkout",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts
            {
                Pass = 5,
                Fail = 0,
                EnvError = 0,
                Inconclusive = 1,
            },
        };

        // Act
        var line = EventStreamJson.ToLine(evt);

        // Assert — verify the VerdictCounts wire keys exactly (§14.4)
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("counts", out var countsProp),
            "ScenarioCompletedEvent must have a \"counts\" wire field");
        Assert.Equal(JsonValueKind.Object, countsProp.ValueKind);

        Assert.True(countsProp.TryGetProperty("pass", out var p));
        Assert.Equal(5, p.GetInt32());

        Assert.True(countsProp.TryGetProperty("fail", out var f));
        Assert.Equal(0, f.GetInt32());

        Assert.True(countsProp.TryGetProperty("envError", out var e));
        Assert.Equal(0, e.GetInt32());

        Assert.True(countsProp.TryGetProperty("inconclusive", out var i));
        Assert.Equal(1, i.GetInt32());
    }

    // =========================================================================
    // Forward-compatibility / envelope-only renderer test (critical §14)
    // =========================================================================

    [Fact]
    public void StepCompletedEvent_DeserialiseAsEnvelope_PayloadFieldsInExtra()
    {
        // Arrange — serialise a typed payload record
        var evt = new StepCompletedEvent
        {
            RunId = "run-050",
            StepId = "step-payment",
            Verdict = Verdict.Fail,
            DurationMs = 4200L,
        };

        // Act — serialise via typed path, then read back as the generic envelope
        var line = EventStreamJson.ToLine(evt);
        var envelope = EventStreamJson.FromLine(line); // EventEnvelope overload

        // Assert — envelope-level typed fields are present
        Assert.Equal(EventTypes.StepCompleted, envelope.Type);
        Assert.Equal("run-050", envelope.RunId);
        Assert.Equal(1, envelope.Version);
        Assert.Equal("v1", envelope.SchemaVersion);

        // Assert — payload-specific fields appear in Extra (the renderer sees them)
        Assert.NotNull(envelope.Extra);
        Assert.True(envelope.Extra!.ContainsKey("stepId"),
            "stepId must appear in EventEnvelope.Extra");
        Assert.True(envelope.Extra.ContainsKey("verdict"),
            "verdict must appear in EventEnvelope.Extra");
        Assert.True(envelope.Extra.ContainsKey("durationMs"),
            "durationMs must appear in EventEnvelope.Extra");

        // Assert — verdict is preserved as its string token in Extra
        var verdictElement = envelope.Extra["verdict"];
        Assert.Equal(JsonValueKind.String, verdictElement.ValueKind);
        Assert.Equal("FAIL", verdictElement.GetString());

        // Assert — stepId and durationMs values are also correct
        Assert.Equal("step-payment", envelope.Extra["stepId"].GetString());
        Assert.Equal(4200L, envelope.Extra["durationMs"].GetInt64());
    }

    // =========================================================================
    // JSON Lines discipline: no embedded newlines
    // =========================================================================

    [Theory]
    [InlineData("ScenarioStarted")]
    [InlineData("StepStarted")]
    [InlineData("StepAttempt")]
    [InlineData("StepCompleted")]
    [InlineData("ScenarioCompleted")]
    public void ToLine_PayloadEvents_ContainNoEmbeddedNewlines(string eventKind)
    {
        // Arrange — build a representative event for each payload type
        var line = eventKind switch
        {
            "ScenarioStarted" => EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = "r",
                ScenarioId = "s",
                File = "f.e2e.yaml",
                ContentHash = "h",
            }),
            "StepStarted" => EventStreamJson.ToLine(new StepStartedEvent
            {
                RunId = "r",
                StepId = "s",
                Kind = "http.rest",
                VerifyMode = "IMMEDIATE",
                TimeoutMs = 1000L,
            }),
            "StepAttempt" => EventStreamJson.ToLine(new StepAttemptEvent
            {
                RunId = "r",
                StepId = "s",
                Attempt = 1,
                TMs = 100L,
            }),
            "StepCompleted" => EventStreamJson.ToLine(new StepCompletedEvent
            {
                RunId = "r",
                StepId = "s",
                Verdict = Verdict.Pass,
                DurationMs = 200L,
            }),
            "ScenarioCompleted" => EventStreamJson.ToLine(new ScenarioCompletedEvent
            {
                RunId = "r",
                ScenarioId = "s",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(eventKind)),
        };

        // Assert — JSON Lines mandate: no embedded newlines
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.NotEmpty(line);
        using var doc = JsonDocument.Parse(line); // must not throw
        _ = doc;
    }

    // =========================================================================
    // Default type discriminators
    // =========================================================================

    [Fact]
    public void PayloadRecords_TypeDefaults_MatchEventTypesConsts()
    {
        // Each payload record must default Type to its matching EventTypes constant
        // so callers never have to set it manually.
        Assert.Equal(EventTypes.ScenarioStarted, new ScenarioStartedEvent { RunId = "r", ScenarioId = "s" }.Type);
        Assert.Equal(EventTypes.StepStarted, new StepStartedEvent { RunId = "r", StepId = "s" }.Type);
        Assert.Equal(EventTypes.StepAttempt, new StepAttemptEvent { RunId = "r", StepId = "s", Attempt = 1, TMs = 0L }.Type);
        Assert.Equal(EventTypes.StepCompleted, new StepCompletedEvent { RunId = "r", StepId = "s", Verdict = Verdict.Pass, DurationMs = 0L }.Type);
        Assert.Equal(EventTypes.ScenarioCompleted, new ScenarioCompletedEvent { RunId = "r", ScenarioId = "s", Verdict = Verdict.Pass, Counts = new VerdictCounts() }.Type);
    }

    // =========================================================================
    // Generic ToLine<T> / FromLine<T> helpers
    // =========================================================================

    [Fact]
    public void GenericToLine_ReturnsValidJson()
    {
        // Arrange
        var evt = new StepStartedEvent
        {
            RunId = "run-generic",
            StepId = "step-generic",
        };

        // Act — use the generic overload explicitly
        var line = EventStreamJson.ToLine<StepStartedEvent>(evt);

        // Assert
        Assert.NotEmpty(line);
        using var doc = JsonDocument.Parse(line);
        Assert.True(doc.RootElement.TryGetProperty("type", out var t));
        Assert.Equal("step-started", t.GetString());
    }

    [Fact]
    public void GenericFromLine_ReturnsTypedRecord()
    {
        // Arrange
        var original = new StepCompletedEvent
        {
            RunId = "run-generic-2",
            StepId = "step-generic-2",
            Verdict = Verdict.Pass,
            DurationMs = 123L,
        };
        var line = EventStreamJson.ToLine(original);

        // Act — use the generic FromLine<T> overload
        var restored = EventStreamJson.FromLine<StepCompletedEvent>(line);

        // Assert
        Assert.Equal(original.RunId, restored.RunId);
        Assert.Equal(original.StepId, restored.StepId);
        Assert.Equal(original.Verdict, restored.Verdict);
        Assert.Equal(original.DurationMs, restored.DurationMs);
    }

    [Fact]
    public void GenericFromLine_NullJson_ThrowsInvalidOperationException()
    {
        // Arrange — "null" is valid JSON but Deserialize<T> returns null for it
        const string nullJson = "null";

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            EventStreamJson.FromLine<StepCompletedEvent>(nullJson));
    }
}
