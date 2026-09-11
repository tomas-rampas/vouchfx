// Vouchfx.Engine.Runtime.Tests — issue #480's provenance marker, pinned at ALL SEVEN sites that
// set it, and at three that must not.
//
// WHAT WAS UNPINNED, AND IT IS THE HOLE THE HOP CENSUS WAS WRITTEN TO CLOSE ONE HOP LATER.
// `ProviderPipeline.ProviderOrEngineFault(...)` is the single factory that sets
// `ValidationFailure.IsProviderOrEngineFault`, and it is called from seven guards: the six
// `DescribeProviderFault` sites — `Bind` in `BindAllSteps`, then `Validate`, `HostResources`,
// `Resources`, `CompileReferenceAssemblies` and `Emit` in `Compile`'s Pass 2 — and the suite-level
// `CsxAssembler.Assemble` guard, whose diagnostic `DescribeAssemblyFault` composes. MEASURED before
// this file existed: the only step type that REACHED A GUARD while any test asserted the marker
// (on `ValidationFailure` or on `SuiteResult.ProviderOrEngineFaultObserved`) was
// `stub.throwing-bind`, in `MixedSuiteEngineFaultTaxonomyTests`; `RunParallelAsyncTests` sets the
// marker on a fake `ScenarioCoreResult` and never reaches a guard at all. ("Reached a guard" is the
// load-bearing half: that same file also drives `script.csharp` while asserting the marker is
// FALSE, which is a control and reaches no guard by construction.) So the wrapper could be
// deleted from any of the OTHER SIX and the whole suite stayed green, while a provider defect
// beside a passing sibling silently returned to exit 0.
//
// That is exactly the mutation-survivability hole `MixedSuiteEngineFaultHopCensusTests` closes for
// the two PLUMBING hops between `PipelineResult.Failure` and `ComputeExitCode` — left open on the
// guards that produce the value those hops carry. A census over the hops proves the wire is
// connected; it says nothing about how many of the seven sources are wired to it.
//
// WHY THE ROWS DRIVE `ProviderPipeline.Compile` DIRECTLY rather than a runner. The marker is a
// property of the pipeline's own failure, and `Compile` is `internal` and visible to this project.
// Driving a runner would make each row's redness depend on the two hops another file already pins,
// which would blur what a failure here means.
//
// AN EARLIER REVISION ALSO CLAIMED A RUNNER "would add a topology-shaped dependency", AND THAT WAS
// FALSE — recorded because the false reason is what kept the NEXT hop uncovered for a round.
// `RunScenarioOwningTopologyAsync` returns at the pre-topology authoring door, before Aspire is
// started, so a runner-driven row needs no container and carries no trait: two rows in this very
// assembly already prove it (`ProviderReflectiveFaultTaxonomyTests`' parallel theory and
// `ProviderBindThrowTaxonomyTests`' parallel row, both untraited). Believing otherwise is why the
// hop from the pipeline's failure onto `ScenarioCoreResult.ProviderOrEngineFaultObserved` — the
// single line the parallel path's whole fold is fed by — went unasserted in the blocking lane
// until peer review found it. Those two rows now assert the marker, covering all six provider
// surfaces on that hop; the direct-`Compile` rows below stay the per-guard census.
//
// THE STUBS ARE REUSED, NOT REDECLARED. All seven step types below are already declared as
// `file`-scoped `[StepProvider]` classes in this assembly — `stub.throwing-bind` and
// `stub.throwing-hostresource` in `ProviderPipelineTests`, `stub.throwing-validate`,
// `stub.throwing-resources`, `stub.throwing-compilerefs`, `stub.throwing-emit` and
// `stub.bad-fragment` in `ProviderReflectiveFaultTaxonomyTests` — and every one is discovered by
// the SAME assembly scan the registry performs for real providers. `file` scope hides the TYPES
// from this file; it does not hide the step TYPES from the registry, which is the only handle
// these rows need. A second declaration would have been a second fixture to keep in step.
//
// WHAT THE OTHER FILES ALREADY ASSERT, AND WHY THIS IS NOT A DUPLICATE OF THEM.
// `ProviderReflectiveFaultTaxonomyTests` pins the DIAGNOSTIC each guard composes (it names the
// step, the provider type, the member and the exception) and the VERDICT each produces through the
// runners. Neither is the marker: a guard whose `ProviderOrEngineFault(...)` wrapper is removed
// composes a byte-identical diagnostic and reaches an identical Inconclusive verdict, and only the
// exit code moves. This file asserts the one thing that moves.
//
// THE CONTROL ROWS ARE NOT DECORATION. A "fix" that simply set the marker on every
// `ValidationFailure` would pass all seven rows above and destroy the boundary the marker exists to
// draw — `ProviderOrEngineFault`'s own remarks state that boundary, and #514 is the open question
// of whether it should move. Three ordinary authoring failures assert it is still false: a model
// validation a provider REPORTED (the working case of the contract, not a defect in it), the
// registry-lookup internal error reached before any provider code runs, and the
// host-resource/service name collision the engine raises about the author's own document.
//
// NO CONTAINERS, NO DOCKER, NO ASPIRE: every fault here is raised inside `Compile`, which sits
// before any topology on every run path.

