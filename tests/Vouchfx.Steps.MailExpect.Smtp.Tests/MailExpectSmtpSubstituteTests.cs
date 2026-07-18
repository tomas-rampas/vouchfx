// Tests for MailExpectSmtpProvider — {placeholder} + ${secret} substitution wiring (FIX 1).
//
// The match criteria (to / subject-contains / body-contains) are emitted as RAW template
// literals and resolved at EXECUTION time inside the helper's guarded region via
// Secret_Helpers.ResolveTemplate (single pass: both {placeholder} substitution AND
// ${secret:source/path} resolution, §17).  No secret value is ever baked into the IL.
//
// Covers:
//   1. Emit: each match field is passed through Secret_Helpers.ResolveTemplate, the raw
//      token text is baked (NOT pre-resolved at the call site), and the call passes Secrets.
//   2. Emit: RequiredHelpers includes the shared Substitute_Helpers and Secret_Helpers.
//   3. Compile-and-run (no docker): a {placeholder} in 'to' resolves from Vars and matches.
//   4. Compile-and-run (no docker): a missing ${secret:env/…} in 'to' yields EnvironmentError
//      via secret resolution, with a REFERENCE-ONLY observation (source/path, never a value).
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MailExpect.Smtp;
using Xunit;

namespace Vouchfx.Steps.MailExpect.Smtp.Tests;

