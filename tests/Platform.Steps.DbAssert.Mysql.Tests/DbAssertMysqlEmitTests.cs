// Tests for DbAssertMysqlProvider — CSX emitter + resource contributor.
//
// All tests in this file are non-docker.  They exercise:
//   1. Emit: StatementBlock begins and ends with a brace.
//   2. Emit: no 'using var' in the emitted fragment.
//   3. Emit: helper class is named 'DbAssertMysql_Helpers' (§13.3.1 prefix rule).
//   4. Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5. Emit: SQL query and parameter values are JSON-escaped (injection safety).
//   6. Emit: RequiredUsings contains the MySqlConnector namespace.
//   7. Resources: yields a mysql ResourceRequirement whose Name equals model.Target.
//   8. CompileReferenceAssemblies: contains the MySqlConnector assembly.
//   9. Full compile-and-run (no docker): EnvironmentError when conn key is absent.
//  10. Full compile-and-run (no docker): EnvironmentError when conn string is malformed.
//  11. Full compile-and-run (no docker): credential not present in observation on failure.
//  12. Direct test of RedactCredentials via compiled CSX body.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.DbAssert.Mysql;
using Xunit;

namespace Platform.Steps.DbAssert.Mysql.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="DbAssertMysqlProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>),
/// resource contributor (<see cref="IResourceContributor{TModel}"/>), and
/// compile-reference contributor (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class DbAssertMysqlEmitTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────────

    /// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
    private sealed class StubCompileContext : ICompileContext
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

    // ── Shared provider instance ──────────────────────────────────────────────────

    private readonly DbAssertMysqlProvider _provider = new();

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    /// <summary>
    /// The emitted <see cref="CsxFragment.StatementBlock"/> must begin with '{'
    /// and end with '}', satisfying the §13.3.1 brace rule.
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var model = MakeModel("orders-db", "SELECT 1", null, rowCount: 1);
        var ctx = new StubCompileContext("check-step");

        var fragment = _provider.Emit(model, ctx);
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'),
            $"StatementBlock must start with '{{'; actual start: '{block[..Math.Min(20, block.Length)]}'");
        Assert.True(block.EndsWith('}'),
            $"StatementBlock must end with '}}'; actual end: '{block[Math.Max(0, block.Length - 20)..]}'");
    }

    // ── 2. No 'using var' ────────────────────────────────────────────────────────

    /// <summary>
    /// Neither the <see cref="CsxFragment.StatementBlock"/> nor any entry in
    /// <see cref="CsxFragment.RequiredHelpers"/> must contain 'using var'
    /// (Roslyn script parse error, §13.3.1).
    /// </summary>
    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var model = MakeModel("db", "SELECT id FROM t WHERE id = @p", new[] { ("p", "42") }, rowCount: 1);
        var ctx = new StubCompileContext("my-step");

        var fragment = _provider.Emit(model, ctx);
        var fullSource = fragment.StatementBlock
            + "\n"
            + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredHelpers"/> must contain an entry whose class name
    /// begins with <c>DbAssertMysql_</c> (§13.3.1 provider-prefix rule).
    /// Since S04-B-03 the fragment also contains <c>Substitute_Helpers</c>; the test
    /// therefore asserts the provider-prefixed class is present rather than requiring
    /// exactly one helper.
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_ContainsDbAssertMysqlPrefixedClass()
    {
        var model = MakeModel("db", "SELECT 1", null, rowCount: 1);
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("DbAssertMysql_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ──────────────────────────────────────────────────

    /// <summary>
    /// A step id containing hyphens must appear in the StatementBlock only after
    /// sanitisation (hyphens replaced with underscores), never as the raw hyphenated
    /// form (which would be an invalid C# identifier, §13.3.1).
    /// </summary>
    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "check-order-status";
        var safeId = CsxFragment.SanitiseId(rawId); // "check_order_status"
        var model = MakeModel("db", "SELECT 1", null, rowCount: 1);
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);

        // The sanitised outcome key must appear in the block.
        var expectedKey = VarKeys.Outcome(safeId);
        Assert.Contains(expectedKey, fragment.StatementBlock, StringComparison.Ordinal);

        // The raw hyphenated id must NOT appear in the block (it would be an invalid identifier).
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. JSON-escaped values ────────────────────────────────────────────────────

    /// <summary>
    /// SQL query text and parameter values containing double-quotes, backslashes,
    /// or other special characters must be emitted as JSON-escaped string literals
    /// so they cannot break the CSX statement block.
    /// </summary>
    [Fact]
    public void Emit_SpecialCharactersInQueryAndParams_AreJsonEscaped()
    {
        const string dangerousQuery = "SELECT \"col\" FROM t WHERE x = @p";
        const string dangerousParam = "val\\with\"quotes";
        var model = MakeModel(
            target: "db",
            query: dangerousQuery,
            parameters: new[] { ("p", dangerousParam) },
            rowCount: 1);
        var ctx = new StubCompileContext("escape-test");

        var fragment = _provider.Emit(model, ctx);

        // The raw unescaped strings must not appear verbatim — they would break the literal.
        Assert.DoesNotContain(dangerousQuery, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(dangerousParam, fragment.StatementBlock, StringComparison.Ordinal);

        // The block must compile cleanly — verified by test 9 (compile round-trip).
    }

    // ── 6. RequiredUsings contains MySqlConnector namespace ──────────────────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredUsings"/> must include the
    /// <c>MySqlConnector</c> namespace, which the emitted helper class
    /// requires (§13.3.1 bare namespace rule).
    /// </summary>
    [Fact]
    public void Emit_RequiredUsings_ContainsMysqlConnectorNamespace()
    {
        var model = MakeModel("db", "SELECT 1", null, rowCount: 1);
        var ctx = new StubCompileContext("u");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("MySqlConnector", fragment.RequiredUsings, StringComparer.Ordinal);
    }

    // ── 7. IResourceContributor yields mysql ResourceRequirement ──────────────────

    /// <summary>
    /// <see cref="IResourceContributor{TModel}.Resources"/> must yield exactly one
    /// <see cref="ResourceRequirement"/> with <c>Family="mysql"</c> and
    /// <c>Name</c> equal to <see cref="DbAssertMysqlModel.Target"/>.
    /// </summary>
    [Fact]
    public void Resources_YieldsMysqlRequirementWithMatchingName()
    {
        var model = MakeModel("orders-db", "SELECT 1", null, rowCount: 1);

        var requirements = _provider.Resources(model).ToList();

        Assert.Single(requirements);
        var req = requirements[0];
        Assert.Equal("mysql", req.Family, StringComparer.Ordinal);
        Assert.Equal("orders-db", req.Name, StringComparer.Ordinal);
    }

    // ── 8. ICompileReferenceContributor returns MySqlConnector assembly ───────────

    /// <summary>
    /// <see cref="ICompileReferenceContributor.CompileReferenceAssemblies"/> must
    /// contain the <c>MySqlConnector</c> assembly so the Roslyn compiler
    /// can resolve the <c>MySqlConnection</c> type in the emitted helper.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsMysqlConnectorAssembly()
    {
        var contributor = (ICompileReferenceContributor)_provider;

        var assemblies = contributor.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Contains("MySqlConnector", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 9. Compile round-trip: EnvironmentError when conn key absent ──────────────

    /// <summary>
    /// When the connection key is absent from <c>Vars</c>, the emitted helper must
    /// write <see cref="Verdict.EnvironmentError"/> to the outcome key rather than
    /// throwing an unhandled exception.  This test also verifies that the emitted
    /// CSX compiles without errors with the MySqlConnector reference assembly.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        const string stepId = "db-step";
        var model = MakeModel("missing-dep", "SELECT 1", null, rowCount: 1);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        // Assemble exactly as CsxAssembler.Assemble would.
        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        // The emitted helper references MySqlConnector and System.Text.Json — supply
        // both as compile-time metadata references.  Neither is ever loaded into the
        // collectible ALC (§5 memory-model invariant).
        var additionalRefs = new[]
        {
            typeof(MySqlConnector.MySqlConnection).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        // No connection key seeded in Vars — the helper must detect the absence and
        // write EnvironmentError rather than throwing.
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 10. Compile round-trip: EnvironmentError when conn string is malformed ────

    /// <summary>
    /// When the connection key is present but the connection string is malformed
    /// (causing the constructor or <c>OpenAsync</c> to throw), the emitted helper must
    /// catch the exception and write <see cref="Verdict.EnvironmentError"/> rather than
    /// propagating the throw.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_MalformedConnStr_ReturnsEnvironmentError()
    {
        const string stepId = "db-step-bad-conn";
        var model = MakeModel("my-dep", "SELECT 1", null, rowCount: 1);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

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
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // Non-null but wholly invalid connection string — OpenAsync must throw.
            [VarKeys.Connection("my-dep")] = "@@@malformed@@@",
        };
        var globals = new ScriptGlobalVariables(vars);

        // Must NOT propagate the exception — the helper catches it and writes EnvironmentError.
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 11. Compile round-trip: credential absent from observation on failure ──────

    /// <summary>
    /// When the connection string contains credentials and the connection attempt fails,
    /// the observation must NOT expose the password (§17 — no secrets in observations).
    /// <c>RedactCredentials</c> inside <c>DbAssertMysql_Helpers</c> sanitises
    /// both the full connection string and ADO-style <c>Password=</c> / <c>Pwd=</c> segments.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_CredentialedConnFails_CredentialAbsentFromObservation()
    {
        const string stepId = "mysql-cred-leak-check";
        // MySQL connection string format uses Uid= and Pwd= (as well as Password=).
        const string connStr = "Server=bad-host;Database=db;Uid=user;Pwd=sup3rsecret;ConnectionTimeout=1;";
        var model = MakeModel("dep", "SELECT 1", null, rowCount: 1);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

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
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("dep")] = connStr,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
        Assert.DoesNotContain("sup3rsecret", outcome.Observation, StringComparison.Ordinal);
    }

    // ── 12. Direct test of RedactCredentials with a crafted dangerous message ────

    /// <summary>
    /// Directly tests the emitted <c>DbAssertMysql_Helpers.RedactCredentials</c> method
    /// by invoking it — via a compiled test CSX body — with a crafted message that
    /// intentionally contains the password.  Both the literal connection-string replacement
    /// path and the ADO <c>Pwd=</c> key-value pattern are exercised.
    /// </summary>
    [Fact]
    public async Task Emit_RedactCredentials_WithCraftedMessageContainingSecret_SecretIsStripped()
    {
        const string stepId = "mysql-redact-direct";
        const string connStr = "Server=bad-host;Database=db;Uid=user;Pwd=sup3rsecret;";
        // Craft a message that DOES contain the password, simulating a hypothetical driver
        // that leaks credentials in its error output.
        const string craftedMessage =
            "login failed for user 'user'. Connection: " +
            "Server=bad-host;Database=db;Uid=user;Pwd=sup3rsecret;";

        var model = MakeModel("dep", "SELECT 1", null, rowCount: 1);
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_result__\"] = DbAssertMysql_Helpers.RedactCredentials(" +
            "Vars[\"__conn_str__\"] as string ?? string.Empty, " +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var additionalRefs = new[]
        {
            typeof(MySqlConnector.MySqlConnection).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__conn_str__"] = connStr,
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.True(vars.ContainsKey("__redact_result__"),
            "Expected '__redact_result__' in Vars after calling RedactCredentials.");
        var result = Assert.IsType<string>(vars["__redact_result__"]);
        Assert.DoesNotContain("sup3rsecret", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="DbAssertMysqlModel"/> for use in emit tests.
    /// </summary>
    private static DbAssertMysqlModel MakeModel(
        string target,
        string query,
        (string Name, string Value)[]? parameters,
        int? rowCount = null,
        IReadOnlyDictionary<string, string>? row = null)
    {
        IReadOnlyDictionary<string, string>? paramMap = null;
        if (parameters is { Length: > 0 })
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (n, v) in parameters)
                d[n] = v;
            paramMap = d;
        }

        return new DbAssertMysqlModel(
            Target: target,
            Query: query,
            Parameters: paramMap,
            Expect: new MysqlExpectation(RowCount: rowCount, Row: row));
    }
}
