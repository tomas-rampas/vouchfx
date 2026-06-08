// S04-B-03 — DbAssertPostgresProvider substitution emit tests (non-docker).
//
// Verifies that DbAssertPostgresProvider.Emit wraps the SQL query text and
// each parameter value in Substitute_Helpers.Resolve(Vars, …).
using Platform.Engine.Abstractions;
using Platform.Sdk;
using Platform.Steps.DbAssert.Postgres;
using Xunit;

namespace Platform.Steps.DbAssert.Postgres.Tests;

/// <summary>
/// S04-B-03: emit-lint tests for <see cref="DbAssertPostgresProvider"/>
/// substitution wrapping.
/// </summary>
public sealed class DbAssertSubstituteTests
{
    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // ── Query text wrapped in Resolve call ────────────────────────────────────

    /// <summary>
    /// <see cref="DbAssertPostgresProvider.Emit"/> must wrap the SQL query text
    /// in <c>Substitute_Helpers.Resolve(Vars, …)</c> so that <c>{placeholder}</c>
    /// tokens in the query (e.g. table identifiers) resolve at runtime.
    /// </summary>
    [Fact]
    public void Emit_QueryText_IsWrappedInResolveCall()
    {
        var provider = new DbAssertPostgresProvider();
        var model = new DbAssertPostgresModel(
            Target: "orders-db",
            Query: "SELECT * FROM {tableName} WHERE id = @p",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["p"] = "42",
            },
            Expect: new PostgresExpectation(RowCount: 1, Row: null));
        var ctx = new StubCompileContext("db-step");

        var fragment = provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // The query must be wrapped in Resolve.
        Assert.Contains("Substitute_Helpers.Resolve(Vars,", block, StringComparison.Ordinal);

        // The {tableName} token must survive as literal text inside the JSON-escaped string.
        Assert.Contains("{tableName}", block, StringComparison.Ordinal);
    }

    // ── Parameter values wrapped in Resolve call ──────────────────────────────

    /// <summary>
    /// Parameter values must also be wrapped in <c>Substitute_Helpers.Resolve(Vars, …)</c>
    /// so that <c>{placeholder}</c> tokens in parameter values resolve at runtime.
    /// The SQL remains parameterised (SQL injection safety is preserved).
    /// </summary>
    [Fact]
    public void Emit_ParameterValues_AreWrappedInResolveCall()
    {
        var provider = new DbAssertPostgresProvider();
        var model = new DbAssertPostgresModel(
            Target: "orders-db",
            Query: "SELECT * FROM orders WHERE id = @p",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["p"] = "{orderId}",
            },
            Expect: new PostgresExpectation(RowCount: 1, Row: null));
        var ctx = new StubCompileContext("db-step-2");

        var fragment = provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // The param value must be wrapped in Resolve.
        Assert.Contains("Substitute_Helpers.Resolve(Vars,", block, StringComparison.Ordinal);

        // The {orderId} token in the param value must survive as literal text.
        Assert.Contains("{orderId}", block, StringComparison.Ordinal);
    }

    // ── Substitute_Helpers in RequiredHelpers ─────────────────────────────────

    /// <summary>
    /// The <c>Substitute_Helpers</c> class must appear in the emitted
    /// <see cref="CsxFragment.RequiredHelpers"/> list.
    /// </summary>
    [Fact]
    public void Emit_SubstituteHelpersInRequiredHelpers()
    {
        var provider = new DbAssertPostgresProvider();
        var model = new DbAssertPostgresModel(
            Target: "db",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: 1, Row: null));
        var ctx = new StubCompileContext("s");

        var fragment = provider.Emit(model, ctx);
        var allHelpers = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("Substitute_Helpers", allHelpers, StringComparison.Ordinal);
    }

    // ── No 'using var' ────────────────────────────────────────────────────────

    /// <summary>
    /// Neither the StatementBlock nor RequiredHelpers from
    /// <see cref="DbAssertPostgresProvider.Emit"/> must contain <c>using var</c>.
    /// </summary>
    [Fact]
    public void Emit_NoUsingVar_AfterSubstituteIntegration()
    {
        var provider = new DbAssertPostgresProvider();
        var model = new DbAssertPostgresModel(
            Target: "db",
            Query: "SELECT {col} FROM t WHERE id = @id",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = "{rowId}",
            },
            Expect: new PostgresExpectation(RowCount: 1, Row: null));
        var ctx = new StubCompileContext("sub-test");

        var fragment = provider.Emit(model, ctx);
        var full = fragment.StatementBlock
                   + "\n"
                   + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", full, StringComparison.Ordinal);
    }
}
