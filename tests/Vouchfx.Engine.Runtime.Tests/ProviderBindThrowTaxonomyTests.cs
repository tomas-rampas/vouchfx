// Vouchfx.Engine.Runtime.Tests — issue #413: an unexpected provider throw must produce a
// TAXONOMY verdict with report artefacts, never an unhandled exception. No Docker.
//
// WHAT WAS UNPINNED. `ProviderPipeline.BindAllSteps` called `ReflectBind` UNGUARDED, and the
// comment beside it documented the propagation as deliberate. Nothing above it caught: neither
// `ScenarioRunner.RunSuiteAsync` (whose pre-topology loop calls `ProviderPipeline.Compile`
// directly) nor `ParallelSuiteRunner` nor `RunCommand`. A provider whose `Bind` threw therefore
// escaped the whole run as a `TargetInvocationException`: no verdict, and no `--junit`/`--html`/
// `--events` artefacts.
//
// AND THE EXIT CODE WAS 1, WHICH IS WORSE THAN BEING OUTSIDE THE TAXONOMY. System.CommandLine's
// default exception handler is ON for the bare `InvocationConfiguration` `Program.cs` uses, so the
// framework caught the escape and returned TestFailure — the one code §12.1 reserves for a product
// defect the SUITE observed. Measured on the pinned framework by `Vouchfx.Cli.Tests`'
// `SystemCommandLineExitCodeTests`. `--parallel` answered 0 for the same fault (it classified the
// throw as an EnvironmentError, which exits 0 when nothing executed), so one provider defect
// produced 1 on one run path and 0 on the other.
//
// NO CONTAINERS ARE NEEDED, AND THAT IS A PROPERTY OF THE FIX RATHER THAN OF A MOCK. A throwing
// `Bind` is now a `PipelineResult.Failure`, which both run paths already treat as a pre-topology
// authoring/compile refusal: the scenario takes an early `Verdict.Inconclusive` and the suite
// returns through its without-topology completion path, before `SuiteTopology.StartAsync`.
//
// THE STUB IS `stub.throwing-bind`, declared in ProviderPipelineTests and discovered by the
// SAME assembly scan the registry does for real providers — deliberately reused rather than
// duplicated, so there is one throwing-Bind fixture in this assembly, not two that can drift.

using System.IO;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Non-docker tests for issue #413: a provider whose <c>Bind</c> throws is reported as a
/// taxonomy verdict with artefacts, on BOTH run paths.
/// </summary>
public sealed class ProviderBindThrowTaxonomyTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        new[] { typeof(ProviderBindThrowTaxonomyTests).Assembly };

    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    private static readonly string[] s_scenarioNames = { "throwing-bind-suite" };

    /// <summary>
    /// One step whose provider's <c>Bind</c> always throws. The document is schema-valid and the
    /// AST builds — the fault is reachable only at bind time, which is the whole point.
    /// </summary>
    private const string ThrowingBindSuite = """
        environment:
          services:
            api:
              image: myorg/api:1.0
        steps:
          - id: will-throw
            type: stub.throwing-bind
        """;

    /// <summary>
    /// The bare <c>run</c> path: <see cref="Verdict.Inconclusive"/>, nothing executed, and every
    /// requested artefact written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Inconclusive, not <see cref="Verdict.EnvironmentError"/>, and the difference is an
    /// exit code.</strong> A run that executed nothing and carries
    /// <see cref="Verdict.EnvironmentError"/> exits 0 (#390, deliberately open); the same run
    /// carrying <see cref="Verdict.Inconclusive"/> exits 4 through #369's rule. A provider defect
    /// must never produce a green CI build over a suite that never ran, so the assertion on
    /// <see cref="SuiteResult.ExecutedAnyScenario"/> below is load-bearing rather than
    /// descriptive — it is the half of the pair that decides the exit code.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_ProviderBindThrows_IsInconclusiveAndWritesEveryRequestedReport()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-bind-throw-run-");
        try
        {
            var junitPath = Path.Combine(directory.FullName, "results.xml");
            var htmlPath = Path.Combine(directory.FullName, "report.html");
            var eventsPath = Path.Combine(directory.FullName, "events.jsonl");

            var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(ThrowingBindSuite), registry);
            var sw = new StringWriter();

            var scenarios = new[] { ast };
            var yamls = new[] { ThrowingBindSuite };

            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: scenarios,
                scenarioNames: s_scenarioNames,
                yamlTexts: yamls,
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                seedBaseDirectory: directory.FullName,
                htmlReportPath: htmlPath,
                junitReportPath: junitPath,
                eventsReportPath: eventsPath);

            Assert.Equal(Verdict.Inconclusive, result.Verdict);
            Assert.False(
                result.ExecutedAnyScenario,
                "nothing ran, so #369's rule must take the exit code off Success.");

            Assert.True(File.Exists(junitPath), "the refusal must still write the requested JUnit report.");
            Assert.True(File.Exists(htmlPath), "…and the requested HTML report.");
            Assert.True(File.Exists(eventsPath), "…and the requested events stream.");

            // The diagnostic names the step and the provider — an author reading it must be able to
            // tell WHICH step's provider is defective without a stack trace.
            var rendered = sw.ToString();
            Assert.Contains("will-throw", rendered, StringComparison.Ordinal);
            Assert.Contains("stub.throwing-bind", rendered, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The <c>--parallel</c> path, which has carried the same exposure for as long as it has
    /// existed: the same verdict, the same nothing-executed derivation, the same artefacts.
    /// </summary>
    [Fact]
    public async Task RunParallelAsync_ProviderBindThrows_IsInconclusiveAndWritesEveryRequestedReport()
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-bind-throw-parallel-");
        try
        {
            var junitPath = Path.Combine(directory.FullName, "results.xml");
            var htmlPath = Path.Combine(directory.FullName, "report.html");
            var eventsPath = Path.Combine(directory.FullName, "events.jsonl");

            var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
            var ast = AstBuilder.Build(YamlDocumentParser.Parse(ThrowingBindSuite), registry);
            var sw = new StringWriter();

            var scenarios = new[] { ast };
            var yamls = new[] { ThrowingBindSuite };

            var result = await ParallelSuiteRunner.RunParallelAsync(
                scenarios: scenarios,
                scenarioNames: s_scenarioNames,
                yamlTexts: yamls,
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: sw,
                maxConcurrency: 1,
                seedBaseDirectory: directory.FullName,
                htmlReportPath: htmlPath,
                junitReportPath: junitPath,
                eventsReportPath: eventsPath);

            Assert.Equal(Verdict.Inconclusive, result.Verdict);
            Assert.False(
                result.ExecutedAnyScenario,
                "the parallel path derives the same nothing-executed answer from its event buffers.");

            Assert.True(File.Exists(junitPath), "the refusal must still write the requested JUnit report.");
            Assert.True(File.Exists(htmlPath), "…and the requested HTML report.");
            Assert.True(File.Exists(eventsPath), "…and the requested events stream.");

            var rendered = sw.ToString();
            Assert.Contains("will-throw", rendered, StringComparison.Ordinal);
            Assert.Contains("stub.throwing-bind", rendered, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
