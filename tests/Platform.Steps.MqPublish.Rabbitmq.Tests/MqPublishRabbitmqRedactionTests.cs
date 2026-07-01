// §17 credential-redaction regression tests for MqPublishRabbitmqProvider (non-docker).
//
// Directly exercises the emitted MqPublishRabbitmq_Helpers.RedactAmqpUri method by
// compiling a throwaway CSX body that invokes it with a CRAFTED message that
// intentionally contains an AMQP URI with credentials.  This proves the redaction logic
// actually strips the userinfo, rather than relying on RabbitMQ.Client 7.x never
// emitting credentials in its exception messages (which is version-dependent).
//
// Background: BrokerUnreachableException.Message in 7.x does not echo the URI,
// so a live-connection test that checks DoesNotContain("s3cr3t", observation) is
// vacuously green — delete the RedactAmqpUri call and the test still passes.
// These tests guard against that regression: they supply a message KNOWN to contain
// the credentials and verify the output has been redacted.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.MqPublish.Rabbitmq;
using Xunit;

namespace Platform.Steps.MqPublish.Rabbitmq.Tests;

/// <summary>
/// Non-docker credential-redaction regression tests for the emitted
/// <c>MqPublishRabbitmq_Helpers.RedactAmqpUri</c> method.
/// </summary>
public sealed class MqPublishRabbitmqRedactionTests
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

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(RabbitMQ.Client.ConnectionFactory).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
    };

    /// <summary>
    /// <c>RedactAmqpUri</c> must strip the userinfo segment from an AMQP URI that
    /// appears verbatim in the crafted message.
    /// </summary>
    [Fact]
    public async Task RedactAmqpUri_WithCraftedMessageContainingSecret_SecretIsStripped()
    {
        // A message that DOES contain AMQP credentials, simulating a future driver
        // version or wrapper that echoes the full URI in an exception message.
        const string craftedMessage =
            "Connection failed for endpoint amqp://admin:s3cr3t@rmq-host:5672/ after 3 attempts";

        var provider = new MqPublishRabbitmqProvider();
        var model = new MqPublishRabbitmqModel("rmq", null, "orders", "hello", null);
        var fragment = provider.Emit(model, new StubCompileContext("redact-direct"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_result__\"] = MqPublishRabbitmq_Helpers.RedactAmqpUri(" +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(
            csx,
            additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.True(vars.ContainsKey("__redact_result__"),
            "Expected '__redact_result__' in Vars after calling RedactAmqpUri.");
        var result = Assert.IsType<string>(vars["__redact_result__"]);

        Assert.DoesNotContain("s3cr3t", result, StringComparison.Ordinal);
        Assert.DoesNotContain("admin:s3cr3t", result, StringComparison.Ordinal);
        Assert.Contains("***@", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Userinfo-only segment (no trailing path) is also redacted.
    /// </summary>
    [Fact]
    public async Task RedactAmqpUri_UserinfoBareHost_IsStripped()
    {
        const string craftedMessage =
            "broker refused connection: amqp://alice:p@ssw0rd@broker.example.com";

        var provider = new MqPublishRabbitmqProvider();
        var model = new MqPublishRabbitmqModel("rmq", null, "q", "payload", null);
        var fragment = provider.Emit(model, new StubCompileContext("redact-bare-host"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_result__\"] = MqPublishRabbitmq_Helpers.RedactAmqpUri(" +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(
            csx,
            additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var result = Assert.IsType<string>(vars["__redact_result__"]);
        Assert.DoesNotContain("p@ssw0rd", result, StringComparison.Ordinal);
        Assert.DoesNotContain("alice:", result, StringComparison.Ordinal);
        Assert.Contains("***@", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A message without any AMQP URI is returned unchanged (no false positives).
    /// </summary>
    [Fact]
    public async Task RedactAmqpUri_MessageWithoutUri_ReturnedUnchanged()
    {
        const string craftedMessage = "connection timed out after 5000ms";

        var provider = new MqPublishRabbitmqProvider();
        var model = new MqPublishRabbitmqModel("rmq", null, "q", "payload", null);
        var fragment = provider.Emit(model, new StubCompileContext("redact-no-uri"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_result__\"] = MqPublishRabbitmq_Helpers.RedactAmqpUri(" +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(
            csx,
            additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var result = Assert.IsType<string>(vars["__redact_result__"]);
        Assert.Equal(craftedMessage, result);
    }
}
