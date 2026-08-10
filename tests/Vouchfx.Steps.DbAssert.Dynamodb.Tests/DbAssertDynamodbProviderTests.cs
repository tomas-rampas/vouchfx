// Tests for DbAssertDynamodbProvider — bind/validate/schema/registry/resources.
//
// Covers:
//   1.  Bind: full YAML step (target/table/key/expect) → correct model.
//   2.  Bind: non-mapping node → safe empty model.
//   3.  Bind: expect.exists absent → null (provider defaults to true at Emit/Validate time).
//   4.  Validate: valid model (exists default, item assertions) → IsValid.
//   5.  Validate: empty target/table/key → invalid.
//   6.  Validate: exists:false + item → invalid (coherence).
//   7.  Validate: key with a nested object attribute → invalid.
//   8.  Validate: key containing a {placeholder} (unparsable) → skipped, still valid.
//   9.  Validate: target not declared / wrong dependency type → invalid.
//   10. Registry: provider discoverable via StepKindRegistry with key "db-assert.dynamodb".
//   11. Registry: SchemaFragment contains "key".
//   12. Resources: yields a dynamodb ResourceRequirement whose Name equals model.Target.
//   13. CompileReferenceAssemblies: contains the AWSSDK.DynamoDBv2 assembly.
//
// All tests are non-docker.
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Dynamodb;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.DbAssert.Dynamodb.Tests;

file sealed class StubProjectContext : IProjectContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    internal StubProjectContext(IReadOnlyDictionary<string, string>? deps = null)
    {
        DeclaredDependencies = deps ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; } =
        new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);
}

internal sealed class StubBindingContext : IBindingContext { }

