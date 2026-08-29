// Tests for the transport-notice event record (#450, #453) — the wire half of the
// two transport advisories the engine prints to the terminal by their own route.
//
// Covered concerns:
//   • Round-trip fidelity for BOTH kinds.
//   • The wire shape is flat and matches the pinned names exactly.
//   • rejectedEndpoint is absent from the wire for the no-engine-trust kind — the
//     serialiser's WhenWritingNull does it, so no [JsonIgnore] is needed and its
//     absence must be proved rather than assumed.
//   • The kind vocabulary is the two tokens, matched ordinally — the ONLY value-freeze
//     for them, since the event-contract golden pins names and types, never values.
//   • replayed round-trips when set and is absent from the wire when unset, which is
//     what keeps the two fresh-build paths byte-identical to a stream without it.
//   • The four required members are NOT tolerated as absent — tolerance covers the
//     token vocabulary, not the field set.
//   • An envelope-only renderer still sees every payload field (§14 forward
//     compatibility) — which is what makes the record safe to add to a frozen wire.

using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

/// <summary>
/// Verifies <see cref="TransportNoticeEvent"/> and its <see cref="TransportNoticeKinds"/>
/// vocabulary against the §14.4 JSON Lines wire format.
/// </summary>
public sealed class TransportNoticeEventTests
{
    // =========================================================================
    // Round-trip — both kinds
    // =========================================================================

    [Fact]
    public void PlaintextDowngrade_RoundTrip_PreservesAllFields()
    {
        // Arrange — the downgrade kind is the one that names both endpoints.
        var evt = new TransportNoticeEvent
        {
            RunId = "run-001",
            Timestamp = new DateTimeOffset(2026, 8, 29, 10, 30, 0, TimeSpan.Zero),
            Kind = TransportNoticeKinds.PlaintextDowngrade,
            Service = "orders-api",
            SelectedEndpoint = "http",
            RejectedEndpoint = "https",
            CorrelationIds = new Dictionary<string, string> { ["traceId"] = "abc123" },
        };

        // Act
        var line = EventStreamJson.ToLine(evt);
        var back = EventStreamJson.FromLine<TransportNoticeEvent>(line);

        // Assert
        Assert.Equal(1, back.Version);
        Assert.Equal("v1", back.SchemaVersion);
        Assert.Equal(EventTypes.TransportNotice, back.Type);
        Assert.Equal(evt.Timestamp, back.Timestamp);
        Assert.Equal("run-001", back.RunId);
        Assert.Equal(TransportNoticeKinds.PlaintextDowngrade, back.Kind);
        Assert.Equal("orders-api", back.Service);
        Assert.Equal("http", back.SelectedEndpoint);
        Assert.Equal("https", back.RejectedEndpoint);
        Assert.NotNull(back.CorrelationIds);
        Assert.Equal("abc123", back.CorrelationIds!["traceId"]);
    }

    [Fact]
    public void NoEngineTrust_RoundTrip_PreservesAllFields()
    {
        // Arrange — nothing was rejected, so RejectedEndpoint is left unset.
        var evt = new TransportNoticeEvent
        {
            RunId = "run-002",
            Timestamp = new DateTimeOffset(2026, 8, 29, 11, 0, 0, TimeSpan.Zero),
            Kind = TransportNoticeKinds.NoEngineTrust,
            Service = "payments-api",
            SelectedEndpoint = "https",
        };

        // Act
        var line = EventStreamJson.ToLine(evt);
        var back = EventStreamJson.FromLine<TransportNoticeEvent>(line);

        // Assert
        Assert.Equal(EventTypes.TransportNotice, back.Type);
        Assert.Equal("run-002", back.RunId);
        Assert.Equal(TransportNoticeKinds.NoEngineTrust, back.Kind);
        Assert.Equal("payments-api", back.Service);
        Assert.Equal("https", back.SelectedEndpoint);
        Assert.Null(back.RejectedEndpoint);
        Assert.Null(back.CorrelationIds);
    }

    // =========================================================================
    // Wire shape
    // =========================================================================

    [Fact]
    public void WireNames_AreFlatAndExact()
    {
        // Arrange
        var evt = new TransportNoticeEvent
        {
            RunId = "run-003",
            Kind = TransportNoticeKinds.PlaintextDowngrade,
            Service = "orders-api",
            SelectedEndpoint = "http",
            RejectedEndpoint = "https",
        };

        // Act
        var line = EventStreamJson.ToLine(evt);

        // Assert — every field is a sibling of the envelope fields at the root.
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("transport-notice", root.GetProperty("type").GetString());
        Assert.Equal("run-003", root.GetProperty("runId").GetString());
        Assert.Equal("plaintext-downgrade", root.GetProperty("kind").GetString());
        Assert.Equal("orders-api", root.GetProperty("service").GetString());
        Assert.Equal("http", root.GetProperty("selectedEndpoint").GetString());
        Assert.Equal("https", root.GetProperty("rejectedEndpoint").GetString());

        Assert.False(
            root.TryGetProperty("payload", out _),
            "Wire shape must be flat — no nested payload object");
        Assert.False(
            root.TryGetProperty("scenarioId", out _),
            "The advisory is a property of the topology a run built, not of a scenario; "
            + "one topology can serve many scenarios.");
    }

