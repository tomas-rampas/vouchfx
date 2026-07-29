// Vouchfx.Engine.Planning.Tests — PlanLoopClosureTests (M3 Planner, REQ-014, the M3 exit
// criterion, T6 scope).
//
// Proves the whole point of the Planner's hand-off hints mechanically: take a gap finding
// produced from a fixture suite folder, feed its REQ-007 hints (SuggestedTypes,
// SuggestedStepId, Target) UNMODIFIED into the scaffold path, and assert the scaffolded YAML
// passes validation with exit 0 — Docker-free and in-process, calling the exact library code
// (SuiteScaffolder.Generate, DocumentValidator.Validate) the CLI's scaffold/validate commands
// call (flag F-14), rather than spawning the packaged CLI.
//
// "Unmodified" means every value plumbed into the ScaffoldIntent below is read DIRECTLY off
// the PlanFinding record (finding.SuggestedStepId, finding.SuggestedTypes[0], finding.Target)
// with no string manipulation, substitution, or hand-editing in between — so a future change
// that renames one of those three properties is a COMPILE failure here, and a future change
// that makes SuiteScaffolder reject what HandOffHints produces (e.g. an illegal step id
// pattern, or a suggested type no longer present in the registry) is a runtime assertion
// failure here. Both are exactly what REQ-014's acceptance criterion asks for: "fails if a
// hint field is renamed or becomes unusable by scaffold".
//
// Two fixtures exercise the loop across BOTH target kinds a gap finding can carry
// (PlanTargetKinds.Service / .Dependency), since HandOffHints enriches the two differently
// (a service suggestion is the hardcoded REQ-005 http.rest rule; a dependency suggestion
// comes from DependencyKindStepMap):
//   - Fixtures/acceptance/loop-closure-service: a declared service with no http.* step
//     targeting it -> service-missing-http-step (Target/TargetKind = the service).
//   - Fixtures/acceptance/loop-closure-dependency: a zero-step kafka dependency ->
//     dependency-missing-step-type (Target/TargetKind = the dependency). The dependency's
//     OWN declared kind (needed to build a legal ScaffoldDependencyIntent — orthogonal
//     plumbing, not one of the three hint fields under test) is read from the SAME report's
//     Inventory.Dependencies list, never hand-typed.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Compilation.Scaffold;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Planning.Report;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class PlanLoopClosureTests
{
    // ── Service-scoped gap: service-missing-http-step -> scaffold -> validate ───────────

    [Fact]
    public void ServiceScopedGapFinding_ScaffoldedFromItsHintsUnmodified_PassesValidation()
    {
        var registry = PlannerTestFixtures.BuildCoreRegistry();
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("acceptance/loop-closure-service"));

        var finding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.ServiceMissingHttpStep);
        Assert.Equal(PlanTargetKinds.Service, finding.TargetKind);
        Assert.NotEmpty(finding.SuggestedTypes);
        Assert.False(string.IsNullOrEmpty(finding.SuggestedStepId));
        Assert.False(string.IsNullOrEmpty(finding.Target));

        // The three REQ-007 hint fields, read DIRECTLY off the finding — no hand-editing.
        var intent = new ScaffoldIntent(
            Steps: new[] { new ScaffoldStepIntent(finding.SuggestedStepId!, finding.SuggestedTypes[0]) },
            Services: new[] { new ScaffoldServiceIntent(finding.Target!) });

        var yaml = SuiteScaffolder.Generate(registry, intent);
        var result = DocumentValidator.Validate(yaml, registry);

        Assert.True(result.IsValid, DescribeErrors(result));
        Assert.Empty(result.Errors);
    }

    // ── Dependency-scoped gap: dependency-missing-step-type -> scaffold -> validate ─────

    [Fact]
    public void DependencyScopedGapFinding_ScaffoldedFromItsHintsUnmodified_PassesValidation()
    {
        var registry = PlannerTestFixtures.BuildCoreRegistry();
        var report = PlannerTestFixtures.Plan(PlannerTestFixtures.FixtureRoot("acceptance/loop-closure-dependency"));

        var finding = Assert.Single(
            report.Findings, f => f.Kind == PlanFindingKinds.DependencyMissingStepType);
        Assert.Equal(PlanTargetKinds.Dependency, finding.TargetKind);
        Assert.NotEmpty(finding.SuggestedTypes);
        Assert.False(string.IsNullOrEmpty(finding.SuggestedStepId));
        Assert.False(string.IsNullOrEmpty(finding.Target));

        // The dependency's declared KIND is not one of the three hint fields under test — it
        // is read from the same report's own Inventory (structural, machine-derived data a
        // real host already has), never hand-typed or guessed.
        var dependencyType = Assert.Single(
            report.Inventory.Dependencies, d => d.Name == finding.Target).Type;

        var intent = new ScaffoldIntent(
            Steps: new[] { new ScaffoldStepIntent(finding.SuggestedStepId!, finding.SuggestedTypes[0]) },
            Dependencies: new[] { new ScaffoldDependencyIntent(finding.Target!, dependencyType) });

        var yaml = SuiteScaffolder.Generate(registry, intent);
        var result = DocumentValidator.Validate(yaml, registry);

        Assert.True(result.IsValid, DescribeErrors(result));
        Assert.Empty(result.Errors);
    }

    private static string DescribeErrors(SchemaValidationResult result) =>
        result.Errors.Count == 0
            ? "(no errors)"
            : string.Join("; ", result.Errors.Select(e => $"{e.InstanceLocation}: {e.Message}"));
}
