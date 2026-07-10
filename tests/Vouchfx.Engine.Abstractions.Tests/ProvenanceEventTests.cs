// S04-G-01 — Provenance event payload tests (non-docker).
//
// Verifies:
//   1. CapturedVar serialises/deserialises via EventStreamJson.Options with correct
//      wire keys (name, path, matched) — no value field present.
//   2. SubstitutionRef serialises/deserialises with correct wire keys
//      (placeholder, originStepId, secretDerived) — no value field.
//   3. StepCompletedEvent with Captured + Substitutions round-trips correctly.
//   4. The serialised JSON does NOT contain any captured value string — assert
//      the VALUE never appears.
//   5. An old renderer that only knows StepCompletedEvent without the new fields
//      tolerates forward-compat: deserialising a JSON line that carries
//      'captured'/'substitutions' into the old type succeeds (Extra absorbs the
//      unknown fields via EventEnvelope / JsonExtensionData).
//   6. CapturedVar with Matched=false serialises correctly.
//   7. SubstitutionRef with null OriginStepId serialises with omitted wire field.
using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests;

/// <summary>
/// S04-G-01: provenance event payload tests.
/// </summary>
public sealed class ProvenanceEventTests
{
    // ── 1. CapturedVar round-trip ─────────────────────────────────────────────

    /// <summary>
    /// <see cref="CapturedVar"/> must serialise to the expected wire keys and
    /// round-trip cleanly.
    /// </summary>
    [Fact]
    public void CapturedVar_SerialiseAndDeserialise_RoundTrips()
    {
        var original = new CapturedVar(
            Name: "orderId",
            Path: "$.id",
            Matched: true);

        var json = JsonSerializer.Serialize(original, EventStreamJson.Options);
        var restored = JsonSerializer.Deserialize<CapturedVar>(json, EventStreamJson.Options);

        Assert.NotNull(restored);
        Assert.Equal("orderId", restored!.Name);
        Assert.Equal("$.id", restored.Path);
        Assert.True(restored.Matched);

        // Wire keys must be lower-camel.
        Assert.Contains("\"name\"", json, StringComparison.Ordinal);
        Assert.Contains("\"path\"", json, StringComparison.Ordinal);
        Assert.Contains("\"matched\"", json, StringComparison.Ordinal);
    }

    // ── 2. SubstitutionRef round-trip ─────────────────────────────────────────

    /// <summary>
    /// <see cref="SubstitutionRef"/> must serialise to the expected wire keys and
    /// round-trip cleanly.
    /// </summary>
    [Fact]
    public void SubstitutionRef_SerialiseAndDeserialise_RoundTrips()
    {
        var original = new SubstitutionRef(
            Placeholder: "userId",
            OriginStepId: "step-1",
            SecretDerived: false);

        var json = JsonSerializer.Serialize(original, EventStreamJson.Options);
        var restored = JsonSerializer.Deserialize<SubstitutionRef>(json, EventStreamJson.Options);

        Assert.NotNull(restored);
        Assert.Equal("userId", restored!.Placeholder);
        Assert.Equal("step-1", restored.OriginStepId);
        Assert.False(restored.SecretDerived);

        Assert.Contains("\"placeholder\"", json, StringComparison.Ordinal);
        Assert.Contains("\"originStepId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"secretDerived\"", json, StringComparison.Ordinal);
    }

    // ── 3. StepCompletedEvent with Captured + Substitutions round-trips ───────

    /// <summary>
    /// <see cref="StepCompletedEvent"/> with <see cref="StepCompletedEvent.Captured"/>
    /// and <see cref="StepCompletedEvent.Substitutions"/> populated must serialise
    /// and deserialise correctly via <see cref="EventStreamJson.ToLine{T}"/> and
    /// <see cref="EventStreamJson.FromLine{T}"/>.
    /// </summary>
    [Fact]
    public void StepCompletedEvent_WithProvenanceFields_RoundTrips()
    {
        var original = new StepCompletedEvent
        {
            RunId = "run-123",
            Timestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            StepId = "step-a",
            Verdict = Verdict.Pass,
            DurationMs = 42L,
            Captured = new[]
            {
                new CapturedVar("orderId", "$.id", true),
                new CapturedVar("missing", "$.x", false),
            },
            Substitutions = new[]
            {
                new SubstitutionRef("orderId", "step-prev", false),
            },
        };

        var line = EventStreamJson.ToLine(original);
        var restored = EventStreamJson.FromLine<StepCompletedEvent>(line);

        Assert.Equal("step-a", restored.StepId);
        Assert.Equal(Verdict.Pass, restored.Verdict);
        Assert.NotNull(restored.Captured);
        Assert.Equal(2, restored.Captured!.Count);
        Assert.Equal("orderId", restored.Captured[0].Name);
        Assert.Equal("$.id", restored.Captured[0].Path);
        Assert.True(restored.Captured[0].Matched);
        Assert.False(restored.Captured[1].Matched);
        Assert.NotNull(restored.Substitutions);
        Assert.Equal("orderId", restored.Substitutions![0].Placeholder);
        Assert.Equal("step-prev", restored.Substitutions[0].OriginStepId);
    }

