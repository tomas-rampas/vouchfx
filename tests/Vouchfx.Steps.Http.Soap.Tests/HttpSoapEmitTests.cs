// Tests for HttpSoapProvider — CSX emitter and full compile-and-run round-trips (non-docker; a
// real SOAP response is served by an in-process System.Net.HttpListener so the full
// POST → parse → fault/status/xpath → capture path is exercised without any container).
// Mirrors MetricsAssertPrometheusEmitTests.cs's local-HttpListener harness pattern.
//
// Covers:
//   1-5.  Emit lint: braces, no 'using var', helper class prefix, Secret_Helpers included,
//         step-id sanitisation.
//   6-8.  Validate: target/path/envelope required; SSRF path guard; envelope well-formedness
//         (a malformed envelope template is rejected at compile time, §"ENVELOPE VALIDATION
//         STRICTNESS").
//   9.    Full round-trip: success envelope, default expect (status 200, no fault) → Pass.
//  10.    Full round-trip: expect.fault:true + status:500 + a real Fault envelope → Pass.
//  11.    Full round-trip: an UNEXPECTED Fault (fault:false default) → Fail, faultcode present,
//         faultstring content NEVER leaked into the observation (§17).
//  12.    Full round-trip: expect.fault:true but the response has NO Fault → Fail
//         (expectedFaultMissing).
//  13.    Full round-trip: status mismatch → Fail.
//  14.    Full round-trip: xpath assertion match → Pass; mismatch → Fail.
//  15.    Full round-trip: xpath capture → Pass + captured Vars.
//  16.    Full round-trip: SOAPAction header sent quoted per SOAP 1.1 convention.
//  17.    Full round-trip: connection refused → EnvironmentError.
//  18.    Full round-trip: missing ${secret:env/…} in envelope → EnvironmentError,
//         reference-only.
//  19.    A JSONPath-format (bare) capture entry against a SOAP response is always unmet →
//         Inconclusive (http.soap supports XPath captures only).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.Http.Soap;
using Xunit;

