// Tests for the additive diff-lookup overload of TerminalRenderer.Render (S07-G-01).
//
// The new overload accepts an optional diff-lookup delegate
// (Func<string kind, JsonElement observation, string?>).  When a step-completed
// event has verdict Fail and carries a structured observation, the renderer calls
// the delegate and, if it returns a non-null string, splices it under the step line.
//
// Decoupling invariant: the delegate is a plain Func over JsonElement, so this test
// project does NOT reference Platform.Sdk or any IStepDiffRenderer type — it stubs
// the lookup inline.

using System.IO;
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Reporting;
using Xunit;

namespace Platform.Engine.Reporting.Tests;

public sealed class TerminalRendererDiffTests
{
    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private const string DiffMarker = "<<RENDERED-DIFF-MARKER>>";

    // ── A Fail step with an observation invokes the lookup and renders its result ──

    [Fact]
    public void Render_WithDiffLookup_FailStepWithObservation_RendersDiff()
    {
        // The kind threads in via the step-started event; the observation rides on
        // the step-completed event.
        var lines = new[]
        {
            Line(new StepStartedEvent
            {
                RunId  = "run-1",
                StepId = "assert-order",
                Kind   = "db-assert.postgres",
            }),
            Line(new StepCompletedEvent
            {
                RunId       = "run-1",
                StepId      = "assert-order",
                Verdict     = Verdict.Fail,
                DurationMs  = 12,
                Observation = Parse("""{"column":"status","expected":"SHIPPED","actual":"PENDING"}"""),
            }),
        };

        string? capturedKind = null;
        Func<string, JsonElement, string?> lookup = (kind, _) =>
        {
            capturedKind = kind;
            return DiffMarker;
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer, lookup);
        var output = writer.ToString();

        Assert.Contains("assert-order", output, StringComparison.Ordinal);
        Assert.Contains("FAIL", output, StringComparison.Ordinal);
        // The lookup's returned diff text must be spliced into the output.
        Assert.Contains(DiffMarker, output, StringComparison.Ordinal);
        // The kind must be threaded through from the step-started event.
        Assert.Equal("db-assert.postgres", capturedKind);
    }

    // ── A passing step never invokes the lookup ───────────────────────────────

    [Fact]
    public void Render_WithDiffLookup_PassStep_DoesNotRenderDiff()
    {
        var lines = new[]
        {
            Line(new StepStartedEvent
            {
                RunId  = "run-2",
                StepId = "assert-order",
                Kind   = "db-assert.postgres",
            }),
            Line(new StepCompletedEvent
            {
                RunId       = "run-2",
                StepId      = "assert-order",
                Verdict     = Verdict.Pass,
                DurationMs  = 8,
                Observation = Parse("""{"rowCount":1}"""),
            }),
        };

        var invoked = false;
        Func<string, JsonElement, string?> lookup = (_, _) =>
        {
            invoked = true;
            return DiffMarker;
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer, lookup);
        var output = writer.ToString();

        Assert.DoesNotContain(DiffMarker, output, StringComparison.Ordinal);
        Assert.False(invoked, "Diff lookup must not be called for a passing step.");
    }

    // ── A lookup returning null leaves the step line unadorned ─────────────────

    [Fact]
    public void Render_WithDiffLookup_LookupReturnsNull_NoDiffRendered()
    {
        var lines = new[]
        {
            Line(new StepStartedEvent
            {
                RunId  = "run-3",
                StepId = "assert-order",
                Kind   = "db-assert.postgres",
            }),
            Line(new StepCompletedEvent
            {
                RunId       = "run-3",
                StepId      = "assert-order",
                Verdict     = Verdict.Fail,
                DurationMs  = 5,
                Observation = Parse("""{"error":"boom"}"""),
            }),
        };

        Func<string, JsonElement, string?> lookup = (_, _) => null;

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer, lookup);
        var output = writer.ToString();

