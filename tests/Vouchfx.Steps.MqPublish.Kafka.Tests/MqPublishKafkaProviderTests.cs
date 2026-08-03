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
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Kafka;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.Kafka.Tests;

// ── Stub context implementations ─────────────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> that exposes a configurable
/// <see cref="IProjectContext.DeclaredDependencies"/> map for validator unit tests.
/// </summary>
file sealed class StubProjectContext : IProjectContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    internal StubProjectContext(
        IReadOnlyDictionary<string, string>? deps = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? services = null)
    {
        DeclaredDependencies = deps
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        DeclaredServices = services
            ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeclaredServices { get; }
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
    /// Dependency type comparison is case-sensitive (pre-GA decision,
    /// feat/case-sensitive-kinds): "Kafka" does not match the canonical "kafka".
    /// </summary>
    [Fact]
    public void Validate_DependencyTypeWrongCase_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "Kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("bus", "t", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("bus", StringComparison.Ordinal));
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

    // ── 12. Validate: target names a declared SERVICE, not a dependency (REQ-011) ──

    /// <summary>
    /// Services-generalisation spec REQ-011: a <c>target</c> naming a declared
    /// <em>service</em> (a customer-supplied broker under <c>environment.services</c>,
    /// not the engine-provisioned <c>kafka</c> dependency type) is accepted —
    /// name-membership only; protocol correctness is a later slice's concern.
    /// </summary>
    [Fact]
    public void Validate_TargetIsDeclaredService_IsValid()
    {
        var services = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["kafka-broker"] = new List<string> { "tcp-9093" },
        };
        var ctx = new StubProjectContext(services: services);

        var model = MakeModel("kafka-broker", "orders", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// REQ-011/REQ-012 message-shape alignment: a target naming NEITHER a declared
    /// kafka dependency NOR a declared service names the target and lists what IS
    /// declared (both dependencies and services) — not merely the pre-REQ-011
    /// dependency-only wording.
    /// </summary>
    [Fact]
    public void Validate_TargetNeitherDependencyNorService_ListsBothDeclaredSurfaces()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orders-db"] = "postgres",
        };
        var services = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["web"] = new List<string> { "http" },
        };
        var ctx = new StubProjectContext(deps, services);

        var model = MakeModel("does-not-exist", "orders", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("does-not-exist", StringComparison.Ordinal) &&
            e.Contains("orders-db", StringComparison.Ordinal) &&
            e.Contains("web", StringComparison.Ordinal));
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

    // ── 12. Bind: avro block → populated Avro spec ─────────────────────────────

    /// <summary>
    /// A step with an <c>avro</c> mapping binds <c>schemaRegistry</c>/<c>subject</c>/
    /// <c>schema</c>/<c>record</c> into <see cref="MqPublishKafkaModel.Avro"/>; the
    /// record map round-trips.
    /// </summary>
    [Fact]
    public void Bind_AvroBlock_PopulatesAvroSpec()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("events-bus") },
            { "topic",   new YamlScalarNode("orders.created") },
            { "payload", new YamlScalarNode("ignored-when-avro") },
            {
                "avro", new YamlMappingNode
                {
                    { "schemaRegistry", new YamlScalarNode("events-bus") },
                    { "subject",        new YamlScalarNode("orders.created-value") },
                    { "schema",         new YamlScalarNode("{\"type\":\"record\",\"name\":\"Order\",\"fields\":[{\"name\":\"id\",\"type\":\"int\"}]}") },
                    {
                        "record", new YamlMappingNode
                        {
                            { "id",   new YamlScalarNode("42") },
                            { "name", new YamlScalarNode("widget") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.NotNull(model.Avro);
        Assert.Equal("events-bus", model.Avro!.SchemaRegistryTarget);
        Assert.Equal("orders.created-value", model.Avro.Subject);
        Assert.Contains("\"name\":\"Order\"", model.Avro.Schema, StringComparison.Ordinal);
        Assert.Equal(2, model.Avro.Record.Count);
        Assert.Equal("42", model.Avro.Record["id"]);
        Assert.Equal("widget", model.Avro.Record["name"]);
    }

    /// <summary>
    /// A step with no <c>avro</c> mapping binds <see cref="MqPublishKafkaModel.Avro"/>
    /// as <see langword="null"/> (the PLAIN-payload path).
    /// </summary>
    [Fact]
    public void Bind_NoAvroBlock_AvroIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("events-bus") },
            { "topic",   new YamlScalarNode("t") },
            { "payload", new YamlScalarNode("hello") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Avro);
    }

    // ── 13. Validate: avro path ────────────────────────────────────────────────

    /// <summary>
    /// A valid avro publish step (schemaRegistry is a kafka dep; subject/schema/record
    /// present) passes validation.
    /// </summary>
    [Fact]
    public void Validate_ValidAvro_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeAvroModel("events-bus", "events-bus");

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// An avro publish step whose <c>avro.schemaRegistry</c> is not a declared kafka
    /// dependency fails validation.
    /// </summary>
    [Fact]
    public void Validate_AvroSchemaRegistryNotKafkaDep_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
            // 'no-such-registry' is intentionally absent.
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeAvroModel("events-bus", "no-such-registry");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("avro.schemaRegistry", StringComparison.Ordinal) &&
            e.Contains("no-such-registry", StringComparison.Ordinal));
    }

    /// <summary>
    /// An avro publish step whose <c>avro.schemaRegistry</c> names a postgres (not kafka)
    /// dependency fails validation.
    /// </summary>
    [Fact]
    public void Validate_AvroSchemaRegistryWrongType_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
            ["orders-db"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeAvroModel("events-bus", "orders-db");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("avro.schemaRegistry", StringComparison.Ordinal) &&
            e.Contains("kafka dependency", StringComparison.Ordinal));
    }

    /// <summary>
    /// An avro publish step missing the subject fails validation.
    /// </summary>
    [Fact]
    public void Validate_AvroMissingSubject_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = new MqPublishKafkaModel(
            Target: "events-bus", Topic: "t", Key: null, Payload: "p", Headers: null,
            Avro: new KafkaAvro(
                SchemaRegistryTarget: "events-bus",
                Subject: string.Empty,
                Schema: "{\"type\":\"record\",\"name\":\"R\",\"fields\":[]}",
                Record: new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = "1" }));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("avro.subject", StringComparison.Ordinal));
    }

    /// <summary>
    /// An avro publish step missing the schema fails validation.
    /// </summary>
    [Fact]
    public void Validate_AvroMissingSchema_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = new MqPublishKafkaModel(
            Target: "events-bus", Topic: "t", Key: null, Payload: "p", Headers: null,
            Avro: new KafkaAvro(
                SchemaRegistryTarget: "events-bus",
                Subject: "s",
                Schema: string.Empty,
                Record: new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = "1" }));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("avro.schema", StringComparison.Ordinal));
    }

    /// <summary>
    /// An avro publish step with an empty record fails validation.
    /// </summary>
    [Fact]
    public void Validate_AvroEmptyRecord_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = new MqPublishKafkaModel(
            Target: "events-bus", Topic: "t", Key: null, Payload: "p", Headers: null,
            Avro: new KafkaAvro(
                SchemaRegistryTarget: "events-bus",
                Subject: "s",
                Schema: "{\"type\":\"record\",\"name\":\"R\",\"fields\":[]}",
                Record: new Dictionary<string, string>(StringComparer.Ordinal)));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("avro.record", StringComparison.Ordinal));
    }

    /// <summary>
    /// The plain-path validation is unchanged by the additive avro field: a valid plain
    /// model (no avro) still passes.
    /// </summary>
    [Fact]
    public void Validate_PlainPath_StillValid_WithAvroFieldAdded()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("events-bus", "orders.created", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
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

    private static MqPublishKafkaModel MakeAvroModel(string target, string schemaRegistry)
        => new(
            Target: target,
            Topic: "orders.created",
            Key: null,
            Payload: "ignored",
            Headers: null,
            Avro: new KafkaAvro(
                SchemaRegistryTarget: schemaRegistry,
                Subject: "orders.created-value",
                Schema: "{\"type\":\"record\",\"name\":\"Order\",\"fields\":[{\"name\":\"id\",\"type\":\"int\"}]}",
                Record: new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = "42" }));
}
