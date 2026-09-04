// Vouchfx.Engine.Runtime.Tests — BLOCKER B2: no absolute host path may reach the WRITTEN
// artefacts through a validation-time security diagnostic.
//
// WHY THIS LIVES IN Runtime.Tests AND NOT IN Vouchfx.Engine.Reporting.Tests
// (ScenarioMessageArtefactTests, the other candidate home). That project references
// Vouchfx.Engine.Reporting and nothing else, so a test there can only render a HAND-BUILT
// ScenarioCompletedEvent — it can assert what a renderer does with a message, never what the
// engine PUTS in one. The defect is the whole chain, and every link of it is upstream of
// Reporting: EnvironmentSecurityValidator's ValidationFailure → ProviderPipeline's
// Failure.Message → ScenarioRunner's EarlyMessage → ScenarioCompletedEvent.Message → the three
// renderers. Runtime.Tests references Vouchfx.Engine.Runtime (and Reporting transitively), so it
// can drive the real ScenarioRunner.RunSuiteAsync over a real suite directory with a real missing
// artefact and read the three real files back. No Docker: every scenario carries an early verdict,
// so RunSuiteAsync's "stop here when NO scenario can run" guard returns through
// CompleteWithoutTopologyAsync before any container is started.
using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// A validation-time security diagnostic must name the author's DECLARED path and the CONCEPT it
/// resolves against, never the resolved absolute host path and never the resolved suite directory.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The channel, and why the old rule stopped holding.</strong> Until #372/#407 an
/// <c>EarlyMessage</c> reached the terminal and nothing else, and REQ-004's acceptance criterion
/// leaned on exactly that when it required the resolved path in the message. Those two commits
/// carried the cause onto <see cref="ScenarioCompletedEvent.Message"/>, so the same text now lands
/// in the §14 event stream, the JUnit <c>message</c> attribute and the HTML report — archived and
/// uploaded, and unreachable by any scrubber (<c>ResolvedSecrets.Scrub</c> covers revealed secret
/// VALUES; a filesystem path is never one). The maintainer superseded that criterion; this test is
/// what keeps the supersession from decaying.
/// </para>
/// <para>
/// <strong>Asserted as a PROPERTY, not against one expected string.</strong>
/// <see cref="AssertNoAbsoluteHostPath"/> rejects ANY rooted token in the rendered text, so it
/// fails for a sibling diagnostic leaking some other host path — the containment message, a
/// <c>serverArtifacts[].source</c> throw, a future caller — not only for the not-found message this
/// suite happens to trigger. A hard-coded expected string would have passed all three of those.
/// </para>
/// </remarks>
public sealed class SecurityDiagnosticPathDisclosureTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(HttpRestProvider).Assembly };

    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    /// <summary>The author's own text — this half of the message is NOT a disclosure.</summary>
    private const string DeclaredClientCert = "./certs/client.pem";

    /// <summary>
    /// One service declaring <c>mtls</c> against two artefacts that do not exist in the suite
    /// directory, so REQ-004's existence check is the first door the document fails.
    /// </summary>
    private const string MissingArtefactSuite = """
        environment:
          services:
            api:
              image: myorg/api:1.0
              security:
                profile: mtls
                endpoint: 8443
                clientCert: ./certs/client.pem
                clientKey: ./certs/client.key
        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            expect:
              status: 200
        """;

    private static readonly string[] s_scenarioNames = { "missing-artefact" };

    /// <summary>CA1861: the token separators and path separators are fields, not inline arrays.</summary>
    /// <remarks>
    /// <c>&amp;</c> and <c>;</c> are separators so an HTML-escaped quote (<c>&amp;#39;</c>) splits
    /// off the path it wraps instead of gluing itself to the front of it.
    /// </remarks>
    private static readonly char[] s_tokenSeparators =
        { ' ', '\t', '\r', '\n', '"', '\'', '<', '>', '&', ';', ',', '(', ')', '[', ']' };

    private static readonly char[] s_pathSeparators = { '\\', '/' };

    /// <summary>
    /// Trimmed from the END only. Trimming <c>.</c> from the FRONT would turn the author's own
    /// <c>./certs/client.pem</c> into the rooted-looking <c>/certs/client.pem</c> and fail a
    /// correct message — measured, on the first run of this test.
    /// </summary>
    private static readonly char[] s_trailingPunctuation = { '.', ':' };

    [Fact]
    public async Task MissingSecurityArtefact_NoWrittenArtefactNamesAnAbsoluteHostPath()
    {
        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(MissingArtefactSuite), registry);

        var suite = Directory.CreateTempSubdirectory("vouchfx-b2-path-disclosure-");
        try
        {
            var suiteDirectory = suite.FullName;
            var junitPath = Path.Combine(suiteDirectory, "results.xml");
            var htmlPath = Path.Combine(suiteDirectory, "report.html");
            var eventsPath = Path.Combine(suiteDirectory, "events.jsonl");

            var sw = new StringWriter();
            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: new[] { ast },
                scenarioNames: s_scenarioNames,
                yamlTexts: new[] { MissingArtefactSuite },
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                scenarioBaseDirectories: new string?[] { suiteDirectory },
                htmlReportPath: htmlPath,
                junitReportPath: junitPath,
                eventsReportPath: eventsPath);

            // The preflight refused the suite before any container was started (REQ-004).
            Assert.Equal(Verdict.Inconclusive, result.Verdict);

            // ── The event stream ──────────────────────────────────────────────
            var eventMessage = ScenarioCompletedMessage(File.ReadAllLines(eventsPath));

            // The channel really does carry the cause — without this, every absence assertion
            // below would pass vacuously on an empty message.
            Assert.False(
                string.IsNullOrEmpty(eventMessage),
                "ScenarioCompletedEvent.message must carry the preflight cause (#372); an empty "
                + "message would make the disclosure assertions below vacuous.");
            Assert.Contains(DeclaredClientCert, eventMessage, StringComparison.Ordinal);
            AssertNoAbsoluteHostPath("the event stream", eventMessage!, suiteDirectory);

            // ── The JUnit message attribute ───────────────────────────────────
            // Parsed, not substring-matched: the renderer XML-escapes, and XDocument unescapes.
            var junitMessage = XDocument.Parse(File.ReadAllText(junitPath))
                .Descendants("skipped")
                .Single()
                .Attribute("message")!
                .Value;

            Assert.Contains(DeclaredClientCert, junitMessage, StringComparison.Ordinal);
            AssertNoAbsoluteHostPath("the JUnit message attribute", junitMessage, suiteDirectory);

            // ── The HTML scenario-message paragraph ───────────────────────────
            var htmlMessage = ScenarioMessageParagraph(File.ReadAllText(htmlPath));

            Assert.Contains(DeclaredClientCert, htmlMessage, StringComparison.Ordinal);
            AssertNoAbsoluteHostPath("the HTML report", htmlMessage, suiteDirectory);
        }
        finally
        {
            suite.Delete(recursive: true);
        }
    }

    // ── SEC-MAJOR-1 (issue #466): the same rule, for a PROVIDER-fault diagnostic ──────────

    /// <summary>
    /// A guarded provider fault reaches the same three written artefacts as a security
    /// preflight failure, so it is bound by the same rule: name the member and the exception,
    /// never the resolved host path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>WHAT WAS UNPINNED.</strong> Issue #466's guards splice <c>cause.Message</c> into
    /// <c>ValidationFailure.Message</c>, and a BCL filesystem exception's message carries an
    /// absolute host path verbatim — measured: <c>Could not find file 'D:\…\never-written.csx'.</c>
    /// The taxonomy tests for those guards assert what the message CONTAINS; nothing asserted
    /// what it must NOT, which is the gap this closes.
    /// </para>
    /// <para>
    /// <strong>THE SCRUB CHOKEPOINT CANNOT COVER IT, which is why the fix is at the source.</strong>
    /// This door runs PRE-TOPOLOGY and <c>SecurityPathDisclosureLedger</c> is populated only at
    /// topology-build time, so both nets are empty by construction here — and that ledger only
    /// ever holds security-material paths anyway. <c>ScriptCsharpProvider</c> states the same
    /// conclusion for the same class of leak: a net cannot replace what was never recorded into
    /// it.
    /// </para>
    /// <para>
    /// <strong>THE PROVIDER IS NOT A MOCK OF THE ROUTE, IT IS THE ROUTE.</strong>
    /// <c>stub.suite-path-leaking-emit</c> does exactly what <c>ScriptCsharpProvider.Emit</c>
    /// does — <c>File.ReadAllText(Path.GetFullPath(Path.Combine(ctx.SuiteDirectory, …)))</c> —
    /// against a file that is not there, which is the in-tree TOCTOU race that provider's own
    /// comment accepts. The path in the diagnostic is therefore whatever the BCL really wrote,
    /// not a hand-forged string.
    /// </para>
    /// <para>
    /// MEASURED RED before the substitution: the event stream, the JUnit <c>message</c>
    /// attribute and the HTML report each carried the full suite directory.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ProviderFaultDiagnostic_NoWrittenArtefactNamesTheResolvedSuiteDirectory()
    {
        var providers = new[] { typeof(SecurityDiagnosticPathDisclosureTests).Assembly };
        var registry = StepKindRegistry.BuildAndFreeze(providers);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(PathLeakingProviderSuite), registry);

        var suite = Directory.CreateTempSubdirectory("vouchfx-466-path-disclosure-");
        try
        {
            var suiteDirectory = suite.FullName;
            var junitPath = Path.Combine(suiteDirectory, "results.xml");
            var htmlPath = Path.Combine(suiteDirectory, "report.html");
            var eventsPath = Path.Combine(suiteDirectory, "events.jsonl");

            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: new[] { ast },
                scenarioNames: s_pathLeakingScenarioNames,
                yamlTexts: new[] { PathLeakingProviderSuite },
                providerAssemblies: providers,
                appHostAssemblyName: AppHostAssemblyName,
                output: new StringWriter(),
                scenarioBaseDirectories: new string?[] { suiteDirectory },
                htmlReportPath: htmlPath,
                junitReportPath: junitPath,
                eventsReportPath: eventsPath);

            Assert.Equal(Verdict.Inconclusive, result.Verdict);

            // ── The event stream, DECODED ─────────────────────────────────────
            var eventLines = File.ReadAllLines(eventsPath);
            var eventMessage = ScenarioCompletedMessage(eventLines);

            // NOT VACUOUS: the diagnostic really did reach the channel, really did come from the
            // guard, and really did quote the BCL's own not-found text — so the absence
            // assertions below are testing a message that had every opportunity to leak.
            Assert.False(
                string.IsNullOrEmpty(eventMessage),
                "the provider-fault guard must carry its cause onto ScenarioCompletedEvent.message.");
            Assert.Contains("stub.suite-path-leaking-emit", eventMessage!, StringComparison.Ordinal);
            Assert.Contains("Emit", eventMessage!, StringComparison.Ordinal);
            Assert.Contains("never-written.csx", eventMessage!, StringComparison.Ordinal);

            // ── THE DISCLOSURE ASSERTIONS COME FIRST, deliberately. Ordered ahead of the
            // positive wording check below so that when the substitution is broken the test
            // fails ON THE LEAK rather than on the replacement phrase being absent - the
            // failure message then names the defect instead of a symptom of it.
            AssertNoAbsoluteHostPath("the event stream", eventMessage!, suiteDirectory);

            // ── The event stream, RAW (the JSON-escaped spelling) ─────────────
            // The decoded assertion above cannot see a path that survived only in its escaped
            // form, and the escaped form is what a consumer of the on-disk artifact recovers by
            // JSON-decoding. On Windows the suite directory is full of '\', which serialises as
            // '\\' - the exact bypass SecurityPathDisclosureLedger records having shipped once.
            var rawEvents = string.Join("\n", eventLines);
            Assert.DoesNotContain(suiteDirectory, rawEvents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                JavaScriptEncoder.Default.Encode(suiteDirectory),
                rawEvents,
                StringComparison.OrdinalIgnoreCase);

            // The substitution's own wording, so a future change that merely DELETES the path
            // (rather than substituting the concept) fails here.
            Assert.Contains("the suite directory", eventMessage!, StringComparison.Ordinal);

            // ── The JUnit message attribute ───────────────────────────────────
            var junitMessage = XDocument.Parse(File.ReadAllText(junitPath))
                .Descendants("skipped")
                .Single()
                .Attribute("message")!
                .Value;

            AssertNoAbsoluteHostPath("the JUnit message attribute", junitMessage, suiteDirectory);

            // ── The HTML scenario-message paragraph ───────────────────────────
            AssertNoAbsoluteHostPath(
                "the HTML report", ScenarioMessageParagraph(File.ReadAllText(htmlPath)), suiteDirectory);
        }
        finally
        {
            suite.Delete(recursive: true);
        }
    }

    /// <summary>
    /// One step whose provider's <c>Emit</c> reads a file that does not exist under the suite
    /// directory — see <c>stub.suite-path-leaking-emit</c> in
    /// <c>ProviderReflectiveFaultTaxonomyTests</c>, discovered by the same assembly scan.
    /// </summary>
    private const string PathLeakingProviderSuite = """
        steps:
          - id: leaks-a-path
            type: stub.suite-path-leaking-emit
        """;

    private static readonly string[] s_pathLeakingScenarioNames = { "path-leaking-provider" };

    /// <summary>
    /// The <c>message</c> of the one <c>scenario-completed</c> line in a written event stream.
    /// </summary>
    private static string? ScenarioCompletedMessage(string[] eventLines)
    {
        foreach (var line in eventLines)
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), EventTypes.ScenarioCompleted, StringComparison.Ordinal))
            {
                continue;
            }

            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }

        return null;
    }

    /// <summary>The text inside the report's single <c>&lt;p class="scenario-message"&gt;</c>.</summary>
    private static string ScenarioMessageParagraph(string html)
    {
        const string Open = "<p class=\"scenario-message\">";
        var start = html.IndexOf(Open, StringComparison.Ordinal);
        Assert.True(start >= 0, "The HTML report must carry the scenario-level cause (#372).");

        start += Open.Length;
        var end = html.IndexOf("</p>", start, StringComparison.Ordinal);
        Assert.True(end > start, "The scenario-message paragraph must be closed.");

        return html[start..end];
    }

    /// <summary>
    /// Fails when <paramref name="text"/> names an absolute host path, by the PROPERTY rather than
    /// by any one expected string.
    /// </summary>
    /// <remarks>
    /// Two checks, and the second is the one that generalises. (a) The suite directory itself must
    /// not appear — that is the specific leak this suite triggers, and a substring test catches it
    /// even where no token boundary exists. (b) No whitespace- or quote-delimited token may be a
    /// rooted path CONTAINING a separator: that is what a leaked host path looks like on either
    /// platform (<c>C:\…</c> / <c>\\host\…</c> on Windows, <c>/…/…</c> elsewhere), and it holds for
    /// a path this suite never names. The separator clause is what keeps ordinary message text out
    /// of the net — a bare <c>drive:</c>-shaped token with no separator is not a path reference.
    /// </remarks>
    private static void AssertNoAbsoluteHostPath(string channel, string text, string suiteDirectory)
    {
        Assert.DoesNotContain(suiteDirectory, text, StringComparison.OrdinalIgnoreCase);

        foreach (var token in text.Split(s_tokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.TrimEnd(s_trailingPunctuation);
            if (candidate.Length < 2 || candidate.IndexOfAny(s_pathSeparators) < 0)
            {
                continue;
            }

            Assert.False(
                Path.IsPathRooted(candidate),
                $"{channel} names an absolute host path '{candidate}'. A validation-time security "
                + "diagnostic must name the declared path and the concept it resolves against "
                + $"(#357's rule), never a resolved one. Full text: {text}");
        }
    }
}
