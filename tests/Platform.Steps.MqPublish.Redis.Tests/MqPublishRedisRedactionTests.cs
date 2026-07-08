// §17 credential-redaction regression tests for MqPublishRedisProvider (non-docker).
//
// Directly exercises the emitted MqPublishRedis_Helpers.RedactCredentials method by
// compiling a throwaway CSX body that invokes it with a CRAFTED message containing a
// Redis connection string's password=/user= tokens.  This proves the redaction logic
// actually strips credentials, mirroring CacheAssertRedisRedactionTests /
// MqPublishRabbitmqRedactionTests exactly.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.MqPublish.Redis;
using Xunit;

namespace Platform.Steps.MqPublish.Redis.Tests;

/// <summary>
/// Non-docker credential-redaction regression tests for the emitted
/// <c>MqPublishRedis_Helpers.RedactCredentials</c> method.
/// </summary>
public sealed class MqPublishRedisRedactionTests
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
        typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    private static async Task<string> CallRedactAsync(string connStr, string message)
    {
        var provider = new MqPublishRedisProvider();
        var model = new MqPublishRedisModel("cache", "orders", "hello");
        var fragment = provider.Emit(model, new StubCompileContext("redact-direct"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__result__\"] = MqPublishRedis_Helpers.RedactCredentials(" +
            "Vars[\"__conn__\"] as string ?? string.Empty, " +
            "Vars[\"__msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__conn__"] = connStr,
            ["__msg__"] = message,
        };
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);
        return Assert.IsType<string>(vars["__result__"]);
    }

    /// <summary>
    /// A literal connection string that appears verbatim in the message is fully
    /// replaced with <c>***</c>.
    /// </summary>
    [Fact]
    public async Task RedactCredentials_LiteralConnStringInMessage_IsStripped()
    {
        const string connStr = "cache-host:6379,password=s3cr3t,user=admin";
        const string message = "It was not possible to connect using cache-host:6379,password=s3cr3t,user=admin";

        var result = await CallRedactAsync(connStr, message);

        Assert.DoesNotContain("s3cr3t", result, StringComparison.Ordinal);
        Assert.DoesNotContain("password=s3cr3t", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>password=</c> token that appears in the message WITHOUT the full
    /// connection string present verbatim (e.g. a reformatted diagnostic string) is
    /// still redacted by the regex fallback.
    /// </summary>
    [Fact]
    public async Task RedactCredentials_PasswordTokenWithoutFullConnString_IsStripped()
    {
        const string connStr = "";
        const string message = "connection string rejected: password=hunter2;user=bob";

        var result = await CallRedactAsync(connStr, message);

        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", result, StringComparison.Ordinal);
        Assert.Contains("password=***", result, StringComparison.Ordinal);
        Assert.Contains("user=***", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A message without any credential material is returned unchanged (no false
    /// positives).
    /// </summary>
    [Fact]
    public async Task RedactCredentials_MessageWithoutCredentials_ReturnedUnchanged()
    {
        const string connStr = "cache-host:6379";
        const string message = "connection timed out after 5000ms";

        var result = await CallRedactAsync(connStr, message);

        Assert.Equal(message, result);
    }
}
