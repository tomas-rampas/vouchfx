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

// Throwing Bind — verifies the pre-pass swallow-catch inside
// ProviderPipeline.BuildProjectContext (S2, security MINOR-1). Also the "throwing-stub
// pattern" G6(b)'s HostResources-throws stub below reuses, per the review's own note.
[StepProvider]
file sealed class StubThrowingBindProvider
    : IStepProvider,
      IStepBinder<AlphaModel>,
      IStepValidator<AlphaModel>,
      IStepCompiler<AlphaModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "throwing-bind");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment =>
        new("""{"type":"object"}""");

    public AlphaModel Bind(YamlNode node, IBindingContext ctx) =>
        throw new InvalidOperationException("stub: Bind always throws.");

    public ValidationResult Validate(AlphaModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(AlphaModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: $"{{ /* throwing-bind step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");
}

// Throwing HostResources — G6 (gatekeeper MAJOR-6b): the LAZY-ITERATOR variant of the
// same swallow-catch proof. Bind succeeds; HostResources() is a real C# iterator (yield
// return) whose body — and so HostResourceRequirement's own ctor validation — does not
// run until the caller's foreach starts enumerating it, exactly the shape that used to
// sit OUTSIDE BuildProjectContext's try/catch.
[StepProvider]
file sealed class StubThrowingHostResourceProvider
    : IStepProvider,
      IStepBinder<AlphaModel>,
      IStepValidator<AlphaModel>,
      IStepCompiler<AlphaModel>,
      IHostResourceContributor<AlphaModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "throwing-hostresource");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment =>
        new("""{"type":"object"}""");

    public AlphaModel Bind(YamlNode node, IBindingContext ctx) =>
        new AlphaModel(Tag: "throwing-hostresource-tag");

    public ValidationResult Validate(AlphaModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(AlphaModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: $"{{ /* throwing-hostresource step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    public IEnumerable<HostResourceRequirement> HostResources(AlphaModel model)
    {
        // Lazy iterator: nothing below runs until MoveNext() is called on the enumerator
        // this method returns — HostResourceRequirement's own ctor validation
        // (ArgumentException.ThrowIfNullOrEmpty on Kind/VarName) throws right here,
        // mid-enumeration, on an unvalidated model — reachable by a community provider
        // whose Bind() legitimately produces a "safe empty" model with an absent field.
        yield return new HostResourceRequirement(Kind: string.Empty, VarName: string.Empty);
    }
}

// Host resource named identically to a DECLARED SERVICE — G5 (gatekeeper MAJOR-5):
// proves the service/listener name-collision guard in BuildProjectContext/Compile.
[StepProvider]
file sealed class StubListenerProvider
    : IStepProvider,
      IStepBinder<AlphaModel>,
      IStepValidator<AlphaModel>,
      IStepCompiler<AlphaModel>,
      IHostResourceContributor<AlphaModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "listener");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment =>
        new("""{"type":"object"}""");

    public AlphaModel Bind(YamlNode node, IBindingContext ctx) =>
        new AlphaModel(Tag: "listener-tag");

    public ValidationResult Validate(AlphaModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(AlphaModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: $"{{ /* listener step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    // Fixed VarName "cb" regardless of the model — this stub exists solely to prove the
    // collision guard, mirroring the other stubs' own hardcoded-Tag minimalism.
    public IEnumerable<HostResourceRequirement> HostResources(AlphaModel model) =>
        new[] { new HostResourceRequirement(Kind: "webhook-listener", VarName: "cb") };
}

// m1 fix (fix round 2): a host resource named identically to a dependency's own SIDECAR
// svc:: key (a mailpit dependency's "-smtp" suffix) — proves the collision guard's fixed
// name set now covers dependency sidecars, not only declared services. Fixed VarName
// "mail-smtp" mirrors StubListenerProvider's own hardcoded-VarName minimalism.
[StepProvider]
file sealed class StubMailSmtpListenerProvider
    : IStepProvider,
      IStepBinder<AlphaModel>,
      IStepValidator<AlphaModel>,
      IStepCompiler<AlphaModel>,
      IHostResourceContributor<AlphaModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "mail-smtp-listener");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment =>
        new("""{"type":"object"}""");

    public AlphaModel Bind(YamlNode node, IBindingContext ctx) =>
        new AlphaModel(Tag: "mail-smtp-listener-tag");

    public ValidationResult Validate(AlphaModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(AlphaModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: $"{{ /* mail-smtp-listener step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    public IEnumerable<HostResourceRequirement> HostResources(AlphaModel model) =>
        new[] { new HostResourceRequirement(Kind: "webhook-listener", VarName: "mail-smtp") };
}

// m1 fix (fix round 2) — the kafka-schema-registry sibling of
// StubMailSmtpListenerProvider: proves a host resource named identically to a kafka
// dependency's OWN "-sr" schema-registry sidecar is caught too.
[StepProvider]
file sealed class StubBusSrListenerProvider
    : IStepProvider,
      IStepBinder<AlphaModel>,
      IStepValidator<AlphaModel>,
      IStepCompiler<AlphaModel>,
      IHostResourceContributor<AlphaModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("stub", "bus-sr-listener");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment =>
        new("""{"type":"object"}""");

    public AlphaModel Bind(YamlNode node, IBindingContext ctx) =>
        new AlphaModel(Tag: "bus-sr-listener-tag");

    public ValidationResult Validate(AlphaModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(AlphaModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: Array.Empty<string>(),
            RequiredHelpers: Array.Empty<string>(),
            StatementBlock: $"{{ /* bus-sr-listener step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    public IEnumerable<HostResourceRequirement> HostResources(AlphaModel model) =>
        new[] { new HostResourceRequirement(Kind: "webhook-listener", VarName: "bus-sr") };
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

    // ── Test: RunProjectContext.DeclaredServices (services-generalisation, REQ-010) ──

    /// <summary>
    /// <see cref="RunProjectContext"/> constructed with a services map exposes it via
    /// <see cref="Vouchfx.Sdk.IProjectContext.DeclaredServices"/> — mirrors
    /// <see cref="RunProjectContext_WithDependencies_ExposesMapViaInterface"/> exactly,
    /// for the new sibling member.
    /// </summary>
    [Fact]
    public void RunProjectContext_WithServices_ExposesMapViaInterface()
    {
        var services = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["kafka-broker"] = new List<string> { "tcp-9093" },
            ["api"] = new List<string> { "http" },
        };

        Vouchfx.Sdk.IProjectContext ctx = new RunProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal),
            Directory.GetCurrentDirectory(),
            services);

        Assert.Equal(2, ctx.DeclaredServices.Count);
        Assert.Equal("tcp-9093", Assert.Single(ctx.DeclaredServices["kafka-broker"]));
        Assert.Equal("http", Assert.Single(ctx.DeclaredServices["api"]));
    }

    /// <summary>
    /// <see cref="RunProjectContext.Empty"/> exposes an empty
    /// <see cref="Vouchfx.Sdk.IProjectContext.DeclaredServices"/> map too.
    /// </summary>
    [Fact]
    public void RunProjectContext_Empty_HasNoDeclaredServices()
    {
        Vouchfx.Sdk.IProjectContext ctx = RunProjectContext.Empty(Directory.GetCurrentDirectory());

        Assert.Empty(ctx.DeclaredServices);
    }

    /// <summary>
    /// REQ-010 acceptance criterion: <see cref="ProviderPipeline.BuildProjectContext"/> —
    /// the SAME wiring <see cref="ProviderPipeline.Compile"/> uses internally — derives
    /// <see cref="Vouchfx.Sdk.IProjectContext.DeclaredServices"/> directly from the parsed
    /// AST's <c>environment.services</c> section, for a suite with one declared service.
    /// Exercised through the REAL <c>YamlDocumentParser</c> → <c>AstBuilder</c> pipeline, not
    /// a hand-built <see cref="Vouchfx.Engine.Authoring.Model.EnvironmentSpec"/>.
    /// </summary>
    [Fact]
    public void BuildProjectContext_SuiteWithOneDeclaredService_DeclaredServicesContainsItsName()
    {
        const string yaml = """
            environment:
              services:
                kafka-broker:
                  image: myorg/kafka-broker:1.0
                  ports: [9093]
            steps:
              - id: step-alpha
                type: stub.alpha
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        // M5 fix (fix round 2): BuildProjectContext no longer binds — it reads the
        // already-bound BoundStep list from ProviderPipeline.BindAllSteps (Compile's own
        // Pass 1), never re-binding a step a second time.
        var (boundSteps, registryFailure) = ProviderPipeline.BindAllSteps(ast, s_registry);
        Assert.Null(registryFailure);
        var ctx = ProviderPipeline.BuildProjectContext(
            ast, Directory.GetCurrentDirectory(), boundSteps, out _);

        Assert.True(ctx.DeclaredServices.ContainsKey("kafka-broker"));
        Assert.Equal("tcp-9093", Assert.Single(ctx.DeclaredServices["kafka-broker"]));
    }

    /// <summary>
    /// The sibling default-HTTP-shape case: a service declared with no <c>ports:</c> gets
    /// the implicit <c>["http"]</c> endpoint name — the SAME name
    /// <c>EnvironmentMapper</c> builds the actual Aspire endpoint under (see
    /// <c>ServiceEndpointNaming</c>'s own remarks on why the two share one convention).
    /// </summary>
    [Fact]
    public void BuildProjectContext_HttpOnlyService_DeclaredServicesListsHttpEndpoint()
    {
        const string yaml = """
            environment:
              services:
                web:
                  image: traefik/whoami
            steps:
              - id: step-alpha
                type: stub.alpha
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        var (boundSteps, registryFailure) = ProviderPipeline.BindAllSteps(ast, s_registry);
        Assert.Null(registryFailure);
        var ctx = ProviderPipeline.BuildProjectContext(
            ast, Directory.GetCurrentDirectory(), boundSteps, out _);

        Assert.Equal("http", Assert.Single(ctx.DeclaredServices["web"]));
    }

    // ── Test: single-Bind-per-step, no swallow-catch (M5 fix, fix round 2) ───

    /// <summary>
    /// M5 fix (fix round 2, PR #349 follow-up): <c>Bind</c> is now called EXACTLY once per
    /// step, in <see cref="ProviderPipeline.BindAllSteps"/> (Compile's own Pass 1) — there is
    /// no longer a separate speculative pre-pass, so there is nothing left to swallow a
    /// throwing <c>Bind</c> into. A step whose provider's <c>Bind</c> always throws now
    /// propagates that exception out of <see cref="ProviderPipeline.BindAllSteps"/> directly
    /// — exactly the same "no purity assumption beyond what already holds" contract the
    /// pre-M5 MAIN loop's own (always unguarded) <c>Bind</c> call already had; only the
    /// now-removed speculative pre-pass ever silently ate this. <c>MethodInfo.Invoke</c>
    /// wraps the provider's own thrown exception in <see cref="TargetInvocationException"/>.
    /// </summary>
    [Fact]
    public void BindAllSteps_StepBindThrows_PropagatesTargetInvocationException()
    {
        const string yaml = """
            environment:
              services:
                svc:
                  image: myorg/svc:1.0
            steps:
              - id: will-throw
                type: stub.throwing-bind
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => ProviderPipeline.BindAllSteps(ast, s_registry));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Bind always throws", ex.InnerException!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// M5 fix (fix round 2) — the LAZY-ITERATOR sibling of
    /// <see cref="BindAllSteps_StepBindThrows_PropagatesTargetInvocationException"/>: a step
    /// whose <c>Bind</c> SUCCEEDS but whose <c>HostResources()</c> enumerator throws (from
    /// <c>HostResourceRequirement</c>'s own ctor validation) now propagates directly too —
    /// <see cref="ProviderPipeline.BindAllSteps"/> materialises the host-resource list via
    /// <c>.ToList()</c> (plain LINQ, not reflection <c>Invoke</c>), so the exception surfaces
    /// UNWRAPPED, as the provider actually threw it.
    /// </summary>
    [Fact]
    public void BindAllSteps_StepHostResourcesThrows_PropagatesUnwrapped()
    {
        const string yaml = """
            environment:
              services:
                svc:
                  image: myorg/svc:1.0
            steps:
              - id: will-throw
                type: stub.throwing-hostresource
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        Assert.Throws<ArgumentException>(() => ProviderPipeline.BindAllSteps(ast, s_registry));
    }

    // ── Test: service/listener name-collision guard (G5) ─────────────────────

    /// <summary>
    /// G5 (gatekeeper MAJOR-5): a webhook-listen-shaped host resource named identically to
    /// a DECLARED SERVICE must be rejected at compile time, not silently allowed to shadow
    /// it. <c>ScenarioRunner</c> stages every host resource under
    /// <c>svc::&lt;VarName&gt;</c> — the SAME Vars key a declared service's endpoint is
    /// staged under — so an undetected collision means an <c>http.rest</c> step targeting
    /// <c>cb</c> could silently talk to the listener instead of the service it thinks it
    /// declared, and Pass having exercised nothing but the engine's own listener. Before the
    /// fix, <see cref="ProviderPipeline.BuildProjectContext"/>'s unconditional
    /// <c>serviceMap[hostReq.VarName] = ...</c> silently overwrote the declared service's
    /// endpoint names with the listener's, and <see cref="ProviderPipeline.Compile"/> never
    /// surfaced any failure at all.
    /// </summary>
    [Fact]
    public void Compile_HostResourceNameCollidesWithDeclaredService_FailsNamingBothSurfaces()
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

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Contains("cb", result.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("service", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("listen-step", result.Failure.Message, StringComparison.Ordinal);
        Assert.Null(result.Assembled);
    }

    /// <summary>
    /// m1 fix (fix round 2, PR #349 follow-up): a host resource named identically to a
    /// MAILPIT DEPENDENCY's own SMTP sidecar key must be rejected too — before this fix, the
    /// collision guard checked only declared-service names, on the (wrong) stated reasoning
    /// that dependencies never stage a svc::-shaped key; a listener named <c>mail-smtp</c>
    /// alongside a <c>mailpit</c> dependency <c>mail</c> validated PASS and would have
    /// silently shadowed the dependency's own SMTP sidecar endpoint.
    /// </summary>
    [Fact]
    public void Compile_HostResourceNameCollidesWithMailpitSmtpSidecar_FailsNamingBothSurfaces()
    {
        const string yaml = """
            environment:
              dependencies:
                mail:
                  type: mailpit
            steps:
              - id: listen-step
                type: stub.mail-smtp-listener
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Contains("mail-smtp", result.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("mail", result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("listen-step", result.Failure.Message, StringComparison.Ordinal);
        Assert.Null(result.Assembled);
    }

    /// <summary>
    /// m1 fix (fix round 2) — the kafka-schema-registry sibling of
    /// <see cref="Compile_HostResourceNameCollidesWithMailpitSmtpSidecar_FailsNamingBothSurfaces"/>:
    /// a listener named <c>bus-sr</c> alongside a <c>kafka</c> dependency <c>bus</c> with
    /// <c>schemaRegistry: true</c> must be rejected — the reviewer's own measured
    /// consequence of the pre-fix gap: the <c>-sr</c> key is read at run time by both Kafka
    /// providers, so an Avro publish would have sent schema-registry traffic to the engine's
    /// own listener instead of the real registry.
    /// </summary>
    [Fact]
    public void Compile_HostResourceNameCollidesWithKafkaSchemaRegistrySidecar_FailsNamingBothSurfaces()
    {
        const string yaml = """
            environment:
              dependencies:
                bus:
                  type: kafka
                  schemaRegistry: true
            steps:
              - id: listen-step
                type: stub.bus-sr-listener
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Contains("bus-sr", result.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("bus", result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("listen-step", result.Failure.Message, StringComparison.Ordinal);
        Assert.Null(result.Assembled);
    }

    /// <summary>
    /// m7 fix (fix round 2, PR #349 follow-up): a service and a dependency may not share a
    /// name. Before this fix, a suite declaring both <c>environment.services.orders</c> and
    /// <c>environment.dependencies.orders</c> validated PASS and only failed later, deep
    /// inside Aspire's own <c>AddContainer</c> ("a resource with the same name already
    /// exists") — an opaque failure at topology-build time rather than a located authoring
    /// diagnostic at validate time.
    /// </summary>
    [Fact]
    public void Compile_ServiceAndDependencyShareName_FailsNamingBoth()
    {
        const string yaml = """
            environment:
              services:
                orders:
                  image: myorg/orders-api:1.0
              dependencies:
                orders:
                  type: postgres
            steps:
              - id: step-alpha
                type: stub.alpha
            """;

        var doc = YamlDocumentParser.Parse(yaml);
        var ast = AstBuilder.Build(doc, s_registry);

        var result = ProviderPipeline.Compile(ast, s_registry, SuiteNamespace);

        Assert.NotNull(result.Failure);
        Assert.Contains("orders", result.Failure!.Message, StringComparison.Ordinal);
        Assert.Contains("service", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dependency", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Assembled);
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
