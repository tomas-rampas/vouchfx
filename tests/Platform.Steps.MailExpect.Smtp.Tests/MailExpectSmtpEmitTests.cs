// Tests for MailExpectSmtpProvider — CSX emitter + resource contributor.
//
// All tests in this file are non-docker.  They exercise:
//   1. Emit: StatementBlock begins and ends with a brace.
//   2. Emit: no 'using var' anywhere in the emitted fragment (§13.3.1 guard).
//   3. Emit: helper class is named 'MailExpectSmtp_Helpers' (§13.3.1 prefix rule).
//   4. Emit: a hyphenated step id is sanitised in the outcome key.
//   5. Emit: absent To / SubjectContains / BodyContains → bare 'null' literals.
//   6. Emit: special characters in match criteria are JSON-escaped.
//   7. Resources: yields exactly one 'mailpit' ResourceRequirement.
//   8. CompileReferenceAssemblies: contains System.Net.Http and System.Text.Json.
//   9. Full compile-and-run (no docker): EnvironmentError when the conn key is absent.
//  10. Full compile-and-run (no docker): Pass when Mailpit mock returns a matching message.
//  11. Full compile-and-run (no docker): Fail when Mailpit mock returns no matching message.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.MailExpect.Smtp;
using Xunit;

namespace Platform.Steps.MailExpect.Smtp.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MailExpectSmtpProvider"/>
/// covering the emitter (<see cref="IStepCompiler{TModel}"/>) and resource
/// contributor (<see cref="IResourceContributor{TModel}"/>).
/// </summary>
public sealed class MailExpectSmtpEmitTests
{
    // ── Stubs ─────────────────────────────────────────────────────────────────────

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

    // ── Shared provider instance ──────────────────────────────────────────────────

    private readonly MailExpectSmtpProvider _provider = new();

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var model = MakeModel("mp", new MailMatch(To: "a@b.com"));
        var ctx = new StubCompileContext("check-mail");

