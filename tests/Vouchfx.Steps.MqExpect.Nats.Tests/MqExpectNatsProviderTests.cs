// Tests for MqExpectNatsProvider — bind / validate / schema / registry.
//
// Covers:
//   1. Bind: full mapping with target/subject/stream/payloadContains/json → populated model.
//   2. Bind: non-mapping node → safe empty-criteria model (defensive).
//   3. Bind: no 'stream' present → Stream is null.
//   4. Bind: match with only json (no payloadContains) → PayloadContains is null.
//   5. Bind: match with only payloadContains (no json) → Json is null.
//   6. Validate: valid model with payloadContains + matching nats dep → IsValid.
//   7. Validate: valid model with json only + matching nats dep → IsValid.
//   8. Validate: dependency type comparison is case-insensitive ("Nats" matches "nats").
//   9. Validate: no criteria declared (neither payloadContains nor json) → invalid.
//  10. Validate: empty json map is treated as absent (same as no criteria).
//  11. Validate: empty target → invalid.
//  12. Validate: empty subject → invalid.
//  13. Validate: target declared as wrong type → invalid.
//  14. Validate: target not declared at all → invalid.
//  15. Registry: provider discoverable at key "mq-expect.nats".
//  16. Registry: SchemaFragment references "match".
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Nats;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqExpect.Nats.Tests;

// ── Stub context implementations ─────────────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> that exposes a configurable
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

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
        new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);
}

