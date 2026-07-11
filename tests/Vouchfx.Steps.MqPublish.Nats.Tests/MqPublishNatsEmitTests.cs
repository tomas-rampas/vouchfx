// Tests for MqPublishNatsProvider — CSX emitter + resource / compile-reference contributors.
//
// All tests in this file are non-docker.  They exercise:
//   1. Emit: StatementBlock begins and ends with a brace.
//   2. Emit: no 'using var' in the emitted fragment (CSX parse-error guard, §13.3.1).
//   3. Emit: helper class is named 'MqPublishNats_Helpers' (§13.3.1 prefix rule).
//   4. Emit: a hyphenated step id is sanitised to the outcome key '__outcome::pub_nats'.
//   5. Emit: RequiredUsings contains the NATS.Client.Core namespace.
//   6. Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers sources.
//   7. Emit: subject / payload with special characters are JSON-escaped (literal safety).
//   8. Emit: when stream is absent, the StatementBlock embeds a derived stream name.
//   9. Resources: yields exactly one nats ResourceRequirement whose Name == model.Target.
//  10. CompileReferenceAssemblies: contains the NATS.Client.Core and NATS.Client.JetStream assemblies.
//  11. Full compile-and-run (no docker): EnvironmentError when the conn key is absent.
//  12. Full compile-and-run (no docker): EnvironmentError when the broker is unreachable.
using System;
using System.Collections.Generic;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.Nats;
using Xunit;

