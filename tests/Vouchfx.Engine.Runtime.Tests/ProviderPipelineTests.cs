// Tests for ProviderPipeline (S04-B-01).
//
// Verifies:
//   • Happy-path: assembled CsxSource contains both helper classes once
//     (deduplication), both usings once, both statement blocks in order.
//   • ResourcePlan: the provider that implements IResourceContributor contributes
//     its ResourceRequirement; the provider without it contributes nothing (tolerant).
//   • CompileReferencePaths: the provider implementing ICompileReferenceContributor
//     contributes the expected assembly location (once, deduplicated).
//   • Validation failure: a stub that returns Failure causes PipelineResult.Failure
//     to be non-null and Assembled to be null (Inconclusive path).
//
// All tests are non-docker.  No topology is started.  Stub providers are defined
// with file scope and declared in this file only.

using System.IO;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Tests;

// ── File-scoped stub provider infrastructure ──────────────────────────────────
//
// Two stub step kinds:
//   stub.alpha  — implements IResourceContributor + ICompileReferenceContributor
//   stub.beta   — does NOT implement either optional interface
//
// Both emit distinct provider-id-prefixed helper classes and distinct usings.

file sealed record AlphaModel(string Tag) : IStepModel;
file sealed record BetaModel(string Tag) : IStepModel;

