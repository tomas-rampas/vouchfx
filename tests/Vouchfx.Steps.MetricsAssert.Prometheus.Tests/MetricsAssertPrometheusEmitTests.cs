// Tests for MetricsAssertPrometheusProvider — CSX emitter and full compile-and-run
// round-trips (non-docker; a real Prometheus exposition body is served by an
// in-process System.Net.HttpListener so the full scrape → parse → match → capture
// path is exercised without any container).
//
// Covers:
//   1.  Emit: StatementBlock begins and ends with a brace.
//   2.  Emit: no 'using var' in the emitted StatementBlock.
//   3.  Emit: helper class is named 'MetricsAssertPrometheus_Helpers' (§13.3.1 prefix rule).
//   4.  Emit: step id with hyphens is sanitised to underscores in the StatementBlock.
//   5.  Emit: RequiredHelpers includes Substitute_Helpers and Secret_Helpers sources.
//   6.  Full compile-and-run: EnvironmentError when the service base URL is absent.
//   7.  Full compile-and-run: EnvironmentError when the target host is unreachable.
//   8.  Full compile-and-run: real scrape — label subset match + min/max bounds → Pass,
//       plus a capture: $.value evaluated against the synthesised observation object.
//   9.  Full compile-and-run: ambiguous selection (two samples share the name, no label
//       filter) → Fail with "ambiguous":true.
//   10. Full compile-and-run: metric not found → Fail with "found":false.
//   11. Full compile-and-run: non-200 scrape response → Fail with the status code.
//   12. Full compile-and-run: value mismatch → Fail with a {"value":{expected,actual}} shape.
//   13. Full compile-and-run: a NaN gauge value → Fail with a JSON-safe {"nonFinite":"NaN"}
//       shape, never a silent Pass (every IEEE comparison against NaN is false) and never
//       a raw invalid-JSON NaN token in the observation.
//   14. Full compile-and-run: missing ${secret:env/…} in path → EnvironmentError,
//       REFERENCE-ONLY observation (§17), no HTTP request ever attempted.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation;
using Vouchfx.Sdk;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Xunit;

namespace Vouchfx.Steps.MetricsAssert.Prometheus.Tests;

/// <summary>
/// Non-docker unit and integration tests for <see cref="MetricsAssertPrometheusProvider"/>.
/// </summary>
public sealed class MetricsAssertPrometheusEmitTests
{
    private sealed class StubCompileContext : ICompileContext
    {
        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        public StubCompileContext(
            string stepId,
            IReadOnlyDictionary<string, CaptureExpr>? captures = null)
        {
            StepId = stepId;
            CaptureExprs = captures ?? new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
            Captures = CaptureExprs.ToDictionary(
                kv => kv.Key, kv => kv.Value.Expression, StringComparer.Ordinal);
        }

        public string StepId { get; }
        public string SuiteNamespace => "Generated";
        public IReadOnlyDictionary<string, string> Captures { get; }
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
    }

