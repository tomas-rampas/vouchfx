// Tests for DbAssertMysqlProvider — bind/validate/schema/registry.
//
// Mirrors Vouchfx.Steps.DbAssert.SqlServer.Tests/DbAssertSqlServerProviderTests.cs.
//
// Covers:
//   1. Bind: full YAML step (target/query/parameters/expect.rowCount + expect.row)
//      is deserialised into the correct DbAssertMysqlModel.
//   2. Bind: non-mapping node → safe empty model (defensive).
//   3. Bind: rowCount-only expect → null Row.
//   4. Validate: valid model + matching mysql dependency → IsValid.
//   5. Validate: dependency type comparison is case-insensitive.
//   6. Validate: empty target → invalid.
//   7. Validate: empty query → invalid.
//   8. Validate: empty expect (neither rowCount nor row) → invalid with clear message.
//   9. Validate: empty row dictionary + null rowCount → invalid.
//  10. Validate: target not in DeclaredDependencies → invalid.
//  11. Validate: target declared but type is wrong (e.g. "postgres") → invalid.
//  12. Emit: StatementBlock begins and ends with a brace.
//  13. Emit: no 'using var' in the emitted fragment.
//  14. Emit: helper class is named 'DbAssertMysql_Helpers' (§13.3.1 prefix rule).
//  15. Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//  16. Emit: RequiredUsings contains the MySqlConnector namespace.
//  17. Resources: yields a mysql ResourceRequirement whose Name equals model.Target.
//  18. CompileReferenceAssemblies: contains the MySqlConnector assembly.
//  19. Registry: provider discoverable via StepKindRegistry with key "db-assert.mysql".
//  20. Registry: SchemaFragment contains "rowCount".
//
// All tests are non-docker.  No topology is started.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using Vouchfx.Steps.DbAssert.Mysql;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Steps.DbAssert.Mysql.Tests;

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
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

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
/// Non-docker unit tests for <see cref="DbAssertMysqlProvider"/>
/// (bind, validate, emit, schema, registry discoverability).
/// </summary>
public sealed class DbAssertMysqlProviderTests
{
    private readonly DbAssertMysqlProvider _provider = new();
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

    // ── 2. Bind: non-mapping node ──────────────────────────────────────────────

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

    // ── 3. Bind: rowCount-only ────────────────────────────────────────────────

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

    // ── 4. Validate: valid model ──────────────────────────────────────────────

    /// <summary>
    /// A fully valid model whose target is declared as type "mysql" passes
    /// validation.
    /// </summary>
    [Fact]
    public void Validate_ValidModel_WithMatchingMysqlDependency_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orders-db"] = "mysql",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMysqlModel(
            Target: "orders-db",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    // ── 5. Validate: dependency type case-insensitive ─────────────────────────

    /// <summary>
    /// Dependency type comparison is case-insensitive ("MySql" matches "mysql").
    /// </summary>
    [Fact]
    public void Validate_DependencyTypeCaseInsensitive_IsValid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mydb"] = "MySql",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMysqlModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

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

