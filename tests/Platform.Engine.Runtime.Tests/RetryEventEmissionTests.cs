// Tests for the engine-owned RETRY step-attempt event emission (Sprint 6).
//
// These exercise the extracted ScenarioRunner helpers directly, with NO Docker and
// NO topology:
//   • BuildAttemptEventLines — turns the per-step List<AttemptRecord> written by the
//     RETRY runner into one step-attempt JSON Lines event per record, in order.
//   • ParseObservation — parses the small per-attempt observation JSON string into a
//     JsonElement, degrading null/empty/invalid input to null rather than throwing.
//
// The helpers are internal; the test project sees them via InternalsVisibleTo
// (Platform.Engine.Runtime.csproj already grants it — RunSuiteAsyncTests calls the
// internal ScenarioRunner.Elevate).

using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Retry;
using Platform.Engine.Runtime;
using Xunit;

namespace Platform.Engine.Runtime.Tests;

/// <summary>
/// Non-docker unit tests for the RETRY step-attempt event helpers extracted from
/// <see cref="ScenarioRunner"/> (Sprint 6).
/// </summary>
public sealed class RetryEventEmissionTests
{
    private const string RunId = "run-abc";
    private static readonly DateTimeOffset Ts =
        new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    // ── BuildAttemptEventLines ───────────────────────────────────────────────

    /// <summary>
    /// A <c>vars</c> dictionary carrying a two-element <see cref="AttemptRecord"/>
    /// list (under the sanitised-step-id attempts key) yields exactly two
    /// <c>step-attempt</c> event lines, in list order, with the un-sanitised step
    /// id on the wire, the correct attempt ordinals and outcome tokens, and the
    /// observation object round-tripped.
    /// </summary>
    [Fact]
    public void BuildAttemptEventLines_TwoAttempts_EmitsTwoOrderedEvents()
    {
        // The step id has a hyphen so the test also proves the helper sanitises
        // it for the Vars lookup while emitting the original id on the wire.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Attempts("poll_step")] = new List<AttemptRecord>
            {
                new(1, 5, Verdict.Fail, "{\"matched\":false}"),
                new(2, 7, Verdict.Pass, "{\"matched\":true}"),
            },
        };

        var lines = ScenarioRunner.BuildAttemptEventLines(RunId, Ts, "poll-step", vars);

        Assert.Equal(2, lines.Count);

        // ── First attempt: Fail, matched=false ───────────────────────────────
        using (var doc = JsonDocument.Parse(lines[0]))
        {
            var root = doc.RootElement;
            Assert.Equal("step-attempt", root.GetProperty("type").GetString());
            Assert.Equal(RunId, root.GetProperty("runId").GetString());
            Assert.Equal("poll-step", root.GetProperty("stepId").GetString());
            Assert.Equal(1, root.GetProperty("attempt").GetInt32());
            Assert.Equal(5, root.GetProperty("tMs").GetInt64());
            Assert.Equal("FAIL", root.GetProperty("outcome").GetString());
            Assert.False(root.GetProperty("observation").GetProperty("matched").GetBoolean());
        }

        // ── Second attempt: Pass, matched=true ───────────────────────────────
        using (var doc = JsonDocument.Parse(lines[1]))
        {
            var root = doc.RootElement;
            Assert.Equal("step-attempt", root.GetProperty("type").GetString());
            Assert.Equal("poll-step", root.GetProperty("stepId").GetString());
            Assert.Equal(2, root.GetProperty("attempt").GetInt32());
            Assert.Equal(7, root.GetProperty("tMs").GetInt64());
            Assert.Equal("PASS", root.GetProperty("outcome").GetString());
            Assert.True(root.GetProperty("observation").GetProperty("matched").GetBoolean());
        }
    }

    /// <summary>
    /// When the <c>vars</c> dictionary carries no attempts entry for the step
    /// (the IMMEDIATE case), the helper returns an empty list — no step-attempt
    /// events are emitted.
    /// </summary>
    [Fact]
    public void BuildAttemptEventLines_NoAttemptsKey_ReturnsEmpty()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);

        var lines = ScenarioRunner.BuildAttemptEventLines(RunId, Ts, "immediate-step", vars);

        Assert.Empty(lines);
    }

    // ── ParseObservation ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ScenarioRunner.ParseObservation"/> returns <see langword="null"/>
    /// for a null, empty, or syntactically invalid observation string — the
    /// observation is best-effort diagnostic context and must never crash emission.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void ParseObservation_NullEmptyOrInvalid_ReturnsNull(string? json)
    {
        Assert.Null(ScenarioRunner.ParseObservation(json));
    }

    /// <summary>
    /// <see cref="ScenarioRunner.ParseObservation"/> parses a valid JSON object
    /// into a <see cref="JsonElement"/> whose members are readable.
    /// </summary>
    [Fact]
    public void ParseObservation_ValidJson_ReturnsReadableElement()
    {
        var element = ScenarioRunner.ParseObservation("{\"k\":1}");

        Assert.NotNull(element);
        Assert.Equal(JsonValueKind.Object, element.Value.ValueKind);
        Assert.Equal(1, element.Value.GetProperty("k").GetInt32());
    }
}
