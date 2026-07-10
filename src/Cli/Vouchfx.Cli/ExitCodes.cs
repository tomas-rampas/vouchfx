// Vouchfx.Cli — ExitCodes (S07-C-01; taxonomy finalised S09-C-03).
//
// The process exit-code taxonomy for the `run` command. Kept in its own static class
// (with no Docker / topology dependency) so the mapping is directly unit-testable.

using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Cli;

/// <summary>
/// Maps a suite-level <see cref="Verdict"/> (and usage errors) to a process exit code (§12.1).
/// </summary>
/// <remarks>
/// The four-outcome verdict taxonomy is kept separate all the way to the exit code so CI can
/// tell <strong>infrastructure breakage</strong> apart from a <strong>timeout</strong> apart
/// from a <strong>genuine defect</strong>:
/// <list type="bullet">
///   <item><description>
///     <see cref="Verdict.Fail"/> → <see cref="TestFailure"/> (1) — <strong>always</strong>.
///     Only <see cref="Verdict.Fail"/> breaks CI by default.
///   </description></item>
///   <item><description>
///     <see cref="Verdict.EnvironmentError"/> → <see cref="Success"/> (0) by default, or
///     <see cref="EnvironmentError"/> (3) only when the caller opts in via
///     <c>--fail-on-env-error</c>.
///   </description></item>
///   <item><description>
///     <see cref="Verdict.Inconclusive"/> → <see cref="Success"/> (0) by default, or
///     <see cref="Inconclusive"/> (4) only when the caller opts in via
///     <c>--fail-on-inconclusive</c>.
///   </description></item>
///   <item><description>
///     <see cref="Verdict.Pass"/> → <see cref="Success"/> (0).
///   </description></item>
/// </list>
/// The distinct codes 3 and 4 sit deliberately <em>above</em> <see cref="UsageError"/> (2,
/// reserved for System.CommandLine parse errors) so there is no collision: 0 = ok, 1 = a
/// product Fail, 2 = a usage error, 3 = infra broke, 4 = the engine could not decide.
/// </remarks>
internal static class ExitCodes
{
    /// <summary>
    /// Nothing broke CI: the suite passed, or produced only EnvironmentError / Inconclusive
    /// results that were not opted in to gate CI.
    /// </summary>
    public const int Success = 0;

    /// <summary>At least one scenario produced a genuine <see cref="Verdict.Fail"/>.</summary>
    public const int TestFailure = 1;

    /// <summary>A usage error (bad arguments, missing path) — the suite never ran.</summary>
    public const int UsageError = 2;

    /// <summary>
    /// The aggregate verdict was <see cref="Verdict.EnvironmentError"/> and the caller opted in
    /// to gate CI on it via <c>--fail-on-env-error</c> (infrastructure broke; the system under
    /// test was never exercised).
    /// </summary>
    public const int EnvironmentError = 3;

    /// <summary>
    /// The aggregate verdict was <see cref="Verdict.Inconclusive"/> and the caller opted in to
    /// gate CI on it via <c>--fail-on-inconclusive</c> (timeout / partition outlasted grace /
    /// upstream capture unmet — the engine could not determine correctness).
    /// </summary>
    public const int Inconclusive = 4;

    /// <summary>
    /// Maps a suite-level <see cref="Verdict"/> to a process exit code, honouring the two opt-in
    /// CI-gating flags.
    /// </summary>
    /// <param name="verdict">The aggregate suite verdict from the runner.</param>
    /// <param name="failOnEnvironmentError">
    /// When <see langword="true"/>, an <see cref="Verdict.EnvironmentError"/> verdict exits with
    /// <see cref="EnvironmentError"/> (3) instead of <see cref="Success"/> (0).
    /// </param>
    /// <param name="failOnInconclusive">
    /// When <see langword="true"/>, an <see cref="Verdict.Inconclusive"/> verdict exits with
    /// <see cref="Inconclusive"/> (4) instead of <see cref="Success"/> (0).
    /// </param>
    /// <returns>
    /// <see cref="TestFailure"/> (1) for <see cref="Verdict.Fail"/> — always; the opt-in code
    /// (<see cref="EnvironmentError"/> / <see cref="Inconclusive"/>) for the matching verdict
    /// when its flag is set; otherwise <see cref="Success"/> (0).  Per the verdict taxonomy
    /// (§12.1), <strong>only <see cref="Verdict.Fail"/> breaks CI by default</strong> — so
    /// <see cref="Verdict.Pass"/>, <see cref="Verdict.EnvironmentError"/> and
    /// <see cref="Verdict.Inconclusive"/> all map to 0 unless the caller opts in.
    /// </returns>
    public static int FromVerdict(
        Verdict verdict,
        bool failOnEnvironmentError,
        bool failOnInconclusive) => verdict switch
        {
            Verdict.Fail => TestFailure,
            Verdict.EnvironmentError => failOnEnvironmentError ? EnvironmentError : Success,
            Verdict.Inconclusive => failOnInconclusive ? Inconclusive : Success,
            _ => Success,
        };
}