        var model = new DbAssertMysqlModel(
            Target: string.Empty,
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'target'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 7. Validate: empty query ──────────────────────────────────────────────

    /// <summary>
    /// An empty query produces a validation error.
    /// </summary>
    [Fact]
    public void Validate_EmptyQuery_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mydb"] = "mysql",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMysqlModel(
            Target: "mydb",
            Query: string.Empty,
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'query'", StringComparison.Ordinal) &&
            e.Contains("empty", StringComparison.Ordinal));
    }

    // ── 8. Validate: empty expect ─────────────────────────────────────────────

    /// <summary>
    /// When neither rowCount nor row is specified, the validator returns a
    /// clear error naming the exact constraint.
    /// </summary>
    [Fact]
    public void Validate_EmptyExpect_ReturnsExpectMustSpecifyRowCountOrRow()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mydb"] = "mysql",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMysqlModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: null, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("db-assert.mysql", StringComparison.Ordinal) &&
            e.Contains("'expect'", StringComparison.Ordinal) &&
            e.Contains("rowCount", StringComparison.Ordinal) &&
            e.Contains("row", StringComparison.Ordinal));
    }

    // ── 9. Validate: empty row dictionary ─────────────────────────────────────

    /// <summary>
    /// An empty row dictionary (Count == 0) with null rowCount also triggers
    /// the expect constraint.
    /// </summary>
    [Fact]
    public void Validate_EmptyRowDictionary_ReturnsExpectError()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mydb"] = "mysql",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMysqlModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(
                RowCount: null,
                Row: new Dictionary<string, string>(StringComparer.Ordinal)));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'expect'", StringComparison.Ordinal));
    }

    // ── 10. Validate: target not in DeclaredDependencies ──────────────────────

    /// <summary>
    /// When the target is not present in the declared-dependencies map at all,
    /// the validator returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetNotInDeclaredDependencies_IsInvalid()
    {
        var ctx = new StubProjectContext();

        var model = new DbAssertMysqlModel(
            Target: "orders-db",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("orders-db", StringComparison.Ordinal) &&
            e.Contains("mysql dependency", StringComparison.Ordinal));
    }

    // ── 11. Validate: target declared as wrong type ───────────────────────────

    /// <summary>
    /// When the target is declared but its type is not "mysql", the validator
    /// returns a dependency-reconciliation error.
    /// </summary>
    [Fact]
    public void Validate_TargetDeclaredAsWrongType_IsInvalid()
    {
        var deps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["orders-db"] = "postgres",
        };
        var ctx = new StubProjectContext(deps);

        var model = new DbAssertMysqlModel(
            Target: "orders-db",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var result = _provider.Validate(model, ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("orders-db", StringComparison.Ordinal) &&
            e.Contains("mysql dependency", StringComparison.Ordinal));
    }

    // ── 12. Emit: StatementBlock braces ───────────────────────────────────────

    /// <summary>
    /// The emitted StatementBlock must begin with '{' and end with '}'.
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_BeginsAndEndsWithBrace()
    {
        var model = new DbAssertMysqlModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var ctx = new StubCompileContext("assert-step");
        var fragment = _provider.Emit(model, ctx);

        var trimmed = fragment.StatementBlock.Trim();
        Assert.True(trimmed.StartsWith('{'),
            "StatementBlock must begin with '{'.");
        Assert.True(trimmed.EndsWith('}'),
            "StatementBlock must end with '}'.");
    }

    // ── 13. Emit: no 'using var' ──────────────────────────────────────────────

    /// <summary>
    /// The emitted fragment (StatementBlock + helpers) must not contain 'using var'
    /// (§13.3.1: prohibited in Roslyn script bodies).
    /// </summary>
    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var model = new DbAssertMysqlModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var ctx = new StubCompileContext("assert-step");
        var fragment = _provider.Emit(model, ctx);

        var allText = fragment.StatementBlock
            + string.Concat(fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", allText, StringComparison.Ordinal);
    }

    // ── 14. Emit: helper class prefix ────────────────────────────────────────

    /// <summary>
    /// The helper class contributed to RequiredHelpers must be named
    /// 'DbAssertMysql_Helpers' (§13.3.1 provider-id prefix rule).
    /// </summary>
    [Fact]
    public void Emit_HelperClass_IsNamedDbAssertMysql_Helpers()
    {
        var model = new DbAssertMysqlModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var ctx = new StubCompileContext("assert-step");
        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("DbAssertMysql_Helpers", StringComparison.Ordinal));
    }

    // ── 15. Emit: step-id sanitisation ───────────────────────────────────────

    /// <summary>
    /// A step id containing hyphens is sanitised to underscores in the emitted
    /// StatementBlock (§13.3.1 SanitiseId rule — emitted variable names may not
    /// contain hyphens).
    /// </summary>
    [Fact]
    public void Emit_HyphenatedStepId_SanitisedToUnderscores()
    {
        var model = new DbAssertMysqlModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var ctx = new StubCompileContext("assert-my-step");
        var fragment = _provider.Emit(model, ctx);

        // The sanitised id "assert_my_step" must appear; the raw "assert-my-step" must not.
        Assert.Contains("assert_my_step", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("assert-my-step", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 16. Emit: RequiredUsings ──────────────────────────────────────────────

    /// <summary>
    /// RequiredUsings must include the MySqlConnector namespace so the
    /// emitted helper can resolve MySqlConnection without a fully-qualified name in
    /// the outer using context.
    /// </summary>
    [Fact]
    public void Emit_RequiredUsings_ContainsMysqlConnectorNamespace()
    {
        var model = new DbAssertMysqlModel(
            Target: "mydb",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var ctx = new StubCompileContext("assert-step");
        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("MySqlConnector", fragment.RequiredUsings,
            StringComparer.Ordinal);
    }

    // ── 17. Resources ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resources() yields a single mysql ResourceRequirement whose Name equals
    /// model.Target.
    /// </summary>
    [Fact]
    public void Resources_YieldsMysqlRequirementWithCorrectName()
    {
        var model = new DbAssertMysqlModel(
            Target: "orders-db",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));

        var reqs = _provider.Resources(model).ToList();

        Assert.Single(reqs);
        Assert.Equal("mysql", reqs[0].Family);
        Assert.Equal("orders-db", reqs[0].Name);
    }

    // ── 18. CompileReferenceAssemblies ────────────────────────────────────────

    /// <summary>
    /// CompileReferenceAssemblies must contain the MySqlConnector assembly
    /// so the Roslyn compiler can resolve MySqlConnection in the emitted helper.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsMysqlConnectorAssembly()
    {
        var assemblies = _provider.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Contains("MySqlConnector", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 19. Registry: provider discoverable ──────────────────────────────────

    /// <summary>
    /// Scanning the provider assembly via <see cref="StepKindRegistry.BuildAndFreeze"/>
    /// discovers <see cref="DbAssertMysqlProvider"/> at key
    /// <c>"db-assert.mysql"</c>.
    /// </summary>
    [Fact]
    public void Provider_IsDiscoverableViaStepKindRegistry()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(DbAssertMysqlProvider).Assembly });

        var found = registry.TryGet("db-assert.mysql", out var registered);

        Assert.True(found, "Expected 'db-assert.mysql' to be registered.");
        Assert.NotNull(registered);
        Assert.Equal("db-assert", registered!.Kind.Family);
        Assert.Equal("mysql", registered.Kind.Provider);
        Assert.IsType<DbAssertMysqlProvider>(registered.Instance);
    }

    // ── 20. Registry: SchemaFragment contains "rowCount" ─────────────────────

    /// <summary>
    /// The discovered provider's <see cref="JsonSchemaFragment"/> must be
    /// non-null and its JSON must reference the <c>rowCount</c> field.
    /// </summary>
    [Fact]
    public void Provider_SchemaFragment_ContainsRowCount()
    {
        var registry = StepKindRegistry.BuildAndFreeze(
            new[] { typeof(DbAssertMysqlProvider).Assembly });

        registry.TryGet("db-assert.mysql", out var registered);

        Assert.NotNull(registered!.SchemaFragment);
        Assert.Contains("rowCount", registered.SchemaFragment!.Json,
            StringComparison.Ordinal);
    }
}
