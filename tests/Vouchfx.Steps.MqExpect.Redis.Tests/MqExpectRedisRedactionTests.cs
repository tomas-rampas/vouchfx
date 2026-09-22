// §17 credential-redaction regression tests for MqExpectRedisProvider (non-docker).
//
// Directly exercises the emitted MqExpectRedis_Helpers.RedactCredentials method by
// compiling a throwaway CSX body that invokes it with a CRAFTED message containing a
// Redis connection string's password=/user= tokens.  Mirrors
// MqPublishRedisRedactionTests exactly.
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MqExpect.Redis;
using Xunit;

namespace Vouchfx.Steps.MqExpect.Redis.Tests;

/// <summary>
/// Non-docker credential-redaction regression tests for the emitted
/// <c>MqExpectRedis_Helpers.RedactCredentials</c> method.
/// </summary>
public sealed class MqExpectRedisRedactionTests
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

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
    };

    private static async Task<string> CallRedactAsync(string connStr, string message)
    {
        var provider = new MqExpectRedisProvider();
        var model = new MqExpectRedisModel("cache", "orders", new RedisMatch("x", null));
        var fragment = provider.Emit(model, new StubCompileContext("redact-direct"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__result__\"] = MqExpectRedis_Helpers.RedactCredentials(" +
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
    /// A <c>password=</c> token that appears in the message WITHOUT the full connection
    /// string present verbatim is still redacted by the regex fallback.  The crafted message
    /// separates its two tokens with ',', the only delimiter StackExchange.Redis's parser
    /// knows; it used to use ';', which only worked because the value bound wrongly stopped
    /// there (#553) -- a ';' is now part of the password value, as the parser treats it.
    /// </summary>
    [Fact]
    public async Task RedactCredentials_PasswordTokenWithoutFullConnString_IsStripped()
    {
        const string connStr = "";
        const string message = "connection string rejected: password=hunter2,user=bob";

        var result = await CallRedactAsync(connStr, message);

        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("bob", result, StringComparison.Ordinal);
        Assert.Contains("password=***", result, StringComparison.Ordinal);
        Assert.Contains("user=***", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedactCredentials_MessageWithoutCredentials_ReturnedUnchanged()
    {
        const string connStr = "cache-host:6379";
        const string message = "connection timed out after 5000ms";

        var result = await CallRedactAsync(connStr, message);

        Assert.Equal(message, result);
    }

    /// <summary>
    /// A <c>password=</c> value CONTAINING A LITERAL ';' is redacted in full (#553).
    /// StackExchange.Redis's <c>ConfigurationOptions</c> parser delimits options on ','
    /// only, so ';' is not special to it and can legally appear inside a password value.
    /// The old value-class <c>[^,;]+</c> wrongly treated ';' as a second delimiter and
    /// stopped the match at the first one, leaving the remainder of the password visible.
    /// </summary>
    [Fact]
    public async Task RedactCredentials_PasswordContainingSemicolon_IsFullyScrubbed()
    {
        const string connStr = "not-the-message";
        const string message = "auth error: password=sup3r;secret;pw was rejected";

        var result = await CallRedactAsync(connStr, message);

        // The FULL secret, including the embedded semicolons, must be absent; the old
        // ';'-bounded regex left "secret;pw" exposed.
        // Boolean assertions with fixed diagnostics, never Assert.Contains/DoesNotContain on
        // `result`: xUnit prints the actual string on failure, which here would publish the
        // very password this test exists to keep out of a log.
        Assert.True(
            !result.Contains("sup3r;secret;pw", StringComparison.Ordinal),
            "result leaked the full password");
        Assert.True(
            !result.Contains("secret;pw", StringComparison.Ordinal),
            "result leaked the password's remainder past the first ';'");
        Assert.True(
            !result.Contains("sup3r", StringComparison.Ordinal),
            "result leaked the password's first segment");
        Assert.True(
            result.Contains("password=***", StringComparison.Ordinal),
            "result does not carry the redaction marker");
    }
}
