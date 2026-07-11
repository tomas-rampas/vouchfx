// Tests for MqPublishAzureServiceBusProvider — CSX emitter + resource / compile-reference
// contributors.
//
// All tests in this file are non-docker.  They exercise:
//   1. Emit: StatementBlock begins and ends with a brace.
//   2. Emit: no 'using var' in the emitted fragment (CSX parse-error guard, §13.3.1).
//   3. Emit: helper class is named 'MqPublishAzureServiceBus_Helpers' (§13.3.1 prefix rule).
//   4. Emit: a hyphenated step id is sanitised to the outcome key '__outcome::pub_evt'.
//   5. Emit: RequiredUsings contains 'Azure.Messaging.ServiceBus'.
//   6. Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers sources.
//   7. Emit: queue/topic with special chars are JSON-escaped (literal safety).
//   8. Emit: topic model emits null for the queue argument.
//   9. Resources: yields exactly one azureservicebus ResourceRequirement.
//  10. CompileReferenceAssemblies: contains Azure.Messaging.ServiceBus AND Azure.Core.
//  11. Full compile-and-run (no docker): EnvironmentError when the conn key is absent.
//  12. Full compile-and-run (no docker): EnvironmentError via SECRET resolution (missing
//      ${secret:env/…} payload reference) WITHOUT a broker.
//  13. Redaction: emitted RequiredHelpers contains RedactAsbConnStr method.
//  14. Redaction: emitted RequiredHelpers contains SharedAccessKey=[^;]* regex pattern.
//  15. Redaction: regex pattern covers Base64 keys with +, /, = characters.
using System;
using System.Collections.Generic;
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqPublish.AzureServiceBus;
using Xunit;

