// Tests for MqPublishRabbitmqProvider — CSX emitter, resource + compile-reference
// contributors, and full compile-and-run round-trips (non-docker).
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' in the emitted fragment.
//   3.  Emit: helper class is named 'MqPublishRabbitmq_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: routingKey / payload are JSON-escaped (injection safety).
//   6.  Emit: exchange absent → null literal in StatementBlock.
//   7.  Full compile-and-run (no docker): EnvironmentError when conn key is absent.
//   8.  Full compile-and-run (no docker): EnvironmentError when the broker is unreachable.
//   9.  Full compile-and-run (no docker): AMQP credentials are redacted from the observation.
//  10.  Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers sources (§17 parity).
//  11.  Full compile-and-run (no docker): missing ${secret:env/…} in payload → EnvironmentError,
//       observation is REFERENCE-ONLY (secretError marker + source + path, never a value, §17).
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Rabbitmq;
using Xunit;

namespace Vouchfx.Steps.MqPublish.Rabbitmq.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MqPublishRabbitmqProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class MqPublishRabbitmqEmitTests
{
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

    private readonly MqPublishRabbitmqProvider _provider = new();

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  RabbitMQ.Client,
    /// System.Text.Json, and System.Text.RegularExpressions are not in the default TPA
    /// subset, so they must be supplied explicitly.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(RabbitMQ.Client.ConnectionFactory).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        // System.Uri is forwarded to System.Private.Uri; Roslyn does not pick it up
        // automatically from System.Runtime, so it must be added explicitly.
        typeof(System.Uri).Assembly.Location,
    };

    private static MqPublishRabbitmqModel GetModel(
        string target = "rmq",
        string? exchange = null,
        string routingKey = "q",
        string payload = "hello") =>
        new MqPublishRabbitmqModel(target, exchange, routingKey, payload, null);

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
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsMqPublishRabbitmqPrefixedClass()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("pub-step"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MqPublishRabbitmq_Helpers", StringComparison.Ordinal));
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

    // ── 5. JSON-escaped routingKey / payload ──────────────────────────────────

    [Fact]
    public void Emit_SpecialCharactersInRoutingKeyAndPayload_AreJsonEscaped()
    {
        const string dangerousKey = "order\".created";
        const string dangerousPayload = "{\"msg\":\"val\\nwith\\\"quotes\"}";
        var model = GetModel(routingKey: dangerousKey, payload: dangerousPayload);
        var fragment = _provider.Emit(model, new StubCompileContext("escape-test"));

        // The raw unescaped strings must not appear verbatim — they would break the literal.
        Assert.DoesNotContain(dangerousKey, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. Exchange absent → null literal ─────────────────────────────────────

    [Fact]
    public void Emit_ExchangeAbsent_EmitsNullLiteral()
    {
        var fragment = _provider.Emit(GetModel(exchange: null), new StubCompileContext("pub-step"));

        Assert.Contains("null", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 7. Full compile-and-run: EnvironmentError when conn key absent ─────────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = GetModel(target: "rmq", exchange: null, routingKey: "q", payload: "hello");
        var outcome = await RunStepAsync(model, "pub-step", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 8. Full compile-and-run: EnvironmentError when broker unreachable ──────

    [Fact]
    public async Task Emit_CompileAndRun_BrokerUnreachable_ReturnsEnvironmentError()
    {
        var model = GetModel(target: "rmq", exchange: null, routingKey: "q", payload: "hello");

        // Use an unreachable endpoint — nothing listens on 57901.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("rmq")] = "amqp://guest:guest@localhost:57901/",
        };

        var outcome = await RunStepAsync(model, "pub-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 9. Full compile-and-run: AMQP credentials are redacted ───────────────

    [Fact]
    public async Task Emit_CompileAndRun_CredentialedConnFails_CredentialAbsentFromObservation()
    {
        var model = GetModel(target: "rmq");
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("rmq")] = "amqp://admin:s3cr3t@localhost:57901/",
        };

        var outcome = await RunStepAsync(model, "pub-cred-leak-check", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.DoesNotContain("s3cr3t", outcome.Observation, StringComparison.Ordinal);
        Assert.DoesNotContain("admin:s3cr3t", outcome.Observation, StringComparison.Ordinal);
    }

    // ── 10. RequiredHelpers includes Substitute_Helpers and Secret_Helpers ────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredHelpers"/> must include the <c>Substitute_Helpers</c>
    /// and <c>Secret_Helpers</c> sources so the emitted CSX can resolve
    /// <c>{placeholder}</c> tokens and <c>${secret:source/path}</c> references at
    /// runtime (§17 parity with Kafka providers).
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

    // ── 11. Compile round-trip: EnvironmentError via SECRET resolution (no docker) ─

    /// <summary>
    /// When the payload carries a <c>${secret:env/…}</c> reference whose environment
    /// variable is unset, the emitted helper must write
    /// <see cref="Verdict.EnvironmentError"/> via secret resolution — NOT via a broker
    /// connection.  A connection string is staged in <c>Vars</c> so the helper passes
    /// the connection check and proceeds to secret resolution before any broker contact.
    /// The observation is REFERENCE-ONLY (§17): it carries the <c>secretError</c> marker
    /// plus the <c>env</c> source and the variable-name path, and never the
    /// (non-existent) secret value.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInPayload_ReturnsEnvironmentError_ReferenceOnly()
    {
        // Unique env name so the variable is guaranteed absent.
        var envName = "VOUCHFX_MQPUB_RMQ_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "pub-secret-step";
            const string target = "rmq";

            // Payload carries a missing secret reference.
            var model = new MqPublishRabbitmqModel(
                target,
                null,
                "orders",
                $"{{\"token\":\"${{secret:env/{envName}}}\"}}",
                null);

            var fragment = _provider.Emit(model, new StubCompileContext(stepId));
            var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
            var helpers = string.Join("\n", fragment.RequiredHelpers);
            var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_additionalRefs);

            // Stage a connection value so the helper proceeds past the connection check
            // and INTO secret resolution; no real broker is needed — resolution throws
            // SecretResolutionException before any IConnection is built.
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection(target)] = "amqp://guest:guest@localhost:57903/",
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

    private static MqPublishRabbitmqModel GetModelForStep(string target, string routingKey, string payload) =>
        new MqPublishRabbitmqModel(target, null, routingKey, payload, null);

    /// <summary>
    /// Emits the fragment for a <c>mq-publish.rabbitmq</c> step, assembles it, compiles it
    /// once, and executes it with the supplied <c>Vars</c> dictionary.  Returns the
    /// <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        MqPublishRabbitmqModel model,
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
