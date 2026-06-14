// Renderer parity check (S09-D-01).
//
// The §14 invariant under test: ONE schema-versioned JSON Lines event stream feeds
// every renderer — "render the one stream differently, never a per-audience
// pipeline".  A renderer that mis-maps a verdict token (e.g. an HTML report that
// labels an Inconclusive scenario FAIL, or a JUnit writer that emits <failure> for an
// EnvironmentError) is a per-audience DIVERGENCE: two consumers of the same stream
// would disagree on the outcome.  Conflating an environment error with a defect, in
// particular, destroys trust in the tool (§12.1).
//
// Strategy:
//   • Build ONE fixed buffer covering all four §12.1 verdicts — a Pass scenario, a
//     Fail scenario, an EnvironmentError scenario, and an Inconclusive scenario —
//     each authored exactly as the runner would emit it (scenario-started + a
//     step-completed + scenario-completed; the env-error scenario also emits an
//     environment-error event, mirroring how the orchestration layer surfaces an
//     infra failure).  Buffer construction reuses EventStreamJson.ToLine<T>, the same
//     pattern the sibling renderer tests use.
//   • Render that SAME buffer through all three renderers (TerminalRenderer,
//     HtmlRenderer, JunitXmlRenderer), each to its own StringWriter.
//   • Extract the per-scenario verdict each renderer asserts and prove they AGREE
//     with the expected verdict set — and crucially that no renderer surfaces a
//     DIFFERENT verdict for a scenario.
//   • Also prove the aggregate counts agree where the renderers expose them
//     (JUnit <testsuite failures/errors/skipped>; terminal/HTML run-summary tallies).
//
// This is a TEST-ONLY guard.  It touches no renderer or production code; a genuine
// renderer disagreement would be a real bug to fix in the renderer, not here.
//
// Note on the env-error event line: the Reporting test project does not reference
// Platform.Engine.Orchestration (where EnvironmentErrorEvent lives), so the
// environment-error line is hand-crafted as flat-wire JSON — exactly as
// TerminalRendererTests does — keeping the dependency graph unchanged.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Reporting;
using Xunit;

namespace Platform.Engine.Reporting.Tests;

public sealed class RendererParityTests
{
    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    // The four scenarios, one per §12.1 verdict, and their canonical wire tokens.
    // Each scenario runs under its own runId so the buffer mirrors an aggregated
    // multi-run stream — the worst case for cross-resolution — while still keeping
    // each scenario unambiguous across all three renderers.
    private const string PassRun = "run-pass";
    private const string FailRun = "run-fail";
    private const string EnvRun = "run-env";
    private const string IncRun = "run-inc";

    private const string PassScenario = "checkout-happy-path";
    private const string FailScenario = "checkout-rejects-bad-card";
    private const string EnvScenario = "checkout-db-unreachable";
    private const string IncScenario = "checkout-webhook-timeout";

    private static readonly SortedDictionary<string, string> ExpectedVerdicts =
        new(StringComparer.Ordinal)
        {
            [PassScenario] = "PASS",
            [FailScenario] = "FAIL",
            [EnvScenario] = "ENV_ERROR",
            [IncScenario] = "INCONCLUSIVE",
        };

    // Newline separators for splitting rendered output line-by-line, hoisted to a
    // static field so the per-call array allocation (CA1861) is avoided.
    private static readonly string[] NewlineSeparators = { "\r\n", "\n" };

    // -------------------------------------------------------------------------
    // The fixed buffer: four scenarios, one per verdict, authored as the runner
    // would emit them.  Built ONCE and rendered three times.
    // -------------------------------------------------------------------------