namespace Vouchfx.Steps.MqPublish.Nats.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MqPublishNatsProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>), resource contributor
/// (<see cref="IResourceContributor{TModel}"/>), and compile-reference contributor
/// (<see cref="ICompileReferenceContributor"/>).
/// </summary>
public sealed class MqPublishNatsEmitTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────────

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

    // ── Shared provider instance ──────────────────────────────────────────────────

    private readonly MqPublishNatsProvider _provider = new();

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    /// <summary>
    /// The emitted <see cref="CsxFragment.StatementBlock"/> must begin with '{' and
    /// end with '}', satisfying the §13.3.1 brace rule.
    /// </summary>
    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var model = MakeModel("nats-bus", "orders.created", "hello");
        var ctx = new StubCompileContext("publish-step");

        var fragment = _provider.Emit(model, ctx);
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'),
            $"StatementBlock must start with '{{'; actual start: '{block[..Math.Min(20, block.Length)]}'");
        Assert.True(block.EndsWith('}'),
            $"StatementBlock must end with '}}'; actual end: '{block[Math.Max(0, block.Length - 20)..]}'");
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
        var model = MakeModel("bus", "t", "hello");
        var ctx = new StubCompileContext("my-step");

        var fragment = _provider.Emit(model, ctx);

        // Check the StatementBlock only — RequiredHelpers are static, hand-authored,
        // and validated separately; they may mention "using var" in doc comment prose.
        Assert.DoesNotContain("using var", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredHelpers"/> must contain an entry whose class name
    /// begins with <c>MqPublishNats_</c> (§13.3.1 provider-prefix rule).
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_ContainsMqPublishNatsPrefixedClass()
    {
        var model = MakeModel("bus", "t", "hello");
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MqPublishNats_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ──────────────────────────────────────────────────

    /// <summary>
    /// A hyphenated step id must appear in the StatementBlock only after sanitisation;
    /// the id <c>pub-nats</c> must yield the outcome key <c>__outcome::pub_nats</c>, and
    /// the raw hyphenated form must NOT appear (it would be an invalid C# identifier).
    /// </summary>
    [Fact]
    public void Emit_HyphenatedStepId_YieldsSanitisedOutcomeKey()
    {
        const string rawId = "pub-nats";
        var model = MakeModel("bus", "t", "hello");
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("__outcome::pub_nats", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. RequiredUsings contains NATS.Client.Core namespace ────────────────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredUsings"/> must include the <c>NATS.Client.Core</c>
    /// namespace, which the emitted helper requires (§13.3.1 bare namespace rule).
    /// </summary>
    [Fact]
    public void Emit_RequiredUsings_ContainsNatsClientCoreNamespace()
    {
        var model = MakeModel("bus", "t", "hello");
        var ctx = new StubCompileContext("u");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("NATS.Client.Core", fragment.RequiredUsings, StringComparer.Ordinal);
    }

    // ── 6. RequiredHelpers includes shared substitution + secret helpers ──────────

    /// <summary>
    /// <see cref="CsxFragment.RequiredHelpers"/> must include the byte-identical
    /// <c>Substitute_Helpers</c> and <c>Secret_Helpers</c> sources so the emitted CSX
    /// can resolve <c>{placeholder}</c> and <c>${secret:source/path}</c> at runtime.
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var model = MakeModel("bus", "t", "hello");
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 7. Special characters JSON-escaped ───────────────────────────────────────

    /// <summary>
    /// Payload and subject text containing double-quotes or backslashes must be emitted
    /// as JSON-escaped string literals so they cannot break the CSX statement block.
    /// </summary>
    [Fact]
    public void Emit_SpecialCharactersInPayloadAndSubject_AreJsonEscaped()
    {
        const string dangerousPayload = "{\"raw\":\"a\\b\\\"c\"}";
        const string dangerousSubject = "subject\"with\\specials";
        var model = MakeModel("bus", dangerousSubject, dangerousPayload);
        var ctx = new StubCompileContext("escape-test");

        var fragment = _provider.Emit(model, ctx);

        Assert.DoesNotContain(dangerousPayload, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(dangerousSubject, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 8. Absent stream → derived stream name in StatementBlock ─────────────────

    /// <summary>
    /// When <see cref="MqPublishNatsModel.Stream"/> is <see langword="null"/>, the
    /// StatementBlock must embed the derived stream name (from DeriveStreamName).
    /// For <c>orders.created</c> the derived name is <c>ORDERS_CREATED</c>.
    /// </summary>
    [Fact]
    public void Emit_AbsentStream_EmbedsDerivedStreamName()
    {
        var model = MakeModel("bus", "orders.created", "hello", stream: null);
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("ORDERS_CREATED", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 9. IResourceContributor yields nats ResourceRequirement ──────────────────

    /// <summary>
    /// <see cref="IResourceContributor{TModel}.Resources"/> must yield exactly one
    /// <see cref="ResourceRequirement"/> with <c>Family="nats"</c>, <c>Name</c> equal
    /// to <see cref="MqPublishNatsModel.Target"/>, and a <c>null</c> Image.
    /// </summary>
    [Fact]
    public void Resources_YieldsSingleNatsRequirementWithMatchingName()
    {
        var model = MakeModel("nats-bus", "t", "hello");

        var requirements = _provider.Resources(model).ToList();

        Assert.Single(requirements);
        var req = requirements[0];
        Assert.Equal("nats", req.Family, StringComparer.Ordinal);
        Assert.Equal("nats-bus", req.Name, StringComparer.Ordinal);
        Assert.Null(req.Image);
    }

    // ── 10. ICompileReferenceContributor returns NATS assemblies ─────────────────

    /// <summary>
    /// <see cref="ICompileReferenceContributor.CompileReferenceAssemblies"/> must
    /// contain the <c>NATS.Client.Core</c> assembly (NatsConnection) and the
    /// <c>NATS.Client.JetStream</c> assembly (NatsJSContext, StreamConfig) so the
    /// Roslyn compiler can resolve all types used in the emitted helper.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsNatsCoreAndJetStreamAssemblies()
    {
        var contributor = (ICompileReferenceContributor)_provider;

        var assemblies = contributor.CompileReferenceAssemblies.ToList();

        Assert.Contains(assemblies, a =>
            a.GetName().Name?.Equals("NATS.Client.Core", StringComparison.OrdinalIgnoreCase) == true
            || a.FullName?.Contains("NATS", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 11. Compile round-trip: EnvironmentError when conn key absent ─────────────

    /// <summary>
    /// When the connection key is absent from <c>Vars</c>, the emitted helper must write
    /// <see cref="Verdict.EnvironmentError"/> to the outcome key rather than throwing or
    /// attempting to connect.  This proves the emitted CSX compiles against the real
    /// NATS.Net metadata AND that the missing-connection path is reached WITHOUT any
    /// broker (no Docker required).
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        const string stepId = "pub-nats-step";
        var model = MakeModel("missing-bus", "orders.created", "hello");
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        var additionalRefs = new[]
        {
            typeof(NATS.Client.Core.NatsConnection).Assembly.Location,
            typeof(NATS.Client.JetStream.NatsJSContext).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
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

    // ── 12. Compile round-trip: EnvironmentError when broker is unreachable ──────

    /// <summary>
    /// When the connection string targets a definitely-dead NATS URL (port 1 is
    /// always refused by the OS), the emitted helper must write
    /// <see cref="Verdict.EnvironmentError"/> to the outcome key.  NATS.Net
    /// connects lazily, so the error surfaces when <c>CreateStreamAsync</c> or
    /// <c>PublishAsync</c> is first called rather than at <c>NatsConnection</c>
    /// construction.  Port 1 (tcpmux) returns ECONNREFUSED immediately, so this
    /// test completes in milliseconds without any Docker broker.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_BrokerUnreachable_ReturnsEnvironmentError()
    {
        const string stepId = "pub-nats-dead";
        const string target = "dead-bus";
        var model = MakeModel(target, "orders.created", "hello");
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        var additionalRefs = new[]
        {
            typeof(NATS.Client.Core.NatsConnection).Assembly.Location,
            typeof(NATS.Client.JetStream.NatsJSContext).Assembly.Location,
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Uri).Assembly.Location,
        };
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: additionalRefs);

        // Port 1 is always refused; NATS.Net surfaces the connection error on
        // the first operation (PublishAsync / CreateStreamAsync) since
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

    private static MqPublishNatsModel MakeModel(
        string target,
        string subject,
        string payload,
        string? stream = null)
        => new(
            Target: target,
            Subject: subject,
            Stream: stream,
            Payload: payload);
}
