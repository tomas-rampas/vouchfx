// Tests for HtmlRenderer — the self-contained HTML report renderer (S09-G-01).
//
// Strategy mirrors TerminalRendererTests: build recorded event streams from typed
// payload records via EventStreamJson.ToLine<T>, render to a StringWriter, then
// assert on the emitted HTML string.  The HTML report is driven by the SAME
// buffered JSON Lines event stream the terminal renderer consumes — "render the one
// stream differently, never a per-audience pipeline" (§14).
//
// The four acceptance gates exercised here:
//   1. Four-verdict distinctness — Pass / Fail / EnvironmentError / Inconclusive
//      each render with a DISTINCT, non-colour-only marker (a verdict CSS class AND
//      a text label), satisfying WCAG AA (do not rely on colour alone).
//   2. Self-contained — <!DOCTYPE html>, an inline <style>, and NO external fetches
//      (no http://, https://, src=, or href= pointing off-document).
//   3. Secret-safety (the load-bearing invariant, §17) — a reproducibility envelope
//      with a secret REFERENCE and a secret-derived SubstitutionRef render the
//      reference label/hash and a (redacted) marker, while a deliberately-planted
//      resolved-secret sentinel never appears anywhere in the output.
//   4. HTML-escaping — a step id / observation carrying <script>, &, or " is escaped
//      so no raw markup or value can sneak through.

using System;
using System.IO;
using System.Text.Json;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Abstractions.Reproducibility;
using Platform.Engine.Reporting;
using Xunit;

namespace Platform.Engine.Reporting.Tests;

