// Vouchfx.Engine.Runtime.Tests — issue #480: a provider defect BESIDE A PASSING SIBLING.
// No container runtime is used by any row here; three of the four nevertheless carry
// `[Trait("requires", "docker")]` because they start a real (zero-resource) DCP application — see
// the trait paragraph below, which is the whole of that argument.
//
// WHAT IS UNPINNED, AND WHY TWO ISSUES' REVIEWS WALKED PAST IT. Issue #413 (a throwing `Bind`) and
// issue #466 (a throw from any of the five other reflective SDK surfaces) both closed with the
// same guard: `ProviderPipeline` turns the escape into a `PipelineResult.Failure`, the scenario
// takes an early `Verdict.Inconclusive` at the pre-topology authoring door, and the suite exits 4.
// Every test row either issue left behind runs the defective document ALONE. Not one of them puts
// it beside a scenario that genuinely executes — and that single missing row is the whole of #480,
// because the exit code is decided by a flag whose value depends on the sibling and not on the
// defect.
//
// THE MECHANISM, RE-DERIVED AGAINST `main` (fb16c83) RATHER THAN QUOTED FROM THE ISSUE.
// `SuiteResult.ExecutedAnyScenario` (ScenarioRunner.cs:108) DEFAULTS TO `true` and is set to
// `false` in exactly one place — `CompleteWithoutTopologyAsync`. A mixed suite deliberately never
// reaches that method: the all-early guard above it requires EVERY scenario to carry an early
// verdict, and this suite's sibling carries none. So the shared-topology path builds its topology,
// runs the sibling, and returns through the normal completion tail carrying the DEFAULT `true`.
// The parallel path arrives at the same answer by a different route — it DERIVES the flag from
// `allBuffers.Exists(ContainsStepEvent)` (ParallelSuiteRunner.cs:857), and the sibling's own
// `step-started` line makes that predicate true. Downstream, `RunCommand.ComputeExitCode`'s #369
// rule is conditioned on `!executedAnyScenario`, so it does not fire, and
// `ExitCodes.FromVerdict(Inconclusive, failOnInconclusive: false, …)` returns `Success`. One
// unrelated passing scenario turns a provider defect into a green CI build.
//
// WHY THIS FILE ASSERTS THE CURRENT PAIR RATHER THAN THE DESIRED EXIT CODE. The defect is an
// integer, and the integer is not computable here: `ComputeExitCode` and `ExitCodes` are internal
// to `Vouchfx.Cli`, which this project does not reference. What this project CAN establish — and
// what the CLI-side file cannot, because `Vouchfx.Cli.Tests` is deliberately not an Aspire host and
// therefore cannot execute a scenario at all — is the PAIR the exit-code seam is handed:
// `(Verdict.Inconclusive, ExecutedAnyScenario == true)`. The two files are one test split across
// the one boundary neither project can cross; the split, and the reason it is not closed with an
// `InternalsVisibleTo` grant, is recorded again in the CLI file's own header. Pinning the pair here
// is what stops the CLI-side row from being an assertion about three literals somebody typed.
//
// THESE ROWS WERE GREEN BEFORE THE FIX AND ARE STILL GREEN AFTER IT, WHICH IS THE ANSWER TO THE
// QUESTION THEY WERE WRITTEN TO LEAVE OPEN. They began as characterisation: they recorded what the
// tree did, so that the CLI-side RED row was fed measured inputs rather than assumed ones, and
// T1's own header noted that whether the fix ALSO changed this pair was the fix's design decision.
// It did not. `Verdict.Inconclusive` and `ExecutedAnyScenario == true` are both still correct
// descriptions of a mixed suite — the defect was never that the runner mis-described the run, it
// was that the exit-code seam had no way to tell this Inconclusive from an execution-time one. T2
// therefore added a THIRD member to the pair rather than moving either half of it:
// `SuiteResult.ProviderOrEngineFaultObserved`, asserted below on both run paths, and false on the
// control row. The CLI-side file consumes all three.
//
// THE THREE ROWS THAT START A TOPOLOGY CARRY `[Trait("requires", "docker")]`, AND THE TRAIT IS
// ABOUT THE LANE RATHER THAN ABOUT A CONTAINER. No container is involved in any of them: neither
// scenario of those three declares an `environment:` section, so `TopologyRequest.ForSuite` is
// handed a null environment — which `SuiteTopology.StartAsync` documents on its own parameter as
// "an empty topology (no resources, no health gates)". Nothing is pulled and nothing is
// health-gated. But that method has no null-environment short-circuit: it calls
// `HeadlessTopology.StartAsync` unconditionally, so each of those rows starts a REAL zero-resource
// DCP application.
//
// MEASURED (Windows 11, .NET 8, VSTest 17.11.1, `dotnet test --no-build -m:1 --logger trx --filter
// FullyQualifiedName~MixedSuiteEngineFaultTaxonomyTests`): exit 0, 4 passed / 0 failed, twice.
// Per-test durations read off the TRX rather than the console summary, worst of the two runs:
// 2.18 s and 1.71 s for the two docker-traited defect rows, 6.99 s for the docker-traited control
// row, and 0.15 s for the untraited topology-failure row, which starts no DCP at all. Two of the
// control row's seconds are its own deliberate over-budget sleep; where the remaining five go was
// NOT measured and no claim is made about it. Whole invocation, build excluded: 12.6 s wall clock.
// Container and network counts on the host were taken either side of a run and are UNCHANGED — 6
// containers and 3 networks before and after — which is the measured form of "no container is
// involved"; nothing here asserts anything about whether a container runtime is installed.
//
// That is precisely the shape this repo already classifies as Docker-requiring —
// `SuiteTopologyTests.SuiteTopology_NoEnvironment_StartsAndExposesEmptyServices` is a
// null-environment empty topology, it carries the same trait, and it additionally TOLERATES an
// `OrchestrationException`, because "DCP starts" is not something this repo is willing to assert.
// Issue #420 is on file for DCP refusing outright on a host whose state store it will not take
// ownership of. The blocking CI lane must not be able to go red for that, so these rows are
// traited out of it.
//
// WHAT THE TRAIT COSTS, NAMED SO THE ANSWER IS NOT "COVERAGE". Only the ENGINE-SIDE derivation of
// `ProviderOrEngineFaultObserved` leaves the blocking lane. The exit RULE it feeds stays pinned
// there by `Vouchfx.Cli.Tests.MixedSuiteEngineFaultExitCodeTests`, and the two hops carrying the
// value from `SuiteResult` into `ComputeExitCode` by
// `Vouchfx.Cli.Tests.MixedSuiteEngineFaultHopCensusTests` — neither of which starts anything. The
// fourth row below, the topology that fails to START, is deliberately NOT traited: it is refused
// inside `SuiteTopology.StartAsync` BEFORE `HeadlessTopology.StartAsync` is reached, so it never
// touches DCP and keeps one end-to-end engine-side row in the blocking lane.
//
// THE SCENARIOS OF EACH ROW DECLARE A BYTE-IDENTICAL ENVIRONMENT — for three rows, namely none —
// so the shared-`environment` divergence guard cannot fire. That guard compares
// `SerialiseEnvironment(scenarios[i].Environment)` against the first schema-valid scenario's, and
// two absent blocks serialise identically, so the suite is held to one topology exactly as a
// two-service suite would be. This is load-bearing: a divergence would return through
// `CompleteWithoutTopologyAsync` with `EnvironmentError`, and the pair these rows exist to pin
// would never be produced. The fourth row keeps the same discipline for a non-empty environment:
// both its documents are built from ONE interpolated environment string.
//
// THE DEFECT STUB IS `stub.throwing-bind`, DECLARED IN ProviderPipelineTests and already reused by
// ProviderBindThrowTaxonomyTests. Reused a third time rather than duplicated, for the reason that
// file's own header gives: one throwing-Bind fixture in this assembly cannot drift against itself.
// The remaining reflective surfaces already have their own stubs elsewhere in this assembly —
// `stub.throwing-validate`, `stub.throwing-resources`, `stub.throwing-emit` and
// `stub.throwing-compilerefs` in ProviderReflectiveFaultTaxonomyTests, `stub.throwing-hostresource`
// alongside the bind stub in ProviderPipelineTests. None of them is repeated here: the mixed-suite
// exposure is a property of the SUITE SHAPE and not of which surface threw, so one surface is
// enough and the per-surface matrix stays where it already lives.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Characterisation rows for issue #480: what a suite pairing a provider-defect scenario with a
/// genuinely-executing one hands to the CLI's exit-code seam, on BOTH run paths.
/// </summary>
public sealed class MixedSuiteEngineFaultTaxonomyTests
{
    /// <summary>
    /// Both the assembly holding the <c>stub.throwing-bind</c> fixture and the real
    /// <c>script.csharp</c> provider the sibling needs — the sibling has to COMPILE AND RUN, so a
    /// second stub would not do: the whole exposure is that something genuinely executed.
    /// </summary>
    private static readonly Assembly[] ProviderAssemblies =
    {
        typeof(MixedSuiteEngineFaultTaxonomyTests).Assembly,
        typeof(ScriptCsharpProvider).Assembly,
    };