using System;
using System.Reflection;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Issue #480: <see cref="ValidationFailure.IsProviderOrEngineFault"/> is set at every one of the
/// seven guards that wrap provider code or provider-emitted content, and at none of the authoring
/// failures around them.
/// </summary>
public sealed class ProviderOrEngineFaultMarkerTests
{
    private static readonly Assembly[] ProviderAssemblies =
        new[] { typeof(ProviderOrEngineFaultMarkerTests).Assembly };

    private static readonly StepKindRegistry Registry =
        StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

    private const string SuiteNamespace = "Vouchfx.Generated.MarkerCensus";

    private static string SuiteFor(string stepType) => $"""
        environment:
          services:
            api:
              image: myorg/api:1.0
        steps:
          - id: will-throw
            type: {stepType}
        """;

    /// <summary>
    /// One row per guard: a fault raised inside provider code — or inside the assembler's refusal
    /// of what a provider emitted — carries the provenance marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>SEVEN ROWS FOR SEVEN CALL SITES, and the count is the assertion.</strong>
    /// <c>ProviderPipeline.ProviderOrEngineFault</c> is called from exactly these seven places and
    /// nowhere else; a row here per site is what makes deleting any one wrapper a red test rather
    /// than a silent regression to exit 0. MEASURED BY MUTATION, ALL SEVEN: with the
    /// <c>ProviderOrEngineFault(...)</c> wrapper removed from each guard in turn — so the failure
    /// went through the plain <c>Refuse</c>/<c>ValidationFailure</c> spelling and the diagnostic
    /// was byte-identical — exactly the matching row below failed, the other nine stayed green,
    /// and the build stayed clean at <c>-warnaserror</c>.
    /// </para>
    /// <para>
    /// <paramref name="diagnosticFragment"/> proves the row reached the guard it claims rather than
    /// some earlier door that happens to fail too. For the six per-step guards it is the SDK member
    /// name <c>DescribeProviderFault</c> writes; for the assembler it is
    /// <c>DescribeAssemblyFault</c>'s own opening clause, because that diagnostic deliberately
    /// names no step and no member — <c>CsxAssemblyException</c> carries neither.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("stub.throwing-bind", "Bind")]
    [InlineData("stub.throwing-validate", "Validate")]
    [InlineData("stub.throwing-hostresource", "HostResources")]
    // Leading space, deliberately: bare "Resources" is a substring of "HostResources", so a
    // regression routing this stub into the HostResources guard would still have satisfied it.
    [InlineData("stub.throwing-resources", " Resources threw")]
    [InlineData("stub.throwing-compilerefs", "CompileReferenceAssemblies")]
    [InlineData("stub.throwing-emit", "Emit")]
    [InlineData("stub.bad-fragment", "suite CSX assembly failed")]
    public void Compile_FaultFromAGuardedProviderSurface_CarriesTheProvenanceMarker(
        string stepType,
        string diagnosticFragment)
    {
        var ast = AstBuilder.Build(YamlDocumentParser.Parse(SuiteFor(stepType)), Registry);

        var result = ProviderPipeline.Compile(ast, Registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Null(result.Assembled);
        Assert.Contains(diagnosticFragment, result.Failure!.Message, StringComparison.Ordinal);

        Assert.True(
            result.Failure.IsProviderOrEngineFault,
            $"the guard reached by '{stepType}' dropped issue #480's provenance marker; this "
            + "defect would exit 0 again beside a passing sibling.");
    }

    /// <summary>
    /// A model-validation failure a provider REPORTED is an authoring failure, and must not carry
    /// the marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first of three control rows, and the one <c>ProviderOrEngineFault</c>'s own remarks call
    /// a judgement rather than an obvious exclusion: <c>ValidationResult.Errors</c> is
    /// provider-authored text reaching the author through the same channel as a guard's diagnostic.
    /// What separates them is that this is a provider REPORTING the author's model invalid — the
    /// working case of the v1 contract — while a guard fires because something came back out of
    /// provider code that no author can fix by editing the suite. Marking this would redden every
    /// mixed suite containing one mistyped field.
    /// </para>
    /// <para>
    /// It runs through the SAME <c>Compile</c> front door as the seven rows above, on the same
    /// registry, so the contrast is the failure's provenance and nothing about how it was reached.
    /// </para>
    /// </remarks>
    [Fact]
    public void Compile_ModelValidationFailure_DoesNotCarryTheProvenanceMarker()
    {
        const string yaml = """
            steps:
              - id: will-fail
                type: stub.failing
            """;

        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);

        var result = ProviderPipeline.Compile(ast, Registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Contains("stub validation error", result.Failure!.Message, StringComparison.Ordinal);

        Assert.False(
            result.Failure.IsProviderOrEngineFault,
            "a provider reporting an invalid model is the contract working, not a defect in it; "
            + "marking it would answer issue #514 silently.");
    }

    /// <summary>
    /// The registry-lookup internal error is raised before any provider code runs, and must not
    /// carry the marker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second control row, and the one that covers the branch <c>BindAllSteps</c> comments as
    /// DELIBERATELY unmarked. It is reached by building the AST against the full registry and then
    /// compiling against an EMPTY one — one way in rather than the only one (a hand-built
    /// <c>ScenarioAst</c> carrying an unregistered <c>StepNode</c> is another, the shape
    /// <c>ProviderPipelineTests.Compile_EmptyAst_</c>… uses), and the one chosen because it keeps
    /// the document realistic. <c>AstBuilder.Build</c> verifies
    /// every step type against the registry it is handed, so a single registry can never produce an
    /// AST whose type it lacks. The two-registry split has no precedent in this assembly; an
    /// earlier revision cited
    /// <c>ProviderPipelineTests.Compile_StepValidationFails_ReturnsFailureNonNull_AssembledNull</c>
    /// for it, and that test builds ONE registry and hands it to both <c>AstBuilder.Build</c> and
    /// <c>Compile</c>. What it shares with the rows here is building the AST directly, which is how
    /// every row in this file bypasses <c>DocumentValidator</c>; the split is this row's own.
    /// </para>
    /// <para>
    /// This row is the sharpest of the three: the failure names a PROVIDER and reads like a
    /// provider problem, which is precisely why "the diagnostic mentions a provider" is not a safe
    /// proxy for the marker. Nothing was dispatched into.
    /// </para>
    /// </remarks>
    [Fact]
    public void Compile_UnknownStepTypeAtBindTime_DoesNotCarryTheProvenanceMarker()
    {
        const string yaml = """
            steps:
              - id: will-not-resolve
                type: stub.alpha
            """;

        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);
        var emptyRegistry = StepKindRegistry.BuildAndFreeze(Array.Empty<IStepProvider>());

        var result = ProviderPipeline.Compile(ast, emptyRegistry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Contains("missing from registry", result.Failure!.Message, StringComparison.Ordinal);

        Assert.False(
            result.Failure.IsProviderOrEngineFault,
            "the registry is a frozen lookup and no provider code was entered on that line.");
    }

    /// <summary>
    /// The host-resource/declared-service name collision is the engine refusing the author's own
    /// document, and must not carry the marker.
    /// </summary>
    /// <remarks>
    /// The third control row, and the one that is NOT a per-step failure at all: it is raised from
    /// <c>Compile</c>'s own body against <c>BuildProjectContext</c>'s <c>out</c> parameter, between
    /// Pass 1 and Pass 2 — a door that belongs to neither, and the property worth pinning, since a
    /// refusal there is reachable before any per-step guard has run. An earlier revision justified
    /// this row by the <c>Refuse</c> OVERLOAD it takes, claiming the other two controls would miss
    /// a widening of the message overload; that was false and is recorded rather than deleted.
    /// Control 1 goes through the message overload and control 2 through this same
    /// <c>ValidationFailure</c> one, so the overload buys no coverage this row alone has — as the
    /// measured sentence below has always said. MEASURED BY MUTATION: setting the marker inside
    /// <c>Refuse(ValidationFailure)</c> — the one edit that satisfies all seven rows above with a
    /// single line, since every failure in the file funnels through it — reddens this row and the
    /// other two controls, and nothing else.
    /// </remarks>
    [Fact]
    public void Compile_HostResourceCollidesWithADeclaredService_DoesNotCarryTheProvenanceMarker()
    {
        const string yaml = """
            environment:
              services:
                cb:
                  image: myorg/callback-target:1.0
            steps:
              - id: listen-step
                type: stub.listener
            """;

        var ast = AstBuilder.Build(YamlDocumentParser.Parse(yaml), Registry);

        var result = ProviderPipeline.Compile(ast, Registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Contains("listen-step", result.Failure!.Message, StringComparison.Ordinal);

        Assert.False(
            result.Failure.IsProviderOrEngineFault,
            "a name collision between a listener and a declared service is fixed by editing the "
            + "suite, which is the definition of an authoring failure.");
    }
}
