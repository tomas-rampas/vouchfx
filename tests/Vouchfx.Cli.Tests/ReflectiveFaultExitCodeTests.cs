// Vouchfx.Cli.Tests — issue #466's actual defect: the EXIT CODE.
//
// WHAT WAS UNPINNED. `ParallelSuiteRunner`'s per-slot catch-all classifies any throw escaping
// `RunScenarioOwningTopologyAsync` as `Verdict.EnvironmentError` + `SecurityAbortKind.
// TopologyUnavailable`. `TopologyUnavailable` never raises `SecurityAssurance.Unconfirmed`
// (recorded on the member itself), and #369's nothing-executed rule is scoped to
// `Verdict.Inconclusive` so that #390 stays deliberately open — so the pair
// (EnvironmentError, executedAnyScenario: false) lands on `ExitCodes.Success`. A provider
// defect therefore produced a GREEN CI build. Everything downstream of the classification was
// already pinned; nothing pinned the classification's own exit code for a PROVIDER fault.
//
// THE TWO RUN PATHS FAILED DIFFERENTLY, AND ONLY THE PARALLEL ONE EXITED 0. `ScenarioRunner.
// RunSuiteAsync` has no backstop at all — measured, zero `try` and zero `catch` between its
// entry and `RunPreTopologyAuthoringDoor` — so on the bare `run` path the provider's exception
// simply propagated out of the runner. That is a different defect from a wrong exit code (no
// verdict, no artefacts, and #413's own header records that the CLI framework's default
// exception handler then answered 1), and both are closed by the same guard. The per-test
// remarks below say which arm did which; do not compress the two into "both exited 0".
//
// WHY THE INTEGER IS MEASURED HERE AND NOT INFERRED FROM THE TAXONOMY TABLE. §12.1 says
// "only Fail breaks CI by default", which read alone predicts 0 for BOTH the before and the
// after. The code that decides it is `RunCommand.ComputeExitCode`, whose two nothing-executed
// rules are conditioned on `code == ExitCodes.Success` and on the aggregate verdict — so the
// answer flips on the VERDICT, not on the taxonomy prose. This asserts the integer.
//
// WHY IT LIVES IN THIS PROJECT. `ComputeExitCode` and `ExitCodes` are internal to Vouchfx.Cli
// and reach exactly one test project through its InternalsVisibleTo; Vouchfx.Engine.Runtime.
// Tests has no reference to Vouchfx.Cli at all (see NothingExecutedExitCodeParityTests' own
// header, which records the same constraint for #369).
//
// WHY A LOCAL STUB PROVIDER RATHER THAN `RunCommand.ExecuteAsync`. The CLI's registry is a
// SEALED 25-assembly Core list, so no throwing stub can be reached through the full front
// door. The runner is therefore driven directly — the same shape
// NothingExecutedExitCodeParityTests uses for its derivation half — and its two outputs are
// handed to the real `ComputeExitCode` with the same arguments `RunCommand` passes.
//
// NO CONTAINERS: the fault is reached inside `ProviderPipeline.Compile`, before
// `SuiteTopology.StartAsync` on either path. This project deliberately is not an Aspire host.

using System.Reflection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Cli.Tests;

public sealed class ReflectiveFaultExitCodeTests
{
    /// <summary>
    /// A schema-valid, AST-building document whose single step's provider throws from
    /// <c>Emit</c> — the LAST of the five reflective SDK surfaces, so reaching it proves
    /// Bind, Validate, Resources and HostResources all completed first and the escape really
    /// did come from the surface named.
    /// </summary>
    private const string ThrowingEmitSuite = """
        environment:
          services:
            api:
              image: myorg/api:1.0
        steps:
          - id: will-throw
            type: stub.cli-throwing-emit
        """;

    private static readonly string[] s_oneScenario = { "only-scenario" };

    private static readonly Assembly[] s_providerAssemblies =
        new[] { typeof(ReflectiveFaultExitCodeTests).Assembly };

    /// <summary>
    /// The provider defect must not exit 0 on either run path, and the two paths must agree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED RED BEFORE THE FIX — <strong>and the two arms failed DIFFERENTLY, which is the
    /// point of running both.</strong> The parallel arm reached
    /// <see cref="ExitCodeFor"/> and returned <c>0</c>: the slot catch-all had classified the
    /// escape as <see cref="Verdict.EnvironmentError"/>, which with
    /// <c>executedAnyScenario: false</c> lands on <see cref="ExitCodes.Success"/>. The
    /// sequential arm never got that far — <c>ScenarioRunner.RunSuiteAsync</c> has NO backstop
    /// (measured: zero <c>try</c> and zero <c>catch</c> anywhere between its entry and
    /// <c>RunPreTopologyAuthoringDoor</c>), so the provider's exception propagated straight out
    /// of the <c>await</c> below and the test errored on that line, before any exit code was
    /// computed. An earlier revision of this remark asserted "both arms returned 0 … the bare
    /// run via the same classification after the escape unwound through its own backstop";
    /// that backstop does not exist and the sequential 0 was never observed. Corrected rather
    /// than softened: a wrong claim under a MEASURED label is worse than a vague one.
    /// </para>
    /// <para>
    /// MEASURED GREEN AFTER: both arms return <see cref="ExitCodes.Inconclusive"/> (4),
    /// because the guard turns the throw into a <c>PipelineResult.Failure</c> and the scenario
    /// takes the pre-topology <see cref="Verdict.Inconclusive"/> path #369's rule reddens.
    /// </para>
    /// <para>
    /// The absolute assertion sits beside the parity one deliberately: two arms that both
    /// exited 0 would be equal, and would be exactly the bug.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ProviderThrowsFromEmit_NeitherRunPathExitsZero()
    {
        var registry = StepKindRegistry.BuildAndFreeze(s_providerAssemblies);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(ThrowingEmitSuite), registry);
        var asts = new[] { ast };
        var yamlTexts = new[] { ThrowingEmitSuite };
        var appHost = Assembly.GetExecutingAssembly().GetName().Name;

