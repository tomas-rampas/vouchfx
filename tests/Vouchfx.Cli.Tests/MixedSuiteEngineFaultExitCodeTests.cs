// Vouchfx.Cli.Tests — issue #480's actual defect: THE EXIT CODE, when a provider-defect scenario
// sits beside a sibling that genuinely ran. No Docker, and no topology.
//
// WHAT IS UNPINNED. Issues #413 and #466 both closed by turning a throw from a provider's
// reflective SDK surface into a `PipelineResult.Failure`, so the scenario takes an early
// `Verdict.Inconclusive` at the pre-topology authoring door and the suite exits 4. Both left
// behind rows that run the defective document ALONE — `ReflectiveFaultExitCodeTests` in this
// project, `ProviderBindThrowTaxonomyTests` and `ProviderReflectiveFaultTaxonomyTests` in
// Vouchfx.Engine.Runtime.Tests. There is no row anywhere, on either run path, for the same defect
// BESIDE A PASSING SIBLING, and that absence is why the gap survived both issues' reviews.
//
// THE EXIT CODE FLIPS ON THE SIBLING, NOT ON THE DEFECT. `SuiteResult.ExecutedAnyScenario`
// defaults to `true` (named rather than cited by line — that number has already moved once on this
// branch). On the shared-topology path it is set false by `CompleteWithoutTopologyAsync`, which a
// mixed suite deliberately never reaches — the all-early guard requires EVERY scenario to carry an
// early verdict. The parallel path does not default the flag at all — it DERIVES it, and the
// sibling's own `step-started` line is what makes it `true`
// (ParallelSuiteRunner.cs:857). `ComputeExitCode`'s #369 rule is conditioned on
// `!executedAnyScenario`, so with a sibling present it does not fire, and
// `ExitCodes.FromVerdict(Inconclusive, failOnInconclusive: false, …)` returns Success. Add one
// unrelated passing scenario to a directory and the same provider defect goes from exit 4 to
// exit 0.
//
// WHY THIS IS SPLIT ACROSS TWO PROJECTS, AND WHY NEITHER HALF IS MOVED TO THE OTHER. The split is
// forced by two csproj facts that point in opposite directions, and it is the mirror image of the
// constraint `NothingExecutedExitCodeParityTests` records for #369:
//   • `ComputeExitCode` and `ExitCodes` are `internal` to Vouchfx.Cli and reach exactly one test
//     project through its InternalsVisibleTo. Vouchfx.Engine.Runtime.Tests has no reference to
//     Vouchfx.Cli at all, so the integer cannot be computed there.
//   • This project deliberately does NOT set `IsAspireHost` (its csproj says so, and says why), so
//     it carries no DCP metadata and no scenario can execute here. The pair the defect turns on —
//     an Inconclusive suite that nonetheless EXECUTED something — therefore cannot be produced in
//     this project at all.
// Neither csproj is changed and no `InternalsVisibleTo` grant is added. Pointing an Aspire-host
// test project at the CLI to assert an integer, or making the lean CLI project an Aspire host to
// run a scenario, would each buy one file at the cost of the boundary both projects are shaped by.
// The derivation half lives in `Vouchfx.Engine.Runtime.Tests.MixedSuiteEngineFaultTaxonomyTests`,
// which MEASURES the pair below rather than asserting three literals somebody typed; this file
// hands those measured values to the real `ComputeExitCode` with the same argument list
// `RunCommand.ExecuteAsync` passes.
//
// MEASURED RED BEFORE THE FIX: with the same arguments the rows below pass, minus the provenance
// signal T2 added, `ComputeExitCode` returned 0 (`ExitCodes.Success`). MEASURED GREEN AFTER: 4.
//
// MEASURED GREEN BEFORE AND AFTER — the control row: the same call with
// `SecurityAssurance.None` returns 0 and must keep returning 0. §12.1 says a scenario that DID run
// and could not conclude — a timeout, a partition that outlasted its grace, an unmet upstream
// capture — exits 0 by default, on purpose. A "fix" that simply reddened every Inconclusive suite
// carrying `executedAnyScenario: true` would break that and would still pass a file containing
// only the row above.
//
// COULD THE SEAM TELL THE TWO APART BEFORE THE FIX? YES, ON EXACTLY ONE MEMBER, AND IT DID NOT
// READ IT. Every other argument `ComputeExitCode` received was identical across the two shapes:
// `parsedCount: 2`, `parseFailureCount: 0`, `Verdict.Inconclusive`, both gates false,
// `executedAnyScenario: true`. The one difference was `SecurityAssurance.Refusal` —
// `SecurityAbortKind.AuthoringFault` for the suite refused at a pre-topology door, `null` for the
// suite that ran to completion — and both values were MEASURED off a real run by the derivation
// file's rows, not reasoned about here. `ExitCodes.FromVerdict` consults only
// `SecurityAssurance.Unconfirmed`, which is false on BOTH sides (an unsecured suite declares
// nothing, so no declared target can go unconfirmed), so the distinguisher was present at the seam
// and unread. What these two rows established was that the information was not missing.
//
// AND `AuthoringFault` IS BROADER THAN "A PROVIDER THREW". The same kind is recorded for an
// ordinary authoring fault — a schema-rejected sibling, an unresolvable `script.csharp file:` — so
// a fix keyed on it reddens those mixed suites too. That is arguably the correct generalisation of
// #480 (a document the engine refused, beside one that passed, is not a clean run either), but it
// is a scope decision and was written down here so it got made rather than discovered.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────
// THE T2 DECISION, AND WHAT THE ROWS BELOW NOW HAND IN. The widening described in the paragraph
// above was CONSIDERED AND DECLINED, and it is filed as issue #514 rather than dropped: whether a
// schema error, an unresolvable secret, a malformed dependency `env:` or a protocol conflict
// should ALSO be sibling-independent — the way #425 already makes a parse failure — is a real
// question with a wider blast radius than #480's, and answering it silently inside #480's fix
// would have reddened every mixed suite containing one mistyped document.
//
// So the fix does NOT read `Refusal`. `ProviderPipeline` now marks the failure at the point of
// the fault instead: `ValidationFailure.IsProviderOrEngineFault` is set at the six
// `DescribeProviderFault` guards and the suite-level CSX-assembler guard, and NOWHERE else — the
// same init-only-marker mechanism `IsSecurityPreflight` already used, and cited as the precedent
// at its declaration. `ScenarioRunner` and `ParallelSuiteRunner` carry it to
// `SuiteResult.ProviderOrEngineFaultObserved`, and `ComputeExitCode` takes it as a new optional
// argument. The rows below therefore vary TWO things between the defect shape and the control
// shape, and only the second of them is read: the assurance (measured, and now decorative at this
// seam) and the provenance marker (measured, and load-bearing).
//
// The exit rule is `providerOrEngineFaultObserved && code == ExitCodes.Success` → Inconclusive,
// placed after #369's rule and conditioned the same way, so it states "a provider or engine defect
// never exits 0" and never "exits 4" — the Failing-sibling row below is what keeps that
// distinction honest.
//
// AND ONE SHAPE HERE IS NOT A MIXED-SUITE SHAPE AT ALL. The topology-failure row below drives the
// only route on which #369's rule and #480's DISAGREE: a Pass-B provider defect whose runnable
// sibling gets as far as the topology build, which then fails. The aggregate is
// `Verdict.EnvironmentError` and `executedAnyScenario` is false, so #369's Inconclusive-scoped
// rule cannot fire and `FromVerdict` returns Success by #390's still-open default. On that shape
// the marker is the ONLY thing between the run and a green CI build, which is why
// `CompleteWithoutTopologyAsync` takes it as a parameter rather than assuming `false`. Its inputs
// are measured by the derivation file's fourth row, which needs no Docker: EDGE-012's
// pinned-host-port pre-flight refuses the build inside `SuiteTopology.StartAsync` and before
// `HeadlessTopology.StartAsync`.
//
// THE TWO HOPS BETWEEN `SuiteResult` AND THIS SEAM ARE NOT COVERED BY EITHER FILE, and are pinned
// by `MixedSuiteEngineFaultHopCensusTests` in this project — a source census, because the
// end-to-end route is unavailable for two csproj reasons that file states and measures.

