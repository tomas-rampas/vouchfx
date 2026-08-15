// Vouchfx.Cli — ExitCodes (S07-C-01; taxonomy finalised S09-C-03).
//
// The process exit-code taxonomy for the `run` command. Kept in its own static class
// (with no Docker / topology dependency) so the mapping is directly unit-testable.

using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Runtime;

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
    /// <param name="securityAssurance">
    /// REQ-018's narrow carve-out, supplied as EVIDENCE rather than as a conclusion: what the suite
    /// declared, what REQ-005's probe confirmed, and which door refused. <see langword="null"/>
    /// (the default) means the caller never asked about security at all.
    /// <para>
    /// <strong>The POLICY is here; the PREDICATE is on the record.</strong> This file decides which
    /// exit code an unconfirmed assurance produces for a given verdict.
    /// <c>SecurityAssurance.Unconfirmed</c> decides whether the assurance is unconfirmed, and it is
    /// the only place that decision is written — the parallel runner's per-scenario fold reads the
    /// same member, and a second spelling here is precisely how the two would drift.
    /// </para>
    /// <para>
    /// This parameter used to be a <c>bool</c> whose meaning was an OPEN, hand-maintained list of
    /// the doors that set it, kept in <c>ScenarioRunner</c> and pointed at from here. There is no
    /// list any more, so there is nothing here to fall out of step: a door records only which door
    /// it was, and the suite's one walk of <c>SecuredTargets.Enumerate</c> supplies the rest.
    /// </para>
    /// Defaulted to <see langword="null"/> so every
    /// existing call site and every existing parameterised test case keeps compiling and keeps
    /// its current result — that default is what makes the carve-out provably narrow.
    /// </param>
    /// <returns>
    /// <see cref="TestFailure"/> (1) for <see cref="Verdict.Fail"/> — always; the opt-in code
    /// (<see cref="EnvironmentError"/> / <see cref="Inconclusive"/>) for the matching verdict
    /// when its flag is set OR when <paramref name="securityAssurance"/> is unconfirmed;
    /// otherwise <see cref="Success"/> (0).  Per the verdict taxonomy (§12.1),
    /// <strong>only <see cref="Verdict.Fail"/> breaks CI by default</strong> — so
    /// <see cref="Verdict.Pass"/>, <see cref="Verdict.EnvironmentError"/> and
    /// <see cref="Verdict.Inconclusive"/> all map to 0 unless the caller opts in, or unless the
    /// cause was an unconfirmable security assertion (REQ-018; see this class's own remarks).
    /// </returns>
    /// <remarks>
    /// <para>
    /// An unconfirmed <paramref name="securityAssurance"/> can arrive with THREE verdicts, and
    /// forces the verdict's OWN opt-in code rather than substituting a single fixed one on the two
    /// that have one, so the code a pipeline reads still identifies the outcome: a failed probe
    /// aborts the topology and aggregates to <see cref="Verdict.EnvironmentError"/> → 3, while a
    /// pre-topology refusal is an authoring error that aggregates to
    /// <see cref="Verdict.Inconclusive"/> → 4. Both are non-zero, which is the whole of what
    /// REQ-018 requires.
    /// </para>
    /// <para>
    /// <see cref="Verdict.Fail"/> is the third, and the assurance is IGNORED there because Fail is
    /// already unconditionally non-zero. It is reachable rather than theoretical: a mixed suite
    /// whose one scenario carries a schema error inside its <c>security</c> block records a refusal
    /// (<c>ScenarioRunner.RunSuiteAsync</c>'s <c>RejectsASecurityDeclaration</c>) and folds in as
    /// <see cref="Verdict.Inconclusive"/>, while a runnable sibling whose step fails folds in as
    /// <see cref="Verdict.Fail"/> — and <c>Elevate</c> ranks Fail (precedence rank 2) above
    /// Inconclusive (precedence rank 1), so the suite returns Fail carrying an unconfirmed
    /// assurance. Those
    /// ranks are <c>VerdictPrecedence</c>'s ordering only: they are neither the enum's values nor
    /// the exit codes named elsewhere in this file. REQ-018 is satisfied by the Fail arm's own
    /// code; nothing is lost by not consulting the assurance.
    /// </para>
    /// <para>
    /// The DISCARD arm (<c>_ =&gt;</c>) is the one place it DOES substitute a fixed code —
    /// <see cref="EnvironmentError"/> (3) — and it is the exception that proves the own-code rule
    /// rather than a contradiction of it, because nothing it covers is reachable with the flag set.
    /// It covers <see cref="Verdict.Pass"/> and, deliberately, anything else that is not a declared
    /// enum member (an out-of-range cast, or a member a later release adds) — and that breadth is
    /// the point rather than an accident, since a fail-closed default is worth exactly as much as
    /// the set of unforeseen inputs it catches. For Pass the unreachability is structural: the
    /// precedence <c>EnvironmentError &gt; Fail &gt; Inconclusive &gt; Pass</c> means any scenario
    /// carrying a security failure elevates the aggregate above Pass, and Pass has no opt-in code
    /// to prefer in any case. It is nonetheless written to fail CLOSED rather than to fall through
    /// to <see cref="Success"/>. A combination that cannot occur is exactly the one a later
    /// refactor makes occur, and reporting 0 for it would be the false assurance this whole
    /// requirement exists to destroy.
    /// </para>
    /// </remarks>
    public static int FromVerdict(
        Verdict verdict,
        bool failOnEnvironmentError,
        bool failOnInconclusive,
        SecurityAssurance? securityAssurance = null)
    {
        // REQ-018's carve-out, read ONCE from the assurance's own evidence rather than decided at
        // any of the nine doors that used to decide it for themselves. `Unconfirmed` is
        // `(an authoring refusal ∧ some declared target went unconfirmed) ∨ a failed probe
        // ∨ a refusal located in the declaration itself` — see SecurityAssurance.
        var securityUnconfirmed = securityAssurance?.Unconfirmed == true;

        return verdict switch
        {
            Verdict.Fail => TestFailure,
            Verdict.EnvironmentError =>
                failOnEnvironmentError || securityUnconfirmed ? EnvironmentError : Success,
            Verdict.Inconclusive =>
                failOnInconclusive || securityUnconfirmed ? Inconclusive : Success,
            _ => securityUnconfirmed ? EnvironmentError : Success,
        };
    }
}