        var fragment = _provider.Emit(model, ctx);
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'),
            $"StatementBlock must start with '{{'; actual: '{block[..Math.Min(20, block.Length)]}'");
        Assert.True(block.EndsWith('}'),
            $"StatementBlock must end with '}}'; actual: '{block[Math.Max(0, block.Length - 20)..]}'");
    }

    // ── 2. No 'using var' ────────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var model = MakeModel("mp", new MailMatch(
            To: "a@b.com",
            SubjectContains: "Hi",
            BodyContains: "Hello"));
        var ctx = new StubCompileContext("my-step");

        var fragment = _provider.Emit(model, ctx);
        var fullSource = fragment.StatementBlock
            + "\n"
            + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsMailExpectSmtpPrefixedClass()
    {
        var model = MakeModel("mp", new MailMatch(To: "a@b.com"));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MailExpectSmtp_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step-id sanitisation ───────────────────────────────────────────────────

    [Fact]
    public void Emit_HyphenatedStepId_YieldsSanitisedOutcomeKey()
    {
        const string rawId = "check-mail-step";
        var model = MakeModel("mp", new MailMatch(SubjectContains: "hi"));
        var ctx = new StubCompileContext(rawId);

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains("__outcome::check_mail_step", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. Absent criteria → bare 'null' literals ─────────────────────────────────

    [Fact]
    public void Emit_AbsentCriteria_EmitsNullLiterals()
    {
        // Only To is set; SubjectContains and BodyContains are null.
        var model = MakeModel("mp", new MailMatch(To: "a@b.com"));
        var ctx = new StubCompileContext("k");

        var fragment = _provider.Emit(model, ctx);

        // SubjectContains and BodyContains appear as bare 'null' keyword.
        Assert.Contains("null,", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6. Special characters JSON-escaped ───────────────────────────────────────

    [Fact]
    public void Emit_SpecialCharactersInCriteria_AreJsonEscaped()
    {
        const string dangerous = "subject\"with\\special";
        var model = MakeModel("mp", new MailMatch(SubjectContains: dangerous));
        var ctx = new StubCompileContext("escape-test");

        var fragment = _provider.Emit(model, ctx);

        // The raw unescaped string must not appear verbatim — it would break the literal.
        Assert.DoesNotContain(dangerous, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 7. IResourceContributor yields mailpit ResourceRequirement ────────────────

    [Fact]
    public void Resources_YieldsSingleMailpitRequirementWithMatchingName()
    {
        var model = MakeModel("mymailpit", new MailMatch(To: "x@y.com"));

        var requirements = _provider.Resources(model).ToList();

        Assert.Single(requirements);
        var req = requirements[0];
        Assert.Equal("mailpit", req.Family, StringComparer.Ordinal);
        Assert.Equal("mymailpit", req.Name, StringComparer.Ordinal);
        Assert.Null(req.Image);
    }

    // ── 8. CompileReferenceAssemblies contains System.Net.Http + System.Text.Json ──

    /// <summary>
    /// The provider's <see cref="ICompileReferenceContributor.CompileReferenceAssemblies"/>
    /// must contain both <c>System.Net.Http</c> and <c>System.Text.Json</c> so the
    /// Roslyn compiler can resolve <c>HttpClient</c>, <c>JsonDocument</c>, and
    /// <c>JsonSerializer</c> in the emitted helper.
    /// </summary>
    [Fact]
    public void CompileReferenceAssemblies_ContainsHttpAndJsonAssemblies()
    {
        var contributor = (ICompileReferenceContributor)_provider;
        var names = contributor.CompileReferenceAssemblies
            .Select(a => a.GetName().Name)
            .ToList();

        Assert.Contains(names, n =>
            string.Equals(n, "System.Net.Http", StringComparison.Ordinal));
        Assert.Contains(names, n =>
            string.Equals(n, "System.Text.Json", StringComparison.Ordinal));
    }

    // ── 9. Compile round-trip: EnvironmentError when conn key absent ─────────────

    [Fact]
    public async Task Emit_CompileAndRun_AbsentConnKey_ReturnsEnvironmentError()
    {
        const string stepId = "check-mail";
        var model = MakeModel("missing-mp", new MailMatch(To: "a@b.com"));
        var ctx = new StubCompileContext(stepId);
        var (csx, refs) = BuildCsx(model, ctx);

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var safeId = CsxFragment.SanitiseId(stepId);
        var outcomeKey = VarKeys.Outcome(safeId);

        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain key '{outcomeKey}'. Actual: [{string.Join(", ", vars.Keys)}]");

        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0);
        Assert.NotNull(outcome.Observation);
    }

    // ── 9. Compile round-trip: Pass when mock returns matching message ────────────

    [Fact]
    public async Task Emit_CompileAndRun_MatchingMessage_ReturnsPass()
    {
        const string stepId = "expect-mail";
        const string target = "mailpit-test";

        var model = MakeModel(target, new MailMatch(To: "alice@example.com", SubjectContains: "Welcome"));
        var ctx = new StubCompileContext(stepId);
        var (csx, refs) = BuildCsx(model, ctx);

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

        // Mailpit API response: one message matching the criteria.
        var messagesJson = """
            {
              "total": 1,
              "messages": [
                {
                  "ID": "abc123",
                  "To": [{"Name": "Alice", "Address": "alice@example.com"}],
                  "Subject": "Welcome to vouchfx",
                  "Snippet": ""
                }
              ]
            }
            """;

        using var listener = StartMockMailpit(out var baseUrl, messagesJson);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(target)] = baseUrl,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("true", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 10. Compile round-trip: Fail when mock returns no matching message ────────

    [Fact]
    public async Task Emit_CompileAndRun_NoMatchingMessage_ReturnsFail()
    {
        const string stepId = "expect-mail-fail";
        const string target = "mailpit-fail";

        // Criterion: To "bob@example.com" — but mock returns mail to alice only.
        var model = MakeModel(target, new MailMatch(To: "bob@example.com"));
        var ctx = new StubCompileContext(stepId);
        var (csx, refs) = BuildCsx(model, ctx);

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

        var messagesJson = """
            {
              "total": 1,
              "messages": [
                {
                  "ID": "msg1",
                  "To": [{"Name": "Alice", "Address": "alice@example.com"}],
                  "Subject": "Hello",
                  "Snippet": ""
                }
              ]
            }
            """;

        using var listener = StartMockMailpit(out var baseUrl, messagesJson);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(target)] = baseUrl,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("false", outcome.Observation!, StringComparison.Ordinal);
        Assert.Contains("expected", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 12. At-least-one vs exact-count semantics ─────────────────────────────────

    /// <summary>
    /// When <see cref="MailExpectation.Count"/> is absent (null), the emitted
    /// <c>StatementBlock</c> must pass the literal <c>null</c> as the
    /// <c>expectedCount</c> argument (not the integer <c>1</c>), so the helper
    /// activates its "at least one" branch (<c>found &gt;= 1</c>).
    /// When <see cref="MailExpectation.Count"/> is explicitly set, the emitted
    /// block must pass the corresponding integer literal, activating the exact-match
    /// branch (<c>found == N</c>).
    /// </summary>
    [Fact]
    public void Emit_AbsentCount_EmitsNullExpectedCount_ExplicitCount_EmitsIntLiteral()
    {
        var modelNull = MakeModel("mp", new MailMatch(To: "a@b.com"), count: null);
        var modelExact = MakeModel("mp", new MailMatch(To: "a@b.com"), count: 3);
        var ctxNull = new StubCompileContext("at-least-one");
        var ctxExact = new StubCompileContext("exact-count");

        var fragNull = _provider.Emit(modelNull, ctxNull);
        var fragExact = _provider.Emit(modelExact, ctxExact);

        // Absent count → the last ExpectAsync argument must be the literal null,
        // not 1 (the old bug: defaulting null→1 bypassed the at-least-one path).
        Assert.Contains(
            "null);",
            fragNull.StatementBlock,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            ", 1);",
            fragNull.StatementBlock,
            StringComparison.Ordinal);

        // Explicit count=3 → integer literal 3.
        Assert.Contains(
            "3);",
            fragExact.StatementBlock,
            StringComparison.Ordinal);

        // The helper signature must declare the parameter as nullable to support
        // both the at-least-one and exact-match branches.
        var helperSource = string.Join("\n", fragNull.RequiredHelpers);
        Assert.Contains(
            "int? expectedCount",
            helperSource,
            StringComparison.Ordinal);
    }

    // ── 13. Dead endpoint in conn key → EnvironmentError ─────────────────────────

    /// <summary>
    /// When the conn key is present but points at a port with nothing listening,
    /// the emitted helper must write <see cref="Verdict.EnvironmentError"/>.
    /// The <c>HttpRequestException</c> (connection refused) is caught by the outer
    /// <c>catch (System.Exception)</c> handler and classified as EnvironmentError,
    /// not propagated further.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_DeadEndpointInConnKey_ReturnsEnvironmentError()
    {
        const string stepId = "mail-dead-endpoint";
        var model = MakeModel("mymail", new MailMatch(To: "user@example.com"));
        var ctx = new StubCompileContext(stepId);
        var (csx, refs) = BuildCsx(model, ctx);

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

        // Get a free port then deliberately do NOT start a server, so the OS
        // immediately refuses the connection.
        var deadPort = GetFreePort();
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("mymail")] = $"http://127.0.0.1:{deadPort}",
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain key '{outcomeKey}'. Actual: [{string.Join(", ", vars.Keys)}]");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── 14. Exact-count rejection (count: N, found != N) → Fail ─────────────────

    /// <summary>
    /// When <c>count: 1</c> is declared explicitly and zero messages are in the
    /// inbox, the emitted helper must write <see cref="Verdict.Fail"/>.
    /// This exercises the exact-count branch: <c>matched == expectedCount.Value</c>
    /// (distinct from the at-least-one branch <c>matched &gt;= 1</c> used when
    /// <c>count</c> is absent).
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_ExactCountMismatch_ReturnsFail()
    {
        const string stepId = "mail-exact-count";
        var model = MakeModel("mymail", new MailMatch(To: "user@example.com"), count: 1);
        var ctx = new StubCompileContext(stepId);
        var (csx, refs) = BuildCsx(model, ctx);

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

        // Mock returns an empty inbox — zero messages, so count 1 cannot be satisfied.
        const string emptyInboxJson = """{"messages":[]}""";
        using var listener = StartMockMailpit(out var baseUrl, emptyInboxJson);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("mymail")] = baseUrl,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain key '{outcomeKey}'. Actual: [{string.Join(", ", vars.Keys)}]");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("false", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 15. BodyContains fetches detail endpoint (per-candidate call) → Pass ──────

    /// <summary>
    /// When <c>body-contains</c> is declared, the emitted helper UNCONDITIONALLY issues a
    /// second HTTP call (<c>GET /api/v1/message/{ID}</c>) per candidate to fetch the full
    /// plain-text body — it does not inspect the list <c>Snippet</c>.  Confirms the
    /// detail-call path writes <see cref="Verdict.Pass"/> when the fetched body text matches.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_BodyContainsMatch_SecondCallPath_ReturnsPass()
    {
        const string stepId = "mail-body-contains";
        // The helper fetches the full body from the detail endpoint unconditionally for
        // body-contains; the list Snippet is never consulted.
        var model = MakeModel("mymail", new MailMatch(BodyContains: "click here"));
        var ctx = new StubCompileContext(stepId);
        var (csx, refs) = BuildCsx(model, ctx);

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

        var listJson = """
            {
              "messages": [
                {
                  "ID": "msg-001",
                  "Subject": "Hello",
                  "Snippet": "short preview without the keyword",
                  "To": []
                }
              ]
            }
            """;
        const string detailJson = """{"Text":"Please click here to confirm your account."}""";

        using var listener = StartMockMailpitWithDetail(out var baseUrl, listJson, detailJson);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection("mymail")] = baseUrl,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain key '{outcomeKey}'. Actual: [{string.Join(", ", vars.Keys)}]");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
    }

    // ── 16. BodyContains + failing detail fetch → candidate skipped → Fail ───────

    /// <summary>
    /// FIX 3 regression: when <c>body-contains</c> is declared and the per-candidate
    /// detail fetch (<c>GET /api/v1/message/{ID}</c>) fails (HTTP 500), the emitted
    /// helper must treat THAT candidate as NON-matching (skip it) — never a terminal
    /// step <see cref="Verdict.EnvironmentError"/>.  Here the single candidate matches
    /// <c>To</c> and <c>subject-contains</c> from the list, but its body fetch 500s, so
    /// zero messages match.  With no explicit <c>count</c> the at-least-one expectation
    /// fails, yielding <see cref="Verdict.Fail"/> (NOT EnvironmentError, NOT Inconclusive).
    /// </summary>
    [Fact]
    public async Task Emit_BodyContains_DetailFetchFailure_TreatsAsCandidateNonMatching()
    {
        const string stepId = "mail-detail-fetch-500";
        const string target = "mailpit-detail-fail";

        // The candidate matches To + subject-contains from the list; body-contains forces a
        // per-candidate detail fetch, which the mock fails with 500.
        var model = MakeModel(target, new MailMatch(
            To: "user@example.com",
            SubjectContains: "Welcome",
            BodyContains: "expected"));
        var ctx = new StubCompileContext(stepId);
        var (csx, refs) = BuildCsx(model, ctx);

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

        // List: exactly one candidate matching To + Subject, id "msg-1".
        var listJson = """
            {
              "messages": [
                {
                  "ID": "msg-1",
                  "To": [{"Name": "User", "Address": "user@example.com"}],
                  "Subject": "Welcome aboard",
                  "Snippet": ""
                }
              ]
            }
            """;

        using var listener = StartMailpitWithFailingDetail(out var baseUrl, listJson);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(target)] = baseUrl,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain key '{outcomeKey}'. Actual: [{string.Join(", ", vars.Keys)}]");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

        // The candidate's detail fetch failed → treated as non-matching (skipped), so zero
        // messages matched → the at-least-one expectation fails → Fail.  Crucially NOT
        // EnvironmentError (a failed detail fetch is not terminal) and NOT Inconclusive.
        Assert.Equal(Verdict.Fail, outcome.Verdict);
        Assert.Contains("false", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 17. List call failure → EnvironmentError (outer path stays intact) ────────

    /// <summary>
    /// FIX 3 regression (counterpart): a failure of the LIST call
    /// (<c>GET /api/v1/messages?limit=100</c>, here HTTP 500) is TERMINAL — the
    /// <c>HttpRequestException</c> propagates to the helper's outer
    /// <c>catch (System.Exception)</c> handler and classifies the step as
    /// <see cref="Verdict.EnvironmentError"/>.  This proves the per-candidate skip logic
    /// did NOT swallow the outer environment-error path.
    /// </summary>
    [Fact]
    public async Task Emit_ListCallFailure_ReturnsEnvironmentError()
    {
        const string stepId = "mail-list-500";
        const string target = "mailpit-list-fail";

        var model = MakeModel(target, new MailMatch(To: "user@example.com"));
        var ctx = new StubCompileContext(stepId);
        var (csx, refs) = BuildCsx(model, ctx);

        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

        // The mock returns 500 for every request, so the list call itself fails.
        using var listener = StartMailpitAlways500(out var baseUrl);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(target)] = baseUrl,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Expected Vars to contain key '{outcomeKey}'. Actual: [{string.Join(", ", vars.Keys)}]");
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    private static MailExpectSmtpModel MakeModel(string target, MailMatch match, int? count = null)
        => new(
            Target: target,
            Expect: new MailExpectation(Match: match, Count: count));

    private static (string Csx, string[] AdditionalRefs) BuildCsx(
        MailExpectSmtpModel model, ICompileContext ctx)
    {
        var provider = new MailExpectSmtpProvider();
        var fragment = provider.Emit(model, ctx);
        var usings = string.Join("\n", fragment.RequiredUsings.Select(u => $"using {u};"));
        var helpers = string.Join("\n", fragment.RequiredHelpers);
        var csx = $"{usings}\n{helpers}\n{fragment.StatementBlock}";

        // Collect compile-reference assembly paths from ICompileReferenceContributor.
        // System.Net.Http + System.Text.Json are not in the TPA-only Roslyn reference set.
        var refs = ((ICompileReferenceContributor)provider)
            .CompileReferenceAssemblies
            .Select(a => a.Location)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();
        return (csx, refs);
    }

    /// <summary>
    /// Starts a minimal HttpListener on a random port that returns
    /// <paramref name="messagesJson"/> for any GET /api/v1/messages request
    /// and a stub for /api/v1/message/* detail requests.
    /// </summary>
    private static DisposableAction StartMockMailpit(out string baseUrl, string messagesJson)
    {
        // Find a free port.
        var listener = new HttpListener();
        var port = GetFreePort();
        baseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(baseUrl + "/");
        listener.Start();

        // Serve requests on a background thread.
        var cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }

                var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
                string responseBody;

                if (path.StartsWith("/api/v1/messages", StringComparison.Ordinal))
                {
                    responseBody = messagesJson;
                }
                else if (path.StartsWith("/api/v1/message/", StringComparison.Ordinal))
                {
                    // Detail endpoint — return minimal message with a text body.
                    responseBody = """{"ID":"abc","Text":"detail body","HTML":""}""";
                }
                else if (path == "/api/v1/info")
                {
                    responseBody = """{"Version":"v1.0.0"}""";
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    responseBody = "{}";
                }

                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = 200;
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
        })
        { IsBackground = true };
        thread.Start();

        return new DisposableAction(() =>
        {
            cts.Cancel();
            listener.Stop();
            listener.Close();
        });
    }

    /// <summary>
    /// Starts a minimal HttpListener on a random port that returns
    /// <paramref name="messagesJson"/> for GET /api/v1/messages requests and
    /// <paramref name="detailJson"/> for GET /api/v1/message/* detail requests.
    /// Use when the test needs to control both the inbox list and the detail response.
    /// </summary>
    private static DisposableAction StartMockMailpitWithDetail(
        out string baseUrl, string messagesJson, string detailJson)
    {
        var listener = new HttpListener();
        var port = GetFreePort();
        baseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(baseUrl + "/");
        listener.Start();

        var cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }

                var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
                string responseBody;

                if (path.StartsWith("/api/v1/messages", StringComparison.Ordinal))
                {
                    responseBody = messagesJson;
                }
                else if (path.StartsWith("/api/v1/message/", StringComparison.Ordinal))
                {
                    responseBody = detailJson;
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    responseBody = "{}";
                }

                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = 200;
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
        })
        { IsBackground = true };
        thread.Start();

        return new DisposableAction(() =>
        {
            cts.Cancel();
            listener.Stop();
            listener.Close();
        });
    }

    /// <summary>
    /// Starts a minimal HttpListener that returns a normal 200
    /// <paramref name="messagesJson"/> for the list call
    /// (<c>GET /api/v1/messages</c>) but 500 for every per-candidate detail request
    /// (<c>GET /api/v1/message/{ID}</c>).  Unlike <see cref="StartMockMailpit"/> the
    /// status code is set CONDITIONALLY (not overwritten to 200 at the end), so the
    /// 500 actually reaches the client — exercising the FIX 3 per-candidate skip path.
    /// </summary>
    private static DisposableAction StartMailpitWithFailingDetail(
        out string baseUrl, string messagesJson)
    {
        var listener = new HttpListener();
        var port = GetFreePort();
        baseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(baseUrl + "/");
        listener.Start();

        var cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }

                var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
                int statusCode;
                string responseBody;

                // Check the detail path FIRST: "/api/v1/message/…" is more specific than the
                // list path "/api/v1/messages" (no trailing slash), so the two never collide.
                if (path.StartsWith("/api/v1/message/", StringComparison.Ordinal))
                {
                    statusCode = 500;
                    responseBody = """{"error":"internal server error"}""";
                }
                else if (path.StartsWith("/api/v1/messages", StringComparison.Ordinal))
                {
                    statusCode = 200;
                    responseBody = messagesJson;
                }
                else
                {
                    statusCode = 404;
                    responseBody = "{}";
                }

                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = statusCode;
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
        })
        { IsBackground = true };
        thread.Start();

        return new DisposableAction(() =>
        {
            cts.Cancel();
            listener.Stop();
            listener.Close();
        });
    }

    /// <summary>
    /// Starts a minimal HttpListener that returns HTTP 500 for EVERY request, so the
    /// helper's list call (<c>GET /api/v1/messages?limit=100</c>) fails outright.  The
    /// status code is set unconditionally to 500 (never overwritten to 200), exercising
    /// the FIX 3 outer EnvironmentError path.
    /// </summary>
    private static DisposableAction StartMailpitAlways500(out string baseUrl)
    {
        var listener = new HttpListener();
        var port = GetFreePort();
        baseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(baseUrl + "/");
        listener.Start();

        var cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }

                const string responseBody = """{"error":"internal server error"}""";
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = 500;
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
        })
        { IsBackground = true };
        thread.Start();

        return new DisposableAction(() =>
        {
            cts.Cancel();
            listener.Stop();
            listener.Close();
        });
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class DisposableAction : IDisposable
    {
        private readonly Action _action;
        public DisposableAction(Action action) => _action = action;
        public void Dispose() => _action();
    }
}
