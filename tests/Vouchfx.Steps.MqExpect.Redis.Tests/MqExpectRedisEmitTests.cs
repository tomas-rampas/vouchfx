// Tests for MqExpectRedisProvider — CSX emitter, resource + compile-reference
// contributors, and full compile-and-run round-trips (non-docker).
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' in the emitted StatementBlock.
//   3.  Emit: helper class is named 'MqExpectRedis_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: payloadContains absent → null literal in StatementBlock.
//   6.  Full compile-and-run (no docker): EnvironmentError when conn key is absent.
//   7.  Full compile-and-run (no docker): EnvironmentError when Redis is unreachable.
//   8.  Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers sources (§17 parity).
//   9.  Full compile-and-run (no docker): missing ${secret:env/…} in payloadContains →
//       EnvironmentError, observation is REFERENCE-ONLY (§17), with NO connection ever opened.
//   10. Emit: RequiredHelpers bounds the XRANGE scan with an explicit count (mirrors
//       mq-expect.nats's MaxMsgs=10000 cap) — the emitted CSX must never call
//       StreamRangeAsync unbounded.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Redis;
using Xunit;

namespace Vouchfx.Steps.MqExpect.Redis.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MqExpectRedisProvider"/>.
/// </summary>
public sealed class MqExpectRedisEmitTests
{
    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(string stepId) => StepId = stepId;
        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }

    private readonly MqExpectRedisProvider _provider = new();

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
    };

    private static MqExpectRedisModel GetModel(
        string target = "cache",
        string stream = "orders",
        string? payloadContains = "shipped") =>
        new MqExpectRedisModel(target, stream, new RedisMatch(payloadContains, null));

    // ── 1. StatementBlock braces ──────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("exp-step"));
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'), "StatementBlock must begin with '{'.");
        Assert.True(block.EndsWith('}'), "StatementBlock must end with '}'.");
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("my-step"));

        // Check the StatementBlock only — RequiredHelpers are static, hand-authored,
        // and validated separately; they may mention "using var" in doc comment prose.
        Assert.DoesNotContain("using var", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsMqExpectRedisPrefixedClass()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("exp-step"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MqExpectRedis_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step id sanitisation ───────────────────────────────────────────────

    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "exp-step-one";
        var safeId = CsxFragment.SanitiseId(rawId);
        var fragment = _provider.Emit(GetModel(), new StubCompileContext(rawId));

        Assert.Contains(VarKeys.Outcome(safeId), fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. payloadContains absent → null literal ──────────────────────────────

    [Fact]
    public void Emit_PayloadContainsAbsent_EmitsNullLiteral()
    {
        var model = new MqExpectRedisModel("cache", "orders", new RedisMatch(null, new Dictionary<string, string> { ["$.a"] = "1" }));
        var fragment = _provider.Emit(model, new StubCompileContext("exp-step"));

        Assert.Contains("null", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. Full compile-and-run: EnvironmentError when conn key absent ─────────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = GetModel();
        var outcome = await RunStepAsync(model, "exp-step", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 7. Full compile-and-run: EnvironmentError when Redis unreachable ───────

    [Fact]
    public async Task Emit_CompileAndRun_RedisUnreachable_ReturnsEnvironmentError()
    {
        var model = GetModel();
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("cache")] = "127.0.0.1:1,abortConnect=true,connectTimeout=200,connectRetry=0",
        };

        var outcome = await RunStepAsync(model, "exp-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 8. RequiredHelpers includes Substitute_Helpers and Secret_Helpers ────────

    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("h-check"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 9. Compile round-trip: EnvironmentError via SECRET resolution (no docker) ─

    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInPayloadContains_ReturnsEnvironmentError_ReferenceOnly()
    {
        var envName = "VOUCHFX_MQEXP_REDIS_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "exp-secret-step";
            const string target = "cache";

            var model = new MqExpectRedisModel(
                target, "orders",
                new RedisMatch($"${{secret:env/{envName}}}", null));

            var fragment = _provider.Emit(model, new StubCompileContext(stepId));
            var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
            var helpers = string.Join("\n", fragment.RequiredHelpers);
            var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_additionalRefs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection(target)] = "127.0.0.1:1,abortConnect=true",
            };

            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
            Assert.True(vars.ContainsKey(outcomeKey),
                $"Expected Vars to contain outcome key '{outcomeKey}'. " +
                $"Actual keys: [{string.Join(", ", vars.Keys)}]");

            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.NotNull(outcome.Observation);
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("env", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 10. RequiredHelpers bounds the XRANGE scan with an explicit count ────────

    /// <summary>
    /// The emitted helper must call <c>StreamRangeAsync</c> with an explicit
    /// <c>count</c> bound (mirrors mq-expect.nats's <c>MaxMsgs=10000</c> cap) — an
    /// unbounded <c>StreamRangeAsync(stream, "-", "+")</c> call would let a
    /// sufficiently large retained stream make every attempt scan unboundedly.
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_BoundsStreamRangeScanWithExplicitCount()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("exp-step"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("StreamRangeAsync(stream, \"-\", \"+\", 10000)", StringComparison.Ordinal));
        Assert.DoesNotContain(fragment.RequiredHelpers, h =>
            h.Contains("StreamRangeAsync(stream, \"-\", \"+\")", StringComparison.Ordinal));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<StepOutcome> RunStepAsync(
        MqExpectRedisModel model,
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
