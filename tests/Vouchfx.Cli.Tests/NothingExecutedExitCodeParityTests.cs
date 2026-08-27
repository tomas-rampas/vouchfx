// Vouchfx.Cli.Tests — #369's `--parallel` half: the DERIVATION, and the exit code it decides.
// No Docker.
//
// WHAT WAS UNPINNED. `RunCommand.ComputeExitCode` already had the rule itself pinned by
// ComputeExitCodeTests, which hands `executedAnyScenario` in as a literal. Nothing tested where
// that literal comes from: `ParallelSuiteRunner` DERIVES it from the concatenated event buffers
// (`allBuffers.Exists(ContainsStepEvent)`), `ScenarioRunner.CompleteWithoutTopologyAsync` sets
// the sequential half, and `RunCommand` reads it off `SuiteResult` and forwards it. Every link
// between the derivation and the process exit code was untested, so the measured regression #369
// names — `--parallel 1` on a refused suite exiting 0 while the identical bare run exited 4 —
// could reappear with the whole suite green.
//
// WHY BOTH TESTS AND NOT ONE. They are two different properties and they fail for two different
// reasons. The parity test is the USER-VISIBLE contract (two invocations of the same CLI over the
// same directory must not disagree about the exit code) and it is what catches a break anywhere
// along the chain; the derivation test names the ONE link that produced the regression, so a
// failure localises instead of merely reporting that the chain is broken somewhere. Folding both
// into one test would have made the failure ambiguous, which is the trap this repo's own
// divergence history is full of.
//
// WHY HERE AND NOT AS AN EXTENSION OF ScenarioCauseArtefactTests.
// `SchemaInvalidDocument_WritesTheSameCause_UnderRunAndUnderParallel` builds both arms over one
// directory and looks like a two-line extension away from this — but it lives in
// Vouchfx.Engine.Runtime.Tests, which has NO reference to Vouchfx.Cli and therefore no
// `ComputeExitCode` and no `ExitCodes`. Adding one would point an Aspire-host test project at the
// CLI to assert an integer. The CLI test project already exists for exactly this seam, already
// drives the real `RunCommand.ExecuteAsync`, and needs no containers to reach the refusal.
//
// NO CONTAINERS ARE NEEDED AND THAT IS A PROPERTY OF THE DOCUMENT, NOT A MOCK. The suite below
// parses and builds an AST (so discovery reports zero parse failures and hands it to the runner),
// and is then refused at the runner's schema door — before `SuiteTopology.StartAsync` on either
// path. That is the same door #369 was measured on.