        // Step line still renders; no diff text since lookup returned null.
        Assert.Contains("assert-order", output, StringComparison.Ordinal);
        Assert.Contains("FAIL", output, StringComparison.Ordinal);
        Assert.DoesNotContain(DiffMarker, output, StringComparison.Ordinal);
    }

    // ── A Fail step with no observation never invokes the lookup ───────────────

    [Fact]
    public void Render_WithDiffLookup_FailStepWithoutObservation_DoesNotInvokeLookup()
    {
        var lines = new[]
        {
            Line(new StepCompletedEvent
            {
                RunId      = "run-4",
                StepId     = "assert-order",
                Verdict    = Verdict.Fail,
                DurationMs = 5,
                // No Observation.
            }),
        };

        var invoked = false;
        Func<string, JsonElement, string?> lookup = (_, _) =>
        {
            invoked = true;
            return DiffMarker;
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer, lookup);

        Assert.False(invoked, "Diff lookup must not be called when there is no observation.");
    }

    // ── Interleaved multi-RUN stream: kind must be keyed by (runId, stepId) ────────
    //
    // Two runs ("A", "B") share the same stepId ("s1") but declare different kinds
    // (A = db-assert.postgres, B = http.rest).  The events arrive interleaved:
    //   A:step-started(s1), B:step-started(s1),
    //   A:step-completed(s1, FAIL), B:step-completed(s1, FAIL).
    // With a stepId-only cache, B's step-started overwrites the kind for "s1", so A's
    // step-completed resolves B's kind (http.rest) — the wrong diff.  Keying by
    // (runId, stepId) keeps the two runs' kinds separate.

    [Fact]
    public void Render_InterleavedRunsSharingStepId_ResolveTheirOwnKind()
    {
        // Each run's observation carries a distinct marker so the lookup can record
        // which kind was resolved for which run.
        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "A", StepId = "s1", Kind = "db-assert.postgres" }),
            Line(new StepStartedEvent { RunId = "B", StepId = "s1", Kind = "http.rest" }),
            Line(new StepCompletedEvent
            {
                RunId       = "A",
                StepId      = "s1",
                Verdict     = Verdict.Fail,
                DurationMs  = 10,
                Observation = Parse("""{"run":"A"}"""),
            }),
            Line(new StepCompletedEvent
            {
                RunId       = "B",
                StepId      = "s1",
                Verdict     = Verdict.Fail,
                DurationMs  = 11,
                Observation = Parse("""{"run":"B"}"""),
            }),
        };

        // Record the (run -> kind) pairing the renderer resolved at each diff call.
        var resolvedKindByRun = new Dictionary<string, string>(StringComparer.Ordinal);
        Func<string, JsonElement, string?> lookup = (kind, observation) =>
        {
            var run = observation.GetProperty("run").GetString()!;
            resolvedKindByRun[run] = kind;
            return DiffMarker;
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer, lookup);

        // Run A's FAIL diff must use A's kind; run B's must use B's kind.  With the
        // stepId-only cache, A resolves http.rest (B's later overwrite) and this fails.
        Assert.Equal("db-assert.postgres", resolvedKindByRun["A"]);
        Assert.Equal("http.rest", resolvedKindByRun["B"]);
    }

    // ── Back-compat: the diff overload with null lookup matches the 2-arg overload ──

    [Fact]
    public void Render_DiffOverloadWithNullLookup_MatchesTwoArgOverload()
    {
        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-5", ScenarioId = "flow" }),
            Line(new StepStartedEvent { RunId = "run-5", StepId = "s1", Kind = "db-assert.postgres" }),
            Line(new StepCompletedEvent
            {
                RunId       = "run-5",
                StepId      = "s1",
                Verdict     = Verdict.Fail,
                DurationMs  = 12,
                Observation = Parse("""{"column":"status","expected":"A","actual":"B"}"""),
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId      = "run-5",
                ScenarioId = "flow",
                Verdict    = Verdict.Fail,
                Counts     = new VerdictCounts { Fail = 1 },
            }),
        };

        using var twoArg = new StringWriter();
        TerminalRenderer.Render(lines, twoArg);

        using var threeArgNull = new StringWriter();
        TerminalRenderer.Render(lines, threeArgNull, diffLookup: null);

        // With a null lookup the new overload must be byte-for-byte identical to the
        // existing 2-arg overload (no diff spliced).
        Assert.Equal(twoArg.ToString(), threeArgNull.ToString());
    }
}