using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Exit-code rows for issue #480: the pair a mixed suite hands the CLI, and what
/// <see cref="RunCommand.ComputeExitCode"/> currently makes of it.
/// </summary>
public sealed class MixedSuiteEngineFaultExitCodeTests
{
    /// <summary>
    /// A provider defect beside a passing sibling must not exit 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED: <c>0</c> before T2, <c>4</c> after it. The inputs are not invented — every one of
    /// them was read off a real two-scenario run in
    /// <c>Vouchfx.Engine.Runtime.Tests.MixedSuiteEngineFaultTaxonomyTests</c>, on BOTH run paths,
    /// which agree: aggregate <see cref="Verdict.Inconclusive"/> (the defect scenario's early
    /// verdict elevated over the sibling's Pass), <c>ExecutedAnyScenario == true</c>, an assurance
    /// carrying <see cref="SecurityAbortKind.AuthoringFault"/> with nothing declared and nothing
    /// confirmed, and — since T2 — <c>ProviderOrEngineFaultObserved == true</c>, which the
    /// derivation file asserts on both run paths against the real runners.
    /// </para>
    /// <para>
    /// <strong>The PROPERTY is "never Success"; the integer is asserted as well, and the two
    /// assertions are not redundant.</strong> T1 deliberately pinned only the property, because
    /// which non-zero code was right was still open. It is now decided:
    /// <see cref="ExitCodes.Inconclusive"/> (4), the same code the SAME defect returns when it is
    /// alone in a directory through #369's rule — sibling-independence is the whole point, so a
    /// mixed suite answering differently from a solo one would reintroduce the defect in a
    /// narrower spelling. Pinning the integer catches a regression to 3
    /// (<see cref="ExitCodes.EnvironmentError"/>, which would be a mis-classification of an engine
    /// defect as infrastructure) or to 1 (<see cref="ExitCodes.TestFailure"/>, which would claim
    /// the SUITE observed a product defect when nothing ever ran) — both non-zero, both wrong, and
    /// both invisible to a bare "not Success".
    /// </para>
    /// <para>
    /// The two gate flags are false because that is what an unqualified <c>vouchfx run</c> passes.
    /// A row that set <c>--fail-on-inconclusive</c> would exit 4 before the fix too and prove
    /// nothing: the defect is precisely that the DEFAULT invocation was green.
    /// </para>
    /// </remarks>
    [Fact]
    public void ComputeExitCode_ProviderDefectBesideAPassingSibling_DoesNotExitZero()
    {
        var exitCode = ExitCodeFor(
            PreTopologyRefusalAssurance, providerOrEngineFaultObserved: true);

        Assert.NotEqual(ExitCodes.Success, exitCode);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    /// <summary>
    /// A provider defect beside a FAILING sibling still exits 1: the new rule must not override a
    /// code another rule already chose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes the guard's <c>code == ExitCodes.Success</c> condition testable rather
    /// than merely stated. <see cref="Verdict.Fail"/> outranks <see cref="Verdict.Inconclusive"/>
    /// by <c>ScenarioRunner</c>'s own precedence, so a suite carrying both aggregates to Fail and
    /// <see cref="ExitCodes.FromVerdict"/> returns 1 before the #480 guard is reached. A guard
    /// written as "if a provider defect was observed, return Inconclusive" would turn that 1 into
    /// a 4 and tell CI a real product failure was merely undetermined — a strictly worse report
    /// than the one #480 exists to fix.
    /// </para>
    /// <para>
    /// The same shape is what #425's and #369's rules already promise for themselves, in their own
    /// comments, using the same condition. This row is the third instance of one property and is
    /// written out because a promise in a comment is not a pin.
    /// </para>
    /// </remarks>
    [Fact]
    public void ComputeExitCode_ProviderDefectBesideAFailingSibling_StillExitsTestFailure()
    {
        var exitCode = ExitCodeFor(
            PreTopologyRefusalAssurance,
            providerOrEngineFaultObserved: true,
            suiteVerdict: Verdict.Fail);

        Assert.Equal(ExitCodes.TestFailure, exitCode);
    }

    /// <summary>
    /// <c>--fail-on-env-error</c> changes nothing about this shape: it exits 4 either way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate is scoped to <see cref="Verdict.EnvironmentError"/>, and a provider defect is
    /// deliberately NOT one — §12.1 reserves EnvironmentError for infrastructure an author cannot
    /// fix by editing the suite, and a provider whose <c>Bind</c> throws is neither infrastructure
    /// nor the author's fault. So the flag cannot reach this shape, and this row pins that it does
    /// not: an implementation that reddened the run by classifying the defect as an environment
    /// error would pass the row above (4 is 4) and fail here only if it ALSO made the answer
    /// depend on the flag — which is why the assertion is an equality against the un-flagged
    /// answer rather than a bare "non-zero".
    /// </para>
    /// </remarks>
    [Fact]
    public void ComputeExitCode_ProviderDefectBesideAPassingSibling_IsUnaffectedByFailOnEnvError()
    {
        var withoutGate = ExitCodeFor(
            PreTopologyRefusalAssurance, providerOrEngineFaultObserved: true);

        var withGate = ExitCodeFor(
            PreTopologyRefusalAssurance,
            providerOrEngineFaultObserved: true,
            failOnEnvironmentError: true);

        Assert.Equal(withoutGate, withGate);
        Assert.Equal(ExitCodes.Inconclusive, withGate);
    }

    /// <summary>
    /// A genuine EXECUTION-time Inconclusive beside a passing sibling must keep exiting 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED GREEN, BEFORE AND AFTER, and that is the whole job of this row: a change that
    /// simply reddened every Inconclusive suite would satisfy the row above and fail here. §12.1
    /// keeps four outcomes separate and gates the undetermined one behind
    /// <c>--fail-on-inconclusive</c> on purpose — a step that ran and could not conclude is not a
    /// defect the engine can attribute to anybody, and reddening it by default is the behaviour
    /// that destroys trust in the taxonomy.
    /// </para>
    /// <para>
    /// The assurance is <see cref="SecurityAssurance.None"/> because that is what such a run
    /// MEASURABLY returns — the derivation file's control row drives a real suite whose first
    /// scenario overruns its own <c>timeout</c> (§12.1's <c>step-timeout</c>, Inconclusive and
    /// never Fail) beside the same passing sibling, and asserts <c>Assurance.Refusal is null</c>
    /// there. No pre-topology door ran, so nothing recorded a refusal.
    /// </para>
    /// </remarks>
    [Fact]
    public void ComputeExitCode_ExecutionTimeInconclusiveBesideAPassingSibling_StillExitsZero()
    {
        var exitCode = ExitCodeFor(SecurityAssurance.None, providerOrEngineFaultObserved: false);

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    /// <summary>
    /// A provider defect followed by a topology that FAILED TO START must not exit 0 either — and
    /// on this shape the marker is the only thing that stops it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the one shape on which #369's rule and #480's disagree, so it is the row
    /// that makes threading the marker into <c>CompleteWithoutTopologyAsync</c> load-bearing rather
    /// than tidy.</strong> Every argument below is measured, by
    /// <c>MixedSuiteEngineFaultTaxonomyTests.RunSuiteAsync_ProviderDefectThenAFailedTopology_IsEnvironmentErrorAndNothingExecuted</c>,
    /// which drives a real two-scenario suite whose provider throws in Pass B and whose topology is
    /// then refused by EDGE-012's pinned-host-port pre-flight: aggregate
    /// <see cref="Verdict.EnvironmentError"/>, <c>ExecutedAnyScenario == false</c>, the SAME
    /// <see cref="SecurityAbortKind.AuthoringFault"/> assurance the other defect rows carry (the
    /// topology failure records <c>TopologyUnavailable</c>, which loses to it on
    /// <c>Refusing</c>'s precedence), and <c>ProviderOrEngineFaultObserved == true</c>.
    /// </para>
    /// <para>
    /// TRACE THE SEAM WITH THE MARKER REMOVED and the answer is 0.
    /// <see cref="ExitCodes.FromVerdict"/> maps EnvironmentError to
    /// <see cref="ExitCodes.Success"/> whenever <c>--fail-on-env-error</c> is absent and nothing
    /// was declared — which is #390, deliberately open. #425's rule needs a parse failure and
    /// there is none. #369's rule needs the aggregate to be <see cref="Verdict.Inconclusive"/>,
    /// and it is not: the sibling that would have run was stamped EnvironmentError, which outranks
    /// the defect's Inconclusive by <c>VerdictPrecedence</c>. So no earlier rule fires, and #480's
    /// is the only one left.
    /// </para>
    /// <para>
    /// <strong>It does NOT close #390, and the difference is what the row pins.</strong> A suite
    /// whose topology merely failed still exits 0 — that shape sets no marker, because no provider
    /// code was entered. What exits 4 here is a suite in which the engine KNOWS it was inside a
    /// provider when something threw, and whose topology then also failed. The final assertion
    /// below is that contrast in executable form: the same arguments with the marker false are a
    /// clean 0.
    /// </para>
    /// </remarks>
    [Fact]
    public void ComputeExitCode_ProviderDefectThenAFailedTopology_DoesNotExitZero()
    {
        var exitCode = ExitCodeFor(
            PreTopologyRefusalAssurance,
            providerOrEngineFaultObserved: true,
            suiteVerdict: Verdict.EnvironmentError,
            executedAnyScenario: false);

        Assert.NotEqual(ExitCodes.Success, exitCode);
        Assert.Equal(ExitCodes.Inconclusive, exitCode);

        // #390 stays open: the identical run WITHOUT a provider defect is still a clean 0.
        Assert.Equal(
            ExitCodes.Success,
            ExitCodeFor(
                PreTopologyRefusalAssurance,
                providerOrEngineFaultObserved: false,
                suiteVerdict: Verdict.EnvironmentError,
                executedAnyScenario: false));
    }

    /// <summary>
    /// NOT VACUOUS: the defect row and the control row differ in the provenance marker, and in
    /// NOTHING the exit-code seam reads apart from it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, a future edit could quietly give the two rows different verdicts or different
    /// <c>executedAnyScenario</c> values and leave both green while the contrast they were written
    /// to express had evaporated — the row that must redden reddening for the wrong reason.
    /// </para>
    /// <para>
    /// <strong>It also pins the shape of the T2 decision, which is a claim about what the fix does
    /// NOT read.</strong> The two assurances still differ — <see cref="SecurityAbortKind.AuthoringFault"/>
    /// against <see langword="null"/> — and that difference is now decorative at this seam:
    /// <see cref="ExitCodes.FromVerdict"/> consults only <see cref="SecurityAssurance.Unconfirmed"/>,
    /// which is false on both sides, and the #480 guard consults only its own argument. The
    /// executable form of that is the last row here: hand the DEFECT's assurance in with the marker
    /// false and the answer is 0, so no rule anywhere in the seam is keying on
    /// <see cref="SecurityAssurance.Refusal"/>. If a later change makes it do so — closing #514 —
    /// this row fails and says which claim moved, rather than the widening arriving silently.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTwoRows_DifferInTheProvenanceMarkerAndNothingElseTheSeamReads()
    {
        var refused = PreTopologyRefusalAssurance;

        Assert.Equal(SecurityAbortKind.AuthoringFault, refused.Refusal);
        Assert.Null(SecurityAssurance.None.Refusal);

        // Everything ExitCodes.FromVerdict actually reads is identical on both sides.
        Assert.False(refused.Unconfirmed);
        Assert.False(SecurityAssurance.None.Unconfirmed);
        Assert.Empty(refused.Declared);
        Assert.Empty(refused.Confirmed);

        // And the fix keys on the marker, not on the refusal: the defect suite's own assurance,
        // handed in with the marker false, is a clean exit.
        Assert.Equal(
            ExitCodes.Success,
            ExitCodeFor(refused, providerOrEngineFaultObserved: false));
    }

    /// <summary>
    /// The assurance a suite refused at a pre-topology authoring door carries: a recorded
    /// <see cref="SecurityAbortKind.AuthoringFault"/> over an unsecured document, so nothing is
    /// declared and nothing is confirmed.
    /// </summary>
    /// <remarks>
    /// Built through the production <see cref="SecurityAssurance.Refusing"/> API rather than
    /// constructed positionally, so this fixture cannot drift from the shape
    /// <c>ScenarioRunner.RunSuiteAsync</c>'s compile loop actually produces — which is the same
    /// call, on the same starting value.
    /// </remarks>
    private static SecurityAssurance PreTopologyRefusalAssurance =>
        SecurityAssurance.None.Refusing(SecurityAbortKind.AuthoringFault);

    /// <summary>
    /// The same argument list <c>RunCommand.ExecuteAsync</c> passes for a fully-parsed
    /// TWO-scenario run — the mixed suite the derivation file drives — with only the arguments a
    /// row varies exposed as parameters.
    /// </summary>
    /// <param name="securityAssurance">
    /// What the suite established about its <c>security</c> blocks; measured off a real run by the
    /// derivation file for both shapes.
    /// </param>
    /// <param name="providerOrEngineFaultObserved">
    /// Issue #480's provenance signal, added by T2. Deliberately a REQUIRED parameter here while
    /// <c>ComputeExitCode</c> itself defaults it to <see langword="false"/>: the default exists so
    /// the engine's other callers and the existing exit-code rows keep compiling, but a row in
    /// THIS file that left it implicit would be asserting about #480 without stating which side of
    /// it the row is on.
    /// </param>
    /// <param name="suiteVerdict">
    /// <see cref="Verdict.Inconclusive"/> for both of the shapes this file was written for; the
    /// Failing-sibling row overrides it to prove the guard cannot override a code already chosen.
    /// </param>
    /// <param name="failOnEnvironmentError">
    /// False for an unqualified <c>vouchfx run</c>; one row sets it to prove the answer does not
    /// move.
    /// </param>
    /// <param name="executedAnyScenario">
    /// <see langword="true"/> for the mixed suite this file was written for — the sibling ran, which
    /// is the whole exposure. The topology-failure row overrides it to <see langword="false"/>,
    /// which is what the runner MEASURABLY returns there, and that row is the only shape here on
    /// which #369's rule could compete for the answer at all.
    /// </param>
    /// <remarks>
    /// <c>failOnInconclusive</c> is NOT exposed, deliberately: every shape in this file exits 4
    /// under it, so a row that set it would pass whether or not the fix existed. The defect #480
    /// names is precisely that the DEFAULT invocation was green.
    /// </remarks>
    private static int ExitCodeFor(
        SecurityAssurance securityAssurance,
        bool providerOrEngineFaultObserved,
        Verdict suiteVerdict = Verdict.Inconclusive,
        bool failOnEnvironmentError = false,
        bool executedAnyScenario = true) =>
        RunCommand.ComputeExitCode(
            parsedCount: 2,
            parseFailureCount: 0,
            suiteVerdict: suiteVerdict,
            failOnEnvironmentError: failOnEnvironmentError,
            failOnInconclusive: false,
            securityAssurance: securityAssurance,
            executedAnyScenario: executedAnyScenario,
            providerOrEngineFaultObserved: providerOrEngineFaultObserved);
}
