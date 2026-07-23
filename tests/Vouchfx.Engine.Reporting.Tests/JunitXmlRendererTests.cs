// Tests for JunitXmlRenderer — the JUnit XML output renderer (S09-G-02).
//
// Strategy mirrors HtmlRendererTests / TerminalRendererTests: build recorded event
// streams from typed payload records via EventStreamJson.ToLine<T>, render to a
// StringWriter, then assert on the emitted XML.  The JUnit report is driven by the
// SAME buffered JSON Lines event stream the other renderers consume — "render the
// one stream differently, never a per-audience pipeline" (§14).
//
// The four acceptance gates exercised here:
//   1. No-collapse mapping (THE acceptance gate) — a 4-scenario suite (one Pass, one
//      Fail, one EnvironmentError, one Inconclusive) yields EXACTLY one <failure>, one
//      <error>, one <skipped>, and one clean <testcase>; the <testsuite> aggregate
//      attributes (failures=1 errors=1 skipped=1) match the mapping.  This is the
//      "the four verdicts survive the mapping" requirement: Fail, EnvError, and
//      Inconclusive must NOT all collapse to <failure>.
//   2. Verdict survives — each <testcase> carries a vouchfx.verdict property with the
//      EXACT taxonomy token (PASS/FAIL/ENV_ERROR/INCONCLUSIVE), so a consumer can
//      recover EnvError-vs-Inconclusive-vs-Fail rather than seeing them collapsed.
//   3. Valid + escaped XML — the output parses as XML (XDocument.Parse), and a
//      scenario id carrying <, &, and " is escaped so no raw markup breaks the doc.
//   4. Secret-safety (the load-bearing invariant, §17) — a secret-derived
//      substitution and a planted resolved-secret sentinel never appear in the XML.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

