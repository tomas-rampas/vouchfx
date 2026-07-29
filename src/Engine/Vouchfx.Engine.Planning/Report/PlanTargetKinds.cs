// Vouchfx.Engine.Planning.Report — PlanTargetKinds (M3 Planner, REQ-004, fix-round B2).
// Frozen v1 discriminator tokens for PlanFinding.TargetKind — see PlanReportContractFreezeTests.
//
// Both CoverageGapAnalyser (T2) and VocabularyGapAnalyser (T3) independently classify a
// step's target as a service or a dependency and must stamp the IDENTICAL wire token —
// two independent string literals ("Dependency" vs "dependency", etc.) would each compile,
// pass their own author's tests, and silently break every cross-repo consumer of the frozen
// wire shape. Centralising the tokens here (and StepTargetResolver.TryClassifyTarget's shared
// classification logic) removes that drift risk structurally.

namespace Vouchfx.Engine.Planning.Report;

/// <summary>
/// The frozen v1 <see cref="PlanFinding.TargetKind"/> discriminator tokens.
/// </summary>
public static class PlanTargetKinds
{
    /// <summary>The finding's <see cref="PlanFinding.Target"/> names a declared <c>environment.services</c> entry.</summary>
    public const string Service = "service";

    /// <summary>The finding's <see cref="PlanFinding.Target"/> names a declared <c>environment.dependencies</c> entry.</summary>
    public const string Dependency = "dependency";

    /// <summary>Every frozen v1 target-kind token, ordinal-sorted.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Dependency, Service };
}
