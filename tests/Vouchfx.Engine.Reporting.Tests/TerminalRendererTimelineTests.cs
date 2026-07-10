// Golden-output tests for the RETRY polling timeline renderer (S08 T8 / S08-G-01).
//
// A step run under verifyMode: RETRY emits one step-attempt event per poll.  The
// renderer must surface those attempts as a legible, indented "polling timeline"
// grouped under its step (docs/01 §14.5): one line per poll attempt carrying the
// attempt number, the elapsed time relative to step start, and the per-attempt
// outcome.  This is the single most distinguishing feature of the report against
// the existing testing-tool market, so the rendering is proven by golden output.
//
// Strategy mirrors TerminalRendererDiffTests: build an event-line buffer from
// typed payload records via EventStreamJson.ToLine<T>, render to a StringWriter,
// and assert on the textual output.  The renderer is single-pass over an ordered
// stream; the attempt-timeline rendering itself is STATELESS — each timeline line
// is produced solely from its own step-attempt event's fields — which is exactly
// why interleaved attempts from different runs never cross-contaminate (the only
// per-step state the renderer keeps is the diff-lookup kind map, keyed by
// (runId, stepId), which the timeline lines do not consult).  The input order here
// matches the engine's emission order: step-started → step-attempt* → step-completed.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

public sealed class TerminalRendererTimelineTests
{
    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── A RETRY step renders one timeline line per poll attempt, in order ──────
    //
    // Six polls over a 30s window that ends in a timeout (Inconclusive), mirroring
    // the canonical §14.5 example.  Each attempt must produce its own legible line
    // beneath the step, carrying the attempt number, the elapsed time, and the
    // per-attempt outcome.

