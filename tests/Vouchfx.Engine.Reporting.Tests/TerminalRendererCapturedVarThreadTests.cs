// Golden-output tests for the captured-variable provenance thread renderer
// (S08 T9 / S08-G-02).
//
// A value captured by one step and substituted into a later step forms a
// provenance thread: "this value came from step A's $.id and was used as
// {orderId} in step B".  The renderer must surface that thread so an author can
// trace a value end-to-end (docs/01 §14, the capture/substitution provenance
// metadata frozen by T3 on the step-completed event).  This is rendered purely
// from the EXISTING StepCompletedEvent.Captured / .Substitutions fields — no
// event field is added or changed (the T3 wire-contract freeze must stay green).
//
// §17 secret-safety is the load-bearing invariant here: a SubstitutionRef whose
// SecretDerived flag is true carries the non-sensitive secret REFERENCE label in
// its Placeholder (never the value), and the thread must render a redaction
// marker for it — never reveal a value.  The event stream never carries the
// value anyway; the rendering must still make the secret-derived provenance
// VISIBLE without it.
//
// Strategy mirrors TerminalRendererTimelineTests / TerminalRendererDiffTests:
// build an event-line buffer from typed payload records via
// EventStreamJson.ToLine<T>, render to a StringWriter, and assert on the text.

using System;
using System.IO;
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

public sealed class TerminalRendererCapturedVarThreadTests
{
    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    // ── Step A captures a var; step B substitutes it — the thread shows both ───
    //
    // Step A ("create-order") captures `orderId` from `$.id`.  Step B
    // ("ship-order") substitutes the plain placeholder {orderId} (origin = A) and a
    // secret-derived reference (env/API_TOKEN).  The provenance thread must show:
    //   • the capture: var name `orderId`, originating step `create-order`, path `$.id`;
    //   • the plain substitution INTO `ship-order`, traced back to origin `create-order`;
    //   • the secret-derived substitution rendered as a REDACTION MARKER — never a value.