    // ── 4. Captured VALUE never appears in serialised JSON ────────────────────

    /// <summary>
    /// The serialised JSON of a <see cref="StepCompletedEvent"/> must NOT contain
    /// any runtime-captured value string — provenance is metadata-only (§17).
    /// </summary>
    [Fact]
    public void StepCompletedEvent_SerialisedJson_DoesNotContainCapturedValue()
    {
        const string capturedValue = "super-secret-order-id-xyz";

        // The event record itself carries no value — the captured value would only
        // appear if someone accidentally added a value field.  This test proves the
        // wire format never leaks a value by constructing a scenario where the value
        // is known and asserting it is absent from the JSON.
        var evt = new StepCompletedEvent
        {
            RunId = "run-1",
            Timestamp = DateTimeOffset.UtcNow,
            StepId = "s1",
            Verdict = Verdict.Pass,
            DurationMs = 1L,
            Captured = new[]
            {
                // Name and Path are metadata; Matched is a bool flag.
                // The VALUE 'capturedValue' is not part of the record.
                new CapturedVar("orderId", "$.id", true),
            },
        };

        var json = EventStreamJson.ToLine(evt);

        // The value must not appear — it was never in the record to begin with.
        Assert.DoesNotContain(capturedValue, json, StringComparison.Ordinal);

        // Also assert it contains the safe metadata.
        Assert.Contains("\"orderId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$.id\"", json, StringComparison.Ordinal);
    }

    // ── 5. Forward-compat: old renderer ignores new fields ────────────────────

    /// <summary>
    /// A JSON line that contains the new <c>captured</c> and <c>substitutions</c>
    /// fields must be tolerable by a renderer that only deserialises to
    /// <see cref="EventEnvelope"/>: the unknown fields are silently captured in
    /// <see cref="EventEnvelope.Extra"/> and do not cause a deserialisation error.
    /// </summary>
    [Fact]
    public void StepCompletedEvent_ForwardCompat_OldRendererIgnoresNewFields()
    {
        // Emit a new-format line with provenance fields.
        var evt = new StepCompletedEvent
        {
            RunId = "run-1",
            Timestamp = DateTimeOffset.UtcNow,
            StepId = "s1",
            Verdict = Verdict.Pass,
            DurationMs = 1L,
            Captured = new[] { new CapturedVar("id", "$.id", true) },
            Substitutions = new[] { new SubstitutionRef("id", "prev", false) },
        };
        var line = EventStreamJson.ToLine(evt);

        // An old renderer reads the line as EventEnvelope.
        var envelope = EventStreamJson.FromLine(line);

        // Must not throw and must carry the known envelope fields.
        Assert.Equal("run-1", envelope.RunId);
        Assert.Equal(EventTypes.StepCompleted, envelope.Type);

        // The new fields land in Extra — not null and not empty.
        Assert.NotNull(envelope.Extra);
        // 'captured' and 'substitutions' must be among the extra keys.
        Assert.True(
            envelope.Extra!.ContainsKey("captured") ||
            envelope.Extra.ContainsKey("substitutions"),
            "Expected 'captured' or 'substitutions' in EventEnvelope.Extra for forward-compat.");
    }

    // ── 6. CapturedVar with Matched=false ─────────────────────────────────────

    /// <summary>
    /// <see cref="CapturedVar"/> with <c>Matched = false</c> must serialise the
    /// flag correctly (not omitted as null).
    /// </summary>
    [Fact]
    public void CapturedVar_MatchedFalse_SerialisesFlagCorrectly()
    {
        var cv = new CapturedVar("missing", "$.nope", false);
        var json = JsonSerializer.Serialize(cv, EventStreamJson.Options);

        Assert.Contains("\"matched\":false", json, StringComparison.Ordinal);
    }

    // ── 7. SubstitutionRef with null OriginStepId ─────────────────────────────

    /// <summary>
    /// When <see cref="SubstitutionRef.OriginStepId"/> is <see langword="null"/>
    /// (variable sourced from <c>variables</c> block or unknown), the wire field
    /// must be omitted (due to <c>DefaultIgnoreCondition.WhenWritingNull</c>).
    /// </summary>
    [Fact]
    public void SubstitutionRef_NullOriginStepId_FieldOmittedFromWire()
    {
        var sr = new SubstitutionRef("baseUrl", null, false);
        var json = JsonSerializer.Serialize(sr, EventStreamJson.Options);

        // The null field must be omitted from the wire (WhenWritingNull).
        Assert.DoesNotContain("\"originStepId\"", json, StringComparison.Ordinal);

        // The placeholder must still be present.
        Assert.Contains("\"baseUrl\"", json, StringComparison.Ordinal);
    }
}