    [Fact]
    public void Render_RetryStep_RendersAttemptByAttemptTimeline()
    {
        var lines = new[]
        {
            Line(new StepStartedEvent
            {
                RunId      = "run-1",
                StepId     = "expect-billing-event",
                Kind       = "mq-expect.kafka",
                VerifyMode = "RETRY",
                TimeoutMs  = 30000,
            }),
            Line(new StepAttemptEvent { RunId = "run-1", StepId = "expect-billing-event", Attempt = 1, TMs =   540, Outcome = Verdict.Inconclusive }),
            Line(new StepAttemptEvent { RunId = "run-1", StepId = "expect-billing-event", Attempt = 2, TMs =  1530, Outcome = Verdict.Fail }),
            Line(new StepAttemptEvent { RunId = "run-1", StepId = "expect-billing-event", Attempt = 3, TMs =  4000, Outcome = Verdict.Inconclusive }),
            Line(new StepAttemptEvent { RunId = "run-1", StepId = "expect-billing-event", Attempt = 4, TMs = 10000, Outcome = Verdict.Inconclusive }),
            Line(new StepAttemptEvent { RunId = "run-1", StepId = "expect-billing-event", Attempt = 5, TMs = 20000, Outcome = Verdict.Inconclusive }),
            Line(new StepAttemptEvent { RunId = "run-1", StepId = "expect-billing-event", Attempt = 6, TMs = 30000, Outcome = Verdict.Inconclusive }),
            Line(new StepCompletedEvent
            {
                RunId      = "run-1",
                StepId     = "expect-billing-event",
                Verdict    = Verdict.Inconclusive,
                DurationMs = 30024,
            }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The step header and completion verdict are present.
        Assert.Contains("expect-billing-event", output, StringComparison.Ordinal);
        Assert.Contains("INCONCLUSIVE", output, StringComparison.Ordinal);

        // Every poll attempt produces its own line.
        for (var n = 1; n <= 6; n++)
        {
            Assert.Contains($"attempt {n}", output, StringComparison.Ordinal);
        }

        // The timeline shows elapsed time relative to step start, in seconds.
        // 540 ms → 0.5s, 1530 ms → 1.5s, 30000 ms → 30.0s.
        Assert.Contains("t=", output, StringComparison.Ordinal);
        Assert.Contains("0.5s", output, StringComparison.Ordinal);
        Assert.Contains("1.5s", output, StringComparison.Ordinal);
        Assert.Contains("30.0s", output, StringComparison.Ordinal);

        // The per-attempt outcome is shown (attempt 2 differed — it was a FAIL poll).
        Assert.Contains("FAIL", output, StringComparison.Ordinal);

        // The timeline lines are indented beneath the step, in attempt order.
        var timelineLines = output
            .Split('\n')
            .Where(l => l.Contains("attempt ", StringComparison.Ordinal)
                        && l.Contains("t=", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(6, timelineLines.Length);
        for (var i = 0; i < timelineLines.Length; i++)
        {
            // Each timeline line is indented (grouped under its step line).
            Assert.StartsWith(" ", timelineLines[i], StringComparison.Ordinal);
            // …and in ascending attempt order.
            Assert.Contains($"attempt {i + 1}", timelineLines[i], StringComparison.Ordinal);
        }

        // The attempt-N line precedes the step-N+1 line in the rendered output:
        // attempt 1's index is before attempt 6's index, before the completion line.
        var idxAttempt1 = output.IndexOf("attempt 1", StringComparison.Ordinal);
        var idxAttempt6 = output.IndexOf("attempt 6", StringComparison.Ordinal);
        var idxComplete = output.LastIndexOf("INCONCLUSIVE", StringComparison.Ordinal);
        Assert.True(idxAttempt1 < idxAttempt6, "attempt 1 must render before attempt 6.");
        Assert.True(idxAttempt6 < idxComplete, "the attempts must render before the completion verdict.");
    }

    // ── An attempt whose outcome is not yet resolved renders without throwing ──
    //
    // A mid-RETRY poll may carry a null outcome (the engine has not resolved an
    // outcome for that cycle yet).  The timeline line must still render its attempt
    // number and elapsed time, with a legible pending marker rather than a verdict.

    [Fact]
    public void Render_RetryAttempt_NullOutcome_RendersPendingMarker()
    {
        var lines = new[]
        {
            Line(new StepStartedEvent
            {
                RunId      = "run-2",
                StepId     = "poll-status",
                Kind       = "mq-expect.kafka",
                VerifyMode = "RETRY",
            }),
            // No Outcome → omitted from the wire (DefaultIgnoreCondition).
            Line(new StepAttemptEvent { RunId = "run-2", StepId = "poll-status", Attempt = 1, TMs = 250 }),
            Line(new StepCompletedEvent
            {
                RunId      = "run-2",
                StepId     = "poll-status",
                Verdict    = Verdict.Pass,
                DurationMs = 300,
            }),
        };

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(lines, writer));
        Assert.Null(ex);

        var output = writer.ToString();
        Assert.Contains("attempt 1", output, StringComparison.Ordinal);
        // 250 ms renders as elapsed seconds (one decimal place) on the timeline.
        Assert.Contains("0.3s", output, StringComparison.Ordinal);
        // A pending marker stands in for the unresolved outcome.
        Assert.Contains("(pending)", output, StringComparison.Ordinal);
    }

    // ── Interleaved multi-run stream: a shared step id keeps its own timeline ──
    //
    // Two runs reuse the same step id "poll".  The renderer keys per-step timeline
    // state by (runId, stepId), so run A's attempts group under run A's step and
    // run B's under run B's — they must not cross-contaminate.

    [Fact]
    public void Render_InterleavedRunsSharingStepId_KeepSeparateTimelines()
    {
        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "A", StepId = "poll", Kind = "mq-expect.kafka", VerifyMode = "RETRY" }),
            Line(new StepStartedEvent { RunId = "B", StepId = "poll", Kind = "mq-expect.kafka", VerifyMode = "RETRY" }),
            Line(new StepAttemptEvent { RunId = "A", StepId = "poll", Attempt = 1, TMs = 100, Outcome = Verdict.Inconclusive }),
            Line(new StepAttemptEvent { RunId = "B", StepId = "poll", Attempt = 1, TMs = 200, Outcome = Verdict.Inconclusive }),
            Line(new StepAttemptEvent { RunId = "A", StepId = "poll", Attempt = 2, TMs = 300, Outcome = Verdict.Pass }),
            Line(new StepCompletedEvent { RunId = "A", StepId = "poll", Verdict = Verdict.Pass, DurationMs = 320 }),
            Line(new StepCompletedEvent { RunId = "B", StepId = "poll", Verdict = Verdict.Fail, DurationMs = 250 }),
        };

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(lines, writer));
        Assert.Null(ex);

        var output = writer.ToString();

        // Run A had two attempts; run B had one.  All three attempt lines render.
        var attemptLines = output
            .Split('\n')
            .Count(l => l.Contains("attempt ", StringComparison.Ordinal)
                        && l.Contains("t=", StringComparison.Ordinal));
        Assert.Equal(3, attemptLines);

