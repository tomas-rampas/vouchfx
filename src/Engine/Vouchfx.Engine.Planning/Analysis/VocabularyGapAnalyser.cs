// Vouchfx.Engine.Planning.Analysis — VocabularyGapAnalyser (M3 Planner, REQ-005, REQ-007).
//
// Declared-vs-available vocabulary gaps: a dependency-missing-step-type finding under the
// REQ-005 zero-of-N candidate rule (DependencyKindStepMap.TryGetCandidates), and a
// service-missing-http-step finding for a declared service with no http.* step targeting it
// (REQ-005's only v1 service rule — services carry no technology-kind field in the DSL).
// Registered in PlanPipeline.Run already — do NOT edit PlanPipeline.cs to wire this in
// further.
//
// This is purely a declared-vs-catalogue analysis: unlike CoverageGapAnalyser (T2), it never
// reads PlanInputs.History. A dependency/service is aggregated by NAME across every suite
// that declares it (mirroring PlanPipeline.BuildInventory's own REQ-003 aggregation), so a
// step in ANY analysed suite that targets it counts as coverage.
//
// A dependency whose kind has no REQ-005 candidate step type at all
// (DependencyKindStepMap.TryGetCandidates returns false) is never reported here — REQ-007
// forbids a hint-less gap finding for it. PlanPipeline.BuildInventory already records it as
// an unmappable dependency instead, from the very same TryGetCandidates call, so filling the
// mapping table narrows both outputs together with no wiring here.
//
// Findings are emitted with their REQ-007 hints already filled in (this analyser owns both
// the mapping and the enrichment helpers, in HandOffHints.cs) rather than emitted empty and
// passed through HandOffHints.Enrich — that round trip exists only for OTHER analysers
// (CoverageGapAnalyser, T2) whose findings need a hint this file's mapping computes.

using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;

namespace Vouchfx.Engine.Planning.Analysis;

/// <summary>REQ-005 declared-vs-available vocabulary-gap analyser.</summary>
internal static class VocabularyGapAnalyser
{
    private const string HttpFamily = "http";
    private const string ServiceSuggestedStepType = "http.rest";

    /// <summary>Analyses <paramref name="inputs"/> for vocabulary gaps.</summary>
    internal static IReadOnlyList<PlanFinding> Analyse(PlanInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var findings = new List<PlanFinding>();
        findings.AddRange(AnalyseDependencies(inputs));
        findings.AddRange(AnalyseServices(inputs));
        return findings;
    }

    // ── REQ-005: dependency-missing-step-type (zero-of-N rule) ──────────────────

    private static IEnumerable<PlanFinding> AnalyseDependencies(PlanInputs inputs)
    {
        var typeByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var suite in inputs.Suites)
        {
            var dependencies = suite.Ast.Environment?.Dependencies;
            if (dependencies is null)
            {
                continue;
            }

            foreach (var (name, spec) in dependencies)
            {
                // First-seen type wins for a name declared with conflicting types across
                // suites — an inconsistency the DSL does not forbid but that is outside this
                // spec's scope; REQ-003's own inventory aggregation has the same property.
                typeByName.TryAdd(name, spec.Type);
            }
        }

        foreach (var name in typeByName.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            var type = typeByName[name];

            if (!DependencyKindStepMap.TryGetCandidates(type, out var candidates))
            {
                // Unmappable: no candidate asserting/observing type exists for this kind at
                // all. REQ-007 forbids a hint-less finding here; PlanPipeline.BuildInventory
                // records this dependency as unmappable instead, from the same
                // TryGetCandidates call.
                continue;
            }

            var isCovered = IsTargetCovered(inputs, name, PlanTargetKinds.Dependency, step =>
                candidates.Contains(step.CanonicalType, StringComparer.Ordinal));

            if (isCovered)
            {
                continue;
            }

            var suggestedTypes = HandOffHints.FilterToRegistered(inputs.Registry, candidates);
            if (suggestedTypes.Count == 0)
            {
                // Defensive only: DependencyKindStepMapDriftTests guarantees every mapped
                // type exists in the current registration, so this is unreachable in a
                // correctly-wired registry. Skip rather than emit a hint-less finding.
                continue;
            }

            yield return new PlanFinding(
                Kind: PlanFindingKinds.DependencyMissingStepType,
                Suite: null,
                StepId: null,
                Target: name,
                TargetKind: PlanTargetKinds.Dependency,
                SuggestedTypes: suggestedTypes,
                SuggestedStepId: HandOffHints.BuildSuggestedStepId(name),
                Ambiguous: false,
                AmbiguityReason: null,
                History: null,
                RelatedSuites: Array.Empty<string>(),
                Detail: $"Dependency '{name}' ({type}) has no analysed step of a candidate asserting type.");
        }
    }

    // ── REQ-005: service-missing-http-step (the only v1 service rule) ───────────

    private static IEnumerable<PlanFinding> AnalyseServices(PlanInputs inputs)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var suite in inputs.Suites)
        {
            var services = suite.Ast.Environment?.Services;
            if (services is null)
            {
                continue;
            }

            foreach (var name in services.Keys)
            {
                names.Add(name);
            }
        }

        foreach (var name in names)
        {
            var isCovered = IsTargetCovered(inputs, name, PlanTargetKinds.Service, step =>
                string.Equals(step.Kind.Family, HttpFamily, StringComparison.Ordinal));

            if (isCovered)
            {
                continue;
            }

            var suggestedTypes = HandOffHints.FilterToRegistered(inputs.Registry, new[] { ServiceSuggestedStepType });
            if (suggestedTypes.Count == 0)
            {
                continue;
            }

            yield return new PlanFinding(
                Kind: PlanFindingKinds.ServiceMissingHttpStep,
                Suite: null,
                StepId: null,
                Target: name,
                TargetKind: PlanTargetKinds.Service,
                SuggestedTypes: suggestedTypes,
                SuggestedStepId: HandOffHints.BuildSuggestedStepId(name),
                Ambiguous: false,
                AmbiguityReason: null,
                History: null,
                RelatedSuites: Array.Empty<string>(),
                Detail: $"Service '{name}' has no http.* step targeting it.");
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when at least one step, in any analysed suite, both
    /// satisfies <paramref name="typePredicate"/> and resolves (via the shared
    /// <see cref="StepTargetResolver.TryClassifyTarget"/> classifier — the same lookup
    /// <c>CoverageGapAnalyser</c> uses, so the two analysers can never disagree about what a
    /// target name resolves to) to <paramref name="name"/> classified as
    /// <paramref name="expectedTargetKind"/>.
    /// </summary>
    private static bool IsTargetCovered(
        PlanInputs inputs,
        string name,
        string expectedTargetKind,
        Func<StepNode, bool> typePredicate) =>
        inputs.Suites.Any(suite => suite.Ast.Steps.Any(step =>
        {
            if (!typePredicate(step))
            {
                return false;
            }

            var target = StepTargetResolver.ResolveTarget(step);
            return target is not null
                && string.Equals(target, name, StringComparison.Ordinal)
                && StepTargetResolver.TryClassifyTarget(suite, target, out var targetKind)
                && string.Equals(targetKind, expectedTargetKind, StringComparison.Ordinal);
        }));
}