    private const string AppHostAssemblyName = "Vouchfx.Engine.Runtime.Tests";

    /// <summary>
    /// The defective document: schema-valid, AST-building, and reachable only at bind time. No
    /// <c>environment:</c> section, so it declares the same (absent) environment as its sibling.
    /// </summary>
    private const string DefectScenarioYaml = """
        steps:
          - id: will-throw
            type: stub.throwing-bind
        """;

    /// <summary>
    /// The sibling that genuinely executes and passes. A <c>script.csharp</c> step whose body
    /// touches nothing but the shared <c>Vars</c> dictionary — no target, no dependency, no
    /// connection string — so it needs no resource in the topology to succeed.
    /// </summary>
    private const string PassingSiblingYaml = """
        steps:
          - id: sibling-runs
            type: script.csharp
            code: |
              Vars["mixed-suite-sibling"] = "ran";
        """;

    /// <summary>
    /// The CONTROL row's defective-looking-but-genuinely-executing scenario: a step that runs to
    /// completion and then has its outcome superseded, because it overran its own declared
    /// <c>timeout</c>. §12.1's <c>step-timeout</c> is Inconclusive and never Fail (#232), which is
    /// what makes this the same VERDICT as the defect suite reached by a categorically different
    /// route.
    /// </summary>
    /// <remarks>
    /// The body deliberately does NOT observe the step's cancellation token. <c>CsxAssembler</c>
    /// emits that token as the convention local <c>__stepCt_&lt;sanitisedId&gt;</c>, and naming it
    /// here would couple this row to a generated identifier that is not part of the authored
    /// language. It emits a LATE enforcement arm as well — a body that completes past the budget
    /// has its outcome superseded by an Inconclusive <c>step-timeout</c> — so an ordinary
    /// <c>Task.Delay</c> reaches the same verdict through the author-visible surface. Two seconds
    /// against a one-second budget is the smallest margin that is not a race on a loaded CI agent.
    /// </remarks>
    private const string TimingOutScenarioYaml = """
        steps:
          - id: will-time-out
            type: script.csharp
            timeout: 1s
            code: |
              await Task.Delay(TimeSpan.FromSeconds(2));
        """;

