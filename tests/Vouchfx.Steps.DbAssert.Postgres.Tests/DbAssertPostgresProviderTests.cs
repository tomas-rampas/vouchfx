// Tests for DbAssertPostgresProvider — S04-F-01 (bind/validate/schema).
//
// Covers:
//   1. Bind: full YAML step (target/query/parameters/expect.rowCount + expect.row)
//      is deserialised into the correct DbAssertPostgresModel.
//   2. Validate: valid model + matching postgres dependency → IsValid.
//   3. Validate: missing target → invalid.
//   4. Validate: missing query → invalid.
//   5. Validate: empty expect (neither rowCount nor row) → invalid with clear message.
//   6. Validate: target not in DeclaredDependencies → invalid.
//   7. Validate: target declared but type is "mongodb" → invalid (reconciliation).
//   8. Registry: provider discoverable via StepKindRegistry with key "db-assert.postgres".
//   9. Registry: SchemaFragment contains "rowCount".
//
// RunProjectContext.DeclaredDependencies is tested in
// Vouchfx.Engine.Runtime.Tests/ProviderPipelineTests.cs (which has
// InternalsVisibleTo access to the Runtime internals).
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Postgres;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.DbAssert.Postgres.Tests;

// ── Stub IProjectContext implementations ──────────────────────────────────────

/// <summary>
/// Stub <see cref="IProjectContext"/> that exposes a configurable
/// <see cref="IProjectContext.DeclaredDependencies"/> map for use in
/// validator unit tests.
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
/// Stub <see cref="IBindingContext"/> for tests that do not require
/// binding-stage services.
/// </summary>
internal sealed class StubBindingContext : IBindingContext { }

// ── Test class ────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker unit tests for <see cref="DbAssertPostgresProvider"/>
/// (bind, validate, schema, registry discoverability).
/// </summary>
public sealed class DbAssertPostgresProviderTests
{
    private readonly DbAssertPostgresProvider _provider = new();
    private static readonly StubBindingContext s_bindCtx = new();

    // ── 1. Bind: full YAML step ────────────────────────────────────────────────