    private readonly MetricsAssertPrometheusProvider _provider = new();

    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(Json.Path.JsonPath).Assembly.Location,
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.RegularExpressions.Regex).Assembly.Location,
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,
    };

    private static MetricsAssertPrometheusModel GetModel(
        string target = "sut",
        string path = "/metrics",
        string metric = "orders_total",
        IReadOnlyDictionary<string, string>? labels = null,
        string? value = "1",
        string? min = null,
        string? max = null) =>
        new MetricsAssertPrometheusModel(target, path, metric, labels, new MetricsExpectation(value, min, max));

    // ── 1. StatementBlock braces ──────────────────────────────────────────────

    [Fact]
    public void Emit_StatementBlock_StartsAndEndsWithBrace()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("met-step"));
        var block = fragment.StatementBlock.Trim();

        Assert.True(block.StartsWith('{'), "StatementBlock must begin with '{'.");
        Assert.True(block.EndsWith('}'), "StatementBlock must end with '}'.");
    }

    // ── 2. No 'using var' ─────────────────────────────────────────────────────

    [Fact]
    public void Emit_Fragment_ContainsNoUsingVar()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("my-step"));

        Assert.DoesNotContain("using var", fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 3. Helper class name prefix ───────────────────────────────────────────

    [Fact]
    public void Emit_RequiredHelpers_ContainsMetricsAssertPrometheusPrefixedClass()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("met-step"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("MetricsAssertPrometheus_Helpers", StringComparison.Ordinal));
    }

    // ── 4. Step id sanitisation ───────────────────────────────────────────────

    [Fact]
    public void Emit_StepIdWithHyphens_IsSanitisedInStatementBlock()
    {
        const string rawId = "met-step-one";
        var safeId = CsxFragment.SanitiseId(rawId);
        var fragment = _provider.Emit(GetModel(), new StubCompileContext(rawId));

        Assert.Contains(VarKeys.Outcome(safeId), fragment.StatementBlock, StringComparison.Ordinal);
        Assert.DoesNotContain(rawId, fragment.StatementBlock, StringComparison.Ordinal);
    }

    // ── 5. RequiredHelpers includes Substitute_Helpers and Secret_Helpers ────────

    [Fact]
    public void Emit_RequiredHelpers_IncludesSubstituteAndSecretSources()
    {
        var fragment = _provider.Emit(GetModel(), new StubCompileContext("h-check"));

        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Substitute_Helpers", StringComparison.Ordinal));
        Assert.Contains(fragment.RequiredHelpers, h =>
            h.Contains("Secret_Helpers", StringComparison.Ordinal));
    }

    // ── 6. Full compile-and-run: EnvironmentError when service base URL absent ──

    [Fact]
    public async Task Emit_CompileAndRun_AbsentServiceUrl_ReturnsEnvironmentError()
    {
        var outcome = await RunStepAsync(GetModel(), "met-step", new Dictionary<string, object?>(StringComparer.Ordinal));

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.True(outcome.DurationMs >= 0, "DurationMs must be non-negative.");
        Assert.NotNull(outcome.Observation);
    }

    // ── 7. Full compile-and-run: EnvironmentError when host unreachable ─────────

    [Fact]
    public async Task Emit_CompileAndRun_HostUnreachable_ReturnsEnvironmentError()
    {
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Service("sut")] = "http://127.0.0.1:1/",
        };

        var outcome = await RunStepAsync(GetModel(), "met-dead", vars);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
    }

    // ── 8. Full compile-and-run: real scrape, label match, min/max, capture ────

    [Fact]
    public async Task Emit_CompileAndRun_RealScrape_LabelMatchMinMaxAndCapture_ReturnsPass()
    {
        const string body =
            "# HELP orders_total Total orders\n" +
            "# TYPE orders_total counter\n" +
            "orders_total{status=\"ok\",region=\"eu\"} 42.5 1700000000000\n" +
            "orders_total{status=\"failed\"} 1\n" +
            "http_requests_total 7 # {trace_id=\"abc\"} 0.5\n";

        var (prefix, serveTask, listener) = StartOneShotServer(body);
        try
        {
            var model = GetModel(
                labels: new Dictionary<string, string> { ["status"] = "ok" },
                value: null, min: "40", max: "45");
            var captures = new Dictionary<string, CaptureExpr>(StringComparer.Ordinal)
            {
                ["orderVal"] = new CaptureExpr(CaptureFormat.JsonPath, "$.value"),
            };
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "met-scrape", vars, captures);
            await serveTask;

            Assert.Equal(Verdict.Pass, outcome.Verdict);
            Assert.NotNull(outcome.Observation);
            Assert.Contains("42.5", outcome.Observation!, StringComparison.Ordinal);
            Assert.True(vars.TryGetValue("orderVal", out var captured), "orderVal must be captured.");
            Assert.Equal("42.5", captured);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 9. Full compile-and-run: ambiguous selection ───────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_AmbiguousSelection_ReturnsFail()
    {
        const string body = "orders_total{status=\"ok\"} 1\norders_total{status=\"failed\"} 2\n";

        var (prefix, serveTask, listener) = StartOneShotServer(body);
        try
        {
            var model = GetModel(labels: null, value: "1");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "met-ambiguous", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("ambiguous", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 10. Full compile-and-run: metric not found ─────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_MetricNotFound_ReturnsFail()
    {
        const string body = "other_metric 1\n";

        var (prefix, serveTask, listener) = StartOneShotServer(body);
        try
        {
            var model = GetModel(value: "1");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "met-notfound", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("\"found\":false", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 11. Full compile-and-run: non-200 scrape response ──────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_NonSuccessStatus_ReturnsFail()
    {
        var (prefix, serveTask, listener) = StartOneShotServer("not found", statusCode: 404);
        try
        {
            var model = GetModel(value: "1");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "met-404", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("\"status\":404", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 12. Full compile-and-run: value mismatch ───────────────────────────────

    [Fact]
    public async Task Emit_CompileAndRun_ValueMismatch_ReturnsFail()
    {
        const string body = "orders_total 5\n";

        var (prefix, serveTask, listener) = StartOneShotServer(body);
        try
        {
            var model = GetModel(value: "1");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "met-mismatch", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("\"value\":{\"expected\":1", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("\"actual\":5", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 13. Full compile-and-run: NaN gauge → Fail, never a silent Pass ─────────

    /// <summary>
    /// A matched sample whose value is <c>NaN</c> must Fail, not silently Pass.  Every
    /// IEEE double comparison against NaN evaluates to <see langword="false"/>, so
    /// without an explicit non-finite guard <c>actual &lt; expectMin</c> /
    /// <c>actual &gt; expectMax</c> would both be false and an exact-value check would
    /// never detect the "mismatch" — the step would Pass on a value that fails every
    /// declared assertion.  The Fail observation must also be valid JSON: NaN has no
    /// JSON numeric representation, so the shape carries a string token
    /// (<c>"nonFinite":"NaN"</c>), never a raw <c>NaN</c> literal.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_NonFiniteValue_NaN_ReturnsFail()
    {
        const string body = "orders_total NaN\n";

        var (prefix, serveTask, listener) = StartOneShotServer(body);
        try
        {
            var model = GetModel(value: "1");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "met-nan", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.NotNull(outcome.Observation);
            Assert.Contains("\"nonFinite\":\"NaN\"", outcome.Observation!, StringComparison.Ordinal);

            // The observation must be valid JSON (no raw NaN token leaked into it).
            using var doc = System.Text.Json.JsonDocument.Parse(outcome.Observation!);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// A matched sample whose value is <c>+Inf</c> against an <c>expect.max</c> bound
    /// must also Fail (an unbounded value must not silently satisfy a finite upper
    /// bound), reported via the same JSON-safe <c>nonFinite</c> shape.
    /// </summary>
    [Fact]
    public async Task Emit_CompileAndRun_NonFiniteValue_PositiveInfinity_ReturnsFail()
    {
        const string body = "orders_total +Inf\n";

        var (prefix, serveTask, listener) = StartOneShotServer(body);
        try
        {
            var model = GetModel(value: null, min: null, max: "100");
            var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [VarKeys.Service("sut")] = prefix,
            };

            var outcome = await RunStepAsync(model, "met-posinf", vars);
            await serveTask;

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.NotNull(outcome.Observation);

            // Parse rather than substring-match: System.Text.Json's default (safe)
            // encoder escapes the plus sign as a \uXXXX sequence, so the raw
            // serialised text does not contain a literal "+Inf" substring even
            // though it is valid, correctly-round-tripping JSON for the string "+Inf".
            using var doc = System.Text.Json.JsonDocument.Parse(outcome.Observation!);
            Assert.Equal("+Inf", doc.RootElement.GetProperty("nonFinite").GetString());
        }
        finally
        {
            listener.Stop();
        }
    }

    // ── 14. Compile round-trip: EnvironmentError via SECRET resolution (no HTTP) ──

    [Fact]
    public async Task Emit_CompileAndRun_MissingSecretInPath_ReturnsEnvironmentError_ReferenceOnly()
    {
        var envName = "VOUCHFX_METRICS_PROM_MISSING_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envName, null);
        try
        {
            const string stepId = "met-secret-step";
            var model = new MetricsAssertPrometheusModel(
                "sut", $"/metrics?token=${{secret:env/{envName}}}", "orders_total", null,
                new MetricsExpectation(Value: "1", Min: null, Max: null));

            var fragment = _provider.Emit(model, new StubCompileContext(stepId));
            // Splice via CsxAssembler (not a manual join) — it declares the per-step
            // __stepCt_<safeId> / __stepBudgetGoverned_<safeId> locals the emitted call
            // site now references (§4 common step fields, issue #232).
            var assembled = CsxAssembler.Assemble(new[] { (stepId, fragment) });

            var compiled = RoslynScriptCompiler.CompileOnce(
                assembled.CsxSource, additionalReferencePaths: s_additionalRefs);

            // Stage a service base URL so the helper proceeds PAST path resolution's
            // secret check first — the path is resolved before the base-URL is even
            // read in the guarded region, so an unreachable/absent base URL would
            // never be exercised here; a valid-looking base URL is staged so the
            // ONLY failure mode possible is the missing secret.
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
            Assert.True(vars.ContainsKey(outcomeKey),
                $"Expected Vars to contain outcome key '{outcomeKey}'. " +
                $"Actual keys: [{string.Join(", ", vars.Keys)}]");

            var outcome = Assert.IsType<StepOutcome>(vars[outcomeKey]);

            Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
            Assert.NotNull(outcome.Observation);
            Assert.Contains("secretError", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains("env", outcome.Observation!, StringComparison.Ordinal);
            Assert.Contains(envName, outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, null);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts an in-process <see cref="HttpListener"/> bound to a freshly-allocated loopback
    /// port that serves <paramref name="body"/> exactly once with the given
    /// <paramref name="statusCode"/>, then returns the bound prefix, a <see cref="Task"/> that
    /// completes once the single request has been served, and the listener so the caller can
    /// <c>Stop()</c> it.
    /// </summary>
    /// <remarks>
    /// The port is chosen by the OS, never hard-coded (issue #431). A fixed port is
    /// red-by-boot-order on Windows: WinNAT's dynamic exclusion ranges move across reboots,
    /// and a measured range of 18498-18597 covered every port this file used to pin, turning
    /// all seven listener-backed rows red with <see cref="HttpListenerException"/> and costing
    /// a full misattributed investigation.
    /// </remarks>
    private static (string Prefix, Task ServeTask, HttpListener Listener) StartOneShotServer(
        string body, int statusCode = 200)
    {
        var (prefix, listener) = StartOnAFreePort();

        var serveTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.OutputStream.Close();
        });

        return (prefix, serveTask, listener);
    }

    /// <summary>
    /// Binds an <see cref="HttpListener"/> to a loopback port the OS reports as free, returning
    /// the bound prefix and the started listener.
    /// </summary>
    /// <remarks>
    /// <see cref="FindFreePort"/> releases the probed port before <see cref="HttpListener"/>
    /// re-binds it, so another process can win the race in between. That TOCTOU window is far
    /// narrower than a fixed port's permanent exposure, and a single retry with a fresh port
    /// absorbs it. If BOTH attempts fail, both exceptions are surfaced: the first attempt's
    /// error code is the evidence a later http.sys/WinNAT diagnosis needs, and swallowing it
    /// would leave only the second — a different port, and possibly a different cause.
    /// </remarks>
    private static (string Prefix, HttpListener Listener) StartOnAFreePort()
    {
        try
        {
            return BindOnAFreePort();
        }
        catch (HttpListenerException first)
        {
            try
            {
                return BindOnAFreePort();
            }
            catch (HttpListenerException second)
            {
                throw new AggregateException(
                    "Could not bind an HttpListener on an OS-allocated free port, twice running.",
                    first,
                    second);
            }
        }

        static (string Prefix, HttpListener Listener) BindOnAFreePort()
        {
            var prefix = $"http://127.0.0.1:{FindFreePort()}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
            }
            catch
            {
                listener.Close();
                throw;
            }

            return (prefix, listener);
        }
    }

    /// <summary>Reserves a free loopback TCP port by binding port 0 and releasing it.</summary>
    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<StepOutcome> RunStepAsync(
        MetricsAssertPrometheusModel model,
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
