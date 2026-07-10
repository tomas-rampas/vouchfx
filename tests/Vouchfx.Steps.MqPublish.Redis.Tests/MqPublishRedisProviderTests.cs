// Tests for MqPublishRedisProvider — bind/validate/schema/registry.
//
// Covers:
//   1.  Bind: full YAML step (target/stream/payload) → correct model.
//   2.  Bind: non-mapping node → safe empty model (defensive).
//   3.  Validate: valid model + matching redis dependency → IsValid.
//   4.  Validate: dependency type comparison is case-insensitive.
//   5.  Validate: empty target → invalid.
//   6.  Validate: empty stream → invalid.
//   7.  Validate: empty payload → invalid.
//   8.  Validate: target not in DeclaredDependencies → invalid.
//   9.  Validate: target declared but type is wrong (e.g. "kafka") → invalid.
//   10. Registry: provider discoverable via StepKindRegistry with key "mq-publish.redis".
//   11. Registry: SchemaFragment contains "stream".
//   12. Resources: yields a redis ResourceRequirement whose Name equals model.Target.
//   13. CompileReferenceAssemblies: contains the StackExchange.Redis assembly.
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Redis;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqPublish.Redis.Tests;

// ── Stub contexts ─────────────────────────────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> exposing a configurable
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

/// <summary>Stub <see cref="IBindingContext"/> for tests that need no binding services.</summary>
internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="MqPublishRedisProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqPublishRedisProviderTests
{
    private readonly MqPublishRedisProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    private static Dictionary<string, string> RedisDeps(string name = "cache")
        => new Dictionary<string, string>(StringComparer.Ordinal) { [name] = "redis" };

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("cache") },
            { "stream", new YamlScalarNode("orders") },
            { "payload", new YamlScalarNode("{\"id\":1}") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("cache", model.Target);
        Assert.Equal("orders", model.Stream);
        Assert.Equal("{\"id\":1}", model.Payload);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsSafeEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("not-a-mapping"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Stream);
        Assert.Equal(string.Empty, model.Payload);
    }

    // ── 3. Validate: valid model ───────────────────────────────────────────────

    [Fact]
    public void Validate_ValidModel_IsValid()
    {
        var model = new MqPublishRedisModel(Target: "cache", Stream: "orders", Payload: "hello");

        var result = _provider.Validate(model, new StubProjectContext(RedisDeps("cache")));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 4. Validate: dependency type comparison is case-insensitive ────────────

    [Fact]
    public void Validate_DependencyTypeComparison_IsCaseInsensitive()
    {
        var model = new MqPublishRedisModel("cache", "orders", "hello");
        var deps = new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = "Redis" };

        var result = _provider.Validate(model, new StubProjectContext(deps));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 5. Validate: empty target ──────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var model = new MqPublishRedisModel("", "orders", "hello");

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    // ── 6. Validate: empty stream ──────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyStream_IsInvalid()
    {
        var model = new MqPublishRedisModel("cache", "", "hello");

        var result = _provider.Validate(model, new StubProjectContext(RedisDeps("cache")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("stream"));
    }

    // ── 7. Validate: empty payload ─────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyPayload_IsInvalid()
    {
        var model = new MqPublishRedisModel("cache", "orders", "");

        var result = _provider.Validate(model, new StubProjectContext(RedisDeps("cache")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("payload"));
    }

    // ── 8. Validate: target not in DeclaredDependencies ─────────────────────

    [Fact]
    public void Validate_TargetNotInDependencies_IsInvalid()
    {
        var model = new MqPublishRedisModel("cache", "orders", "hello");

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cache") && e.Contains("redis"));
    }

    // ── 9. Validate: target declared but wrong type ──────────────────────────

    [Fact]
    public void Validate_TargetWrongType_IsInvalid()
    {
        var model = new MqPublishRedisModel("cache", "orders", "hello");
        var deps = new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = "kafka" };

        var result = _provider.Validate(model, new StubProjectContext(deps));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("kafka"));
    }

    // ── 10. Registry: provider discoverable ──────────────────────────────────

    [Fact]
    public void Registry_ProviderIsDiscoverable_ByKey()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqPublishRedisProvider).Assembly });

        Assert.True(
            registry.TryGet("mq-publish.redis", out var provider) && provider is not null,
            "Provider for 'mq-publish.redis' must be discoverable via StepKindRegistry.");
    }

    // ── 11. Registry: SchemaFragment contains 'stream' ───────────────────────

    [Fact]
    public void SchemaFragment_ContainsStream()
    {
        Assert.Contains("stream", _provider.SchemaFragment.Json, StringComparison.Ordinal);
    }

    // ── 12. Resources: yields redis ResourceRequirement ──────────────────────

    [Fact]
    public void Resources_YieldsRedisRequirementWithCorrectName()
    {
        var model = new MqPublishRedisModel("cache", "orders", "hello");
        var resources = _provider.Resources(model).ToList();

        Assert.Single(resources);
        Assert.Equal("redis", resources[0].Family);
        Assert.Equal("cache", resources[0].Name);
    }

    // ── 13. CompileReferenceAssemblies: contains StackExchange.Redis ─────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsStackExchangeRedisAssembly()
    {
        var refs = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(refs, a => a.GetName().Name == "StackExchange.Redis");
    }
}