/// <summary>
/// Non-docker tests proving <see cref="MailExpectSmtpProvider.Emit"/> wires the match
/// criteria through <c>Secret_Helpers.ResolveTemplate</c> so <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens resolve at step-execution time, not compile time.
/// </summary>
public sealed class MailExpectSmtpSubstituteTests
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

    private readonly MailExpectSmtpProvider _provider = new();

    // ── 1. Each match field is resolved via ResolveTemplate; tokens baked verbatim ──

    /// <summary>
    /// The emitted helper resolves every match template (to / subjectContains /
    /// bodyContains) through <c>Secret_Helpers.ResolveTemplate(secrets, vars, …)</c>, the
    /// raw token text is baked verbatim into the call site (NOT resolved at emit time), and
    /// the call site passes the <c>Secrets</c> global into <c>ExpectAsync</c>.
    /// </summary>
    [Fact]
    public void Emit_MatchFields_ResolvedViaResolveTemplateAtRuntime()
    {
        // Each criterion carries a token — proving the value is passed as a RAW template.
        var model = MakeModel("mp", new MailMatch(
            To: "{recipient}",
            SubjectContains: "${secret:env/SUBJECT}",
            BodyContains: "{bodyToken}"));
        var ctx = new StubCompileContext("wire");

        var fragment = _provider.Emit(model, ctx);
        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        // The helper resolves each match template via Secret_Helpers.ResolveTemplate.
        Assert.Contains(
            "Secret_Helpers.ResolveTemplate(secrets, vars, toTemplate)",
            helperSource, StringComparison.Ordinal);
        Assert.Contains(
            "Secret_Helpers.ResolveTemplate(secrets, vars, subjectContainsTemplate)",
            helperSource, StringComparison.Ordinal);
        Assert.Contains(
            "Secret_Helpers.ResolveTemplate(secrets, vars, bodyContainsTemplate)",
            helperSource, StringComparison.Ordinal);

        // The raw token text is baked verbatim into the emitted call site (NOT expanded here).
        Assert.Contains("{recipient}", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("${secret:env/SUBJECT}", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.Contains("{bodyToken}", fragment.StatementBlock, StringComparison.Ordinal);

        // The call site passes the Secrets global into ExpectAsync.
        Assert.Contains("Secrets,", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 2. RequiredHelpers includes shared substitution + secret helper sources ────

    /// <summary>
    /// <see cref="CsxFragment.RequiredHelpers"/> must include the byte-identical
    /// <c>Substitute_Helpers</c> and <c>Secret_Helpers</c> sources so the emitted CSX can
    /// resolve <c>{placeholder}</c> and <c>${secret:source/path}</c> at runtime.
    /// </summary>
    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var model = MakeModel("mp", new MailMatch(To: "a@b.com"));
        var ctx = new StubCompileContext("s");

        var fragment = _provider.Emit(model, ctx);

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 3. Compile-and-run: {placeholder} in 'to' resolves from Vars → Pass ────────

    /// <summary>
    /// A <c>{placeholder}</c> in the <c>to</c> criterion is resolved from <c>Vars</c> at
    /// runtime, so a Mailpit message addressed to the resolved recipient matches and the
    /// step passes — proving the substitution happens at execution time, not compile time.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_PlaceholderInTo_ResolvesFromVars_ReturnsPass()
    {
        const string stepId = "expect-placeholder";
        const string target = "mailpit-ph";

        var model = MakeModel(target, new MailMatch(To: "{recipient}"));
        var ctx = new StubCompileContext(stepId);
        var (csx, refs) = BuildCsx(model, ctx);
        var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

        var messagesJson = """
            {
              "messages": [
                {
                  "ID": "m1",
                  "To": [{"Name": "Alice", "Address": "alice@example.com"}],
                  "Subject": "Hi",
                  "Snippet": ""
                }
              ]
            }
            """;

        using var listener = StartMockMailpit(out var baseUrl, messagesJson);

        // {recipient} resolves to the address the mock message is sent to.
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(target)] = baseUrl,
            ["recipient"] = "alice@example.com",
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);
        Assert.Equal(Verdict.Pass, outcome.Verdict);
        Assert.Contains("true", outcome.Observation!, StringComparison.Ordinal);
    }

    // ── 4. Compile-and-run: missing ${secret:env/…} in 'to' → EnvironmentError ─────

    /// <summary>
    /// When the <c>to</c> criterion carries a <c>${secret:env/…}</c> reference whose
    /// environment variable is unset, the emitted helper resolves it INSIDE its guarded
    /// region BEFORE any Mailpit call, so the missing secret throws
    /// <see cref="SecretResolutionException"/> and maps to
    /// <see cref="Verdict.EnvironmentError"/> — with no server contacted.  The observation
    /// is REFERENCE-ONLY (§17): the <c>secretError</c> marker plus the <c>env</c> source and
    /// the variable-name path, and never the (non-existent) secret value.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInTo_ReturnsEnvironmentError_ReferenceOnly()
    {
        // Unique env name so the variable is guaranteed absent and cannot be raced.
        var envName = "VOUCHFX_MAILEXPECT_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "expect-secret";
            const string target = "mailpit-secret";

            var model = MakeModel(target, new MailMatch(To: $"${{secret:env/{envName}}}"));
            var ctx = new StubCompileContext(stepId);
            var (csx, refs) = BuildCsx(model, ctx);
            var compiled = RoslynScriptCompiler.CompileOnce(csx, additionalReferencePaths: refs);

            // Stage a baseUrl so the helper enters its else branch and reaches secret
            // resolution.  No server is contacted: resolution throws BEFORE the HTTP call.
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Connection(target)] = "http://127.0.0.1:1",
            };

            // A REAL env-backed accessor — the same resolver the runner builds.  Because
            // envName is unset, env/<NAME> genuinely cannot resolve, so ResolveTemplate
            // throws SecretResolutionException inside the guarded region.
            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            // Must NOT throw — the exception is contained inside the step's guarded region.
            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.NotNull(outcome.Observation);

            // REFERENCE-ONLY contract (§17): names the secret-error marker, the 'env' source,
            // and the variable-name path — never a secret value (none exists on failure).
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("env", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
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

        // Splice via CsxAssembler (not a manual join) — it declares the per-step
        // __stepCt_<safeId> / __stepBudgetGoverned_<safeId> locals the emitted call
        // site now references (§4 common step fields, issue #232).
        var assembled = CsxAssembler.Assemble(new[] { (ctx.StepId, fragment) });

        // System.Net.Http + System.Text.Json + System.Private.Uri are not in the TPA-only
        // Roslyn reference set; the shared Secret_/Substitute_ helpers resolve against the
        // base TPA references (System.Text.RegularExpressions) + Vouchfx.Engine.Abstractions.
        var refs = ((ICompileReferenceContributor)provider)
            .CompileReferenceAssemblies
            .Select(a => a.Location)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();
        return (assembled.CsxSource, refs);
    }

    /// <summary>
    /// Starts a minimal <see cref="HttpListener"/> on a random port that returns
    /// <paramref name="messagesJson"/> for any GET /api/v1/messages request and a stub for
    /// /api/v1/message/* detail requests.
    /// </summary>
    private static DisposableAction StartMockMailpit(out string baseUrl, string messagesJson)
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

                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
                    string responseBody;

                    if (path.StartsWith("/api/v1/messages", StringComparison.Ordinal))
                    {
                        responseBody = messagesJson;
                    }
                    else if (path.StartsWith("/api/v1/message/", StringComparison.Ordinal))
                    {
                        responseBody = """{"ID":"abc","Text":"detail body","HTML":""}""";
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
                catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
                {
                    // Dispose() (below) cancels cts BEFORE calling listener.Stop()/Close(), but
                    // that teardown can still race a response this thread is actively writing:
                    // the client may already have every byte it needs while this thread sits
                    // between Write and Close (or inside either call) when the listener disposes
                    // the response's underlying resources out from under it. That surfaces here
                    // as ObjectDisposedException/HttpListenerException — expected teardown, not a
                    // handler fault — and must be contained: this loop runs on a raw background
                    // Thread, where an unhandled exception aborts the whole test host, not just
                    // this test. (Mirrors the identical fix in
                    // MailExpectSmtpEmitTests.StartMockMailpit.)
                }
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
