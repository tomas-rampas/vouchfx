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
///     <c>--fail-on-env-error</c> — with ONE narrow exception, below.
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
/// <para>
/// <strong>The one exception to "only <see cref="Verdict.Fail"/> breaks CI by default"
/// (authenticated-infrastructure-mtls, REQ-018).</strong> A suite that declares a
/// <c>security</c> block the engine cannot confirm — REQ-005's post-health-gate probe fails,
/// or a security preflight rejects the declaration before any container starts — exits non-zero
/// WITHOUT <c>--fail-on-env-error</c>. Every OTHER cause of
/// <see cref="Verdict.EnvironmentError"/> (an unhealthy container, an image that cannot be
/// pulled, a seed failure unrelated to security) is unaffected and still exits
/// <see cref="Success"/> by default. The rationale: an unconfirmable security assertion is not
/// an infrastructure flake the way a failed image pull is — it is an assertion the author
/// explicitly wrote into the suite, and treating it as opt-in-only would let a team that forgot
/// the flag get a green pipeline on a security suite that verified nothing.
/// </para>
/// <para>
/// The carve-out is deliberately narrow in mechanism as well as in scope: it is a separate,
/// defaulted parameter on <see cref="FromVerdict"/>, and it changes only WHETHER the verdict's
/// own opt-in code is returned — never WHICH code, and never what
/// <see cref="Verdict.EnvironmentError"/> means. A security-confirmation failure still reports
/// <see cref="EnvironmentError"/> (3), so a pipeline keying on the taxonomy reads the same
/// outcome it always did.
/// </para>
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
    /// The Planner's <c>plan --fail-on-gap</c> opt-in code (M3 / planner-coverage-and-gap-report,
    /// REQ-010): at least one coverage or vocabulary gap finding
    /// (<c>PlanFindingKinds.IsGap</c>) was present in the report AND the caller opted in via
    /// <c>--fail-on-gap</c>.
    /// </summary>
    /// <remarks>
    /// Sits deliberately ABOVE the existing 0–4 taxonomy so it cannot collide with any of
    /// them: 0 = ok, 1 = a product Fail, 2 = a usage error, 3 = infra/catalogue broke, 4 = the
    /// engine could not decide, 5 = the Planner found at least one gap AND the caller asked to
    /// be told via a non-zero exit. A <c>plan</c> invocation exits 0 on a successful analysis
    /// regardless of how many gaps were found unless <c>--fail-on-gap</c> is passed — gaps are
    /// data, mirroring the verdict taxonomy's "only a genuine Fail breaks CI by default" rule
    /// (§12.1). This code is reserved exclusively for that opt-in path; no other command uses it.
    /// </remarks>
    public const int GapsFound = 5;

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
    /// <param name="securityConfirmationFailed">
    /// REQ-018's narrow carve-out. <see langword="true"/> when the run aborted because a declared
    /// <c>security</c> block could not be confirmed — REQ-005's probe failed; a security
    /// preflight (REQ-003/REQ-004 certificate and artefact paths, REQ-016 artefact
    /// <c>target</c> shape, REQ-022 profile wiring) rejected the declaration before any container
    /// started; the root schema rejected it earlier still, which is where REQ-021's per-kind
    /// narrowing of <c>profile</c> lives and which covers any schema error located at or inside a
    /// declared <c>security</c> block, not only the causes listed here; or a secured
    /// multi-scenario suite was refused because its scenarios resolve their declared security
    /// paths against different directories, which likewise starts no container.
    /// <c>ScenarioRunner.SuiteResult.SecurityConfirmationFailed</c> carries the maintained
    /// enumeration; this is a summary of it. Defaulted to <see langword="false"/> so every
    /// existing call site and every existing parameterised test case keeps compiling and keeps
    /// its current result — that default is what makes the carve-out provably narrow.
    /// </param>
    /// <returns>
    /// <see cref="TestFailure"/> (1) for <see cref="Verdict.Fail"/> — always; the opt-in code
    /// (<see cref="EnvironmentError"/> / <see cref="Inconclusive"/>) for the matching verdict
    /// when its flag is set OR when <paramref name="securityConfirmationFailed"/> is set;
    /// otherwise <see cref="Success"/> (0).  Per the verdict taxonomy (§12.1),
    /// <strong>only <see cref="Verdict.Fail"/> breaks CI by default</strong> — so
    /// <see cref="Verdict.Pass"/>, <see cref="Verdict.EnvironmentError"/> and
    /// <see cref="Verdict.Inconclusive"/> all map to 0 unless the caller opts in, or unless the
    /// cause was an unconfirmable security assertion (REQ-018; see this class's own remarks).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <paramref name="securityConfirmationFailed"/> forces the verdict's OWN opt-in code rather
    /// than substituting a single fixed one, so the code a pipeline reads still identifies the
    /// outcome: a failed probe aborts the topology and aggregates to
    /// <see cref="Verdict.EnvironmentError"/> → 3, while a pre-topology security preflight
    /// rejection is an authoring error that aggregates to <see cref="Verdict.Inconclusive"/> → 4.
    /// Both are non-zero, which is the whole of what REQ-018 requires.
    /// </para>
    /// <para>
    /// The <see cref="Verdict.Pass"/> arm is unreachable with the flag set — the verdict
    /// precedence <c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c> means any
    /// scenario carrying a security failure elevates the aggregate above Pass — and it is
    /// nonetheless written to fail CLOSED rather than to fall through to
    /// <see cref="Success"/>. A combination that cannot occur is exactly the one a later refactor
    /// makes occur, and reporting 0 for it would be the false assurance this whole requirement
    /// exists to destroy.
    /// </para>
    /// </remarks>
    public static int FromVerdict(
        Verdict verdict,
        bool failOnEnvironmentError,
        bool failOnInconclusive,
        bool securityConfirmationFailed = false) => verdict switch
        {
            Verdict.Fail => TestFailure,
            Verdict.EnvironmentError =>
                failOnEnvironmentError || securityConfirmationFailed ? EnvironmentError : Success,
            Verdict.Inconclusive =>
                failOnInconclusive || securityConfirmationFailed ? Inconclusive : Success,
            _ => securityConfirmationFailed ? EnvironmentError : Success,
        };
}