        var sequential = await ScenarioRunner.RunSuiteAsync(
            scenarios: asts,
            scenarioNames: s_oneScenario,
            yamlTexts: yamlTexts,
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: appHost,
            output: new StringWriter());

        var parallel = await ParallelSuiteRunner.RunParallelAsync(
            scenarios: asts,
            scenarioNames: s_oneScenario,
            yamlTexts: yamlTexts,
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: appHost,
            output: new StringWriter(),
            maxConcurrency: 1);

        var sequentialCode = ExitCodeFor(sequential);
        var parallelCode = ExitCodeFor(parallel);

        Assert.NotEqual(ExitCodes.Success, sequentialCode);
        Assert.NotEqual(ExitCodes.Success, parallelCode);
        Assert.Equal(ExitCodes.Inconclusive, sequentialCode);
        Assert.Equal(ExitCodes.Inconclusive, parallelCode);
        Assert.Equal(sequentialCode, parallelCode);
    }

    /// <summary>
    /// NOT VACUOUS: the exit code above is 4 because the verdict is
    /// <see cref="Verdict.Inconclusive"/>, not because some unrelated door fired. This names
    /// the verdict directly, so a regression localises to the classification rather than
    /// being reported one layer downstream as a mysterious integer.
    /// </summary>
    [Fact]
    public async Task ProviderThrowsFromEmit_IsInconclusiveAndExecutedNothing()
    {
        var registry = StepKindRegistry.BuildAndFreeze(s_providerAssemblies);
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(ThrowingEmitSuite), registry);
        var appHost = Assembly.GetExecutingAssembly().GetName().Name;

        var parallel = await ParallelSuiteRunner.RunParallelAsync(
            scenarios: new[] { ast },
            scenarioNames: s_oneScenario,
            yamlTexts: new[] { ThrowingEmitSuite },
            providerAssemblies: s_providerAssemblies,
            appHostAssemblyName: appHost,
            output: new StringWriter(),
            maxConcurrency: 1);

        Assert.Equal(Verdict.Inconclusive, parallel.Verdict);
        Assert.NotEqual(Verdict.EnvironmentError, parallel.Verdict);
        Assert.False(parallel.ExecutedAnyScenario);
    }

    /// <summary>
    /// The same argument list <c>RunCommand.ExecuteAsync</c> passes for a fully-parsed
    /// single-scenario run with neither opt-in gate set.
    /// </summary>
    private static int ExitCodeFor(SuiteResult result) =>
        RunCommand.ComputeExitCode(
            parsedCount: 1,
            parseFailureCount: 0,
            suiteVerdict: result.Verdict,
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            securityAssurance: result.Assurance,
            executedAnyScenario: result.ExecutedAnyScenario);

    // ── Test-only provider ───────────────────────────────────────────────────

    /// <summary>
    /// Binds and validates cleanly, then throws from <c>Emit</c>. Nested rather than
    /// file-scoped so the type name in the engine's diagnostic stays readable; it is
    /// discovered by the same <c>[StepProvider]</c> assembly scan the registry does for real
    /// providers, and only when THIS assembly is passed as a provider assembly — the CLI's own
    /// sealed Core list never sees it.
    /// </summary>
    [StepProvider]
    private sealed class CliThrowingEmitProvider
        : IStepProvider,
          IStepBinder<CliFaultModel>,
          IStepValidator<CliFaultModel>,
          IStepCompiler<CliFaultModel>
    {
        public StepKindId Kind { get; } = new("stub", "cli-throwing-emit");

        public ProviderMetadata Metadata { get; } = new(
            Version: "0.0.0-test",
            MinEngineVersion: "1.0.0",
            License: "Apache-2.0",
            Authors: new[] { "test-only" });

        public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

        public CliFaultModel Bind(YamlNode node, IBindingContext ctx) => new("cli-throwing-emit");

        public ValidationResult Validate(CliFaultModel model, IProjectContext ctx) =>
            ValidationResult.Success;

        public CsxFragment Emit(CliFaultModel model, ICompileContext ctx) =>
            throw new InvalidOperationException("stub: Emit always throws.");
    }

    private sealed record CliFaultModel(string Tag) : IStepModel;
}
