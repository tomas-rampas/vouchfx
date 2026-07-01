// S04-B-03 / H1 — DbAssertMysqlProvider substitution emit tests (non-docker).
//
// Verifies that DbAssertMysqlProvider.Emit:
//   • passes the raw query template as a literal to ExecuteAsync (H1 blast-radius fix);
//   • the ResolveIdentifier call lives in RequiredHelpers (DbAssertMysql_Helpers),
//     inside its own try, so an unsafe identifier yields a STEP-scoped EnvironmentError
//     rather than a scenario-level abort;
//   • wraps each parameter value in Substitute_Helpers.Resolve(Vars, …)
//     (values go via AddWithValue — parameterised SQL, safe for arbitrary values);
//   • wraps each expect-row value in Substitute_Helpers.Resolve(Vars, …)
//     (values are compared in-memory, safe for arbitrary values).
using System.Linq;
using Platform.Engine.Abstractions;
using Platform.Sdk;
using Platform.Steps.DbAssert.Mysql;
using Xunit;

namespace Platform.Steps.DbAssert.Mysql.Tests;

/// <summary>
/// S04-B-03 / H1: emit-lint tests for <see cref="DbAssertMysqlProvider"/>
/// substitution wrapping.
/// </summary>
public sealed class DbAssertMysqlSubstituteTests
{
    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    // ── Query text: raw template in StatementBlock; ResolveIdentifier in helper (H1) ──

    /// <summary>
    /// H1 blast-radius fix: the StatementBlock must pass the raw query template literal
    /// (JSON-escaped) directly to <c>ExecuteAsync</c> — NOT via a
    /// <c>Substitute_Helpers.ResolveIdentifier(Vars, …)</c> call expression.
    /// The <c>ResolveIdentifier</c> call must appear instead in
    /// <see cref="CsxFragment.RequiredHelpers"/> (inside <c>DbAssertMysql_Helpers</c>)
    /// where it runs inside its own <c>try</c>, limiting an unsafe-identifier failure to
    /// a step-scoped <c>Verdict.EnvironmentError</c> rather than a scenario-level abort.
    /// </summary>
    [Fact]
    public void Emit_QueryText_IsRawTemplateInStatementBlock_NotResolveIdentifierExpression()
    {
        var provider = new DbAssertMysqlProvider();
        var model = new DbAssertMysqlModel(
            Target: "orders-db",
            Query: "SELECT * FROM {tableName} WHERE id = @p",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["p"] = "42",
            },
            Expect: new MysqlExpectation(RowCount: 1, Row: null));
        var ctx = new StubCompileContext("db-step");

        var fragment = provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // (a) The StatementBlock must NOT contain a ResolveIdentifier call expression —
        //     that call has moved into the helper.
        Assert.DoesNotContain("ResolveIdentifier(Vars,", block, StringComparison.Ordinal);

        // (b) The raw {tableName} template must survive as literal text inside the
        //     JSON-escaped string argument passed to ExecuteAsync.
        Assert.Contains("{tableName}", block, StringComparison.Ordinal);