        // Run A's 0.3s (attempt 2) and run B's 0.2s (attempt 1) both appear — neither
        // run's timeline swallowed the other's.
        Assert.Contains("0.1s", output, StringComparison.Ordinal);
        Assert.Contains("0.2s", output, StringComparison.Ordinal);
        Assert.Contains("0.3s", output, StringComparison.Ordinal);
    }

    // ── Back-compat: an IMMEDIATE single-attempt step still renders cleanly ────
    //
    // An IMMEDIATE step emits exactly one step-attempt; the timeline rendering must
    // not regress the existing single-attempt output (the verdict still appears).

    [Fact]
    public void Render_ImmediateSingleAttempt_RendersWithoutTimelineNoise()
    {
        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "run-3", StepId = "ping", Kind = "http.rest", VerifyMode = "IMMEDIATE" }),
            Line(new StepAttemptEvent { RunId = "run-3", StepId = "ping", Attempt = 1, TMs = 42, Outcome = Verdict.Pass }),
            Line(new StepCompletedEvent { RunId = "run-3", StepId = "ping", Verdict = Verdict.Pass, DurationMs = 42 }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        Assert.Contains("ping", output, StringComparison.Ordinal);
        Assert.Contains("attempt 1", output, StringComparison.Ordinal);
        Assert.Contains("PASS", output, StringComparison.Ordinal);
    }

    // ── An OBJECT observation renders keys-only (shape hint, no values) ────────
    //
    // The keys-only secret-safety invariant: an object observation on an attempt
    // surfaces only its property NAMES on the timeline, never their values.  This
    // is the established behaviour and must not regress.

    [Fact]
    public void Render_ObjectObservation_SurfacesKeysOnlyNeverValues()
    {
        const string sensitiveValue = "leaked-secret-value-must-not-appear";
        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "run-4", StepId = "expect", Kind = "mq-expect.kafka", VerifyMode = "RETRY" }),
            Line(new StepAttemptEvent
            {
                RunId       = "run-4",
                StepId      = "expect",
                Attempt     = 1,
                TMs         = 500,
                Outcome     = Verdict.Inconclusive,
                // An object observation: keys are metadata; the raw value must NOT print.
                Observation = Parse($"{{\"matched\":0,\"raw\":\"{sensitiveValue}\"}}"),
            }),
            Line(new StepCompletedEvent { RunId = "run-4", StepId = "expect", Verdict = Verdict.Inconclusive, DurationMs = 520 }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The keys appear; the values do not.
        Assert.Contains("matched", output, StringComparison.Ordinal);
        Assert.Contains("raw", output, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveValue, output, StringComparison.Ordinal);
    }

    // ── A SCALAR observation renders as a SHAPE HINT, never verbatim (§17) ─────
    //
    // No Core provider emits a bare-scalar observation today, but the
    // StepAttemptEvent.Observation contract advertises "raw response" as a
    // legitimate payload — so a future provider could place a sensitive scalar
    // there.  The keys-only invariant is TOTAL: a scalar observation is summarised
    // by its JSON value-kind (<string>/<number>/<bool>), never by its literal
    // value, so the polling timeline can never print that value.

    [Fact]
    public void Render_ScalarStringObservation_RendersShapeHintNotValue()
    {
        const string sensitiveScalar = "sk-live-deadbeef-must-not-appear-on-timeline";
        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "run-5", StepId = "poll", Kind = "mq-expect.kafka", VerifyMode = "RETRY" }),
            Line(new StepAttemptEvent
            {
                RunId       = "run-5",
                StepId      = "poll",
                Attempt     = 1,
                TMs         = 250,
                Outcome     = Verdict.Inconclusive,
                // A bare-scalar string observation (the "raw response" the contract allows).
                Observation = Parse($"\"{sensitiveScalar}\""),
            }),
            Line(new StepCompletedEvent { RunId = "run-5", StepId = "poll", Verdict = Verdict.Inconclusive, DurationMs = 260 }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The attempt line renders; the scalar's VALUE never does — only its shape.
        Assert.Contains("attempt 1", output, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveScalar, output, StringComparison.Ordinal);
        Assert.Contains("<string>", output, StringComparison.Ordinal);
    }

    // A numeric scalar observation likewise renders only its value-kind, not the number.

    [Fact]
    public void Render_ScalarNumberObservation_RendersShapeHintNotValue()
    {
        // A distinctive number that would be unmistakable if printed verbatim.
        const string distinctiveNumber = "8675309";
        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "run-6", StepId = "poll", Kind = "mq-expect.kafka", VerifyMode = "RETRY" }),
            Line(new StepAttemptEvent
            {
                RunId       = "run-6",
                StepId      = "poll",
                Attempt     = 1,
                TMs         = 250,
                Outcome     = Verdict.Inconclusive,
                Observation = Parse(distinctiveNumber),
            }),
            Line(new StepCompletedEvent { RunId = "run-6", StepId = "poll", Verdict = Verdict.Inconclusive, DurationMs = 260 }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        Assert.Contains("attempt 1", output, StringComparison.Ordinal);
        Assert.DoesNotContain(distinctiveNumber, output, StringComparison.Ordinal);
        Assert.Contains("<number>", output, StringComparison.Ordinal);
    }
}