public sealed class DbAssertDynamodbProviderTests
{
    private readonly DbAssertDynamodbProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML mapping ─────────────────────────────────────────────

    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("orders") },
            { "table", new YamlScalarNode("Orders") },
            { "key", new YamlScalarNode("{\"orderId\":\"1001\"}") },
            {
                "expect", new YamlMappingNode
                {
                    { "exists", new YamlScalarNode("true") },
                    {
                        "item", new YamlMappingNode
                        {
                            { "status", new YamlScalarNode("SHIPPED") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("orders", model.Target);
        Assert.Equal("Orders", model.Table);
        Assert.Equal("{\"orderId\":\"1001\"}", model.Key);
        Assert.True(model.Expect.Exists);
        Assert.NotNull(model.Expect.Item);
        Assert.Equal("SHIPPED", model.Expect.Item!["status"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    [Fact]
    public void Bind_NonMappingNode_ReturnsSafeEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("not-a-mapping"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Table);
        Assert.Equal(string.Empty, model.Key);
        Assert.Null(model.Expect.Exists);
        Assert.Null(model.Expect.Item);
    }

    // ── 3. Bind: expect.exists absent → null ──────────────────────────────────

    [Fact]
    public void Bind_ExistsAbsent_IsNull()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("orders") },
            { "table", new YamlScalarNode("Orders") },
            { "key", new YamlScalarNode("{\"orderId\":\"1001\"}") },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Null(model.Expect.Exists);
    }

    // ── 4. Validate: valid model ──────────────────────────────────────────────

    [Fact]
    public void Validate_ValidModel_IsValid()
    {
        var model = new DbAssertDynamodbModel(
            "orders", "Orders", "{\"orderId\":\"1001\"}",
            new DynamoExpectation(
                Exists: null,
                Item: new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = "SHIPPED" }));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["orders"] = "dynamodb" }));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 5. Validate: empty target/table/key ───────────────────────────────────

    [Fact]
    public void Validate_EmptyFields_IsInvalid()
    {
        var model = new DbAssertDynamodbModel(
            "", "", "", new DynamoExpectation(null, null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
        Assert.Contains(result.Errors, e => e.Contains("table"));
        Assert.Contains(result.Errors, e => e.Contains("key"));
    }

    // ── 6. Validate: exists:false + item → invalid ────────────────────────────

    [Fact]
    public void Validate_ExistsFalseWithItem_IsInvalid()
    {
        var model = new DbAssertDynamodbModel(
            "orders", "Orders", "{\"orderId\":\"1001\"}",
            new DynamoExpectation(
                Exists: false,
                Item: new Dictionary<string, string>(StringComparer.Ordinal) { ["status"] = "SHIPPED" }));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["orders"] = "dynamodb" }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("exists: false"));
    }

    // ── 7. Validate: key with a nested object attribute ───────────────────────

    [Fact]
    public void Validate_KeyWithNestedObject_IsInvalid()
    {
        var model = new DbAssertDynamodbModel(
            "orders", "Orders", "{\"orderId\":{\"nested\":true}}",
            new DynamoExpectation(true, null));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["orders"] = "dynamodb" }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("orderId"));
    }

    // ── 8. Validate: key with an unparsable {placeholder} is skipped ─────────

    [Fact]
    public void Validate_KeyWithUnquotedPlaceholder_SkipsShapeCheck_StillValid()
    {
        var model = new DbAssertDynamodbModel(
            "orders", "Orders", "{\"orderId\": {orderId}}",
            new DynamoExpectation(true, null));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["orders"] = "dynamodb" }));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 9. Validate: target dependency reconciliation ─────────────────────────

    [Fact]
    public void Validate_TargetNotDeclared_IsInvalid()
    {
        var model = new DbAssertDynamodbModel(
            "orders", "Orders", "{\"orderId\":\"1001\"}", new DynamoExpectation(true, null));

        var result = _provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    [Fact]
    public void Validate_TargetWrongDependencyType_IsInvalid()
    {
        var model = new DbAssertDynamodbModel(
            "orders", "Orders", "{\"orderId\":\"1001\"}", new DynamoExpectation(true, null));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["orders"] = "postgres" }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    /// <summary>
    /// Dependency type comparison is case-sensitive (pre-GA decision,
    /// feat/case-sensitive-kinds): "Dynamodb" does not match the canonical "dynamodb" — treated
    /// identically to a genuinely mismatched type.
    /// </summary>
    [Fact]
    public void Validate_TargetWrongCaseDependencyType_IsInvalid()
    {
        var model = new DbAssertDynamodbModel(
            "orders", "Orders", "{\"orderId\":\"1001\"}", new DynamoExpectation(true, null));

        var result = _provider.Validate(model, new StubProjectContext(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["orders"] = "Dynamodb" }));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target"));
    }

    // ── 10. Registry: provider discoverable ───────────────────────────────────

    [Fact]
    public void Registry_ProviderIsDiscoverable_ByKey()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(DbAssertDynamodbProvider).Assembly });

        Assert.True(
            registry.TryGet("db-assert.dynamodb", out var provider) && provider is not null,
            "Provider for 'db-assert.dynamodb' must be discoverable via StepKindRegistry.");
    }

    // ── 11. Registry: SchemaFragment contains 'key' ───────────────────────────

    [Fact]
    public void SchemaFragment_ContainsKey()
    {
        Assert.Contains("\"key\"", _provider.SchemaFragment.Json, StringComparison.Ordinal);
    }

    // ── 12. Resources: yields dynamodb ResourceRequirement ───────────────────

    [Fact]
    public void Resources_YieldsDynamodbRequirementWithCorrectName()
    {
        var model = new DbAssertDynamodbModel(
            "orders", "Orders", "{\"orderId\":\"1001\"}", new DynamoExpectation(true, null));
        var resources = _provider.Resources(model).ToList();

        Assert.Single(resources);
        Assert.Equal("dynamodb", resources[0].Family);
        Assert.Equal("orders", resources[0].Name);
    }

    // ── 13. CompileReferenceAssemblies: contains AWSSDK.DynamoDBv2 ────────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsDynamoDbAssembly()
    {
        var refs = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(refs, a => a.GetName().Name == "AWSSDK.DynamoDBv2");
    }
}
