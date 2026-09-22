// Vouchfx.Cli.Tests — issue #515: a #480 provider-defect exit gives no stdout reason connecting
// it to the exit code, unlike REQ-018's security rule beside it. No Docker.
//
// WHAT WAS MISSING. RunCommand.ExecuteAsync prints SecurityUnconfirmableNotice whenever REQ-018's
// carve-out is why a run exits non-zero, so an author reading the terminal knows WHICH rule fired.
// #480's rule (issue #480, "a provider or engine defect never exits 0") printed nothing equivalent
// — and unlike #425's and #369's rules, which fire on a run that is VISIBLY broken (nothing
// executed, or a file could not be read), #480's rule can fire on a run whose terminal shows a
// PASSING scenario, with the refused sibling's own diagnostic one line among many. The author sees
// a green-looking run and a non-zero exit, and nothing connects the two.
//
// THE FIX is RunCommand.ProviderOrEngineFaultNotice, printed exactly when
// RunCommand.ShouldPrintProviderOrEngineFaultNotice answers true — which this file pins is EXACTLY
// when #480's rule (and no earlier rule, and not REQ-018) is what took the exit code off Success.
//
// WHY THE PREDICATE IS TESTED DIRECTLY, DOCKER-FREE, RATHER THAN THROUGH ExecuteAsync ITSELF. The
// same two csproj facts MixedSuiteEngineFaultExitCodeTests' own header records for #480's exit
// code apply unchanged to #515's notice: the CLI's registry is a sealed 25-assembly Core list (no
// throwing stub reaches it through the front door — see ReflectiveFaultExitCodeTests' header for
// the same constraint), and this project is not an Aspire host, so no scenario can execute here at
// all. ShouldPrintProviderOrEngineFaultNotice is the extracted, Docker-free seam this file drives
// instead, with the SAME argument shapes MixedSuiteEngineFaultExitCodeTests and
// SecurityConfirmationExitCodeTests already measured for ComputeExitCode.
//
// MEASURED RED BEFORE THE FIX: ShouldPrintProviderOrEngineFaultNotice did not exist at all (the
// call site printed nothing, unconditionally). MEASURED GREEN AFTER: the rows below.

using System;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Boundary rows for <see cref="RunCommand.ShouldPrintProviderOrEngineFaultNotice"/>: printed
/// exactly when issue #480's rule is what took the exit code off <see cref="ExitCodes.Success"/>.
/// </summary>
public sealed class ShouldPrintProviderOrEngineFaultNoticeTests
{
    /// <summary>
    /// THE HEADLINE SHAPE: a provider defect beside a passing sibling. Mirrors
    /// <c>MixedSuiteEngineFaultExitCodeTests.ComputeExitCode_ProviderDefectBesideAPassingSibling_DoesNotExitZero</c>'s
    /// own inputs — the exit code is 4 there, and this is the row that says why the terminal must
    /// now explain it.
    /// </summary>
    [Fact]
    public void ProviderDefectBesideAPassingSibling_Prints()
    {
        Assert.True(ShouldPrintFor(
            PreTopologyRefusalAssurance, providerOrEngineFaultObserved: true));
    }

    /// <summary>
    /// No provider or engine fault was observed at all — a genuine execution-time Inconclusive
    /// beside a passing sibling, the same shape
    /// <c>ComputeExitCode_ExecutionTimeInconclusiveBesideAPassingSibling_StillExitsZero</c> pins at
    /// the exit-code seam. There is nothing for this notice to connect.
    /// </summary>
    [Fact]
    public void NoProviderOrEngineFaultObserved_NeverPrints()
    {
        Assert.False(ShouldPrintFor(SecurityAssurance.None, providerOrEngineFaultObserved: false));
    }

    /// <summary>
    /// A <see cref="Verdict.Fail"/> already took the code to <see cref="ExitCodes.TestFailure"/>
    /// before #480's rule is ever consulted — mirrors
    /// <c>ComputeExitCode_ProviderDefectBesideAFailingSibling_StillExitsTestFailure</c>. The run's
    /// own genuine failure is why it is red, not #480's rule, so this notice must stay silent.
    /// </summary>
    [Fact]
    public void FailVerdictAlreadyChoseTheCode_DoesNotPrint()
    {
        Assert.False(ShouldPrintFor(
            PreTopologyRefusalAssurance,
            providerOrEngineFaultObserved: true,
            suiteVerdict: Verdict.Fail));
    }

