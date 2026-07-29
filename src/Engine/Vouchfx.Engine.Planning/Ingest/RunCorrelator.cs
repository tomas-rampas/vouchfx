// Vouchfx.Engine.Planning.Ingest — RunCorrelator (M3 Planner, REQ-008, EDGE-002, EDGE-007,
// flag F-2).
//
// Attributes event-history observations to declared suites by EXACT runId equality —
// never by file position or a positional window between scenario-started/scenario-completed
// lines, which would misattribute events under parallel interleaved streams. Implements the
// 4-rule disambiguation exactly:
//   1. >=2 declared suites share a scenarioId -> collision (independent of history).
//   2. Zero match, but the run's recorded `file` resolves under the analysed root and no
//      longer exists on disk -> renamed-file ambiguity.
//   3. Zero match with no `file`, or a `file` outside the analysed root -> unmatched
//      observation, no finding (EDGE-007).
//   4. Same step id in two suites under distinct runIds -> resolved by runId; no ambiguity.
//
// The actual PlanFinding for an ambiguity (kind suite-identity-ambiguous, and the
// Ambiguous/AmbiguityReason markers on coverage findings) is emitted by CoverageGapAnalyser
// (T2) from the raw facts this class produces — this class never emits a PlanFinding itself.

namespace Vouchfx.Engine.Planning.Ingest;

