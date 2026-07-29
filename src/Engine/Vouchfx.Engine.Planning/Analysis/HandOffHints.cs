// Vouchfx.Engine.Planning.Analysis — HandOffHints (M3 Planner, REQ-007).
//
// Enriches every REQ-004/REQ-005 gap finding that is missing its hand-off hints with a
// suggested step type list and a suggested step id, so a host can go gap -> scaffold ->
// validate without re-deriving anything (REQ-014). Registered in PlanPipeline.Run already —
// do NOT edit PlanPipeline.cs to wire this in further.
//
// Two un-filled finding shapes reach this enricher from CoverageGapAnalyser (T2), which
// deliberately does not depend on this file's DependencyKindStepMap (that would put a
// cross-todo dependency on a file T3 owns):
//   - a target-scoped finding (Target/TargetKind set — dependency-not-asserted,
//     dependency-missing-step-type, service-missing-http-step): the candidate step types
//     come from DependencyKindStepMap (for a dependency target) or the hardcoded REQ-005
//     service rule (http.rest, for a service target). The dependency's declared `type` is
//     resolved via the finding's Suite when set, else any suite that declares it.
//   - a step-scoped finding (StepId set — step-never-exercised): the step already has a
//     known, registered CanonicalType, so that becomes the sole suggested type and the
//     step's OWN id becomes the suggested id. BLOCKER fix-round: this finding kind may ALSO
//     now carry a resolved/classified Target/TargetKind (the service/dependency its own
//     declared `target` addresses — CoverageGapAnalyser.AnalyseStepsNeverExercised), but the
//     HINT stays "re-run this already-declared step", never a candidate drawn from
//     DependencyKindStepMap/the REQ-005 service rule — that answers a different question
//     ("what NEW step would close this gap") than "re-run the step that already exists". So
//     EnrichOne dispatches on Kind == StepNeverExercised BEFORE the generic target-scoped
//     branch, never on "Target is set" alone.
// A finding with neither a Target nor a StepId (suite-never-run) names no single
// service/dependency/step a scaffold call could act on, so there is nothing to suggest — it
// is left exactly as declared.
//
// VocabularyGapAnalyser (this todo's other analyser) emits its own findings already filled
// in (it owns both the mapping and this file), so Enrich is a no-op pass-through for them;
// this enricher exists for OTHER analysers' un-filled findings.

using Vouchfx.Engine.Planning.Ingest;
using Vouchfx.Engine.Planning.Report;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Planning.Analysis;

/// <summary>REQ-007 hand-off-hint enrichment over every gap finding that lacks one.</summary>
internal static class HandOffHints
{
    private const string ServiceSuggestedStepType = "http.rest";

    /// <summary>
    /// Returns <paramref name="findings"/> with every REQ-004/REQ-005 gap finding that is
    /// missing its hand-off hints enriched with a suggested step type list and a suggested
    /// step id. History-health and suite-identity-ambiguity findings, and any gap finding
    /// that already carries hints, pass through unchanged.
    /// </summary>
    /// <param name="findings">Every finding produced by the three analysers, unsorted.</param>
    /// <param name="inputs">The Planner inputs, used to resolve a target's declared kind/step and to validate suggested types against the current registration.</param>
    internal static IReadOnlyList<PlanFinding> Enrich(IReadOnlyList<PlanFinding> findings, PlanInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(inputs);

        var enriched = new PlanFinding[findings.Count];
        for (var i = 0; i < findings.Count; i++)
        {
            enriched[i] = EnrichOne(findings[i], inputs);
        }

        return enriched;
    }

    private static PlanFinding EnrichOne(PlanFinding finding, PlanInputs inputs)
    {
        if (!PlanFindingKinds.IsGap(finding.Kind) || HasHints(finding))
        {
            return finding;
        }

        // step-never-exercised dispatches FIRST and unconditionally to EnrichStepScoped,
        // regardless of whether Target is also set (see the file header comment): its hint is
        // always "re-run this step's own type/id", never a target-derived candidate.
        if (finding.Kind == PlanFindingKinds.StepNeverExercised && finding.StepId is { } stepId)
        {
            return EnrichStepScoped(finding, stepId, inputs);
        }

        if (finding.Target is { } target)
        {
            return EnrichTargetScoped(finding, target, inputs);
        }

        // No target and no step id (e.g. suite-never-run): nothing a scaffold call could act
        // on — left un-enriched.
        return finding;
    }

    private static bool HasHints(PlanFinding finding) =>
        finding.SuggestedTypes.Count > 0 || finding.SuggestedStepId is not null;

