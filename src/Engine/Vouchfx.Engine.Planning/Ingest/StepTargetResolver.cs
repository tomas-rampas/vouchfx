// Vouchfx.Engine.Planning.Ingest — StepTargetResolver (M3 Planner, REQ-004 target
// resolution, flags F-16 and F-17, fix-round M3).
//
// A step targets a dependency/service via its `target` scalar. Two exclusions (REQ-004):
//   - a `target` containing a {placeholder} or ${secret:...} reference is structurally
//     unresolvable and MUST be treated as targeting nothing (never substituted, never
//     counted as coverage, never a crash) — flag F-16;
//   - `listener` / `receiver` fields (webhook-listen.http, trace-expect.otlp) name
//     host-owned resources, not declared dependencies, so they are never target
//     references — flag F-17. Satisfied simply by never reading those fields: this
//     resolver only ever reads `target`.
//
// TryClassifyTarget is the single shared service-vs-dependency classifier: both
// CoverageGapAnalyser (T2) and VocabularyGapAnalyser (T3) need the identical lookup against a
// suite's declared Environment.Services / .Dependencies and both must stamp the frozen
// PlanTargetKinds token — two independent implementations of a frozen-output classifier is
// exactly the drift risk PlanTargetKinds itself exists to close, so it lives once, here, in
// the file both todos already read.

using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Planning.Report;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Planning.Ingest;

/// <summary>Resolves a step's declared <c>target</c> from its raw YAML mapping node, and classifies a target name against a suite's declared environment.</summary>
internal static class StepTargetResolver
{
    /// <summary>
    /// Returns the step's <c>target</c> scalar, or <see langword="null"/> when the field is
    /// absent, empty, or structurally unresolvable (a <c>{placeholder}</c> or
    /// <c>${secret:...}</c> reference — flag F-16). Never throws and never attempts
    /// substitution: an unresolvable target simply targets nothing.
    /// </summary>
    /// <param name="step">The normalised AST step node.</param>
    internal static string? ResolveTarget(StepNode step)
    {
        if (!TryGetScalar(step.RawNode, "target", out var target) || string.IsNullOrEmpty(target))
        {
            return null;
        }

        return IsStructurallyUnresolvable(target) ? null : target;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> contains a
    /// <c>{placeholder}</c> or <c>${secret:source/path}</c> reference. Both forms contain a
    /// literal <c>{</c>, so a single character check covers both without needing to
    /// distinguish them.
    /// </summary>
    internal static bool IsStructurallyUnresolvable(string value) =>
        value.Contains('{', StringComparison.Ordinal);

    /// <summary>
    /// Classifies <paramref name="target"/> as a declared service or dependency name within
    /// <paramref name="suite"/>'s <c>environment</c> block (fix-round M3) — the single shared
    /// lookup both the coverage-gap and vocabulary-gap analysers must use so they can never
    /// stamp <see cref="PlanFinding.TargetKind"/> inconsistently.
    /// </summary>
    /// <param name="suite">The declared suite whose environment to classify against.</param>
    /// <param name="target">A resolved target name (see <see cref="ResolveTarget"/>).</param>
    /// <param name="targetKind">
    /// <see cref="PlanTargetKinds.Service"/> or <see cref="PlanTargetKinds.Dependency"/> when
    /// this method returns <see langword="true"/>; otherwise <see cref="string.Empty"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="target"/> names a key under
    /// <c>environment.services</c> or <c>environment.dependencies</c>; <see langword="false"/>
    /// when it matches neither (e.g. a stale or typo'd target, or one this suite does not
    /// declare).
    /// </returns>
    internal static bool TryClassifyTarget(PlanSuite suite, string target, out string targetKind)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(target);

        if (suite.Ast.Environment?.Services?.ContainsKey(target) == true)
        {
            targetKind = PlanTargetKinds.Service;
            return true;
        }

        if (suite.Ast.Environment?.Dependencies?.ContainsKey(target) == true)
        {
            targetKind = PlanTargetKinds.Dependency;
            return true;
        }

        targetKind = string.Empty;
        return false;
    }

    private static bool TryGetScalar(YamlMappingNode mapping, string key, out string? value)
    {
        var scalarKey = new YamlScalarNode(key);
        if (mapping.Children.TryGetValue(scalarKey, out var node) && node is YamlScalarNode scalar)
        {
            value = scalar.Value;
            return true;
        }

        value = null;
        return false;
    }
}
