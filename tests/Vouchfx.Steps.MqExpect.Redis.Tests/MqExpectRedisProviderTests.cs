// Tests for MqExpectRedisProvider — bind/validate/schema/registry.
//
// Covers:
//   1.  Bind: full YAML step (target/stream/match) → correct model.
//   2.  Bind: non-mapping node → safe empty model (defensive).
//   3.  Bind: match absent → empty RedisMatch (no criteria).
//   4.  Bind: payloadContains / json all parsed.
//   5.  Validate: valid model + matching redis dependency → IsValid.
//   6.  Validate: dependency type comparison is case-insensitive.
//   7.  Validate: empty target → invalid.
//   8.  Validate: empty stream → invalid.
//   9.  Validate: match with no criterion → invalid.
//   10. Validate: target not in DeclaredDependencies → invalid.
//   11. Validate: target declared but type is wrong (e.g. "kafka") → invalid.
//   12. Registry: provider discoverable via StepKindRegistry with key "mq-expect.redis".
//   13. Registry: SchemaFragment contains "payloadContains".
//   14. Resources: yields a redis ResourceRequirement whose Name equals model.Target.
//   15. CompileReferenceAssemblies: contains the StackExchange.Redis assembly.
//   16. CompileReferenceAssemblies: contains the JsonPath.Net assembly.
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Redis;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.MqExpect.Redis.Tests;

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

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
        new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);
}

/// <summary>Stub <see cref="IBindingContext"/> for tests that need no binding services.</summary>
internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="MqExpectRedisProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class MqExpectRedisProviderTests
{
    private readonly MqExpectRedisProvider _provider = new();
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
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("shipped") },
                    {
                        "json", new YamlMappingNode
                        {
                            { "$.status", new YamlScalarNode("PENDING") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("cache", model.Target);
        Assert.Equal("orders", model.Stream);
        Assert.Equal("shipped", model.Match.PayloadContains);
        Assert.NotNull(model.Match.Json);
        Assert.Equal("PENDING", model.Match.Json!["$.status"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsSafeEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("not-a-mapping"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Stream);
        Assert.Null(model.Match.PayloadContains);
        Assert.Null(model.Match.Json);
    }

    // ── 3. Bind: match absent ──────────────────────────────────────────────────

    [Fact]
    public void Bind_MatchAbsent_ReturnsEmptyMatch()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("cache") },
            { "stream", new YamlScalarNode("orders") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Match.PayloadContains);
        Assert.Null(model.Match.Json);
    }

    // ── 4. Bind: payloadContains only ──────────────────────────────────────────

    [Fact]
    public void Bind_PayloadContainsOnly_JsonIsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("cache") },
            { "stream", new YamlScalarNode("orders") },
            {
                "match", new YamlMappingNode
                {
                    { "payloadContains", new YamlScalarNode("shipped") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("shipped", model.Match.PayloadContains);
        Assert.Null(model.Match.Json);
    }

    // ── 5. Validate: valid model ───────────────────────────────────────────────

    [Fact]
    public void Validate_ValidModel_IsValid()
    {
        var model = new MqExpectRedisModel(
            Target: "cache", Stream: "orders",
            Match: new RedisMatch(PayloadContains: "shipped", Json: null));

        var result = _provider.Validate(model, new StubProjectContext(RedisDeps("cache")));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 6. Validate: dependency type comparison is case-sensitive (feat/case-sensitive-kinds) ──

    [Fact]
    public void Validate_DependencyTypeComparison_WrongCaseIsInvalid()
    {
        var model = new MqExpectRedisModel("cache", "orders", new RedisMatch("x", null));
        var deps = new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = "Redis" };

        var result = _provider.Validate(model, new StubProjectContext(deps));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cache", StringComparison.Ordinal));
    }

    // ── 7. Validate: empty target ──────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var model = new MqExpectRedisModel("", "orders", new RedisMatch("x", null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    // ── 8. Validate: empty stream ──────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyStream_IsInvalid()
    {
        var model = new MqExpectRedisModel("cache", "", new RedisMatch("x", null));

        var result = _provider.Validate(model, new StubProjectContext(RedisDeps("cache")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("stream"));
    }

    // ── 9. Validate: match with no criterion ──────────────────────────────────

    [Fact]
    public void Validate_MatchWithNoCriterion_IsInvalid()
    {
        var model = new MqExpectRedisModel("cache", "orders", new RedisMatch(null, null));

        var result = _provider.Validate(model, new StubProjectContext(RedisDeps("cache")));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("match"));
    }

    // ── 10. Validate: target not in DeclaredDependencies ─────────────────────

    [Fact]
    public void Validate_TargetNotInDependencies_IsInvalid()
    {
        var model = new MqExpectRedisModel("cache", "orders", new RedisMatch("x", null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cache") && e.Contains("redis"));
    }

    // ── 11. Validate: target declared but wrong type ──────────────────────────

    [Fact]
    public void Validate_TargetWrongType_IsInvalid()
    {
        var model = new MqExpectRedisModel("cache", "orders", new RedisMatch("x", null));
        var deps = new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = "kafka" };

        var result = _provider.Validate(model, new StubProjectContext(deps));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("kafka"));
    }

    // ── 12. Registry: provider discoverable ──────────────────────────────────

    [Fact]
    public void Registry_ProviderIsDiscoverable_ByKey()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(MqExpectRedisProvider).Assembly });

        Assert.True(
            registry.TryGet("mq-expect.redis", out var provider) && provider is not null,
            "Provider for 'mq-expect.redis' must be discoverable via StepKindRegistry.");
    }

    // ── 13. Registry: SchemaFragment contains 'payloadContains' ──────────────

    [Fact]
    public void SchemaFragment_ContainsPayloadContains()
    {
        Assert.Contains("payloadContains", _provider.SchemaFragment.Json, StringComparison.Ordinal);
    }

    // ── 14. Resources: yields redis ResourceRequirement ──────────────────────

    [Fact]
    public void Resources_YieldsRedisRequirementWithCorrectName()
    {
        var model = new MqExpectRedisModel("cache", "orders", new RedisMatch("x", null));
        var resources = _provider.Resources(model).ToList();

        Assert.Single(resources);
        Assert.Equal("redis", resources[0].Family);
        Assert.Equal("cache", resources[0].Name);
    }

    // ── 15. CompileReferenceAssemblies: contains StackExchange.Redis ─────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsStackExchangeRedisAssembly()
    {
        var refs = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(refs, a => a.GetName().Name == "StackExchange.Redis");
    }

    // ── 16. CompileReferenceAssemblies: contains JsonPath.Net ────────────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsJsonPathNetAssembly()
    {
        var refs = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(refs, a => a.GetName().Name == "JsonPath.Net");
    }
}
