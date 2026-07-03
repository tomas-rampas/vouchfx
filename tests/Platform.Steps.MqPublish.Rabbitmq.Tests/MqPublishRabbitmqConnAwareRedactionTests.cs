// §17 connection-string-aware credential-redaction tests for MqPublishRabbitmqProvider.
//
// FIX 1 regression tests — three cases the old regex-only approach could NOT handle:
//
//   (1) SetUri-colon-in-password: the URI parser echos "Bad user info in AMQP URI:
//       user:p:ass" — the message has NO amqp:// prefix so the old regex never fires.
//       Step (b) of the new implementation parses the URI, extracts "p:ass" as the
//       password portion, and replaces it.
//
//   (2) At-sign-in-password: the old non-greedy [^@]+@ regex stopped at the FIRST '@'
//       and redacted only the username, leaving the rest of the password (e.g. "ssw0rd"
//       from "p@ssw0rd") visible.  The new step (c) regex is greedy ([^/\s]*@) so it
//       advances to the LAST '@' before the host.
//
//   (3) End-to-end compile-and-run (wiring smoke test): staging amqp://user:s3cr3t@bad-host:1
//       (unreachable) returns Verdict.EnvironmentError — proves call-site plumbing only.
//       BrokerUnreachableException never echoes the URI or credentials, so
//       DoesNotContain("s3cr3t") would pass vacuously even without the RedactAmqpUri call.
//       Redaction correctness is covered by the crafted-message tests (1) and (2) above.
//   (4) Regex-fallback isolation: amqpUri="" disables steps (a)+(b); a message with '@'
//       in the password exercises only the greedy step-(c) regex.
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.MqPublish.Rabbitmq;
using Xunit;

namespace Platform.Steps.MqPublish.Rabbitmq.Tests;