    [Fact]
    public void NoEngineTrust_RejectedEndpoint_AbsentFromWire()
    {
        // Arrange — the kind for which no endpoint was rejected.
        var evt = new TransportNoticeEvent
        {
            RunId = "run-004",
            Kind = TransportNoticeKinds.NoEngineTrust,
            Service = "payments-api",
            SelectedEndpoint = "https",
        };

        // Act
        var line = EventStreamJson.ToLine(evt);

        // Assert — omitted entirely, not written as null. The shared serialiser's
        // DefaultIgnoreCondition = WhenWritingNull does this; the record deliberately
        // carries no [JsonIgnore], so this test is what proves the option is doing it.
        using var doc = JsonDocument.Parse(line);
        Assert.False(
            doc.RootElement.TryGetProperty("rejectedEndpoint", out _),
            "Null RejectedEndpoint must be omitted from the wire, not emitted as null.");
        Assert.DoesNotContain("rejectedEndpoint", line, StringComparison.Ordinal);

        // …and the optional envelope field behaves the same way.
        Assert.False(doc.RootElement.TryGetProperty("correlationIds", out _));
    }

    [Fact]
    public void TypeDefault_MatchesEventTypesConst()
    {
        var evt = new TransportNoticeEvent
        {
            RunId = "r",
            Kind = TransportNoticeKinds.NoEngineTrust,
            Service = "s",
            SelectedEndpoint = "https",
        };

        Assert.Equal(EventTypes.TransportNotice, evt.Type);
        Assert.Equal("transport-notice", evt.Type);
    }

    [Fact]
    public void ToLine_ContainsNoEmbeddedNewlines()
    {
        // JSON Lines mandate: one compact object per line.
        var line = EventStreamJson.ToLine(new TransportNoticeEvent
        {
            RunId = "r",
            Kind = TransportNoticeKinds.PlaintextDowngrade,
            Service = "s",
            SelectedEndpoint = "http",
            RejectedEndpoint = "https",
        });

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        using var doc = JsonDocument.Parse(line);
        _ = doc;
    }

    // =========================================================================
    // Forward compatibility — an envelope-only renderer still sees everything
    // =========================================================================

    [Fact]
    public void DeserialiseAsEnvelope_PayloadFieldsInExtra()
    {
        // Arrange — this is what an older renderer that predates the record does.
        var line = EventStreamJson.ToLine(new TransportNoticeEvent
        {
            RunId = "run-005",
            Kind = TransportNoticeKinds.PlaintextDowngrade,
            Service = "orders-api",
            SelectedEndpoint = "http",
            RejectedEndpoint = "https",
        });

        // Act
        var envelope = EventStreamJson.FromLine(line);

        // Assert — the envelope fields bind, and the payload survives in Extra.
        Assert.Equal("transport-notice", envelope.Type);
        Assert.Equal("run-005", envelope.RunId);
        Assert.NotNull(envelope.Extra);
        Assert.Equal("plaintext-downgrade", envelope.Extra!["kind"].GetString());
        Assert.Equal("orders-api", envelope.Extra["service"].GetString());
        Assert.Equal("http", envelope.Extra["selectedEndpoint"].GetString());
        Assert.Equal("https", envelope.Extra["rejectedEndpoint"].GetString());
    }

    // =========================================================================
    // The kind vocabulary
    // =========================================================================

    /// <summary>
    /// The value-freeze for the <c>kind</c> vocabulary.  The event-contract golden pins
    /// property names and CLR types, never string VALUES, and its completeness guard
    /// only reaches records — so a renamed token produces no golden diff and this test
    /// is the only thing that reddens.  Spelled as literals, because a test comparing
    /// the constants against themselves would pin nothing.
    /// </summary>
    [Fact]
    public void Kinds_AreTheTwoPinnedTokens()
    {
        Assert.Equal("plaintext-downgrade", TransportNoticeKinds.PlaintextDowngrade);
        Assert.Equal("no-engine-trust", TransportNoticeKinds.NoEngineTrust);
    }

    [Theory]
    [InlineData("plaintext-downgrade", true)]
    [InlineData("no-engine-trust", true)]
    [InlineData("Plaintext-Downgrade", false)] // ordinal: case is not folded
    [InlineData("PLAINTEXT-DOWNGRADE", false)]
    [InlineData("plaintext_downgrade", false)]
    [InlineData("some-future-advisory", false)] // a newer engine's token: unknown, not invalid
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKnown_MatchesOrdinally(string? kind, bool expected) =>
        Assert.Equal(expected, TransportNoticeKinds.IsKnown(kind));

