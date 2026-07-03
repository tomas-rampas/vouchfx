// Tests for MqExpectNatsProvider — CSX emitter + resource / compile-reference contributors.
//
// Covers:
//   1. Emit: StatementBlock begins and ends with a brace.
//   2. Emit: no 'using var' in the emitted fragment (§13.3.1 guard).
//   3. Emit: helper class is named 'MqExpectNats_Helpers' (§13.3.1 prefix rule).
//   4. Emit: a hyphenated step id is sanitised in the outcome key.
//   5. Emit: RequiredUsings contains the NATS.Client.Core namespace.
//   6. Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers.
//   7. Emit: when stream absent, derived name embedded ('ORDERS_CREATED' from 'orders.created').
//   8. Emit: json paths and expected values are emitted as parallel arrays.
//   9. Emit: payloadContains absent → 'null' literal in StatementBlock.
//  10. Resources: yields exactly one nats ResourceRequirement.
//  11. CompileReferenceAssemblies: contains NATS.Client.Core, NATS.Client.JetStream, JsonPath.Net.
//  12. Full compile-and-run (no docker): EnvironmentError when the conn key is absent.
//  13. Full compile-and-run (no docker): EnvironmentError when the broker is unreachable.
using System;
using System.Collections.Generic;
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.MqExpect.Nats;
using Xunit;

