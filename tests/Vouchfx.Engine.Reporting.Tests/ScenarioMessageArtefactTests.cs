// Vouchfx.Engine.Reporting.Tests — #372: a scenario-level cause reaches the WRITTEN artefacts.

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

/// <summary>
/// #372: before this, no written channel carried a scenario-level cause. A maintainer triaging
/// from a JUnit publisher UI — the artefact existing precisely so they need not read console
/// logs — saw <c>Scenario 'a' INCONCLUSIVE (pass=0 fail=0 envError=0 inconclusive=1)</c> and
/// could not tell a suite the engine REJECTED from one whose scenarios were legitimately skipped.
/// </summary>
public sealed class ScenarioMessageArtefactTests
{
    private const string Cause =
        "RunSuiteAsync: scenario 'b' declares a different environment; all scenarios in a suite "
        + "must share one topology.";

    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    private static string[] StreamWithMessage() => new[]
    {
        Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "a", File = "a.e2e.yaml" }),
        Line(new ScenarioCompletedEvent
        {
            RunId = "run-1",
            ScenarioId = "a",
            Verdict = Verdict.Inconclusive,
            Counts = new VerdictCounts { Inconclusive = 1 },
            Message = Cause,
        }),
    };

    private static string[] StreamWithoutMessage() => new[]
    {
        Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "a", File = "a.e2e.yaml" }),
        Line(new ScenarioCompletedEvent
        {
            RunId = "run-1",
            ScenarioId = "a",
            Verdict = Verdict.Inconclusive,
            Counts = new VerdictCounts { Inconclusive = 1 },
        }),
    };

    private static string RenderJunit(string[] stream)
    {
        var sw = new StringWriter();
        JunitXmlRenderer.Render(stream, sw);
        return sw.ToString();
    }

    private static string RenderHtml(string[] stream)
    {
        var sw = new StringWriter();
        HtmlRenderer.Render(stream, sw);
        return sw.ToString();
    }

    /// <summary>The JUnit half of the gap: the cause now reaches the publisher UI.</summary>
    [Fact]
    public void Junit_CarriesTheScenarioCause_AppendedToTheShapeSummary()
    {
        // PARSED, never substring-matched. The renderer XML-escapes, so the document holds
        // `Scenario &apos;a&apos;` — a substring assertion written in raw apostrophes could never
        // match however correct the renderer was, and would have read as a renderer defect.
        // Reading the attribute back tests the semantics and lets XDocument do the unescaping.
        var message = XDocument.Parse(RenderJunit(StreamWithMessage()))
            .Descendants("skipped")
            .Single()
            .Attribute("message")!
            .Value;

        Assert.Contains(Cause, message, StringComparison.Ordinal);

        // APPENDED, not substituted: the shape summary is what a publisher groups and diffs on.
        Assert.StartsWith(
            "Scenario 'a' INCONCLUSIVE (pass=0 fail=0 envError=0 inconclusive=1)",
            message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The document must stay well-formed with the cause spliced in — the renderer's own XML
    /// escaping is what makes author-controlled text inert, and this is the row that proves the
    /// appended text goes through it.
    /// </summary>
    [Fact]
    public void Junit_WithMarkupInTheCause_StaysWellFormedAndInert()
    {
        var hostile = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "a" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-1",
                ScenarioId = "a",
                Verdict = Verdict.Inconclusive,
                Counts = new VerdictCounts { Inconclusive = 1 },
                Message = "unknown key </error><injected/><error message=\"x\">& '\"",
            }),
        };

        var xml = RenderJunit(hostile);

        // Parses at all — the assertion that actually catches an escaping mistake.
        var document = XDocument.Parse(xml);
        Assert.Contains("injected", xml, StringComparison.Ordinal);
        Assert.Empty(document.Descendants("injected"));
    }

    /// <summary>The HTML half: a scenario refused before the topology has no steps to render.</summary>
    [Fact]
    public void Html_CarriesTheScenarioCause()
    {
        var html = RenderHtml(StreamWithMessage());

        Assert.Contains("<p class=\"scenario-message\">", html, StringComparison.Ordinal);

        // The renderer HTML-escapes, so the apostrophes in the cause arrive as `&#39;` — compare
        // against the escaped form rather than the raw one, for the same reason as the JUnit row.
        Assert.Contains(
            Cause.Replace("'", "&#39;", StringComparison.Ordinal),
            html,
            StringComparison.Ordinal);
    }

    /// <summary>HTML escaping, for the same reason as the JUnit row above.</summary>
    [Fact]
    public void Html_WithMarkupInTheCause_EscapesIt()
    {
        var hostile = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "a" }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-1",
                ScenarioId = "a",
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
                Message = "<script>alert(1)</script>",
            }),
        };

        var html = RenderHtml(hostile);

        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// §14's tolerant-reader property, in the direction that matters for an additive field: a
    /// stream carrying NO message — every ordinary pass, and every stream an older engine wrote —
    /// must render exactly as it did before the field existed.
    /// </summary>
    [Fact]
    public void BothRenderers_WithNoMessage_AreUnchanged()
    {
        var xml = RenderJunit(StreamWithoutMessage());
        var html = RenderHtml(StreamWithoutMessage());

        // Exactly the shape summary and nothing appended — the ": <cause>" suffix must be absent,
        // which is the property that makes the field additive for an older stream.
        var message = XDocument.Parse(xml)
            .Descendants("skipped")
            .Single()
            .Attribute("message")!
            .Value;

        Assert.Equal(
            "Scenario 'a' INCONCLUSIVE (pass=0 fail=0 envError=0 inconclusive=1)",
            message);
        // The ELEMENT, not the class name: the stylesheet always defines `.scenario-message`, so
        // a bare class-name assertion matches the <style> block on every document and can never
        // fail. Asserting the absence of the paragraph is the property meant.
        Assert.DoesNotContain(
            "<p class=\"scenario-message\">", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wire half of the same property: <c>null</c> is OMITTED, so a stream with nothing to
    /// report is byte-identical to one written before the field was added. This is what makes the
    /// golden regeneration additive in fact and not merely in intent.
    /// </summary>
    [Fact]
    public void NullMessage_IsOmittedFromTheWire()
    {
        var line = Line(new ScenarioCompletedEvent
        {
            RunId = "run-1",
            ScenarioId = "a",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        });

        Assert.DoesNotContain("message", line, StringComparison.Ordinal);
    }


}
