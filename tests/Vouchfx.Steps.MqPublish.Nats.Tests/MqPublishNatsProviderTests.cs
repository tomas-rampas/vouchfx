// Tests for MqPublishNatsProvider — bind / validate / schema / registry.
//
// Covers:
//   1. Bind: full YAML step (target/subject/stream/payload) → populated model.
//   2. Bind: non-mapping node → safe empty-stringed model (defensive).
//   3. Bind: no 'stream' present → Stream is null.
//   4. Validate: valid model + matching nats dependency → IsValid.
//   5. Validate: dependency type comparison is case-insensitive ("Nats" matches "nats").
//   6. Validate: empty target → invalid with clear message.
//   7. Validate: empty subject → invalid with clear message.
//   8. Validate: empty payload → invalid with clear message.
//   9. Validate: target declared as a postgres dependency (wrong type) → invalid.
//  10. Validate: target not declared at all → invalid.
//  11. Registry: provider discoverable at key "mq-publish.nats".
//  12. Registry: SchemaFragment references "payload".
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Nats;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.Nats.Tests;

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
/// Non-docker unit tests for <see cref="MqPublishNatsProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqPublishNatsProviderTests
{
    private readonly MqPublishNatsProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    /// <summary>
    /// A full YAML mapping with target, subject, stream, and payload is
    /// deserialised into the correct model fields.
    /// </summary>
    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("nats-bus") },
            { "subject", new YamlScalarNode("orders.created") },
            { "stream",  new YamlScalarNode("ORDERS") },
            { "payload", new YamlScalarNode("{\"id\":42,\"status\":\"NEW\"}") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("nats-bus", model.Target);
        Assert.Equal("orders.created", model.Subject);
        Assert.Equal("ORDERS", model.Stream);
        Assert.Equal("{\"id\":42,\"status\":\"NEW\"}", model.Payload);
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
        Assert.Equal(string.Empty, model.Subject);
        Assert.Null(model.Stream);
        Assert.Equal(string.Empty, model.Payload);
    }

    // ── 3. Bind: no 'stream' present ──────────────────────────────────────────

    /// <summary>
    /// A step that omits the 'stream' field binds <see cref="MqPublishNatsModel.Stream"/>
    /// as <see langword="null"/> (the stream name is derived from the subject at emit time).
    /// </summary>
    [Fact]
    public void Bind_NoStream_StreamIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target",  new YamlScalarNode("nats-bus") },
            { "subject", new YamlScalarNode("orders.created") },
            { "payload", new YamlScalarNode("hello") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Stream);
    }

    // ── 4. Validate: valid model with matching nats dependency ────────────────

    /// <summary>
    /// A fully valid model whose target is declared as type "nats" passes validation.
    /// </summary>
    [Fact]
    public void Validate_ValidModel_WithMatchingNatsDependency_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nats-bus"] = "nats",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("nats-bus", "orders.created", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
    }

    // ── 5. Validate: dependency type comparison is case-insensitive ────────────

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

        var model = MakeModel("bus", "t", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("bus", StringComparison.Ordinal));
    }

    // ── 6. Validate: empty target ──────────────────────────────────────────────

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

    // ── 7. Validate: empty subject ─────────────────────────────────────────────

    /// <summary>
    /// An empty subject produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptySubject_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "nats",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("bus", string.Empty, "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'subject'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 8. Validate: empty payload ─────────────────────────────────────────────

    /// <summary>
    /// An empty payload produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyPayload_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bus"] = "nats",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("bus", "t", string.Empty);

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'payload'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 9. Validate: target declared with wrong type ───────────────────────────

    /// <summary>
    /// When the target is declared but its type is "postgres" (not "nats"), the
    /// validator returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetDeclaredAsWrongType_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nats-bus"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = MakeModel("nats-bus", "t", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("nats-bus", StringComparison.Ordinal) &&
            e.Contains("nats dependency", StringComparison.Ordinal));
    }

    // ── 10. Validate: target not declared ─────────────────────────────────────

    /// <summary>
    /// When the target is not present in the declared-dependencies map at all, the
    /// validator returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetNotInDeclaredDependencies_IsInvalid()
    {
        var ctx = new StubProjectContext();

        var model = MakeModel("nats-bus", "t", "hello");

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("nats-bus", StringComparison.Ordinal) &&
            e.Contains("nats dependency", StringComparison.Ordinal));
    }

    // ── 11. Registry: provider discoverable ────────────────────────────────────

    /// <summary>
    /// Scanning the provider assembly via <see cref="StepKindRegistry.BuildAndFreeze(System.Collections.Generic.IEnumerable{System.Reflection.Assembly})"/>
    /// discovers <see cref="MqPublishNatsProvider"/> at key <c>"mq-publish.nats"</c>.
    /// </summary>
    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqPublishNatsProvider).Assembly });

        var found = registry.TryGet("mq-publish.nats", out var registered);

        Assert.True(found, "Expected 'mq-publish.nats' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("mq-publish", registered!.Kind.Family);
        Assert.Equal("nats", registered.Kind.Provider);
        Assert.IsType<MqPublishNatsProvider>(registered.Instance);
    }

    // ── 12. Registry: SchemaFragment references "payload" ─────────────────────

    /// <summary>
    /// The discovered provider's <see cref="JsonSchemaFragment"/> must be non-null
    /// and its JSON must reference the <c>payload</c> field.
    /// </summary>
    [Fact]
    public void Provider_SchemaFragment_ContainsPayload()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqPublishNatsProvider).Assembly });

        registry.TryGet("mq-publish.nats", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("payload", registered.SchemaFragment!.Json,
            StringComparison.Ordinal);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static MqPublishNatsModel MakeModel(
        string target,
        string subject,
        string payload,
        string? stream = null)
        => new(
            Target: target,
            Subject: subject,
            Stream: stream,
            Payload: payload);
}
