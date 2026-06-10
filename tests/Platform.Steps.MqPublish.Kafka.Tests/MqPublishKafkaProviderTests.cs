// Tests for MqPublishKafkaProvider — bind / validate / schema / registry.
//
// Covers:
//   1. Bind: full YAML step (target/topic/payload/key/headers) → populated model;
//      headers map round-trips.
//   2. Bind: non-mapping node → safe empty-stringed model (defensive).
//   3. Bind: no 'key' present → Key is null.
//   4. Validate: valid model + matching kafka dependency → IsValid.
//   5. Validate: empty target → invalid with clear message.
//   6. Validate: empty topic → invalid with clear message.
//   7. Validate: empty payload → invalid with clear message.
//   8. Validate: target declared as a postgres dependency (wrong type) → invalid.
//   9. Validate: target not declared at all → invalid.
//  10. Registry: provider discoverable at key "mq-publish.kafka".
//  11. Registry: SchemaFragment references "payload".
//
// All tests are non-docker.  No topology is started.
using Platform.Sdk;
using Platform.Steps.MqPublish.Kafka;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.MqPublish.Kafka.Tests;

// ── Stub context implementations ─────────────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> that exposes a configurable
/// <see cref="IProjectContext.DeclaredDependencies"/> map for validator unit tests.
/// </summary>
file sealed class StubProjectContext : IProjectContext
{
    internal StubProjectContext(IReadOnlyDictionary<string, string>? deps = null)
    {
        DeclaredDependencies = deps
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }
}

/// <summary>
/// Stub <see cref="IBindingContext"/> for tests that do not require
/// binding-stage services.
/// </summary>
internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="MqPublishKafkaProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqPublishKafkaProviderTests
{
    private readonly MqPublishKafkaProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    /// <summary>
    /// A full YAML mapping with target, topic, payload, key, and headers is
    /// deserialised into the correct model fields; the headers map round-trips.
    /// </summary>
    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("events-bus") },
            { "topic",   new YamlScalarNode("orders.created") },
            { "key",     new YamlScalarNode("order-42") },
            { "payload", new YamlScalarNode("{\"id\":42,\"status\":\"NEW\"}") },
            {
                "headers", new YamlMappingNode
                {
                    { "content-type",  new YamlScalarNode("application/json") },
                    { "x-correlation", new YamlScalarNode("abc-123") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("events-bus", model.Target);
        Assert.Equal("orders.created", model.Topic);
        Assert.Equal("order-42", model.Key);
        Assert.Equal("{\"id\":42,\"status\":\"NEW\"}", model.Payload);

        Assert.NotNull(model.Headers);
        Assert.Equal(2, model.Headers!.Count);
        Assert.Equal("application/json", model.Headers["content-type"]);
        Assert.Equal("abc-123", model.Headers["x-correlation"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    /// <summary>
    /// Binding from a non-mapping node returns a safe empty-stringed model (defensive).
    /// </summary>
    [Fact]
    public void Bind_NonMappingNode_ReturnsEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("bad"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Topic);
        Assert.Null(model.Key);
        Assert.Equal(string.Empty, model.Payload);
        Assert.Null(model.Headers);
    }

    // ── 3. Bind: no key present ─────────────────────────────────────────────────

    /// <summary>
    /// A step that omits the 'key' field binds <see cref="MqPublishKafkaModel.Key"/>
    /// as <see langword="null"/> (an empty key is then sent by the producer).
    /// </summary>
    [Fact]
    public void Bind_NoKey_KeyIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("events-bus") },
            { "topic",   new YamlScalarNode("t") },
            { "payload", new YamlScalarNode("hello") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Key);
        Assert.Null(model.Headers);
    }

    // ── 4. Validate: valid model with matching kafka dependency ────────────────

    /// <summary>
    /// A fully valid model whose target is declared as type "kafka" passes validation.
    /// </summary>
    [Fact]
    public void Validate_ValidModel_WithMatchingKafkaDependency_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("events-bus", "orders.created", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// Dependency type comparison is case-insensitive ("Kafka" matches "kafka").
    /// </summary>
    [Fact]
    public void Validate_DependencyTypeCaseInsensitive_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "Kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("bus", "t", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 5. Validate: empty target ──────────────────────────────────────────────

    /// <summary>
    /// An empty target produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var ctx = new StubProjectContext();

        var model = MakeModel(string.Empty, "t", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'target'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 6. Validate: empty topic ───────────────────────────────────────────────

    /// <summary>
    /// An empty topic produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyTopic_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("bus", string.Empty, "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'topic'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 7. Validate: empty payload ─────────────────────────────────────────────

    /// <summary>
    /// An empty payload produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyPayload_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("bus", "t", string.Empty);

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'payload'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 8. Validate: target declared with wrong type ───────────────────────────

    /// <summary>
    /// When the target is declared but its type is "postgres" (not "kafka"), the
    /// validator returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetDeclaredAsWrongType_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("events-bus", "t", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("events-bus", StringComparison.Ordinal) &&
            e.Contains("kafka dependency", StringComparison.Ordinal));
    }

    // ── 9. Validate: target not declared ───────────────────────────────────────

    /// <summary>
    /// When the target is not present in the declared-dependencies map at all, the
    /// validator returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetNotInDeclaredDependencies_IsInvalid()
    {
        var ctx = new StubProjectContext();

        var model = MakeModel("events-bus", "t", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("events-bus", StringComparison.Ordinal) &&
            e.Contains("kafka dependency", StringComparison.Ordinal));
    }

    // ── 10. Registry: provider discoverable ────────────────────────────────────

    /// <summary>
    /// Scanning the provider assembly via <see cref="StepKindRegistry.BuildAndFreeze"/>
    /// discovers <see cref="MqPublishKafkaProvider"/> at key <c>"mq-publish.kafka"</c>.
    /// </summary>
    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqPublishKafkaProvider).Assembly });

        var found = registry.TryGet("mq-publish.kafka", out var registered);

        Assert.True(found, "Expected 'mq-publish.kafka' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("mq-publish", registered!.Kind.Family);
        Assert.Equal("kafka", registered.Kind.Provider);
        Assert.IsType<MqPublishKafkaProvider>(registered.Instance);
    }

    // ── 11. Registry: SchemaFragment references "payload" ──────────────────────

    /// <summary>
    /// The discovered provider's <see cref="JsonSchemaFragment"/> must be non-null
    /// and its JSON must reference the <c>payload</c> field.
    /// </summary>
    [Fact]
    public void Provider_SchemaFragment_ContainsPayload()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqPublishKafkaProvider).Assembly });

        registry.TryGet("mq-publish.kafka", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("payload", registered.SchemaFragment!.Json,
            StringComparison.Ordinal);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static MqPublishKafkaModel MakeModel(
        string target,
        string topic,
        string payload,
        string? key = null,
        IReadOnlyDictionary<string, string>? headers = null)
        => new(
            Target: target,
            Topic: topic,
            Key: key,
            Payload: payload,
            Headers: headers);
}