/// <summary>
/// Stub <see cref="IBindingContext"/> for tests that do not require
/// binding-stage services.
/// </summary>
internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="MqExpectNatsProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqExpectNatsProviderTests
{
    private readonly MqExpectNatsProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full mapping ─────────────────────────────────────────────────

    /// <summary>
    /// A full YAML mapping (target/subject/stream/payloadContains/json) is
    /// deserialised into the correct model fields.
    /// </summary>
    [Fact]
    public void Bind_FullMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("nats-bus") },
            { "subject", new YamlScalarNode("orders.created") },
            { "stream",  new YamlScalarNode("ORDERS") },
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("ORDER_NEW") },
                    {
                        "json", new YamlMappingNode
                        {
                            { "$.status", new YamlScalarNode("NEW") },
                            { "$.id",     new YamlScalarNode("42") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("nats-bus", model.Target);
        Assert.Equal("orders.created", model.Subject);
        Assert.Equal("ORDERS", model.Stream);
        Assert.Equal("ORDER_NEW", model.Match.PayloadContains);
        Assert.NotNull(model.Match.Json);
        Assert.Equal(2, model.Match.Json!.Count);
        Assert.Equal("NEW", model.Match.Json!["$.status"]);
        Assert.Equal("42", model.Match.Json!["$.id"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    /// <summary>
    /// Binding from a non-mapping node returns a safe all-null match (defensive).
    /// </summary>
    [Fact]
    public void Bind_NonMappingNode_ReturnsEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("bad"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Subject);
        Assert.Null(model.Stream);
        Assert.Null(model.Match.PayloadContains);
        Assert.Null(model.Match.Json);
    }

    // ── 3. Bind: no 'stream' present ──────────────────────────────────────────

    /// <summary>
    /// Omitting the 'stream' field binds <see cref="MqExpectNatsModel.Stream"/>
    /// as <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Bind_NoStream_StreamIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("bus") },
            { "subject", new YamlScalarNode("t") },
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("x") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Stream);
    }

    // ── 4. Bind: match with only json ─────────────────────────────────────────

    /// <summary>
    /// A match block that declares only json (no payloadContains) binds
    /// <see cref="NatsMatch.PayloadContains"/> as <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Bind_MatchWithOnlyJson_PayloadContainsIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("bus") },
            { "subject", new YamlScalarNode("t") },
            {
                "match", new YamlMappingNode
                {
                    {
                        "json", new YamlMappingNode
                        {
                            { "$.ok", new YamlScalarNode("true") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Match.PayloadContains);
        Assert.NotNull(model.Match.Json);
    }

    // ── 5. Bind: match with only payloadContains ──────────────────────────────

    /// <summary>
    /// A match block that declares only payloadContains (no json) binds
    /// <see cref="NatsMatch.Json"/> as <see langword="null"/>.
    /// </summary>
    [Fact]
    public void Bind_MatchWithOnlyPayloadContains_JsonIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("bus") },
            { "subject", new YamlScalarNode("t") },
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("needle") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("needle", model.Match.PayloadContains);
        Assert.Null(model.Match.Json);
    }

    // ── 6. Validate: payloadContains only + valid dep ─────────────────────────

    /// <summary>
    /// A model with only payloadContains (no json) and a matching nats dependency passes.
    /// </summary>
    [Fact]
    public void Validate_PayloadContainsOnly_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "nats",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel(
            "bus", "t",
            new NatsMatch(PayloadContains: "needle", Json: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 7. Validate: json only + valid dep ────────────────────────────────────

    /// <summary>
    /// A model with only a non-empty json map (no payloadContains) and a matching
    /// nats dependency passes validation.
    /// </summary>
    [Fact]
    public void Validate_JsonOnly_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "nats",
        };
        var ctx = new StubProjectContext(deps);

        var json = new Dictionary<string, string> { ["$.x"] = "1" };
        var model = MakeModel(
            "bus", "t",
            new NatsMatch(PayloadContains: null, Json: json));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 8. Validate: case-insensitive dep type ────────────────────────────────

    /// <summary>
    /// Dependency type comparison is case-sensitive (pre-GA decision,
    /// feat/case-sensitive-kinds): "Nats" does not match the canonical "nats".
    /// </summary>
    [Fact]
    public void Validate_DependencyTypeWrongCase_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "Nats",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel(
            "bus", "t",
            new NatsMatch(PayloadContains: "x", Json: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("bus", StringComparison.Ordinal));
    }

    // ── 9. Validate: no criteria declared ─────────────────────────────────────

    /// <summary>
    /// A match block with neither payloadContains nor json is rejected.
    /// </summary>
    [Fact]
    public void Validate_NoCriteria_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "nats",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel(
            "bus", "t",
            new NatsMatch(PayloadContains: null, Json: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("at least one criterion", StringComparison.Ordinal));
    }

    // ── 10. Validate: empty json map == no criteria ────────────────────────────

    /// <summary>
    /// An empty (zero-entry) json map is treated the same as absent — no criteria.
    /// </summary>
    [Fact]
    public void Validate_EmptyJsonMap_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "nats",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel(
            "bus", "t",
            new NatsMatch(PayloadContains: null, Json: new Dictionary<string, string>()));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("at least one criterion", StringComparison.Ordinal));
    }

    // ── 11. Validate: empty target ─────────────────────────────────────────────

    /// <summary>An empty target produces a validation error.</summary>
    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var ctx = new StubProjectContext();
        var model = MakeModel(string.Empty, "t", new NatsMatch("x", null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'target'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 12. Validate: empty subject ────────────────────────────────────────────

    /// <summary>An empty subject produces a validation error.</summary>
    [Fact]
    public void Validate_EmptySubject_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "nats",
        };
        var ctx = new StubProjectContext(deps);
        var model = MakeModel("bus", string.Empty, new NatsMatch("x", null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'subject'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 13. Validate: target declared with wrong type ─────────────────────────

    /// <summary>
    /// A target declared as "postgres" rather than "nats" produces a reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_WrongDependencyType_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);
        var model = MakeModel("bus", "t", new NatsMatch("x", null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("bus", StringComparison.Ordinal) &&
            e.Contains("nats dependency", StringComparison.Ordinal));
    }

    // ── 14. Validate: target not declared ─────────────────────────────────────

    /// <summary>
    /// A target that is completely absent from the declared-dependencies map is rejected.
    /// </summary>
    [Fact]
    public void Validate_TargetNotDeclared_IsInvalid()
    {
        var ctx = new StubProjectContext();
        var model = MakeModel("missing", "t", new NatsMatch("x", null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("missing", StringComparison.Ordinal) &&
            e.Contains("nats dependency", StringComparison.Ordinal));
    }

    // ── 15. Registry: provider discoverable ────────────────────────────────────

    /// <summary>
    /// Scanning the provider assembly via <see cref="StepKindRegistry.BuildAndFreeze(System.Collections.Generic.IEnumerable{System.Reflection.Assembly})"/>
    /// discovers <see cref="MqExpectNatsProvider"/> at key <c>"mq-expect.nats"</c>.
    /// </summary>
    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqExpectNatsProvider).Assembly });

        var found = registry.TryGet("mq-expect.nats", out var registered);

        Assert.True(found, "Expected 'mq-expect.nats' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("mq-expect", registered!.Kind.Family);
        Assert.Equal("nats", registered.Kind.Provider);
        Assert.IsType<MqExpectNatsProvider>(registered.Instance);
    }

    // ── 16. Registry: SchemaFragment references "match" ────────────────────────

    /// <summary>
    /// The discovered provider's <see cref="JsonSchemaFragment"/> must be non-null
    /// and its JSON must reference the <c>match</c> field.
    /// </summary>
    [Fact]
    public void Provider_SchemaFragment_ContainsMatch()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqExpectNatsProvider).Assembly });

        registry.TryGet("mq-expect.nats", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("match", registered.SchemaFragment!.Json,
            StringComparison.Ordinal);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static MqExpectNatsModel MakeModel(
        string target,
        string subject,
        NatsMatch match,
        string? stream = null)
        => new(
            Target: target,
            Subject: subject,
            Stream: stream,
            Match: match);
}
