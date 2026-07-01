// §17 credential-redaction regression tests for CacheAssertRedisProvider (non-docker).
//
// Directly exercises the emitted CacheAssertRedis_Helpers.RedactCredentials method by
// compiling a throwaway CSX body that invokes it with a CRAFTED message that intentionally
// contains the password.  This proves the redaction logic actually strips the secret,
// rather than relying on StackExchange.Redis never emitting credentials in its error
// message (which is version-dependent).  Both the literal connection-string replacement
// path and the password=/user= key-value pattern are exercised.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.CacheAssert.Redis;
using Xunit;

namespace Platform.Steps.CacheAssert.Redis.Tests;

/// <summary>
/// Non-docker credential-redaction regression tests for the emitted
/// <c>CacheAssertRedis_Helpers.RedactCredentials</c> method.
/// </summary>
public sealed class CacheAssertRedisRedactionTests
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

    /// <summary>
    /// <c>RedactCredentials</c> must strip a password that appears both inside the full
    /// connection string (literal replacement) and as a <c>password=</c> key-value pair.
    /// </summary>
    [Fact]
    public async Task RedactCredentials_WithCraftedMessageContainingSecret_SecretIsStripped()
    {
        const string connStr = "localhost:6379,password=sup3rsecret,user=default";
        // A message that DOES contain the password, simulating a driver that leaks it.
        const string craftedMessage =
            "connect failed for configuration 'localhost:6379,password=sup3rsecret,user=default'";

        var provider = new CacheAssertRedisProvider();
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));
        var fragment = provider.Emit(model, new StubCompileContext("redact-direct"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_result__\"] = CacheAssertRedis_Helpers.RedactCredentials(" +
            "Vars[\"__conn_str__\"] as string ?? string.Empty, " +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(
            csx,
            additionalReferencePaths: new[]
            {
                typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
                typeof(System.Text.Json.JsonSerializer).Assembly.Location,
                typeof(System.Globalization.CultureInfo).Assembly.Location,
                typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            });

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__conn_str__"] = connStr,
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.True(vars.ContainsKey("__redact_result__"),
            "Expected '__redact_result__' in Vars after calling RedactCredentials.");
        var result = Assert.IsType<string>(vars["__redact_result__"]);
        Assert.DoesNotContain("sup3rsecret", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>password=</c> token is redacted even when the full connection string is not
    /// present verbatim in the message (the regex path, not the literal-replacement path).
    /// </summary>
    [Fact]
    public async Task RedactCredentials_PasswordTokenOnly_IsStripped()
    {
        const string connStr = "not-the-message";
        const string craftedMessage = "auth error: password=sup3rsecret was rejected";

        var provider = new CacheAssertRedisProvider();
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));
        var fragment = provider.Emit(model, new StubCompileContext("redact-regex"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_result__\"] = CacheAssertRedis_Helpers.RedactCredentials(" +
            "Vars[\"__conn_str__\"] as string ?? string.Empty, " +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(
            csx,
            additionalReferencePaths: new[]
            {
                typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
                typeof(System.Text.Json.JsonSerializer).Assembly.Location,
                typeof(System.Globalization.CultureInfo).Assembly.Location,
                typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            });

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__conn_str__"] = connStr,
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var result = Assert.IsType<string>(vars["__redact_result__"]);
        Assert.DoesNotContain("sup3rsecret", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>password=</c> value that CONTAINS A SPACE is scrubbed in full; the regex
    /// value-class is whitespace-tolerant (<c>[^,;]+</c>) and stops only at a comma or
    /// semicolon delimiter, not at the first space inside the secret.
    /// </summary>
    [Fact]
    public async Task RedactCredentials_PasswordWithSpaces_IsFullyScrubbed()
    {
        const string connStr = "not-the-message";
        const string craftedMessage = "auth error: password=sup3r secret pw was rejected";

        var provider = new CacheAssertRedisProvider();
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));
        var fragment = provider.Emit(model, new StubCompileContext("redact-space"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_result__\"] = CacheAssertRedis_Helpers.RedactCredentials(" +
            "Vars[\"__conn_str__\"] as string ?? string.Empty, " +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(
            csx,
            additionalReferencePaths: new[]
            {
                typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
                typeof(System.Text.Json.JsonSerializer).Assembly.Location,
                typeof(System.Globalization.CultureInfo).Assembly.Location,
                typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            });

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__conn_str__"] = connStr,
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var result = Assert.IsType<string>(vars["__redact_result__"]);
        // The FULL secret — including the embedded space — must be absent from the output.
        Assert.DoesNotContain("sup3r secret pw", result, StringComparison.Ordinal);
        Assert.DoesNotContain("sup3r", result, StringComparison.Ordinal);
        Assert.Contains("password=***", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>user=</c> token is rewritten as <c>user=***</c>, not <c>password=***</c>:
    /// the two scrub patterns are targeted separately so the correct key label is preserved.
    /// </summary>
    [Fact]
    public async Task RedactCredentials_UserToken_RedactsWithCorrectLabel()
    {
        const string connStr = "not-the-message";
        const string craftedMessage = "auth error: user=alice was rejected";

        var provider = new CacheAssertRedisProvider();
        var model = new CacheAssertRedisModel(
            Target: "cache",
            Key: "k",
            Operation: RedisOp.Get,
            Field: null,
            Expect: new RedisExpectation(Value: "v", Exists: null, Length: null));
        var fragment = provider.Emit(model, new StubCompileContext("redact-user"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_result__\"] = CacheAssertRedis_Helpers.RedactCredentials(" +
            "Vars[\"__conn_str__\"] as string ?? string.Empty, " +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(
            csx,
            additionalReferencePaths: new[]
            {
                typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly.Location,
                typeof(System.Text.Json.JsonSerializer).Assembly.Location,
                typeof(System.Globalization.CultureInfo).Assembly.Location,
                typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
            });

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__conn_str__"] = connStr,
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var result = Assert.IsType<string>(vars["__redact_result__"]);
        Assert.DoesNotContain("alice", result, StringComparison.Ordinal);
        // Must emit "user=***" — not "password=***" — so the key label is preserved.
        Assert.Contains("user=***", result, StringComparison.Ordinal);
        Assert.DoesNotContain("password=***", result, StringComparison.Ordinal);
    }
}
