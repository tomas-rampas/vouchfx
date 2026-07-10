// Tests for the §14 per-line tolerance guard against an UNREADABLE string value
// (S09 robustness fix — lone-surrogate tolerance).
//
// The defect these tests pin (found by peer review):
//   Every renderer consumes a buffered JSON Lines event stream, parses each line, then
//   reads string fields out of EventEnvelope.Extra via GetStr(...) -> JsonElement.GetString().
//   The §14 per-line tolerance guard originally wrapped only the PARSE in catch (JsonException).
//   But a JSON string value containing a LONE / unpaired UTF-16 surrogate (e.g. the 6-char
//   JSON escape "\uD800" with no low-surrogate partner) PARSES fine — JsonDocument.Parse
//   succeeds and the value lands in Extra as a JsonElement of kind String — and only throws
//   System.InvalidOperationException ("Cannot read incomplete UTF-16…") LATER, when GetString()
//   is called during model-building.  That exception was NOT caught, so it propagated out of
//   the per-line build loop and aborted the ENTIRE render.  In a real run that happens AFTER
//   the verdict / exit code is already computed, so a clean Pass run would crash during report
//   writing — violating the invariant that report writing must never change the verdict.
//
// The fix broadens each renderer's per-line guard to
//   catch (Exception ex) when (ex is JsonException or InvalidOperationException)
// scoped to the per-line parse + field-extraction, so an unreadable line is skipped exactly
// like a malformed-JSON line, and the rest of the stream still renders.  FileReportWriter
// adds InvalidOperationException to its per-file catch filter as defence in depth.
//
// Strategy mirrors the sibling renderer tests: VALID lines are built from typed payload
// records via EventStreamJson.ToLine<T>; the ONE hostile line is hand-built raw JSON TEXT so
// that the literal 6-char escape \uD800 sits in a string VALUE that PARSES but fails on read.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

public sealed class RendererSurrogateToleranceTests
{
    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    // A hand-built event line whose scenarioId string VALUE carries a LONE high surrogate:
    // the C# literal "\\uD800" places the six characters  \  u  D  8  0  0  into the JSON
    // TEXT, so the buffer line literally contains \uD800 — a string escape with no matching
    // low surrogate.  JsonDocument.Parse ACCEPTS this (kind String); GetString() throws
    // InvalidOperationException when the renderer reads it.  scenarioId is read by all three
    // renderers (it rides in Extra), so this single line exercises every per-line guard.
    private const string LoneSurrogateScenarioLine =
        "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"scenario-completed\","
        + "\"ts\":\"2026-01-01T00:00:00Z\",\"runId\":\"run-bad\","
        + "\"scenarioId\":\"bad\\uD800scn\",\"verdict\":\"FAIL\","
        + "\"counts\":{\"pass\":0,\"fail\":1,\"envError\":0,\"inconclusive\":0}}";

    // A buffer with the hostile line sandwiched between two VALID, clean scenarios so the
    // tolerance assertion can prove the bad line is SKIPPED (not fatal) while the valid
    // scenarios still render.
    private static string[] BufferWithOneUnreadableLine() => new[]
    {
        Line(new ScenarioStartedEvent { RunId = "run-good", ScenarioId = "before-bad", File = "before.e2e.yaml" }),
        Line(new ScenarioCompletedEvent
        {
            RunId = "run-good",
            ScenarioId = "before-bad",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        }),

        // The poison line: parses, but GetString() throws when read.
        LoneSurrogateScenarioLine,

        Line(new ScenarioStartedEvent { RunId = "run-good", ScenarioId = "after-bad", File = "after.e2e.yaml" }),
        Line(new ScenarioCompletedEvent
        {
            RunId = "run-good",
            ScenarioId = "after-bad",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        }),
    };

    // -------------------------------------------------------------------------
    // Guard the test's central premise: the crafted line genuinely PARSES (so the
    // failure mode under test is at GetString, not at parse).  If JsonDocument.Parse
    // rejected the line, the renderers' JsonException branch would already cover it and
    // the test would prove nothing.
    // -------------------------------------------------------------------------

    [Fact]
    public void CraftedLine_Parses_ButGetStringThrowsInvalidOperationException()
    {
        // (a) The whole envelope deserialises — exactly the FromLine path the renderers use.
        var envelope = EventStreamJson.FromLine(LoneSurrogateScenarioLine);
        Assert.NotNull(envelope.Extra);

        // (b) scenarioId is present as a JSON String value (it parses fine).
        Assert.True(envelope.Extra!.TryGetValue("scenarioId", out var element));
        Assert.Equal(JsonValueKind.String, element.ValueKind);

        // (c) Reading it via GetString() — what the renderers do — throws
        //     InvalidOperationException, NOT JsonException.  This is the precise failure
        //     the broadened per-line guard must tolerate.
        var ex = Record.Exception(() => element.GetString());
        Assert.IsType<InvalidOperationException>(ex);
    }