    private static List<string> BuildFixedBuffer()
    {
        var buffer = new List<string>();

        // ---- Pass scenario: scenario-started → step PASS → scenario PASS. -------
        buffer.Add(Line(new ScenarioStartedEvent
        {
            RunId = PassRun,
            ScenarioId = PassScenario,
            File = "checkout-happy-path.e2e.yaml",
        }));
        buffer.Add(Line(new StepStartedEvent
        {
            RunId = PassRun,
            StepId = "place-order",
            Kind = "http.rest",
        }));
        buffer.Add(Line(new StepCompletedEvent
        {
            RunId = PassRun,
            StepId = "place-order",
            Verdict = Verdict.Pass,
            DurationMs = 42,
        }));
        buffer.Add(Line(new ScenarioCompletedEvent
        {
            RunId = PassRun,
            ScenarioId = PassScenario,
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        }));

        // ---- Fail scenario: scenario-started → step FAIL → scenario FAIL. -------
        buffer.Add(Line(new ScenarioStartedEvent
        {
            RunId = FailRun,
            ScenarioId = FailScenario,
            File = "checkout-rejects-bad-card.e2e.yaml",
        }));
        buffer.Add(Line(new StepStartedEvent
        {
            RunId = FailRun,
            StepId = "assert-status",
            Kind = "db-assert.postgres",
        }));
        buffer.Add(Line(new StepCompletedEvent
        {
            RunId = FailRun,
            StepId = "assert-status",
            Verdict = Verdict.Fail,
            DurationMs = 88,
        }));
        buffer.Add(Line(new ScenarioCompletedEvent
        {
            RunId = FailRun,
            ScenarioId = FailScenario,
            Verdict = Verdict.Fail,
            Counts = new VerdictCounts { Fail = 1 },
        }));

        // ---- EnvironmentError scenario: scenario-started → environment-error ----
        //      → step ENV_ERROR → scenario ENV_ERROR.  The environment-error event
        //      mirrors how the orchestration layer surfaces an infra failure; it is
        //      hand-crafted as flat-wire JSON (the test project does not reference
        //      Platform.Engine.Orchestration where EnvironmentErrorEvent lives).
        buffer.Add(Line(new ScenarioStartedEvent
        {
            RunId = EnvRun,
            ScenarioId = EnvScenario,
            File = "checkout-db-unreachable.e2e.yaml",
        }));
        buffer.Add(EnvironmentErrorLine(
            runId: EnvRun,
            errorKind: "HealthGate",
            resourceName: "appdb",
            detail: "container 'appdb' never reported healthy"));
        buffer.Add(Line(new StepCompletedEvent
        {
            RunId = EnvRun,
            StepId = "query-orders",
            Verdict = Verdict.EnvironmentError,
            DurationMs = 5,
        }));
        buffer.Add(Line(new ScenarioCompletedEvent
        {
            RunId = EnvRun,
            ScenarioId = EnvScenario,
            Verdict = Verdict.EnvironmentError,
            Counts = new VerdictCounts { EnvError = 1 },
        }));

        // ---- Inconclusive scenario: scenario-started → step INCONCLUSIVE → ------
        //      scenario INCONCLUSIVE (e.g. a RETRY step that outlasted its grace).
        buffer.Add(Line(new ScenarioStartedEvent
        {
            RunId = IncRun,
            ScenarioId = IncScenario,
            File = "checkout-webhook-timeout.e2e.yaml",
        }));
        buffer.Add(Line(new StepStartedEvent
        {
            RunId = IncRun,
            StepId = "await-webhook",
            Kind = "webhook-listen.http",
            VerifyMode = "RETRY",
        }));
        buffer.Add(Line(new StepCompletedEvent
        {
            RunId = IncRun,
            StepId = "await-webhook",
            Verdict = Verdict.Inconclusive,
            DurationMs = 30_000,
        }));
        buffer.Add(Line(new ScenarioCompletedEvent
        {
            RunId = IncRun,
            ScenarioId = IncScenario,
            Verdict = Verdict.Inconclusive,
            Counts = new VerdictCounts { Inconclusive = 1 },
        }));

        return buffer;
    }

