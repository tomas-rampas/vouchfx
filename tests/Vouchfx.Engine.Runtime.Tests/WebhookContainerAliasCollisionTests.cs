// Tests for the SUT configuration surface (point 3): ScenarioRunner ALSO stages a
// container-reachable alias of every webhook listener's callback URL under
// "<VarName>_container" (see ScenarioRunner.ContainerVarSuffix). ProviderPipeline.Compile
// must reject — before the topology is even built — a suite where that engine-synthesised
// alias collides with another, DISTINCT listener's own VarName; otherwise the two Vars writes
// would race and one listener's real callback URL would silently be replaced by an unrelated
// listener's container-rewritten alias.
//
// All tests are non-docker: only ProviderPipeline.Compile runs (no topology, no Roslyn
// compilation beyond CsxAssembler.Assemble, which Compile already performs internally).

using System.Collections.Generic;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Tests;

// ── File-scoped stub provider — a webhook-listener host resource whose VarName is bound
//    from a step-level 'varName' field, so tests can construct colliding VarName pairs. ──

file sealed record ConfigurableListenerModel(string VarName) : IStepModel;

[StepProvider]
file sealed class ConfigurableListenerProvider
    : IStepProvider,
      IStepBinder<ConfigurableListenerModel>,
      IStepValidator<ConfigurableListenerModel>,
      IStepCompiler<ConfigurableListenerModel>,
      IHostResourceContributor<ConfigurableListenerModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("hostres2", "listener");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object","required":["varName"],"properties":{"varName":{"type":"string"}}}""");

    public ConfigurableListenerModel Bind(YamlNode node, IBindingContext ctx)
    {
        var mapping = (YamlMappingNode)node;
        var varNameNode = (YamlScalarNode)mapping.Children[new YamlScalarNode("varName")];
        return new ConfigurableListenerModel(varNameNode.Value!);
    }

    public ValidationResult Validate(ConfigurableListenerModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(ConfigurableListenerModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: System.Array.Empty<string>(),
            RequiredHelpers: System.Array.Empty<string>(),
            StatementBlock: $"{{ /* listener step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    public IEnumerable<HostResourceRequirement> HostResources(ConfigurableListenerModel model) =>
        new[] { new HostResourceRequirement("webhook-listener", model.VarName) };
}

// ── File-scoped stub provider — an otlp-receiver host resource, structurally identical to
//    ConfigurableListenerProvider but a DIFFERENT kind, so tests can construct a cross-kind
//    VarName collision (a webhook listener and an OTLP receiver sharing one name). ──

file sealed record ConfigurableReceiverModel(string VarName) : IStepModel;

[StepProvider]
file sealed class ConfigurableReceiverProvider
    : IStepProvider,
      IStepBinder<ConfigurableReceiverModel>,
      IStepValidator<ConfigurableReceiverModel>,
      IStepCompiler<ConfigurableReceiverModel>,
      IHostResourceContributor<ConfigurableReceiverModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("hostres2", "receiver");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object","required":["varName"],"properties":{"varName":{"type":"string"}}}""");

    public ConfigurableReceiverModel Bind(YamlNode node, IBindingContext ctx)
    {
        var mapping = (YamlMappingNode)node;
        var varNameNode = (YamlScalarNode)mapping.Children[new YamlScalarNode("varName")];
        return new ConfigurableReceiverModel(varNameNode.Value!);
    }

    public ValidationResult Validate(ConfigurableReceiverModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(ConfigurableReceiverModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: System.Array.Empty<string>(),
            RequiredHelpers: System.Array.Empty<string>(),
            StatementBlock: $"{{ /* receiver step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    public IEnumerable<HostResourceRequirement> HostResources(ConfigurableReceiverModel model) =>
        new[] { new HostResourceRequirement("otlp-receiver", model.VarName) };
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for the <c>ProviderPipeline.Compile</c> guard against a webhook
/// listener <c>VarName</c> colliding with the engine-synthesised <c>"&lt;VarName&gt;_container"</c>
/// alias (SUT configuration surface, point 3).
/// </summary>
public sealed class WebhookContainerAliasCollisionTests
{
    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
        new[] { typeof(WebhookContainerAliasCollisionTests).Assembly };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "TestSuite";

    private static ScenarioAst BuildAst(string yaml)
    {
        var doc = YamlDocumentParser.Parse(yaml);
        return AstBuilder.Build(doc, s_registry);
    }

    [Fact]
    public void Compile_ListenerVarNameCollidesWithContainerAlias_ReturnsValidationFailure()
    {
        // Arrange — "cb_container" collides with the engine-synthesised alias of "cb".
        const string yaml = """
            steps:
              - id: listen-a
                type: hostres2.listener
                varName: cb
              - id: listen-b
                type: hostres2.listener
                varName: cb_container
            """;

        // Act
        var result = ProviderPipeline.Compile(BuildAst(yaml), s_registry, SuiteNamespace);

        // Assert — a ValidationFailure (never a thrown exception, never a silently-built
        // Assembled script) so the caller maps it to Verdict.Inconclusive (authoring error).
        Assert.NotNull(result.Failure);
        Assert.Null(result.Assembled);
        Assert.Contains("cb_container", result.Failure!.Message, System.StringComparison.Ordinal);
        Assert.Contains("cb", result.Failure.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ListenerVarNamesDoNotCollide_Succeeds()
    {
        // Arrange — two unrelated listener names; no alias collision.
        const string yaml = """
            steps:
              - id: listen-a
                type: hostres2.listener
                varName: cb
              - id: listen-b
                type: hostres2.listener
                varName: other
            """;

        // Act
        var result = ProviderPipeline.Compile(BuildAst(yaml), s_registry, SuiteNamespace);

        // Assert
        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        Assert.Equal(2, result.HostResourcePlan.Count);
    }

    [Fact]
    public void Compile_SingleListener_NoSelfCollision_Succeeds()
    {
        // Arrange — a lone listener never collides with its own synthesised alias (the suffix
        // is never empty, so "<x>" can never equal "<x>_container").
        const string yaml = """
            steps:
              - id: listen-a
                type: hostres2.listener
                varName: cb
            """;

        // Act
        var result = ProviderPipeline.Compile(BuildAst(yaml), s_registry, SuiteNamespace);

        // Assert
        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
    }

    [Fact]
    public void Compile_SameListenerVarNameTwice_DeDuplicatedNotFlaggedAsCollision()
    {
        // Arrange — two DIFFERENT steps referencing the SAME listener VarName is the existing,
        // supported de-duplication case (HostResourcePlanTests.Compile_TwoListenerSteps...);
        // it must not be misclassified as a "<x>"/"<x>_container" collision.
        const string yaml = """
            steps:
              - id: listen-a
                type: hostres2.listener
                varName: cb
              - id: listen-b
                type: hostres2.listener
                varName: cb
            """;

        // Act
        var result = ProviderPipeline.Compile(BuildAst(yaml), s_registry, SuiteNamespace);

        // Assert
        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
    }

    [Fact]
    public void Compile_CrossKindVarNameCollision_ReturnsValidationFailure()
    {
        // Arrange — a webhook-listener host resource and an otlp-receiver host resource
        // DECLARE THE SAME VarName ("shared"). ScenarioRunner stages every host resource's
        // URL into the SAME three Vars keys (svc::<VarName> / <VarName> / <VarName>_container)
        // keyed only by VarName, with no way to tell the two requirements came from different
        // kinds — so without this guard the two would silently last-write-wins collide.
        const string yaml = """
            steps:
              - id: listen-a
                type: hostres2.listener
                varName: shared
              - id: receive-a
                type: hostres2.receiver
                varName: shared
            """;

        // Act
        var result = ProviderPipeline.Compile(BuildAst(yaml), s_registry, SuiteNamespace);

        // Assert — a ValidationFailure (never a thrown exception, never a silently-built
        // Assembled script) naming the shared VarName and both colliding kinds.
        Assert.NotNull(result.Failure);
        Assert.Null(result.Assembled);
        Assert.Contains("shared", result.Failure!.Message, System.StringComparison.Ordinal);
        Assert.Contains("webhook-listener", result.Failure.Message, System.StringComparison.Ordinal);
        Assert.Contains("otlp-receiver", result.Failure.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_DifferentKindsDifferentVarNames_Succeeds()
    {
        // Arrange — a webhook listener and an otlp receiver with DISTINCT names never collide.
        const string yaml = """
            steps:
              - id: listen-a
                type: hostres2.listener
                varName: cb
              - id: receive-a
                type: hostres2.receiver
                varName: traces
            """;

        // Act
        var result = ProviderPipeline.Compile(BuildAst(yaml), s_registry, SuiteNamespace);

        // Assert
        Assert.Null(result.Failure);
        Assert.NotNull(result.Assembled);
        Assert.Equal(2, result.HostResourcePlan.Count);
    }
}