    [Fact]
    public void Render_CaptureThenSubstitution_ShowsProvenanceThread()
    {
        const string secretValueMustNeverAppear = "super-secret-token-value-DO-NOT-LEAK";

        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "run-1", StepId = "create-order", Kind = "http.rest" }),
            Line(new StepCompletedEvent
            {
                RunId      = "run-1",
                StepId     = "create-order",
                Verdict    = Verdict.Pass,
                DurationMs = 12,
                // create-order captures orderId from $.id.
                Captured   = new[] { new CapturedVar("orderId", "$.id", Matched: true) },
            }),
            Line(new StepStartedEvent { RunId = "run-1", StepId = "ship-order", Kind = "http.rest" }),
            Line(new StepCompletedEvent
            {
                RunId         = "run-1",
                StepId        = "ship-order",
                Verdict       = Verdict.Pass,
                DurationMs    = 20,
                // ship-order substitutes the plain {orderId} (origin = create-order) and a
                // secret reference (env/API_TOKEN).  The Placeholder for a secret-derived
                // ref is the NON-sensitive reference label, never the value (§17).
                Substitutions = new[]
                {
                    new SubstitutionRef("orderId", OriginStepId: "create-order", SecretDerived: false),
                    new SubstitutionRef("env/API_TOKEN", OriginStepId: null, SecretDerived: true),
                },
            }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The captured value's name, originating step, and path are all surfaced.
        Assert.Contains("orderId", output, StringComparison.Ordinal);
        Assert.Contains("create-order", output, StringComparison.Ordinal);
        Assert.Contains("$.id", output, StringComparison.Ordinal);

        // The substitution INTO ship-order is shown, traced back to its origin step.
        Assert.Contains("ship-order", output, StringComparison.Ordinal);

        // There is a dedicated provenance section header (the thread is a distinct
        // section, not just the per-step verdict lines that already existed).
        Assert.Contains("provenance", output, StringComparison.OrdinalIgnoreCase);

        // §17: the secret-derived substitution renders a redaction marker and NEVER
        // a value.  The reference label is allowed; the value must be absent.
        Assert.Contains("redacted", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretValueMustNeverAppear, output, StringComparison.Ordinal);

        // The plain placeholder and the secret reference label are distinguished:
        // the plain one names its origin step; the secret one is marked redacted.
        var threadBlock = output;
        Assert.Contains("env/API_TOKEN", threadBlock, StringComparison.Ordinal);
    }

    // ── A secret-derived substitution renders ONLY the reference + redaction ───
    //
    // Even with no plain captures in play, a secret-derived substitution must be
    // visible in the provenance thread as a redaction — proving the secret-safety
    // path stands on its own (the reference label is shown; never a value).

    [Fact]
    public void Render_SecretDerivedSubstitution_RendersRedactionMarkerNeverValue()
    {
        const string secretValueMustNeverAppear = "sk-live-must-not-appear-anywhere";

        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "run-2", StepId = "call-api", Kind = "http.rest" }),
            Line(new StepCompletedEvent
            {
                RunId         = "run-2",
                StepId        = "call-api",
                Verdict       = Verdict.Pass,
                DurationMs    = 30,
                Substitutions = new[]
                {
                    new SubstitutionRef("vault/db/password", OriginStepId: null, SecretDerived: true),
                },
            }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The non-sensitive reference label is shown; a redaction marker accompanies it.
        Assert.Contains("vault/db/password", output, StringComparison.Ordinal);
        Assert.Contains("redacted", output, StringComparison.OrdinalIgnoreCase);
        // No value ever appears (none is in the stream — this proves the render never invents one).
        Assert.DoesNotContain(secretValueMustNeverAppear, output, StringComparison.Ordinal);
    }

    // ── Issue #266, Item 4: a hostile capture path is rendered inert ───────────
    //
    // A captured variable's JSONPath is author-controlled (it comes straight from the
    // step's own `capture:` block in the .e2e.yaml) — an embedded ANSI escape sequence
    // must not corrupt/spoof the terminal. GetStrFromObject (the choke point every
    // capture/substitution string field passes through, see TerminalRenderer's header
    // comment) applies DisplaySanitiser.SanitiseForDisplay before the path ever reaches
    // this renderer's output.

    [Fact]
    public void Render_CapturedVarWithHostilePath_RendersInert()
    {
        var esc = (char)0x1B;
        var hostilePath = "$.id" + esc + "[31m" + esc + "[0m";

        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "run-5", StepId = "create-order", Kind = "http.rest" }),
            Line(new StepCompletedEvent
            {
                RunId      = "run-5",
                StepId     = "create-order",
                Verdict    = Verdict.Pass,
                DurationMs = 12,
                Captured   = new[] { new CapturedVar("orderId", hostilePath, Matched: true) },
            }),
        };

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(lines, writer));
        Assert.Null(ex);

        var output = writer.ToString();

        // The surrounding path text survives sanitisation intact…
        Assert.Contains("$.id", output, StringComparison.Ordinal);
        // …but no raw ESC byte reaches the rendered output.
        Assert.DoesNotContain(esc, output);
    }

    // ── A step with neither captures nor substitutions emits no thread noise ───
    //
    // The provenance section is only drawn when there is provenance to show, so a
    // plain step run must not regress the existing output with an empty thread.

    [Fact]
    public void Render_NoCapturesOrSubstitutions_EmitsNoProvenanceSection()
    {
        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "run-3", StepId = "ping", Kind = "http.rest" }),
            Line(new StepCompletedEvent { RunId = "run-3", StepId = "ping", Verdict = Verdict.Pass, DurationMs = 5 }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The per-step verdict line still renders…
        Assert.Contains("ping", output, StringComparison.Ordinal);
        Assert.Contains("PASS", output, StringComparison.Ordinal);
        // …but no provenance section appears for a step with no captures/substitutions.
        Assert.DoesNotContain("provenance", output, StringComparison.OrdinalIgnoreCase);
    }

    // ── Interleaved multi-run stream: provenance threads do not cross-contaminate ─
    //
    // Two runs ("A", "B") reuse the same step ids.  Run A captures `orderId` and
    // run B captures `userId`; each run's substitution must trace to ITS OWN origin
    // step, not the other run's.  Per-var provenance state is keyed by (runId, …),
    // so the two threads stay separate (mirrors the T8 timeline cross-contamination
    // guard).

    [Fact]
    public void Render_InterleavedRunsSharingStepId_KeepSeparateProvenanceThreads()
    {
        var lines = new[]
        {
            // Both runs use stepId "src" to capture, then stepId "dst" to substitute.
            Line(new StepStartedEvent { RunId = "A", StepId = "src", Kind = "http.rest" }),
            Line(new StepStartedEvent { RunId = "B", StepId = "src", Kind = "http.rest" }),
            Line(new StepCompletedEvent
            {
                RunId      = "A",
                StepId     = "src",
                Verdict    = Verdict.Pass,
                DurationMs = 10,
                Captured   = new[] { new CapturedVar("orderId", "$.order", Matched: true) },
            }),
            Line(new StepCompletedEvent
            {
                RunId      = "B",
                StepId     = "src",
                Verdict    = Verdict.Pass,
                DurationMs = 11,
                Captured   = new[] { new CapturedVar("userId", "$.user", Matched: true) },
            }),
            Line(new StepCompletedEvent
            {
                RunId         = "A",
                StepId        = "dst",
                Verdict       = Verdict.Pass,
                DurationMs    = 12,
                Substitutions = new[] { new SubstitutionRef("orderId", OriginStepId: "src", SecretDerived: false) },
            }),
            Line(new StepCompletedEvent
            {
                RunId         = "B",
                StepId        = "dst",
                Verdict       = Verdict.Pass,
                DurationMs    = 13,
                Substitutions = new[] { new SubstitutionRef("userId", OriginStepId: "src", SecretDerived: false) },
            }),
        };

        using var writer = new StringWriter();
        var ex = Record.Exception(() => TerminalRenderer.Render(lines, writer));
        Assert.Null(ex);

        var output = writer.ToString();

        // Both runs' captured variable names appear; neither thread swallowed the other.
        Assert.Contains("orderId", output, StringComparison.Ordinal);
        Assert.Contains("userId", output, StringComparison.Ordinal);
        Assert.Contains("$.order", output, StringComparison.Ordinal);
        Assert.Contains("$.user", output, StringComparison.Ordinal);
    }

    // ── Back-compat: the existing T8 timeline output is unaffected ─────────────
    //
    // Adding the provenance thread must NOT change the per-step verdict / timeline
    // rendering.  A RETRY step's attempts and verdict still render unchanged.

    [Fact]
    public void Render_RetryStep_TimelineUnaffectedByProvenanceThread()
    {
        var lines = new[]
        {
            Line(new StepStartedEvent { RunId = "run-4", StepId = "poll", Kind = "mq-expect.kafka", VerifyMode = "RETRY" }),
            Line(new StepAttemptEvent { RunId = "run-4", StepId = "poll", Attempt = 1, TMs = 500, Outcome = Verdict.Inconclusive }),
            Line(new StepAttemptEvent { RunId = "run-4", StepId = "poll", Attempt = 2, TMs = 1500, Outcome = Verdict.Pass }),
            Line(new StepCompletedEvent { RunId = "run-4", StepId = "poll", Verdict = Verdict.Pass, DurationMs = 1520 }),
        };

        using var writer = new StringWriter();
        TerminalRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The timeline lines are still present and intact.
        var timelineLines = output
            .Split('\n')
            .Where(l => l.Contains("attempt ", StringComparison.Ordinal)
                        && l.Contains("t=", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, timelineLines.Length);
        Assert.Contains("0.5s", output, StringComparison.Ordinal);
        Assert.Contains("1.5s", output, StringComparison.Ordinal);
        Assert.Contains("PASS", output, StringComparison.Ordinal);
    }
}