    /// <summary>
    /// A full YAML mapping with target, query, parameters, expect.rowCount and
    /// expect.row is deserialised into the correct model fields.
    /// </summary>
    [Fact]
    public void Bind_FullYamlMapping_ReturnsCorrectModel()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("orders-db") },
            { "query",  new YamlScalarNode("SELECT id, status FROM orders WHERE id = @orderId") },
            {
                "parameters", new YamlMappingNode
                {
                    { "orderId", new YamlScalarNode("42") },
                }
            },
            {
                "expect", new YamlMappingNode
                {
                    { "rowCount", new YamlScalarNode("1") },
                    {
                        "row", new YamlMappingNode
                        {
                            { "status", new YamlScalarNode("SHIPPED") },
                        }
                    },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal("orders-db", model.Target);
        Assert.Equal("SELECT id, status FROM orders WHERE id = @orderId", model.Query);

        Assert.NotNull(model.Parameters);
        Assert.Equal("42", model.Parameters!["orderId"]);

        Assert.Equal(1, model.Expect.RowCount);

        Assert.NotNull(model.Expect.Row);
        Assert.Equal("SHIPPED", model.Expect.Row!["status"]);
    }

    /// <summary>
    /// Binding from a non-mapping node returns a safe empty model (defensive).
    /// </summary>
    [Fact]
    public void Bind_NonMappingNode_ReturnsEmptyModel()
    {
        var model = _provider.Bind(new YamlScalarNode("bad"), s_bindCtx);

        Assert.Equal(string.Empty, model.Target);
        Assert.Equal(string.Empty, model.Query);
        Assert.Null(model.Parameters);
        Assert.Null(model.Expect.RowCount);
        Assert.Null(model.Expect.Row);
    }

    /// <summary>
    /// A step with only rowCount (no row map) is bound correctly.
    /// </summary>
    [Fact]
    public void Bind_RowCountOnly_NullRow()
    {
        var yaml = new YamlMappingNode
        {
            { "target", new YamlScalarNode("db") },
            { "query",  new YamlScalarNode("SELECT 1") },
            {
                "expect", new YamlMappingNode
                {
                    { "rowCount", new YamlScalarNode("0") },
                }
            },
        };

        var model = _provider.Bind(yaml, s_bindCtx);

        Assert.Equal(0, model.Expect.RowCount);
        Assert.Null(model.Expect.Row);
    }

    // ── 2. Validate: valid model with matching postgres dependency ────────────

    /// <summary>
    /// A fully valid model whose target is declared as type "postgres" passes
    /// validation.
    /// </summary>
    [Fact]
    public void Validate_ValidModel_WithMatchingPostgresDependency_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orders-db"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertPostgresModel(
            Target: "orders-db",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// Dependency type comparison is case-sensitive (pre-GA decision,
    /// feat/case-sensitive-kinds): "Postgres" does not match the canonical "postgres" — treated
    /// identically to a genuinely mismatched type (reconciliation failure).
    /// </summary>
    [Fact]
    public void Validate_DependencyTypeWrongCase_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mydb"] = "Postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertPostgresModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("mydb", StringComparison.Ordinal) &&
            e.Contains("postgres dependency", StringComparison.Ordinal));
    }

    // ── 3. Validate: missing target ───────────────────────────────────────────

    /// <summary>
    /// An empty target produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyTarget_IsInvalid()
    {
        var ctx = new StubProjectContext();

        var model = new DbAssertPostgresModel(
            Target: string.Empty,
            Query: "SELECT 1",
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'target'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 4. Validate: missing query ────────────────────────────────────────────

    /// <summary>
    /// An empty query produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyQuery_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mydb"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertPostgresModel(
            Target: "mydb",
            Query: string.Empty,
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'query'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 5. Validate: empty expect → clear error message ───────────────────────

    /// <summary>
    /// When neither rowCount nor row is specified, the validator returns a
    /// clear error naming the exact constraint.
    /// </summary>
    [Fact]
    public void Validate_EmptyExpect_ReturnsExpectMustSpecifyRowCountOrRow()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mydb"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertPostgresModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: null, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("db-assert.postgres", StringComparison.Ordinal) &&
            e.Contains("'expect'", StringComparison.Ordinal) &&
            e.Contains("rowCount", StringComparison.Ordinal) &&
            e.Contains("row", StringComparison.Ordinal));
    }

    /// <summary>
    /// An empty row dictionary (Count == 0) with null rowCount also triggers
    /// the expect constraint.
    /// </summary>
    [Fact]
    public void Validate_EmptyRowDictionary_ReturnsExpectError()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mydb"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertPostgresModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new PostgresExpectation(
                RowCount: null,
                Row: new Dictionary<string, string>(StringComparer.Ordinal)));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'expect'", StringComparison.Ordinal));
    }

    // ── 6. Validate: target not in DeclaredDependencies ──────────────────────

    /// <summary>
    /// When the target is not present in the declared-dependencies map at all,
    /// the validator returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetNotInDeclaredDependencies_IsInvalid()
    {
        // Empty map — no dependencies declared.
        var ctx = new StubProjectContext();

        var model = new DbAssertPostgresModel(
            Target: "orders-db",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("orders-db", StringComparison.Ordinal) &&
            e.Contains("postgres dependency", StringComparison.Ordinal));
    }

    // ── 7. Validate: target declared with wrong type ──────────────────────────

    /// <summary>
    /// When the target is declared but its type is not "postgres", the validator
    /// returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetDeclaredAsWrongType_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orders-db"] = "mongodb",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertPostgresModel(
            Target: "orders-db",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("orders-db", StringComparison.Ordinal) &&
            e.Contains("postgres dependency", StringComparison.Ordinal));
    }

    // ── 8. Registry: provider discoverable ───────────────────────────────────

    /// <summary>
    /// Scanning the provider assembly via <see cref="StepKindRegistry.BuildAndFreeze"/>
    /// discovers <see cref="DbAssertPostgresProvider"/> at key
    /// <c>"db-assert.postgres"</c>.
    /// </summary>
    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(DbAssertPostgresProvider).Assembly });

        var found = registry.TryGet("db-assert.postgres", out var registered);

        Assert.True(found, "Expected 'db-assert.postgres' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("db-assert", registered!.Kind.Family);
        Assert.Equal("postgres", registered.Kind.Provider);
        Assert.IsType<DbAssertPostgresProvider>(registered.Instance);
    }

    // ── 9. Registry: SchemaFragment contains "rowCount" ──────────────────────

    /// <summary>
    /// The discovered provider's <see cref="JsonSchemaFragment"/> must be
    /// non-null and its JSON must reference the <c>rowCount</c> field.
    /// </summary>
    [Fact]
    public void Provider_SchemaFragment_ContainsRowCount()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(DbAssertPostgresProvider).Assembly });

        registry.TryGet("db-assert.postgres", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("rowCount", registered.SchemaFragment!.Json,
            StringComparison.Ordinal);
    }

}