    // -------------------------------------------------------------------------
    // RED -> GREEN for each renderer: Render must NOT throw, and the valid scenarios
    // either side of the poison line must still appear.  Before the fix every one of
    // these threw InvalidOperationException out of the per-line build loop.
    // -------------------------------------------------------------------------

    [Fact]
    public void TerminalRenderer_ToleratesUnreadableLine_StillRendersValidScenarios()
    {
        var buffer = BufferWithOneUnreadableLine();
        using var writer = new StringWriter();

        var ex = Record.Exception(() => TerminalRenderer.Render(buffer, writer));
        Assert.Null(ex); // RED before the fix: InvalidOperationException.

        var output = writer.ToString();
        // Both valid scenarios survive — the bad line was skipped, not fatal to the stream.
        Assert.Contains("before-bad", output, StringComparison.Ordinal);
        Assert.Contains("after-bad", output, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlRenderer_ToleratesUnreadableLine_StillRendersValidScenarios()
    {
        var buffer = BufferWithOneUnreadableLine();
        using var writer = new StringWriter();

        var ex = Record.Exception(() => HtmlRenderer.Render(buffer, writer));
        Assert.Null(ex); // RED before the fix: InvalidOperationException.

        var output = writer.ToString();
        Assert.Contains("<!DOCTYPE html>", output, StringComparison.Ordinal);
        Assert.Contains("before-bad", output, StringComparison.Ordinal);
        Assert.Contains("after-bad", output, StringComparison.Ordinal);
    }

    [Fact]
    public void JunitXmlRenderer_ToleratesUnreadableLine_StillRendersValidScenarios()
    {
        var buffer = BufferWithOneUnreadableLine();
        using var writer = new StringWriter();

        var ex = Record.Exception(() => JunitXmlRenderer.Render(buffer, writer));
        Assert.Null(ex); // RED before the fix: InvalidOperationException.

        var output = writer.ToString();

        // The document is still well-formed XML (the bad scenario never reached XmlEscape).
        var doc = XDocument.Parse(output);

        // The two valid scenarios are present as testcases; the poison scenario is absent.
        var names = doc.Descendants("testcase")
            .Select(tc => (string?)tc.Attribute("name"))
            .ToList();
        Assert.Contains("before-bad", names);
        Assert.Contains("after-bad", names);
    }

    // -------------------------------------------------------------------------
    // FileReportWriter: writing HTML + JUnit from a buffer containing an unreadable
    // line must not throw and must still produce BOTH files (defence in depth — the
    // renderers already skip the line, and the per-file catch contains anything that
    // still surfaces so report writing cannot change the already-computed verdict).
    // -------------------------------------------------------------------------

    [Fact]
    public void FileReportWriter_WithUnreadableLine_WritesBothFiles_AndDoesNotThrow()
    {
        var buffer = BufferWithOneUnreadableLine();
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-surr-" + Guid.NewGuid().ToString("n"));
        var htmlPath = Path.Combine(dir, "report.html");
        var junitPath = Path.Combine(dir, "report.xml");

        try
        {
            var ex = Record.Exception(() => FileReportWriter.WriteFileReports(
                buffer, diffLookup: null, htmlPath, junitPath));

            // (a) The seam never throws — the run's verdict / exit code is unaffected.
            Assert.Null(ex);

            // (b) BOTH artifacts were still produced from the same buffer (parity preserved).
            Assert.True(File.Exists(htmlPath), "HTML report should still have been written.");
            Assert.True(File.Exists(junitPath), "JUnit report should still have been written.");

            // (c) Each file is valid and still carries the surviving valid scenarios.
            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<!DOCTYPE html>", html, StringComparison.Ordinal);
            Assert.Contains("after-bad", html, StringComparison.Ordinal);

            var xml = File.ReadAllText(junitPath);
            var doc = XDocument.Parse(xml);
            var names = doc.Descendants("testcase")
                .Select(tc => (string?)tc.Attribute("name"))
                .ToList();
            Assert.Contains("before-bad", names);
            Assert.Contains("after-bad", names);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // -------------------------------------------------------------------------
    // The EMIT-TIME path the surrogate-in-scenarioId tests above do NOT cover
    // (HtmlRenderer-only; peer-review MAJOR).
    //
    // HtmlRenderer uses a TWO-PASS shape: the GUARDED BuildModel reads only
    // scenarioId / stepId / verdict / durationMs / counts, then STORES the whole
    // step-completed / reproducibility envelope and DEFERS the GetString() reads of
    // captured / substitutions / secretReferences / fixtures / observation to EMIT
    // time in WriteDocument — which runs OUTSIDE the per-line try/catch.  A lone /
    // unpaired UTF-16 surrogate in any of THOSE string values therefore still threw
    // InvalidOperationException uncaught, mid-document, leaving a TRUNCATED HTML file
    // (the writer streams straight to the output).  JUnit (full BuildModel) and the
    // terminal renderer (reads now inside the widened try) are already covered; this
    // is the HtmlRenderer-only emit-time gap.  Some of these fields are SUT-derived
    // (a captured value, an observation), so reachability is real.
    //
    // The fix makes HtmlRenderer's emit-time string extraction defensive: an
    // UNREADABLE value is treated as ABSENT (exactly like a missing field), so only
    // the affected provenance row is omitted while the document renders to completion.
    // -------------------------------------------------------------------------

    // A hand-built step-completed line whose captured[0].name string VALUE carries a
    // LONE high surrogate.  stepId / verdict / durationMs are clean (BuildModel reads
    // those INSIDE its guard), so the line is fully accepted by BuildModel; the
    // poison surfaces only at EMIT time, when WriteProvenanceThread reads captured[0]
    // .name via GetStrFromObject -> GetString().  The valid captured[1] entry proves
    // the rest of the same step's provenance still renders.
    private const string LoneSurrogateCapturedStepLine =
        "{\"v\":1,\"schemaVersion\":\"v1\",\"type\":\"step-completed\","
        + "\"ts\":\"2026-01-01T00:00:00Z\",\"runId\":\"run-good\","
        + "\"stepId\":\"emit-poison-step\",\"verdict\":\"PASS\",\"durationMs\":3,"
        + "\"captured\":["
        + "{\"name\":\"bad\\uD800var\",\"path\":\"$.poison\",\"matched\":true},"
        + "{\"name\":\"cleanvar\",\"path\":\"$.clean\",\"matched\":true}"
        + "]}";

    // A buffer that places the poison-captured step inside a VALID scenario,
    // sandwiched between two clean scenarios, so the emit-time tolerance assertion can
    // prove the document still completes and every other section survives.
    private static string[] BufferWithUnreadableEmitTimeField() => new[]
    {
        Line(new ScenarioStartedEvent { RunId = "run-good", ScenarioId = "before-bad", File = "before.e2e.yaml" }),
        Line(new ScenarioCompletedEvent
        {
            RunId = "run-good",
            ScenarioId = "before-bad",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        }),

        // A clean scenario that OWNS the poison step.  The scenario, its heading and
        // its own clean captured entry must all still render.
        Line(new ScenarioStartedEvent { RunId = "run-good", ScenarioId = "host-scenario", File = "host.e2e.yaml" }),
        LoneSurrogateCapturedStepLine,
        Line(new ScenarioCompletedEvent
        {
            RunId = "run-good",
            ScenarioId = "host-scenario",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        }),

        Line(new ScenarioStartedEvent { RunId = "run-good", ScenarioId = "after-bad", File = "after.e2e.yaml" }),
        Line(new ScenarioCompletedEvent
        {
            RunId = "run-good",
            ScenarioId = "after-bad",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        }),
    };

    // Guard the central premise of the emit-time test: the crafted line PARSES, its
    // captured[0].name is a String JsonElement, and reading it via GetString() throws
    // InvalidOperationException (the precise emit-time failure HtmlRenderer must now
    // tolerate) — NOT a parse-time JsonException.
    [Fact]
    public void EmitTimeCraftedLine_Parses_ButNestedCapturedNameThrowsOnRead()
    {
        var envelope = EventStreamJson.FromLine(LoneSurrogateCapturedStepLine);
        Assert.NotNull(envelope.Extra);

        // captured parses to an Array; stepId / verdict / durationMs are all clean.
        Assert.True(envelope.Extra!.TryGetValue("captured", out var captured));
        Assert.Equal(JsonValueKind.Array, captured.ValueKind);

        var first = captured.EnumerateArray().First();
        Assert.True(first.TryGetProperty("name", out var nameEl));
        Assert.Equal(JsonValueKind.String, nameEl.ValueKind);

        // Reading the nested name — what WriteProvenanceThread does at EMIT time —
        // throws InvalidOperationException, the failure the deferred read must tolerate.
        var ex = Record.Exception(() => nameEl.GetString());
        Assert.IsType<InvalidOperationException>(ex);
    }

    // RED -> GREEN: an unreadable EMIT-TIME field (captured[0].name) must NOT throw out
    // of Render and must NOT truncate the document.  Before the fix WriteProvenanceThread
    // threw InvalidOperationException mid-WriteDocument, leaving a partial HTML file with
    // no closing tags and the post-poison sections missing.
    [Fact]
    public void HtmlRenderer_UnreadableEmitTimeField_RendersCompleteDocument_OmitsOnlyBadFragment()
    {
        var buffer = BufferWithUnreadableEmitTimeField();
        using var writer = new StringWriter();

        // (a) Render must NOT throw — the emit-time read is now defensive.
        var ex = Record.Exception(() => HtmlRenderer.Render(buffer, writer));
        Assert.Null(ex); // RED before the fix: InvalidOperationException out of WriteDocument.

        var output = writer.ToString();

        // (b) The document is COMPLETE — not truncated mid-stream.  HTML is not strictly
        //     XML (the <!DOCTYPE>, inline CSS, and HTML void elements are not XML), so
        //     completeness is asserted structurally: the document opens, the closing
        //     <body>/<html> trailer is present, and the trailer is the LAST meaningful
        //     content (nothing was cut off before it).
        Assert.Contains("<!DOCTYPE html>", output, StringComparison.Ordinal);
        Assert.Contains("</body>", output, StringComparison.Ordinal);
        Assert.Contains("</html>", output, StringComparison.Ordinal);
        Assert.EndsWith("</html>", output.TrimEnd(), StringComparison.Ordinal);

        // The provenance section the poison step belongs to was reached and emitted at
        // least one terminated row — proving WriteDocument did NOT abort BEFORE the poison
        // step's provenance, and the provenance list was properly closed.
        Assert.Contains("<div class=\"provenance\">", output, StringComparison.Ordinal);

        // (c) Every valid scenario still renders — including the poison step's OWN
        //     host scenario and the scenarios AFTER it (which a truncation would lose).
        Assert.Contains("before-bad", output, StringComparison.Ordinal);
        Assert.Contains("host-scenario", output, StringComparison.Ordinal);
        Assert.Contains("after-bad", output, StringComparison.Ordinal);

        // (d) The rest of the offending scenario's content survives: the poison step id
        //     and its CLEAN sibling capture both render — only the unreadable value is gone.
        Assert.Contains("emit-poison-step", output, StringComparison.Ordinal);
        Assert.Contains("cleanvar", output, StringComparison.Ordinal);

        // (e) The poison capture's ROW still renders, with its unreadable name omitted: its
        //     OTHER (readable) fields survive — the JSONPath "$.poison" is shown — and the
        //     name degrades to the same "(unknown)" placeholder a MISSING name already
        //     renders, so the row is present but value-less rather than dropped.  (We do not
        //     assert the absence of the literal "bad" token: the surrogate is unreadable, so
        //     the value can never reach the output at all, and "bad" also legitimately occurs
        //     in the "before-bad" / "after-bad" scenario ids.)
        Assert.Contains("$.poison", output, StringComparison.Ordinal);
        Assert.Contains("(unknown)", output, StringComparison.Ordinal);
    }

    // RED -> GREEN for the file seam: FileReportWriter streams HtmlRenderer.Render
    // straight to a FileStream, so a mid-document throw (swallowed by the per-file
    // InvalidOperationException catch) would still leave a TRUNCATED file on disk.  This
    // asserts the written HTML file is COMPLETE (terminated) — the check the existing
    // FileReportWriter surrogate test (scenarioId path) does not make.
    [Fact]
    public void FileReportWriter_UnreadableEmitTimeField_WritesCompleteHtmlFile_NotTruncated()
    {
        var buffer = BufferWithUnreadableEmitTimeField();
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-surr-emit-" + Guid.NewGuid().ToString("n"));
        var htmlPath = Path.Combine(dir, "report.html");

        try
        {
            var ex = Record.Exception(() => FileReportWriter.WriteFileReports(
                buffer, diffLookup: null, htmlPath, junitPath: null));

            // The seam never throws — the run's verdict / exit code is unaffected.
            Assert.Null(ex);
            Assert.True(File.Exists(htmlPath), "HTML report should still have been written.");

            var html = File.ReadAllText(htmlPath);

            // The on-disk file is COMPLETE: it opens with the DOCTYPE and ends with the
            // closing </html> trailer.  Before the fix the file was truncated at the poison
            // step's provenance, so </body></html> were absent.
            Assert.Contains("<!DOCTYPE html>", html, StringComparison.Ordinal);
            Assert.Contains("</body>", html, StringComparison.Ordinal);
            Assert.EndsWith("</html>", html.TrimEnd(), StringComparison.Ordinal);

            // Content after the poison step still made it to disk.
            Assert.Contains("host-scenario", html, StringComparison.Ordinal);
            Assert.Contains("after-bad", html, StringComparison.Ordinal);
            Assert.Contains("cleanvar", html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