namespace Vouchfx.Steps.MqPublish.AzureServiceBus.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MqPublishAzureServiceBusProvider"/>
/// covering the emitter, resource contributor, and compile-reference contributor.
/// </summary>
public sealed class MqPublishAzureServiceBusEmitTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────────

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

    // ── Shared provider instance + compile-time refs ──────────────────────────────

    private readonly MqPublishAzureServiceBusProvider _provider = new();

    // MAJOR 1 fix: source provider-specific refs from the provider's own
    // CompileReferenceAssemblies so that removing an assembly from the provider
    // turns tests #11 and #12 RED — not just the membership check in test #10.
    private readonly string[] _additionalRefs;

    public MqPublishAzureServiceBusEmitTests()
    {
        _additionalRefs = BuildAdditionalRefs(_provider);
    }

    private static string[] BuildAdditionalRefs(ICompileReferenceContributor provider)
    {
        // Provider-specific assemblies (e.g. Azure.Messaging.ServiceBus, Azure.Core)
        // sourced directly from the provider so the test is coupled to the provider's
        // actual declaration.
        var providerRefs = provider.CompileReferenceAssemblies
            .Select(a => a.Location)
            .Where(p => !string.IsNullOrEmpty(p));

        // BCL assemblies that RoslynScriptCompiler.BuildTpaReferences() always
        // contributes via TRUSTED_PLATFORM_ASSEMBLIES — not provider-specific.
        var bclRefs = new[]
        {
            typeof(System.Text.Json.JsonSerializer).Assembly.Location,
            typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            typeof(System.Globalization.CultureInfo).Assembly.Location,
            typeof(System.Text.Encoding).Assembly.Location,
        };

        return providerRefs.Concat(bclRefs).ToArray();
    }

    private static MqPublishAzureServiceBusModel MakeQueueModel(string target, string queue, string payload,
        Dictionary<string, string>? properties = null)
        => new MqPublishAzureServiceBusModel(target, queue, null, payload, properties);

    private static MqPublishAzureServiceBusModel MakeTopicModel(string target, string topic, string payload)
        => new MqPublishAzureServiceBusModel(target, null, topic, payload, null);

    // ── 1. StatementBlock braces ──────────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(MakeQueueModel("asb", "orders", "hello"), new StubCompileContext("s"));
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'),
            $"StatementBlock must start with '{{'; actual: '{block[..Math.Min(20, block.Length)]}'");
        Assert.True(block.EndsWith('}'),
            $"StatementBlock must end with '}}'; actual: '{block[Math.Max(0, block.Length - 20)..]}'");
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var fragment = _provider.Emit(MakeQueueModel("asb", "orders", "hello"), new StubCompileContext("s"));
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsMqPublishAzureServiceBusPrefixedClass()
    {
        var fragment = _provider.Emit(MakeQueueModel("asb", "orders", "hello"), new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MqPublishAzureServiceBus_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ───────────────────────────────────────────────────

    [Fact]
    public void Emit_HyphenatedStepId_YieldsSanitisedOutcomeKey()
    {
        const string rawId = "pub-evt";
        var fragment = _provider.Emit(MakeQueueModel("asb", "orders", "hello"), new StubCompileContext(rawId));

        Assert.Contains("__outcome::pub_evt", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. RequiredUsings contains Azure.Messaging.ServiceBus namespace ───────────

    [Fact]
    public void Emit_RequiredUsings_ContainsAzureMessagingServiceBus()
    {
        var fragment = _provider.Emit(MakeQueueModel("asb", "orders", "hello"), new StubCompileContext("s"));

        Assert.Contains("Azure.Messaging.ServiceBus", fragment.RequiredUsings, StringComparer.Ordinal);
    }

    // ── 6. RequiredHelpers includes Substitute_Helpers and Secret_Helpers ─────────

    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var fragment = _provider.Emit(MakeQueueModel("asb", "orders", "hello"), new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 7. Special characters are JSON-escaped ────────────────────────────────────

    [Fact]
    public void Emit_SpecialCharactersInQueueAndPayload_AreJsonEscaped()
    {
        const string dangerousQueue = "queue\"with\\specials";
        const string dangerousPayload = "{\"raw\":\"a\\b\\\"c\"}";
        var fragment = _provider.Emit(MakeQueueModel("asb", dangerousQueue, dangerousPayload), new StubCompileContext("s"));

        Assert.DoesNotContain(dangerousQueue, fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(dangerousPayload, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 8. Topic model emits null for queue argument ──────────────────────────────

    [Fact]
    public void Emit_TopicModel_EmitsNullForQueueArgument()
    {
        var fragment = _provider.Emit(MakeTopicModel("asb", "orders-topic", "hello"), new StubCompileContext("s"));

        // The queue argument must be the bare null keyword (not a quoted empty string).
        Assert.Contains("null,", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 9. IResourceContributor yields azureservicebus ResourceRequirement ─────────

    [Fact]
    public void Resources_YieldsSingleAzureServiceBusRequirementWithMatchingName()
    {
        var model = MakeQueueModel("my-asb", "orders", "hello");
        var requirements = _provider.Resources(model).ToList();

        Assert.Single(requirements);
        var req = requirements[0];
        Assert.Equal("azureservicebus", req.Family, StringComparer.Ordinal);
        Assert.Equal("my-asb", req.Name, StringComparer.Ordinal);
        Assert.Null(req.Image);
    }

    // ── 10. CompileReferenceAssemblies contains ServiceBus AND Azure.Core ─────────

    [Fact]
    public void CompileReferenceAssemblies_ContainsServiceBusAndAzureCore()
    {
        var contributor = (ICompileReferenceContributor)_provider;
        var names = contributor.CompileReferenceAssemblies
            .Select(a => a.GetName().Name)
            .ToList();

        Assert.Contains(names, n => n?.Equals("Azure.Messaging.ServiceBus", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(names, n => n?.Equals("Azure.Core", StringComparison.OrdinalIgnoreCase) == true);
    }

    // ── 11. Compile-and-run: EnvironmentError when conn key absent ────────────────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        const string stepId = "pub-step";
        var model = MakeQueueModel("missing-asb", "orders", "hello");
        var ctx = new StubCompileContext(stepId);
        var fragment = _provider.Emit(model, ctx);

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: _additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        // No connection key in Vars — helper must short-circuit to EnvironmentError.
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected outcome key '{outcomeKey}'. Actual: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0);
        Assert.NotNull(outcome.Observation);
    }

    // ── 12. Compile-and-run: EnvironmentError via SECRET (no broker) ──────────────

    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInPayload_ReturnsEnvironmentError_ReferenceOnly()
    {
        var envName = "VOUCHFX_MQPUBLISH_ASB_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "pub-secret-step";
            const string target = "asb";
            var model = MakeQueueModel(target, "orders", $"{{\"token\":\"${{secret:env/{envName}}}\"}}");
            var ctx = new StubCompileContext(stepId);
            var fragment = _provider.Emit(model, ctx);

            var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
            var helpers = string.Join("\n", fragment.RequiredHelpers);
            var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: _additionalRefs);

            // Stage a connection key so the helper passes the conn-key check and reaches
            // secret resolution.  The missing env var causes SecretResolutionException
            // before any ServiceBusClient is built, so no broker is contacted.
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection(target)] = "Endpoint=sb://localhost:5672;SharedAccessKeyName=Root;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;",
            };
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
            Assert.True(vars.ContainsKey(outcomeKey));
            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.NotNull(outcome.Observation);
            // Reference-only (§17): source and path, never the value.
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("env", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 13. Redaction: emitted helper contains RedactAsbConnStr ──────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsRedactAsbConnStrMethod()
    {
        var fragment = _provider.Emit(MakeQueueModel("asb", "orders", "hello"), new StubCompileContext("s"));
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("RedactAsbConnStr", helperSource, StringComparison.Ordinal);
    }

    // ── 14. Redaction: emitted helper contains SharedAccessKey regex pattern ──────

    [Fact]
    public void Emit_RequiredHelpers_ContainsSharedAccessKeyRegexPattern()
    {
        var fragment = _provider.Emit(MakeQueueModel("asb", "orders", "hello"), new StubCompileContext("s"));
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        // The regex [^;]* matches SharedAccessKey values up to the next semicolon,
        // covering Base64 keys with +, /, and = characters.
        Assert.Contains("SharedAccessKey=[^;]*", helperSource, StringComparison.Ordinal);
    }

    // ── 15. Redaction: two-layer redaction appears in emitted helpers ─────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsTwoLayerRedaction()
    {
        var fragment = _provider.Emit(MakeQueueModel("asb", "orders", "hello"), new StubCompileContext("s"));
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        // Layer (a): literal full connection-string replacement.
        Assert.Contains(".Replace(connStr,", helperSource, StringComparison.Ordinal);
        // Layer (b): regex scrub — also covers Base64 keys with +, /, = chars (FIX 6).
        Assert.Contains("SharedAccessKey=[^;]*", helperSource, StringComparison.Ordinal);
    }
}
