// Tests for MqPublishRedisProvider — CSX emitter, resource + compile-reference
// contributors, and full compile-and-run round-trips (non-docker).
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' in the emitted fragment.
//   3.  Emit: helper class is named 'MqPublishRedis_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: stream / payload are JSON-escaped (injection safety).
//   6.  Full compile-and-run (no docker): EnvironmentError when conn key is absent.
//   7.  Full compile-and-run (no docker): EnvironmentError when Redis is unreachable.
//   8.  Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers sources (§17 parity).
//   9.  Full compile-and-run (no docker): missing ${secret:env/…} in payload → EnvironmentError,
//       observation is REFERENCE-ONLY (secretError marker + source + path, never a value, §17).
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Redis;
using Xunit;

namespace Vouchfx.Steps.MqPublish.Redis.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MqPublishRedisProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class MqPublishRedisEmitTests
{
    /// <summary>Minimal <see cref="ICompileContext"/> for emit tests.</summary>
    private sealed class StubCompileContext : ICompileContext
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

    private readonly MqPublishRedisProvider _provider = new();

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  StackExchange.Redis,
    /// System.Text.Json, and System.Text.RegularExpressions are not in the default TPA
    /// subset, so they must be supplied explicitly.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    private static MqPublishRedisModel GetModel(
        string target = "cache",
        string stream = "orders",
        string payload = "hello") =>
        new MqPublishRedisModel(target, stream, payload);

    // ── 1. StatementBlock braces ──────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("pub-step"));
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
        // and validated separately; they may mention "using var" in doc comment prose
        // (mirrors MqPublishNatsEmitTests.Emit_Fragment_ContainsNoUsingVar).
        Assert.DoesNotContain("using var", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsMqPublishRedisPrefixedClass()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("pub-step"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MqPublishRedis_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step id sanitisation ───────────────────────────────────────────────

    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "pub-step-one";
        var safeId = CsxFragment.SanitiseId(rawId); // "pub_step_one"
        var fragment = _provider.Emit(GetModel(), new StubCompileContext(rawId));

        Assert.Contains(VarKeys.Outcome(safeId), fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. JSON-escaped stream / payload ──────────────────────────────────────

    [Fact]
    public void Emit_SpecialCharactersInStreamAndPayload_AreJsonEscaped()
    {
        const string dangerousStream = "order\".events";
        const string dangerousPayload = "{\"msg\":\"val\\nwith\\\"quotes\"}";
        var model = GetModel(stream: dangerousStream, payload: dangerousPayload);
        var fragment = _provider.Emit(model, new StubCompileContext("escape-test"));

        // The raw unescaped string must not appear verbatim — it would break the literal.
        Assert.DoesNotContain(dangerousStream, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. Full compile-and-run: EnvironmentError when conn key absent ─────────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = GetModel(target: "cache", stream: "orders", payload: "hello");
        var outcome = await RunStepAsync(model, "pub-step", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 7. Full compile-and-run: EnvironmentError when Redis unreachable ───────

    [Fact]
    public async Task Emit_CompileAndRun_RedisUnreachable_ReturnsEnvironmentError()
    {
        var model = GetModel(target: "cache", stream: "orders", payload: "hello");

        // abortConnect=true + a short timeout: ConnectAsync fails fast against an
        // address nothing listens on, rather than retrying forever.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("cache")] = "127.0.0.1:1,abortConnect=true,connectTimeout=200,connectRetry=0",
        };

        var outcome = await RunStepAsync(model, "pub-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 8. RequiredHelpers includes Substitute_Helpers and Secret_Helpers ────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredHelpers"/> must include the <c>Substitute_Helpers</c>
    /// and <c>Secret_Helpers</c> sources so the emitted CSX can resolve
    /// <c>{placeholder}</c> tokens and <c>${secret:source/path}</c> references at
    /// runtime (§17 parity with the other mq-publish providers).
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var model = GetModel();
        var ctx = new StubCompileContext("h-check");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 9. Compile round-trip: EnvironmentError via SECRET resolution (no docker) ─

    /// <summary>
    /// When the payload carries a <c>${secret:env/…}</c> reference whose environment
    /// variable is unset, the emitted helper must write
    /// <see cref="Verdict.EnvironmentError"/> via secret resolution — NOT via a Redis
    /// connection.  A connection string is staged in <c>Vars</c> so the helper passes
    /// the connection check and proceeds to secret resolution before any connection
    /// attempt.  The observation is REFERENCE-ONLY (§17): it carries the
    /// <c>secretError</c> marker plus the <c>env</c> source and the variable-name
    /// path, and never the (non-existent) secret value.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInPayload_ReturnsEnvironmentError_ReferenceOnly()
    {
        // Unique env name so the variable is guaranteed absent.
        var envName = "VOUCHFX_MQPUB_REDIS_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "pub-secret-step";
            const string target = "cache";

            // Payload carries a missing secret reference.
            var model = new MqPublishRedisModel(
                target,
                "orders",
                $"{{\"token\":\"${{secret:env/{envName}}}\"}}");

            var fragment = _provider.Emit(model, new StubCompileContext(stepId));
            var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
            var helpers = string.Join("\n", fragment.RequiredHelpers);
            var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_additionalRefs);

            // Stage a connection value so the helper proceeds past the connection check
            // and INTO secret resolution; no real Redis is needed — resolution throws
            // SecretResolutionException before any ConnectionMultiplexer is built.
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection(target)] = "127.0.0.1:1,abortConnect=true",
            };

            // Real env-backed accessor — envName is unset, so resolution genuinely fails.
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            // Must NOT throw — exception is contained inside the step's guarded region.
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
            Assert.True(vars.ContainsKey(outcomeKey),
                $"Expected Vars to contain outcome key '{outcomeKey}'. " +
                $"Actual keys: [{string.Join(", ", vars.Keys)}]");

            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
            Assert.NotNull(outcome.Observation);

            // REFERENCE-ONLY contract (§17): observation names the error marker, the
            // source, and the path — NEVER a secret value.
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("env", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits the fragment for a <c>mq-publish.redis</c> step, assembles it, compiles it
    /// once, and executes it with the supplied <c>Vars</c> dictionary.  Returns the
    /// <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        MqPublishRedisModel model,
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