public sealed class HtmlRendererTests
{
    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // -------------------------------------------------------------------------
    // Test 1: all four verdicts render with a DISTINCT, non-colour-only marker.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_AllFourVerdicts_EachRendersWithDistinctMarker()
    {
        // One scenario whose four steps cover every verdict in the §12.1 taxonomy.
        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "order-flow" }),
            Line(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-pass",
                Verdict = Verdict.Pass,
                DurationMs = 10,
            }),
            Line(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-fail",
                Verdict = Verdict.Fail,
                DurationMs = 20,
            }),
            Line(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-env-error",
                Verdict = Verdict.EnvironmentError,
                DurationMs = 30,
            }),
            Line(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "step-inconclusive",
                Verdict = Verdict.Inconclusive,
                DurationMs = 40,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-1",
                ScenarioId = "order-flow",
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Pass = 1, Fail = 1, EnvError = 1, Inconclusive = 1 },
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // Each verdict has its OWN CSS class — a non-colour cue (the class can carry
        // a symbol / border / weight, not only a colour), so the four are
        // distinguishable without relying on colour (WCAG AA).
        Assert.Contains("verdict-pass", output, StringComparison.Ordinal);
        Assert.Contains("verdict-fail", output, StringComparison.Ordinal);
        Assert.Contains("verdict-env-error", output, StringComparison.Ordinal);
        Assert.Contains("verdict-inconclusive", output, StringComparison.Ordinal);

        // Each verdict ALSO carries its canonical text token (a non-colour text cue).
        Assert.Contains("PASS", output, StringComparison.Ordinal);
        Assert.Contains("FAIL", output, StringComparison.Ordinal);
        Assert.Contains("ENV_ERROR", output, StringComparison.Ordinal);
        Assert.Contains("INCONCLUSIVE", output, StringComparison.Ordinal);

        // The four step ids are present so a reader can locate each verdict.
        Assert.Contains("step-pass", output, StringComparison.Ordinal);
        Assert.Contains("step-fail", output, StringComparison.Ordinal);
        Assert.Contains("step-env-error", output, StringComparison.Ordinal);
        Assert.Contains("step-inconclusive", output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 2: the document is self-contained — DOCTYPE + inline <style>, and NO
    //         external fetches (no CDN links / remote fonts / remote scripts).
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_ProducesSelfContainedDocument_NoExternalReferences()
    {
        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-2", ScenarioId = "health-check" }),
            Line(new StepCompletedEvent
            {
                RunId = "run-2",
                StepId = "ping",
                Verdict = Verdict.Pass,
                DurationMs = 5,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-2",
                ScenarioId = "health-check",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // A complete HTML document with an inline stylesheet.
        Assert.Contains("<!DOCTYPE html>", output, StringComparison.Ordinal);
        Assert.Contains("<style", output, StringComparison.Ordinal);

        // No external fetches of any kind: no remote URLs, and no src=/href= that
        // would pull a resource off-document.
        Assert.DoesNotContain("http://", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=", output, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Test 3 (MANDATORY acceptance gate): no resolved secret VALUE can appear.
    //
    // The stream carries a reproducibility-envelope with a secret REFERENCE
    // (env/API_TOKEN) and a step-completed with a secret-derived SubstitutionRef.
    // A sentinel resolved value is deliberately planted in a field the renderer must
    // NOT surface; the report must contain the reference label/hash and a (redacted)
    // marker, but NEVER the sentinel.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_SecretReferenceAndDerivedSubstitution_NeverLeaksResolvedValue()
    {
        const string sentinelSecretValue = "super-secret-value-xyz";
        const string referenceHash =
            "a1b2c3d4e5f6071829304152637485960718293041526374859607182930a1b2";

        // The reproducibility-envelope line: a secret REFERENCE digest (source + hash,
        // never a value) and a fixture digest.
        var envelopeLine = Line(new ReproducibilityEnvelopeEvent
        {
            RunId = "run-3",
            ScenarioId = "secret-flow",
            EnvSchemaVersion = ReproducibilityEnvelope.CurrentSchemaVersion,
            SecretReferences = new[]
            {
                new SecretReferenceDigest("env", referenceHash),
            },
            Fixtures = new[]
            {
                new FixtureDigest("fixtures/orders.sql", "deadbeefcafef00d"),
            },
        });

        // A step-completed carrying a secret-derived substitution.  The Placeholder is
        // the NON-sensitive reference label (env/API_TOKEN), never the value (§17).  We
        // then hand-craft a SECOND step-completed line that ALSO smuggles the resolved
        // sentinel into an UNKNOWN extra field, proving the renderer surfaces no
        // arbitrary field verbatim (forward-compat fields are tolerated, not printed).
        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-3", ScenarioId = "secret-flow" }),
            envelopeLine,
            Line(new StepCompletedEvent
            {
                RunId = "run-3",
                StepId = "call-api",
                Verdict = Verdict.Pass,
                DurationMs = 30,
                Substitutions = new[]
                {
                    new SubstitutionRef("env/API_TOKEN", OriginStepId: null, SecretDerived: true),
                },
            }),
            // Hand-crafted line: the sentinel lives ONLY in an unknown "resolvedValue"
            // field that the renderer must never surface.
            "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"step-completed\",\"ts\":\"2026-01-01T00:00:00Z\","
                + "\"runId\":\"run-3\",\"stepId\":\"leak-bait\",\"verdict\":\"PASS\",\"durationMs\":1,"
                + "\"resolvedValue\":\"" + sentinelSecretValue + "\"}",
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-3",
                ScenarioId = "secret-flow",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 2 },
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The reproducibility-envelope panel surfaces the non-sensitive REFERENCE
        // digest (source + hash).
        Assert.Contains(referenceHash, output, StringComparison.Ordinal);
        Assert.Contains("env", output, StringComparison.Ordinal);

        // The secret-derived substitution renders the reference label and a redaction
        // marker — never a value.
        Assert.Contains("env/API_TOKEN", output, StringComparison.Ordinal);
        Assert.Contains("(redacted)", output, StringComparison.Ordinal);

        // THE LOAD-BEARING ASSERTION: the resolved secret value never appears.
        Assert.DoesNotContain(sentinelSecretValue, output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 4: every dynamic string is HTML-escaped — markup / script injection is
    //         neutralised and no value can sneak through the markup.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_DynamicStringsAreHtmlEscaped()
    {
        const string evilStepId = "<script>alert(\"xss\")</script>&friends";

        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-4", ScenarioId = "<b>scenario&name</b>" }),
            Line(new StepCompletedEvent
            {
                RunId = "run-4",
                StepId = evilStepId,
                Verdict = Verdict.Fail,
                DurationMs = 7,
                Observation = Parse("""{"note":"<script>"}"""),
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-4",
                ScenarioId = "<b>scenario&name</b>",
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The raw <script> tag from the step id must NOT appear unescaped anywhere
        // outside the inline <script>/<style> the renderer itself emits — assert the
        // exact injected open tag is escaped.
        Assert.DoesNotContain("<script>alert", output, StringComparison.Ordinal);

        // The escaped form of the dangerous characters is present instead.
        Assert.Contains("&lt;script&gt;", output, StringComparison.Ordinal);

        // The ampersand from the step id / scenario name is escaped.
        Assert.Contains("&amp;friends", output, StringComparison.Ordinal);
        Assert.Contains("scenario&amp;name", output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 5: a partial buffer (a step with step-started + step-attempt but NO
    //         step-completed) has an UNKNOWN verdict.  The renderer must not throw,
    //         must still emit a well-formed single document, and must mark the step
    //         with the neutral verdict-unknown class (which carries its own
    //         non-colour cue) rather than silently dropping the distinction.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_PartialBufferWithNoStepCompleted_RendersUnknownVerdict()
    {
        // No step-completed for "in-flight" → its verdict is unknown.  We still emit a
        // step-started (kind) and a mid-flight step-attempt so the buffer mirrors a real
        // truncated stream (a run cut off before the step resolved).
        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-5", ScenarioId = "interrupted-flow" }),
            Line(new StepStartedEvent
            {
                RunId = "run-5",
                StepId = "in-flight",
                Kind = "http.rest",
                VerifyMode = "RETRY",
            }),
            Line(new StepAttemptEvent
            {
                RunId = "run-5",
                StepId = "in-flight",
                Attempt = 1,
                TMs = 15,
                Outcome = null,
            }),
            // Deliberately NO StepCompletedEvent and NO ScenarioCompletedEvent — the
            // buffer is truncated, leaving the step's verdict unresolved.
        };

        using var writer = new StringWriter();

        // (a) Render must not throw on the partial buffer.
        var ex = Record.Exception(() => HtmlRenderer.Render(lines, writer));
        Assert.Null(ex);

        var output = writer.ToString();

        // (b) The output is still a well-formed single HTML document.
        Assert.Contains("<!DOCTYPE html>", output, StringComparison.Ordinal);
        Assert.Contains("</html>", output, StringComparison.Ordinal);

        // (c) The unknown-verdict step carries the neutral verdict-unknown class — the
        // CSS rule for which now exists in the inline stylesheet (a non-colour cue).
        Assert.Contains("verdict-unknown", output, StringComparison.Ordinal);

        // The step itself is still located in the report.
        Assert.Contains("in-flight", output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 6 (C145-3): a step attaches to the MOST-RECENTLY-INSERTED scenario for
    //                  its run — "last by insertion order", never "last touched".
    //
    // This locks the semantics the O(1) refactor must preserve.  Two scenarios
    // (A then B) share one run; a step appears after each scenario-started, so s1
    // must land under A and s2 under B.  A leading step with NO preceding
    // scenario-started must attach to a synthesised "(scenario)".  The test holds
    // both before the refactor (linear scan for the last matching scenario) and
    // after it (an insertion-order index), and would FAIL a naive "last-touched"
    // index that re-pointed the run at an already-seen scenario on every step.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_StepsAttachToMostRecentlyInsertedScenarioForTheirRun()
    {
        var lines = new[]
        {
            // A leading step with no preceding scenario-started → synthesised
            // "(scenario)".
            Line(new StepCompletedEvent
            {
                RunId = "run-6",
                StepId = "orphan-step",
                Verdict = Verdict.Pass,
                DurationMs = 1,
            }),

            // Scenario A opens, then its step s1.
            Line(new ScenarioStartedEvent { RunId = "run-6", ScenarioId = "scenario-A" }),
            Line(new StepCompletedEvent
            {
                RunId = "run-6",
                StepId = "step-s1",
                Verdict = Verdict.Pass,
                DurationMs = 10,
            }),

            // Scenario B opens AFTER A (same run), then its step s2.  Once B is the
            // most-recently-inserted scenario, s2 must land under B — not under A,
            // and not under the orphan "(scenario)".
            Line(new ScenarioStartedEvent { RunId = "run-6", ScenarioId = "scenario-B" }),
            Line(new StepCompletedEvent
            {
                RunId = "run-6",
                StepId = "step-s2",
                Verdict = Verdict.Fail,
                DurationMs = 20,
            }),

            // A scenario-completed for the OLDER scenario A re-touches A's model (it
            // already exists, so GetOrAddScenario returns it).  Under a last-INSERTED
            // index this leaves B as the run's "last" scenario; under a last-TOUCHED
            // index it would wrongly re-point the run at A.  The NEXT step (s3) then
            // discriminates the two: it must still attach to B, the last INSERTED
            // scenario, not to the just-touched A.
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-6",
                ScenarioId = "scenario-A",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
            Line(new StepCompletedEvent
            {
                RunId = "run-6",
                StepId = "step-s3",
                Verdict = Verdict.Pass,
                DurationMs = 30,
            }),

            Line(new ScenarioCompletedEvent
            {
                RunId = "run-6",
                ScenarioId = "scenario-B",
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The synthesised scenario for the orphan step exists, and all three
        // scenario sections are present.
        Assert.Contains("Scenario: (scenario)", output, StringComparison.Ordinal);
        Assert.Contains("Scenario: scenario-A", output, StringComparison.Ordinal);
        Assert.Contains("Scenario: scenario-B", output, StringComparison.Ordinal);

        // The report lays sections out in first-seen (insertion) order:
        // (scenario) → scenario-A → scenario-B.
        var orphanHeading = output.IndexOf("Scenario: (scenario)", StringComparison.Ordinal);
        var headingA = output.IndexOf("Scenario: scenario-A", StringComparison.Ordinal);
        var headingB = output.IndexOf("Scenario: scenario-B", StringComparison.Ordinal);
        Assert.True(orphanHeading >= 0 && orphanHeading < headingA && headingA < headingB);

        // Each step renders exactly once; locate each.
        var orphanStep = output.IndexOf("orphan-step", StringComparison.Ordinal);
        var s1 = output.IndexOf("step-s1", StringComparison.Ordinal);
        var s2 = output.IndexOf("step-s2", StringComparison.Ordinal);
        var s3 = output.IndexOf("step-s3", StringComparison.Ordinal);
        Assert.True(orphanStep >= 0 && s1 >= 0 && s2 >= 0 && s3 >= 0);

        // THE LOCK: orphan-step sits in the synthesised "(scenario)" section (before
        // scenario-A's heading); s1 sits under scenario-A (between A's and B's
        // headings); s2 AND s3 sit under scenario-B (after B's heading).  s3 is the
        // discriminator: it arrives AFTER scenario-A's completed event re-touches A,
        // so a "last-touched" index would have mislocated it under scenario-A; a
        // "last-inserted" index keeps it under B.
        Assert.True(orphanStep > orphanHeading && orphanStep < headingA);
        Assert.True(s1 > headingA && s1 < headingB);
        Assert.True(s2 > headingB);
        Assert.True(s3 > headingB);
    }
}
