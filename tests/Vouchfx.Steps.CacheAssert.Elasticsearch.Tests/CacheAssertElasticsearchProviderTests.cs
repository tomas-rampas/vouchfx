// Tests for CacheAssertElasticsearchProvider — bind/validate/schema/registry.
//
// Mirrors Vouchfx.Steps.CacheAssert.Redis.Tests/CacheAssertRedisProviderTests.cs.
//
// Covers:
//   1.  Bind: full YAML step with all fields → correct model.
//   2.  Bind: minimal YAML (target/index/expect only) → model with defaults.
//   3.  Bind: non-mapping node → defensive empty model (no exception).
//   4.  Bind: missing 'target' → defensive empty string (validator catches it).
//   5.  Bind: missing 'index' → defensive empty string (validator catches it).
//   6.  Bind: missing 'expect' → default EsExpectation (validator catches it).
//   7.  Bind: expect.count is parsed.
//   8.  Bind: expect.min-count is parsed.
//   9.  Bind: expect.fields (field/value pairs) are parsed.
//   10. Validate: valid model + matching elasticsearch dependency → IsValid.
//   11. Validate: dependency type comparison is case-insensitive.
//   12. Validate: blank index → invalid.
//   13. Validate: target not in DeclaredDependencies → invalid.
//   14. Validate: target declared as wrong type (e.g. "redis") → invalid.
//   15. Validate: invalid JSON in query field → invalid.
//   16. Validate: query must be a JSON object (not array) → invalid.
//   17. Validate: dot-notation in field name → invalid.
//   18. Validate: blank field name → invalid.
//   19. Validate: exact count and min-count both present → count takes precedence (not an error).
//   20. Registry: provider discoverable via StepKindRegistry with key "cache-assert.elasticsearch".
//   21. Registry: SchemaFragment contains "index".
//   22. Resources: yields an elasticsearch ResourceRequirement whose Name equals model.Target.
//   23. CompileReferenceAssemblies: contains System.Net.Http assembly.
//   24. Validate: negative expect.count → invalid.
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Elasticsearch;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.CacheAssert.Elasticsearch.Tests;

