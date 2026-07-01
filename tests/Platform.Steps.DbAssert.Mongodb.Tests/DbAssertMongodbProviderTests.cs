// Tests for DbAssertMongodbProvider — bind/validate/schema/registry.
//
// Covers:
//   1. Bind: full YAML step (target/collection/filter/expect.count + expect.document)
//      is deserialised into the correct DbAssertMongodbModel.
//   2. Bind: non-mapping node → safe empty model (defensive).
//   3. Bind: count-only expect → null Document.
//   4. Validate: valid model + matching mongodb dependency → IsValid.
//   5. Validate: dependency type comparison is case-insensitive.
//   6. Validate: empty target → invalid.
//   7. Validate: empty collection → invalid.
//   8. Validate: empty filter → invalid.
//   9. Validate: empty expect (neither count nor document) → invalid with clear message.
//  10. Validate: empty document dictionary + null count → invalid.
//  11. Validate: target not in DeclaredDependencies → invalid.
//  12. Validate: target declared but type is wrong (e.g. "postgres") → invalid.
//  13. Emit: StatementBlock begins and ends with a brace.
//  14. Emit: no 'using var' in the emitted fragment.
//  15. Emit: helper class is named 'DbAssertMongodb_Helpers' (§13.3.1 prefix rule).
//  16. Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//  17. Emit: RequiredUsings contains the MongoDB.Driver namespace.
//  18. Resources: yields a mongodb ResourceRequirement whose Name equals model.Target.
//  19. CompileReferenceAssemblies: contains the MongoDB.Driver assembly.
//  20. Registry: provider discoverable via StepKindRegistry with key "db-assert.mongodb".
//  21. Registry: SchemaFragment contains "collection".
//
// All tests are non-docker.  No topology is started.
using Platform.Engine.Abstractions;
using Platform.Sdk;
using Platform.Steps.DbAssert.Mongodb;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Platform.Steps.DbAssert.Mongodb.Tests;

// ── Stub IProjectContext implementations ──────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> that exposes a configurable
/// <see cref="IProjectContext.DeclaredDependencies"/> map for use in
/// validator unit tests.
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

/// <summary>
/// Stub <see cref="IBindingContext"/> for tests that do not require
/// binding-stage services.
/// </summary>
internal sealed class StubBindingContext : IBindingContext { }

/// <summary>
/// Stub <see cref="ICompileContext"/> for emit tests.
/// </summary>
internal sealed class StubCompileContext : ICompileContext
{
    public StubCompileContext(string stepId) => StepId = stepId;

    /// <inheritdoc />
    public string StepId { get; }

