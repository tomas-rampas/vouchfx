// Tests for MqExpectKafkaProvider — bind / validate / schema / registry.
//
// Covers:
//   1. Bind: full YAML step (target/topic/match with key + headers + payloadContains
//      + json) → populated model; the nested match maps round-trip.
//   2. Bind: non-mapping node → safe empty-stringed model with an all-null match (defensive).
//   3. Bind: a match with only payloadContains → key/headers/json absent.
//   4. Validate: valid model (declared kafka + ≥1 criterion) → IsValid.
//   5. Validate: empty target → invalid with clear message.
//   6. Validate: empty topic → invalid with clear message.
//   7. Validate: empty match (no criteria) → invalid with clear message.
//   8. Validate: target declared as a postgres dependency (wrong type) → invalid.
//   9. Validate: target not declared at all → invalid.
//  10. Registry: provider discoverable at key "mq-expect.kafka".
//  11. Registry: SchemaFragment references "match".
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Kafka;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqExpect.Kafka.Tests;

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
        IReadOnlyDictionary<string, DeclaredServiceInfo>? services = null)
    {
        DeclaredDependencies = deps
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        DeclaredServices = services
            ?? new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; }
}

/// <summary>
/// Stub <see cref="IBindingContext"/> for tests that do not require
/// binding-stage services.
/// </summary>
internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="MqExpectKafkaProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqExpectKafkaProviderTests
{
    private readonly MqExpectKafkaProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    /// <summary>
    /// A full YAML mapping with target, topic, and a match block declaring key,
    /// headers, payloadContains, and json is deserialised into the correct model
    /// fields; the nested maps round-trip.
    /// </summary>
    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("events-bus") },
            { "topic",  new YamlScalarNode("orders.created") },
            {
                "match", new YamlMappingNode
                {
                    { "key",             new YamlScalarNode("order-42") },
                    { "payloadContains", new YamlScalarNode("\"status\":\"NEW\"") },
                    {
                        "headers", new YamlMappingNode
                        {
                            { "content-type",  new YamlScalarNode("application/json") },
                            { "x-correlation", new YamlScalarNode("abc-123") },
                        }
                    },
                    {
                        "json", new YamlMappingNode
                        {
                            { "$.id",     new YamlScalarNode("42") },
                            { "$.status", new YamlScalarNode("NEW") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("events-bus", model.Target);
        Assert.Equal("orders.created", model.Topic);

        Assert.Equal("order-42", model.Match.Key);
        Assert.Equal("\"status\":\"NEW\"", model.Match.PayloadContains);

        Assert.NotNull(model.Match.Headers);
        Assert.Equal(2, model.Match.Headers!.Count);
        Assert.Equal("application/json", model.Match.Headers["content-type"]);
        Assert.Equal("abc-123", model.Match.Headers["x-correlation"]);

        Assert.NotNull(model.Match.Json);
        Assert.Equal(2, model.Match.Json!.Count);
        Assert.Equal("42", model.Match.Json["$.id"]);
        Assert.Equal("NEW", model.Match.Json["$.status"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    /// <summary>
    /// Binding from a non-mapping node returns a safe empty-stringed model with an
    /// all-null match (defensive).
    /// </summary>
    [Fact]
    public void Bind_NonMappingNode_ReturnsEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("bad"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Topic);
        Assert.Null(model.Match.Key);
        Assert.Null(model.Match.Headers);
        Assert.Null(model.Match.PayloadContains);
        Assert.Null(model.Match.Json);
    }

    // ── 3. Bind: match with only payloadContains ────────────────────────────────

    /// <summary>
    /// A match declaring only payloadContains binds that field and leaves
    /// key / headers / json as <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Bind_MatchWithOnlyPayloadContains_OtherCriteriaNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("events-bus") },
            { "topic",  new YamlScalarNode("t") },
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("hello") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("hello", model.Match.PayloadContains);
        Assert.Null(model.Match.Key);
        Assert.Null(model.Match.Headers);
        Assert.Null(model.Match.Json);
    }

    // ── 4. Validate: valid model with matching kafka dependency ────────────────

    /// <summary>
    /// A fully valid model whose target is declared as type "kafka" and whose match
    /// declares at least one criterion passes validation.
    /// </summary>
    [Fact]
    public void Validate_ValidModel_WithMatchingKafkaDependency_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("events-bus", "orders.created",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));

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

        var model = MakeModel("bus", "t",
            new KafkaMatch(Key: null, Headers: null, PayloadContains: "x", Json: null));

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

        var model = MakeModel(string.Empty, "t",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));

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

        var model = MakeModel("bus", string.Empty,
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'topic'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 7. Validate: empty match (no criteria) ──────────────────────────────────

    /// <summary>
    /// A match declaring no criteria (all null / empty) produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyMatch_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("bus", "t",
            new KafkaMatch(Key: null, Headers: null, PayloadContains: null, Json: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'match'", StringComparison.Ordinal) &&
            e.Contains("at least one criterion", StringComparison.Ordinal));
    }

    /// <summary>
    /// A match whose only maps are present-but-empty is also rejected (an empty
    /// headers map or empty json map imposes no constraint).
    /// </summary>
    [Fact]
    public void Validate_MatchWithEmptyMaps_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("bus", "t",
            new KafkaMatch(
                Key: null,
                Headers: new Dictionary<string, string>(StringComparer.Ordinal),
                PayloadContains: null,
                Json: new Dictionary<string, string>(StringComparer.Ordinal)));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("at least one criterion", StringComparison.Ordinal));
    }

    // ── 8. Validate: target declared with wrong type ───────────────────────────

    /// <summary>
    /// When the target is declared but its type is "postgres" (not "kafka"), the
    /// validator returns a dependency-reconciliation error naming the actual type.
    /// </summary>
    [Fact]
    public void Validate_TargetDeclaredAsWrongType_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("events-bus", "t",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("events-bus", StringComparison.Ordinal) &&
            e.Contains("postgres", StringComparison.Ordinal) &&
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

        var model = MakeModel("events-bus", "t",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));

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
    /// name-membership only. Validate never inspects the service's protocol or ports;
    /// the target's staged form is settled at Emit (svc::&lt;name&gt;, REQ-023) and its
    /// protocol at run time.
    /// </summary>
    [Fact]
    public void Validate_TargetIsDeclaredService_IsValid()
    {
        var services = new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal)
        {
            ["kafka-broker"] = new DeclaredServiceInfo(new List<string> { "tcp-9093" }),
        };
        var ctx = new StubProjectContext(services: services);

        var model = MakeModel("kafka-broker", "orders",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// REQ-011/REQ-012 message-shape alignment: a target naming NEITHER a declared
    /// kafka dependency NOR a declared service names the target and lists what IS
    /// declared (both dependencies and services).
    /// </summary>
    [Fact]
    public void Validate_TargetNeitherDependencyNorService_ListsBothDeclaredSurfaces()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orders-db"] = "postgres",
        };
        var services = new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal)
        {
            ["web"] = new DeclaredServiceInfo(new List<string> { "http" }),
        };
        var ctx = new StubProjectContext(deps, services);

        var model = MakeModel("does-not-exist", "orders",
            new KafkaMatch(Key: "k", Headers: null, PayloadContains: null, Json: null));

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
    /// discovers <see cref="MqExpectKafkaProvider"/> at key <c>"mq-expect.kafka"</c>.
    /// </summary>
    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqExpectKafkaProvider).Assembly });

        var found = registry.TryGet("mq-expect.kafka", out var registered);

        Assert.True(found, "Expected 'mq-expect.kafka' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("mq-expect", registered!.Kind.Family);
        Assert.Equal("kafka", registered.Kind.Provider);
        Assert.IsType<MqExpectKafkaProvider>(registered.Instance);
    }

    // ── 11. Registry: SchemaFragment references "match" ────────────────────────

    /// <summary>
    /// The discovered provider's <see cref="JsonSchemaFragment"/> must be non-null
    /// and its JSON must reference the <c>match</c> field.
    /// </summary>
    [Fact]
    public void Provider_SchemaFragment_ContainsMatch()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqExpectKafkaProvider).Assembly });

        registry.TryGet("mq-expect.kafka", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("match", registered.SchemaFragment!.Json,
            StringComparison.Ordinal);
    }

    // ── 12. Bind: avro block → populated Avro spec ─────────────────────────────

    /// <summary>
    /// A step with an <c>avro</c> mapping binds <c>schemaRegistry</c> (and optional
    /// <c>subject</c>/<c>schema</c>) into <see cref="MqExpectKafkaModel.Avro"/>.
    /// </summary>
    [Fact]
    public void Bind_AvroBlock_PopulatesAvroSpec()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("events-bus") },
            { "topic",  new YamlScalarNode("orders.created") },
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("widget") },
                }
            },
            {
                "avro", new YamlMappingNode
                {
                    { "schemaRegistry", new YamlScalarNode("events-bus") },
                    { "subject",        new YamlScalarNode("orders.created-value") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.NotNull(model.Avro);
        Assert.Equal("events-bus", model.Avro!.SchemaRegistryTarget);
        Assert.Equal("orders.created-value", model.Avro.Subject);
        Assert.Null(model.Avro.Schema);  // schema omitted → null (optional for expect).
    }

    /// <summary>
    /// A step with no <c>avro</c> mapping binds <see cref="MqExpectKafkaModel.Avro"/>
    /// as <see langword="null"/> (the PLAIN-payload path).
    /// </summary>
    [Fact]
    public void Bind_NoAvroBlock_AvroIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("events-bus") },
            { "topic",  new YamlScalarNode("t") },
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("x") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Avro);
    }

    // ── 13. Validate: avro path ────────────────────────────────────────────────

    /// <summary>
    /// A valid avro expect step (schemaRegistry is a kafka dep; ≥1 match criterion)
    /// passes validation.  subject/schema are optional for the expect side.
    /// </summary>
    [Fact]
    public void Validate_ValidAvro_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = new MqExpectKafkaModel(
            Target: "events-bus", Topic: "orders.created",
            Match: new KafkaMatch(Key: null, Headers: null, PayloadContains: "widget", Json: null),
            Avro: new KafkaAvro(SchemaRegistryTarget: "events-bus", Subject: null, Schema: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// An avro expect step whose <c>avro.schemaRegistry</c> is not a declared kafka
    /// dependency fails validation.
    /// </summary>
    [Fact]
    public void Validate_AvroSchemaRegistryNotKafkaDep_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["events-bus"] = "kafka",
        };
        var ctx = new StubProjectContext(deps);

        var model = new MqExpectKafkaModel(
            Target: "events-bus", Topic: "orders.created",
            Match: new KafkaMatch(Key: null, Headers: null, PayloadContains: "widget", Json: null),
            Avro: new KafkaAvro(SchemaRegistryTarget: "no-such-registry", Subject: null, Schema: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("avro.schemaRegistry", StringComparison.Ordinal) &&
            e.Contains("no-such-registry", StringComparison.Ordinal));
    }

    /// <summary>
    /// An avro expect step whose <c>avro.schemaRegistry</c> names a postgres (not kafka)
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

        var model = new MqExpectKafkaModel(
            Target: "events-bus", Topic: "orders.created",
            Match: new KafkaMatch(Key: null, Headers: null, PayloadContains: "widget", Json: null),
            Avro: new KafkaAvro(SchemaRegistryTarget: "orders-db", Subject: null, Schema: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("avro.schemaRegistry", StringComparison.Ordinal) &&
            e.Contains("kafka dependency", StringComparison.Ordinal));
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

        var model = MakeModel("events-bus", "orders.created",
            new KafkaMatch(Key: null, Headers: null, PayloadContains: "widget", Json: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static MqExpectKafkaModel MakeModel(
        string target,
        string topic,
        KafkaMatch match)
        => new(
            Target: target,
            Topic: topic,
            Match: match);
}
