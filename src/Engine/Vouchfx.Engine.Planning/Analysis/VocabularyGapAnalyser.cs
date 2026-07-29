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
// reads PlanInputs.History.
//
// MAJOR fix-round (REQ-005 amended): evaluation scope is PER DECLARING SUITE, never
// aggregated by dependency/service NAME across the analysed set. A dependency (or service) is
// declared inside exactly one suite's `environment`, so its coverage MUST be judged within
// that suite — a step in suite B can never mask an unexercised seam in suite A, even when both
// suites happen to declare a dependency of the same name. The previous name-aggregated
// implementation let a zero-coverage dependency in one suite silently disappear whenever
// another suite declared (and covered) a same-named dependency; two suites each declaring
// `cache: {type: redis}`, one asserting it and one never touching it, now both get judged
// independently, and the second is still reported. This also settles the case of two suites
// declaring the same dependency NAME with different `type` values: each is now evaluated
// against its OWN declared kind, so a dependency can no longer be simultaneously an
// `unmappable` inventory entry (one suite's kind) and a gap finding (another suite's
// same-named-but-differently-typed kind) — the two suites are simply never conflated at all.
// Findings are Suite-scoped (Suite is the declaring suite's RelativePath, never null) so a
// reader can tell which suite's dependency/service is uncovered when two suites share a name.
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
using Vouchfx.Sdk;

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
        foreach (var suite in inputs.Suites)
        {
            findings.AddRange(AnalyseDependencies(suite, inputs.Registry));
            findings.AddRange(AnalyseServices(suite, inputs.Registry));
        }

        return findings;
    }

    // ── REQ-005: dependency-missing-step-type (zero-of-N rule), per declaring suite ─────

    private static IEnumerable<PlanFinding> AnalyseDependencies(PlanSuite suite, StepKindRegistry registry)
    {
        var dependencies = suite.Ast.Environment?.Dependencies;
        if (dependencies is null)
        {
            yield break;
        }

        foreach (var name in dependencies.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            var type = dependencies[name].Type;

            if (!DependencyKindStepMap.TryGetCandidates(type, out var candidates))
            {
                // Unmappable: no candidate asserting/observing type exists for this kind at
                // all. REQ-007 forbids a hint-less finding here; PlanPipeline.BuildInventory
                // records this dependency as unmappable instead, from the same
                // TryGetCandidates call.
                continue;
            }

            var isCovered = IsTargetCoveredWithinSuite(suite, name, PlanTargetKinds.Dependency, step =>
                candidates.Contains(step.CanonicalType, StringComparer.Ordinal));

            if (isCovered)
            {
                continue;
            }

            var suggestedTypes = HandOffHints.FilterToRegistered(registry, candidates);
            if (suggestedTypes.Count == 0)
            {
                // Defensive only: DependencyKindStepMapDriftTests guarantees every mapped
                // type exists in the current registration, so this is unreachable in a
                // correctly-wired registry. Skip rather than emit a hint-less finding.
                continue;
            }

            yield return new PlanFinding(
                Kind: PlanFindingKinds.DependencyMissingStepType,
                Suite: suite.RelativePath,
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

    // ── REQ-005: service-missing-http-step (the only v1 service rule), per declaring suite ──

    private static IEnumerable<PlanFinding> AnalyseServices(PlanSuite suite, StepKindRegistry registry)
    {
        var services = suite.Ast.Environment?.Services;
        if (services is null)
        {
            yield break;
        }

        foreach (var name in services.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            var isCovered = IsTargetCoveredWithinSuite(suite, name, PlanTargetKinds.Service, step =>
                string.Equals(step.Kind.Family, HttpFamily, StringComparison.Ordinal));

            if (isCovered)
            {
                continue;
            }

            var suggestedTypes = HandOffHints.FilterToRegistered(registry, new[] { ServiceSuggestedStepType });
            if (suggestedTypes.Count == 0)
            {
                continue;
            }

            yield return new PlanFinding(
                Kind: PlanFindingKinds.ServiceMissingHttpStep,
                Suite: suite.RelativePath,
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
    /// Returns <see langword="true"/> when at least one step DECLARED IN <paramref name="suite"/>
    /// itself (never any other analysed suite — REQ-005's per-declaring-suite evaluation
    /// scope) both satisfies <paramref name="typePredicate"/> and resolves (via the shared
    /// <see cref="StepTargetResolver.TryClassifyTarget"/> classifier — the same lookup
    /// <c>CoverageGapAnalyser</c> uses, so the two analysers can never disagree about what a
    /// target name resolves to) to <paramref name="name"/> classified as
    /// <paramref name="expectedTargetKind"/>.
    /// </summary>
    private static bool IsTargetCoveredWithinSuite(
        PlanSuite suite,
        string name,
        string expectedTargetKind,
        Func<StepNode, bool> typePredicate) =>
        suite.Ast.Steps.Any(step =>
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
        });
}