/// <summary>Correlates a raw event history against the declared suite set (REQ-008).</summary>
internal static class RunCorrelator
{
    /// <summary>
    /// Correlates <paramref name="raw"/> against <paramref name="suites"/>, producing the
    /// fully-classified <see cref="PlanEventHistory"/> the analysers read.
    /// </summary>
    /// <param name="suites">Every successfully-parsed declared suite.</param>
    /// <param name="analysedRoot">The absolute analysed suite root, for rule 2's under-root check.</param>
    /// <param name="raw">The raw, uncorrelated event data from <see cref="EventHistoryReader"/>.</param>
    internal static PlanEventHistory Correlate(
        IReadOnlyList<PlanSuite> suites,
        string analysedRoot,
        EventHistoryReader.RawEventHistory raw)
    {
        // Rule 1: scenario-id collisions among DECLARED suites — a property of the suite set
        // itself, independent of whether any history references it.
        var collisions = suites
            .GroupBy(s => s.ScenarioId, StringComparer.Ordinal)
            .Where(g => g.Count() >= 2)
            .Select(g => new PlanScenarioIdCollision(
                g.Key,
                (IReadOnlyList<string>)g.Select(s => s.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToArray()))
            .OrderBy(c => c.ScenarioId, StringComparer.Ordinal)
            .ToList();

        var declaredScenarioIds = new HashSet<string>(suites.Select(s => s.ScenarioId), StringComparer.Ordinal);

        var runIdToScenarioId = new Dictionary<string, string>(StringComparer.Ordinal);
        var runIdsByScenarioId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var renamedFileByScenarioId = new Dictionary<string, (string File, List<string> RunIds)>(StringComparer.Ordinal);
        // M2 fix: runIds already classified as a rule-2 renamed-file ambiguity. A
        // step-completed event sharing one of these runIds must NOT also inflate
        // UnmatchedObservationCount — both PlanInputs.cs's and the frozen
        // PlanReportDocument.cs's XML docs say that count excludes observations already
        // classified as a suite-identity ambiguity.
        var ambiguousRunIds = new HashSet<string>(StringComparer.Ordinal);
        var runs = new List<PlanScenarioRun>(raw.ScenarioStarts.Count);
        var unmatched = 0;

        foreach (var evt in raw.ScenarioStarts)
        {
            runs.Add(new PlanScenarioRun(evt.RunId, evt.ScenarioId, evt.File, evt.Timestamp));
            runIdToScenarioId[evt.RunId] = evt.ScenarioId;

            if (declaredScenarioIds.Contains(evt.ScenarioId))
            {
                AddDistinct(runIdsByScenarioId, evt.ScenarioId, evt.RunId);

                // Fix-round-2 NEW-4: a real runId is minted once per scenario execution
                // (blueprint §16.2), so a single runId appearing under BOTH a declared match
                // and a rule-2 renamed-file ambiguity cannot happen in production history —
                // this Remove only matters for a hand-authored fixture that (illegally)
                // reuses one runId across two scenario-started events. Chosen semantics:
                // whichever event for a given runId is processed LAST in raw.ScenarioStarts
                // decides its classification — consistent with runIdToScenarioId's own
                // unconditional overwrite two lines above, so the two never disagree about
                // the same runId. Without this Remove, a runId classified ambiguous here
                // could never be un-classified, silently dropping every step-completed event
                // sharing it (see the loop below) even when a later event in the same fixture
                // legitimately matches a declared suite.
                ambiguousRunIds.Remove(evt.RunId);
                continue;
            }

            // Zero match by scenario id: rule 2 (renamed file) vs rule 3 (unmatched, EDGE-007).
            if (evt.File is not null && ResolvesUnderRootAndMissing(analysedRoot, evt.File))
            {
                ambiguousRunIds.Add(evt.RunId);

                if (!renamedFileByScenarioId.TryGetValue(evt.ScenarioId, out var entry))
                {
                    entry = (evt.File, new List<string>());
                    renamedFileByScenarioId[evt.ScenarioId] = entry;
                }

                if (!entry.RunIds.Contains(evt.RunId, StringComparer.Ordinal))
                {
                    entry.RunIds.Add(evt.RunId);
                }

                continue;
            }

            unmatched++;
        }

        var stepObservations = new Dictionary<(string, string), List<PlanStepObservation>>();
        foreach (var evt in raw.StepCompletions)
        {
            if (ambiguousRunIds.Contains(evt.RunId))
            {
                // Already classified as a rule-2 suite-identity ambiguity above — excluded
                // from UnmatchedObservationCount (M2), and not attributable to any known
                // suite/step either, so it is neither counted nor recorded as an observation.
                continue;
            }

            if (!runIdToScenarioId.TryGetValue(evt.RunId, out var scenarioId)
                || !declaredScenarioIds.Contains(scenarioId))
            {
                // Either this step's run has no matching scenario-started event in the
                // analysed history, or its scenario id names a suite outside the analysed
                // path (EDGE-007). Neither is a suite-identity ambiguity (rule 3): count it
                // and move on — never a finding, never a crash.
                unmatched++;
                continue;
            }

            var key = (scenarioId, evt.StepId);
            if (!stepObservations.TryGetValue(key, out var list))
            {
                list = new List<PlanStepObservation>();
                stepObservations[key] = list;
            }

            list.Add(new PlanStepObservation(evt.RunId, evt.Verdict, evt.Timestamp));
        }

        var renamedFileAmbiguities = renamedFileByScenarioId
            .Select(kvp => new PlanRenamedFileAmbiguity(
                kvp.Key,
                kvp.Value.File,
                (IReadOnlyList<string>)kvp.Value.RunIds.OrderBy(r => r, StringComparer.Ordinal).ToArray()))
            .OrderBy(a => a.ScenarioId, StringComparer.Ordinal)
            .ToList();

        var runCount = raw.RunIds.Distinct(StringComparer.Ordinal).Count();

        return new PlanEventHistory(
            Runs: runs,
            RunIdsByScenarioId: runIdsByScenarioId.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<string>)kvp.Value.OrderBy(r => r, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal),
            StepObservations: stepObservations.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<PlanStepObservation>)kvp.Value),
            SkippedLines: raw.SkippedLines,
            UnmatchedObservationCount: unmatched,
            FirstTimestamp: raw.FirstTimestamp,
            LastTimestamp: raw.LastTimestamp,
            Collisions: collisions,
            RenamedFileAmbiguities: renamedFileAmbiguities,
            RunCount: runCount);
    }

    private static void AddDistinct(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<string>();
            map[key] = list;
        }

        if (!list.Contains(value, StringComparer.Ordinal))
        {
            list.Add(value);
        }
    }

    /// <summary>
    /// Resolves <paramref name="recordedFile"/> (relative to <paramref name="analysedRoot"/>
    /// when not rooted) and returns <see langword="true"/> only when the resolved path falls
    /// under the analysed root AND no longer exists on disk (REQ-008 rule 2, the rename
    /// case). Path-prefix comparison is case-insensitive, matching the case-insensitive
    /// filesystems this check is primarily exercised against.
    /// </summary>
    private static bool ResolvesUnderRootAndMissing(string analysedRoot, string recordedFile)
    {
        string resolved;
        string normalisedRoot;
        try
        {
            normalisedRoot = Path.GetFullPath(analysedRoot);
            resolved = Path.IsPathRooted(recordedFile)
                ? Path.GetFullPath(recordedFile)
                : Path.GetFullPath(Path.Combine(normalisedRoot, recordedFile));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        var rootWithSeparator = normalisedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalisedRoot
            : normalisedRoot + Path.DirectorySeparatorChar;

        var isUnderRoot = resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            || string.Equals(resolved, normalisedRoot, StringComparison.OrdinalIgnoreCase);

        return isUnderRoot && !File.Exists(resolved);
    }
}
