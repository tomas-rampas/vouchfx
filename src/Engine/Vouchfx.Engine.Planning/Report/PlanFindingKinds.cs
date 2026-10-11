// Vouchfx.Engine.Planning.Report — PlanFindingKinds (M3 Planner, REQ-004 through REQ-010).
// Frozen v1 discriminator tokens — see PlanReportContractFreezeTests.

namespace Vouchfx.Engine.Planning.Report;

/// <summary>
/// The frozen v1 <see cref="PlanFinding.Kind"/> discriminator tokens, and the
/// <see cref="IsGap"/> predicate that decides what <c>--fail-on-gap</c> (exit code 5) counts.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsGap"/> membership is exactly the five coverage/vocabulary tokens (three coverage kinds:
/// <see cref="SuiteNeverRun"/>, <see cref="StepNeverExercised"/>, <see cref="DependencyNotAsserted"/>;
/// two vocabulary kinds: <see cref="DependencyMissingStepType"/>, <see cref="ServiceMissingHttpStep"/>).
/// <see cref="StepStale"/>, <see cref="StepFlaky"/>, <see cref="StepFragile"/>,
/// <see cref="StepInconclusiveProne"/>, and <see cref="SuiteIdentityAmbiguous"/> are
/// deliberately not gaps: <c>--fail-on-gap</c> applies to gap findings only,
/// and hand-off hints are required on every coverage or vocabulary gap finding.
/// History-health findings may omit them. A
/// <see cref="SuiteIdentityAmbiguous"/> finding likewise carries no hint — it reports an
/// identity problem, not a missing test — which is only consistent if it stays outside
/// <see cref="IsGap"/>.
/// </para>
/// </remarks>
public static class PlanFindingKinds
{
    /// <summary>A discovered suite that never appears in the analysed event history.</summary>
    public const string SuiteNeverRun = "suite-never-run";

    /// <summary>A declared step id for which no step event is attributed to it.</summary>
    public const string StepNeverExercised = "step-never-exercised";

    /// <summary>
    /// A dependency targeted by at least one declared step but by no step of an
    /// asserting family — the seam is exercised but never verified.
    /// </summary>
    public const string DependencyNotAsserted = "dependency-not-asserted";

    /// <summary>
    /// A declared dependency has a registered candidate step type that would
    /// assert/observe it, and none of its candidate types is used against it.
    /// </summary>
    public const string DependencyMissingStepType = "dependency-missing-step-type";

    /// <summary>A declared service has no <c>http.*</c> step targeting it.</summary>
    public const string ServiceMissingHttpStep = "service-missing-http-step";

    /// <summary>A step's last observed <c>step-completed</c> event is stale.</summary>
    public const string StepStale = "step-stale";

    /// <summary>A step shows both <c>Pass</c> and <c>Fail</c> across distinct runs.</summary>
    public const string StepFlaky = "step-flaky";

    /// <summary>A step shows repeated <c>EnvironmentError</c> outcomes.</summary>
    public const string StepFragile = "step-fragile";

    /// <summary>A step shows repeated <c>Inconclusive</c> outcomes.</summary>
    public const string StepInconclusiveProne = "step-inconclusive-prone";

    /// <summary>
    /// A historical run's suite identity could not be resolved to
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
    /// Returns <see langword="true"/> for exactly the coverage/vocabulary
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