/// <summary>
/// FIX 1 regression tests for connection-string-aware credential redaction in
/// <c>MqPublishRabbitmq_Helpers.RedactAmqpUri(string amqpUri, string message)</c>.
/// </summary>
public sealed class MqPublishRabbitmqConnAwareRedactionTests
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

    // ── Helper to emit + compile a CSX fragment that calls RedactAmqpUri ─────────

    private static async Task<string> CallRedactAsync(
        string amqpUri,
        string message)
    {
        var provider = new MqPublishRabbitmqProvider();
        var model = new MqPublishRabbitmqModel("rmq", null, "q", "hello", null);
        var fragment = provider.Emit(model, new StubCompileContext("redact-conn-aware"));

        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        const string scriptBody =
            "Vars[\"__result__\"] = MqPublishRabbitmq_Helpers.RedactAmqpUri(" +
            "Vars[\"__uri__\"] as string ?? string.Empty, " +
            "Vars[\"__msg__\"] as string ?? string.Empty);";
        var csx = $"{usings}\n{helpers}\n{scriptBody}";

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["__uri__"] = amqpUri,
            ["__msg__"] = message,
        };
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);
        return Assert.IsType<string>(vars["__result__"]);
    }

    // ── (1) SetUri colon-in-password ─────────────────────────────────────────────

    /// <summary>
    /// When the URI parser echoes the userinfo in the form "Bad user info in AMQP URI:
    /// user:p:ass" — which has NO amqp:// prefix — step (b) (parsed-userinfo replacement)
    /// must extract the password "p:ass" and redact it.
    /// </summary>
    [Fact]
    public async Task RedactAmqpUri_SetUriColonInPassword_PasswordIsFullyAbsent()
    {
        // amqp://user:p:ass@host:5672 — RabbitMQ.Client SetUri would throw:
        // "Bad user info in AMQP URI: user:p:ass" (no amqp:// prefix, so the old
        // regex-only approach leaves "p:ass" in the observation).
        const string amqpUri = "amqp://user:p:ass@host:5672";
        const string message = "Bad user info in AMQP URI: user:p:ass";

        var result = await CallRedactAsync(amqpUri, message);

        // The password fragment "p:ass" must be absent.
        Assert.DoesNotContain("p:ass", result, StringComparison.Ordinal);
        // The result must signal that redaction occurred.
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    // ── (2) At-sign in password ───────────────────────────────────────────────────

    /// <summary>
    /// When the password contains '@' (e.g. "p@ssw0rd"), the old non-greedy regex
    /// stopped at the first '@' and left "ssw0rd" visible.  The new 3-step approach
    /// fully redacts the password via step (a) (literal URI replacement) or step (c)
    /// (greedy regex fallback), so "p@ssw0rd" is FULLY absent.
    /// </summary>
    [Fact]
    public async Task RedactAmqpUri_AtSignInPassword_PasswordIsFullyAbsent()
    {
        // The password "p@ssw0rd" embeds an '@'.  The full URI appears in the message.
        const string amqpUri = "amqp://alice:p@ssw0rd@host";
        const string message = "connection failed: amqp://alice:p@ssw0rd@host";

        var result = await CallRedactAsync(amqpUri, message);

        // The full password must be absent — not just the prefix before '@'.
        Assert.DoesNotContain("p@ssw0rd", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ssw0rd", result, StringComparison.Ordinal);
        Assert.Contains("***", result, StringComparison.Ordinal);
    }

    // ── (3) End-to-end compile-and-run: s3cr3t absent from Observation ────────────

    /// <summary>
    /// Wiring smoke test: staging <c>amqp://user:s3cr3t@bad-host:1</c> (unreachable)
    /// must return <see cref="Verdict.EnvironmentError"/>, confirming the call-site
    /// plumbing is correct. <c>BrokerUnreachableException</c> never echoes the URI or
    /// credentials in its message, so <c>DoesNotContain("s3cr3t")</c> would pass
    /// vacuously even with the <c>RedactAmqpUri</c> call deleted. Redaction correctness
    /// is proved by the crafted-message tests (1)–(2) above and the regex-isolation
    /// test (4) below.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_CredentialedUnreachableConn_s3cr3tAbsentFromObservation()
    {
        const string amqpUri = "amqp://user:s3cr3t@bad-host:1";
        var model = new MqPublishRabbitmqModel("rmq", null, "q", "hello", null);
        var provider = new MqPublishRabbitmqProvider();
        var fragment = provider.Emit(model, new StubCompileContext("conn-aware-e2e"));

        var assembled = CsxAssembler.Assemble(new[] { ("conn-aware-e2e", fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource,
            additionalReferencePaths: s_additionalRefs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("rmq")] = amqpUri,
        };
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId("conn-aware-e2e"));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected outcome key '{outcomeKey}'. Keys: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

        // Connection must fail (no real broker at bad-host:1).  This is the sole
        // meaningful assertion: BrokerUnreachableException never echoes the URI or
        // credentials, so DoesNotContain("s3cr3t") would pass vacuously here.
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── (4) Regex-fallback isolation: '@'-in-password, empty URI, step (c) only ──

    /// <summary>
    /// Isolates step (c) of <c>RedactAmqpUri</c> by passing an empty <paramref name="amqpUri"/>
    /// so that step (a) is skipped (the <c>IsNullOrEmpty</c> guard) and <c>new Uri("")</c>
    /// throws in step (b), leaving only the greedy step-(c) regex to act on the message.
    /// <para>
    /// The message contains <c>amqp://alice:p@ssw0rd@host:5672/vhost</c> — a URI whose
    /// password embeds an '<c>@</c>'. Under the old non-greedy <c>[^@]+@</c> regex the
    /// match stops at the first '<c>@</c>' and the suffix <c>ssw0rd@host:5672/vhost</c>
    /// remains visible (this test FAILS). Under the new greedy <c>[^/\s]*@</c> regex the
    /// match advances past the interior '<c>@</c>' to the last one before the host, fully
    /// redacting the password (this test PASSES).
    /// </para>
    /// </summary>
    [Fact]
    public async Task RedactAmqpUri_RegexFallback_AtInPassword_IsFullyRedacted()
    {
        // amqpUri="" → step (a) skipped (IsNullOrEmpty guard in RedactAmqpUri).
        //           → new Uri("") throws UriFormatException → step (b) skipped.
        // Only the greedy step-(c) regex fires against the message.
        const string amqpUri = "";
        const string message = "connection failed: amqp://alice:p@ssw0rd@host:5672/vhost";

        var result = await CallRedactAsync(amqpUri, message);

        // Old [^@]+@  → matches "amqp://alice:p@"  → "ssw0rd@host:5672/vhost" still present → FAIL.
        // New [^/\s]*@ → matches "amqp://alice:p@ssw0rd@" → full password gone             → PASS.
        Assert.DoesNotContain("ssw0rd", result, StringComparison.Ordinal);
        Assert.Contains("amqp://***@", result, StringComparison.Ordinal);
    }
}