namespace Vouchfx.Steps.Http.Soap.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="HttpSoapProvider"/>.
/// </summary>
public sealed class HttpSoapEmitTests
{
    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(string stepId, IReadOnlyDictionary<string, CaptureExpr>? captures = null)
        {
            StepId = stepId;
            CaptureExprs = captures ?? new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
        }

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    private readonly HttpSoapProvider _provider = new();

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Xml.XmlDocument).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
    };

    private const string SuccessEnvelope = """
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <GetUserResponse xmlns="http://example.com/users">
              <Name>Ada Lovelace</Name>
              <Status>OK</Status>
            </GetUserResponse>
          </soap:Body>
        </soap:Envelope>
        """;

    private const string FaultEnvelope = """
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
          <soap:Body>
            <soap:Fault>
              <faultcode>soap:Server</faultcode>
              <faultstring>SENSITIVE-INTERNAL-DETAIL</faultstring>
            </soap:Fault>
          </soap:Body>
        </soap:Envelope>
        """;

    private static HttpSoapModel MakeModel(
        string target = "sut",
        string path = "/soap",
        string? action = null,
        string envelope = "<a/>",
        SoapExpectation? expect = null) =>
        new(target, path, action, envelope, expect);

    /// <summary>
    /// A <see cref="StubProjectContext"/> declaring service <c>"sut"</c> — <see cref="MakeModel"/>'s
    /// default target (REQ-012: target reconciliation needs it declared as a service or
    /// dependency to pass).
    /// </summary>
    private static readonly StubProjectContext s_sutDeclaredContext = new(
        new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal)
        {
            ["sut"] = new DeclaredServiceInfo(new List<string> { "http" }),
        });

    // ── 1. StatementBlock braces ─────────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("soap-step"));
        var block = fragment.StatementBlock.Trim();

        Assert.StartsWith("{", block, StringComparison.Ordinal);
        Assert.EndsWith("}", block, StringComparison.Ordinal);
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("my-step"));
        var fullSource = fragment.StatementBlock + "\n" + string.Join("\n", fragment.RequiredHelpers);

        Assert.DoesNotContain("using var", fullSource, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsHttpSoapPrefixedClass()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h => h.Contains("HttpSoap_Helpers", StringComparison.Ordinal));
    }

    // ── 4. RequiredHelpers includes Secret_Helpers ────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_IncludesSecretHelperSource()
    {
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext("s"));

        Assert.Contains(fragment.RequiredHelpers, h => h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 5. Step-id sanitisation ──────────────────────────────────────────────────

    [Fact]
    public void Emit_HyphenatedStepId_YieldsSanitisedOutcomeKey()
    {
        const string rawId = "call-soap";
        var fragment = _provider.Emit(MakeModel(), new StubCompileContext(rawId));

        Assert.Contains("__outcome::call_soap", fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 6-8. Validate ─────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyTargetPathEnvelope_ReturnsErrors()
    {
        var result = _provider.Validate(
            new HttpSoapModel(string.Empty, string.Empty, null, string.Empty, null),
            s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("target", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("path", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("envelope", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AbsoluteUrlPath_ReturnsError()
    {
        var result = _provider.Validate(
            MakeModel(path: "http://evil.example/x"),
            s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("rooted relative path", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MalformedEnvelope_ReturnsError()
    {
        var result = _provider.Validate(
            MakeModel(envelope: "<unclosed><tag>"),
            s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("well-formed", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WellFormedEnvelopeWithPlaceholders_Succeeds()
    {
        // {placeholder} / ${secret:...} tokens inside text/attribute content do not affect
        // well-formedness — see the provider's "ENVELOPE VALIDATION STRICTNESS" note.
        var result = _provider.Validate(
            MakeModel(envelope: "<a><b>{userId}</b><c attr=\"${secret:env/X}\"/></a>"),
            s_sutDeclaredContext);

        Assert.True(result.IsValid);
    }

    // ── Target reconciliation (services-generalisation spec, REQ-012/EDGE-009) ──

    /// <summary>
    /// REQ-012/EDGE-009: a <c>target</c> naming neither a declared service nor a declared
    /// dependency fails validation, naming the unknown target and listing what IS declared.
    /// </summary>
    [Fact]
    public void Validate_TargetNamesNeitherServiceNorDependency_IsInvalid_ListsDeclaredSurfaces()
    {
        var result = _provider.Validate(
            MakeModel(target: "does-not-exist"),
            s_sutDeclaredContext);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("does-not-exist", StringComparison.Ordinal) &&
            e.Contains("sut", StringComparison.Ordinal));
    }

    /// <summary>
    /// M1 fix (fix round 2, PR #349 follow-up): a <c>target</c> naming a declared
    /// DEPENDENCY (never a service) must be rejected — mirrors HttpRestProvider's own
    /// identical fix and rationale. The message must name the dependency and list the
    /// services it CAN reach.
    /// </summary>
    [Fact]
    public void Validate_TargetNamesDeclaredDependency_IsInvalid_NamesDependencyAndListsServices()
    {
        var ctx = new StubProjectContext(
            services: new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal)
            {
                ["sut"] = new DeclaredServiceInfo(new List<string> { "http" }),
            },
            dependencies: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["orders-db"] = "postgres",
            });

        var result = _provider.Validate(MakeModel(target: "orders-db"), ctx);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("orders-db", StringComparison.Ordinal) &&
            e.Contains("dependency", StringComparison.OrdinalIgnoreCase) &&
            e.Contains("sut", StringComparison.Ordinal));
    }

    // ── 9. Success envelope, default expect → Pass ───────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_SuccessEnvelope_DefaultExpect_ReturnsPass()
    {
        var (prefix, serveTask, listener) = StartOneShotServer(19601, SuccessEnvelope);
        try
        {
            var model = MakeModel(envelope: "<GetUser/>");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "soap-success", vars);
            await serveTask;

            Assert.Equal(Verdict.Pass, outcome.Verdict);
            Assert.Contains("\"status\":200", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 10. Expected Fault present → Pass ────────────────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_ExpectedFault_ReturnsPass()
    {
        var (prefix, serveTask, listener) = StartOneShotServer(19602, FaultEnvelope, statusCode: 500);
        try
        {
            var model = MakeModel(
                envelope: "<GetUser/>",
                expect: new SoapExpectation(Status: 500, Fault: true, Xpath: null));
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "soap-fault-expected", vars);
            await serveTask;

            Assert.Equal(Verdict.Pass, outcome.Verdict);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 11. Unexpected Fault → Fail, faultcode present, faultstring NEVER leaked ─

    [Fact]
    public async Task Emit_CompileAndRun_UnexpectedFault_ReturnsFail_WithFaultcodeOnly()
    {
        var (prefix, serveTask, listener) = StartOneShotServer(19603, FaultEnvelope, statusCode: 500);
        try
        {
            var model = MakeModel(envelope: "<GetUser/>"); // default expect: no fault expected
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "soap-fault-unexpected", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("unexpectedFault", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("soap:Server", outcome.Observation!, StringComparison.Ordinal);
            Assert.DoesNotContain("SENSITIVE-INTERNAL-DETAIL", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 12. Expected Fault absent → Fail ─────────────────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_ExpectedFaultMissing_ReturnsFail()
    {
        var (prefix, serveTask, listener) = StartOneShotServer(19604, SuccessEnvelope);
        try
        {
            var model = MakeModel(
                envelope: "<GetUser/>",
                expect: new SoapExpectation(Status: null, Fault: true, Xpath: null));
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "soap-fault-missing", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("expectedFaultMissing", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 13. Status mismatch → Fail ───────────────────────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_StatusMismatch_ReturnsFail()
    {
        var (prefix, serveTask, listener) = StartOneShotServer(19605, SuccessEnvelope, statusCode: 200);
        try
        {
            var model = MakeModel(
                envelope: "<GetUser/>",
                expect: new SoapExpectation(Status: 201, Fault: null, Xpath: null));
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "soap-status-mismatch", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("\"status\":200", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("\"expected\":201", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 14. XPath assertion match / mismatch ─────────────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_XPathAssertionMatch_ReturnsPass()
    {
        var (prefix, serveTask, listener) = StartOneShotServer(19606, SuccessEnvelope);
        try
        {
            var model = MakeModel(
                envelope: "<GetUser/>",
                expect: new SoapExpectation(
                    Status: null,
                    Fault: null,
                    Xpath: new[]
                    {
                        new SoapXPathAssertion(
                            "//*[local-name()='Body']/*[local-name()='GetUserResponse']/*[local-name()='Status']",
                            "OK"),
                    }));
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "soap-xpath-match", vars);
            await serveTask;

            Assert.Equal(Verdict.Pass, outcome.Verdict);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Emit_CompileAndRun_XPathAssertionMismatch_ReturnsFail()
    {
        var (prefix, serveTask, listener) = StartOneShotServer(19607, SuccessEnvelope);
        try
        {
            var model = MakeModel(
                envelope: "<GetUser/>",
                expect: new SoapExpectation(
                    Status: null,
                    Fault: null,
                    Xpath: new[]
                    {
                        new SoapXPathAssertion(
                            "//*[local-name()='Body']/*[local-name()='GetUserResponse']/*[local-name()='Status']",
                            "FAILED"),
                    }));
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "soap-xpath-mismatch", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("xpathMismatch", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 15. XPath capture ────────────────────────────────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_XPathCapture_ReturnsPass_AndCapturesValue()
    {
        var (prefix, serveTask, listener) = StartOneShotServer(19608, SuccessEnvelope);
        try
        {
            var model = MakeModel(envelope: "<GetUser/>");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };
            var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["userName"] = new CaptureExpr(
                    CaptureFormat.XPath,
                    "//*[local-name()='Body']/*[local-name()='GetUserResponse']/*[local-name()='Name']"),
            };

            var outcome = await RunStepAsync(model, "soap-capture", vars, captures);
            await serveTask;

            Assert.Equal(Verdict.Pass, outcome.Verdict);
            Assert.True(vars.TryGetValue("userName", out var captured));
            Assert.Equal("Ada Lovelace", captured);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 16. SOAPAction sent quoted ────────────────────────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_SoapActionHeader_SentQuoted()
    {
        string? observedSoapAction = null;
        var (prefix, serveTask, listener) = StartOneShotServer(
            19609, SuccessEnvelope, captureRequest: req => observedSoapAction = req.Headers["SOAPAction"]);
        try
        {
            var model = MakeModel(action: "http://example.com/GetUser", envelope: "<GetUser/>");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "soap-action", vars);
            await serveTask;

            Assert.Equal(Verdict.Pass, outcome.Verdict);
            Assert.Equal("\"http://example.com/GetUser\"", observedSoapAction);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 17. Connection refused → EnvironmentError ────────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_HostUnreachable_ReturnsEnvironmentError()
    {
        var model = MakeModel(envelope: "<GetUser/>");
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service("sut")] = "http://127.0.0.1:1/",
        };

        var outcome = await RunStepAsync(model, "soap-unreachable", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 18. Missing secret in envelope → EnvironmentError, reference-only ────────

    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInEnvelope_ReturnsEnvironmentError_ReferenceOnly()
    {
        var envName = "VOUCHFX_SOAP_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "soap-secret";
            var model = MakeModel(envelope: $"<GetUser>${{secret:env/{envName}}}</GetUser>");

            var fragment = _provider.Emit(model, new StubCompileContext(stepId));
            // Splice via CsxAssembler (not a manual join) — it declares the per-step
            // __stepCt_<safeId> / __stepBudgetGoverned_<safeId> locals the emitted call
            // site now references (§4 common step fields, issue #232).
            var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = "http://127.0.0.1:1/",
            };

            var accessor = new SecretAccessor(
                new SecretSourceCatalog(new ISecretResolver[] { new EnvironmentSecretResolver() }));
            var globals = new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                accessor);

            await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

            var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── 19. A bare (JSONPath-format) capture is always unmet → Inconclusive ─────

    [Fact]
    public async Task Emit_CompileAndRun_JsonPathFormatCapture_IsAlwaysUnmet_ReturnsInconclusive()
    {
        var (prefix, serveTask, listener) = StartOneShotServer(19610, SuccessEnvelope);
        try
        {
            var model = MakeModel(envelope: "<GetUser/>");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };
            var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["bad"] = new CaptureExpr(CaptureFormat.JsonPath, "$.name"),
            };

            var outcome = await RunStepAsync(model, "soap-bad-capture", vars, captures);
            await serveTask;

            Assert.Equal(Verdict.Inconclusive, outcome.Verdict);
            Assert.Contains("captureUnmet", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class StubProjectContext : IProjectContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

        internal StubProjectContext(
            IReadOnlyDictionary<string, DeclaredServiceInfo>? services = null,
            IReadOnlyDictionary<string, string>? dependencies = null)
        {
            DeclaredServices = services
                ?? new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);
            DeclaredDependencies = dependencies
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; }
    }

    /// <summary>
    /// Starts an in-process <see cref="HttpListener"/> bound to
    /// <c>http://127.0.0.1:&lt;port&gt;/</c> that serves <paramref name="body"/> exactly once
    /// with the given <paramref name="statusCode"/>, optionally invoking
    /// <paramref name="captureRequest"/> with the incoming request before responding.
    /// </summary>
    private static (string Prefix, Task ServeTask, HttpListener Listener) StartOneShotServer(
        int port, string body, int statusCode = 200, Action<HttpListenerRequest>? captureRequest = null)
    {
        var prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serveTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            captureRequest?.Invoke(ctx.Request);
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "text/xml; charset=utf-8";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.OutputStream.Close();
        });

        return (prefix, serveTask, listener);
    }

    private async Task<StepOutcome> RunStepAsync(
        HttpSoapModel model,
        string stepId,
        Dictionary<string, object?> vars,
        IReadOnlyDictionary<string, CaptureExpr>? captures = null)
    {
        var fragment = _provider.Emit(model, new StubCompileContext(stepId, captures));

        var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });
        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(CsxFragment.SanitiseId(stepId));
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }
}
