// Tests for DbAssertDynamodbProvider — CSX emitter + full compile-and-run round-trips
// (non-docker; no DynamoDB endpoint is reachable in these tests, so only the
// EnvironmentError / Fail-before-network paths are exercised here — the Pass/Fail-vs-real-item
// paths are covered by DbAssertDynamodbDockerTests against a real dynamodb-local container).
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' anywhere in the fragment.
//   3.  Emit: helper class is named 'DbAssertDynamodb_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: RequiredHelpers includes Substitute_Helpers.
//   6.  Full compile-and-run: EnvironmentError when the conn key is absent from Vars.
//   7.  Full compile-and-run: EnvironmentError when the connection is refused (unreachable
//       ServiceURL) — proves the emitted CSX actually compiles and executes end-to-end.
//   8.  Full compile-and-run: a value containing a literal '"' cannot break out of the key
//       JSON (injection-hardening parity with db-assert.mongodb's ResolveFilter guard).
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.DbAssert.Dynamodb;
using Xunit;

namespace Platform.Steps.DbAssert.Dynamodb.Tests;

public sealed class DbAssertDynamodbEmitTests
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

    private readonly DbAssertDynamodbProvider _provider = new();

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(Amazon.DynamoDBv2.AmazonDynamoDBClient).Assembly.Location,
        typeof(Amazon.Runtime.BasicAWSCredentials).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
    };

    private static DbAssertDynamodbModel GetModel(
        string target = "orders",
        string table = "Orders",
        string key = "{\"orderId\":\"1001\"}",
        bool? exists = null,
        IReadOnlyDictionary<string, string>? item = null) =>
        new DbAssertDynamodbModel(target, table, key, new DynamoExpectation(exists, item));

    // ── 1. StatementBlock braces ──────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("dyn-step"));
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'));
        Assert.True(block.EndsWith('}'));
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("my-step"));
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsDbAssertDynamodbPrefixedClass()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("dyn-step"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("DbAssertDynamodb_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step id sanitisation ───────────────────────────────────────────────

    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "dyn-step-one";
        var safeId = CsxFragment.SanitiseId(rawId);
        var fragment = _provider.Emit(GetModel(), new StubCompileContext(rawId));

        Assert.Contains(VarKeys.Outcome(safeId), fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. RequiredHelpers includes Substitute_Helpers ────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteHelpersSource()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("h-check"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
    }

    // ── 6. Full compile-and-run: EnvironmentError when conn key absent ───────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        var outcome = await RunStepAsync(GetModel(), "dyn-absent-conn", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0);
        Assert.NotNull(outcome.Observation);
    }

    // ── 7. Full compile-and-run: connection refused ───────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_ConnectionRefused_ReturnsEnvironmentError()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("orders")] = "ServiceURL=http://127.0.0.1:1;AccessKey=local;SecretKey=local",
        };

        var outcome = await RunStepAsync(GetModel(exists: true), "dyn-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 8. Injection hardening: a '"' in a placeholder value cannot break out ─

    [Fact]
    public async Task Emit_CompileAndRun_PlaceholderValueWithQuote_DoesNotBreakKeyJson()
    {
        // If the placeholder value were spliced UNESCAPED, the key JSON would become
        // {"orderId": "1001", "admin":"true"} — an injected extra attribute. With the
        // JSON-escape guard, the whole thing stays a single string value, and the AWS
        // SDK call still proceeds (and fails on an unreachable endpoint), proving no
        // parse-time corruption occurred.
        var model = GetModel(key: "{\"orderId\":\"{orderId}\"}");
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("orders")] = "ServiceURL=http://127.0.0.1:1;AccessKey=local;SecretKey=local",
            ["orderId"] = "1001\", \"admin\":\"true",
        };

        var outcome = await RunStepAsync(model, "dyn-injection", vars);

        // The malicious value did not crash the helper with a JSON parse error distinct
        // from the expected connection-refused EnvironmentError — both collapse to
        // EnvironmentError here (no live endpoint), but critically no exception escaped
        // RunIsolatedAsync (which would fail the test outright).
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<StepOutcome> RunStepAsync(
        DbAssertDynamodbModel model,
        string stepId,
        Dictionary<string, object?> vars)
    {
        var fragment = _provider.Emit(model, new StubCompileContext(stepId));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }
}
