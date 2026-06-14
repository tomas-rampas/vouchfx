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
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Reporting;
using Xunit;

namespace Platform.Engine.Reporting.Tests;

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
}
