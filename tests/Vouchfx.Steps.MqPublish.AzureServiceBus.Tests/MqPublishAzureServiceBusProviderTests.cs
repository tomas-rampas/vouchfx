// Tests for MqPublishAzureServiceBusProvider — bind / validate / schema / registry.
//
// Covers:
//   1. Bind: full mapping with target/queue/payload/properties → populated model.
//   2. Bind: topic-based mapping (target/topic/payload) → correct model.
//   3. Bind: non-mapping node → safe empty-stringed model (defensive).
//   4. Bind: no 'properties' present → Properties is null.
//   5. Validate: valid queue model + matching azureservicebus dep → IsValid.
//   6. Validate: valid topic model + matching azureservicebus dep → IsValid.
//   7. Validate: dependency type comparison is case-insensitive ("AzureServiceBus").
//   8. Validate: empty target → invalid with clear message.
//   9. Validate: neither queue nor topic → invalid.
//  10. Validate: both queue and topic → invalid (mutually exclusive).
//  11. Validate: target declared as a postgres dependency (wrong type) → invalid.
//  12. Validate: target not declared at all → invalid.
//  13. Registry: provider discoverable at key "mq-publish.azureservicebus".
//  14. Schema: SchemaFragment references "queue".
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.AzureServiceBus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.AzureServiceBus.Tests;

// ── Stub context implementations ─────────────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> for validator unit tests.
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

/// <summary>
/// Stub <see cref="IBindingContext"/> for tests that do not require binding services.
/// </summary>
internal sealed class StubBindingContext : IBindingContext { }