    private static readonly string[] s_mixedScenarioNames = { "defect-scenario", "passing-sibling" };

    private static readonly string[] s_timeoutScenarioNames = { "timeout-scenario", "passing-sibling" };

    // ── The defect suite, on both run paths ───────────────────────────────────

    /// <summary>
    /// The shared-topology path (<c>ScenarioRunner.RunSuiteAsync</c>, what a bare <c>vouchfx run</c>
    /// drives) hands the exit-code seam <see cref="Verdict.Inconclusive"/> together with
    /// <c>ExecutedAnyScenario == true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED, on <c>main</c> at <c>fb16c83</c> plus this branch's tests: the suite returns
    /// <see cref="Verdict.Inconclusive"/> (the defect scenario's early verdict elevated over the
    /// sibling's Pass) and <c>ExecutedAnyScenario == true</c>. Handed to the real
    /// <c>RunCommand.ComputeExitCode</c> with neither opt-in gate set, that pair returned
    /// <c>ExitCodes.Success</c> — which is the whole of #480.
    /// </para>
    /// <para>
    /// <strong>And it returns <c>ProviderOrEngineFaultObserved == true</c> since T2</strong>, which
    /// is what the exit-code seam now reads. On THIS path the value is accumulated across
    /// <c>RunSuiteAsync</c>'s Pass-B compile loop, from the marker
    /// <c>ProviderPipeline</c> set on the failure record when the stub's <c>Bind</c> threw. The
    /// row below drives the other runner, which reaches the same answer by folding its slots.
    /// </para>
    /// <para>
    /// <strong>The Pass assertion on the sibling is not decoration.</strong> Without it, a
    /// regression that stopped the sibling executing — a topology that failed to start, a
    /// <c>script.csharp</c> compile error — would leave the aggregate Inconclusive and this row
    /// green while the shape it exists to characterise had quietly stopped occurring. The flag
    /// assertion beside it would then be pinning the wrong <c>true</c>.
    /// </para>
    /// <para>
    /// <c>Assurance.Refusal</c> is asserted too, and it USED TO BE the only evidence surviving to
    /// the exit-code seam that distinguished this suite from an execution-time Inconclusive: the
    /// pre-topology door records <see cref="SecurityAbortKind.AuthoringFault"/>, whereas a suite
    /// that ran to completion records nothing. <c>ExitCodes.FromVerdict</c> reads only
    /// <c>Assurance.Unconfirmed</c>, which is false here (nothing was DECLARED, so no declared
    /// target can have gone unconfirmed) — so the distinguisher was present at the seam and
    /// unread. It is STILL unread: T2 deliberately did not key on it, because the same kind is
    /// recorded for any refused document and keying on it would redden every mixed suite
    /// containing one (issue #514). The assertion stays because it pins that this shape's assurance
    /// has not moved, which is what makes the CLI-side rows' inputs measured rather than assumed.
    /// The control row below pins the other half of that contrast.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task RunSuiteAsync_ProviderDefectBesideAPassingSibling_IsInconclusiveAndReportsSomethingExecuted()
    {
        var output = new StringWriter();

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: BuildAsts(DefectScenarioYaml, PassingSiblingYaml),
            scenarioNames: s_mixedScenarioNames,
            yamlTexts: new[] { DefectScenarioYaml, PassingSiblingYaml },
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: output);