    [Fact]
    public void UnknownKind_IsCarriedVerbatim_NotRejected()
    {
        // A stream from a newer engine carrying a third advisory must deserialise —
        // §14 requires renderers to tolerate what they do not recognise. The record
        // must not make the two-value vocabulary structural.
        const string line =
            """
            {"v":1,"schemaVersion":"v1","type":"transport-notice","ts":"2026-08-29T10:30:00+00:00","runId":"run-006","kind":"some-future-advisory","service":"orders-api","selectedEndpoint":"http"}
            """;

        var back = EventStreamJson.FromLine<TransportNoticeEvent>(line);

        Assert.Equal("some-future-advisory", back.Kind);
        Assert.False(TransportNoticeKinds.IsKnown(back.Kind));
        Assert.Equal("orders-api", back.Service);
    }

    // =========================================================================
    // Tolerance covers the vocabulary, not the field set
    // =========================================================================

    [Theory]
    [InlineData("kind")]
    [InlineData("service")]
    [InlineData("selectedEndpoint")]
    [InlineData("runId")]
    public void RequiredField_Missing_ThrowsRatherThanDegrading(string omitted)
    {
        // The four required members are NOT tolerated as absent — this is the measured
        // consequence of `required`, and it is why a future advisory that is not
        // endpoint-scoped cannot reuse this record unchanged. Relaxing one later is
        // wire-compatible; it is simply not an additive change, and this test says so.
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runId"] = "run-007",
            ["kind"] = TransportNoticeKinds.NoEngineTrust,
            ["service"] = "payments-api",
            ["selectedEndpoint"] = "https",
        };
        fields.Remove(omitted);

        var line = "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"transport-notice\","
            + string.Join(',', fields.Select(f => $"\"{f.Key}\":\"{f.Value}\""))
            + "}";

        Assert.Throws<JsonException>(() => EventStreamJson.FromLine<TransportNoticeEvent>(line));
    }

    // =========================================================================
    // replayed — the --watch staleness qualifier
    // =========================================================================

    [Fact]
    public void Replayed_RoundTrips_WhenSet()
    {
        var evt = new TransportNoticeEvent
        {
            RunId = "run-008",
            Kind = TransportNoticeKinds.PlaintextDowngrade,
            Service = "orders-api",
            SelectedEndpoint = "http",
            RejectedEndpoint = "https",
            Replayed = true,
        };

        var line = EventStreamJson.ToLine(evt);
        var back = EventStreamJson.FromLine<TransportNoticeEvent>(line);

        using var doc = JsonDocument.Parse(line);
        Assert.True(doc.RootElement.GetProperty("replayed").GetBoolean());
        Assert.True(back.Replayed);
    }

    [Fact]
    public void Replayed_Unset_AbsentFromWire_NotFalse()
    {
        // The fresh-build paths leave it unset. A defaulted non-nullable bool would
        // write "replayed":false onto every record ever emitted; the nullable declaration
        // plus DefaultIgnoreCondition = WhenWritingNull omits it. Proving the omission is
        // the point of this test — the same guarantee rejectedEndpoint relies on.
        var line = EventStreamJson.ToLine(new TransportNoticeEvent
        {
            RunId = "run-009",
            Kind = TransportNoticeKinds.PlaintextDowngrade,
            Service = "orders-api",
            SelectedEndpoint = "http",
            RejectedEndpoint = "https",
        });

        using var doc = JsonDocument.Parse(line);
        Assert.False(
            doc.RootElement.TryGetProperty("replayed", out _),
            "An unset Replayed must be omitted from the wire, not emitted as false.");
        Assert.DoesNotContain("replayed", line, StringComparison.Ordinal);

        Assert.Null(EventStreamJson.FromLine<TransportNoticeEvent>(line).Replayed);
    }

    [Fact]
    public void Replayed_False_IsWrittenExplicitly()
    {
        // WhenWritingNull omits nulls, not falses: an explicit false survives the wire.
        // Producers therefore say "not a replay" by leaving it unset, never by setting
        // false — which is what keeps the two fresh-build paths byte-identical to today.
        var line = EventStreamJson.ToLine(new TransportNoticeEvent
        {
            RunId = "run-010",
            Kind = TransportNoticeKinds.PlaintextDowngrade,
            Service = "orders-api",
            SelectedEndpoint = "http",
            Replayed = false,
        });

        using var doc = JsonDocument.Parse(line);
        Assert.True(doc.RootElement.TryGetProperty("replayed", out var replayed));
        Assert.False(replayed.GetBoolean());
    }
}
