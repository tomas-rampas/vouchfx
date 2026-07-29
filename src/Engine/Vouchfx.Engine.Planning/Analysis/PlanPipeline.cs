// Vouchfx.Engine.Planning.Analysis — PlanPipeline (M3 Planner).
//
// THE COMPOSITION / REGISTRATION POINT. Written once by T1 and never edited again except for
// the two fix-round MINOR items called out at the sort helper and BuildInventory below: T2/T3/
// T4 each fill in their own already-registered analyser stub file without touching this one, so
// none of their work can conflict on a shared "registration list" file. Runs the three
// analysers, enriches every gap finding with REQ-007 hand-off hints, sorts findings into the
// EDGE-005 deterministic order, and assembles the full PlanReportDocument (including the
// REQ-003 inventory and the REQ-007 unmappable-dependency classification, both computed
// directly here from PlanInputs + the real, shipped DependencyKindStepMap).

using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;

namespace Vouchfx.Engine.Planning.Analysis;

/// <summary>The Planner's single analysis pipeline: run every analyser, enrich, sort, assemble.</summary>
internal static class PlanPipeline
{
    /// <summary>
    /// Runs the full Planner analysis over <paramref name="inputs"/> and assembles the
    /// complete <see cref="PlanReportDocument"/>.
    /// </summary>
    /// <param name="inputs">The fully-assembled, correlated Planner inputs.</param>
    /// <param name="engineVersion">Optional engine/host version stamp for the document root.</param>
    internal static PlanReportDocument Run(PlanInputs inputs, string? engineVersion)
    {
        var findings = new List<PlanFinding>();
        findings.AddRange(CoverageGapAnalyser.Analyse(inputs));      // T2
        findings.AddRange(VocabularyGapAnalyser.Analyse(inputs));    // T3
        findings.AddRange(HistoryHealthAnalyser.Analyse(inputs));    // T4
        var enriched = HandOffHints.Enrich(findings, inputs);        // T3 — REQ-007 on every gap finding

        var thresholdsReport = new PlanThresholdsReport(
            StaleDays: inputs.Thresholds.StaleDays,
            FlakyMinRuns: inputs.Thresholds.FlakyMinRuns,
            FragileMinEnvErrors: inputs.Thresholds.FragileMinEnvErrors,
            InconclusiveMin: inputs.Thresholds.InconclusiveMin);

        return new PlanReportDocument(
            SchemaVersion: PlanExport.PlanSchemaVersion,
            EngineVersion: engineVersion,
            Thresholds: thresholdsReport,
            Inventory: BuildInventory(inputs),
            Findings: Sort(enriched));
    }

    // ── EDGE-005 deterministic order ─────────────────────────────────────────────

    /// <summary>
    /// Sorts findings by kind, then target, then suite path, then step id, then ambiguity
    /// reason, then detail text — every string key <see cref="StringComparer.Ordinal"/>,
    /// nulls last. The fourth key (step id) is required, not decorative: the spec's three
    /// stated keys leave two steps in the same suite against the same target tied, which
    /// would otherwise make byte-identical re-runs depend on input enumeration order.
    /// </summary>
    /// <remarks>
    /// MINOR fix-round: the original four-key claim that no analyser emits a tie past those
    /// four keys was false the moment <see cref="CoverageGapAnalyser"/> went from a T1 stub
    /// to real: two <see cref="PlanFindingKinds.SuiteIdentityAmbiguous"/> findings (kind,
    /// target, suite, and step id all <see langword="null"/> on both — see
    /// <see cref="CoverageGapAnalyser.Analyse"/>'s rule-1/rule-2 emitters) tie on every one of
    /// those four keys whenever a suite set has two scenario-id collisions, or one collision
    /// plus one renamed-file ambiguity. The fifth key
    /// (<see cref="PlanFinding.AmbiguityReason"/>) separates a collision finding from a
    /// renamed-file finding, but TWO collisions (or two renamed-file ambiguities) still tie on
    /// all five — each is keyed by a distinct <c>ScenarioId</c> upstream
    /// (<c>RunCorrelator.Correlate</c>'s per-ScenarioId grouping), and that id is always
    /// embedded in <see cref="PlanFinding.Detail"/>'s templated prose, so the sixth key
    /// finishes the job for every finding shape the current analysers produce. Before this fix
    /// the four-key sort "worked" only by accident of <c>RunCorrelator</c> happening to emit
    /// its collisions/renamed-file lists already ordinal-sorted by <c>ScenarioId</c> and
    /// LINQ's <c>OrderBy</c>/<c>ThenBy</c> being a stable sort — properties this pipeline must
    /// not silently depend on. This six-key sort is a genuinely total order over every finding
    /// shape the current analysers emit; it is not a mathematically total order over every
    /// conceivable <see cref="PlanFinding"/> two hand-built findings identical in all six
    /// fields would still tie, but no analyser today constructs such a pair.
    /// </remarks>
    private static PlanFinding[] Sort(IReadOnlyList<PlanFinding> findings) =>
        findings
            .OrderBy(f => f.Kind, StringComparer.Ordinal)
            .ThenBy(f => f.Target, NullsLastOrdinal.Instance)
            .ThenBy(f => f.Suite, NullsLastOrdinal.Instance)
            .ThenBy(f => f.StepId, NullsLastOrdinal.Instance)
            .ThenBy(f => f.AmbiguityReason, NullsLastOrdinal.Instance)
            .ThenBy(f => f.Detail, StringComparer.Ordinal)
            .ToArray();