using System.Reflection;
using System.Text.Json;
using Vouchfx.Cli.Selection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class NothingExecutedExitCodeParityTests : IDisposable
{
    /// <summary>
    /// Schema-invalid — <c>bogus</c> is an unknown key on a service and <c>$defs/service</c> is
    /// <c>additionalProperties: false</c> — but the YAML parses and the AST BUILDS, which is what
    /// keeps it out of the parse-failure list. Without that it would exit 4 through #425's rule
    /// on both paths and this file would pin nothing.
    /// </summary>
    private const string RefusedBeforeAnyTopologySuite = """
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
    /// The exact substring <c>ParallelSuiteRunner.ContainsStepEvent</c> searches for, composed
    /// from the same <see cref="EventTypes"/> constant the production side references — so a
    /// rename of the wire token moves this with it instead of leaving a stale needle behind.
    /// </summary>
    private static readonly string StepEventNeedle = "\"type\":\"" + EventTypes.StepStarted + "\"";

    /// <summary>
    /// The same refusal, with the needle above spelled INSIDE the offending property name — so
    /// the engine's own diagnostic ("Unknown property '…'") quotes it back verbatim into the
    /// <c>scenario-completed</c> record's <c>message</c>.
    /// </summary>
    private static readonly string TokenQuotingRefusedSuite = $$"""
        environment:
          services:
            api:
              image: myorg/api:1.0
              '{{StepEventNeedle}}': nope
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

    private readonly string _root;

    public NothingExecutedExitCodeParityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-nothing-executed-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a locked or read-only file must not fail the test.
        }
    }

    // ── The user-visible contract: the two run paths agree ────────────────────

    /// <summary>
    /// The same directory, the same refusal, the same exit code — under a bare <c>run</c> and
    /// under <c>--parallel 1</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE PARITY ASSERTION IS THE POINT, and the absolute one beside it is what stops the parity
    /// from being satisfiable by both paths going wrong together: two arms that both exited 0
    /// would be equal and would be exactly the bug. So both are asserted — first that each arm is
    /// <see cref="ExitCodes.Inconclusive"/>, then that they are the same integer.
    /// </para>
    /// <para>
    /// MEASURED RED, TWICE, ONE HALF AT A TIME. With <c>ParallelSuiteRunner</c>'s derivation
    /// inverted (so it yields <c>true</c> for this document) the parallel arm returned 0 against
    /// the bare run's 4 — the exact divergence #369 names. With
    /// <c>CompleteWithoutTopologyAsync</c>'s <c>ExecutedAnyScenario = false</c> flipped instead,
    /// the sequential arm returned 0 against the parallel arm's 4. Both halves are load-bearing
    /// here; neither was before.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_SuiteRefusedBeforeAnyTopology_BareRunAndParallel_ExitTheSame()
    {
        File.WriteAllText(Path.Combine(_root, "refused.e2e.yaml"), RefusedBeforeAnyTopologySuite);

        var sequential = await ExecuteAsync(parallel: null);
        var parallel = await ExecuteAsync(parallel: 1);

        Assert.Equal(ExitCodes.Inconclusive, sequential);
        Assert.Equal(ExitCodes.Inconclusive, parallel);
        Assert.Equal(sequential, parallel);
    }

    /// <summary>
    /// The refused suite's own diagnostic quoting the <c>step-started</c> wire token verbatim
    /// does not turn the run into one that executed something.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ContainsStepEvent</c> is a SUBSTRING test over the serialised event line, and the
    /// serialised line carries an author-influenced <c>message</c>. The property that keeps the
    /// substring test honest is not the message's content but the serialiser's escaping: a
    /// <c>"</c> inside a JSON string is never emitted as a bare <c>"</c>, so the token cannot
    /// re-form inside a value. That is a property of <c>EventStreamJson</c> rather than of this
    /// derivation, and nothing pinned the two together — this does.
    /// </para>
    /// <para>
    /// NOT VACUOUS: the first assertion proves the engine really did quote the token back, so a
    /// change that stopped echoing the property name would fail here rather than silently make
    /// the exit-code assertion prove nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_RefusalDiagnosticQuotingTheStepEventToken_StillExitsInconclusive()
    {
        File.WriteAllText(Path.Combine(_root, "refused.e2e.yaml"), TokenQuotingRefusedSuite);

        var eventsPath = Path.Combine(_root, "events.jsonl");
        var exitCode = await ExecuteAsync(parallel: 1, eventsReportPath: eventsPath);

        var lines = File.ReadAllLines(eventsPath);

        // NOT VACUOUS, part 1: the engine really did echo the exact needle into an author-facing
        // field. Read through the JSON parser, so this is the DECODED message rather than
        // whatever escape spelling the serialiser chose.
        var message = ScenarioCompletedMessage(lines);
        Assert.NotNull(message);
        Assert.Contains(StepEventNeedle, message, StringComparison.Ordinal);

        // NOT VACUOUS, part 2: and it is still not present unescaped on any serialised line, which
        // is the only reason the substring test upstream cannot be fooled by it. Any escape
        // spelling satisfies this; a serialiser that emitted the value raw would not.
        Assert.DoesNotContain(lines, line => line.Contains(StepEventNeedle, StringComparison.Ordinal));

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── The link that produced the regression ─────────────────────────────────

    /// <summary>
    /// <c>ParallelSuiteRunner</c> DERIVES <c>ExecutedAnyScenario == false</c> for a suite refused
    /// before any topology, and <c>ScenarioRunner</c> reports the same for the same document.
    /// </summary>
    /// <remarks>
    /// The sequential arm is asserted too, and not as decoration: the flag is what the two paths
    /// have to agree on for the exit codes above to agree, and asserting only the parallel arm
    /// would leave a sequential regression to be reported one layer downstream as a mysterious
    /// exit-code divergence.
    /// </remarks>
    [Fact]
    public async Task RefusedBeforeAnyTopology_BothRunners_DeriveNothingExecuted()
    {
        var providers = ProviderRegistryFactory.CoreProviderAssemblies();
        var registry = StepKindRegistry.BuildAndFreeze(providers);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(RefusedBeforeAnyTopologySuite), registry);
        var yamlTexts = new[] { RefusedBeforeAnyTopologySuite };
        var asts = new[] { ast };
        var appHost = Assembly.GetExecutingAssembly().GetName().Name;

        var sequential = await ScenarioRunner.RunSuiteAsync(
            scenarios: asts,
            scenarioNames: s_oneScenario,
            yamlTexts: yamlTexts,
            providerAssemblies: providers,
            appHostAssemblyName: appHost,
            output: new StringWriter());

        var parallel = await ParallelSuiteRunner.RunParallelAsync(
            scenarios: asts,
            scenarioNames: s_oneScenario,
            yamlTexts: yamlTexts,
            providerAssemblies: providers,
            appHostAssemblyName: appHost,
            output: new StringWriter(),
            maxConcurrency: 1);

        // The refusal really happened on both paths — without this the flag assertions below
        // would also pass on a suite that simply had nothing to run.
        Assert.Equal(Verdict.Inconclusive, sequential.Verdict);
        Assert.Equal(Verdict.Inconclusive, parallel.Verdict);

        Assert.False(
            parallel.ExecutedAnyScenario,
            "--parallel must DERIVE that nothing executed from its event buffers (#369).");
        Assert.False(
            sequential.ExecutedAnyScenario,
            "the bare run's without-topology completion path must report the same.");
    }

    /// <summary>
    /// The <c>message</c> of the first <c>scenario-completed</c> line, JSON-DECODED.
    /// </summary>
    private static string? ScenarioCompletedMessage(IReadOnlyList<string> eventLines)
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

            return root.TryGetProperty("message", out var message) ? message.GetString() : null;
        }

        return null;
    }

    private Task<int> ExecuteAsync(int? parallel, string? eventsReportPath = null)
        => RunCommand.ExecuteAsync(
            path: _root,
            criteria: SelectionCriteria.None,
            parallel: parallel,
            watch: false,
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            htmlReportPath: null,
            junitReportPath: null,
            eventsReportPath: eventsReportPath,
            eventsStreamPath: null,
            decorate: false,
            output: new StringWriter(),
            telemetryHook: null,
            cancellationToken: default);
}
