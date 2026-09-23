// Tests for CacheAssertRedisProvider — CSX emitter, resource + compile-reference
// contributors, and full compile-and-run round-trips (non-docker).
//
// Mirrors Vouchfx.Steps.DbAssert.SqlServer.Tests/DbAssertSqlServerEmitTests.cs.
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' in the emitted fragment.
//   3.  Emit: helper class is named 'CacheAssertRedis_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: key / expected value are JSON-escaped (injection safety).
//   6.  Resources: yields a redis ResourceRequirement whose Name equals model.Target.
//   7.  CompileReferenceAssemblies: contains the StackExchange.Redis assembly.
//   8.  Full compile-and-run (no docker): EnvironmentError when conn key is absent.
//   9.  Full compile-and-run (no docker): EnvironmentError when the endpoint is dead.
//   10. Full compile-and-run (no docker): every op compiles + returns EnvironmentError
//       against a dead endpoint (covers get/exists/hget/hlen/llen/scard dispatch).
//   11. Full compile-and-run (no docker): a credentialed conn failing must not leak the
//       password into the observation (§17 redaction regression).
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Redis;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Steps.CacheAssert.Redis.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="CacheAssertRedisProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class CacheAssertRedisEmitTests
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

    private readonly CacheAssertRedisProvider _provider = new();

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  StackExchange.Redis,
    /// System.Text.Json, System.Globalization and System.Text.RegularExpressions are not
    /// in the default TPA subset, so they must be supplied explicitly.  These are
    /// compile-time references only; at runtime the assemblies resolve from the Default ALC.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    /// <summary>
    /// The connection string for a Redis endpoint that cannot answer.  The authority comes
    /// from a held <see cref="DeadLoopbackEndpoint"/> reservation — the port is dead because
    /// this process owns it for the row's lifetime, not because the file asserts nothing is
    /// listening there (#527).  The tuning that follows is what keeps the row fast:
    /// abortConnect=false stops Connect itself from throwing, and connectRetry=0 plus the
    /// small timeouts bound the wait.  It is spelled once here for every row.
    /// </summary>
    /// <param name="dead">The held reservation whose authority the client is pointed at.</param>
    /// <param name="credentials">
    /// Credential tokens to splice in (e.g. <c>password=…,user=…</c>) for the rows that
    /// exercise the redaction path; omitted otherwise.
    /// </param>
    private static string DeadConn(DeadLoopbackEndpoint dead, string? credentials = null) =>
        $"{dead.Authority}," +
        (credentials is null ? string.Empty : credentials + ",") +
        "abortConnect=false,connectTimeout=200,connectRetry=0,syncTimeout=800";

    // ── 1. StatementBlock braces ──────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(GetModel("cache", "k", "v"), new StubCompileContext("check-step"));
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'), "StatementBlock must begin with '{'.");
        Assert.True(block.EndsWith('}'), "StatementBlock must end with '}'.");
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var fragment = _provider.Emit(GetModel("cache", "k", "v"), new StubCompileContext("my-step"));
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsCacheAssertRedisPrefixedClass()
    {
        var fragment = _provider.Emit(GetModel("cache", "k", "v"), new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("CacheAssertRedis_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ───────────────────────────────────────────────

    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "check-cache-value";
        var safeId = CsxFragment.SanitiseId(rawId); // "check_cache_value"
        var fragment = _provider.Emit(GetModel("cache", "k", "v"), new StubCompileContext(rawId));

        Assert.Contains(VarKeys.Outcome(safeId), fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. JSON-escaped values ────────────────────────────────────────────────

    [Fact]
    public void Emit_SpecialCharactersInKeyAndValue_AreJsonEscaped()
    {
        const string dangerousKey = "key\"with\\quotes";
        const string dangerousValue = "val\"with\\quotes";
        var model = GetModel("cache", dangerousKey, dangerousValue);
        var fragment = _provider.Emit(model, new StubCompileContext("escape-test"));

        // The raw unescaped strings must not appear verbatim — they would break the literal.
        Assert.DoesNotContain(dangerousKey, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(dangerousValue, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. IResourceContributor ───────────────────────────────────────────────

    [Fact]
    public void Resources_YieldsRedisRequirementWithMatchingName()
    {
        var requirements = _provider.Resources(GetModel("cache", "k", "v")).ToList();

        Assert.Single(requirements);
        Assert.Equal("redis", requirements[0].Family, StringComparer.Ordinal);
        Assert.Equal("cache", requirements[0].Name, StringComparer.Ordinal);
    }

    // ── 7. ICompileReferenceContributor ───────────────────────────────────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsRedisAssembly()
    {
        Assert.Contains(((ICompileReferenceContributor)_provider).CompileReferenceAssemblies.ToList(), a =>
            a.GetName().Name?.Contains("StackExchange.Redis", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 8. Compile round-trip: EnvironmentError when conn key absent ──────────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = GetModel("missing-dep", "k", "v");

        // No connection key seeded in Vars — the helper must detect the absence and write
        // EnvironmentError rather than throwing.
        var outcome = await RunStepAsync(
            model, "cache-step", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 9. Compile round-trip: EnvironmentError when the endpoint is dead ──────

    [Fact]
    public async Task Emit_CompileAndRun_DeadEndpoint_ReturnsEnvironmentError()
    {
        var model = GetModel("cache", "user:42", "active");

        using var dead = DeadLoopbackEndpoint.Reserve();
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("cache")] = DeadConn(dead),
        };

        var outcome = await RunStepAsync(model, "cache-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("error", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 10. Compile round-trip: every op dispatches + fails cleanly ───────────

    [Theory]
    [InlineData(RedisOp.Get)]
    [InlineData(RedisOp.Exists)]
    [InlineData(RedisOp.Ttl)]
    [InlineData(RedisOp.HGet)]
    [InlineData(RedisOp.HLen)]
    [InlineData(RedisOp.LLen)]
    [InlineData(RedisOp.SCard)]
    public async Task Emit_CompileAndRun_EachOp_CompilesAndReturnsEnvironmentError(RedisOp op)
    {
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "user:42",
            Operation: op,
            Field: op == RedisOp.HGet ? "status" : null,
            Expect: new RedisExpectation(
                Value: op is RedisOp.Get or RedisOp.HGet ? "active" : null,
                Exists: op is RedisOp.Exists or RedisOp.Ttl ? true : null,
                Length: op is RedisOp.HLen or RedisOp.LLen or RedisOp.SCard ? 1L : null));

        using var dead = DeadLoopbackEndpoint.Reserve();
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("cache")] = DeadConn(dead),
        };

        var outcome = await RunStepAsync(model, "cache-" + CacheAssertRedisProvider.OpToken(op), vars);

        // With no server reachable, every op path must surface EnvironmentError — proving
        // the emitted CSX compiles for each op and the switch dispatches correctly.
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 11. Compile round-trip: credential absent from observation on failure ──
    //
    // WHAT THIS ROW CAN AND CANNOT ASSERT — read before "tightening" it to an exact
    // equality the way the Elasticsearch twin does
    // (CacheAssertElasticsearchEmitTests, row 13).  There the provider's generic catch
    // emits ONLY ex.GetType().Name, so the whole observation is a 30-character constant
    // and equality is the right pin.  Here all three catches
    // (CacheAssertRedisProvider.cs:511, :517, :533) emit
    // RedactCredentials(connStr, ex.Message) — the REDACTED CLIENT MESSAGE.  MEASURED on
    // this host (2026-09-18): the observation is
    // {"error":"The message timed out in the backlog attempting to send … UnableToConnect
    // on 127.0.0.1:<port>/Interactive … v: 2.13.1.38939 …"} — several hundred characters
    // of StackExchange.Redis diagnostics, including the ENDPOINT it failed to reach and
    // the client version.  MEASURED which catch that arrives through, because the wording
    // misleads: despite reading as a timeout it is a RedisConnectionException, so :511 is
    // this row's path and a mutation drill aimed at :517 does not redden it.  So:
    //   • host and port ARE present, BY DESIGN.  RedactCredentials
    //     (CacheAssertRedisProvider.cs:560) strips credential tokens, not endpoints, and
    //     an endpoint is not credential material (§17).  Their absence is deliberately
    //     NOT asserted — it would pin a third-party message, and a bare port number is a
    //     4-5 digit substring that this message is full of.
    //   • no ENCODED spelling of the password can reach the observation.  The grammar is
    //     comma-separated key=value and the client sends AUTH as plaintext RESP, so the
    //     password has exactly one spelling.  There is no base64 header as in the
    //     Elasticsearch case, hence no encoded form whose absence could be asserted.
    //   • the password=/user= regexes in RedactCredentials are load-bearing, not
    //     belt-and-braces, because the client can produce a `password=` fragment that is
    //     NOT the literal connection string — which the literal replacement therefore
    //     misses.  MEASURED on the pinned StackExchange.Redis 2.13.1 (FileVersion
    //     2.13.1.38939): ConfigurationOptions.Parse(conn).ToString() returns
    //     `…,user=default,password=sup3rsecret,abortConnect=False,…` — tokens REORDERED
    //     and `abortConnect` recased, so it is not the input string.  The parameterless
    //     overload defaults to includePassword:true and does NOT mask; only the explicit
    //     ToString(false) renders `password=*****`.  (Recorded because the DLL contains
    //     both an `includePassword` parameter and the literal `*****`, and reading those
    //     two facts statically invites the opposite conclusion.)
    // Each assertion uses the boolean form: xunit prints the ACTUAL on a failed
    // Assert.DoesNotContain, and the actual here is the string under suspicion of
    // carrying the secret.

    [Fact]
    public async Task Emit_CompileAndRun_CredentialedConnFails_CredentialAbsentFromObservation()
    {
        using var dead = DeadLoopbackEndpoint.Reserve();
        var connStr = DeadConn(dead, "password=sup3rsecret,user=default");
        var model = GetModel("cache", "user:42", "active");

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("cache")] = connStr,
        };

        var outcome = await RunStepAsync(model, "cache-cred-leak-check", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        var obs = outcome.Observation!;

        // §17: the password must never appear in the observation, in any spelling.
        Assert.True(
            !obs.Contains("sup3rsecret", StringComparison.Ordinal),
            "observation leaked the password");

        // The only row here with detection power of its own: `user=default` shares no
        // substring with the password, so a regression that leaked the username alone
        // would pass the row above and fail this one.  RedactCredentials strips the
        // `user=` token for that reason — a username is credential material even though
        // it is not the secret half of the pair.
        Assert.True(
            !obs.Contains("user=default", StringComparison.Ordinal),
            "observation leaked the user= token");

        // The whole connection string echoed verbatim.  This CONTAINS the password, so
        // the first row already fails whenever this one would — it is kept so a failure
        // names the leak shape rather than merely reporting that a secret appeared.
        Assert.True(
            !obs.Contains(connStr, StringComparison.Ordinal),
            "observation echoed the connection string verbatim");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static CacheAssertRedisModel GetModel(string target, string key, string expectedValue) =>
        new(
            Target: target,
            Key: key,
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: expectedValue, Exists: null, Length: null));

    /// <summary>
    /// Emits the fragment for a <c>cache-assert.redis</c> step, assembles it, compiles it
    /// once, and executes it with the supplied <c>Vars</c> dictionary.  Returns the
    /// <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        CacheAssertRedisModel model,
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
