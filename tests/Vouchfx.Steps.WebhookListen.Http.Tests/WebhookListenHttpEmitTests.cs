// Tests for WebhookListenHttpProvider — CSX emitter + compile-and-run round-trip.
//
// All tests are non-docker AND require NO real listener: the compile round-trip runs the
// emitted CSX against a ScriptGlobalVariables built with a STUB IWebhookCaptureAccessor that
// returns pre-seeded CapturedWebhookRequests.  This proves the emitted CSX compiles, that the
// match logic is correct, and — critically — that the redaction contract holds (no captured
// body/header value ever reaches the observation, §17).
//
// Emit lint:
//   1. StatementBlock begins and ends with a brace.
//   2. No 'using var' anywhere (and no literal 'using var' token even in comments, §13.3.1).
//   3. Helper class is named 'WebhookListenHttp_Helpers' (§13.3.1 prefix rule).
//   4. Substitute_Helpers AND Secret_Helpers sources are present in RequiredHelpers.
//   5. A hyphenated step id is sanitised to the outcome key '__outcome::assert_cb'.
//   6. An absent method/path/bodyContains is emitted as a bare 'null' literal.
//
// Compile round-trip (no docker, no real listener — stub accessor):
//   7. A captured request matching the criteria → Pass.
//   8. No captured request / non-matching → Fail (NOT Inconclusive — the provider writes Fail;
//      the runner's RETRY conversion is not exercised here).
//   9. A SUT-echoed secret in a captured body → assert the observation does NOT contain the
//      secret value (redaction): matched=Pass case AND a non-matching case.
//  10. Secret path: an expected value with ${secret:env/MISSING} + a real env SecretAccessor
//      → EnvironmentError, reference-only (source/path, never the value).
using System;
using System.Collections.Generic;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Webhooks;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;

