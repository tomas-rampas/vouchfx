// §17 credential-redaction regression tests for MqExpectNatsProvider (non-docker).
//
// Directly exercises the emitted MqExpectNats_Helpers.RedactNatsUrl method by
// compiling a throwaway CSX body that invokes it with a CRAFTED message that
// intentionally contains a NATS URI with credentials.  This proves the redaction logic
// actually strips the userinfo, rather than relying on NATS.Net never emitting credentials
// in its exception messages (which is version-dependent).
//
// Background: NatsException.Message in current NATS.Net may not echo the URI verbatim,
// so a live-connection test that checks DoesNotContain("s3cr3t", observation) is
// vacuously green — delete the RedactNatsUrl call and the test still passes.
// These tests guard against that regression: they supply a message KNOWN to contain
// the credentials and verify the output has been redacted.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.MqExpect.Nats;
using Xunit;

namespace Platform.Steps.MqExpect.Nats.Tests;

/// <summary>
/// Non-docker credential-redaction regression tests for the emitted
/// <c>MqExpectNats_Helpers.RedactNatsUrl</c> method.
/// </summary>
public sealed class MqExpectNatsRedactionTests
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

    // Both NATS assemblies plus JsonPath.Net (used by MatchesPayload in the emitted
    // MqExpectNats_Helpers class) must be in the reference list so the full helper
    // compiles.  CultureInfo is included because the helper uses InvariantCulture.
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(NATS.Client.Core.NatsConnection).Assembly.Location,
        typeof(NATS.Client.JetStream.NatsJSContext).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
    };

    private static MqExpectNatsModel GetModel(
        string target = "nats-bus",
        string subject = "orders.created",
        string? payloadContains = "hello") =>
        new MqExpectNatsModel(
            target,
            subject,
            null,
            new NatsMatch(payloadContains, null));

    // ── Helper: compile + run a CSX that calls RedactNatsUrl ─────────────────────

    private static async Task<string> CompileAndRedactAsync(
        string natsUrl, string craftedMessage, string stepId)
    {
        var provider = new MqExpectNatsProvider();
        var fragment = provider.Emit(GetModel(), new StubCompileContext(stepId));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__redact_result__\"] = MqExpectNats_Helpers.RedactNatsUrl(" +
            "Vars[\"__nats_url__\"] as string ?? string.Empty, " +
            "Vars[\"__crafted_msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(
            csx,
            additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__nats_url__"] = natsUrl,
            ["__crafted_msg__"] = craftedMessage,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        Assert.True(vars.ContainsKey("__redact_result__"),
            "Expected '__redact_result__' in Vars after calling RedactNatsUrl.");
        return Assert.IsType<string>(vars["__redact_result__"]);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>RedactNatsUrl</c> must strip the userinfo segment from a NATS URI that
    /// appears verbatim in the crafted message (step (a) — literal URI replacement).
    /// </summary>
    [Fact]
    public async Task RedactNatsUrl_WithCraftedMessageContainingSecret_SecretIsStripped()
    {
        const string natsUrl = "nats://user:s3cr3t@nats.example.com:4222";
        const string craftedMessage =
            "connection failed: nats://user:s3cr3t@nats.example.com:4222";

        var result = await CompileAndRedactAsync(natsUrl, craftedMessage, "redact-expect-direct");

        Assert.DoesNotContain("s3cr3t", result, StringComparison.Ordinal);
        Assert.DoesNotContain("user:s3cr3t", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A TLS-scheme NATS URI (<c>tls://user:pass@host:4222</c>) is also redacted —
    /// the regex fallback (step (c)) matches both <c>nats://</c> and <c>tls://</c>.
    /// </summary>
    [Fact]
    public async Task RedactNatsUrl_TlsScheme_SecretIsStripped()
    {
        const string natsUrl = "tls://user:s3cr3t@nats.example.com:4222";
        const string craftedMessage =
            "TLS handshake failed: tls://user:s3cr3t@nats.example.com:4222";

        var result = await CompileAndRedactAsync(natsUrl, craftedMessage, "redact-expect-tls");

        Assert.DoesNotContain("s3cr3t", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A password-only userinfo (<c>nats://:pass@host</c>) is redacted —
    /// step (b) strips the entire userinfo string, including the empty username.
    /// </summary>
    [Fact]
    public async Task RedactNatsUrl_PasswordOnly_SecretIsStripped()
    {
        const string natsUrl = "nats://:s3cr3t@nats.example.com:4222";
        const string craftedMessage =
            "auth failure: nats://:s3cr3t@nats.example.com:4222";

        var result = await CompileAndRedactAsync(natsUrl, craftedMessage, "redact-expect-passonly");

        Assert.DoesNotContain("s3cr3t", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NATS URL with no credentials in a message that does not echo the URL
    /// is returned unchanged (no false positives).
    /// </summary>
    [Fact]
    public async Task RedactNatsUrl_NoCredentials_MessageReturnedUnchanged()
    {
        const string natsUrl = "nats://nats.example.com:4222";
        const string craftedMessage = "FetchAsync returned 0 messages after 1s";

        var result = await CompileAndRedactAsync(natsUrl, craftedMessage, "redact-expect-nocreds");

        Assert.Equal(craftedMessage, result);
    }
}