// ── Stub contexts ─────────────────────────────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> exposing a configurable
/// <see cref="IProjectContext.DeclaredDependencies"/> map for validator unit tests.
/// </summary>
internal sealed class StubProjectContext : IProjectContext
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
/// Non-docker unit tests for <see cref="CacheAssertElasticsearchProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class CacheAssertElasticsearchProviderTests
{
    private readonly CacheAssertElasticsearchProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("es") },
            { "index", new YamlScalarNode("orders") },
            { "query", new YamlScalarNode("{\"query\":{\"match\":{\"status\":\"active\"}}}") },
            {
                "expect", new YamlMappingNode
                {
                    { "count", new YamlScalarNode("3") },
                    {
                        "fields", new YamlSequenceNode(
                            new YamlMappingNode
                            {
                                { "field", new YamlScalarNode("status") },
                                { "value", new YamlScalarNode("active") },
                            })
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("es", model.Target);
        Assert.Equal("orders", model.Index);
        Assert.Equal("{\"query\":{\"match\":{\"status\":\"active\"}}}", model.Query);
        Assert.Equal(3, model.Expect.Count);
        Assert.NotNull(model.Expect.Fields);
        Assert.Single(model.Expect.Fields!);
        Assert.Equal("status", model.Expect.Fields![0].Field);
        Assert.Equal("active", model.Expect.Fields![0].Value);
    }

    // ── 2. Bind: minimal YAML ─────────────────────────────────────────────────

    [Fact]
    public void Bind_MinimalYamlMapping_ReturnsModelWithDefaults()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("search") },
            { "index", new YamlScalarNode("products") },
            {
                "expect", new YamlMappingNode()
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("search", model.Target);
        Assert.Equal("products", model.Index);
        Assert.Null(model.Query);
        Assert.Null(model.Expect.Count);
        Assert.Equal(1, model.Expect.MinCount);  // default
        Assert.Null(model.Expect.Fields);
    }

    // ── 3. Bind: non-mapping node → defensive empty model (no exception) ─────

    [Fact]
    public void Bind_NonMappingNode_ReturnsEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("bad"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Index);
        Assert.Null(model.Query);
        Assert.Null(model.Expect.Count);
        Assert.Equal(1, model.Expect.MinCount);
        Assert.Null(model.Expect.Fields);
    }

    // ── 4. Bind: missing 'target' → empty string (validator catches it) ───────

    [Fact]
    public void Bind_MissingTarget_ReturnsEmptyTargetString()
    {
        var yaml = new YamlMappingNode
        {
            { "index", new YamlScalarNode("orders") },
            { "expect", new YamlMappingNode() },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
    }

    // ── 5. Bind: missing 'index' → empty string (validator catches it) ────────

    [Fact]
    public void Bind_MissingIndex_ReturnsEmptyIndexString()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("es") },
            { "expect", new YamlMappingNode() },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal(string.Empty, model.Index);
    }

    // ── 6. Bind: missing 'expect' → default EsExpectation (validator catches it) ─

    [Fact]
    public void Bind_MissingExpect_ReturnsDefaultExpectation()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("es") },
            { "index", new YamlScalarNode("orders") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Expect.Count);
        Assert.Equal(1, model.Expect.MinCount);
        Assert.Null(model.Expect.Fields);
    }

    // ── 7. Bind: expect.count is parsed ──────────────────────────────────────

    [Fact]
    public void Bind_ExpectCountScalar_IsParsed()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("es") },
            { "index", new YamlScalarNode("orders") },
            {
                "expect", new YamlMappingNode
                {
                    { "count", new YamlScalarNode("5") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal(5, model.Expect.Count);
    }

    // ── 8. Bind: expect.min-count is parsed ──────────────────────────────────

    [Fact]
    public void Bind_ExpectMinCountScalar_IsParsed()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("es") },
            { "index", new YamlScalarNode("orders") },
            {
                "expect", new YamlMappingNode
                {
                    { "min-count", new YamlScalarNode("7") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal(7, model.Expect.MinCount);
        Assert.Null(model.Expect.Count);
    }

    // ── 9. Bind: expect.fields list ───────────────────────────────────────────

    [Fact]
    public void Bind_ExpectFieldsList_IsParsed()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("es") },
            { "index", new YamlScalarNode("orders") },
            {
                "expect", new YamlMappingNode
                {
                    {
                        "fields", new YamlSequenceNode(
                            new YamlMappingNode
                            {
                                { "field", new YamlScalarNode("type") },
                                { "value", new YamlScalarNode("sale") },
                            },
                            new YamlMappingNode
                            {
                                { "field", new YamlScalarNode("region") },
                                { "value", new YamlScalarNode("EU") },
                            })
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.NotNull(model.Expect.Fields);
        Assert.Equal(2, model.Expect.Fields!.Count);
        Assert.Equal("type", model.Expect.Fields![0].Field);
        Assert.Equal("sale", model.Expect.Fields![0].Value);
        Assert.Equal("region", model.Expect.Fields![1].Field);
        Assert.Equal("EU", model.Expect.Fields![1].Value);
    }

    // ── Validate helper ───────────────────────────────────────────────────────

    private static CacheAssertElasticsearchModel MakeModel(
        string target = "search",
        string index = "orders",
        string? query = null,
        int? count = null,
        int minCount = 1,
        IReadOnlyList<EsFieldAssertion>? fields = null) =>
        new CacheAssertElasticsearchModel(
            Target: target,
            Index: index,
            Query: query,
            Expect: new EsExpectation(Count: count, MinCount: minCount, Fields: fields));

    private static StubProjectContext ElasticDeps(string name = "search") =>
        new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [name] = "elasticsearch",
        });

    // ── 10. Validate: valid model → IsValid ───────────────────────────────────

    [Fact]
    public void Validate_ValidModel_ReturnsSuccess()
    {
        var model = MakeModel();
        var result = _provider.Validate(model, ElasticDeps());

        Assert.True(result.IsValid);
    }

    // ── 11. Validate: dependency type comparison is case-insensitive ──────────

    [Fact]
    public void Validate_DependencyTypeCaseInsensitive_ReturnsSuccess()
    {
        var model = MakeModel(target: "es");
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["es"] = "ELASTICSEARCH",
        });

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
    }

    // ── 12. Validate: blank index → invalid ──────────────────────────────────

    [Fact]
    public void Validate_BlankIndex_ReturnsFailure()
    {
        var model = MakeModel(index: "   ");
        var result = _provider.Validate(model, ElasticDeps());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("index", StringComparison.Ordinal));
    }

    // ── 13. Validate: target not in DeclaredDependencies → invalid ────────────

    [Fact]
    public void Validate_TargetNotDeclared_ReturnsFailure()
    {
        var model = MakeModel(target: "nonexistent");
        var result = _provider.Validate(model, ElasticDeps(name: "search"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("nonexistent", StringComparison.Ordinal));
    }

    // ── 14. Validate: target declared as wrong type → invalid ─────────────────

    [Fact]
    public void Validate_TargetIsWrongType_ReturnsFailure()
    {
        var model = MakeModel(target: "cache");
        var ctx = new StubProjectContext(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cache"] = "redis",
        });

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
    }

    // ── 15. Validate: invalid JSON in query → invalid ─────────────────────────

    [Fact]
    public void Validate_InvalidJsonQuery_ReturnsFailure()
    {
        var model = MakeModel(query: "{not valid json");
        var result = _provider.Validate(model, ElasticDeps());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("query", StringComparison.Ordinal));
    }

    // ── 16. Validate: query is JSON array (not object) → invalid ──────────────

    [Fact]
    public void Validate_QueryIsJsonArray_ReturnsFailure()
    {
        var model = MakeModel(query: "[{\"query\":{\"match_all\":{}}}]");
        var result = _provider.Validate(model, ElasticDeps());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("object", StringComparison.Ordinal));
    }

    // ── 17. Validate: dot-notation in field name → invalid ────────────────────

    [Fact]
    public void Validate_DotNotationFieldName_ReturnsFailure()
    {
        var fields = new[] { new EsFieldAssertion("address.city", "Berlin") };
        var model = MakeModel(fields: fields);
        var result = _provider.Validate(model, ElasticDeps());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("dot", StringComparison.Ordinal));
    }

    // ── 18. Validate: blank field name → invalid ──────────────────────────────

    [Fact]
    public void Validate_BlankFieldName_ReturnsFailure()
    {
        var fields = new[] { new EsFieldAssertion("", "value") };
        var model = MakeModel(fields: fields);
        var result = _provider.Validate(model, ElasticDeps());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("field", StringComparison.Ordinal));
    }

    // ── 19. Validate: count + min-count both present → success (count overrides) ─

    [Fact]
    public void Validate_CountAndMinCountBothPresent_ReturnsSuccess()
    {
        // The schema allows both — count takes precedence (validator does not forbid it).
        var model = MakeModel(count: 2, minCount: 5);
        var result = _provider.Validate(model, ElasticDeps());

        Assert.True(result.IsValid);
    }

    // ── 20. Registry: discoverable with "cache-assert.elasticsearch" ──────────

    [Fact]
    public void Registry_KindDiscoverable_ViaCacheAssertElasticsearch()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(CacheAssertElasticsearchProvider).Assembly });

        Assert.True(
            registry.TryGet("cache-assert.elasticsearch", out var entry),
            "StepKindRegistry must discover 'cache-assert.elasticsearch'.");
        Assert.NotNull(entry);
    }

    // ── 21. Registry: SchemaFragment contains "index" ─────────────────────────

    [Fact]
    public void Registry_SchemaFragment_ContainsIndexProperty()
    {
        var fragment = _provider.SchemaFragment;

        Assert.Contains("\"index\"", fragment.Json, StringComparison.Ordinal);
    }

    // ── 22. Resources: yields elasticsearch requirement ───────────────────────

    [Fact]
    public void Resources_YieldsElasticsearchRequirementWithMatchingName()
    {
        var requirements = _provider.Resources(MakeModel(target: "search")).ToList();

        Assert.Single(requirements);
        Assert.Equal("elasticsearch", requirements[0].Family, StringComparer.Ordinal);
        Assert.Equal("search", requirements[0].Name, StringComparer.Ordinal);
    }

    // ── 23. CompileReferenceAssemblies: contains System.Net.Http ─────────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsSystemNetHttpAssembly()
    {
        var contributor = (ICompileReferenceContributor)_provider;

        Assert.Contains(contributor.CompileReferenceAssemblies.ToList(), a =>
            a.GetName().Name?.Contains("System.Net.Http", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 24. Validate: negative expect.count → invalid ─────────────────────────

    [Fact]
    public void Validate_NegativeCount_ReturnsFailure()
    {
        var model = MakeModel(count: -1);
        var result = _provider.Validate(model, ElasticDeps());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("count", StringComparison.Ordinal));
    }

    // ── 25–28. Validate: index charset — invalid characters ───────────────────

    [Theory]
    [InlineData("orders index")]   // embedded space
    [InlineData("orders?filter")]  // question mark
    [InlineData("orders#alias")]   // hash
    [InlineData("orders\x01")]     // control character
    public void Validate_IndexWithInvalidCharset_ReturnsFailure(string index)
    {
        var model = MakeModel(index: index);
        var result = _provider.Validate(model, ElasticDeps());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("invalid character", StringComparison.OrdinalIgnoreCase));
    }

    // ── 29–30. Validate: index charset — allowed patterns ─────────────────────

    [Theory]
    [InlineData("orders,products")] // comma → multi-index
    [InlineData("logs-*")]          // asterisk → wildcard
    public void Validate_IndexWithAllowedPattern_ReturnsSuccess(string index)
    {
        var model = MakeModel(index: index);
        var result = _provider.Validate(model, ElasticDeps());

        Assert.True(result.IsValid);
    }
}