    /// <inheritdoc />
    public string SuiteNamespace => "Generated";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Captures { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
        new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
}

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="DbAssertMongodbProvider"/>
/// (bind, validate, emit, schema, registry discoverability).
/// </summary>
public sealed class DbAssertMongodbProviderTests
{
    private readonly DbAssertMongodbProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    /// <summary>
    /// A full YAML mapping with target, collection, filter, expect.count and
    /// expect.document is deserialised into the correct model fields.
    /// </summary>
    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("testmongo") },
            { "collection", new YamlScalarNode("orders") },
            { "filter", new YamlScalarNode("{\"orderId\": 1}") },
            {
                "expect", new YamlMappingNode
                {
                    { "count", new YamlScalarNode("1") },
                    {
                        "document", new YamlMappingNode
                        {
                            { "status", new YamlScalarNode("SHIPPED") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("testmongo", model.Target);
        Assert.Equal("orders", model.Collection);
        Assert.Equal("{\"orderId\": 1}", model.Filter);

        Assert.Equal(1L, model.Expect.Count);

        Assert.NotNull(model.Expect.Document);
        Assert.Equal("SHIPPED", model.Expect.Document!["status"]);
    }

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

    /// <summary>
    /// Binding from a non-mapping node returns a safe empty model (defensive).
    /// </summary>
    [Fact]
    public void Bind_NonMappingNode_ReturnsEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("bad"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Collection);
        Assert.Equal(string.Empty, model.Filter);
        Assert.Null(model.Expect.Count);
        Assert.Null(model.Expect.Document);
    }

    // ── 3. Bind: count-only expect ────────────────────────────────────────────

    /// <summary>
    /// A step with only count (no document map) is bound correctly.
    /// </summary>
    [Fact]
    public void Bind_CountOnly_NullDocument()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("mongo") },
            { "collection", new YamlScalarNode("orders") },
            { "filter", new YamlScalarNode("{}") },
            {
                "expect", new YamlMappingNode
                {
                    { "count", new YamlScalarNode("0") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal(0L, model.Expect.Count);
        Assert.Null(model.Expect.Document);
    }

    // ── 4. Validate: valid model ──────────────────────────────────────────────

    /// <summary>
    /// A fully valid model whose target is declared as type "mongodb" passes
    /// validation.
    /// </summary>
    [Fact]
    public void Validate_ValidModel_WithMatchingMongodbDependency_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["testmongo"] = "mongodb",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    // ── 5. Validate: dependency type case-insensitive ─────────────────────────

    /// <summary>
    /// Dependency type comparison is case-insensitive ("MongoDB" matches "mongodb").
    /// </summary>
    [Fact]
    public void Validate_DependencyTypeCaseInsensitive_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mymongo"] = "MongoDB",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMongodbModel(
            Target: "mymongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ── 6. Validate: empty target ─────────────────────────────────────────────

    /// <summary>
    /// An empty target produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var ctx = new StubProjectContext();

        var model = new DbAssertMongodbModel(
            Target: string.Empty,
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'target'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 7. Validate: empty collection ────────────────────────────────────────

    /// <summary>
    /// An empty collection produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyCollection_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mymongo"] = "mongodb",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMongodbModel(
            Target: "mymongo",
            Collection: string.Empty,
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'collection'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 8. Validate: empty filter ─────────────────────────────────────────────

    /// <summary>
    /// An empty filter produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyFilter_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mymongo"] = "mongodb",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMongodbModel(
            Target: "mymongo",
            Collection: "orders",
            Filter: string.Empty,
            Expect: new MongoExpectation(Count: 1, Document: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'filter'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 9. Validate: empty expect ─────────────────────────────────────────────

    /// <summary>
    /// When neither count nor document is specified, the validator returns a
    /// clear error naming the exact constraint.
    /// </summary>
    [Fact]
    public void Validate_EmptyExpect_ReturnsExpectMustSpecifyCountOrDocument()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mymongo"] = "mongodb",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMongodbModel(
            Target: "mymongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: null, Document: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("db-assert.mongodb", StringComparison.Ordinal) &&
            e.Contains("'expect'", StringComparison.Ordinal));
    }

    // ── 10. Validate: empty document dictionary ────────────────────────────────

    /// <summary>
    /// An empty document dictionary (Count == 0) with null count also triggers
    /// the expect constraint.
    /// </summary>
    [Fact]
    public void Validate_EmptyDocumentDictionary_ReturnsExpectError()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mymongo"] = "mongodb",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMongodbModel(
            Target: "mymongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(
                Count: null,
                Document: new Dictionary<string, string>(StringComparer.Ordinal)));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'expect'", StringComparison.Ordinal));
    }

    // ── 11. Validate: target not in DeclaredDependencies ──────────────────────

    /// <summary>
    /// When the target is not present in the declared-dependencies map at all,
    /// the validator returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetNotInDeclaredDependencies_IsInvalid()
    {
        var ctx = new StubProjectContext();

        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("testmongo", StringComparison.Ordinal) &&
            e.Contains("mongodb dependency", StringComparison.Ordinal));
    }

    // ── 12. Validate: target declared as wrong type ───────────────────────────