[StepProvider]
file sealed class StubAlphaProvider
    : IStepProvider,
      IStepBinder<AlphaModel>,
      IStepValidator<AlphaModel>,
      IStepCompiler<AlphaModel>,
      IResourceContributor<AlphaModel>,
      ICompileReferenceContributor
{
    private static readonly string[] s_authors = new[] { "test" };
    private static readonly string[] s_usings = new[] { "System.Collections.Generic" };
    private static readonly string[] s_helpers = new[]
    {
        "internal static class StubAlpha_Helpers { public static void Noop() { } }"
    };
    private static readonly ResourceRequirement[] s_resources = new[]
    {
        new ResourceRequirement("test-family", "alpha-resource", null)
    };
    private static readonly System.Reflection.Assembly[] s_compileRefs = new[]
    {
        typeof(System.Uri).Assembly  // System.Private.Uri
    };

    public StepKindId Kind => new("stub", "alpha");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment =>
        new("""{"type":"object"}""");

    public AlphaModel Bind(YamlNode node, IBindingContext ctx) =>
        new AlphaModel(Tag: "alpha-tag");

    public ValidationResult Validate(AlphaModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(AlphaModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: s_helpers,
            StatementBlock: $"{{ /* alpha step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    public IEnumerable<ResourceRequirement> Resources(AlphaModel model) => s_resources;

    public System.Collections.Generic.IEnumerable<System.Reflection.Assembly>
        CompileReferenceAssemblies => s_compileRefs;
}

[StepProvider]
file sealed class StubBetaProvider
    : IStepProvider,
      IStepBinder<BetaModel>,
      IStepValidator<BetaModel>,
      IStepCompiler<BetaModel>
{
    private static readonly string[] s_authors = new[] { "test" };
    private static readonly string[] s_usings = new[] { "System.Collections.Generic", "System.Linq" };
    private static readonly string[] s_helpers = new[]
    {
        "internal static class StubBeta_Helpers { public static void Noop() { } }"
    };

    public StepKindId Kind => new("stub", "beta");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment =>
        new("""{"type":"object"}""");

    public BetaModel Bind(YamlNode node, IBindingContext ctx) =>
        new BetaModel(Tag: "beta-tag");

    public ValidationResult Validate(BetaModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(BetaModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: s_helpers,
            StatementBlock: $"{{ /* beta step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");
}

// Failing validator — used to verify the Inconclusive path.
[StepProvider]
file sealed class StubFailingProvider
    : IStepProvider,
      IStepBinder<AlphaModel>,
      IStepValidator<AlphaModel>,
      IStepCompiler<AlphaModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "failing");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment =>
        new("""{"type":"object"}""");

    public AlphaModel Bind(YamlNode node, IBindingContext ctx) =>
        new AlphaModel(Tag: "will-fail");

    public ValidationResult Validate(AlphaModel model, IProjectContext ctx) =>
        ValidationResult.Failure("stub validation error: intentional failure");

    public CsxFragment Emit(AlphaModel model, ICompileContext ctx) =>
        throw new InvalidOperationException("Should not reach Emit after Validate fails.");
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="ProviderPipeline"/>.
/// </summary>
public sealed class ProviderPipelineTests
{
    // Assemblies to scan: this test assembly contains all stub [StepProvider] types.
    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
        new[] { typeof(ProviderPipelineTests).Assembly };

    // Registry covering all stub kinds (alpha, beta, failing).
    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "TestSuite";

    // ── Happy-path helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal 2-step AST with <c>stub.alpha</c> and <c>stub.beta</c>
    /// step kinds, using the live parser + AstBuilder.
    /// </summary>
    private static ScenarioAst BuildTwoStepAst()
    {
        const string yaml = """
            steps:
              - id: step-alpha
                type: stub.alpha
              - id: step-beta
                type: stub.beta
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        return AstBuilder.Build(doc, s_registry);
    }

    // ── Test: CsxSource contains both helpers once, both usings, both blocks ──

    /// <summary>
    /// The assembled <c>CsxSource</c> must contain each helper class exactly once
    /// (deduplication), all usings without repeats, and both statement blocks in
    /// the correct step order.
    /// </summary>
    [Fact]
    public void Compile_TwoStepAst_AssembledSourceContainsBothHelpersAndUsingsAndBlocks()
    {
        var ast = BuildTwoStepAst();

        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);

        var src = result.Assembled!.CsxSource;

        // Both helper classes present exactly once.
        Assert.Contains("StubAlpha_Helpers", src, StringComparison.Ordinal);
        Assert.Contains("StubBeta_Helpers", src, StringComparison.Ordinal);

        // Helper class deduplication: each class name appears only once.
        Assert.Equal(
            1,
            CountOccurrences(src, "StubAlpha_Helpers"));
        Assert.Equal(
            1,
            CountOccurrences(src, "StubBeta_Helpers"));

        // Usings from both providers are emitted.
        Assert.Contains("using System.Collections.Generic;", src, StringComparison.Ordinal);
        Assert.Contains("using System.Linq;", src, StringComparison.Ordinal);

        // "System.Collections.Generic" using is deduplicated (both providers declare it).
        Assert.Equal(
            1,
            CountOccurrences(src, "using System.Collections.Generic;"));

        // Both statement blocks are present.
        Assert.Contains("step_alpha", src, StringComparison.Ordinal);  // sanitised id
        Assert.Contains("step_beta", src, StringComparison.Ordinal);

        // No "using var" in the assembled source.
        Assert.DoesNotContain("using var", src, StringComparison.Ordinal);

        // StepIds preserves insertion order.
        Assert.Collection(
            result.Assembled.StepIds,
            id => Assert.Equal("step-alpha", id),
            id => Assert.Equal("step-beta", id));
    }

    // ── Test: ResourcePlan ────────────────────────────────────────────────────

    /// <summary>
    /// Only the alpha provider (which implements <see cref="IResourceContributor{TModel}"/>)
    /// should contribute to the resource plan.  The beta provider (which does not)
    /// must silently contribute nothing — tolerant behaviour.
    /// </summary>
    [Fact]
    public void Compile_TwoStepAst_ResourcePlanContainsOnlyAlphaContribution()
    {
        var ast = BuildTwoStepAst();

        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.Null(result.Failure);

        // Exactly one entry from the alpha step.
        Assert.Single(result.ResourcePlan);

        var entry = result.ResourcePlan[0];
        Assert.Equal("step-alpha", entry.StepId);
        Assert.Equal("test-family", entry.Requirement.Family);
        Assert.Equal("alpha-resource", entry.Requirement.Name);
        Assert.Null(entry.Requirement.Image);
        Assert.Contains("StubAlphaProvider", entry.ProviderTypeName,
            StringComparison.Ordinal);
    }

    // ── Test: CompileReferencePaths ───────────────────────────────────────────

    /// <summary>
    /// The alpha provider implements <see cref="ICompileReferenceContributor"/> and
    /// declares <c>typeof(System.Uri).Assembly</c>.  The beta provider does not
    /// implement the interface.  The pipeline should collect exactly the Uri assembly
    /// location, deduplicated.
    /// </summary>
    [Fact]
    public void Compile_TwoStepAst_CompileReferencePathsContainsUriAssembly()
    {
        var ast = BuildTwoStepAst();

        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.Null(result.Failure);

        var uriAssemblyLocation = typeof(System.Uri).Assembly.Location;
        Assert.NotEmpty(uriAssemblyLocation);

        Assert.Contains(uriAssemblyLocation, result.CompileReferencePaths,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Even when the same step kind is used twice, the compile-reference path for
    /// that provider's assembly should appear only once (deduplication by location).
    /// </summary>
    [Fact]
    public void Compile_SameProviderTwice_CompileReferencePathsDeduplicated()
    {
        const string yaml = """
            steps:
              - id: step-alpha-1
                type: stub.alpha
              - id: step-alpha-2
                type: stub.alpha
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.Null(result.Failure);

        var uriAssemblyLocation = typeof(System.Uri).Assembly.Location;

        // The path must appear exactly once.
        var matchCount = result.CompileReferencePaths
            .Count(p => string.Equals(p, uriAssemblyLocation, StringComparison.Ordinal));

        Assert.Equal(1, matchCount);
    }

    // ── Test: Validation failure → PipelineResult.Failure non-null ────────────

    /// <summary>
    /// When the validator for a step returns a failure, <see cref="ProviderPipeline.Compile"/>
    /// must return a <see cref="PipelineResult"/> with <see cref="PipelineResult.Failure"/>
    /// set (the Inconclusive path) and <see cref="PipelineResult.Assembled"/> null.
    /// </summary>
    [Fact]
    public void Compile_StepValidationFails_ReturnsFailureNonNull_AssembledNull()
    {
        // Build a registry with only the failing provider.
        var failRegistry = StepKindRegistry.BuildAndFreeze(
            new IStepProvider[] { new StubFailingProvider() });

        // Build the AST directly so we can bypass DocumentValidator (which checks the
        // registry's step-kind catalogue, and the failing provider is registered there).
        const string yaml = """
            steps:
              - id: will-fail
                type: stub.failing
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, failRegistry);

        var result = ProviderPipeline.Compile(ast, failRegistry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Contains("stub validation error", result.Failure.Message,
            StringComparison.Ordinal);
        Assert.Contains("will-fail", result.Failure.Message,
            StringComparison.Ordinal);
        Assert.Null(result.Assembled);

        // Critic MAJOR-3: an ORDINARY pipeline failure (a step's own model-validation
        // failure, as here) must NOT carry IsSecurityPreflight — only a failure raised by
        // EnvironmentSecurityValidator itself does (see
        // Vouchfx.Engine.Runtime.Tests.EnvironmentSecurityValidatorTests.Validate_Failure_CarriesSecurityPreflightMarker
        // for the positive case).
        Assert.False(result.Failure.IsSecurityPreflight,
            "An ordinary step-validation failure must not carry the security-preflight marker.");
    }

    // ── Test: empty AST produces empty assembled script ───────────────────────

    /// <summary>
    /// An AST with no steps must produce a non-null result with an empty assembled
    /// source and empty resource plan and compile references.
    /// </summary>
    [Fact]
    public void Compile_EmptyAst_ReturnsEmptyAssembledScriptNoFailure()
    {
        // Construct the AST directly with an empty steps list — we need a valid
        // registry for the registry lookup but no steps will be processed.
        var ast = new ScenarioAst(
            Metadata: null,
            Environment: null,
            Variables: new Dictionary<string, string>(StringComparer.Ordinal),
            Steps: Array.Empty<Vouchfx.Engine.Authoring.Ast.StepNode>());

        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        Assert.Equal(string.Empty, result.Assembled!.CsxSource);
        Assert.Empty(result.ResourcePlan);
        Assert.Empty(result.CompileReferencePaths);
    }

    // ── Test: RunProjectContext.DeclaredDependencies (Sprint-4) ──────────────

    /// <summary>
    /// <see cref="RunProjectContext"/> constructed with a dependency map exposes
    /// the map via <see cref="Vouchfx.Sdk.IProjectContext.DeclaredDependencies"/>.
    /// </summary>
    [Fact]
    public void RunProjectContext_WithDependencies_ExposesMapViaInterface()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orders-db"] = "postgres",
            ["events"] = "kafka",
        };

        // Access through the public IProjectContext interface, as providers do.
        Vouchfx.Sdk.IProjectContext ctx = new RunProjectContext(deps, Directory.GetCurrentDirectory());

        Assert.Equal(2, ctx.DeclaredDependencies.Count);
        Assert.Equal("postgres", ctx.DeclaredDependencies["orders-db"]);
        Assert.Equal("kafka", ctx.DeclaredDependencies["events"]);
    }

    /// <summary>
    /// <see cref="RunProjectContext.Empty"/> exposes an empty
    /// <see cref="Vouchfx.Sdk.IProjectContext.DeclaredDependencies"/> map.
    /// </summary>
    [Fact]
    public void RunProjectContext_Empty_HasNoDeclaredDependencies()
    {
        Vouchfx.Sdk.IProjectContext ctx = RunProjectContext.Empty(Directory.GetCurrentDirectory());

        Assert.Empty(ctx.DeclaredDependencies);
    }

    // ── Private helper ────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }
        return count;
    }
}