namespace Platform.Steps.MqExpect.Nats.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MqExpectNatsProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class MqExpectNatsEmitTests
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

    private readonly MqExpectNatsProvider _provider = new();

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    /// <summary>
    /// The emitted StatementBlock must begin with '{' and end with '}'.
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var model = MakeModel("bus", "orders.created", new NatsMatch("x", null));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'),
            $"StatementBlock must start with '{{'; actual: '{block[..Math.Min(20, block.Length)]}'");
        Assert.True(block.EndsWith('}'),
            $"StatementBlock must end with '}}'; actual: '...{block[Math.Max(0, block.Length - 20)..]}'");
    }

    // ── 2. No 'using var' ────────────────────────────────────────────────────────

    /// <summary>
    /// The <see cref="CsxFragment.StatementBlock"/> must not contain 'using var'
    /// (Roslyn script parse error, §13.3.1).
    /// RequiredHelpers are excluded from this check: they are hand-authored and may
    /// legitimately reference "using var" in XML doc comment text that documents the
    /// constraint itself; the check on the dynamically-generated StatementBlock is
    /// the meaningful guard.
    /// </summary>
    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var model = MakeModel("bus", "t", new NatsMatch("x", null));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        // Check the StatementBlock only — RequiredHelpers are static, hand-authored,
        // and validated separately; they may mention "using var" in doc comment prose.
        Assert.DoesNotContain("using var", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────────

    /// <summary>
    /// RequiredHelpers must contain a class named <c>MqExpectNats_Helpers</c>.
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_ContainsMqExpectNatsPrefixedClass()
    {
        var model = MakeModel("bus", "t", new NatsMatch("x", null));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MqExpectNats_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ──────────────────────────────────────────────────

    /// <summary>
    /// A hyphenated step id must appear in the StatementBlock only after sanitisation.
    /// <c>exp-nats</c> → outcome key <c>__outcome::exp_nats</c>.
    /// </summary>
    [Fact]
    public void Emit_HyphenatedStepId_YieldsSanitisedOutcomeKey()
    {
        const string rawId = "exp-nats";
        var model = MakeModel("bus", "t", new NatsMatch("x", null));
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("__outcome::exp_nats", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. RequiredUsings contains NATS.Client.Core ───────────────────────────────

    /// <summary>
    /// RequiredUsings must contain the <c>NATS.Client.Core</c> namespace.
    /// </summary>
    [Fact]
    public void Emit_RequiredUsings_ContainsNatsClientCoreNamespace()
    {
        var model = MakeModel("bus", "t", new NatsMatch("x", null));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("NATS.Client.Core", fragment.RequiredUsings, StringComparer.Ordinal);
    }

    // ── 6. RequiredHelpers includes shared helpers ────────────────────────────────

    /// <summary>
    /// RequiredHelpers must include both <c>Substitute_Helpers</c> and
    /// <c>Secret_Helpers</c> so the emitted CSX can resolve tokens at runtime.
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var model = MakeModel("bus", "t", new NatsMatch("x", null));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 7. Absent stream → derived stream name ────────────────────────────────────

    /// <summary>
    /// When <see cref="MqExpectNatsModel.Stream"/> is <see langword="null"/>, the
    /// derived name from the subject is embedded in the StatementBlock.
    /// <c>orders.created</c> → <c>ORDERS_CREATED</c>.
    /// </summary>
    [Fact]
    public void Emit_AbsentStream_EmbedsDerivedStreamName()
    {
        var model = MakeModel("bus", "orders.created", new NatsMatch("x", null), stream: null);
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("ORDERS_CREATED", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 8. Json criteria emitted as parallel arrays ────────────────────────────────

    /// <summary>
    /// When match.json is non-empty, both the paths and the value templates appear
    /// in the StatementBlock as parallel array literals passed to ExpectAsync.
    /// </summary>
    [Fact]
    public void Emit_JsonCriteria_EmittedAsParallelArrays()
    {
        var json = new Dictionary<string, string>
        {
            ["$.status"] = "NEW",
            ["$.id"] = "42",
        };
        var model = MakeModel("bus", "t", new NatsMatch(null, json));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("$.status", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("$.id", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("NEW", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("42", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 9. PayloadContains absent → 'null' literal ───────────────────────────────

    /// <summary>
    /// When <see cref="NatsMatch.PayloadContains"/> is <see langword="null"/>, the
    /// StatementBlock must contain the literal <c>null</c> for that parameter so
    /// the helper can short-circuit the substring check.
    /// </summary>
    [Fact]
    public void Emit_PayloadContainsAbsent_NullLiteralInBlock()
    {
        var json = new Dictionary<string, string> { ["$.ok"] = "1" };
        var model = MakeModel("bus", "t", new NatsMatch(PayloadContains: null, Json: json));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        // The 5th positional argument to ExpectAsync is payloadContainsTemplate and
        // must be the literal 'null' (not a JSON null string "null") so the helper's
        // null-check fires correctly inside the collectible ALC.
        Assert.Contains("null,", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 10. IResourceContributor yields nats ResourceRequirement ─────────────────

    /// <summary>
    /// Resources() yields exactly one <see cref="ResourceRequirement"/> with
    /// Family="nats" and Name equal to the target.
    /// </summary>
    [Fact]
    public void Resources_YieldsSingleNatsRequirementWithMatchingName()
    {
        var model = MakeModel("nats-bus", "t", new NatsMatch("x", null));

        var requirements = _provider.Resources(model).ToList();

        Assert.Single(requirements);
        var req = requirements[0];
        Assert.Equal("nats", req.Family, StringComparer.Ordinal);
        Assert.Equal("nats-bus", req.Name, StringComparer.Ordinal);
        Assert.Null(req.Image);
    }

    // ── 11. ICompileReferenceContributor returns NATS + JsonPath assemblies ──────

    /// <summary>
    /// CompileReferenceAssemblies must include the NATS.Client.Core assembly,
    /// the NATS.Client.JetStream assembly, and the JsonPath.Net assembly.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsNatsAndJsonPathAssemblies()
    {
        var contributor = (ICompileReferenceContributor)_provider;

        var assemblies = contributor.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Contains("NATS", StringComparison.OrdinalIgnoreCase) == true);

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Contains("JsonPath", StringComparison.OrdinalIgnoreCase) == true
            || a.GetName().Name?.Contains("Json.Path", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 12. Compile round-trip: EnvironmentError when conn key absent ─────────────

    /// <summary>
    /// When the connection key is absent from <c>Vars</c>, the emitted helper must write
    /// <see cref="Verdict.EnvironmentError"/> to the outcome key rather than throwing or
    /// attempting to connect.  Proves the emitted CSX compiles against real NATS.Net
    /// and JsonPath.Net metadata AND that the missing-connection path is correctly reached
    /// WITHOUT any broker (no Docker required).
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        const string stepId = "exp-nats-step";
        var json = new Dictionary<string, string> { ["$.ok"] = "true" };
        var model = MakeModel("missing-bus", "orders.created", new NatsMatch("needle", json));
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        var additionalRefs = new[]
        {
            typeof(NATS.Client.Core.NatsConnection).Assembly.Location,
            typeof(NATS.Client.JetStream.NatsJSContext).Assembly.Location,
            typeof(Json.Path.JsonPath).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            // System.Uri is type-forwarded to System.Private.Uri in .NET 8 and is not
            // pulled in automatically by the Roslyn metareference set — add it explicitly.
            typeof(System.Uri).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        // No connection key seeded in Vars — the helper must short-circuit to EnvironmentError.
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

    // ── 13. Compile round-trip: EnvironmentError when broker is unreachable ──────

    /// <summary>
    /// When the connection string targets a definitely-dead NATS URL (port 1 is
    /// always refused by the OS), the emitted helper must write
    /// <see cref="Verdict.EnvironmentError"/> to the outcome key.  NATS.Net
    /// connects lazily, so the error surfaces when <c>CreateStreamAsync</c> or
    /// the subscribe loop is first entered rather than at <c>NatsConnection</c>
    /// construction.  Port 1 (tcpmux) returns ECONNREFUSED immediately, so this
    /// test completes in milliseconds without any Docker broker.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_BrokerUnreachable_ReturnsEnvironmentError()
    {
        const string stepId = "exp-nats-dead";
        const string target = "dead-bus";
        var json = new Dictionary<string, string> { ["$.ok"] = "true" };
        var model = MakeModel(target, "orders.created", new NatsMatch("needle", json));
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        var additionalRefs = new[]
        {
            typeof(NATS.Client.Core.NatsConnection).Assembly.Location,
            typeof(NATS.Client.JetStream.NatsJSContext).Assembly.Location,
            typeof(Json.Path.JsonPath).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Uri).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        // Port 1 is always refused; NATS.Net surfaces the connection error on
        // the first operation (subscribe / CreateConsumerAsync) since
        // NatsConnection connects lazily — not at construction.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(target)] = "nats://127.0.0.1:1",
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
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    private static MqExpectNatsModel MakeModel(
        string target,
        string subject,
        NatsMatch match,
        string? stream = null)
        => new(
            Target: target,
            Subject: subject,
            Stream: stream,
            Match: match);
}
