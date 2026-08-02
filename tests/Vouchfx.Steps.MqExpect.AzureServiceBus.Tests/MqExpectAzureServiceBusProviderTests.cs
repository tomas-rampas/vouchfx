// Tests for MqExpectAzureServiceBusProvider — bind / validate / schema / registry.
//
// Covers:
//   1. Bind: full queue mapping (target/queue/expectPayloadContains/expectProperties) → populated model.
//   2. Bind: topic+subscription mapping → correct model with Subscription.
//   3. Bind: non-mapping node → safe empty model (defensive).
//   4. Bind: no expectPayloadContains and no expectProperties → both null.
//   5. Bind: no subscription → Subscription is null.
//   6. Validate: valid queue model + payloadContains + matching dep → IsValid.
//   7. Validate: valid topic+subscription model + matching dep → IsValid.
//   8. Validate: dependency type comparison is case-insensitive ("AzureServiceBus").
//   9. Validate: no criteria (neither expectPayloadContains nor expectProperties) → invalid.
//  10. Validate: empty queue map treated as absent (same as no criteria).
//  11. Validate: empty target → invalid.
//  12. Validate: neither queue nor topic → invalid.
//  13. Validate: both queue and topic → invalid (mutually exclusive).
//  14. Validate: topic without subscription → invalid.
//  15. Validate: target wrong type → invalid.
//  16. Validate: target not declared → invalid.
//  17. Registry: provider discoverable at key "mq-expect.azureservicebus".
//  18. Schema: SchemaFragment references "queue".
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.AzureServiceBus;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqExpect.AzureServiceBus.Tests;

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

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeclaredServices { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
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
/// Non-docker unit tests for <see cref="MqExpectAzureServiceBusProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqExpectAzureServiceBusProviderTests
{
    private readonly MqExpectAzureServiceBusProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full queue mapping ─────────────────────────────────────────────

    [Fact]
    public void Bind_QueueMappingWithCriteria_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target",                new YamlScalarNode("asb") },
            { "queue",                 new YamlScalarNode("orders") },
            { "expectPayloadContains", new YamlScalarNode("OrderCreated") },
            {
                "expectProperties",
                new YamlMappingNode
                {
                    { "eventType", new YamlScalarNode("order") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("asb", model.Target);
        Assert.Equal("orders", model.Queue);
        Assert.Null(model.Topic);
        Assert.Null(model.Subscription);
        Assert.Equal("OrderCreated", model.ExpectPayloadContains);
        Assert.NotNull(model.ExpectProperties);
        Assert.Equal("order", model.ExpectProperties["eventType"]);
    }

    // ── 2. Bind: topic+subscription mapping ──────────────────────────────────────

    [Fact]
    public void Bind_TopicSubscriptionMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target",                new YamlScalarNode("asb") },
            { "topic",                 new YamlScalarNode("orders-topic") },
            { "subscription",          new YamlScalarNode("orders-sub") },
            { "expectPayloadContains", new YamlScalarNode("hello") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("orders-topic", model.Topic);
        Assert.Equal("orders-sub", model.Subscription);
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
        Assert.Null(model.Subscription);
        Assert.Null(model.ExpectPayloadContains);
        Assert.Null(model.ExpectProperties);
    }

    // ── 4. Bind: no criteria → both null ─────────────────────────────────────────

    [Fact]
    public void Bind_NoCriteria_BothNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("asb") },
            { "queue",  new YamlScalarNode("orders") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.ExpectPayloadContains);
        Assert.Null(model.ExpectProperties);
    }

    // ── 5. Bind: no subscription → Subscription is null ──────────────────────────

    [Fact]
    public void Bind_NoSubscription_SubscriptionIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target",                new YamlScalarNode("asb") },
            { "topic",                 new YamlScalarNode("orders-topic") },
            { "expectPayloadContains", new YamlScalarNode("hello") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Subscription);
    }

    // ── 6. Validate: valid queue model + payloadContains + matching dep ───────────

    [Fact]
    public void Validate_ValidQueueModelWithPayloadContains_MatchingDep_IsValid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: "OrderCreated",
            ExpectProperties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
    }

    // ── 7. Validate: valid topic+subscription + matching dep ──────────────────────

    [Fact]
    public void Validate_ValidTopicSubscriptionModel_MatchingDep_IsValid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: null,
            Topic: "orders-topic",
            Subscription: "orders-sub",
            ExpectPayloadContains: "hello",
            ExpectProperties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
    }

    // ── 8. Validate: dep type case-sensitive (pre-GA decision, feat/case-sensitive-kinds) ──

    [Fact]
    public void Validate_DepTypeWrongCase_IsInvalid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: "hello",
            ExpectProperties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "AzureServiceBus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("asb", StringComparison.Ordinal));
    }

    // ── 9. Validate: no criteria → invalid ────────────────────────────────────────

    [Fact]
    public void Validate_NoCriteria_IsInvalid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: null,
            ExpectProperties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
    }

    // ── 10. Validate: empty expectProperties treated as absent ────────────────────

    [Fact]
    public void Validate_EmptyExpectProperties_TreatedAsAbsent_IsInvalid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: null,
            ExpectProperties: new Dictionary<string, string>());

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
    }

    // ── 11. Validate: empty target → invalid ──────────────────────────────────────

    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "",
            Queue: "orders",
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: "hello",
            ExpectProperties: null);

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    // ── 12. Validate: neither queue nor topic → invalid ───────────────────────────

    [Fact]
    public void Validate_NeitherQueueNorTopic_IsInvalid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: null,
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: "hello",
            ExpectProperties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
    }

    // ── 13. Validate: both queue and topic → invalid ──────────────────────────────

    [Fact]
    public void Validate_BothQueueAndTopic_IsInvalid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: "orders-topic",
            Subscription: null,
            ExpectPayloadContains: "hello",
            ExpectProperties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("mutually exclusive"));
    }

    // ── 14. Validate: topic without subscription → invalid ────────────────────────

    [Fact]
    public void Validate_TopicWithoutSubscription_IsInvalid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: null,
            Topic: "orders-topic",
            Subscription: null,
            ExpectPayloadContains: "hello",
            ExpectProperties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "azureservicebus",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("subscription"));
    }

    // ── 15. Validate: target wrong type → invalid ────────────────────────────────

    [Fact]
    public void Validate_TargetWrongType_IsInvalid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: "hello",
            ExpectProperties: null);

        var ctx = new StubProjectContext(new Dictionary<string, string>
        {
            ["asb"] = "postgres",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("azureservicebus"));
    }

    // ── 16. Validate: target not declared → invalid ───────────────────────────────

    [Fact]
    public void Validate_TargetNotDeclared_IsInvalid()
    {
        var model = new MqExpectAzureServiceBusModel(
            Target: "asb",
            Queue: "orders",
            Topic: null,
            Subscription: null,
            ExpectPayloadContains: "hello",
            ExpectProperties: null);

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("asb"));
    }

    // ── 17. Registry: provider discoverable at "mq-expect.azureservicebus" ────────

    [Fact]
    public void Registry_ProviderDiscoverable_AtExpectedKey()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqExpectAzureServiceBusProvider).Assembly });

        var found = registry.TryGet("mq-expect.azureservicebus", out var registered);

        Assert.True(found, "Expected 'mq-expect.azureservicebus' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("mq-expect", registered!.Kind.Family);
        Assert.Equal("azureservicebus", registered.Kind.Provider);
        Assert.IsType<MqExpectAzureServiceBusProvider>(registered.Instance);
    }

    // ── 18. Schema: SchemaFragment references "queue" ─────────────────────────────

    [Fact]
    public void Schema_SchemaFragment_ReferencesQueue()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqExpectAzureServiceBusProvider).Assembly });

        registry.TryGet("mq-expect.azureservicebus", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("queue", registered.SchemaFragment!.Json, StringComparison.Ordinal);
    }

    // ── Credential-redaction regression (§17) ──────────────────────────────────
    // FIX 6: tests now assert on the actual EMITTED helper source rather than
    // re-implementing the redaction logic inline.  See MqExpectAzureServiceBusEmitTests.cs
    // for the full emit-level suite (tests 13–15) covering two-layer redaction and
    // Base64 key patterns.

    [Fact]
    public void RedactCredentials_EmittedHelper_ContainsLiteralReplacementLayer()
    {
        var fragment = _provider.Emit(
            new MqExpectAzureServiceBusModel("asb", "orders", null, null, "hello", null),
            new StubCompileContext("s"));
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains(".Replace(connStr,", helperSource, StringComparison.Ordinal);
        Assert.Contains("***", helperSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactCredentials_EmittedHelper_ContainsSharedAccessKeyRegexLayer()
    {
        var fragment = _provider.Emit(
            new MqExpectAzureServiceBusModel("asb", "orders", null, null, "hello", null),
            new StubCompileContext("s"));
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("SharedAccessKey=[^;]*", helperSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactCredentials_EmittedHelper_ContainsBothLayers()
    {
        var fragment = _provider.Emit(
            new MqExpectAzureServiceBusModel("asb", "orders", null, null, "hello", null),
            new StubCompileContext("s"));
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains(".Replace(connStr,", helperSource, StringComparison.Ordinal);
        Assert.Contains("SharedAccessKey=[^;]*", helperSource, StringComparison.Ordinal);
    }
}
