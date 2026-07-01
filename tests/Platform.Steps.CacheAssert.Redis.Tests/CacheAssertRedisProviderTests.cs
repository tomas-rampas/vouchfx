// Tests for CacheAssertRedisProvider — bind/validate/schema/registry.
//
// Mirrors Platform.Steps.DbAssert.SqlServer.Tests/DbAssertSqlServerProviderTests.cs.
//
// Covers:
//   1.  Bind: full YAML step (target/key/operation/field/expect) → correct model.
//   2.  Bind: non-mapping node → safe empty model (defensive).
//   3.  Bind: operation scalar is parsed case-insensitively.
//   4.  Bind: expect.exists / expect.length are parsed.
//   5.  Validate: valid get model + matching redis dependency → IsValid.
//   6.  Validate: dependency type comparison is case-insensitive.
//   7.  Validate: empty target → invalid.
//   8.  Validate: empty key → invalid.
//   9.  Validate: hget without field → invalid.
//   10. Validate: get without expect.value → invalid.
//   11. Validate: exists without expect.exists → invalid.
//   12. Validate: ttl without expect.exists → invalid (TTL presence check).
//   13. Validate: hlen/llen/scard without expect.length → invalid.
//   14. Validate: target not in DeclaredDependencies → invalid.
//   15. Validate: target declared but type is wrong (e.g. "postgres") → invalid.
//   16. Registry: provider discoverable via StepKindRegistry with key "cache-assert.redis".
//   17. Registry: SchemaFragment contains "operation".
//   18. Resources: yields a redis ResourceRequirement whose Name equals model.Target.
//   19. CompileReferenceAssemblies: contains the StackExchange.Redis assembly.
//   20. OpToken: every RedisOp maps to its canonical lowercase token.
//
// All tests are non-docker.  No topology is started.
using Platform.Sdk;
using Platform.Steps.CacheAssert.Redis;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.CacheAssert.Redis.Tests;

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
/// Non-docker unit tests for <see cref="CacheAssertRedisProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class CacheAssertRedisProviderTests
{
    private readonly CacheAssertRedisProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("cache") },
            { "key", new YamlScalarNode("user:42") },
            { "operation", new YamlScalarNode("hget") },
            { "field", new YamlScalarNode("status") },
            {
                "expect", new YamlMappingNode
                {
                    { "value", new YamlScalarNode("active") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("cache", model.Target);
        Assert.Equal("user:42", model.Key);
        Assert.Equal(RedisOp.HGet, model.Operation);
        Assert.Equal("status", model.Field);
        Assert.Equal("active", model.Expect.Value);
        Assert.Null(model.Expect.Exists);
        Assert.Null(model.Expect.Length);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("bad"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Key);
        Assert.Equal(RedisOp.Get, model.Operation);
        Assert.Null(model.Field);
        Assert.Null(model.Expect.Value);
        Assert.Null(model.Expect.Exists);
        Assert.Null(model.Expect.Length);
    }

    // ── 3. Bind: operation case-insensitive ────────────────────────────────────

    [Theory]
    [InlineData("get", RedisOp.Get)]
    [InlineData("GET", RedisOp.Get)]
    [InlineData("Exists", RedisOp.Exists)]
    [InlineData("TTL", RedisOp.Ttl)]
    [InlineData("hGet", RedisOp.HGet)]
    [InlineData("HLEN", RedisOp.HLen)]
    [InlineData("llen", RedisOp.LLen)]
    [InlineData("SCard", RedisOp.SCard)]
    public void Bind_OperationScalar_ParsedCaseInsensitively(string raw, RedisOp expected)
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("cache") },
            { "key", new YamlScalarNode("k") },
            { "operation", new YamlScalarNode(raw) },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal(expected, model.Operation);
    }

    // ── 4. Bind: expect.exists / expect.length ────────────────────────────────

    [Fact]
    public void Bind_ExpectExistsAndLength_AreParsed()
    {
        var existsYaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("cache") },
            { "key", new YamlScalarNode("k") },
            { "operation", new YamlScalarNode("exists") },
            {
                "expect", new YamlMappingNode
                {
                    { "exists", new YamlScalarNode("true") },
                }
            },
        };
        var existsModel = _provider.Bind(existsYaml, s_bindCtx);
        Assert.True(existsModel.Expect.Exists);
        Assert.Null(existsModel.Expect.Length);

        var lengthYaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("cache") },
            { "key", new YamlScalarNode("k") },
            { "operation", new YamlScalarNode("llen") },
            {
                "expect", new YamlMappingNode
                {
                    { "length", new YamlScalarNode("3") },
                }
            },
        };
        var lengthModel = _provider.Bind(lengthYaml, s_bindCtx);
        Assert.Equal(3L, lengthModel.Expect.Length);
        Assert.Null(lengthModel.Expect.Exists);
    }

    // ── 5. Validate: valid get model ──────────────────────────────────────────

    [Fact]
    public void Validate_ValidGetModel_WithMatchingRedisDependency_IsValid()
    {
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "redis",
        });

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "user:42",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "active", Exists: null, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
    }

    // ── 6. Validate: dependency type case-insensitive ─────────────────────────

    [Fact]
    public void Validate_DependencyTypeCaseInsensitive_IsValid()
    {
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "Redis",
        });

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Exists,
            Field: null,
            Expect: new RedisExpectation(Value: null, Exists: true, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 7. Validate: empty target ─────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var ctx = new StubProjectContext();

        var model = new CacheAssertRedisModel(
            Target: string.Empty,
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'target'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 8. Validate: empty key ────────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyKey_IsInvalid()
    {
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "redis",
        });

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: string.Empty,
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'key'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 9. Validate: hget without field ───────────────────────────────────────

    [Fact]
    public void Validate_HGetWithoutField_IsInvalid()
    {
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "redis",
        });

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "user:42",
            Operation: RedisOp.HGet,
            Field: null,
            Expect: new RedisExpectation(Value: "active", Exists: null, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'field'", StringComparison.Ordinal) &&
            e.Contains("hget", StringComparison.Ordinal));
    }

    // ── 10. Validate: get without expect.value ────────────────────────────────

    [Fact]
    public void Validate_GetWithoutExpectValue_IsInvalid()
    {
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "redis",
        });

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: null, Exists: null, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("expect.value", StringComparison.Ordinal));
    }

    // ── 11. Validate: exists without expect.exists ────────────────────────────

    [Fact]
    public void Validate_ExistsWithoutExpectExists_IsInvalid()
    {
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "redis",
        });

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Exists,
            Field: null,
            Expect: new RedisExpectation(Value: null, Exists: null, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("expect.exists", StringComparison.Ordinal));
    }

    // ── 12. Validate: ttl without expect.exists ───────────────────────────────

    [Fact]
    public void Validate_TtlWithoutExpectExists_IsInvalid()
    {
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "redis",
        });

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Ttl,
            Field: null,
            // A length is provided but TTL requires exists → still invalid.
            Expect: new RedisExpectation(Value: null, Exists: null, Length: 5L));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("ttl", StringComparison.Ordinal) &&
            e.Contains("expect.exists", StringComparison.Ordinal));
    }

    // ── 13. Validate: hlen/llen/scard without expect.length ───────────────────

    [Theory]
    [InlineData(RedisOp.HLen)]
    [InlineData(RedisOp.LLen)]
    [InlineData(RedisOp.SCard)]
    public void Validate_LengthOpWithoutExpectLength_IsInvalid(RedisOp op)
    {
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "redis",
        });

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: op,
            Field: null,
            Expect: new RedisExpectation(Value: null, Exists: null, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("expect.length", StringComparison.Ordinal));
    }

    // ── 14. Validate: target not in DeclaredDependencies ──────────────────────

    [Fact]
    public void Validate_TargetNotInDeclaredDependencies_IsInvalid()
    {
        var ctx = new StubProjectContext();

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("cache", StringComparison.Ordinal) &&
            e.Contains("redis dependency", StringComparison.Ordinal));
    }

    // ── 15. Validate: target declared as wrong type ───────────────────────────

    [Fact]
    public void Validate_TargetDeclaredAsWrongType_IsInvalid()
    {
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "postgres",
        });

        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("cache", StringComparison.Ordinal) &&
            e.Contains("redis dependency", StringComparison.Ordinal));
    }

    // ── 16. Registry: provider discoverable ───────────────────────────────────

    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(CacheAssertRedisProvider).Assembly });

        var found = registry.TryGet("cache-assert.redis", out var registered);

        Assert.True(found, "Expected 'cache-assert.redis' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("cache-assert", registered!.Kind.Family);
        Assert.Equal("redis", registered.Kind.Provider);
        Assert.IsType<CacheAssertRedisProvider>(registered.Instance);
    }

    // ── 17. Registry: SchemaFragment contains "operation" ─────────────────────

    [Fact]
    public void Provider_SchemaFragment_ContainsOperation()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(CacheAssertRedisProvider).Assembly });

        registry.TryGet("cache-assert.redis", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("operation", registered.SchemaFragment!.Json, StringComparison.Ordinal);
    }

    // ── 18. Resources ─────────────────────────────────────────────────────────

    [Fact]
    public void Resources_YieldsRedisRequirementWithCorrectName()
    {
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));

        var reqs = _provider.Resources(model).ToList();

        Assert.Single(reqs);
        Assert.Equal("redis", reqs[0].Family);
        Assert.Equal("cache", reqs[0].Name);
    }

    // ── 19. CompileReferenceAssemblies ────────────────────────────────────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsStackExchangeRedisAssembly()
    {
        var assemblies = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Contains("StackExchange.Redis", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 20. OpToken mapping ───────────────────────────────────────────────────

    [Theory]
    [InlineData(RedisOp.Get, "get")]
    [InlineData(RedisOp.Exists, "exists")]
    [InlineData(RedisOp.Ttl, "ttl")]
    [InlineData(RedisOp.HGet, "hget")]
    [InlineData(RedisOp.HLen, "hlen")]
    [InlineData(RedisOp.LLen, "llen")]
    [InlineData(RedisOp.SCard, "scard")]
    public void OpToken_MapsEachOpToCanonicalLowercaseToken(RedisOp op, string expected)
    {
        Assert.Equal(expected, CacheAssertRedisProvider.OpToken(op));
    }
}
