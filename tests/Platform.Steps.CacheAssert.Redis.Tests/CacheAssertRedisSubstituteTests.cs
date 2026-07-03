// S04-B-03 — CacheAssertRedisProvider substitution emit tests (non-docker).
//
// Verifies that CacheAssertRedisProvider.Emit:
//   • passes the RAW key / field / expect.value templates (with {placeholder} tokens
//     intact) into the emitted helper call — never pre-resolved at the call site;
//   • the Substitute_Helpers.Resolve calls live in RequiredHelpers
//     (CacheAssertRedis_Helpers), so resolution happens at EXECUTION time (§17);
//   • Substitute_Helpers is contributed to RequiredHelpers;
//   • a {placeholder} in the key resolves at runtime without throwing (compile-and-run).
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.CacheAssert.Redis;
using Xunit;

namespace Platform.Steps.CacheAssert.Redis.Tests;

/// <summary>
/// S04-B-03: emit-lint + compile-and-run tests for
/// <see cref="CacheAssertRedisProvider"/> substitution wiring.
/// </summary>
public sealed class CacheAssertRedisSubstituteTests
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

    private readonly CacheAssertRedisProvider _provider = new();

    // ── Raw templates survive in the StatementBlock ───────────────────────────

    [Fact]
    public void Emit_KeyFieldValueTemplates_SurviveAsLiteralInStatementBlock()
    {
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "user:{userId}",
            Operation: RedisOp.HGet,
            Field: "{fieldName}",
            Expect: new RedisExpectation(Value: "{expectedStatus}", Exists: null, Length: null));
        var ctx = new StubCompileContext("cache-step");

        var block = _provider.Emit(model, ctx).StatementBlock;

        // The {placeholder} tokens survive verbatim (they are resolved at runtime, §17).
        Assert.Contains("user:{userId}", block, StringComparison.Ordinal);
        Assert.Contains("{fieldName}", block, StringComparison.Ordinal);
        Assert.Contains("{expectedStatus}", block, StringComparison.Ordinal);

        // They are passed as the exact JSON-escaped literals (not a Resolve() expression).
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(model.Key), block, StringComparison.Ordinal);
        Assert.DoesNotContain("Substitute_Helpers.Resolve", block, StringComparison.Ordinal);
    }

    // ── Resolve calls live in the helper (execution-time resolution) ──────────

    [Fact]
    public void Emit_ResolveCalls_LiveInHelper_NotStatementBlock()
    {
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "user:{userId}",
            Operation: RedisOp.HGet,
            Field: "{fieldName}",
            Expect: new RedisExpectation(Value: "{expectedStatus}", Exists: null, Length: null));
        var ctx = new StubCompileContext("cache-step");

        var fragment = _provider.Emit(model, ctx);
        var helpers = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("Substitute_Helpers.Resolve(vars, keyTemplate)", helpers, StringComparison.Ordinal);
        Assert.Contains("Substitute_Helpers.Resolve(vars, fieldTemplate)", helpers, StringComparison.Ordinal);
        Assert.Contains("Substitute_Helpers.Resolve(vars, expectValueTemplate)", helpers, StringComparison.Ordinal);
    }

    // ── Substitute_Helpers is in RequiredHelpers ──────────────────────────────

    [Fact]
    public void Emit_SubstituteHelpersInRequiredHelpers()
    {
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));

        var fragment = _provider.Emit(model, new StubCompileContext("s"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("Substitute_Helpers", helpers, StringComparison.Ordinal);
    }

    // ── No 'using var' after substitution integration ─────────────────────────

    [Fact]
    public void Emit_NoUsingVar_AfterSubstituteIntegration()
    {
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "user:{userId}",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "{status}", Exists: null, Length: null));

        var fragment = _provider.Emit(model, new StubCompileContext("sub-test"));
        var full = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", full, StringComparison.Ordinal);
    }

    // ── Compile-and-run: {placeholder} in key resolves without throwing ───────

    /// <summary>
    /// A <c>{placeholder}</c> in the key is resolved via <c>Substitute_Helpers.Resolve</c>
    /// at execution time, BEFORE the connection attempt.  With a seeded var and a dead
    /// endpoint the step surfaces <see cref="Verdict.EnvironmentError"/> — proving the
    /// resolution path executes cleanly (a bug in the wiring would throw instead).
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_KeyPlaceholder_ResolvesThenReportsEnvironmentError()
    {
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "user:{userId}",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "active", Exists: null, Length: null));
        const string stepId = "cache-sub-run";

        var fragment = _provider.Emit(model, new StubCompileContext(stepId));
        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource,
            additionalReferencePaths: new[]
            {
                typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
                typeof(System.Text.Json.JsonSerializer).Assembly.Location,
                typeof(System.Globalization.CultureInfo).Assembly.Location,
                typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            });

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["userId"] = "42",
            [VarKeys.Connection("cache")] =
                "localhost:56789,abortConnect=false,connectTimeout=200,connectRetry=0,syncTimeout=800",
        };
        var globals = new ScriptGlobalVariables(vars);

        // Must NOT throw — Resolve runs, then the dead endpoint yields EnvironmentError.
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcome = Assert.IsType<StepOutcome>(vars[VarKeys.Outcome(CsxFragment.SanitiseId(stepId))]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }
}
