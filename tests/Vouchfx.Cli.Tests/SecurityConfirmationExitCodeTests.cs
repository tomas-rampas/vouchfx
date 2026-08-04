// REQ-018 — a security-confirmation failure exits non-zero unconditionally
// (authenticated-infrastructure-mtls, slice E).
//
// The requirement is a CARVE-OUT from "only Fail breaks CI by default", and the whole engineering
// risk is that a carve-out widens. So the tests below are written in two halves and both matter
// equally:
//
//   • the carve-out WORKS   — a security-confirmation failure exits non-zero with no flag;
//   • the carve-out is NARROW — every other cause of the same verdict is untouched, which is what
//     CliLogicTests.FromVerdict_MapsPerTaxonomy already pins and which this file re-states as an
//     explicit anti-regression rather than leaving implied.
using Vouchfx.Cli;
using Vouchfx.Engine.Abstractions;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// REQ-018 tests: the one non-<see cref="Verdict.Fail"/> outcome that breaks CI without an opt-in.
/// </summary>
public sealed class SecurityConfirmationExitCodeTests
{
    // ── The carve-out works ───────────────────────────────────────────────────────────────

    /// <summary>
    /// REQ-018(a) at the mapping level: an <see cref="Verdict.EnvironmentError"/> caused by an
    /// unconfirmable security assertion exits <see cref="ExitCodes.EnvironmentError"/> (3) with
    /// NO <c>--fail-on-env-error</c> — the shape EDGE-004 (a plaintext port behind the declared
    /// endpoint) and EDGE-005 (a broker that came up with no SSL listener) both produce.
    /// </summary>
    [Fact]
    public void FromVerdict_SecurityConfirmationFailure_ExitsNonZeroWithoutTheFlag()
    {
        Assert.Equal(
            ExitCodes.EnvironmentError,
            ExitCodes.FromVerdict(
                Verdict.EnvironmentError,
                failOnEnvironmentError: false,
                failOnInconclusive: false,
                securityConfirmationFailed: true));
    }

    /// <summary>
    /// EDGE-010(a): a security PREFLIGHT rejection — the declared <c>clientCert</c> file deleted,
    /// a path escaping the suite directory, a <c>(profile, kind)</c> pair with no wiring — aborts
    /// before any container starts and is <see cref="Verdict.Inconclusive"/>, not
    /// EnvironmentError. It must still exit non-zero, and with the code its own verdict names.
    /// </summary>
    [Fact]
    public void FromVerdict_SecurityPreflightRejection_ExitsInconclusiveWithoutTheFlag()
    {
        Assert.Equal(
            ExitCodes.Inconclusive,
            ExitCodes.FromVerdict(
                Verdict.Inconclusive,
                failOnEnvironmentError: false,
                failOnInconclusive: false,
                securityConfirmationFailed: true));
    }

    /// <summary>
    /// The flag never DOWNGRADES an outcome: a genuine <see cref="Verdict.Fail"/> still exits
    /// <see cref="ExitCodes.TestFailure"/> (1) whatever the security signal says.
    /// </summary>
    [Fact]
    public void FromVerdict_FailStaysTestFailure_EvenWithTheSecuritySignal()
    {
        Assert.Equal(
            ExitCodes.TestFailure,
            ExitCodes.FromVerdict(
                Verdict.Fail,
                failOnEnvironmentError: false,
                failOnInconclusive: false,
                securityConfirmationFailed: true));
    }

    /// <summary>
    /// The unreachable arm, pinned as FAIL-CLOSED rather than left to chance. Verdict precedence
    /// (<c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c>) means a scenario carrying a
    /// security failure always elevates the aggregate above <see cref="Verdict.Pass"/>, so this
    /// combination cannot arise today. A combination that cannot occur is exactly the one a later
    /// refactor makes occur, and reporting 0 for it would be the false assurance the whole
    /// requirement exists to destroy.
    /// </summary>
    [Fact]
    public void FromVerdict_PassWithASecurityFailure_StillExitsNonZero()
    {
        Assert.NotEqual(
            ExitCodes.Success,
            ExitCodes.FromVerdict(
                Verdict.Pass,
                failOnEnvironmentError: false,
                failOnInconclusive: false,
                securityConfirmationFailed: true));
    }

    // ── The carve-out is narrow ───────────────────────────────────────────────────────────

    /// <summary>
    /// REQ-018(b): every OTHER cause of an environment error — an unhealthy container, an image
    /// that cannot be pulled, a seed failure unrelated to security — still exits
    /// <see cref="ExitCodes.Success"/> by default. This is the proof the carve-out did not widen
    /// into "environment errors now break CI".
    /// </summary>
    [Theory]
    [InlineData(Verdict.EnvironmentError, ExitCodes.Success)]
    [InlineData(Verdict.Inconclusive, ExitCodes.Success)]
    [InlineData(Verdict.Pass, ExitCodes.Success)]
    [InlineData(Verdict.Fail, ExitCodes.TestFailure)]
    public void FromVerdict_WithoutTheSecuritySignal_IsUnchanged(Verdict verdict, int expected)
    {
        Assert.Equal(
            expected,
            ExitCodes.FromVerdict(
                verdict, failOnEnvironmentError: false, failOnInconclusive: false));

        // …and identically when the parameter is passed explicitly false, so the DEFAULT and the
        // explicit value cannot drift apart.
        Assert.Equal(
            expected,
            ExitCodes.FromVerdict(
                verdict,
                failOnEnvironmentError: false,
                failOnInconclusive: false,
                securityConfirmationFailed: false));
    }

    // ── Threaded through the run command's own decision ───────────────────────────────────

    /// <summary>
    /// The signal survives <c>RunCommand.ComputeExitCode</c>, which is the function the process
    /// actually returns from — a carve-out implemented only in <see cref="ExitCodes"/> and dropped
    /// one layer up would pass every test above and still exit 0.
    /// </summary>
    [Fact]
    public void ComputeExitCode_SecurityConfirmationFailure_ExitsNonZeroWithoutTheFlag()
    {
        var code = RunCommand.ComputeExitCode(
            parsedCount: 1,
            parseFailureCount: 0,
            suiteVerdict: Verdict.EnvironmentError,
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            securityConfirmationFailed: true);

        Assert.Equal(ExitCodes.EnvironmentError, code);
    }

    /// <summary>
    /// The same invocation for an ORDINARY environment error still exits 0 — REQ-018(b) at the
    /// command level.
    /// </summary>
    [Fact]
    public void ComputeExitCode_OrdinaryEnvironmentError_StillExitsZeroWithoutTheFlag()
    {
        var code = RunCommand.ComputeExitCode(
            parsedCount: 1,
            parseFailureCount: 0,
            suiteVerdict: Verdict.EnvironmentError,
            failOnEnvironmentError: false,
            failOnInconclusive: false);

        Assert.Equal(ExitCodes.Success, code);
    }

    /// <summary>
    /// The runner's own signal defaults to <see langword="false"/>, so every existing
    /// <c>SuiteResult</c> construction — and every embedding caller — keeps its current exit code.
    /// </summary>
    [Fact]
    public void SuiteResult_DefaultsTheSecuritySignalToFalse()
    {
        var result = new Vouchfx.Engine.Runtime.SuiteResult(
            Verdict.EnvironmentError, Array.Empty<(string, Verdict)>());

        Assert.False(result.SecurityConfirmationFailed);
    }
}