        // (c) The ResolveIdentifier call must appear in RequiredHelpers
        //     (DbAssertMysql_Helpers), not the StatementBlock.
        var allHelpers = string.Join("\n", fragment.RequiredHelpers);
        Assert.Contains("Substitute_Helpers.ResolveIdentifier(vars,", allHelpers, StringComparison.Ordinal);
    }

    /// <summary>
    /// H1 regression: the SQL query text must arrive at <c>ExecuteAsync</c> as a
    /// DIRECT, UNWRAPPED JSON-escaped string literal in the StatementBlock — NOT
    /// preceded by a <c>Substitute_Helpers.Resolve(Vars, …)</c> or
    /// <c>Substitute_Helpers.ResolveIdentifier(Vars, …)</c> call expression.
    /// The identifier-safe <c>ResolveIdentifier</c> call must still appear at least
    /// once in <see cref="CsxFragment.RequiredHelpers"/> (<c>DbAssertMysql_Helpers</c>).
    /// </summary>
    [Fact]
    public void Emit_QueryText_StatementBlock_DoesNotContainBareResolveForQuery()
    {
        var provider = new DbAssertMysqlProvider();
        var model = new DbAssertMysqlModel(
            Target: "orders-db",
            Query: "SELECT * FROM {tableName} WHERE id = @p",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));
        var ctx = new StubCompileContext("db-step-bare");

        var fragment = provider.Emit(model, ctx);
        var block = fragment.StatementBlock;

        // Compute the exact JSON-escaped string literal the emitter splices for the query.
        var queryLiteral = System.Text.Json.JsonSerializer.Serialize(model.Query);

        // (a) The raw query literal must appear in the StatementBlock as a direct argument.
        Assert.Contains(queryLiteral, block, StringComparison.Ordinal);

        // (b) POSITIVE assertion: the query literal must NOT be immediately preceded by
        //     Substitute_Helpers.Resolve(Vars,  — that would re-open the SQL-injection sink.
        Assert.DoesNotContain("Resolve(Vars, " + queryLiteral, block, StringComparison.Ordinal);

        // (c) POSITIVE assertion: the query literal must NOT be immediately preceded by
        //     Substitute_Helpers.ResolveIdentifier(Vars,  — the call must live in the helper.
        Assert.DoesNotContain("ResolveIdentifier(Vars, " + queryLiteral, block, StringComparison.Ordinal);

        // (d) No ResolveIdentifier expression in the block at all — it moved to the helper.
        Assert.DoesNotContain("ResolveIdentifier(Vars,", block, StringComparison.Ordinal);

        // (e) The identifier-safe call must appear exactly in the helper source.
        var allHelpers = string.Join("\n", fragment.RequiredHelpers);
        var identifierCallCount = CountOccurrences(allHelpers, "ResolveIdentifier");
        Assert.True(identifierCallCount >= 1,
            "Expected at least one ResolveIdentifier call inside RequiredHelpers.");
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
        var provider = new DbAssertMysqlProvider();
        var model = new DbAssertMysqlModel(
            Target: "orders-db",
            Query: "SELECT * FROM orders WHERE id = @p",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["p"] = "{orderId}",
            },
            Expect: new MysqlExpectation(RowCount: 1, Row: null));
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
    /// in an <c>expect.row</c> value resolves at runtime.
    /// </summary>
    [Fact]
    public void Emit_ExpectRowValues_AreWrappedInResolveCall()
    {
        var provider = new DbAssertMysqlProvider();
        var model = new DbAssertMysqlModel(
            Target: "orders-db",
            Query: "SELECT status FROM orders WHERE id = @p",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["p"] = "42",
            },
            Expect: new MysqlExpectation(
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
        var provider = new DbAssertMysqlProvider();
        var model = new DbAssertMysqlModel(
            Target: "db",
            Query: "SELECT 1",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));
        var ctx = new StubCompileContext("s");

        var fragment = provider.Emit(model, ctx);
        var allHelpers = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("Substitute_Helpers", allHelpers, StringComparison.Ordinal);
    }

    // ── No 'using var' ────────────────────────────────────────────────────────

    /// <summary>
    /// Neither the StatementBlock nor RequiredHelpers from
    /// <see cref="DbAssertMysqlProvider.Emit"/> must contain <c>using var</c>.
    /// </summary>
    [Fact]
    public void Emit_NoUsingVar_AfterSubstituteIntegration()
    {
        var provider = new DbAssertMysqlProvider();
        var model = new DbAssertMysqlModel(
            Target: "db",
            Query: "SELECT {col} FROM t WHERE id = @id",
            Parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["id"] = "{rowId}",
            },
            Expect: new MysqlExpectation(RowCount: 1, Row: null));
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
    /// value containing a space.
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

    // ── Injection → EnvironmentError (non-docker compile-and-run) ────────────

    /// <summary>
    /// H1 blast-radius fix: when a capture variable resolves to an injection value,
    /// the emitted helper must catch the <see cref="System.InvalidOperationException"/>
    /// from <c>Substitute_Helpers.ResolveIdentifier</c> and write
    /// <see cref="Verdict.EnvironmentError"/> for this step, before any DB connection
    /// is opened, and return normally so subsequent steps still run.
    /// </summary>
    [Fact]
    public async Task Emit_InjectionValue_WritesEnvironmentError_BeforeDbConnection()
    {
        const string stepId = "inject-step";
        // Query with a {tbl} placeholder; Vars["tbl"] carries an injection string.
        var model = new DbAssertMysqlModel(
            Target: "orders-db",
            Query: "SELECT * FROM {tbl}",
            Parameters: null,
            Expect: new MysqlExpectation(RowCount: 1, Row: null));
        var ctx = new StubCompileContext(stepId);

        var fragment = new DbAssertMysqlProvider().Emit(model, ctx);

        // Assemble exactly as CsxAssembler.Assemble would.
        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        var additionalRefs = new[]
        {
            typeof(MySqlConnector.MySqlConnection).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = Platform.Engine.Compilation.RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Stage a non-empty connection string so we reach the ResolveIdentifier check.
            [Platform.Engine.Abstractions.VarKeys.Connection("orders-db")] = "Server=dummy;Database=dummy;Uid=u;Pwd=p",
            // Injection value — contains a semicolon, which is outside [A-Za-z0-9_.].
            ["tbl"] = "orders; DROP TABLE x",
        };
        var globals = new Platform.Engine.Abstractions.ScriptGlobalVariables(vars);

        // Must NOT throw — the helper catches the InvalidOperationException internally.
        await Platform.Engine.Compilation.RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = Platform.Engine.Abstractions.VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<Platform.Engine.Abstractions.StepOutcome>(vars[outcomeKey]);
        // The injection must yield EnvironmentError (not a propagated exception).
        Assert.Equal(Platform.Engine.Abstractions.Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
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