    /// <summary>
    /// Builds an <c>environment-error</c> event line as flat-wire JSON, mirroring the
    /// shape of <c>Platform.Engine.Orchestration.EnvironmentErrorEvent</c> without
    /// referencing that assembly (the same technique <c>TerminalRendererTests</c> uses).
    /// </summary>
    private static string EnvironmentErrorLine(
        string runId,
        string errorKind,
        string resourceName,
        string detail) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"environment-error\","
            + "\"ts\":\"2026-01-01T00:00:00Z\",\"runId\":\"{0}\",\"verdict\":\"ENV_ERROR\","
            + "\"errorKind\":\"{1}\",\"resourceName\":\"{2}\",\"detail\":\"{3}\"}}",
            runId,
            errorKind,
            resourceName,
            detail);

    // -------------------------------------------------------------------------
    // THE parity gate: all three renderers agree on every scenario's verdict.
    // -------------------------------------------------------------------------

    [Fact]
    public void AllThreeRenderers_AgreeOnEveryScenarioVerdict()
    {
        // ONE fixed buffer, rendered three times — exactly the §14 single-stream model.
        var buffer = BuildFixedBuffer();

        using var terminalWriter = new StringWriter();
        using var htmlWriter = new StringWriter();
        using var junitWriter = new StringWriter();

        TerminalRenderer.Render(buffer, terminalWriter, diffLookup: null);
        HtmlRenderer.Render(buffer, htmlWriter, diffLookup: null);
        JunitXmlRenderer.Render(buffer, junitWriter);

        var terminalOutput = terminalWriter.ToString();
        var htmlOutput = htmlWriter.ToString();
        var junitOutput = junitWriter.ToString();

        // Derive each renderer's per-scenario verdict set independently of the others.
        var terminalVerdicts = ExtractTerminalVerdicts(terminalOutput);
        var htmlVerdicts = ExtractHtmlVerdicts(htmlOutput);
        var junitVerdicts = ExtractJunitVerdicts(junitOutput);

        // Each renderer surfaces EXACTLY the expected verdict for EXACTLY the four
        // scenarios — same keys, same tokens.  Equality over the dictionary catches
        // both a mis-mapped token AND a missing/extra scenario.
        Assert.Equal(ExpectedVerdicts, terminalVerdicts);
        Assert.Equal(ExpectedVerdicts, htmlVerdicts);
        Assert.Equal(ExpectedVerdicts, junitVerdicts);

        // And, transitively, the three renderers agree with one another — the
        // load-bearing "render the one stream differently, never per-audience
        // pipelines" guarantee.  Asserted explicitly so a failure names the divergence.
        Assert.Equal(terminalVerdicts, htmlVerdicts);
        Assert.Equal(htmlVerdicts, junitVerdicts);
    }

    // -------------------------------------------------------------------------
    // The JUnit primitive must MATCH the verdict (Pass⇒no child, Fail⇒<failure>,
    // EnvError⇒<error>, Inconclusive⇒<skipped>) — the verdict must not collapse onto
    // a single primitive when crossing into the JUnit audience.
    // -------------------------------------------------------------------------

    [Fact]
    public void JunitPrimitive_MatchesVerdict_ForEveryScenario()
    {
        var buffer = BuildFixedBuffer();

        using var junitWriter = new StringWriter();
        JunitXmlRenderer.Render(buffer, junitWriter);
        var doc = XDocument.Parse(junitWriter.ToString());

        // Pass: a clean <testcase>, no failure/error/skipped child.
        var pass = Testcase(doc, PassScenario);
        Assert.Null(pass.Element("failure"));
        Assert.Null(pass.Element("error"));
        Assert.Null(pass.Element("skipped"));

        // Fail: a <failure>, and ONLY a <failure>.
        var fail = Testcase(doc, FailScenario);
        Assert.NotNull(fail.Element("failure"));
        Assert.Null(fail.Element("error"));
        Assert.Null(fail.Element("skipped"));

        // EnvironmentError: an <error> (JUnit's distinct "infra broke" primitive),
        // NOT a <failure> — conflating the two destroys trust (§12.1).
        var env = Testcase(doc, EnvScenario);
        Assert.NotNull(env.Element("error"));
        Assert.Null(env.Element("failure"));
        Assert.Null(env.Element("skipped"));

        // Inconclusive: a <skipped>, NOT a <failure>.
        var inc = Testcase(doc, IncScenario);
        Assert.NotNull(inc.Element("skipped"));
        Assert.Null(inc.Element("failure"));
        Assert.Null(inc.Element("error"));
    }

    // -------------------------------------------------------------------------
    // The aggregate counts agree across the renderers that expose them.
    // -------------------------------------------------------------------------

    [Fact]
    public void AggregateCounts_AgreeAcrossRenderers()
    {
        var buffer = BuildFixedBuffer();

        using var terminalWriter = new StringWriter();
        using var htmlWriter = new StringWriter();
        using var junitWriter = new StringWriter();

        TerminalRenderer.Render(buffer, terminalWriter, diffLookup: null);
        HtmlRenderer.Render(buffer, htmlWriter, diffLookup: null);
        JunitXmlRenderer.Render(buffer, junitWriter);

        var htmlOutput = htmlWriter.ToString();

        // The fixed buffer holds exactly one scenario per verdict.
        const int expectedTests = 4;
        const int expectedFailures = 1;
        const int expectedErrors = 1;
        const int expectedSkipped = 1;

        // JUnit: the <testsuite> aggregate attributes mirror the verdict mix exactly
        // (Fail→failures, EnvError→errors, Inconclusive→skipped); Pass contributes only
        // to the total tests count.  The <testsuites> root mirrors the same shape.
        var doc = XDocument.Parse(junitWriter.ToString());
        var suite = doc.Descendants("testsuite").Single();
        Assert.Equal(expectedTests, ParseInt(suite, "tests"));
        Assert.Equal(expectedFailures, ParseInt(suite, "failures"));
        Assert.Equal(expectedErrors, ParseInt(suite, "errors"));
        Assert.Equal(expectedSkipped, ParseInt(suite, "skipped"));

        var suites = doc.Root!;
        Assert.Equal("testsuites", suites.Name.LocalName);
        Assert.Equal(expectedTests, ParseInt(suites, "tests"));
        Assert.Equal(expectedFailures, ParseInt(suites, "failures"));
        Assert.Equal(expectedErrors, ParseInt(suites, "errors"));
        Assert.Equal(expectedSkipped, ParseInt(suites, "skipped"));

        // HTML: the run-summary block tallies one of each verdict, consistent with the
        // JUnit aggregate (the per-scenario VerdictCounts summed across scenarios).  The
        // summary is now a real <table> (WCAG 1.3.1): each verdict is a row whose label
        // sits in a <th scope="row"> and whose count sits in the adjacent <td>, so the
        // markup is "…<span class="verdict">PASS</span></th><td>1</td>".  Assert each
        // count of 1 is present for its verdict label in that row shape.
        Assert.Contains(">PASS</span></th><td>1</td>", htmlOutput, StringComparison.Ordinal);
        Assert.Contains(">FAIL</span></th><td>1</td>", htmlOutput, StringComparison.Ordinal);
        Assert.Contains(">ENV_ERROR</span></th><td>1</td>", htmlOutput, StringComparison.Ordinal);
        Assert.Contains(">INCONCLUSIVE</span></th><td>1</td>", htmlOutput, StringComparison.Ordinal);

        // Terminal: each scenario-completed summary line carries its own per-verdict
        // count of 1.  The single passing scenario's summary line shows pass=1, the
        // failing one fail=1, the env-error one envError=1, the inconclusive one
        // inconclusive=1 — proving the terminal renderer's counts agree with the others.
        Assert.Equal(1, CountTerminalScenarioField(terminalWriter.ToString(), PassScenario, "pass"));
        Assert.Equal(1, CountTerminalScenarioField(terminalWriter.ToString(), FailScenario, "fail"));
        Assert.Equal(1, CountTerminalScenarioField(terminalWriter.ToString(), EnvScenario, "envError"));
        Assert.Equal(1, CountTerminalScenarioField(terminalWriter.ToString(), IncScenario, "inconclusive"));
    }

    // -------------------------------------------------------------------------
    // Per-renderer verdict extraction.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Extracts each scenario's verdict token from the terminal output.  The terminal
    /// renderer prints one summary line per scenario:
    /// <c>Scenario '{id}': {TOKEN}  (pass=… fail=… envError=… inconclusive=…)</c>.
    /// The token sits between the scenario-id quote+colon and the counts parenthesis.
    /// </summary>
    private static SortedDictionary<string, string> ExtractTerminalVerdicts(string output)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var scenarioId in ExpectedVerdicts.Keys)
        {
            // Match only the scenario-COMPLETED summary line: it begins
            // "Scenario '{id}': " (the started line is "Scenario '{id}' started",
            // with no colon after the closing quote, so it cannot match).
            var prefix = string.Format(CultureInfo.InvariantCulture, "Scenario '{0}': ", scenarioId);

            string? token = null;
            foreach (var line in SplitLines(output))
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                // The verdict token is the run of non-space characters immediately
                // after the prefix (the renderer separates it from the counts with two
                // spaces).
                var rest = line.Substring(prefix.Length).TrimStart();
                var space = rest.IndexOf(' ', StringComparison.Ordinal);
                token = space < 0 ? rest : rest.Substring(0, space);
                break;
            }

            Assert.True(
                token is not null,
                $"Terminal output has no scenario-completed summary line for '{scenarioId}'.");
            result[scenarioId] = token!;
        }

        return result;
    }

    /// <summary>
    /// Extracts each scenario's verdict token from the HTML output.  The report is an
    /// HTML5 document (void elements such as <c>&lt;meta charset="utf-8"&gt;</c> and the
    /// <c>&lt;!DOCTYPE html&gt;</c> preamble), so it is NOT XML-parseable; the sibling
    /// <c>HtmlRendererTests</c> deliberately assert on the string for the same reason.
    /// This extractor therefore scans the markup line-by-line.
    /// </summary>
    /// <remarks>
    /// Each scenario is rendered as a section opened by
    /// <c>&lt;section class="scenario verdict-{token}"&gt;</c> immediately followed by
    /// <c>&lt;h2&gt;Scenario: {id} &lt;span class="verdict"&gt;{TOKEN}&lt;/span&gt;…&lt;/h2&gt;</c>.
    /// For each scenario section the extractor reads the verdict from the SECTION CLASS
    /// (the machine-facing cue) AND confirms the heading's human-readable TOKEN text
    /// agrees with it — a class/label divergence inside one renderer is itself a defect.
    /// The run-summary block also uses <c>verdict-*</c> classes but is NOT a
    /// <c>scenario</c> section, so it is skipped.
    /// </remarks>
    private static SortedDictionary<string, string> ExtractHtmlVerdicts(string output)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);

        var lines = SplitLines(output);
        string? pendingClassToken = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // A scenario section opening tag carries the verdict CSS class.  Capture the
            // token and wait for the very next scenario heading to bind it to an id.
            if (line.StartsWith("<section class=\"scenario", StringComparison.Ordinal))
            {
                var classes = ExtractClassList(line);
                pendingClassToken = VerdictTokenFromCssClass(classes);
                continue;
            }

            // The scenario heading: "<h2>Scenario: {id} <span class="verdict">{TOKEN}</span>…".
            const string headingMarker = "<h2>Scenario: ";
            if (!line.StartsWith(headingMarker, StringComparison.Ordinal))
            {
                continue;
            }

            var afterMarker = line.Substring(headingMarker.Length);
            var scenarioId = ExpectedVerdicts.Keys.FirstOrDefault(id =>
                afterMarker.StartsWith(id, StringComparison.Ordinal));
            if (scenarioId is null)
            {
                continue;
            }

            Assert.True(
                pendingClassToken is not null,
                $"HTML scenario section for '{scenarioId}' carries no recognised verdict-* class.");

            // The heading's human-readable token (inside the <span class="verdict">) must
            // agree with the section's CSS-class token.
            Assert.Contains(
                ">" + pendingClassToken + "</span>",
                line,
                StringComparison.Ordinal);

            result[scenarioId] = pendingClassToken!;
            pendingClassToken = null;
        }

        return result;
    }

    /// <summary>
    /// Pulls the space-separated CSS class list out of a <c>class="…"</c> attribute on
    /// a single markup line.  Returns an empty array when no class attribute is present.
    /// </summary>
    private static string[] ExtractClassList(string line)
    {
        const string attr = "class=\"";
        var start = line.IndexOf(attr, StringComparison.Ordinal);
        if (start < 0)
        {
            return Array.Empty<string>();
        }

        start += attr.Length;
        var end = line.IndexOf('"', start);
        if (end < 0)
        {
            return Array.Empty<string>();
        }

        return line.Substring(start, end - start)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Extracts each scenario's verdict from the JUnit XML output via the authoritative
    /// <c>vouchfx.verdict</c> property on its <c>&lt;testcase&gt;</c>.
    /// </summary>
    private static SortedDictionary<string, string> ExtractJunitVerdicts(string output)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var doc = XDocument.Parse(output);

        foreach (var testcase in doc.Descendants("testcase"))
        {
            var name = (string?)testcase.Attribute("name");
            if (name is null || !ExpectedVerdicts.ContainsKey(name))
            {
                continue;
            }

            var property = testcase
                .Descendants("property")
                .SingleOrDefault(p => (string?)p.Attribute("name") == "vouchfx.verdict");

            Assert.True(
                property is not null,
                $"JUnit testcase '{name}' carries no vouchfx.verdict property.");

            result[name] = (string?)property!.Attribute("value")
                ?? throw new Xunit.Sdk.XunitException(
                    $"JUnit vouchfx.verdict property for '{name}' has no value attribute.");
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Small helpers.
    // -------------------------------------------------------------------------

    private static XElement Testcase(XDocument doc, string scenarioId) =>
        doc.Descendants("testcase").Single(tc => (string?)tc.Attribute("name") == scenarioId);

    private static int ParseInt(XElement element, string attribute) =>
        int.Parse((string?)element.Attribute(attribute) ?? "0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Maps a scenario section's CSS classes to the verdict token they encode, or
    /// <see langword="null"/> when no <c>verdict-*</c> class is present.
    /// </summary>
    private static string? VerdictTokenFromCssClass(IEnumerable<string> classes)
    {
        foreach (var cssClass in classes)
        {
            switch (cssClass)
            {
                case "verdict-pass":
                    return "PASS";
                case "verdict-fail":
                    return "FAIL";
                case "verdict-env-error":
                    return "ENV_ERROR";
                case "verdict-inconclusive":
                    return "INCONCLUSIVE";
                default:
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the value of a single per-verdict field (e.g. <c>pass</c>, <c>envError</c>)
    /// from a scenario's terminal summary line:
    /// <c>Scenario '{id}': {TOKEN}  (pass=1 fail=0 envError=0 inconclusive=0)</c>.
    /// </summary>
    private static int CountTerminalScenarioField(string output, string scenarioId, string field)
    {
        var prefix = string.Format(CultureInfo.InvariantCulture, "Scenario '{0}': ", scenarioId);

        foreach (var line in SplitLines(output))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var token = field + "=";
            var index = line.IndexOf(token, StringComparison.Ordinal);
            Assert.True(
                index >= 0,
                $"Terminal summary line for '{scenarioId}' has no '{field}=' field: {line}");

            var start = index + token.Length;
            var end = start;
            while (end < line.Length && char.IsDigit(line[end]))
            {
                end++;
            }

            return int.Parse(line.AsSpan(start, end - start), CultureInfo.InvariantCulture);
        }

        throw new Xunit.Sdk.XunitException(
            $"Terminal output has no scenario-completed summary line for '{scenarioId}'.");
    }

    private static string[] SplitLines(string text) =>
        text.Split(NewlineSeparators, StringSplitOptions.None);
}