    /// <summary>
    /// <c>--fail-on-env-error</c> takes the code to <see cref="ExitCodes.EnvironmentError"/> (3)
    /// before #480's rule is reached — mirrors
    /// <c>ComputeExitCode_ProviderDefectThenAFailedTopology_UnderFailOnEnvErrorExitsThree</c>, the
    /// exit-code seam's own stated boundary of the whole rule.
    /// </summary>
    [Fact]
    public void FailOnEnvironmentErrorTookTheCodeFirst_DoesNotPrint()
    {
        Assert.False(ShouldPrintFor(
            PreTopologyRefusalAssurance,
            providerOrEngineFaultObserved: true,
            suiteVerdict: Verdict.EnvironmentError,
            failOnEnvironmentError: true,
            executedAnyScenario: false));
    }

    /// <summary>
    /// THE CONVERSE OF THE ROW ABOVE, and the one shape on which #369's nothing-executed rule and
    /// #480's rule disagree (see
    /// <c>ComputeExitCode_ProviderDefectThenAFailedTopology_DoesNotExitZero</c>'s own remarks):
    /// WITHOUT <c>--fail-on-env-error</c>, <c>FromVerdict</c> maps <see cref="Verdict.EnvironmentError"/>
    /// to <see cref="ExitCodes.Success"/> (#390, deliberately open), #369's rule needs the aggregate
    /// to be <see cref="Verdict.Inconclusive"/> and this one is not, so #480's rule is the ONLY rule
    /// standing between this run and a clean exit. This is the case the notice matters most for.
    /// </summary>
    [Fact]
    public void ProviderDefectThenAnUngatedFailedTopology_Prints()
    {
        Assert.True(ShouldPrintFor(
            PreTopologyRefusalAssurance,
            providerOrEngineFaultObserved: true,
            suiteVerdict: Verdict.EnvironmentError,
            executedAnyScenario: false));
    }

    /// <summary>
    /// #425's parse-failure rule already took the code off <see cref="ExitCodes.Success"/> before
    /// #480's rule is reached — a run with an unread file is #425's territory (the issue's own
    /// framing: #425 and #369 fire on a run that is VISIBLY broken), not #480's, and neither #425
    /// nor #369 prints a notice of their own either.
    /// </summary>
    [Fact]
    public void ParseFailureAlreadyChoseTheCode_DoesNotPrint()
    {
        Assert.False(ShouldPrintFor(
            PreTopologyRefusalAssurance,
            providerOrEngineFaultObserved: true,
            parsedCount: 1,
            parseFailureCount: 1,
            suiteVerdict: Verdict.Pass));
    }

    /// <summary>
    /// THE SOLO SHAPE #480 GREW OUT OF: a provider defect alone in a directory (no sibling
    /// executed) already exits 4 through #369's nothing-executed rule — <c>ComputeExitCode</c>'s
    /// own #480 comment says so explicitly ("alone in a directory it already exits 4 through that
    /// rule"), and it is the whole reason #480's rule exists: without it, adding a passing sibling
    /// turned this exact defect green. #369, not #480, is why THIS run is non-zero, so this notice
    /// — which exists to connect #480's rule specifically to the exit code — must stay silent here.
    /// A run where nothing executed is exactly the "visibly broken" shape the issue contrasts
    /// against #480's own less-visible one.
    /// </summary>
    [Fact]
    public void NothingExecutedAlreadyChoseTheCode_DoesNotPrint()
    {
        Assert.False(ShouldPrintFor(
            PreTopologyRefusalAssurance,
            providerOrEngineFaultObserved: true,
            executedAnyScenario: false));
    }

