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
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

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
    // Test 4b (issue #279 — HTML/script-injection hole): every author/suite-controlled
    //          interpolation site is HTML-encoded, not only the step id covered by
    //          Test 4 above.  This exercises the FULL enumerated set: a step id
    //          carrying a <script> tag, a capture PATH attempting an attribute
    //          breakout (a leading `x">` sequence), a provider diff carrying mixed
    //          markup + both quote characters, and environment-error fields
    //          (resourceName / registryHost / detail).  A plain, non-hostile step
    //          sits alongside the hostile one to prove ordinary content still
    //          renders untouched (no double-encoding, no over-eager stripping) and
    //          the document stays a single well-formed whole.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_HostileMarkupAcrossCapturePathDiffAndEnvironmentFields_IsHtmlEncodedNotRaw()
    {
        const string evilStepId = "<script>alert(1)</script>";
        const string evilCapturePath = "x\"><img src=x onerror=alert(1)>";
        const string evilDiff = "<b>&\"'";
        const string evilResourceName = "<script>alert('env')</script>";
        const string evilRegistryHost = "reg<script>x</script>.example.com";
        const string evilDetail = "<b>bad&\"'</b>";

        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-279", ScenarioId = "hostile-flow" }),

            // A LEGITIMATE, plain step alongside the hostile one.
            Line(new StepCompletedEvent
            {
                RunId = "run-279",
                StepId = "clean-step",
                Verdict = Verdict.Pass,
                DurationMs = 5,
            }),

            // The step-started carries the "kind" the diffLookup keys off.
            Line(new StepStartedEvent
            {
                RunId = "run-279",
                StepId = evilStepId,
                Kind = "db-assert.postgres",
                VerifyMode = "IMMEDIATE",
            }),
            Line(new StepCompletedEvent
            {
                RunId = "run-279",
                StepId = evilStepId,
                Verdict = Verdict.Fail,
                DurationMs = 12,
                Observation = Parse("""{"column":"status"}"""),
                Captured = new[]
                {
                    new CapturedVar("orderId", evilCapturePath, Matched: false),
                },
            }),

            Line(new ScenarioCompletedEvent
            {
                RunId = "run-279",
                ScenarioId = "hostile-flow",
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Pass = 1, Fail = 1 },
            }),

            // An environment-error line carrying hostile resourceName/registryHost/detail
            // — hand-built because there is no typed record for this event in this test
            // project (mirrors the pattern TerminalRendererTests already uses).
            "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"environment-error\",\"ts\":\"2026-01-01T00:00:00Z\","
                + "\"runId\":\"run-279\",\"verdict\":\"ENV_ERROR\",\"errorKind\":\"ImagePull\","
                + "\"resourceName\":" + JsonSerializer.Serialize(evilResourceName) + ","
                + "\"registryHost\":" + JsonSerializer.Serialize(evilRegistryHost) + ","
                + "\"detail\":" + JsonSerializer.Serialize(evilDetail) + "}",
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(
            lines,
            writer,
            diffLookup: (_, _) => evilDiff);
        var output = writer.ToString();

        // Legitimate content renders untouched.
        Assert.Contains("clean-step", output, StringComparison.Ordinal);

        // The document remains a single, well-formed whole.
        Assert.Contains("<!DOCTYPE html>", output, StringComparison.Ordinal);
        Assert.Contains("</html>", output, StringComparison.Ordinal);

        // THE LOAD-BEARING ASSERTIONS: none of the raw hostile fragments survive
        // anywhere in the document.
        Assert.DoesNotContain(evilStepId, output, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x onerror=alert(1)>", output, StringComparison.Ordinal);
        Assert.DoesNotContain(evilDiff, output, StringComparison.Ordinal);
        Assert.DoesNotContain(evilResourceName, output, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>x</script>", output, StringComparison.Ordinal);
        Assert.DoesNotContain(evilDetail, output, StringComparison.Ordinal);

        // The encoded form is present instead, at each site.
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", output, StringComparison.Ordinal);
        Assert.Contains("x&quot;&gt;&lt;img src=x onerror=alert(1)&gt;", output, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;&amp;&quot;&#39;", output, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(&#39;env&#39;)&lt;/script&gt;", output, StringComparison.Ordinal);
        Assert.Contains("reg&lt;script&gt;x&lt;/script&gt;.example.com", output, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;bad&amp;&quot;&#39;&lt;/b&gt;", output, StringComparison.Ordinal);
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

    // -------------------------------------------------------------------------
    // Test 8 (issue #484): a fixture row carrying a NULL contentHash renders a
    // CAUSE-NEUTRAL token.
    //
    // Every other envelope test in this assembly — and the one in
    // ReproducibilityEnvelopeRenderTests — constructs only HASHED fixtures, so the
    // null branch of this renderer had no coverage at all and the token it printed
    // was free to say something the engine cannot know. Since issue #466 widened
    // ScenarioRunner.HashFixtureOrNull's catch to the whole IO family, a null hash
    // no longer means the file was absent: it means the fixture could not be hashed,
    // for a reason the envelope deliberately does not record. The report must
    // therefore not name one.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_FixtureWithNullContentHash_RendersCauseNeutralToken()
    {
        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-484", ScenarioId = "unhashable-fixture" }),
            Line(new ReproducibilityEnvelopeEvent
            {
                RunId = "run-484",
                ScenarioId = "unhashable-fixture",
                EnvSchemaVersion = ReproducibilityEnvelope.CurrentSchemaVersion,
                SecretReferences = Array.Empty<SecretReferenceDigest>(),
                Fixtures = new[]
                {
                    // The shape under test: a declared fixture the engine could not hash.
                    new FixtureDigest("fixtures/unreadable.sql", ContentHash: null),
                },
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-484",
                ScenarioId = "unhashable-fixture",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The row is still rendered — a fixture the engine could not hash is part of
        // what the run referenced, so dropping it would make the envelope a less
        // faithful account than recording it without a hash.
        Assert.Contains("fixtures/unreadable.sql", output, StringComparison.Ordinal);

        // SCOPED TO THE FIXTURE ROW, not the whole document, and deliberately: the
        // cause-neutrality rule binds THIS <li> and nothing else. Asserting
        // DoesNotContain("missing"/"denied"/…) over the entire report would make an
        // unrelated future shell string — a heading, a legend, a verdict label — redden
        // this test with a failure that named the wrong thing entirely.
        var rowStart = output.IndexOf(
            "<li><span class=\"mono\">fixtures/unreadable.sql", StringComparison.Ordinal);
        Assert.True(rowStart >= 0, "The fixture row must be present to be asserted over.");
        var rowEnd = output.IndexOf("</li>", rowStart, StringComparison.Ordinal);
        Assert.True(rowEnd > rowStart, "The fixture row must be a well-formed <li>.");
        var fixtureRow = output[rowStart..(rowEnd + "</li>".Length)];

        // THE LOCK, and it is asserted BEFORE the replacement token so that a run
        // against the defect fails naming the defect rather than naming its fix. The
        // property is "the report must not tell a reader WHY the hash is missing" —
        // the envelope does not know. "(absent)", the token this replaced, asserted a
        // cause the widened catch had already made one possibility among several.
        Assert.DoesNotContain("(absent)", fixtureRow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing", fixtureRow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", fixtureRow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("denied", fixtureRow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deleted", fixtureRow, StringComparison.OrdinalIgnoreCase);

        // And the row carries the cause-neutral token in place of it.
        Assert.Contains("(no hash recorded)", fixtureRow, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 9: the scenario heading shows a duration DERIVED from the
    // scenario-started / scenario-completed timestamp delta, because the frozen
    // v1 wire contract never gave ScenarioCompletedEvent a durationMs field. See
    // the fix note on HtmlRenderer.DeriveScenarioDurationMs.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_ScenarioTimestampDelta_ShowsDurationSuffixOnScenarioHeading()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-9", ScenarioId = "timed-flow", Timestamp = t0 }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-9",
                ScenarioId = "timed-flow",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
                Timestamp = t0.AddMilliseconds(1234),
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // Scoped to the scenario heading itself (the verdict span immediately
        // precedes the duration suffix and the closing </h2>) — not a step suffix,
        // which this stream carries none of.
        Assert.Contains(
            "<span class=\"verdict\">PASS</span> (1234 ms)</h2>",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ScenarioCompletedWithNoStartedEvent_HeadingHasNoDurationSuffix()
    {
        var lines = new[]
        {
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-9b",
                ScenarioId = "orphan",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
                Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero),
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // Isolate the scenario <h2> from the rest of the document (which carries no
        // steps here, so there is nothing else to confuse this with) and assert it
        // carries no " ms)" duration suffix at all.
        var headingStart = output.IndexOf("<h2>Scenario:", StringComparison.Ordinal);
        Assert.True(headingStart >= 0, "The scenario heading must be present to be asserted over.");
        var headingEnd = output.IndexOf("</h2>", headingStart, StringComparison.Ordinal);
        Assert.True(headingEnd > headingStart, "The scenario heading must be a well-formed <h2>.");
        var heading = output[headingStart..(headingEnd + "</h2>".Length)];

        Assert.DoesNotContain("ms)", heading, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 10 (issue #566): the wire durationMs is clamped at the
    // USE SITE, not only inside the derivation helper — a hostile or malformed
    // stream can carry a negative durationMs directly, bypassing the helper
    // entirely, and that must never render as a negative duration suffix.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_NegativeWireDurationMs_ClampsToZero()
    {
        var lines = new[]
        {
            // durationMs:-5 — a hostile/malformed value the wire read must clamp, not
            // pass through to the duration suffix verbatim.
            "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"scenario-completed\",\"ts\":\"2026-01-01T00:00:00Z\","
                + "\"runId\":\"run-10\",\"scenarioId\":\"hostile-negative\",\"verdict\":\"PASS\",\"durationMs\":-5,"
                + "\"counts\":{\"pass\":1,\"fail\":0,\"envError\":0,\"inconclusive\":0}}",
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        Assert.Contains(
            "<span class=\"verdict\">PASS</span> (0 ms)</h2>",
            output,
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 11 (issue #569): the step-level durationMs and the attempt-level tMs are
    // ALSO clamped at zero — the scenario-level clamp above (Test 10) does not cover
    // these two separate wire reads. A hostile or malformed stream can carry a
    // negative value on either, and neither may ever render as a negative duration.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_NegativeStepDurationMsAndAttemptTMs_ClampToZero()
    {
        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-11", ScenarioId = "hostile-negative-step" }),
            Line(new StepStartedEvent
            {
                RunId = "run-11",
                StepId = "negative-step",
                Kind = "http.rest",
                VerifyMode = "RETRY",
            }),
            Line(new StepAttemptEvent
            {
                RunId = "run-11",
                StepId = "negative-step",
                Attempt = 1,
                TMs = -3,
                Outcome = Verdict.Inconclusive,
            }),
            Line(new StepCompletedEvent
            {
                RunId = "run-11",
                StepId = "negative-step",
                Verdict = Verdict.Fail,
                DurationMs = -7,
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The step's duration suffix (WriteStep) clamps -7 to " (0 ms)".
        Assert.Contains(
            "<span class=\"verdict\">FAIL</span> (0 ms)",
            output,
            StringComparison.Ordinal);

        // The attempt timeline (WriteAttemptTimeline) clamps -3 to an elapsed of "0.0s".
        Assert.Contains(
            "<li>t=0.0s attempt 1 <span class=\"verdict\">INCONCLUSIVE</span></li>",
            output,
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // #571: a "runId": null scenario pair must not abort the whole document —
    // both surrounding valid scenarios must still render.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_NullRunIdScenarioAmongValidOnes_DoesNotThrowAndRendersBothValidScenarios()
    {
        // `required` enforces presence only, not non-nullness, so a wire line
        // carrying "runId": null satisfies `required string RunId` on the untyped envelope.
        // Before EventStreamJson.FromLine rejected it, this reached
        // ReportModel.GetOrAddScenario -> _lastScenarioByRun[runId] = scenario and threw
        // ArgumentNullException, aborting the whole document (BuildModel has no catch for
        // ArgumentNullException — only JsonException/InvalidOperationException).
        const string nullRunIdScenarioStarted =
            """{"v":1,"schemaVersion":"v1","type":"scenario-started","ts":"2024-01-15T10:00:00+00:00","runId":null,"scenarioId":"poisoned-flow"}""";
        const string nullRunIdScenarioCompleted =
            """{"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2024-01-15T10:00:05+00:00","runId":null,"scenarioId":"poisoned-flow","verdict":"PASS"}""";

        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-before", ScenarioId = "before-flow" }),
            Line(new StepCompletedEvent
            {
                RunId = "run-before",
                StepId = "before-step",
                Verdict = Verdict.Pass,
                DurationMs = 10,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-before",
                ScenarioId = "before-flow",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
            nullRunIdScenarioStarted,
            nullRunIdScenarioCompleted,
            Line(new ScenarioStartedEvent { RunId = "run-after", ScenarioId = "after-flow" }),
            Line(new StepCompletedEvent
            {
                RunId = "run-after",
                StepId = "after-step",
                Verdict = Verdict.Pass,
                DurationMs = 15,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-after",
                ScenarioId = "after-flow",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };

        using var writer = new StringWriter();
        var exception = Record.Exception(() => HtmlRenderer.Render(lines, writer));
        Assert.Null(exception);

        var output = writer.ToString();
        Assert.Contains("before-flow", output, StringComparison.Ordinal);
        Assert.Contains("before-step", output, StringComparison.Ordinal);
        Assert.Contains("after-flow", output, StringComparison.Ordinal);
        Assert.Contains("after-step", output, StringComparison.Ordinal);
        Assert.DoesNotContain("poisoned-flow", output, StringComparison.Ordinal);

        // Issue #588: both null-runId lines (scenario-started + scenario-completed) are
        // individually unreadable (#571's typed guard refuses each), so the run-summary
        // paragraph surfaces PLURAL wording with count 2.
        Assert.Contains(
            "<p class=\"skipped-lines\">2 event-stream lines could not be read, so this report may be incomplete.</p>",
            output,
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Issue #588: the run-summary skipped-event-lines paragraph.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_OneUnreadableLine_SurfacesSingularSkippedLinesParagraph()
    {
        var lines = new[]
        {
            "{ this is not json",
            Line(new ScenarioStartedEvent { RunId = "run-z", ScenarioId = "flow-z" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-z",
                ScenarioId = "flow-z",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);

        var output = writer.ToString();
        Assert.Contains(
            "<p class=\"skipped-lines\">1 event-stream line could not be read, so this report may be incomplete.</p>",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NoUnreadableLines_OmitsSkippedLinesParagraph()
    {
        var lines = new[]
        {
            string.Empty,
            "   ",
            """{"v":1,"schemaVersion":"v1","type":"future-event-2099","ts":"2025-01-01T00:00:00Z","runId":"run-x","somethingNew":{"x":1}}""",
            Line(new ScenarioStartedEvent { RunId = "run-z", ScenarioId = "flow-z" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-z",
                ScenarioId = "flow-z",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);

        var output = writer.ToString();

        // Byte-identical claim: a stream with no unreadable line must render EXACTLY the
        // same document as before this feature existed — no stylesheet rule, no
        // paragraph, no trace of the string "skipped-lines" anywhere in the document at
        // all when the count is 0.  (WriteStyle emits no ".skipped-lines" CSS rule
        // unconditionally — only the paragraph itself carries the "skipped-lines" class,
        // and only when SkippedEventLines > 0.)
        Assert.DoesNotContain("skipped-lines", output, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be read", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NoUnreadableLines_ProducesByteIdenticalDocument_ToTheSameStreamWithoutTheNoisyLines()
    {
        // Issue #588 (post-review fix): WriteStyle must NEVER emit anything related to
        // skipped-event-lines unconditionally — a normal report (nothing unreadable) has
        // to be byte-identical to what this renderer produced before the feature existed.
        // Proven here by comparing against a buffer with the blank lines and the
        // unknown-but-valid event type simply removed: since those lines contribute
        // nothing observable either way, the two renders must match byte-for-byte.
        var cleanLines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-z", ScenarioId = "flow-z" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-z",
                ScenarioId = "flow-z",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };

        var noisyLines = new[]
        {
            string.Empty,
            "   ",
            """{"v":1,"schemaVersion":"v1","type":"future-event-2099","ts":"2025-01-01T00:00:00Z","runId":"run-x","somethingNew":{"x":1}}""",
            cleanLines[0],
            cleanLines[1],
        };

        using var cleanWriter = new StringWriter();
        using var noisyWriter = new StringWriter();
        HtmlRenderer.Render(cleanLines, cleanWriter);
        HtmlRenderer.Render(noisyLines, noisyWriter);

        var cleanOutput = cleanWriter.ToString();
        Assert.Equal(cleanOutput, noisyWriter.ToString());
        Assert.DoesNotContain("skipped-lines", cleanOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ScenarioCompletedWithUnreadableMessage_StillStoresCountsAndDuration_BeforeAbortingTheLine()
    {
        // Issue #588 (gatekeeper MINOR, probe P4): BuildModel's ScenarioCompleted case
        // used to read scenario.Message BEFORE scenario.Counts / scenario.DurationMs.
        // Now that GetStr throws on an unreadable value (MAJOR-1), keeping that order
        // would let an unreadable `message` abort the line before the counts were ever
        // stored — the run-summary FAIL row would undercount to 0 and the duration would
        // be lost, even though the line IS still (correctly) counted as skipped. Message
        // is read LAST so only the optional message itself is lost when it is unreadable.
        const string PoisonedMessageLine =
            "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"scenario-completed\","
            + "\"ts\":\"2026-01-01T00:00:05Z\",\"runId\":\"run-bad\",\"scenarioId\":\"bad-message\","
            + "\"durationMs\":1234,"
            + "\"verdict\":\"FAIL\",\"message\":\"boom\\uD800\","
            + "\"counts\":{\"pass\":0,\"fail\":1,\"envError\":0,\"inconclusive\":0}}";

        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-good", ScenarioId = "good-flow" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-good",
                ScenarioId = "good-flow",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
            PoisonedMessageLine,
        };

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer);

        var output = writer.ToString();

        // The run-summary FAIL row must show 1 (from the poisoned scenario's counts) —
        // not 0 — proving the counts were stored BEFORE the message read aborted the
        // line.
        Assert.Contains(
            "<tr class=\"verdict-fail\"><th scope=\"row\"><span class=\"verdict\">FAIL</span></th><td>1</td></tr>",
            output,
            StringComparison.Ordinal);

        // The wire durationMs was stored before the message read aborted the line too.
        // The run summary cannot show this (it tallies counts only), so the scenario's
        // own heading carries the pin: no scenario-started exists for run-bad, so without
        // the wire value the heading would carry no duration suffix at all.
        Assert.Contains(
            "bad-message <span class=\"verdict\">FAIL</span> (1234 ms)",
            output,
            StringComparison.Ordinal);

        // The line is still counted as skipped — the message itself IS unreadable.
        Assert.Contains(
            "<p class=\"skipped-lines\">1 event-stream line could not be read, so this report may be incomplete.</p>",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Render_CapturedEntryWithLoneSurrogateKey_CountsTheLine_AndRendersNoFabricatedCapture()
    {
        // Issue #588: JsonElement.TryGetProperty can throw InvalidOperationException when
        // an escaped lone/unpaired UTF-16 surrogate appears among an object's PROPERTY
        // NAMES (not just a value) — here on the very FIRST extraction,
        // GetStrFromObject(capture, "name"), even though "name" itself is present and
        // clean: the lookup scans the poisoned key (last in the object) before it reaches
        // "name". That read runs inside BuildModel's per-line guard, so the line is
        // COUNTED and the capture is not rendered at all. Read outside that guard, the
        // throw would escape Render and cut the report off mid-document; caught and
        // turned into a missing value instead, the capture would render as "(unknown)" /
        // "(unknown)" / " (no match)", three values the stream never carried, with
        // nothing counted.
        // The poisoned key's LENGTH is deliberate: TryGetProperty skips unescaping a raw
        // candidate name no longer than the name being looked up, so a bare "\uD800" (6
        // raw bytes) is too short to be reached by "matched" (7 bytes). Padded to 18 raw
        // bytes so it is reachable by every lookup a capture makes.
        const string PoisonedCapturedKeyLine =
            "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"step-completed\","
            + "\"ts\":\"2026-01-01T00:00:00Z\",\"runId\":\"run-good\","
            + "\"stepId\":\"poison-step\",\"verdict\":\"PASS\",\"durationMs\":3,"
            + "\"captured\":[{\"name\":\"n\",\"path\":\"p\",\"\\uD800padpadpadpad\":1}]}";

        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-good", ScenarioId = "host-scenario" }),
            PoisonedCapturedKeyLine,
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-good",
                ScenarioId = "host-scenario",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };

        using var writer = new StringWriter();
        var ex = Record.Exception(() => HtmlRenderer.Render(lines, writer));
        Assert.Null(ex);

        var output = writer.ToString();
        Assert.Contains("<!DOCTYPE html>", output, StringComparison.Ordinal);
        Assert.EndsWith("</html>", output.TrimEnd(), StringComparison.Ordinal);

        // The line is counted, singular.
        Assert.Contains(
            "<p class=\"skipped-lines\">1 event-stream line could not be read, so this report may be incomplete.</p>",
            output,
            StringComparison.Ordinal);

        // No capture row is rendered for the poisoned entry, so none of the defaults a
        // swallowed read would have substituted appears. The step itself (read before
        // the capture) still renders.
        Assert.Contains("poison-step", output, StringComparison.Ordinal);
        Assert.DoesNotContain("<li>captured", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(unknown)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(no match)", output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Issue #588 (peer review M1): EVERY event-stream read HtmlRenderer makes sits inside
    // BuildModel's per-line guard. One row per read that once ran while the document was
    // being written, outside that guard: the provenance thread (captured
    // name/path/matched, substitution placeholder/secretDerived/originStepId), the
    // reproducibility envelope (secret-reference source/referenceHash, fixture
    // reference/contentHash) and the provider diff. Each row asserts the three outcomes
    // an unguarded or swallowed read cannot all meet: the line is COUNTED, the document
    // is complete, and the poisoned entry is not rendered with a value the stream never
    // carried.
    //
    // A string field is poisoned in its VALUE. A bool cannot carry a surrogate, so
    // matched and secretDerived are poisoned through a KEY in the same object, shaped so
    // the lookup of THAT field is the first one to reach it (measured by
    // PoisonedKey_IsReachedOnlyByTheLookupItTargets below): the key sits last in the
    // object, and the two characters before its escape match only the targeted name.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("captured", """{"name":"n\uD800","path":"$.m1","matched":true}""", "(unknown)")]
    [InlineData("captured", """{"name":"m1var","path":"$.m1\uD800","matched":true}""", "(unknown)")]
    [InlineData("captured", """{"name":"m1var","path":"$.m1","matched":true,"ma\uD800pad":1}""", "(no match)")]
    [InlineData("substitutions", """{"placeholder":"ph\uD800","secretDerived":false,"originStepId":"s0"}""", "(unknown)")]
    [InlineData("substitutions", """{"placeholder":"env/TOKEN","secretDerived":true,"originStepId":"s0","se\uD800padpadpad":1}""", "(from ")]
    [InlineData("substitutions", """{"placeholder":"ph","secretDerived":false,"originStepId":"s0\uD800"}""", "(variables/untraced)")]
    [InlineData("secretReferences", """{"source":"env\uD800","referenceHash":"h1"}""", "(unknown)")]
    [InlineData("secretReferences", """{"source":"env","referenceHash":"h1\uD800"}""", "(unknown)")]
    [InlineData("fixtures", """{"reference":"seed\uD800.sql","contentHash":"c1"}""", "(unknown)")]
    [InlineData("fixtures", """{"reference":"seed.sql","contentHash":"c1\uD800"}""", "(no hash recorded)")]
    [InlineData("observation", """{"actual":"x\uD800"}""", "<div class=\"diff\">")]
    public void Render_UnreadableValueInAMaterialisedRead_CountsTheLine_AndRendersNoFabricatedValue(
        string readClass, string poisonedJson, string fabricatedFragment)
    {
        const string ScenarioStarted =
            """{"v":1,"schemaVersion":"v1","type":"scenario-started","ts":"2026-01-01T00:00:00Z","runId":"run-m1","scenarioId":"m1-flow"}""";
        const string StepStarted =
            """{"v":1,"schemaVersion":"v1","type":"step-started","ts":"2026-01-01T00:00:01Z","runId":"run-m1","stepId":"m1-step","kind":"db-assert.postgres"}""";
        const string ScenarioCompleted =
            """{"v":1,"schemaVersion":"v1","type":"scenario-completed","ts":"2026-01-01T00:00:03Z","runId":"run-m1","scenarioId":"m1-flow","verdict":"PASS","counts":{"pass":1,"fail":0,"envError":0,"inconclusive":0}}""";

        var poisonedLine = readClass switch
        {
            "captured" or "substitutions" =>
                """{"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-01-01T00:00:02Z","runId":"run-m1","stepId":"m1-step","verdict":"PASS","durationMs":3,"""
                + "\"" + readClass + "\":[" + poisonedJson + "]}",
            "secretReferences" or "fixtures" =>
                """{"v":1,"schemaVersion":"v1","type":"reproducibility-envelope","ts":"2026-01-01T00:00:02Z","runId":"run-m1","scenarioId":"m1-flow","envSchemaVersion":"1","""
                + "\"" + readClass + "\":[" + poisonedJson + "]}",

            // A FAIL step whose observation the diff lookup reads: the step-started above
            // supplies the kind, exactly as the engine's own stream does.
            "observation" =>
                """{"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-01-01T00:00:02Z","runId":"run-m1","stepId":"m1-step","verdict":"FAIL","durationMs":3,"observation":"""
                + poisonedJson + "}",
            _ => throw new ArgumentOutOfRangeException(nameof(readClass), readClass, null),
        };

        var lines = new[] { ScenarioStarted, StepStarted, poisonedLine, ScenarioCompleted };

        // Reads the observation the way a real IStepDiffRenderer does: GetString() on a
        // string value, which throws on the lone surrogate.
        static string? ReadActual(string kind, JsonElement observation)
            => "expected <ok>, observed <" + observation.GetProperty("actual").GetString() + ">";

        using var writer = new StringWriter();
        var ex = Record.Exception(() => HtmlRenderer.Render(lines, writer, ReadActual));
        Assert.Null(ex);

        var output = writer.ToString();

        Assert.Contains(
            "<p class=\"skipped-lines\">1 event-stream line could not be read, so this report may be incomplete.</p>",
            output,
            StringComparison.Ordinal);
        Assert.EndsWith("</html>", output.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain(fabricatedFragment, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"name":"m1var","path":"$.m1","matched":true,"ma\uD800pad":1}""", "name,path", "matched")]
    [InlineData("""{"placeholder":"env/TOKEN","secretDerived":true,"originStepId":"s0","se\uD800padpadpad":1}""", "placeholder", "secretDerived")]
    public void PoisonedKey_IsReachedOnlyByTheLookupItTargets(
        string objectJson, string cleanLookups, string throwingLookup)
    {
        // The measurement behind the two key-poisoned rows above: the lookups the
        // renderer makes BEFORE the targeted one succeed, and the targeted lookup is the
        // one that throws — so the row fails at the read it is named for.
        using var document = JsonDocument.Parse(objectJson);
        var element = document.RootElement;

        foreach (var name in cleanLookups.Split(','))
        {
            Assert.True(element.TryGetProperty(name, out _), name);
        }

        var ex = Record.Exception(() => element.TryGetProperty(throwingLookup, out _));
        Assert.IsType<InvalidOperationException>(ex);
    }

    // -------------------------------------------------------------------------
    // Issue #588: a later step-completed line for the same step REPLACES the diff and
    // provenance an earlier one recorded (last line wins), rather than adding to them.
    // The third row's second line is FAIL with an observation the lookup cannot read:
    // the lookup throws, so the diff is never assigned and the provenance is never read
    // for that line — only the reset before them keeps the first line's diff and
    // provenance box from being drawn under the second line's verdict.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("PASS", null, null, false)]
    [InlineData("FAIL", """{"actual":"two"}""", "DIFF-two", false)]
    [InlineData("FAIL", """{"actual":"x\uD800"}""", null, true)]
    public void Render_DuplicateStepCompletedLines_RenderOnlyTheLastLinesDiffAndProvenance(
        string secondVerdict, string? secondObservationJson, string? expectedDiff, bool secondLineUnreadable)
    {
        const string ScenarioStarted =
            """{"v":1,"schemaVersion":"v1","type":"scenario-started","ts":"2026-01-01T00:00:00Z","runId":"run-dup","scenarioId":"dup-flow"}""";
        const string StepStarted =
            """{"v":1,"schemaVersion":"v1","type":"step-started","ts":"2026-01-01T00:00:01Z","runId":"run-dup","stepId":"dup-step","kind":"db-assert.postgres"}""";
        const string FirstCompleted =
            """{"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-01-01T00:00:02Z","runId":"run-dup","stepId":"dup-step","verdict":"FAIL","durationMs":3,"observation":{"actual":"one"},"captured":[{"name":"firstcap","path":"$.f","matched":true}],"substitutions":[{"placeholder":"firstsub","secretDerived":false,"originStepId":"s0"}]}""";
        var secondCompleted =
            """{"v":1,"schemaVersion":"v1","type":"step-completed","ts":"2026-01-01T00:00:03Z","runId":"run-dup","stepId":"dup-step","verdict":"@VERDICT@","durationMs":4,@OBSERVATION@"captured":[{"name":"secondcap","path":"$.s","matched":true}]}"""
                .Replace("@VERDICT@", secondVerdict, StringComparison.Ordinal)
                .Replace(
                    "@OBSERVATION@",
                    secondObservationJson is null ? string.Empty : "\"observation\":" + secondObservationJson + ",",
                    StringComparison.Ordinal);

        var lines = new[] { ScenarioStarted, StepStarted, FirstCompleted, secondCompleted };

        static string? DiffLookup(string kind, JsonElement observation)
            => "DIFF-" + observation.GetProperty("actual").GetString();

        using var writer = new StringWriter();
        HtmlRenderer.Render(lines, writer, DiffLookup);
        var output = writer.ToString();

        // The first line's provenance never survives.
        Assert.DoesNotContain("firstcap", output, StringComparison.Ordinal);
        Assert.DoesNotContain("firstsub", output, StringComparison.Ordinal);

        // The first line's diff never survives: a PASS second line clears it, a FAIL
        // second line replaces it with its own, and an unreadable one leaves none.
        Assert.DoesNotContain("DIFF-one", output, StringComparison.Ordinal);
        if (expectedDiff is null)
        {
            Assert.DoesNotContain("<div class=\"diff\">", output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("<div class=\"diff\">" + expectedDiff + "</div>", output, StringComparison.Ordinal);
        }

        if (secondLineUnreadable)
        {
            // The lookup threw before the second line's provenance was read: no
            // provenance section at all, and the line is counted.
            Assert.DoesNotContain("<div class=\"provenance\">", output, StringComparison.Ordinal);
            Assert.DoesNotContain("secondcap", output, StringComparison.Ordinal);
            Assert.Contains(
                "<p class=\"skipped-lines\">1 event-stream line could not be read, so this report may be incomplete.</p>",
                output,
                StringComparison.Ordinal);
        }
        else
        {
            // Only the second line's provenance renders.
            Assert.Contains("secondcap", output, StringComparison.Ordinal);
            Assert.DoesNotContain("skipped-lines", output, StringComparison.Ordinal);
        }
    }
}
