// Tests for S07-F-01a (Runtime layer): ProviderPipeline host-resource reflection.
//
// Verifies that ProviderPipeline.Compile reflects IHostResourceContributor<TModel>
// across steps into the HostResourcePlan, tolerantly (a provider that does not
// implement it contributes nothing) — mirroring the IResourceContributor<TModel>
// reflection covered by ProviderPipelineTests.
//
// All tests are non-docker.  No topology is started.  Stub providers are file-scoped
// and declared only in this file (distinct kinds from ProviderPipelineTests).

using System.Collections.Generic;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Compilation;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Tests;

// ── File-scoped stub providers ────────────────────────────────────────────────
//
//   hostres.listener — implements IHostResourceContributor (declares a webhook-listener)
//   hostres.plain    — does NOT implement IHostResourceContributor (tolerant: nothing)

file sealed record ListenerModel(string VarName) : IStepModel;
file sealed record PlainModel(string Tag) : IStepModel;

[StepProvider]
file sealed class StubHostResourceProvider
    : IStepProvider,
      IStepBinder<ListenerModel>,
      IStepValidator<ListenerModel>,
      IStepCompiler<ListenerModel>,
      IHostResourceContributor<ListenerModel>
{
    public const string ListenerVarName = "orders-callback";

    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("hostres", "listener");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public ListenerModel Bind(YamlNode node, IBindingContext ctx) =>
        new ListenerModel(VarName: ListenerVarName);

    public ValidationResult Validate(ListenerModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(ListenerModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: System.Array.Empty<string>(),
            RequiredHelpers: System.Array.Empty<string>(),
            StatementBlock: $"{{ /* listener step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");

    public IEnumerable<HostResourceRequirement> HostResources(ListenerModel model) =>
        new[] { new HostResourceRequirement("webhook-listener", model.VarName) };
}

[StepProvider]
file sealed class StubPlainProvider
    : IStepProvider,
      IStepBinder<PlainModel>,
      IStepValidator<PlainModel>,
      IStepCompiler<PlainModel>
{
    private static readonly string[] s_authors = new[] { "test" };

    public StepKindId Kind => new("hostres", "plain");

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public PlainModel Bind(YamlNode node, IBindingContext ctx) =>
        new PlainModel(Tag: "plain");

    public ValidationResult Validate(PlainModel model, IProjectContext ctx) =>
        ValidationResult.Success;

    public CsxFragment Emit(PlainModel model, ICompileContext ctx) =>
        new CsxFragment(
            RequiredUsings: System.Array.Empty<string>(),
            RequiredHelpers: System.Array.Empty<string>(),
            StatementBlock: $"{{ /* plain step: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="ProviderPipeline"/> host-resource reflection
/// (S07-F-01a): the <see cref="IHostResourceContributor{TModel}"/> reflection into
/// <c>PipelineResult.HostResourcePlan</c>.
/// </summary>
public sealed class HostResourcePlanTests
{
    private static readonly System.Reflection.Assembly[] s_providerAssemblies =
        new[] { typeof(HostResourcePlanTests).Assembly };

    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(s_providerAssemblies);

    private const string SuiteNamespace = "TestSuite";

    private static ScenarioAst BuildAst(string yaml)
    {
        var doc = YamlDocumentParser.Parse(yaml);
        return AstBuilder.Build(doc, s_registry);
    }

    [Fact]
    public void Compile_ListenerStep_ContributesWebhookListenerHostResource()
    {
        const string yaml = """
            steps:
              - id: stand-up-listener
                type: hostres.listener
              - id: plain-step
                type: hostres.plain
            """;

        var result = ProviderPipeline.Compile(BuildAst(yaml), s_registry, SuiteNamespace);

        Assert.Null(result.Failure);

        // Exactly one host-resource entry, from the listener step only (plain is tolerant).
        Assert.Single(result.HostResourcePlan);

        var entry = result.HostResourcePlan[0];
        Assert.Equal("stand-up-listener", entry.StepId);
        Assert.Equal("webhook-listener", entry.Requirement.Kind);
        Assert.Equal(StubHostResourceProvider.ListenerVarName, entry.Requirement.VarName);
    }

    [Fact]
    public void Compile_NoListenerStep_HostResourcePlanIsEmpty()
    {
        const string yaml = """
            steps:
              - id: plain-step
                type: hostres.plain
            """;

        var result = ProviderPipeline.Compile(BuildAst(yaml), s_registry, SuiteNamespace);

        Assert.Null(result.Failure);
        Assert.Empty(result.HostResourcePlan);
    }

    [Fact]
    public void Compile_TwoListenerSteps_EachContributesAnEntry()
    {
        // Two listener steps both declare the same VarName; the pipeline records BOTH
        // entries (de-duplication by VarName is the runner's responsibility at start time,
        // not the pipeline's — the plan faithfully records each contributing step).
        const string yaml = """
            steps:
              - id: listener-a
                type: hostres.listener
              - id: listener-b
                type: hostres.listener
            """;

        var result = ProviderPipeline.Compile(BuildAst(yaml), s_registry, SuiteNamespace);

        Assert.Null(result.Failure);
        Assert.Equal(2, result.HostResourcePlan.Count);
        Assert.Equal("listener-a", result.HostResourcePlan[0].StepId);
        Assert.Equal("listener-b", result.HostResourcePlan[1].StepId);
    }
}
