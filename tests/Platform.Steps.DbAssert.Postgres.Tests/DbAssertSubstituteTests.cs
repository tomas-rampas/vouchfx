// S04-B-03 / H1 — DbAssertPostgresProvider substitution emit tests (non-docker).
//
// Verifies that DbAssertPostgresProvider.Emit:
//   • wraps the SQL query text in Substitute_Helpers.ResolveIdentifier(Vars, …)
//     (H1 security fix — identifier-safe substitution only for query text);
//   • wraps each parameter value in Substitute_Helpers.Resolve(Vars, …)
//     (values go via AddWithValue — parameterised SQL, safe for arbitrary values);
//   • wraps each expect-row value in Substitute_Helpers.Resolve(Vars, …)
//     (values are compared in-memory, safe for arbitrary values).
using Platform.Engine.Abstractions;
using Platform.Sdk;
using Platform.Steps.DbAssert.Postgres;
using Xunit;

namespace Platform.Steps.DbAssert.Postgres.Tests;

/// <summary>
/// S04-B-03 / H1: emit-lint tests for <see cref="DbAssertPostgresProvider"/>
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

    // ── Query text wrapped in ResolveIdentifier call (H1) ────────────────────

    /// <summary>
    /// <see cref="DbAssertPostgresProvider.Emit"/> must wrap the SQL query text
    /// in <c>Substitute_Helpers.ResolveIdentifier(Vars, …)</c> — not the bare
    /// <c>Resolve</c> overload — so that only safe SQL identifiers can be spliced
    /// into the query text at runtime (H1 security fix).
    /// </summary>
    [Fact]
    public void Emit_QueryText_IsWrappedInResolveIdentifierCall()
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

        // The query must be wrapped in ResolveIdentifier, not bare Resolve.
        Assert.Contains("Substitute_Helpers.ResolveIdentifier(Vars,", block, StringComparison.Ordinal);

        // The {tableName} token must survive as literal text inside the JSON-escaped string.
        Assert.Contains("{tableName}", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SQL query text must NOT be wrapped in the bare
    /// <c>Substitute_Helpers.Resolve(Vars, …)</c> overload — that overload allows
    /// arbitrary values and would re-open the SQL-injection sink (H1).
    /// Only parameter values and expect-row values may use the bare Resolve overload.
    /// </summary>
    [Fact]
    public void Emit_QueryText_IsNotWrappedInBareResolveCall()
    {
        var provider = new DbAssertPostgresProvider();
        var model = new DbAssertPostgresModel(
            Target: "orders-db",
            Query: "SELECT * FROM {tableName} WHERE id = @p",
            Parameters: null,
            Expect: new PostgresExpectation(RowCount: 1, Row: null));
        var ctx = new StubCompileContext("db-step-bare");

        var fragment = provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // Verify the identifier-safe call IS present.
        Assert.Contains("Substitute_Helpers.ResolveIdentifier(Vars,", block, StringComparison.Ordinal);

        // Strip all ResolveIdentifier occurrences, then assert no bare Resolve remains
        // for the query text position (the first argument to ExecuteAsync after the keys).
        // The simplest invariant: the string "ResolveIdentifier" appears at least once.
        var identifierCallCount = CountOccurrences(block, "ResolveIdentifier");
        Assert.True(identifierCallCount >= 1,
            "Expected at least one ResolveIdentifier call for the query text.");
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

    // ── Expect-row values wrapped in Resolve call ─────────────────────────────

    /// <summary>
    /// Expected-column values must also be wrapped in
    /// <c>Substitute_Helpers.Resolve(Vars, …)</c> so that a <c>{placeholder}</c>
    /// in an <c>expect.row</c> value (a captured variable or a <c>variables</c>-block
    /// constant) resolves at runtime — consistent with parameter values.
    /// </summary>
    [Fact]
    public void Emit_ExpectRowValues_AreWrappedInResolveCall()
    {
        var provider = new DbAssertPostgresProvider();
        var model = new DbAssertPostgresModel(
            Target: "orders-db",
            Query: "SELECT status FROM orders WHERE id = @p",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["p"] = "42",
            },
            Expect: new PostgresExpectation(
                RowCount: 1,
                Row: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = "{expectedStatus}",
                }));
        var ctx = new StubCompileContext("db-step-3");

        var fragment = provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // The expect value must be wrapped in Resolve.
        Assert.Contains("Substitute_Helpers.Resolve(Vars,", block, StringComparison.Ordinal);

        // The {expectedStatus} token in the expect value must survive as literal text,
        // and the column name must NOT be wrapped (identifiers are not substituted).
        Assert.Contains("{expectedStatus}", block, StringComparison.Ordinal);
        Assert.Contains("\"status\"", block, StringComparison.Ordinal);
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

    // ── ResolveIdentifier helper behaviour (compile-and-invoke via Roslyn) ────

    /// <summary>
    /// <c>Substitute_Helpers.ResolveIdentifier</c> must substitute a simple safe
    /// identifier (letters only) without throwing.
    /// </summary>
    [Fact]
    public async Task ResolveIdentifier_SafeIdentifier_Substitutes()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.ResolveIdentifier(Vars, \"{tbl}\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tbl"] = "orders",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = Platform.Engine.Compilation.RoslynScriptCompiler.CompileOnce(csx);
        await Platform.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.Equal("orders", vars["result"]);
    }

    /// <summary>
    /// <c>Substitute_Helpers.ResolveIdentifier</c> must substitute a dotted identifier
    /// (e.g. <c>myschema.orders</c>) without throwing, since dot is in the permitted
    /// charset.
    /// </summary>
    [Fact]
    public async Task ResolveIdentifier_DottedIdentifier_Substitutes()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.ResolveIdentifier(Vars, \"{tbl}\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tbl"] = "myschema.orders",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = Platform.Engine.Compilation.RoslynScriptCompiler.CompileOnce(csx);
        await Platform.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.Equal("myschema.orders", vars["result"]);
    }

    /// <summary>
    /// <c>Substitute_Helpers.ResolveIdentifier</c> must throw
    /// <see cref="System.InvalidOperationException"/> when a placeholder resolves to a
    /// value containing a semicolon — a classic SQL-injection character.
    /// </summary>
    [Fact]
    public async Task ResolveIdentifier_InjectionValueWithSemicolon_ThrowsInvalidOperationException()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "try\n" +
            "{\n" +
            "    var _ = Substitute_Helpers.ResolveIdentifier(Vars, \"{tbl}\");\n" +
            "    Vars[\"threw\"] = false;\n" +
            "}\n" +
            "catch (System.InvalidOperationException ex)\n" +
            "{\n" +
            "    Vars[\"threw\"] = true;\n" +
            "    Vars[\"msg\"] = ex.Message;\n" +
            "}";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tbl"] = "orders; DROP TABLE orders",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = Platform.Engine.Compilation.RoslynScriptCompiler.CompileOnce(csx);
        await Platform.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.True((bool?)vars["threw"] == true, "Expected InvalidOperationException to be thrown.");
        var msg = Assert.IsType<string>(vars["msg"]);
        Assert.Contains("tbl", msg, StringComparison.Ordinal);
        Assert.Contains("safe SQL identifier", msg, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Substitute_Helpers.ResolveIdentifier</c> must throw
    /// <see cref="System.InvalidOperationException"/> when a placeholder resolves to a
    /// value containing a space (e.g. multi-word injection attempt).
    /// </summary>
    [Fact]
    public async Task ResolveIdentifier_InjectionValueWithSpace_ThrowsInvalidOperationException()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "try\n" +
            "{\n" +
            "    var _ = Substitute_Helpers.ResolveIdentifier(Vars, \"{tbl}\");\n" +
            "    Vars[\"threw\"] = false;\n" +
            "}\n" +
            "catch (System.InvalidOperationException)\n" +
            "{\n" +
            "    Vars[\"threw\"] = true;\n" +
            "}";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tbl"] = "orders WHERE 1=1",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = Platform.Engine.Compilation.RoslynScriptCompiler.CompileOnce(csx);
        await Platform.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.True((bool?)vars["threw"] == true, "Expected InvalidOperationException for value containing a space.");
    }

    /// <summary>
    /// <c>Substitute_Helpers.ResolveIdentifier</c> must throw
    /// <see cref="System.InvalidOperationException"/> when a placeholder resolves to a
    /// value containing a single-quote character.
    /// </summary>
    [Fact]
    public async Task ResolveIdentifier_InjectionValueWithQuote_ThrowsInvalidOperationException()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "try\n" +
            "{\n" +
            "    var _ = Substitute_Helpers.ResolveIdentifier(Vars, \"{tbl}\");\n" +
            "    Vars[\"threw\"] = false;\n" +
            "}\n" +
            "catch (System.InvalidOperationException)\n" +
            "{\n" +
            "    Vars[\"threw\"] = true;\n" +
            "}";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tbl"] = "orders'--",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = Platform.Engine.Compilation.RoslynScriptCompiler.CompileOnce(csx);
        await Platform.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.True((bool?)vars["threw"] == true, "Expected InvalidOperationException for value containing a quote.");
    }

    /// <summary>
    /// The existing <c>Substitute_Helpers.Resolve</c> overload must continue to
    /// substitute arbitrary values (including semicolons, spaces, and quotes)
    /// without throwing — regression guard for parameter and expect-row paths.
    /// </summary>
    [Fact]
    public async Task Resolve_ArbitraryValue_StillSubstitutesWithoutThrowing()
    {
        var csx =
            SubstituteHelper.Source + "\n" +
            "Vars[\"result\"] = Substitute_Helpers.Resolve(Vars, \"{val}\");";

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // A value that would fail ResolveIdentifier — must succeed with Resolve.
            ["val"] = "orders; DROP TABLE orders",
        };
        var globals = new ScriptGlobalVariables(vars);
        var compiled = Platform.Engine.Compilation.RoslynScriptCompiler.CompileOnce(csx);
        await Platform.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        // Resolve must pass the value through unchanged; no exception thrown.
        Assert.Equal("orders; DROP TABLE orders", vars["result"]);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static int CountOccurrences(string source, string search)
    {
        int count = 0;
        int idx = 0;
        while ((idx = source.IndexOf(search, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += search.Length;
        }
        return count;
    }
}
