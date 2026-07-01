// Tests for MqExpectRabbitmqProvider — CSX emitter, resource + compile-reference
// contributors, and full compile-and-run round-trips (non-docker).
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' in the emitted fragment.
//   3.  Emit: helper class is named 'MqExpectRabbitmq_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: payloadContains absent → null literal in StatementBlock.
//   6.  Full compile-and-run (no docker): EnvironmentError when conn key is absent.
//   7.  Full compile-and-run (no docker): EnvironmentError when the broker is unreachable.
//   8.  Full compile-and-run (no docker): AMQP credentials are redacted from the observation.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.MqExpect.Rabbitmq;
using Xunit;

namespace Platform.Steps.MqExpect.Rabbitmq.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MqExpectRabbitmqProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class MqExpectRabbitmqEmitTests
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

    private readonly MqExpectRabbitmqProvider _provider = new();

    /// <summary>
    /// Compile-time metadata references for the emitted CSX body.  RabbitMQ.Client,
    /// JsonPath.Net, System.Text.Json, and System.Text.RegularExpressions are not in
    /// the default TPA subset, so they must be supplied explicitly.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(RabbitMQ.Client.ConnectionFactory).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        // System.Globalization.CultureInfo is needed for scanned.ToString(InvariantCulture)
        // in the expect helper's observation string.
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        // System.Uri is forwarded to System.Private.Uri; Roslyn does not pick it up
        // automatically from System.Runtime, so it must be added explicitly.
        typeof(System.Uri).Assembly.Location,
    };

    private static MqExpectRabbitmqModel GetModel(
        string target = "rmq",
        string queue = "orders",
        string? payloadContains = "hello") =>
        new MqExpectRabbitmqModel(
            target,
            queue,
            new RabbitmqMatch(payloadContains, null, null));

    // ── 1. StatementBlock braces ──────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("expect-step"));
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
    public void Emit_RequiredHelpers_ContainsMqExpectRabbitmqPrefixedClass()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("expect-step"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MqExpectRabbitmq_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step id sanitisation ───────────────────────────────────────────────

    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "expect-step-one";
        var safeId = CsxFragment.SanitiseId(rawId); // "expect_step_one"
        var fragment = _provider.Emit(GetModel(), new StubCompileContext(rawId));

        Assert.Contains(VarKeys.Outcome(safeId), fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. payloadContains absent → null literal ──────────────────────────────

    [Fact]
    public void Emit_PayloadContainsAbsent_EmitsNullLiteral()
    {
        var fragment = _provider.Emit(GetModel(payloadContains: null), new StubCompileContext("expect-step"));

        Assert.Contains("null", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. Full compile-and-run: EnvironmentError when conn key absent ─────────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        var model = GetModel();
        var outcome = await RunStepAsync(model, "expect-step", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 7. Full compile-and-run: EnvironmentError when broker unreachable ──────

    [Fact]
    public async Task Emit_CompileAndRun_BrokerUnreachable_ReturnsEnvironmentError()
    {
        var model = GetModel();

        // Use an unreachable endpoint — nothing listens on 57902.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("rmq")] = "amqp://guest:guest@localhost:57902/",
        };

        var outcome = await RunStepAsync(model, "expect-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 8. Full compile-and-run: AMQP credentials are redacted ───────────────

    [Fact]
    public async Task Emit_CompileAndRun_CredentialedConnFails_CredentialAbsentFromObservation()
    {
        var model = GetModel();
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("rmq")] = "amqp://admin:t0ps3cr3t@localhost:57902/",
        };

        var outcome = await RunStepAsync(model, "expect-cred-leak-check", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.DoesNotContain("t0ps3cr3t", outcome.Observation, StringComparison.Ordinal);
        Assert.DoesNotContain("admin:t0ps3cr3t", outcome.Observation, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits the fragment for a <c>mq-expect.rabbitmq</c> step, assembles it, compiles it
    /// once, and executes it with the supplied <c>Vars</c> dictionary.  Returns the
    /// <see cref="StepOutcome"/> written by the emitted helper.
    /// </summary>
    private async Task<StepOutcome> RunStepAsync(
        MqExpectRabbitmqModel model,
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
