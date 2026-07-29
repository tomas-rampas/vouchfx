// Vouchfx.Engine.Planning.Report — PlanAmbiguityReasons (M3 Planner, REQ-008, EDGE-002,
// fix-round B2). Frozen v1 discriminator tokens for PlanFinding.AmbiguityReason — see
// PlanReportContractFreezeTests.
//
// CoverageGapAnalyser (T2) is the sole emitter of these tokens today, but centralising them
// here (rather than as string literals in T2's file) means the golden freeze test, T2's own
// tests, and any future consumer all read from one place. Before this fix,
// "history-references-missing-file" appeared in NO golden anywhere, so a typo in it could not
// fail any CI gate — BuildCanonicalFixture() now includes a finding carrying this token too.

namespace Vouchfx.Engine.Planning.Report;

/// <summary>
/// The frozen v1 <see cref="PlanFinding.AmbiguityReason"/> discriminator tokens (REQ-008
/// rules 1 and 2).
/// </summary>
public static class PlanAmbiguityReasons
{
    /// <summary>REQ-008 rule 1: two or more declared suites derive the same scenario id.</summary>
    public const string ScenarioIdCollision = "scenario-id-collision";

    /// <summary>REQ-008 rule 2: history references a <c>file</c> that no longer exists on disk (the rename case).</summary>
    public const string HistoryReferencesMissingFile = "history-references-missing-file";

    /// <summary>Every frozen v1 ambiguity-reason token, ordinal-sorted.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { HistoryReferencesMissingFile, ScenarioIdCollision };
}
