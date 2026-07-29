// Vouchfx.Engine.Planning.Tests — PlanDeterminismTests (M3 Planner, EDGE-005, T6 scope).
//
// EDGE-005 requires two consecutive analyses of the same inputs to produce byte-identical
// JSON, and pins the sort key order to kind -> target -> suite -> stepId -> ambiguityReason
// -> detail (PlanPipeline.Sort's six-key total order; see that file's own remarks for why a
// four-key sort silently "worked" only by accident of RunCorrelator already emitting its
// collision list ordinal-sorted by ScenarioId plus LINQ's stable sort).
//
// Fixture (Fixtures/acceptance/determinism/two-collisions): FOUR declared suites forming TWO
// INDEPENDENT scenario-id collisions ("collision-alpha" x2, "collision-beta" x2). Each
// collision yields one suite-identity-ambiguous finding with Kind/Target/Suite/StepId/
// AmbiguityReason identical across BOTH findings (Kind="suite-identity-ambiguous",
// Target=null, Suite=null, StepId=null, AmbiguityReason="scenario-id-collision") — the exact
// five-key tie the sort's own remarks describe. Only the sixth key (Detail, which embeds the
// distinct scenario id) distinguishes them. A regression that dropped back to a four- or
// five-key sort would therefore be SILENT for every fixture already in this test suite (none
// of them has two collisions) but would surface here as either a flaky/undefined relative
// order between the two ambiguity findings, or (worse) an order that happens to differ from
// the independently-recomputed ordinal sort below.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class PlanDeterminismTests
{
    private static string SuitePath() =>
        PlannerTestFixtures.FixtureRoot("acceptance/determinism/two-collisions");

    // ── EDGE-005: two consecutive analyses of the same inputs are byte-identical ────────

    [Fact]
    public void TwoConsecutiveAnalyses_OfTheSameInputs_ProduceByteIdenticalJson()
    {
        var suitePath = SuitePath();

        var first = PlannerTestFixtures.Plan(suitePath);
        var second = PlannerTestFixtures.Plan(suitePath);

        Assert.Equal(PlanExport.SerializePlan(first), PlanExport.SerializePlan(second));
    }

    // ── REQ-006 / EDGE-005 at the whole-document level: "now" is history-derived, so the
    // WHOLE document — not just history-health findings — must be wall-clock independent. ──

    [Fact]
    public async Task TwoConsecutiveAnalyses_AtDifferentWallClockTimes_ProduceByteIdenticalJson()
    {
        var suitePath = SuitePath();

        var first = PlannerTestFixtures.Plan(suitePath);
        await Task.Delay(TimeSpan.FromSeconds(1.1));
        var second = PlannerTestFixtures.Plan(suitePath);

        Assert.Equal(PlanExport.SerializePlan(first), PlanExport.SerializePlan(second));
    }

    // ── The two independent scenario-id collisions are both present, distinguished only by
    // Detail (the sixth sort key) ───────────────────────────────────────────────────────

    [Fact]
    public void TwoIndependentScenarioIdCollisions_BothProduceAnAmbiguityFinding()
    {
        var report = PlannerTestFixtures.Plan(SuitePath());

        var ambiguities = report.Findings
            .Where(f => f.Kind == PlanFindingKinds.SuiteIdentityAmbiguous)
            .ToList();

        Assert.Equal(2, ambiguities.Count);
        Assert.Contains(ambiguities, f => f.Detail.Contains("collision-alpha", StringComparison.Ordinal));
        Assert.Contains(ambiguities, f => f.Detail.Contains("collision-beta", StringComparison.Ordinal));

        // Every one of the first five sort keys ties between the two findings — proving the
        // sixth key (Detail) is load-bearing, not decorative, for THIS finding shape.
        Assert.All(ambiguities, f => Assert.Equal(PlanFindingKinds.SuiteIdentityAmbiguous, f.Kind));
        Assert.All(ambiguities, f => Assert.Null(f.Target));
        Assert.All(ambiguities, f => Assert.Null(f.Suite));
        Assert.All(ambiguities, f => Assert.Null(f.StepId));
        Assert.All(ambiguities, f => Assert.Equal(PlanAmbiguityReasons.ScenarioIdCollision, f.AmbiguityReason));
    }

    // ── EDGE-005 acceptance: assert the DOCUMENTED ordering (kind -> target -> suite ->
    // stepId -> ambiguityReason -> detail, all ordinal) by independently recomputing it from
    // the finding set the report itself exposes, and asserting the report's actual order
    // equals that recomputation — never merely trusting PlanPipeline's own internal sort. ──

    [Fact]
    public void Findings_AreOrderedByTheDocumentedSixKeyOrdinalRule()
    {
        var report = PlannerTestFixtures.Plan(SuitePath());

        Assert.True(report.Findings.Count > 1, "Fixture must produce more than one finding to make ordering meaningful.");

        var expected = report.Findings
            .OrderBy(f => f.Kind, StringComparer.Ordinal)
            .ThenBy(f => f.Target, NullsLastOrdinal.Instance)
            .ThenBy(f => f.Suite, NullsLastOrdinal.Instance)
            .ThenBy(f => f.StepId, NullsLastOrdinal.Instance)
            .ThenBy(f => f.AmbiguityReason, NullsLastOrdinal.Instance)
            .ThenBy(f => f.Detail, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, report.Findings);

        // Concretely: the "collision-alpha" ambiguity finding precedes the "collision-beta"
        // one (ordinal: '1' < '2', matching "collision-alpha" < "collision-beta" as text) —
        // pinning a specific, human-checkable consequence of the general assertion above.
        var alphaIndex = IndexOfDetailContaining(report, "collision-alpha");
        var betaIndex = IndexOfDetailContaining(report, "collision-beta");
        Assert.True(alphaIndex < betaIndex, "The 'collision-alpha' ambiguity finding must sort before 'collision-beta' (ordinal Detail comparison).");
    }

    private static int IndexOfDetailContaining(PlanReportDocument report, string substring)
    {
        for (var i = 0; i < report.Findings.Count; i++)
        {
            if (report.Findings[i].Kind == PlanFindingKinds.SuiteIdentityAmbiguous
                && report.Findings[i].Detail.Contains(substring, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"No suite-identity-ambiguous finding with Detail containing '{substring}'.");
    }

    /// <summary>
    /// A fresh, independently-written <see cref="StringComparer.Ordinal"/>-with-nulls-last
    /// comparer — deliberately NOT a reference to <c>PlanPipeline</c>'s own private nested
    /// comparer of the same shape, so this test verifies the report's ACTUAL output against
    /// an independently-authored recomputation of the documented rule, not merely that the
    /// pipeline agrees with itself.
    /// </summary>
    private sealed class NullsLastOrdinal : IComparer<string?>
    {
        internal static readonly NullsLastOrdinal Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return 1;
            }

            if (y is null)
            {
                return -1;
            }

            return string.CompareOrdinal(x, y);
        }
    }
}