    /// <summary>
    /// MUTUAL EXCLUSION WITH <see cref="RunCommand.SecurityUnconfirmableNotice"/>, BY
    /// CONSTRUCTION. When REQ-018 is why the code is not <see cref="ExitCodes.Success"/>,
    /// <see cref="ExitCodes.FromVerdict"/> has already forced it non-Success before #480's rule is
    /// ever consulted, so this predicate answers false whatever
    /// <c>providerOrEngineFaultObserved</c> says — there is no separate exclusion check to drift
    /// from the security print's own condition.
    /// </summary>
    [Fact]
    public void SecurityNoticeAlreadyFiredForThisRun_DoesNotPrint()
    {
        var securityAssurance = SecurityAssurance.None.Refusing(
            SecurityAbortKind.SecurityDeclarationRejected);
        Assert.True(securityAssurance.Unconfirmed);

        Assert.False(ShouldPrintFor(securityAssurance, providerOrEngineFaultObserved: true));
    }

    /// <summary>
    /// The ONE refusal <see cref="RunCommand.SecurityUnconfirmableNotice"/> itself suppresses its
    /// own print for (a failed PROBE) still forces the code non-Success through the SAME
    /// <c>FromVerdict</c> OR-clause — what silences that notice is a narrower, separate condition
    /// (<c>Refusal != ProbeUnconfirmed</c>) this predicate does not share and does not need to:
    /// #480's rule genuinely never ran either way, so this run prints NEITHER notice, and that is
    /// correct rather than a second, accidental suppression.
    /// </summary>
    [Fact]
    public void SecurityNoticeSuppressedButStillReddening_DoesNotPrintEither()
    {
        var securityAssurance = SecurityAssurance.None.Refusing(SecurityAbortKind.ProbeUnconfirmed);
        Assert.True(securityAssurance.Unconfirmed);

        Assert.False(ShouldPrintFor(securityAssurance, providerOrEngineFaultObserved: true));
    }

    // ── The notice's own text ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Engine diagnostic and notice strings are ASCII-only (#379/#474), and the notice is distinct
    /// from the security one it sits beside.
    /// </summary>
    [Fact]
    public void Notice_IsNonEmptyPureAsciiAndDistinctFromTheSecurityNotice()
    {
        Assert.False(string.IsNullOrWhiteSpace(RunCommand.ProviderOrEngineFaultNotice));

        foreach (var ch in RunCommand.ProviderOrEngineFaultNotice)
        {
            Assert.True(ch <= 127, $"Non-ASCII character U+{(int)ch:X4} in the notice.");
        }

        Assert.NotEqual(
            RunCommand.SecurityUnconfirmableNotice, RunCommand.ProviderOrEngineFaultNotice);
    }

    /// <summary>
    /// The assurance a suite refused at a pre-topology authoring door carries: a recorded
    /// <see cref="SecurityAbortKind.AuthoringFault"/> over an unsecured document, so nothing is
    /// declared and nothing is confirmed — <see cref="SecurityAssurance.Unconfirmed"/> is false,
    /// exactly as <c>MixedSuiteEngineFaultExitCodeTests</c> measures for the real #480 shape, so
    /// REQ-018 plays no part in the rows above that use it.
    /// </summary>
    private static SecurityAssurance PreTopologyRefusalAssurance =>
        SecurityAssurance.None.Refusing(SecurityAbortKind.AuthoringFault);

    /// <summary>
    /// The same argument shape <c>MixedSuiteEngineFaultExitCodeTests.ExitCodeFor</c> hands
    /// <see cref="RunCommand.ComputeExitCode"/>, routed instead at
    /// <see cref="RunCommand.ShouldPrintProviderOrEngineFaultNotice"/> — the predicate this file
    /// pins. <c>failOnInconclusive</c> is fixed at <see langword="false"/> for the same reason that
    /// file gives: every shape here that exits non-zero at all does so under the default
    /// invocation, which is the whole point of #480's rule and of this notice.
    /// </summary>
    private static bool ShouldPrintFor(
        SecurityAssurance securityAssurance,
        bool providerOrEngineFaultObserved,
        int parsedCount = 2,
        int parseFailureCount = 0,
        Verdict suiteVerdict = Verdict.Inconclusive,
        bool failOnEnvironmentError = false,
        bool executedAnyScenario = true) =>
        RunCommand.ShouldPrintProviderOrEngineFaultNotice(
            parsedCount,
            parseFailureCount,
            suiteVerdict,
            failOnEnvironmentError,
            failOnInconclusive: false,
            securityAssurance,
            executedAnyScenario,
            providerOrEngineFaultObserved);
}