        // NOT VACUOUS: the sibling really did execute and pass. The whole exposure is that
        // something ran; a row in which nothing ran would pin the single-scenario shape #413
        // already covers.
        Assert.Equal(Verdict.Pass, VerdictFor(result, "passing-sibling"));
        Assert.Equal(Verdict.Inconclusive, VerdictFor(result, "defect-scenario"));

        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(
            result.ExecutedAnyScenario,
            "the mixed suite skips CompleteWithoutTopologyAsync, so the flag keeps its `true` default.");

        Assert.Equal(SecurityAbortKind.AuthoringFault, result.Assurance.Refusal);
        Assert.False(
            result.Assurance.Unconfirmed,
            "an unsecured suite declares nothing, so no declared target can go unconfirmed.");

        // #480's provenance, END TO END: the marker set at ProviderPipeline's Bind guard, read off
        // PipelineResult.Failure at the pre-topology door, accumulated across Pass B, and returned
        // on the suite's normal-completion path — driven through the real runner, not asserted
        // about a hand-built record.
        Assert.True(
            result.ProviderOrEngineFaultObserved,
            "the defect scenario's Bind threw, which is what the exit-code seam must be able to "
            + "see past the sibling that ran.");

        AssertDiagnosticNamesTheDefectiveStep(output.ToString());
    }

    /// <summary>
    /// The <c>--parallel</c> path reaches the same pair by a different mechanism — the flag is
    /// DERIVED from the concatenated event buffers rather than defaulted — and the two paths must
    /// not disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED: same verdict, same <c>true</c>, same <see cref="SecurityAbortKind.AuthoringFault"/>.
    /// The derivation site is <c>ParallelSuiteRunner.cs:857</c>
    /// (<c>allBuffers.Exists(ContainsStepEvent)</c>), and the sibling's <c>step-started</c> line is
    /// what satisfies it — so unlike the sequential arm, this arm's <c>true</c> is a statement of
    /// FACT about the run rather than an unset default. That both routes arrive at the same answer
    /// is why #480 is a taxonomy problem and not a bug in one runner.
    /// </para>
    /// <para>
    /// The PUBLIC overload is driven, not the internal <c>RunParallelCoreAsync</c> seam with an
    /// injected fake core. A fake core could produce the buffer shape cheaply, but the property
    /// being characterised is that a REAL scenario executed beside a real defect; a synthetic
    /// buffer carrying a hand-written <c>step-started</c> line would prove only that the derivation
    /// reads the needle it is documented to read, which <c>RunParallelAsyncTests</c> already covers.
    /// The zero-resource topology makes the real core affordable here, so it is used.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task RunParallelAsync_ProviderDefectBesideAPassingSibling_IsInconclusiveAndDerivesSomethingExecuted()
    {
        var output = new StringWriter();

        var result = await ParallelSuiteRunner.RunParallelAsync(
            scenarios: BuildAsts(DefectScenarioYaml, PassingSiblingYaml),
            scenarioNames: s_mixedScenarioNames,
            yamlTexts: new[] { DefectScenarioYaml, PassingSiblingYaml },
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: output,
            maxConcurrency: 1);

        Assert.Equal(Verdict.Pass, VerdictFor(result, "passing-sibling"));
        Assert.Equal(Verdict.Inconclusive, VerdictFor(result, "defect-scenario"));

        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(
            result.ExecutedAnyScenario,
            "the sibling's step-started line satisfies the parallel path's own derivation.");

        Assert.Equal(SecurityAbortKind.AuthoringFault, result.Assurance.Refusal);
        Assert.False(result.Assurance.Unconfirmed);

        // #480 on the OTHER runner, and by the other mechanism: the core attaches the marker to its
        // ScenarioCoreResult, the slot array carries it across the fan-out, and RenderAndAggregate
        // ORs the slots after the gather joins. The two paths must not disagree about an exit code.
        Assert.True(
            result.ProviderOrEngineFaultObserved,
            "the parallel path folds the same fact from its per-scenario results.");

        AssertDiagnosticNamesTheDefectiveStep(output.ToString());
    }

    // ── The control: the SAME verdict, reached by executing ───────────────────

    /// <summary>
    /// A suite whose Inconclusive comes from a step that DID run and could not conclude hands the
    /// seam the same verdict and the same <c>true</c> — and records NO refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This row exists to keep the fix honest, and it is the reason the CLI-side control
    /// row can be written at all.</strong> §12.1 draws a distinction the exit-code seam must not
    /// lose: a scenario that executed and could not conclude — a timeout, a partition that
    /// outlasted its grace, an upstream capture never met — still exits 0 by default, on purpose.
    /// A fix for #480 that simply reddens every Inconclusive suite carrying
    /// <c>ExecutedAnyScenario == true</c> would break exactly that, and would pass a test file
    /// that contained only the defect rows above.
    /// </para>
    /// <para>
    /// MEASURED, and the measurement is the answer to "can the seam tell the two apart today":
    /// <c>Assurance.Refusal</c> is <see langword="null"/> here and
    /// <see cref="SecurityAbortKind.AuthoringFault"/> in both rows above, while EVERY OTHER
    /// argument <c>ComputeExitCode</c> receives is identical across the two shapes
    /// (<c>parsedCount: 2</c>, <c>parseFailureCount: 0</c>, <see cref="Verdict.Inconclusive"/>,
    /// both gates false, <c>executedAnyScenario: true</c>). So the seam COULD distinguish them, on
    /// that one member — and it did not read it: <c>ExitCodes.FromVerdict</c> consults only
    /// <c>Assurance.Unconfirmed</c>, which is false on both sides. What that established was that
    /// the information was not missing.
    /// </para>
    /// <para>
    /// <strong>`AuthoringFault` is broader than "a provider threw", and that breadth is why the
    /// fix does not read it.</strong> The same kind is recorded for an ordinary authoring fault — a
    /// schema-rejected sibling, an unresolvable <c>script.csharp file:</c> — so a fix keyed on it
    /// would redden those mixed suites too. That is arguably the correct generalisation of #480 (a
    /// document the engine refused, beside one that passed, is not a clean run either); it is
    /// filed as issue #514 and deliberately NOT decided by this change. T2 carried a narrow
    /// provenance marker from the guard sites instead, and the third assertion below — that this
    /// control row's <c>ProviderOrEngineFaultObserved</c> is false while both defect rows' is true
    /// — is what makes the difference between the two designs executable rather than editorial.
    /// </para>
    /// <para>
    /// The step-timeout route is chosen over the other two §12.1 Inconclusive causes because it is
    /// the only one reachable with no resource at all: a partition that outlasts its grace needs a
    /// broker, and an unmet upstream capture needs something upstream to have produced a response.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task RunSuiteAsync_ExecutionTimeInconclusiveBesideAPassingSibling_RecordsNoRefusal()
    {
        var output = new StringWriter();

        var result = await ScenarioRunner.RunSuiteAsync(
            scenarios: BuildAsts(TimingOutScenarioYaml, PassingSiblingYaml),
            scenarioNames: s_timeoutScenarioNames,
            yamlTexts: new[] { TimingOutScenarioYaml, PassingSiblingYaml },
            providerAssemblies: ProviderAssemblies,
            appHostAssemblyName: AppHostAssemblyName,
            output: output);

        // NOT VACUOUS: the timing-out scenario really did EXECUTE — it is Inconclusive because its
        // step ran and was cut off, not because a door refused it before the topology came up.
        Assert.Equal(Verdict.Inconclusive, VerdictFor(result, "timeout-scenario"));
        Assert.Equal(Verdict.Pass, VerdictFor(result, "passing-sibling"));

        Assert.Equal(Verdict.Inconclusive, result.Verdict);
        Assert.True(result.ExecutedAnyScenario);

        Assert.Null(result.Assurance.Refusal);
        Assert.False(result.Assurance.Unconfirmed);

        // THE ROW THE FIX HAD TO KEEP GREEN. A suite that ran and could not conclude entered no
        // provider surface guard, so nothing marked its (absent) failure, and the #480 rule does
        // not fire — this run still exits 0 by default, which §12.1 requires. A fix keyed on the
        // VERDICT, or on `Assurance.Refusal`, would have flipped this.
        Assert.False(
            result.ProviderOrEngineFaultObserved,
            "a step that ran and was cut off is not a provider defect, and must not be reported "
            + "as one.");
    }

    // ── The route on which the marker is the ONLY thing between the run and exit 0 ──

    /// <summary>
    /// A Pass-B provider defect followed by a topology that FAILS TO START: the aggregate is
    /// <see cref="Verdict.EnvironmentError"/>, nothing executed, and the marker is still true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the shape on which #369's rule and #480's disagree, and it is why threading
    /// the marker into <c>CompleteWithoutTopologyAsync</c> is load-bearing rather than tidy.</strong>
    /// The defect scenario takes its early <see cref="Verdict.Inconclusive"/> in Pass B and marks
    /// the accumulator; its runnable sibling leaves the all-early guard unfired, so the topology
    /// build IS attempted; the build throws; <c>RunSuiteAsync</c>'s <c>OrchestrationException</c>
    /// catch stamps <see cref="Verdict.EnvironmentError"/> onto every scenario that has no verdict
    /// of its own and returns through the without-topology completion path. The aggregate elevates
    /// to EnvironmentError (<c>VerdictPrecedence</c> 3 over Inconclusive's 1), and
    /// <c>ExitCodes.FromVerdict(EnvironmentError, failOnEnvironmentError: false, …)</c> over an
    /// unsecured suite returns <c>Success</c> — while #369's rule, scoped to
    /// <see cref="Verdict.Inconclusive"/> so that #390 stays open, never fires. The CLI-side row
    /// <c>ComputeExitCode_ProviderDefectThenAFailedTopology_DoesNotExitZero</c> hands the triple
    /// measured here to the real seam and gets 4.
    /// </para>
    /// <para>
    /// <strong>HOW THE TOPOLOGY IS MADE TO FAIL WITHOUT DOCKER, AND WHY IT IS NOT THE
    /// <c>${conn:typo}</c> TECHNIQUE <c>RunSuiteAsyncTests</c> USES.</strong> That technique
    /// reaches <c>EnvironmentMapper.Map</c>'s eager rejection, which arrives as an
    /// <see cref="ArgumentException"/> and is classified <see cref="Verdict.Inconclusive"/> — an
    /// authoring fault, deliberately, since §12.1 must not report one as infrastructure. It
    /// therefore produces the shape #369 already covers and could not produce this one. What is
    /// needed is an <c>OrchestrationException</c> raised before any container, and EDGE-012's
    /// pinned-host-port pre-flight is exactly that: <c>SuiteTopology.EnsurePinnedHostPortsAreFree</c>
    /// runs AFTER Map and BEFORE <c>HeadlessTopology.StartAsync</c>, and refuses a pin this machine
    /// cannot bind. The row holds the port itself, so the collision is created rather than hoped
    /// for — no DCP, no Aspire, no container runtime, and no dependence on what else the host
    /// happens to be listening on. This row is consequently NOT traited.
    /// </para>
    /// <para>
    /// The squatter binds <see cref="IPAddress.Any"/>, which is the first address the pre-flight
    /// probes on every platform, so the refusal does not depend on the OS-conditional address set
    /// <c>TryHold</c> documents. The port is allocated by the OS rather than hard-coded: a fixed
    /// port makes a test red by boot order on a host with a reserved range.
    /// </para>
    /// <para>
    /// <strong>NOT VACUOUS, and each assertion below rules out a different way of being green for
    /// the wrong reason.</strong> The verdict being EnvironmentError proves the topology build was
    /// reached and refused (an authoring fault would read Inconclusive);
    /// <c>ExecutedAnyScenario == false</c> proves nothing ran; and the sibling's own EnvironmentError
    /// proves it was the scenario that WOULD have run, not a second early verdict. Without all
    /// three, a regression that refused the suite one door earlier would leave the marker assertion
    /// pinning a shape this row was not written for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunSuiteAsync_ProviderDefectThenAFailedTopology_IsEnvironmentErrorAndNothingExecuted()
    {
        var port = FindAFreePort();
        var squatter = new TcpListener(IPAddress.Any, port);
        squatter.Start();
        try
        {
            var defectYaml = PinnedPortEnvironment(port) + DefectScenarioYaml;
            var siblingYaml = PinnedPortEnvironment(port) + PassingSiblingYaml;
            var output = new StringWriter();

            var result = await ScenarioRunner.RunSuiteAsync(
                scenarios: BuildAsts(defectYaml, siblingYaml),
                scenarioNames: s_mixedScenarioNames,
                yamlTexts: new[] { defectYaml, siblingYaml },
                providerAssemblies: ProviderAssemblies,
                appHostAssemblyName: AppHostAssemblyName,
                output: output);

            Assert.Equal(Verdict.Inconclusive, VerdictFor(result, "defect-scenario"));
            Assert.Equal(Verdict.EnvironmentError, VerdictFor(result, "passing-sibling"));

            Assert.Equal(Verdict.EnvironmentError, result.Verdict);
            Assert.False(
                result.ExecutedAnyScenario,
                "the topology never came up, so no step ran on either scenario.");

            // THE POINT OF THE ROW. #369's rule cannot fire on an EnvironmentError aggregate and
            // FromVerdict maps it to Success without --fail-on-env-error, so this marker is the
            // only evidence at the seam that a provider defect was seen.
            Assert.True(
                result.ProviderOrEngineFaultObserved,
                "Pass B marked the defect before the topology was attempted, and the "
                + "topology-failure return must carry that fact rather than resetting it.");

            // MEASURED, and it is what the CLI-side row is fed: `Refusing` keeps the more
            // consequential refusal and AuthoringFault outranks TopologyUnavailable, so the
            // topology failure does NOT overwrite the compile-time refusal and the assurance
            // reaching the seam is byte-identical to the one the two defect rows above measure.
            Assert.Equal(SecurityAbortKind.AuthoringFault, result.Assurance.Refusal);

            // Unsecured, so ExitCodes.FromVerdict's REQ-018 carve-out is inert and the answer
            // really is Success before the #480 rule runs.
            Assert.False(result.Assurance.Unconfirmed);

            // The pre-flight really is what refused the build — not a mock, and not a different
            // door that happens to produce the same verdict.
            Assert.Contains(
                port.ToString(CultureInfo.InvariantCulture),
                output.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            squatter.Stop();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A one-service environment pinning <paramref name="hostPort"/>, written once and spliced
    /// onto both documents so the shared-<c>environment</c> divergence guard sees two identical
    /// blocks.
    /// </summary>
    /// <remarks>
    /// The image is never pulled: the pre-flight refuses the pin before Aspire is asked to build
    /// anything, so the reference only has to be schema-valid.
    /// </remarks>
    private static string PinnedPortEnvironment(int hostPort) =>
        $"""
        environment:
          services:
            api:
              image: myorg/api:1.0
              ports:
                - "{hostPort}:9093"

        """;

    /// <summary>
    /// Finds a port nothing currently holds, by binding the any-address on port 0 and releasing
    /// it — the same shape (and the same reasoning) as
    /// <c>PinnedHostPortPreflightTests.FindAFreePort</c>.
    /// </summary>
    private static int FindAFreePort()
    {
        var probe = new TcpListener(IPAddress.Any, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// The diagnostic must name BOTH the offending step id and the provider's dotted step type.
    /// </summary>
    /// <remarks>
    /// A non-zero exit that does not say WHICH step is defective is not the fix. On a mixed suite
    /// this text reaches the terminal through the per-scenario early-verdict arm of
    /// <c>RunSuiteAsync</c>'s completion loop — a different site from the one
    /// <c>CompleteWithoutTopologyAsync</c> uses for the all-early case, and the site #372 had to be
    /// extended to at all, which is precisely why it is asserted on this shape rather than assumed
    /// from the solo rows.
    /// </remarks>
    private static void AssertDiagnosticNamesTheDefectiveStep(string rendered)
    {
        Assert.Contains("will-throw", rendered, StringComparison.Ordinal);
        Assert.Contains("stub.throwing-bind", rendered, StringComparison.Ordinal);
    }

    /// <summary>The verdict recorded for one named scenario.</summary>
    private static Verdict VerdictFor(SuiteResult result, string scenarioName) =>
        Assert.Single(result.ScenarioVerdicts, v => v.ScenarioName == scenarioName).Verdict;

    /// <summary>
    /// Builds the ASTs over ONE frozen registry — the same registry both runners will build for
    /// themselves from <see cref="ProviderAssemblies"/>, so the documents and the run agree about
    /// which step types exist.
    /// </summary>
    private static ScenarioAst[] BuildAsts(params string[] yamlTexts)
    {
        var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);
        var asts = new ScenarioAst[yamlTexts.Length];
        for (var i = 0; i < yamlTexts.Length; i++)
        {
            asts[i] = AstBuilder.Build(YamlDocumentParser.Parse(yamlTexts[i]), registry);
        }

        return asts;
    }
}