    /// <summary>
    /// When the target is declared but its type is not "mongodb", the validator
    /// returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetDeclaredAsWrongType_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["testmongo"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("testmongo", StringComparison.Ordinal) &&
            e.Contains("mongodb dependency", StringComparison.Ordinal));
    }

    // ── 13. Validate: dot-notation paths rejected (MAJOR M1 guard) ───────────

    /// <summary>
    /// A dot in an expect.document field name (e.g. "address.city") is silently treated
    /// as a literal top-level key lookup by BsonDocument.Contains, producing an incorrect
    /// Fail verdict even when the nested value matches.  Validate must reject it with a
    /// clear error so authors get fast feedback rather than a mis-verdict at execution time.
    /// </summary>
    [Fact]
    public void Validate_DocumentFieldWithDotNotation_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["testmongo"] = "mongodb",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(
                Count: null,
                Document: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["address.city"] = "NYC",
                }));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("address.city", StringComparison.Ordinal) &&
            e.Contains("Dot-notation", StringComparison.Ordinal));
    }

    // ── 14. Emit: StatementBlock braces ───────────────────────────────────────

    /// <summary>
    /// The emitted StatementBlock must begin with '{' and end with '}'.
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_BeginsAndEndsWithBrace()
    {
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var ctx = new StubCompileContext("assert-step");
        var fragment = _provider.Emit(model, ctx);

        var trimmed = fragment.StatementBlock.Trim();
        Assert.True(trimmed.StartsWith('{'),
            "StatementBlock must begin with '{'.");
        Assert.True(trimmed.EndsWith('}'),
            "StatementBlock must end with '}'.");
    }

    // ── 14. Emit: no 'using var' ──────────────────────────────────────────────

    /// <summary>
    /// The emitted fragment (StatementBlock + helpers) must not contain 'using var'
    /// (§13.3.1: prohibited in Roslyn script bodies).
    /// </summary>
    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var ctx = new StubCompileContext("assert-step");
        var fragment = _provider.Emit(model, ctx);

        var allText = fragment.StatementBlock
            + string.Concat(fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", allText, StringComparison.Ordinal);
    }

    // ── 15. Emit: helper class prefix ────────────────────────────────────────

    /// <summary>
    /// The helper class contributed to RequiredHelpers must be named
    /// 'DbAssertMongodb_Helpers' (§13.3.1 provider-id prefix rule).
    /// </summary>
    [Fact]
    public void Emit_HelperClass_IsNamedDbAssertMongodb_Helpers()
    {
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var ctx = new StubCompileContext("assert-step");
        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("DbAssertMongodb_Helpers", StringComparison.Ordinal));
    }

    // ── 16. Emit: step-id sanitisation ───────────────────────────────────────

    /// <summary>
    /// A step id containing hyphens is sanitised to underscores in the emitted
    /// StatementBlock (§13.3.1 SanitiseId rule — emitted variable names may not
    /// contain hyphens).
    /// </summary>
    [Fact]
    public void Emit_HyphenatedStepId_SanitisedToUnderscores()
    {
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var ctx = new StubCompileContext("assert-my-step");
        var fragment = _provider.Emit(model, ctx);

        // The sanitised id "assert_my_step" must appear; the raw "assert-my-step" must not.
        Assert.Contains("assert_my_step", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("assert-my-step", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 17. Emit: RequiredUsings ──────────────────────────────────────────────

    /// <summary>
    /// RequiredUsings must include the MongoDB.Driver namespace so the
    /// emitted helper can resolve MongoClient without a fully-qualified name in
    /// the outer using context.
    /// </summary>
    [Fact]
    public void Emit_RequiredUsings_ContainsMongoDbDriverNamespace()
    {
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var ctx = new StubCompileContext("assert-step");
        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("MongoDB.Driver", fragment.RequiredUsings,
            StringComparer.Ordinal);
    }

    // ── 18. Resources ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resources() yields a single mongodb ResourceRequirement whose Name equals
    /// model.Target.
    /// </summary>
    [Fact]
    public void Resources_YieldsMongodbRequirementWithCorrectName()
    {
        var model = new DbAssertMongodbModel(
            Target: "testmongo",
            Collection: "orders",
            Filter: "{}",
            Expect: new MongoExpectation(Count: 1, Document: null));

        var reqs = _provider.Resources(model).ToList();

        Assert.Single(reqs);
        Assert.Equal("mongodb", reqs[0].Family);
        Assert.Equal("testmongo", reqs[0].Name);
    }

    // ── 19. CompileReferenceAssemblies ────────────────────────────────────────

    /// <summary>
    /// CompileReferenceAssemblies must contain the MongoDB.Driver assembly
    /// so the Roslyn compiler can resolve MongoClient in the emitted helper.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsMongoDbDriverAssembly()
    {
        var assemblies = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Contains("MongoDB.Driver", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 20. Registry: provider discoverable ──────────────────────────────────

    /// <summary>
    /// Scanning the provider assembly via <see cref="StepKindRegistry.BuildAndFreeze"/>
    /// discovers <see cref="DbAssertMongodbProvider"/> at key
    /// <c>"db-assert.mongodb"</c>.
    /// </summary>
    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(DbAssertMongodbProvider).Assembly });

        var found = registry.TryGet("db-assert.mongodb", out var registered);

        Assert.True(found, "Expected 'db-assert.mongodb' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("db-assert", registered!.Kind.Family);
        Assert.Equal("mongodb", registered.Kind.Provider);
        Assert.IsType<DbAssertMongodbProvider>(registered.Instance);
    }

    // ── 21. Registry: SchemaFragment contains "collection" ────────────────────

    /// <summary>
    /// The discovered provider's <see cref="JsonSchemaFragment"/> must be
    /// non-null and its JSON must reference the <c>collection</c> field.
    /// </summary>
    [Fact]
    public void Provider_SchemaFragment_ContainsCollection()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(DbAssertMongodbProvider).Assembly });

        registry.TryGet("db-assert.mongodb", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("collection", registered.SchemaFragment!.Json,
            StringComparison.Ordinal);
    }
}
