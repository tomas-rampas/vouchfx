// Tests for MqExpectRabbitmqProvider — bind/validate/schema/registry.
//
// Covers:
//   1.  Bind: full YAML step (target/queue/match) → correct model.
//   2.  Bind: non-mapping node → safe empty model (defensive).
//   3.  Bind: match absent → empty RabbitmqMatch (no criteria).
//   4.  Bind: payloadContains / headers / json all parsed.
//   5.  Validate: valid model + matching rabbitmq dependency → IsValid.
//   6.  Validate: dependency type comparison is case-insensitive.
//   7.  Validate: empty target → invalid.
//   8.  Validate: empty queue → invalid.
//   9.  Validate: match with no criterion → invalid.
//   10. Validate: target not in DeclaredDependencies → invalid.
//   11. Validate: target declared but type is wrong (e.g. "kafka") → invalid.
//   12. Registry: provider discoverable via StepKindRegistry with key "mq-expect.rabbitmq".
//   13. Registry: SchemaFragment contains "payloadContains".
//   14. Resources: yields a rabbitmq ResourceRequirement whose Name equals model.Target.
//   15. CompileReferenceAssemblies: contains the RabbitMQ.Client assembly.
//   16. CompileReferenceAssemblies: contains the JsonPath.Net assembly.
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Rabbitmq;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqExpect.Rabbitmq.Tests;

// ── Stub contexts ─────────────────────────────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> exposing a configurable
/// <see cref="IProjectContext.DeclaredDependencies"/> map for validator unit tests.
/// </summary>
file sealed class StubProjectContext : IProjectContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    internal StubProjectContext(IReadOnlyDictionary<string, string>? deps = null)
    {
        DeclaredDependencies = deps
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }
}