    private static PlanFinding EnrichTargetScoped(PlanFinding finding, string target, PlanInputs inputs)
    {
        IReadOnlyList<string> suggestedTypes;
        if (string.Equals(finding.TargetKind, PlanTargetKinds.Service, StringComparison.Ordinal))
        {
            suggestedTypes = FilterToRegistered(inputs.Registry, new[] { ServiceSuggestedStepType });
        }
        else if (string.Equals(finding.TargetKind, PlanTargetKinds.Dependency, StringComparison.Ordinal)
            && TryResolveDependencyType(inputs, finding.Suite, target, out var dependencyType)
            && DependencyKindStepMap.TryGetCandidates(dependencyType, out var candidates))
        {
            suggestedTypes = FilterToRegistered(inputs.Registry, candidates);
        }
        else
        {
            // The dependency's declared type could not be resolved, or its kind has no
            // REQ-005 candidate at all (unmappable — recorded in the inventory instead).
            // Enriching with a fabricated suggestion would violate REQ-007; leave the
            // finding as declared.
            return finding;
        }

        if (suggestedTypes.Count == 0)
        {
            return finding;
        }

        return finding with
        {
            SuggestedTypes = suggestedTypes,
            SuggestedStepId = BuildSuggestedStepId(target),
        };
    }

    private static PlanFinding EnrichStepScoped(PlanFinding finding, string stepId, PlanInputs inputs)
    {
        if (!TryResolveStepType(inputs, finding.Suite, stepId, out var stepType)
            || !inputs.Registry.TryGet(stepType, out _))
        {
            return finding;
        }

        return finding with
        {
            SuggestedTypes = new[] { stepType },
            SuggestedStepId = stepId,
        };
    }

    private static bool TryResolveDependencyType(PlanInputs inputs, string? suiteRelativePath, string dependencyName, out string type)
    {
        foreach (var suite in CandidateSuites(inputs, suiteRelativePath))
        {
            if (suite.Ast.Environment?.Dependencies?.TryGetValue(dependencyName, out var spec) == true)
            {
                type = spec.Type;
                return true;
            }
        }

        type = string.Empty;
        return false;
    }

    private static bool TryResolveStepType(PlanInputs inputs, string? suiteRelativePath, string stepId, out string type)
    {
        foreach (var suite in CandidateSuites(inputs, suiteRelativePath))
        {
            var step = suite.Ast.Steps.FirstOrDefault(s => string.Equals(s.Id, stepId, StringComparison.Ordinal));
            if (step is not null)
            {
                type = step.CanonicalType;
                return true;
            }
        }

        type = string.Empty;
        return false;
    }

    private static IEnumerable<PlanSuite> CandidateSuites(PlanInputs inputs, string? suiteRelativePath) =>
        suiteRelativePath is null
            ? inputs.Suites
            : inputs.Suites.Where(s => string.Equals(s.RelativePath, suiteRelativePath, StringComparison.Ordinal));

    /// <summary>
    /// Filters <paramref name="types"/> down to the ones present in <paramref name="registry"/>
    /// (REQ-007: "suggested types MUST exist in the current registration") and ordinal-sorts
    /// the result (REQ-005: "suggestedTypes lists all candidates, ordinal-sorted"). Shared by
    /// <see cref="VocabularyGapAnalyser"/>, which already knows its own candidates are
    /// registered (guarded by <c>DependencyKindStepMapDriftTests</c>) but filters anyway for a
    /// single, consistent guarantee at every call site.
    /// </summary>
    internal static IReadOnlyList<string> FilterToRegistered(StepKindRegistry registry, IReadOnlyList<string> types) =>
        types
            .Where(t => registry.TryGet(t, out _))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Builds a deterministic, YAML-legal (<c>^[A-Za-z_][A-Za-z0-9_-]*$</c>, matching the
    /// root language schema's step-id pattern) suggested step id from a target
    /// service/dependency name: the literal prefix <c>"assert-"</c> followed by every
    /// character of <paramref name="targetName"/> that is not itself legal in a step id,
    /// replaced with <c>_</c>. The <c>"assert-"</c> prefix guarantees the first character is
    /// always legal regardless of <paramref name="targetName"/>'s own first character.
    /// </summary>
    /// <param name="targetName">A declared service or dependency name.</param>
    internal static string BuildSuggestedStepId(string targetName)
    {
        ArgumentNullException.ThrowIfNull(targetName);

        var sanitized = new string(targetName.Select(c => IsLegalStepIdChar(c) ? c : '_').ToArray());
        return "assert-" + sanitized;
    }

    private static bool IsLegalStepIdChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}
