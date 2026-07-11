// Tests for MqPublishRabbitmqProvider — bind/validate/schema/registry.
//
// Covers:
//   1.  Bind: full YAML step (target/exchange/routingKey/payload/headers) → correct model.
//   2.  Bind: non-mapping node → safe empty model (defensive).
//   3.  Bind: exchange absent → null in model.
//   4.  Bind: headers absent → null in model.
//   5.  Validate: valid model + matching rabbitmq dependency → IsValid.
//   6.  Validate: dependency type comparison is case-insensitive.
//   7.  Validate: empty target → invalid.
//   8.  Validate: empty routingKey → invalid.
//   9.  Validate: empty payload → invalid.
//   10. Validate: target not in DeclaredDependencies → invalid.
//   11. Validate: target declared but type is wrong (e.g. "kafka") → invalid.
//   12. Registry: provider discoverable via StepKindRegistry with key "mq-publish.rabbitmq".
//   13. Registry: SchemaFragment contains "routingKey".
//   14. Resources: yields a rabbitmq ResourceRequirement whose Name equals model.Target.
//   15. CompileReferenceAssemblies: contains the RabbitMQ.Client assembly.
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Rabbitmq;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.Rabbitmq.Tests;

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
/// Non-docker unit tests for <see cref="MqPublishRabbitmqProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqPublishRabbitmqProviderTests
{
    private readonly MqPublishRabbitmqProvider _provider = new();
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
            { "exchange", new YamlScalarNode("events") },
            { "routingKey", new YamlScalarNode("order.created") },
            { "payload", new YamlScalarNode("{\"id\":1}") },
            {
                "headers", new YamlMappingNode
                {
                    { "x-type", new YamlScalarNode("order") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("rmq", model.Target);
        Assert.Equal("events", model.Exchange);
        Assert.Equal("order.created", model.RoutingKey);
        Assert.Equal("{\"id\":1}", model.Payload);
        Assert.NotNull(model.Headers);
        Assert.Equal("order", model.Headers!["x-type"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsSafeEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("not-a-mapping"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Null(model.Exchange);
        Assert.Equal(string.Empty, model.RoutingKey);
        Assert.Equal(string.Empty, model.Payload);
        Assert.Null(model.Headers);
    }

    // ── 3. Bind: exchange absent ───────────────────────────────────────────────

    [Fact]
    public void Bind_ExchangeAbsent_ModelExchangeIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("rmq") },
            { "routingKey", new YamlScalarNode("q") },
            { "payload", new YamlScalarNode("hello") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Exchange);
    }

    // ── 4. Bind: headers absent ────────────────────────────────────────────────

    [Fact]
    public void Bind_HeadersAbsent_ModelHeadersIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("rmq") },
            { "routingKey", new YamlScalarNode("q") },
            { "payload", new YamlScalarNode("hello") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Headers);
    }

    // ── 5. Validate: valid model ───────────────────────────────────────────────

    [Fact]
    public void Validate_ValidModel_IsValid()
    {
        var model = new MqPublishRabbitmqModel(
            Target: "rmq",
            Exchange: null,
            RoutingKey: "q",
            Payload: "hello",
            Headers: null);

        var result = _provider.Validate(model, new StubProjectContext(RmqDeps("rmq")));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 6. Validate: dependency type comparison is case-insensitive ────────────

    [Fact]
    public void Validate_DependencyTypeComparison_IsCaseInsensitive()
    {
        var model = new MqPublishRabbitmqModel("rmq", null, "q", "hello", null);
        var deps = new Dictionary<string, string>(StringComparer.Ordinal) { ["rmq"] = "RabbitMQ" };

        var result = _provider.Validate(model, new StubProjectContext(deps));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 7. Validate: empty target ──────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var model = new MqPublishRabbitmqModel("", null, "q", "hello", null);

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    // ── 8. Validate: empty routingKey ─────────────────────────────────────────

    [Fact]
    public void Validate_EmptyRoutingKey_IsInvalid()
    {
        var model = new MqPublishRabbitmqModel("rmq", null, "", "hello", null);

        var result = _provider.Validate(model, new StubProjectContext(RmqDeps("rmq")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("routingKey"));
    }

    // ── 9. Validate: empty payload ─────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyPayload_IsInvalid()
    {
        var model = new MqPublishRabbitmqModel("rmq", null, "q", "", null);

        var result = _provider.Validate(model, new StubProjectContext(RmqDeps("rmq")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("payload"));
    }

    // ── 10. Validate: target not in DeclaredDependencies ─────────────────────

    [Fact]
    public void Validate_TargetNotInDependencies_IsInvalid()
    {
        var model = new MqPublishRabbitmqModel("rmq", null, "q", "hello", null);

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("rmq") && e.Contains("rabbitmq"));
    }

    // ── 11. Validate: target declared but wrong type ──────────────────────────

    [Fact]
    public void Validate_TargetWrongType_IsInvalid()
    {
        var model = new MqPublishRabbitmqModel("rmq", null, "q", "hello", null);
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
            new[] { typeof(MqPublishRabbitmqProvider).Assembly });

        Assert.True(
            registry.TryGet("mq-publish.rabbitmq", out var provider) && provider is not null,
            "Provider for 'mq-publish.rabbitmq' must be discoverable via StepKindRegistry.");
    }

    // ── 13. Registry: SchemaFragment contains 'routingKey' ───────────────────

    [Fact]
    public void SchemaFragment_ContainsRoutingKey()
    {
        Assert.Contains("routingKey", _provider.SchemaFragment.Json, StringComparison.Ordinal);
    }

    // ── 14. Resources: yields rabbitmq ResourceRequirement ───────────────────

    [Fact]
    public void Resources_YieldsRabbitmqRequirementWithCorrectName()
    {
        var model = new MqPublishRabbitmqModel("rmq", null, "q", "hello", null);
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
}