namespace Vouchfx.Steps.WebhookListen.Http.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="WebhookListenHttpProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>) and a full compile-and-run
/// round-trip against a stub <see cref="IWebhookCaptureAccessor"/> (no real listener).
/// </summary>
public sealed class WebhookListenHttpEmitTests
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

    /// <summary>
    /// A tiny in-test <see cref="IWebhookCaptureAccessor"/> that returns a pre-seeded list of
    /// captured requests for one listener name (and an empty list for any other name).  This
    /// lets the compile round-trip exercise the emitted match + redaction logic with NO real
    /// host listener and NO Docker.
    /// </summary>
    private sealed class StubWebhookAccessor : IWebhookCaptureAccessor
    {
        private static readonly IReadOnlyList<CapturedWebhookRequest> Empty =
            Array.Empty<CapturedWebhookRequest>();

        private readonly string _listenerName;
        private readonly IReadOnlyList<CapturedWebhookRequest> _captured;

        public StubWebhookAccessor(string listenerName, IReadOnlyList<CapturedWebhookRequest> captured)
        {
            _listenerName = listenerName;
            _captured = captured;
        }

        public IReadOnlyList<CapturedWebhookRequest> GetCaptured(string listenerVarName) =>
            string.Equals(listenerVarName, _listenerName, StringComparison.Ordinal)
                ? _captured
                : Empty;
    }

    // ── Shared provider instance ──────────────────────────────────────────────────

    private readonly WebhookListenHttpProvider _provider = new();

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(
            MakeModel("cb", new WebhookMatch("POST", "/x", null, null)),
            new StubCompileContext("assert-cb"));

        var block = fragment.StatementBlock.Trim();
        Assert.StartsWith("{", block, StringComparison.Ordinal);
        Assert.EndsWith("}", block, StringComparison.Ordinal);
    }

    // ── 2. No 'using var' (including in comments) ─────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["h"] = "v" };
        var fragment = _provider.Emit(
            MakeModel("cb", new WebhookMatch("POST", "/x", headers, "body")),
            new StubCompileContext("my-step"));

        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsWebhookListenHttpPrefixedClass()
    {
        var fragment = _provider.Emit(
            MakeModel("cb", new WebhookMatch("POST", null, null, null)),
            new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("WebhookListenHttp_Helpers", StringComparison.Ordinal));
    }

    // ── 4. RequiredHelpers includes shared substitution + secret helpers ─────────

    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var fragment = _provider.Emit(
            MakeModel("cb", new WebhookMatch("POST", null, null, null)),
            new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 5. Step-id sanitisation ──────────────────────────────────────────────────

    [Fact]
    public void Emit_HyphenatedStepId_YieldsSanitisedOutcomeKey()
    {
        const string rawId = "assert-cb";
        var fragment = _provider.Emit(
            MakeModel("cb", new WebhookMatch("POST", null, null, null)),
            new StubCompileContext(rawId));

        Assert.Contains("__outcome::assert_cb", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. Absent criteria → bare 'null' literal ─────────────────────────────────

    [Fact]
    public void Emit_AbsentCriteria_EmitsNullLiterals()
    {
        // Only bodyContains is set; method/path are null and must appear as bare 'null,'.
        var fragment = _provider.Emit(
            MakeModel("cb", new WebhookMatch(null, null, null, "hello")),
            new StubCompileContext("k"));

        Assert.Contains("null,", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 7. Compile round-trip: matching captured request → Pass ──────────────────

    [Fact]
    public async Task Emit_CompileAndRun_MatchingCapturedRequest_ReturnsPass()
    {
        const string stepId = "assert-cb";
        const string listener = "orders-callback";
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["x-token"] = "abc" };
        var model = MakeModel(listener,
            new WebhookMatch(Method: "POST", Path: "/callbacks/order", Headers: headers, BodyContains: "NEW"));

        var captured = new[]
        {
            MakeRequest("GET", "/unrelated", body: "nope"),
            MakeRequest("POST", "/callbacks/order",
                headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Token"] = "abc" },
                body: "{\"status\":\"NEW\"}"),
        };

        var outcome = await RunAsync(model, stepId, listener, captured);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("\"matched\":true", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"scanned\":2", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 8. Compile round-trip: no match → Fail (NOT Inconclusive) ────────────────

    [Fact]
    public async Task Emit_CompileAndRun_NoCapturedRequest_ReturnsFail()
    {
        const string stepId = "assert-cb";
        const string listener = "orders-callback";
        var model = MakeModel(listener,
            new WebhookMatch(Method: "POST", Path: "/callbacks/order", Headers: null, BodyContains: null));

        // The listener captured nothing yet.
        var outcome = await RunAsync(model, stepId, listener, Array.Empty<CapturedWebhookRequest>());

        // The provider writes Fail directly — never Inconclusive (the RETRY runner, not this
        // helper, performs the Fail→Inconclusive conversion on timeout, §7/§12.1).
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("\"matched\":false", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"scanned\":0", outcome.Observation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_CompileAndRun_NonMatchingCapturedRequest_ReturnsFail_WithFailedCriteria()
    {
        const string stepId = "assert-cb";
        const string listener = "orders-callback";
        var model = MakeModel(listener,
            new WebhookMatch(Method: "POST", Path: "/callbacks/order", Headers: null, BodyContains: null));

        // A request whose PATH does not match the expected path.
        var captured = new[] { MakeRequest("POST", "/wrong-path", body: "x") };

        var outcome = await RunAsync(model, stepId, listener, captured);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("\"matched\":false", outcome.Observation!, StringComparison.Ordinal);
        // The failed-criteria list names which criterion failed BY NAME (path) — never a value.
        Assert.Contains("\"failed\":", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("path", outcome.Observation!, StringComparison.Ordinal);
        // The actual (wrong) captured path is NOT leaked into the observation.
        Assert.DoesNotContain("/wrong-path", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 9. Redaction: a SUT-echoed secret in a captured body never reaches the obs ─

    [Fact]
    public async Task Emit_CompileAndRun_SutEchoedSecretInBody_NotLeakedToObservation_OnMatch()
    {
        const string stepId = "assert-cb";
        const string listener = "orders-callback";
        const string secretValue = "S3CR3T-leaked-token-value";

        // The assertion matches on path only; the captured body happens to ECHO a secret value
        // the SUT received and wrote back into the webhook.  Captured data is untrusted and
        // outside SecretString redaction, so it must NEVER appear in the observation.
        var model = MakeModel(listener,
            new WebhookMatch(Method: null, Path: "/callbacks/order", Headers: null, BodyContains: null));

        var captured = new[]
        {
            MakeRequest("POST", "/callbacks/order",
                headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Echoed-Secret"] = secretValue,
                },
                body: "{\"token\":\"" + secretValue + "\"}"),
        };

        var outcome = await RunAsync(model, stepId, listener, captured);

        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        // The observation carries ONLY match metadata — never the echoed secret value.
        Assert.DoesNotContain(secretValue, outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"matched\":true", outcome.Observation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emit_CompileAndRun_SutEchoedSecretInBody_NotLeakedToObservation_OnMiss()
    {
        const string stepId = "assert-cb";
        const string listener = "orders-callback";
        const string secretValue = "S3CR3T-leaked-on-miss";

        // A request whose body echoes a secret but whose bodyContains criterion FAILS — the
        // miss path must also never write the captured body verbatim into the observation.
        var model = MakeModel(listener,
            new WebhookMatch(Method: null, Path: null, Headers: null, BodyContains: "EXPECTED-MISSING-SUBSTRING"));

        var captured = new[]
        {
            MakeRequest("POST", "/callbacks/order", body: "{\"token\":\"" + secretValue + "\"}"),
        };

        var outcome = await RunAsync(model, stepId, listener, captured);

        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.DoesNotContain(secretValue, outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("\"matched\":false", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("bodyContains", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 10. Secret path: missing ${secret:env/…} → EnvironmentError reference-only ─

    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInBodyContains_ReturnsEnvironmentError_ReferenceOnly()
    {
        var envName = "VOUCHFX_WEBHOOK_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "assert-secret";
            const string listener = "orders-callback";

            // The bodyContains criterion carries a missing secret reference; the helper resolves
            // every expected value BEFORE scanning, so the missing secret throws first — no
            // captured request needs to exist for this path to be reached.
            var model = MakeModel(listener,
                new WebhookMatch(
                    Method: null,
                    Path: null,
                    Headers: null,
                    BodyContains: $"${{secret:env/{envName}}}"));

            var csx = Assemble(model, stepId);
            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: AdditionalRefs());

            // A REAL env-backed accessor over the env source.  Because envName is unset,
            // env/<NAME> genuinely cannot resolve → SecretResolutionException in the guarded
            // region → EnvironmentError reference-only.
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor,
                new StubWebhookAccessor(listener, Array.Empty<CapturedWebhookRequest>()));

            // Must NOT throw — the exception is contained inside the step's guarded region.
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var outcome = ReadOutcome(vars, stepId);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.NotNull(outcome.Observation);
            // REFERENCE-ONLY (§17): the observation names the secret-error marker, the 'env'
            // source, and the variable-name path — never a secret value (none exists).
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("env", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── Placeholder substitution proof (no docker) ───────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_PlaceholderInPath_ResolvesAndMatches()
    {
        const string stepId = "assert-cb";
        const string listener = "orders-callback";

        // The expected path carries a {orderId} placeholder resolved from Vars at runtime.
        var model = MakeModel(listener,
            new WebhookMatch(Method: null, Path: "/callbacks/{orderId}", Headers: null, BodyContains: null));

        var captured = new[] { MakeRequest("POST", "/callbacks/42", body: "x") };

        var csx = Assemble(model, stepId);
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: AdditionalRefs());

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal) { ["orderId"] = "42" };
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecretAccessor.Instance,
            new StubWebhookAccessor(listener, captured));

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcome = ReadOutcome(vars, stepId);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    // ── Private harness ───────────────────────────────────────────────────────────

    private async Task<StepOutcome> RunAsync(
        WebhookListenHttpModel model,
        string stepId,
        string listener,
        IReadOnlyList<CapturedWebhookRequest> captured)
    {
        var csx = Assemble(model, stepId);
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: AdditionalRefs());

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(
            vars,
            new Dictionary<string, object>(StringComparer.Ordinal),
            NullSecretAccessor.Instance,
            new StubWebhookAccessor(listener, captured));

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        return ReadOutcome(vars, stepId);
    }

    private string Assemble(WebhookListenHttpModel model, string stepId)
    {
        var fragment = _provider.Emit(model, new StubCompileContext(stepId));
        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        return $"{usings}\n{helpers}\n{fragment.StatementBlock}";
    }

    private static StepOutcome ReadOutcome(Dictionary<string, object?> vars, string stepId)
    {
        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain outcome key '{outcomeKey}'. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");
        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    private static string[] AdditionalRefs() => new[]
    {
        // The emitted helper references System.Text.Json, System.Globalization, and (via the
        // shared helpers) System.Text.RegularExpressions.  None is loaded into the collectible ALC.
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
    };

    // ── Model / request factories ──────────────────────────────────────────────────

    private static WebhookListenHttpModel MakeModel(string listener, WebhookMatch match) =>
        new(Listener: listener, Match: match);

    private static CapturedWebhookRequest MakeRequest(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? headers = null,
        string body = "") =>
        new(
            Method: method,
            Path: path,
            Headers: headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Body: body,
            At: DateTimeOffset.UtcNow);
}