    /// <summary><see cref="StringComparer.Ordinal"/> with nulls sorted last instead of first.</summary>
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

    // ── REQ-003 inventory assembly ────────────────────────────────────────────────

    private static PlanInventory BuildInventory(PlanInputs inputs)
    {
        var suiteEntries = inputs.Suites
            .Select(s => new PlanSuiteEntry(s.RelativePath, s.ScenarioId, s.MetadataName, s.Ast.Steps.Count))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToArray();

        var services = inputs.Suites
            .SelectMany(s => s.Ast.Environment?.Services?.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // MAJOR fix-round: scoped to (suite, name, type), never name alone — a dependency is
        // declared inside exactly one suite's environment, and two suites may declare a
        // same-named dependency (optionally with different Type values), so Suite is carried
        // on every entry (PlanDependencyEntry.Suite's own remarks) rather than deduplicating
        // across suites by name the way the pre-fix implementation did.
        var dependencies = inputs.Suites
            .SelectMany(suite => DependenciesOf(suite)
                .Select(kvp => new PlanDependencyEntry(kvp.Key, kvp.Value.Type, suite.RelativePath)))
            .Distinct()
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Type, StringComparer.Ordinal)
            .ThenBy(d => d.Suite, StringComparer.Ordinal)
            .ToArray();

        var stepTypes = inputs.Suites
            .SelectMany(s => s.Ast.Steps.Select(step => step.CanonicalType))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

        // REQ-007: a dependency kind with no candidate step type is recorded here as
        // unmappable (never as a hint-less gap finding). DependencyKindStepMap is the real,
        // shipped mapping table (every one of the thirteen KnownDependencyKinds.All entries
        // maps to at least one candidate today), so this narrows to the genuine REQ-007
        // case-(b) kind (unknown to the engine entirely) with no edit here. The reason text
        // itself lives in DependencyKindStepMap.DescribeUnmappable (T3's file), not here, so
        // the mapping table and its prose stay in the same place.
        var unmappable = dependencies
            .Where(d => !DependencyKindStepMap.TryGetCandidates(d.Type, out _))
            .Select(d => new PlanUnmappableDependency(d.Name, d.Type, DependencyKindStepMap.DescribeUnmappable(d.Type), d.Suite))
            .ToArray();

        return new PlanInventory(
            Suites: suiteEntries,
            Services: services,
            Dependencies: dependencies,
            StepTypes: stepTypes,
            RunCount: inputs.History.RunCount,
            FirstEventTs: inputs.History.FirstTimestamp,
            LastEventTs: inputs.History.LastTimestamp,
            SkippedEventLines: inputs.History.SkippedLines,
            UnmatchedObservations: inputs.History.UnmatchedObservationCount,
            UnanalysableSuites: inputs.UnanalysableSuites,
            UnmappableDependencies: unmappable);
    }

    private static IEnumerable<KeyValuePair<string, DependencySpec>> DependenciesOf(PlanSuite suite) =>
        suite.Ast.Environment?.Dependencies ?? Enumerable.Empty<KeyValuePair<string, DependencySpec>>();
}