/// <summary>Stub <see cref="IBindingContext"/> for tests that need no binding services.</summary>
internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="MqExpectRabbitmqProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqExpectRabbitmqProviderTests
{
    private readonly MqExpectRabbitmqProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    private static Dictionary<string, string> RmqDeps(string name = "rmq")
        => new Dictionary<string, string>(StringComparer.Ordinal) { [name] = "rabbitmq" };

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("rmq") },
            { "queue", new YamlScalarNode("orders") },
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("created") },
                    {
                        "headers", new YamlMappingNode
                        {
                            { "x-type", new YamlScalarNode("order") },
                        }
                    },
                    {
                        "json", new YamlMappingNode
                        {
                            { "$.id", new YamlScalarNode("42") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("rmq", model.Target);
        Assert.Equal("orders", model.Queue);
        Assert.Equal("created", model.Match.PayloadContains);
        Assert.NotNull(model.Match.Headers);
        Assert.Equal("order", model.Match.Headers!["x-type"]);
        Assert.NotNull(model.Match.Json);
        Assert.Equal("42", model.Match.Json!["$.id"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsSafeEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("not-a-mapping"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Queue);
        Assert.Null(model.Match.PayloadContains);
        Assert.Null(model.Match.Headers);
        Assert.Null(model.Match.Json);
    }

    // ── 3. Bind: match absent ──────────────────────────────────────────────────

    [Fact]
    public void Bind_MatchAbsent_ModelMatchHasNoCriteria()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("rmq") },
            { "queue", new YamlScalarNode("q") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Match.PayloadContains);
        Assert.Null(model.Match.Headers);
        Assert.Null(model.Match.Json);
    }

    // ── 4. Bind: payloadContains / headers / json all parsed ───────────────────

    [Fact]
    public void Bind_AllMatchCriteria_AreAllParsed()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("rmq") },
            { "queue", new YamlScalarNode("q") },
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("hello") },
                    { "headers", new YamlMappingNode { { "h1", new YamlScalarNode("v1") } } },
                    { "json", new YamlMappingNode { { "$.x", new YamlScalarNode("1") } } },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("hello", model.Match.PayloadContains);
        Assert.Equal("v1", model.Match.Headers!["h1"]);
        Assert.Equal("1", model.Match.Json!["$.x"]);
    }

    // ── 5. Validate: valid model ───────────────────────────────────────────────

    [Fact]
    public void Validate_ValidModel_IsValid()
    {
        var model = new MqExpectRabbitmqModel(
            Target: "rmq",
            Queue: "orders",
            Match: new RabbitmqMatch(PayloadContains: "hello", Headers: null, Json: null));

        var result = _provider.Validate(model, new StubProjectContext(RmqDeps("rmq")));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 6. Validate: dependency type comparison is case-sensitive (feat/case-sensitive-kinds) ──

    [Fact]
    public void Validate_DependencyTypeComparison_WrongCaseIsInvalid()
    {
        var model = new MqExpectRabbitmqModel("rmq", "q", new RabbitmqMatch("hello", null, null));
        var deps = new Dictionary<string, string>(StringComparer.Ordinal) { ["rmq"] = "RabbitMQ" };

        var result = _provider.Validate(model, new StubProjectContext(deps));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("rmq", StringComparison.Ordinal));
    }

    // ── 7. Validate: empty target ──────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var model = new MqExpectRabbitmqModel("", "q", new RabbitmqMatch("hello", null, null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    // ── 8. Validate: empty queue ───────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyQueue_IsInvalid()
    {
        var model = new MqExpectRabbitmqModel("rmq", "", new RabbitmqMatch("hello", null, null));

        var result = _provider.Validate(model, new StubProjectContext(RmqDeps("rmq")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("queue"));
    }

    // ── 9. Validate: match with no criterion ─────────────────────────────────

    [Fact]
    public void Validate_MatchWithNoCriterion_IsInvalid()
    {
        var model = new MqExpectRabbitmqModel(
            "rmq", "q", new RabbitmqMatch(null, null, null));

        var result = _provider.Validate(model, new StubProjectContext(RmqDeps("rmq")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("match") && e.Contains("criterion"));
    }

    // ── 10. Validate: target not in DeclaredDependencies ─────────────────────

    [Fact]
    public void Validate_TargetNotInDependencies_IsInvalid()
    {
        var model = new MqExpectRabbitmqModel("rmq", "q", new RabbitmqMatch("hello", null, null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("rmq") && e.Contains("rabbitmq"));
    }

    // ── 11. Validate: target declared but wrong type ──────────────────────────

    [Fact]
    public void Validate_TargetWrongType_IsInvalid()
    {
        var model = new MqExpectRabbitmqModel("rmq", "q", new RabbitmqMatch("hello", null, null));
        var deps = new Dictionary<string, string>(StringComparer.Ordinal) { ["rmq"] = "kafka" };

        var result = _provider.Validate(model, new StubProjectContext(deps));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("kafka"));
    }

    // ── 12. Registry: provider discoverable ──────────────────────────────────

    [Fact]
    public void Registry_ProviderIsDiscoverable_ByKey()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqExpectRabbitmqProvider).Assembly });

        Assert.True(
            registry.TryGet("mq-expect.rabbitmq", out var provider) && provider is not null,
            "Provider for 'mq-expect.rabbitmq' must be discoverable via StepKindRegistry.");
    }

    // ── 13. Registry: SchemaFragment contains 'payloadContains' ──────────────

    [Fact]
    public void SchemaFragment_ContainsPayloadContains()
    {
        Assert.Contains("payloadContains", _provider.SchemaFragment.Json, StringComparison.Ordinal);
    }

    // ── 14. Resources: yields rabbitmq ResourceRequirement ───────────────────

    [Fact]
    public void Resources_YieldsRabbitmqRequirementWithCorrectName()
    {
        var model = new MqExpectRabbitmqModel(
            "rmq", "q", new RabbitmqMatch("hello", null, null));
        var resources = _provider.Resources(model).ToList();

        Assert.Single(resources);
        Assert.Equal("rabbitmq", resources[0].Family);
        Assert.Equal("rmq", resources[0].Name);
    }

    // ── 15. CompileReferenceAssemblies: contains RabbitMQ.Client ─────────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsRabbitMqClientAssembly()
    {
        var refs = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(refs, a => a.GetName().Name == "RabbitMQ.Client");
    }

    // ── 16. CompileReferenceAssemblies: contains JsonPath.Net ────────────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsJsonPathNetAssembly()
    {
        var refs = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(refs, a => a.GetName().Name == "JsonPath.Net");
    }
}
