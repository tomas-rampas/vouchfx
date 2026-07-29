// Vouchfx.Engine.Planning.Report — PlanFindingKinds (M3 Planner, REQ-004 through REQ-010).
// Frozen v1 discriminator tokens — see PlanReportContractFreezeTests.

namespace Vouchfx.Engine.Planning.Report;

/// <summary>
/// The frozen v1 <see cref="PlanFinding.Kind"/> discriminator tokens, and the
/// <see cref="IsGap"/> predicate that decides what <c>--fail-on-gap</c> (exit code 5) counts.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsGap"/> membership is exactly the five REQ-004 + REQ-005 tokens (three REQ-004:
/// <see cref="SuiteNeverRun"/>, <see cref="StepNeverExercised"/>, <see cref="DependencyNotAsserted"/>;
/// two REQ-005: <see cref="DependencyMissingStepType"/>, <see cref="ServiceMissingHttpStep"/>).
/// <see cref="StepStale"/>, <see cref="StepFlaky"/>, <see cref="StepFragile"/>,
/// <see cref="StepInconclusiveProne"/>, and <see cref="SuiteIdentityAmbiguous"/> are
/// deliberately NOT gaps: REQ-010 scopes <c>--fail-on-gap</c> to "at least one gap
/// finding", and REQ-007 requires hand-off hints on "every gap finding (REQ-004 and REQ-005
/// kinds)" while explicitly permitting history-health findings to omit them. A
/// <see cref="SuiteIdentityAmbiguous"/> finding likewise carries no hint — it reports an
/// identity problem, not a missing test — which is only consistent if it stays outside
/// <see cref="IsGap"/>.
/// </para>
/// </remarks>
public static class PlanFindingKinds
{
    /// <summary>REQ-004: a discovered suite that never appears in the analysed event history.</summary>
    public const string SuiteNeverRun = "suite-never-run";

    /// <summary>REQ-004: a declared step id for which no step event is attributed to it.</summary>
    public const string StepNeverExercised = "step-never-exercised";

    /// <summary>
    /// REQ-004: a dependency targeted by at least one declared step but by no step of an
    /// asserting family — the seam is exercised but never verified.
    /// </summary>
    public const string DependencyNotAsserted = "dependency-not-asserted";

    /// <summary>
    /// REQ-005: a declared dependency has a registered candidate step type that would
    /// assert/observe it, and none of its candidate types is used against it.
    /// </summary>
    public const string DependencyMissingStepType = "dependency-missing-step-type";

    /// <summary>REQ-005: a declared service has no <c>http.*</c> step targeting it.</summary>
    public const string ServiceMissingHttpStep = "service-missing-http-step";

    /// <summary>REQ-006: a step's last observed <c>step-completed</c> event is stale.</summary>
    public const string StepStale = "step-stale";

    /// <summary>REQ-006: a step shows both <c>Pass</c> and <c>Fail</c> across distinct runs.</summary>
    public const string StepFlaky = "step-flaky";

    /// <summary>REQ-006: a step shows repeated <c>EnvironmentError</c> outcomes.</summary>
    public const string StepFragile = "step-fragile";

    /// <summary>REQ-006: a step shows repeated <c>Inconclusive</c> outcomes.</summary>
    public const string StepInconclusiveProne = "step-inconclusive-prone";

    /// <summary>
    /// REQ-008 / EDGE-002: a historical run's suite identity could not be resolved to
    /// exactly one currently-declared suite (a scenario-id collision or a since-renamed
    /// file).
    /// </summary>
    public const string SuiteIdentityAmbiguous = "suite-identity-ambiguous";

    /// <summary>Every frozen v1 finding-kind token, ordinal-sorted.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        DependencyMissingStepType,
        DependencyNotAsserted,
        ServiceMissingHttpStep,
        StepFlaky,
        StepFragile,
        StepInconclusiveProne,
        StepNeverExercised,
        StepStale,
        SuiteIdentityAmbiguous,
        SuiteNeverRun,
    }.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Returns <see langword="true"/> for exactly the REQ-004 + REQ-005 coverage/vocabulary
    /// gap kinds — the set <c>--fail-on-gap</c> (exit code 5) counts. History-health and
    /// suite-identity-ambiguity kinds always return <see langword="false"/>.
    /// </summary>
    /// <param name="kind">A <see cref="PlanFinding.Kind"/> token.</param>
    public static bool IsGap(string kind) => kind switch
    {
        SuiteNeverRun => true,
        StepNeverExercised => true,
        DependencyNotAsserted => true,
        DependencyMissingStepType => true,
        ServiceMissingHttpStep => true,
        _ => false,
    };
}
