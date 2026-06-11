// Vouchfx.Cli — ExitCodes (S07-C-01).
//
// The process exit-code taxonomy for the `run` command. Kept in its own static class
// (with no Docker / topology dependency) so the mapping is directly unit-testable.
//
// TODO(S09): finalise exit-code taxonomy (§12.1). This sprint maps the four verdicts to
// the coarse "0 = ok, 1 = test failed, 2 = usage error" convention; §12.1 may later split
// EnvironmentError / Inconclusive onto their own codes so CI can distinguish infra
// breakage from a genuine product Fail.

using Platform.Engine.Abstractions;

namespace Vouchfx.Cli;

/// <summary>
/// Maps a suite-level <see cref="Verdict"/> (and usage errors) to a process exit code.
/// </summary>
internal static class ExitCodes
{
    /// <summary>Suite passed, or produced only Inconclusive results — nothing broke CI.</summary>
    public const int Success = 0;

    /// <summary>At least one scenario produced a genuine <see cref="Verdict.Fail"/>.</summary>
    public const int TestFailure = 1;

    /// <summary>A usage error (bad arguments, missing path) — the suite never ran.</summary>
    public const int UsageError = 2;

    /// <summary>
    /// Maps a suite-level <see cref="Verdict"/> to a process exit code.
    /// </summary>
    /// <param name="verdict">The aggregate suite verdict from the runner.</param>
    /// <returns>
    /// <see cref="TestFailure"/> (1) for <see cref="Verdict.Fail"/>; otherwise
    /// <see cref="Success"/> (0).  Per the verdict taxonomy (§12.1), <strong>only
    /// <see cref="Verdict.Fail"/> breaks CI by default</strong> — so
    /// <see cref="Verdict.Pass"/>, <see cref="Verdict.Inconclusive"/> and
    /// <see cref="Verdict.EnvironmentError"/> all map to 0 this sprint.
    /// </returns>
    /// <remarks>
    /// TODO(S09): finalise exit-code taxonomy (§12.1) — EnvironmentError and Inconclusive
    /// may later get distinct non-zero codes so CI can tell infra breakage from a defect.
    /// </remarks>
    public static int FromVerdict(Verdict verdict) => verdict switch
    {
        Verdict.Fail => TestFailure,
        _ => Success,
    };
}