/// <summary>Minimal <see cref="ICompileContext"/> for emit tests in provider test class.</summary>
file sealed class StubCompileContext : ICompileContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    public StubCompileContext(string stepId) => StepId = stepId;
    public string StepId { get; }
    public string SuiteNamespace => "Generated";
    public IReadOnlyDictionary<string, string> Captures { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
        new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="MqPublishAzureServiceBusProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqPublishAzureServiceBusProviderTests
{
    private readonly MqPublishAzureServiceBusProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full queue mapping ─────────────────────────────────────────────

    [Fact]
    public void Bind_QueueMappingWithProperties_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("asb") },
            { "queue",   new YamlScalarNode("orders") },
            { "payload", new YamlScalarNode("{\"id\":1}") },
            {
                "properties",
                new YamlMappingNode
                {
                    { "eventType", new YamlScalarNode("OrderCreated") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("asb", model.Target);
        Assert.Equal("orders", model.Queue);
        Assert.Null(model.Topic);
        Assert.Equal("{\"id\":1}", model.Payload);
        Assert.NotNull(model.Properties);
        Assert.Equal("OrderCreated", model.Properties["eventType"]);
    }

    // ── 2. Bind: topic mapping ───────────────────────────────────────────────────

    [Fact]
    public void Bind_TopicMapping_ReturnsTopicModelWithNullQueue()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("asb") },
            { "topic",   new YamlScalarNode("orders-topic") },
            { "payload", new YamlScalarNode("hello") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("orders-topic", model.Topic);
        Assert.Null(model.Queue);
    }

    // ── 3. Bind: non-mapping node → safe empty model ────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsSafeEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("invalid"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Null(model.Queue);
        Assert.Null(model.Topic);
    }

    // ── 4. Bind: no properties → Properties is null ──────────────────────────────

    [Fact]
    public void Bind_NoProperties_PropertiesIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("asb") },
            { "queue",   new YamlScalarNode("orders") },
            { "payload", new YamlScalarNode("hello") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Properties);
    }

    // ── 5. Validate: valid queue model + matching dep ─────────────────────────────

    [Fact]
    public void Validate_ValidQueueModel_MatchingDep_IsValid()
    {
        var model = new MqPublishAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Payload: "hello",
            Properties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
    }

    // ── 6. Validate: valid topic model + matching dep ─────────────────────────────

    [Fact]
    public void Validate_ValidTopicModel_MatchingDep_IsValid()
    {
        var model = new MqPublishAzureServiceBusModel(
            Target: "asb",
            Queue: null,
            Topic: "orders-topic",
            Payload: "hello",
            Properties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
    }

    // ── 7. Validate: dep type case-insensitive ────────────────────────────────────

    [Fact]
    public void Validate_DepTypeCaseInsensitive_IsValid()
    {
        var model = new MqPublishAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Payload: "hello",
            Properties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "AzureServiceBus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
    }

    // ── 8. Validate: empty target → invalid ──────────────────────────────────────

    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var model = new MqPublishAzureServiceBusModel(
            Target: "",
            Queue: "orders",
            Topic: null,
            Payload: "hello",
            Properties: null);

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    // ── 9. Validate: neither queue nor topic → invalid ────────────────────────────

    [Fact]
    public void Validate_NeitherQueueNorTopic_IsInvalid()
    {
        var model = new MqPublishAzureServiceBusModel(
            Target: "asb",
            Queue: null,
            Topic: null,
            Payload: "hello",
            Properties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
    }

    // ── 10. Validate: both queue and topic → invalid ──────────────────────────────

    [Fact]
    public void Validate_BothQueueAndTopic_IsInvalid()
    {
        var model = new MqPublishAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: "orders-topic",
            Payload: "hello",
            Properties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("mutually exclusive"));
    }

    // ── 11. Validate: target wrong type → invalid ────────────────────────────────

    [Fact]
    public void Validate_TargetWrongType_IsInvalid()
    {
        var model = new MqPublishAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Payload: "hello",
            Properties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "postgres",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("azureservicebus"));
    }

    // ── 12. Validate: target not declared → invalid ───────────────────────────────

    [Fact]
    public void Validate_TargetNotDeclared_IsInvalid()
    {
        var model = new MqPublishAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Payload: "hello",
            Properties: null);

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("asb"));
    }

    // ── 13. Registry: provider discoverable at "mq-publish.azureservicebus" ───────

    [Fact]
    public void Registry_ProviderDiscoverable_AtExpectedKey()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqPublishAzureServiceBusProvider).Assembly });

        var found = registry.TryGet("mq-publish.azureservicebus", out var registered);

        Assert.True(found, "Expected 'mq-publish.azureservicebus' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("mq-publish", registered!.Kind.Family);
        Assert.Equal("azureservicebus", registered.Kind.Provider);
        Assert.IsType<MqPublishAzureServiceBusProvider>(registered.Instance);
    }

    // ── 14. Schema: SchemaFragment references "queue" ────────────────────────────

    [Fact]
    public void Schema_SchemaFragment_ReferencesQueue()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqPublishAzureServiceBusProvider).Assembly });

        registry.TryGet("mq-publish.azureservicebus", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("queue", registered.SchemaFragment!.Json, StringComparison.Ordinal);
    }

    // ── Credential-redaction regression (§17) ──────────────────────────────────
    // FIX 6: tests now assert on the actual EMITTED helper source rather than
    // re-implementing the redaction logic inline.  See MqPublishAzureServiceBusEmitTests.cs
    // for the full emit-level suite (tests 13–15) covering two-layer redaction and
    // Base64 key patterns.

    [Fact]
    public void RedactCredentials_EmittedHelper_ContainsLiteralReplacementLayer()
    {
        // Layer (a): literal full connection-string replacement via String.Replace.
        var fragment = _provider.Emit(
            new MqPublishAzureServiceBusModel("asb", "orders", null, "p", null),
            new StubCompileContext("s"));
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        // The emitted helper must use .Replace(connStr, "***", ...) for layer (a).
        Assert.Contains(".Replace(connStr,", helperSource, StringComparison.Ordinal);
        Assert.Contains("***", helperSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactCredentials_EmittedHelper_ContainsSharedAccessKeyRegexLayer()
    {
        // Layer (b): regex scrub of SharedAccessKey=<value> up to the next semicolon.
        // The pattern [^;]* covers Base64 keys containing +, /, and = characters.
        var fragment = _provider.Emit(
            new MqPublishAzureServiceBusModel("asb", "orders", null, "p", null),
            new StubCompileContext("s"));
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("SharedAccessKey=[^;]*", helperSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactCredentials_EmittedHelper_ContainsBothLayers()
    {
        // Combined: both the literal-replacement and regex patterns appear in the
        // emitted helper, covering the full two-layer redaction path (§17).
        var fragment = _provider.Emit(
            new MqPublishAzureServiceBusModel("asb", "orders", null, "p", null),
            new StubCompileContext("s"));
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        // Layer (a):
        Assert.Contains(".Replace(connStr,", helperSource, StringComparison.Ordinal);
        // Layer (b): also covers Base64 keys with +abc/xyz== pattern:
        Assert.Contains("SharedAccessKey=[^;]*", helperSource, StringComparison.Ordinal);
    }
}