public sealed class JunitXmlRendererTests
{
    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    // -------------------------------------------------------------------------
    // Test 1 (THE acceptance gate): the four verdicts do not collapse.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_FourScenarioSuite_MapsEachVerdictToItsDistinctJunitPrimitive()
    {
        // Four scenarios, one per verdict in the §12.1 taxonomy.
        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "passing", File = "passing.e2e.yaml" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-1",
                ScenarioId = "passing",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),

            Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "failing", File = "failing.e2e.yaml" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-1",
                ScenarioId = "failing",
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
            }),

            Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "broken-env", File = "broken-env.e2e.yaml" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-1",
                ScenarioId = "broken-env",
                Verdict = Verdict.EnvironmentError,
                Counts = new VerdictCounts { EnvError = 1 },
            }),

            Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "timed-out", File = "timed-out.e2e.yaml" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-1",
                ScenarioId = "timed-out",
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }),
        };

        using var writer = new StringWriter();
        JunitXmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        var doc = XDocument.Parse(output);

        var testcases = doc.Descendants("testcase").ToList();
        Assert.Equal(4, testcases.Count);

        // Exactly one <failure>, one <error>, one <skipped> across the whole document.
        Assert.Single(doc.Descendants("failure"));
        Assert.Single(doc.Descendants("error"));
        Assert.Single(doc.Descendants("skipped"));

        // Exactly one clean <testcase> — the Pass — with no failure/error/skipped child.
        var clean = testcases.Where(tc =>
            tc.Element("failure") is null
            && tc.Element("error") is null
            && tc.Element("skipped") is null).ToList();
        Assert.Single(clean);
        Assert.Equal("passing", (string?)clean[0].Attribute("name"));

        // The Fail scenario carries a <failure>, NOT an <error> or <skipped>.
        var fail = testcases.Single(tc => (string?)tc.Attribute("name") == "failing");
        Assert.NotNull(fail.Element("failure"));
        Assert.Null(fail.Element("error"));
        Assert.Null(fail.Element("skipped"));

        // The EnvironmentError scenario carries an <error> — JUnit's distinct
        // "infrastructure broke" primitive — NOT a <failure>.
        var env = testcases.Single(tc => (string?)tc.Attribute("name") == "broken-env");
        Assert.NotNull(env.Element("error"));
        Assert.Null(env.Element("failure"));
        Assert.Null(env.Element("skipped"));

        // The Inconclusive scenario carries a <skipped> — NOT a <failure>.
        var inc = testcases.Single(tc => (string?)tc.Attribute("name") == "timed-out");
        Assert.NotNull(inc.Element("skipped"));
        Assert.Null(inc.Element("failure"));
        Assert.Null(inc.Element("error"));

        // The <testsuite> aggregate attributes match the mapping exactly.
        var suite = doc.Descendants("testsuite").Single();
        Assert.Equal("4", (string?)suite.Attribute("tests"));
        Assert.Equal("1", (string?)suite.Attribute("failures"));
        Assert.Equal("1", (string?)suite.Attribute("errors"));
        Assert.Equal("1", (string?)suite.Attribute("skipped"));

        // The <testsuites> root mirrors the same aggregates.
        var suites = doc.Root;
        Assert.NotNull(suites);
        Assert.Equal("testsuites", suites!.Name.LocalName);
        Assert.Equal("4", (string?)suites.Attribute("tests"));
        Assert.Equal("1", (string?)suites.Attribute("failures"));
        Assert.Equal("1", (string?)suites.Attribute("errors"));
        Assert.Equal("1", (string?)suites.Attribute("skipped"));
    }

    // -------------------------------------------------------------------------
    // Test 2: the ORIGINAL vouchfx verdict survives the JUnit mapping.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_EachTestcase_CarriesOriginalVouchfxVerdictProperty()
    {
        var lines = new[]
        {
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-2",
                ScenarioId = "passing",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-2",
                ScenarioId = "failing",
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-2",
                ScenarioId = "broken-env",
                Verdict = Verdict.EnvironmentError,
                Counts = new VerdictCounts { EnvError = 1 },
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-2",
                ScenarioId = "timed-out",
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
            }),
        };

        using var writer = new StringWriter();
        JunitXmlRenderer.Render(lines, writer);
        var doc = XDocument.Parse(writer.ToString());

        // For each scenario, the testcase MUST carry a vouchfx.verdict property with the
        // exact taxonomy token — so EnvError vs Inconclusive vs Fail vs Pass are
        // recoverable, not collapsed by the JUnit primitive mapping.
        AssertVerdictProperty(doc, "passing", "PASS");
        AssertVerdictProperty(doc, "failing", "FAIL");
        AssertVerdictProperty(doc, "broken-env", "ENV_ERROR");
        AssertVerdictProperty(doc, "timed-out", "INCONCLUSIVE");
    }

    private static void AssertVerdictProperty(XDocument doc, string scenarioId, string expectedToken)
    {
        var testcase = doc.Descendants("testcase")
            .Single(tc => (string?)tc.Attribute("name") == scenarioId);

        var property = testcase
            .Descendants("property")
            .SingleOrDefault(p => (string?)p.Attribute("name") == "vouchfx.verdict");

        Assert.NotNull(property);
        Assert.Equal(expectedToken, (string?)property!.Attribute("value"));
    }

    // -------------------------------------------------------------------------
    // Test 3: the document is valid, well-formed XML and every dynamic string is
    //         XML-escaped (a scenario id carrying <, &, " does not break the doc).
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_DynamicStringsAreXmlEscaped_AndDocumentParses()
    {
        const string evilScenarioId = "<scenario name=\"x\"> & </scenario>";

        var lines = new[]
        {
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-3",
                ScenarioId = evilScenarioId,
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
            }),
        };

        using var writer = new StringWriter();
        JunitXmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // (a) The output declares itself as XML and parses cleanly — proof the raw
        //     angle brackets / ampersand from the scenario id did NOT break the doc.
        Assert.Contains("<?xml version=\"1.0\" encoding=\"utf-8\"?>", output, StringComparison.Ordinal);
        var doc = XDocument.Parse(output);

        // (b) The raw injected open tag must NOT appear unescaped.
        Assert.DoesNotContain("<scenario name=", output, StringComparison.Ordinal);

        // (c) The escaped forms are present instead.
        Assert.Contains("&lt;scenario", output, StringComparison.Ordinal);
        Assert.Contains("&amp;", output, StringComparison.Ordinal);

        // (d) Round-tripped through the XML parser, the scenario-id attribute value is
        //     restored byte-for-byte — the escaping is lossless, not lossy stripping.
        var testcase = doc.Descendants("testcase").Single();
        Assert.Equal(evilScenarioId, (string?)testcase.Attribute("name"));
    }

    // -------------------------------------------------------------------------
    // Test 3b (issue #279 — HTML/script-injection hole, JUnit side): hostile markup
    //          in BOTH the "name" attribute (ScenarioId) AND the "classname"
    //          attribute (the author-supplied .e2e.yaml file path) is XML-encoded
    //          — including an attribute-breakout attempt (a leading `"><evil>`
    //          sequence) — and the same value threaded into the <failure> message
    //          attribute AND element body round-trips correctly.  Test 3 above only
    //          covers the scenario id in element/attribute text; this covers the
    //          file-derived classname attribute the earlier test does not exercise.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_HostileMarkupInScenarioIdAndFile_IsXmlEncodedInAttributesAndText()
    {
        const string evilScenarioId = "<script>alert(1)</script>&\"'";
        const string evilFile = "suite\"><evil>&'.e2e.yaml";

        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-279", ScenarioId = evilScenarioId, File = evilFile }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-279",
                ScenarioId = evilScenarioId,
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
            }),
        };

        using var writer = new StringWriter();
        JunitXmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The document is still valid, well-formed XML — the raw markup did not
        // break out of the attribute or the element text.
        var doc = XDocument.Parse(output);

        // Raw hostile fragments never appear unescaped.
        Assert.DoesNotContain("<script>alert(1)</script>", output, StringComparison.Ordinal);
        Assert.DoesNotContain("suite\"><evil>", output, StringComparison.Ordinal);

        // Round-tripped through the parser, both the "classname" (File) attribute and
        // the "name" (ScenarioId) attribute restore byte-for-byte — proof the
        // encoding is lossless entity-escaping, not lossy stripping.
        var testcase = doc.Descendants("testcase").Single();
        Assert.Equal(evilScenarioId, (string?)testcase.Attribute("name"));
        Assert.Equal(evilFile, (string?)testcase.Attribute("classname"));

        // The <failure> message attribute AND element body (both built from the
        // scenario id) also round-trip correctly.
        var failure = doc.Descendants("failure").Single();
        Assert.Contains(evilScenarioId, (string?)failure.Attribute("message"), StringComparison.Ordinal);
        Assert.Contains(evilScenarioId, failure.Value, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 4 (MANDATORY acceptance gate): no resolved secret VALUE can appear.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_SecretDerivedSubstitutionAndPlantedSentinel_NeverLeakValue()
    {
        const string sentinelSecretValue = "super-secret-value-xyz";

        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-4", ScenarioId = "secret-flow", File = "secret-flow.e2e.yaml" }),

            // A step-completed carrying a secret-derived substitution: the Placeholder is
            // the NON-sensitive reference label (env/API_TOKEN), never the value (§17).
            Line(new StepCompletedEvent
            {
                RunId = "run-4",
                StepId = "call-api",
                Verdict = Verdict.Fail,
                DurationMs = 30,
                Substitutions = new[]
                {
                    new SubstitutionRef("env/API_TOKEN", OriginStepId: null, SecretDerived: true),
                },
            }),

            // A hand-crafted line that smuggles the resolved sentinel into an UNKNOWN
            // "resolvedValue" field that the renderer must never surface verbatim, and an
            // observation that must not be dumped.
            "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"step-completed\",\"ts\":\"2026-01-01T00:00:00Z\","
                + "\"runId\":\"run-4\",\"stepId\":\"leak-bait\",\"verdict\":\"FAIL\",\"durationMs\":1,"
                + "\"resolvedValue\":\"" + sentinelSecretValue + "\","
                + "\"observation\":{\"actual\":\"" + sentinelSecretValue + "\"}}",

            Line(new ScenarioCompletedEvent
            {
                RunId = "run-4",
                ScenarioId = "secret-flow",
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 2 },
            }),
        };

        using var writer = new StringWriter();
        JunitXmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // The document is still valid XML.
        var doc = XDocument.Parse(output);
        Assert.NotEmpty(doc.Descendants("testcase"));

        // THE LOAD-BEARING ASSERTION: the resolved secret value never appears anywhere
        // in the XML — not in a message, not in an attribute, not in element text.
        Assert.DoesNotContain(sentinelSecretValue, output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Test 5: tolerance — blank and malformed lines and unknown event types are
    //         skipped, and a partial buffer (no scenario-completed) still produces a
    //         well-formed document without throwing.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_BlankMalformedAndUnknownLines_AreToleratedAndDocumentIsWellFormed()
    {
        var lines = new[]
        {
            string.Empty,
            "   ",
            "{ this is not valid json",
            "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"future-unknown-event\",\"ts\":\"2026-01-01T00:00:00Z\",\"runId\":\"run-5\"}",
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-5",
                ScenarioId = "only-survivor",
                Verdict = Verdict.Pass,
                Counts = new VerdictCounts { Pass = 1 },
            }),
        };

        using var writer = new StringWriter();

        var ex = Record.Exception(() => JunitXmlRenderer.Render(lines, writer));
        Assert.Null(ex);

        var doc = XDocument.Parse(writer.ToString());
        var testcase = doc.Descendants("testcase").Single();
        Assert.Equal("only-survivor", (string?)testcase.Attribute("name"));
    }

    // -------------------------------------------------------------------------
    // Test 6 (C145-2 / C146): XML-1.0-forbidden characters reachable in a single
    // BMP char are dropped.
    //
    // XML 1.0 permits only three characters below U+0020 — TAB (U+0009), LF
    // (U+000A) and CR (U+000D).  Every other C0 control (U+0000–U+0008, U+000B,
    // U+000C, U+000E–U+001F) is FORBIDDEN and cannot even be represented as a
    // numeric character reference.  The XML 1.0 Char production ALSO forbids the
    // two BMP non-characters U+FFFE and U+FFFF (they fall outside the permitted
    // [#xE000-#xFFFD] range), and these are reachable because System.Text.Json
    // deserialises "U+FFFE"/"U+FFFF" into ordinary .NET string characters that flow through
    // a scenario id or file path.  So if a dynamic string (a scenario id, a
    // failure message, …) carries any of these the emitted document is
    // non-parseable and breaks CI ingestion.  XmlEscape must therefore DROP the
    // forbidden controls AND U+FFFE/U+FFFF while preserving TAB/LF/CR, still
    // entity-escaping the markup chars, and leaving legitimate astral-plane text
    // (a valid surrogate pair, e.g. an emoji) untouched.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_ForbiddenC0ControlCharacters_AreDroppedAndDocumentParses()
    {
        // The XML-1.0-forbidden C0 controls, built from code points (no literal
        // control bytes in the source): NUL, BEL, BS, VT, FF and a high one at
        // U+001F.  None of these may appear in XML 1.0 — not even as a numeric
        // character reference.
        var forbidden = new[]
        {
            (char)0x00, (char)0x07, (char)0x08, (char)0x0B, (char)0x0C, (char)0x1F,
        };
        var forbiddenText = new string(forbidden);

        // The two BMP non-characters U+FFFE/U+FFFF, also XML-1.0-illegal and also
        // reachable here (System.Text.Json deserialises them verbatim).  Built
        // from integer casts — NEVER literal "U+FFFE"/"U+FFFF" bytes in the source.
        var nonCharacters = new[] { (char)0xFFFE, (char)0xFFFF };
        var nonCharacterText = new string(nonCharacters);

        // A legitimate astral-plane character — a grinning-face emoji encoded as a
        // VALID surrogate pair.  This is legal XML and MUST survive unchanged; it
        // guards against an over-broad predicate that strips anything "weird".
        const string astral = "\U0001F600";

        // The three LEGAL whitespace controls and the five markup chars, woven in
        // alongside the forbidden controls so the fix cannot pass by either
        // stripping ALL controls or by mangling the markup escaping.
        const string legal = "\t\n\r";
        const string markup = "&<>\"'";

        var scenarioId = "scn" + forbiddenText + nonCharacterText + astral + legal + markup;
        var failureMessage = "boom" + forbiddenText + nonCharacterText + astral + legal + markup;

        var lines = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-6", ScenarioId = scenarioId, File = "ctrl.e2e.yaml" }),

            // A failed step whose observation message carries the same hostile text
            // so the forbidden controls also reach a <failure> body, not only an
            // attribute value.
            "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"step-completed\",\"ts\":\"2026-01-01T00:00:00Z\","
                + "\"runId\":\"run-6\",\"stepId\":\"hostile-step\",\"verdict\":\"FAIL\",\"durationMs\":1,"
                + "\"observation\":{\"message\":"
                + System.Text.Json.JsonSerializer.Serialize(failureMessage) + "}}",

            Line(new ScenarioCompletedEvent
            {
                RunId = "run-6",
                ScenarioId = scenarioId,
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
            }),
        };

        using var writer = new StringWriter();
        JunitXmlRenderer.Render(lines, writer);
        var output = writer.ToString();

        // (a) The full document parses — a single forbidden control anywhere would
        //     make XDocument.Parse throw (this is the RED assertion before the fix).
        var doc = XDocument.Parse(output);

        // (b) None of the forbidden C0 control characters survive into the output.
        foreach (var forbiddenChar in forbidden)
        {
            Assert.DoesNotContain(forbiddenChar.ToString(), output, StringComparison.Ordinal);
        }

        // (b2) Neither BMP non-character (U+FFFE/U+FFFF) survives — these are the
        //      characters that, before the fix, made XDocument.Parse throw
        //      ("... 0xFFFF ... is an invalid character"); the parse in (a) would be
        //      RED and this assertion never reached.
        foreach (var nonCharacter in nonCharacters)
        {
            Assert.DoesNotContain(nonCharacter.ToString(), output, StringComparison.Ordinal);
        }

        // (b3) The legitimate astral-plane emoji (a VALID surrogate pair) IS preserved
        //      — guarding against an over-broad predicate that strips legal characters.
        Assert.Contains(astral, output, StringComparison.Ordinal);

        // (c) The LEGAL whitespace controls DO survive (escaping must not be a blanket
        //     "strip all controls").  The scenario id — which carries the TAB/LF/CR — is
        //     echoed into the <failure> element BODY, whose text content preserves TAB
        //     and LF verbatim (unlike an attribute value, which the parser normalises to
        //     spaces).  Parse the failure element and confirm TAB and LF survived; if
        //     the renderer had stripped them, the legitimate whitespace would be gone.
        var testcase = doc.Descendants("testcase").Single();
        Assert.NotNull((string?)testcase.Attribute("name"));

        var failureBody = doc.Descendants("failure").Single().Value;
        Assert.Contains("\t", failureBody, StringComparison.Ordinal);
        Assert.Contains("\n", failureBody, StringComparison.Ordinal);

        // (d) The markup chars are still entity-escaped — the control-character
        //     handling did not disturb the existing & < > " ' escaping.
        Assert.Contains("&amp;", output, StringComparison.Ordinal);
        Assert.Contains("&lt;", output, StringComparison.Ordinal);
        Assert.Contains("&gt;", output, StringComparison.Ordinal);
    }
}

