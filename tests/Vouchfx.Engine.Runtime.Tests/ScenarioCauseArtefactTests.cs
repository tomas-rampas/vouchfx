// Vouchfx.Engine.Runtime.Tests — M3: the scenario-level cause reaches the WRITTEN artefacts at
// EVERY seam that produces a scenario-completed record, not only at the one #372 landed on.
//
// WHY THESE LIVE IN Runtime.Tests AND NOT IN Vouchfx.Engine.Reporting.Tests. That project
// references Vouchfx.Engine.Reporting and nothing else, so ScenarioMessageArtefactTests can only
// render a HAND-BUILT ScenarioCompletedEvent — it asserts what a renderer does with a message,
// never what the ENGINE puts in one. The gap #372 left is entirely on the engine side: eleven of
// the twelve producers left `Message` null while printing the cause to the terminal on the very
// next line. Runtime.Tests can drive the real runners and read the real files back.
//
// NO DOCKER, and the constraint that forces the shape of these tests. Every path past
// SuiteTopology.StartAsync needs a container (PerStepLivenessTests states the same convention in
// its own header: "every RunSuiteAsync/RunAsync non-docker test short-circuits BEFORE that
// call"). The pre-topology and topology-refused seams are therefore reachable here; the two
// seams INSIDE the suite loop — the mixed-suite early-verdict branch and the isolation-failure
// branch — are not, and the one docker-gated test at the bottom is what covers the first of them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Reporting;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Every producer of a <see cref="ScenarioCompletedEvent"/> that HAS a cause must stamp it, and
/// the two run paths must stamp the SAME cause for the same document.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The defect, measured from both sides.</strong> A schema-invalid document under a bare
/// <c>run</c> wrote its cause into <c>--junit</c>/<c>--html</c>/<c>--events</c>, because that
/// path returns through <c>CompleteWithoutTopologyAsync</c> — the one seam #372 taught to stamp.
/// The IDENTICAL document under <c>--parallel</c> wrote none, because the single-scenario core
/// each parallel slot drives built its own <c>ScenarioCompletedEvent</c> and left
/// <c>Message</c> null. A sequential/parallel divergence in what a run REPORTS is the same defect
/// class this codebase has already been bitten by twice on exit codes.
/// </para>
/// <para>
/// <strong>Asserted non-empty FIRST, everywhere.</strong> Each artefact assertion below begins by
/// proving the channel carries a cause at all. Without that, a change that stopped stamping
/// entirely would leave every "the cause says X" assertion passing vacuously on an absent
/// element — the trap <c>SecurityDiagnosticPathDisclosureTests</c> calls out for its own
/// absence-shaped assertions.
/// </para>
/// </remarks>
public sealed class ScenarioCauseArtefactTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(HttpRestProvider).Assembly };

    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    /// <summary>
    /// Schema-invalid: <c>bogus</c> is an unknown key on a service, and
    /// <c>$defs/service</c> is <c>additionalProperties: false</c>. The AST still BUILDS — which
    /// is what lets the same document be handed to both runners, each of which validates the
    /// yamlText for itself.
    /// </summary>
    private const string SchemaInvalidSuite = """
        environment:
          services:
            api:
              image: myorg/api:1.0
              bogus: nope
        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            expect:
              status: 200
        """;

    /// <summary>
    /// Schema-valid and pipeline-valid; refused by the central secret-reference pass, which
    /// names the unknown source. Reaches the merged pre-topology authoring door.
    /// </summary>
    private const string UnknownSecretSourceSuite = """
        environment:
          services:
            api:
              image: myorg/api:1.0
        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            headers:
              Authorization: "${secret:nosuchsource/TOKEN}"
            expect:
              status: 200
        """;

    /// <summary>
    /// Schema-valid; refused by <c>EnvironmentMapper.Map</c>'s eager, pre-DCP validation — an
    /// <c>${conn:}</c> reference to a dependency the environment never declares. This is the
    /// <c>ArgumentException</c> door, and Map runs ahead of DCP so no container is started.
    /// </summary>
    private const string UnknownConnectionReferenceSuite = """
        environment:
          services:
            api:
              image: myorg/api:1.0
              env:
                DB: "${conn:nosuchdependency}"
        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            expect:
              status: 200
        """;

    private static readonly string[] s_oneScenario = { "only-scenario" };

    /// <summary>CA1861: the mixed suite's two names are a field, not an inline array.</summary>
    private static readonly string[] s_mixedScenarioNames = { "runnable", "refused" };

    // ── The sequential/parallel parity the defect broke ───────────────────────

    /// <summary>
    /// The same document, the same three artefacts, the same cause — under a bare <c>run</c> and
    /// under <c>--parallel</c>.
    /// </summary>
    /// <remarks>
    /// RED BEFORE THE CHANGE, on the parallel arm only: <c>message</c> was absent from the
    /// parallel events stream, the JUnit <c>skipped</c> element carried no author-facing text and
    /// the HTML report had no <c>scenario-message</c> paragraph, while the sequential arm carried
    /// all three. The parity assertion at the end is what makes this a divergence test rather
    /// than two independent presence tests: it fails if the two paths ever stamp different text
    /// for the same document, which is how they drifted in the first place.
    /// </remarks>
    [Fact]
    public async Task SchemaInvalidDocument_WritesTheSameCause_UnderRunAndUnderParallel()
    {
        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(SchemaInvalidSuite), registry);

        var directory = Directory.CreateTempSubdirectory("vouchfx-cause-parity-");
        try
        {
            var sequential = await RunSequentialAsync(directory, "seq", ast, SchemaInvalidSuite);
            var parallel = await RunParallelAsync(directory, "par", ast, SchemaInvalidSuite);

            Assert.Equal(Verdict.Inconclusive, sequential.Verdict);
            Assert.Equal(Verdict.Inconclusive, parallel.Verdict);

            AssertCarriesCause(sequential, "bare run");
            AssertCarriesCause(parallel, "--parallel");

            // The cause names the offending key — both paths report the SAME schema fault, not
            // merely SOME text each.
            Assert.Contains("bogus", sequential.EventMessage!, StringComparison.Ordinal);

            Assert.Equal(sequential.EventMessage, parallel.EventMessage);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ── Every Docker-free seam of the single-scenario core ────────────────────

    /// <summary>
    /// Each pre-topology and topology-refused door of the core that <c>--parallel</c> drives
    /// names its cause in all three written artefacts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FOUR ROWS, FOUR DIFFERENT DOORS of <c>RunScenarioOwningTopologyAsync</c> — schema, merged
    /// authoring, <c>ArgumentException</c> and <c>OrchestrationException</c>. All four were RED
    /// before the change (no <c>message</c> on any channel); all four print their cause to the
    /// terminal, which is what made the omission invisible to anyone reading a console log.
    /// </para>
    /// <para>
    /// Each row supplies its own <c>expectedFragment</c> so the assertion is that THIS door's
    /// diagnosis was stamped, not that some text was. A shared fragment would pass if every door
    /// stamped the same generic string.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("schema", SchemaInvalidSuite, "bogus", false)]
    [InlineData("secret", UnknownSecretSourceSuite, "nosuchsource", false)]
    [InlineData("envconfig", UnknownConnectionReferenceSuite, "nosuchdependency", false)]
    // The topology row's fragment is the RESOURCE the orchestrator could not provision, not a
    // prefix: the single-scenario core stamps the OrchestrationException's own message, which
    // reads "Orchestration Provision on resource 'api' …" — the suite path's
    // "RunSuiteAsync: topology failed to start" wrapper belongs to a different seam and asserting
    // it here would pin the wrong path's wording.
    [InlineData("topology", null, "resource 'api'", true)]
    public async Task ParallelCore_EveryReachableRefusal_NamesItsCauseInEveryWrittenArtefact(
        string label, string? yaml, string expectedFragment, bool pinsAPort)
    {
        TcpListener? squatter = null;
        var directory = Directory.CreateTempSubdirectory($"vouchfx-cause-{label}-");
        try
        {
            if (pinsAPort)
            {
                squatter = new TcpListener(IPAddress.Loopback, 0);
                squatter.Start();
                yaml = SecuredSuitePinning(((IPEndPoint)squatter.LocalEndpoint).Port);
                File.WriteAllText(Path.Combine(directory.FullName, "client.pem"), "placeholder");
                File.WriteAllText(Path.Combine(directory.FullName, "client.key"), "placeholder");
            }

            var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml!), registry);

            var written = await RunParallelAsync(directory, label, ast, yaml!, directory.FullName);

            AssertCarriesCause(written, $"the '{label}' door");
            Assert.Contains(expectedFragment, written.EventMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expectedFragment, written.JunitMessage!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(expectedFragment, written.HtmlMessage!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            squatter?.Stop();
            directory.Delete(recursive: true);
        }
    }

    // ── The scrub, on the channel that is written to disk ─────────────────────

    /// <summary>
    /// A resolved secret value in a stamped cause is redacted before it reaches the event
    /// stream, the JUnit <c>message</c> attribute or the HTML report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Driven at the chokepoint rather than end to end, and the reason is a measurement
    /// rather than convenience.</strong> The producer this BLOCKER was found on —
    /// <c>RunSuiteAsync</c>'s <c>ArgumentException</c> catch — cannot be reached with a populated
    /// ledger today: <c>EnvironmentMapper.Map</c>'s eager validation is <c>StartAsync</c>'s Step
    /// 1 and the probe that resolves <c>security.clientKeyPassword</c> runs after the health
    /// gate, so at that catch the ledger is always empty. The leak was therefore latent, not
    /// live — and latent is exactly the state in which a scrub gets dropped and nobody notices,
    /// which is why the fix moved the obligation into the one construction site and why this
    /// test pins the property there.
    /// </para>
    /// <para>
    /// The canary is asserted PRESENT in the unscrubbed input first. Without that the whole test
    /// would pass against a ledger that recorded nothing, a message that was never built, or a
    /// renderer that dropped the field.
    /// </para>
    /// </remarks>
    [Fact]
    public void StampedCause_IsScrubbed_OnEveryWrittenChannel()
    {
        const string Canary = "vouchfx-canary-passphrase-8f31c7";

        var ledger = new ResolvedSecretLedger();
        ledger.Record(Canary);

        var cause =
            $"RunSuiteAsync: environment configuration error — could not load the client key: "
            + $"the supplied password '{Canary}' was rejected.";

        // Not vacuous: the value really is in the text the producer hands over.
        Assert.Contains(Canary, cause, StringComparison.Ordinal);

        var stream = new[]
        {
            EventStreamJson.ToLine(new ScenarioStartedEvent
            {
                RunId = "run-1",
                Timestamp = DateTimeOffset.UnixEpoch,
                ScenarioId = "a",
            }),
            StepEventBuilder.ScenarioCompletedLine(
                "run-1",
                DateTimeOffset.UnixEpoch,
                "a",
                Verdict.Inconclusive,
                new VerdictCounts { Inconclusive = 1 },
                ledger,
                cause),
        };

        var eventMessage = ScenarioCompletedMessage(stream);
        Assert.False(
            string.IsNullOrEmpty(eventMessage),
            "The stamped cause must survive the scrub — an empty message would make every "
            + "absence assertion below vacuous.");

        var junitWriter = new StringWriter();
        JunitXmlRenderer.Render(stream, junitWriter);
        var rawJunit = junitWriter.ToString();
        var junitMessage = XDocument.Parse(rawJunit)
            .Descendants("skipped")
            .Single()
            .Attribute("message")!
            .Value;
        Assert.False(string.IsNullOrEmpty(junitMessage));

        var htmlWriter = new StringWriter();
        HtmlRenderer.Render(stream, htmlWriter);
        var html = htmlWriter.ToString();
        var htmlMessage = ScenarioMessageParagraph(html);
        Assert.False(string.IsNullOrEmpty(htmlMessage));

        // The value is gone from all three, and the redaction marker is what replaced it —
        // asserting only the absence would also pass if the whole message had been dropped.
        Assert.DoesNotContain(Canary, eventMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, junitMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, html, StringComparison.Ordinal);

        Assert.Contains(SecretString.RedactedMarker, eventMessage!, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, junitMessage, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, htmlMessage, StringComparison.Ordinal);

        // The rest of the diagnosis survived — the scrub is targeted, not a wholesale blanking
        // of the author's own message.
        Assert.Contains("environment configuration error", eventMessage!, StringComparison.Ordinal);
    }

    // ── The one seam that needs a container ───────────────────────────────────

    /// <summary>
    /// A compile-refused scenario BESIDE a runnable sibling names its cause in the written
    /// artefacts — the shared-topology suite loop's early-verdict branch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DOCKER-GATED BY NECESSITY, not by preference. This branch sits inside
    /// <c>await using (suite)</c>, so reaching it means the topology really started; a runnable
    /// sibling is the whole point of the shape (with every scenario refused, the all-early guard
    /// returns through <c>CompleteWithoutTopologyAsync</c> instead and a different seam is
    /// exercised). It was RED before the change: the broken file's cause was printed to the
    /// terminal and written to nothing, so the same document reported its fault when it sat alone
    /// in a directory and reported none when a working file sat beside it.
    /// </para>
    /// <para>
    /// The runnable sibling's own record is asserted to carry NO cause, which is the other half
    /// of the property: a suite-level stamp must not smear one scenario's fault across a
    /// scenario that passed.
    /// </para>
    /// <para>
    /// <strong>NOT YET OBSERVED GREEN, and that is stated rather than assumed.</strong> On the
    /// machine this was written on, every container-backed test in this project — including the
    /// pre-existing <c>ScenarioRunnerTests.Capstone_HttpRestGetWhoami_Pass</c>, run three times
    /// in both configurations — fails in Aspire's DCP with <c>Service whoami should have valid
    /// address at this point</c>, reproducibly and before any engine code runs. This test was
    /// executed and reached its first assertion (verdict EnvironmentError instead of
    /// Inconclusive), so its plumbing runs; whether its cause assertions hold is unverified until
    /// the Docker job runs it.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task MixedSuite_TheRefusedFile_NamesItsCause_BesideARunnableSibling()
    {
        const string RunnableSuite = """
            environment:
              services:
                whoami:
                  image: traefik/whoami
            steps:
              - id: get-root
                type: http.rest
                target: whoami
                method: GET
                path: /
                expect:
                  status: 200
            """;

        // The SAME environment block (the shared-topology requirement) with one schema fault in
        // the steps, so the divergence guard does not fire and the fault is per-scenario.
        const string RefusedSuite = """
            environment:
              services:
                whoami:
                  image: traefik/whoami
            steps:
              - id: get-root
                type: http.rest
                target: whoami
                method: GET
                path: /
                bogus: nope
                expect:
                  status: 200
            """;

        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var directory = Directory.CreateTempSubdirectory("vouchfx-cause-mixed-");
        try
        {
            var eventsPath = Path.Combine(directory.FullName, "events.jsonl");
            var sw = new StringWriter();

            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: new[]
                {
                    AstBuilder.Build(YamlDocumentParser.Parse(RunnableSuite), registry),
                    AstBuilder.Build(YamlDocumentParser.Parse(RefusedSuite), registry),
                },
                scenarioNames: s_mixedScenarioNames,
                yamlTexts: new[] { RunnableSuite, RefusedSuite },
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                eventsReportPath: eventsPath,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token);

            // The mixed shape really happened: one ran, one was refused.
            Assert.Equal(Verdict.Inconclusive, result.Verdict);
            Assert.Contains(result.ScenarioVerdicts, r => r.Verdict == Verdict.Pass);
            Assert.Contains(result.ScenarioVerdicts, r => r.Verdict == Verdict.Inconclusive);

            var lines = File.ReadAllLines(eventsPath);
            var refusedMessage = ScenarioCompletedMessage(lines, "refused");
            Assert.False(
                string.IsNullOrEmpty(refusedMessage),
                "The refused scenario's record must carry its cause even when a runnable sibling "
                + "kept the suite out of CompleteWithoutTopologyAsync.");
            Assert.Contains("bogus", refusedMessage!, StringComparison.Ordinal);

            Assert.True(
                string.IsNullOrEmpty(ScenarioCompletedMessage(lines, "runnable")),
                "The runnable scenario passed and has no scenario-level cause; a suite-level "
                + "stamp must not smear one file's fault onto it.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record WrittenArtefacts(
        Verdict Verdict, string? EventMessage, string? JunitMessage, string? HtmlMessage);

    private static void AssertCarriesCause(WrittenArtefacts written, string path)
    {
        Assert.False(
            string.IsNullOrEmpty(written.EventMessage),
            $"{path}: the events stream must carry the scenario-level cause (#372).");
        Assert.False(
            string.IsNullOrEmpty(written.JunitMessage),
            $"{path}: the JUnit message attribute must carry the scenario-level cause.");
        Assert.False(
            string.IsNullOrEmpty(written.HtmlMessage),
            $"{path}: the HTML report must carry the scenario-level cause.");
    }

    private static async Task<WrittenArtefacts> RunSequentialAsync(
        DirectoryInfo directory, string label, ScenarioAst ast, string yaml)
    {
        var paths = ReportPaths(directory, label);
        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: new[] { ast },
            scenarioNames: s_oneScenario,
            yamlTexts: new[] { yaml },
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: new StringWriter(),
            htmlReportPath: paths.Html,
            junitReportPath: paths.Junit,
            eventsReportPath: paths.Events);

        return Read(result.Verdict, paths);
    }

    private static async Task<WrittenArtefacts> RunParallelAsync(
        DirectoryInfo directory,
        string label,
        ScenarioAst ast,
        string yaml,
        string? seedBaseDirectory = null)
    {
        var paths = ReportPaths(directory, label);
        var result = await ParallelSuiteRunner.RunParallelAsync(
            scenarios: new[] { ast },
            scenarioNames: s_oneScenario,
            yamlTexts: new[] { yaml },
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: new StringWriter(),
            maxConcurrency: 1,
            seedBaseDirectory: seedBaseDirectory,
            htmlReportPath: paths.Html,
            junitReportPath: paths.Junit,
            eventsReportPath: paths.Events);

        return Read(result.Verdict, paths);
    }

    private static (string Html, string Junit, string Events) ReportPaths(
        DirectoryInfo directory, string label) =>
        (Path.Combine(directory.FullName, $"{label}.html"),
         Path.Combine(directory.FullName, $"{label}.xml"),
         Path.Combine(directory.FullName, $"{label}.jsonl"));

    private static WrittenArtefacts Read(Verdict verdict, (string Html, string Junit, string Events) paths)
    {
        var eventMessage = ScenarioCompletedMessage(File.ReadAllLines(paths.Events));

        // Parsed, not substring-matched: the renderer XML-escapes and XDocument unescapes.
        var outcome = XDocument.Parse(File.ReadAllText(paths.Junit))
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName is "skipped" or "error" or "failure");
        var junitMessage = outcome?.Attribute("message")?.Value;

        var htmlMessage = ScenarioMessageParagraph(File.ReadAllText(paths.Html));

        return new WrittenArtefacts(verdict, eventMessage, junitMessage, htmlMessage);
    }

    /// <summary>
    /// The <c>message</c> of a <c>scenario-completed</c> line, optionally narrowed to one
    /// <c>scenarioId</c> (the mixed-suite stream carries two).
    /// </summary>
    private static string? ScenarioCompletedMessage(
        IReadOnlyList<string> eventLines, string? scenarioId = null)
    {
        foreach (var line in eventLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type)
                || !string.Equals(type.GetString(), EventTypes.ScenarioCompleted, StringComparison.Ordinal))
            {
                continue;
            }

            if (scenarioId is not null
                && (!root.TryGetProperty("scenarioId", out var id)
                    || !string.Equals(id.GetString(), scenarioId, StringComparison.Ordinal)))
            {
                continue;
            }

            return root.TryGetProperty("message", out var message) ? message.GetString() : null;
        }

        return null;
    }

    /// <summary>
    /// The text inside the report's first <c>&lt;p class="scenario-message"&gt;</c>, HTML-DECODED.
    /// </summary>
    /// <remarks>
    /// Decoded for the same reason the JUnit half goes through <c>XDocument</c>: the renderer
    /// HTML-escapes, so an apostrophe in the engine's own diagnosis arrives as <c>&amp;#39;</c>
    /// and a raw substring match would report text the renderer really did carry as missing —
    /// pinning the escaping rule instead of the message. Measured, on the first run of this
    /// suite: <c>resource 'api'</c> was absent and <c>resource &amp;#39;api&amp;#39;</c> present.
    /// </remarks>
    private static string? ScenarioMessageParagraph(string html)
    {
        const string Open = "<p class=\"scenario-message\">";
        const string Close = "</p>";

        var start = html.IndexOf(Open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += Open.Length;
        var end = html.IndexOf(Close, start, StringComparison.Ordinal);
        return end < 0 ? null : WebUtility.HtmlDecode(html[start..end]);
    }

    /// <summary>
    /// A secured service whose host port is already held, so <c>StartAsync</c> cannot bring the
    /// topology up. Mirrors <c>RunSuiteAsyncTests.SecuredSuitePinning</c>, which is the shape
    /// this repo already relies on for a Docker-free topology failure.
    /// </summary>
    private static string SecuredSuitePinning(int hostPort) => $$"""
        environment:
          services:
            api:
              image: myorg/api:1.0
              ports: ["{{hostPort}}:8443"]
              security:
                profile: mtls
                endpoint: 8443
                clientCert: ./client.pem
                clientKey: ./client.key
        steps:
          - id: get-noop
            type: http.rest
            target: api
            method: GET
            path: /
            expect:
              status: 200
        """;
}
