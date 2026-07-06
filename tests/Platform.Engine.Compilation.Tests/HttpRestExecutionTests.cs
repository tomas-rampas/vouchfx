// S03-F-02 — HttpRestProvider end-to-end execution tests (non-docker).
//
// Proves that the emitted CSX issues a real HTTP request, evaluates the
// response status, and writes a typed StepOutcome into Vars.
//
// Each test:
//   1. Starts an in-process HTTP responder on a free loopback port.
//   2. Builds HttpRestModel + seeds Vars with the service base URL.
//   3. Calls provider.Emit → CsxAssembler.Assemble → RoslynScriptCompiler.CompileOnce
//      → RunIsolatedAsync.
//   4. Reads StepOutcome from Vars and asserts the verdict.
//   5. Disposes the responder in a finally block (no 'using var').
//
// The in-process responder is built with System.Net.HttpListener.  On Windows
// the 'http://localhost:<port>/' prefix does not require a URL ACL reservation
// for the current user.  If HttpListener throws an access / init error the
// test falls back to a minimal raw TcpListener that writes one HTTP/1.1 response.
using System.Net;
using System.Net.Sockets;
using System.Text;
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Platform.Sdk;
using Platform.Steps.HttpRest;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S03-F-02: end-to-end execution tests for <c>HttpRestProvider</c>.
/// Runs against an in-process HTTP responder — no Docker required.
/// </summary>
public sealed class HttpRestExecutionTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal ICompileContext for the execution tests.
    /// </summary>
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
    /// Finds a free loopback TCP port by binding to port 0 and reading the
    /// assigned port number before releasing the socket.
    /// </summary>
    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Additional Roslyn metadata references required by the emitted CSX body.
    /// <c>RoslynScriptCompiler.CompileOnce</c> builds TPA references for the core BCL
    /// subset (CoreLib / Runtime / Collections / RegularExpressions); the emitted
    /// <c>HttpRest_Helpers</c> class also references <c>System.Net.Http</c>,
    /// <c>System.Net.Primitives</c>, <c>System.Text.Json</c>, <c>System.Globalization</c>,
    /// and <c>JsonPath.Net</c> (for S04-B-02 capture logic), so those must be supplied.
    /// </summary>
    private static readonly IReadOnlyList<string> s_additionalRefs = new[]
    {
        typeof(System.Net.Http.HttpClient).Assembly.Location,
        typeof(System.Net.HttpStatusCode).Assembly.Location,      // System.Net.Primitives
        typeof(System.Text.Json.JsonSerializer).Assembly.Location,
        typeof(System.Text.Json.Nodes.JsonNode).Assembly.Location, // System.Text.Json.Nodes
        typeof(System.Globalization.CultureInfo).Assembly.Location,
        typeof(System.Uri).Assembly.Location,                      // System.Private.Uri (safe URI composition, M1)
        typeof(Json.Path.JsonPath).Assembly.Location,              // JsonPath.Net — capture logic (S04-B-02)
        typeof(System.Xml.XmlDocument).Assembly.Location,          // System.Private.Xml — XPath capture logic (S07-B-01b)
    };

    /// <summary>
    /// Runs the provider pipeline for a single step and returns the
    /// <see cref="StepOutcome"/> written to <c>Vars</c>.
    /// </summary>
    private static async Task<StepOutcome> RunStepAsync(
        HttpRestModel model,
        string stepId,
        string serviceBaseUrl)
    {
        var provider = new HttpRestProvider();
        var ctx = new StubCompileContext(stepId);
        var fragment = provider.Emit(model, ctx);

        var assembled = CsxAssembler.Assemble(
            new[] { (stepId, fragment) });

        var compiled = RoslynScriptCompiler.CompileOnce(
            assembled.CsxSource,
            additionalReferencePaths: s_additionalRefs);

        var safeId = CsxFragment.SanitiseId(stepId);
        var serviceKey = VarKeys.Service(model.Target);

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [serviceKey] = serviceBaseUrl,
        };
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals);

        var outcomeKey = VarKeys.Outcome(safeId);
        Assert.True(vars.ContainsKey(outcomeKey),
            $"Vars must contain outcome key '{outcomeKey}' after RunIsolatedAsync. " +
            $"Actual keys: [{string.Join(", ", vars.Keys)}]");

        return Assert.IsType<StepOutcome>(vars[outcomeKey]);
    }

    // ── In-process HTTP responder ─────────────────────────────────────────────

    /// <summary>
    /// Starts an <see cref="HttpListener"/> that returns a fixed status code for
    /// every incoming request on <c>http://localhost:&lt;port&gt;/</c>.
    /// Returns the base URL and a dispose action that stops the listener.
    /// </summary>
    /// <remarks>
    /// If <see cref="HttpListener"/> fails to start (access/ACL error on some
    /// platforms), the method falls back to a raw <see cref="TcpListener"/> that
    /// sends a single hard-coded HTTP/1.1 response line.
    /// </remarks>
    private static (string BaseUrl, IDisposable Responder) StartResponder(
        int statusCode,
        int port)
    {
        var prefix = $"http://localhost:{port}/";
        HttpListener? hl = null;
        try
        {
            hl = new HttpListener();
            hl.Prefixes.Add(prefix);
            hl.Start();

            // Background loop: accept requests and reply with the configured status.
            var cts = new CancellationTokenSource();
            var listener = hl;
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    HttpListenerContext? hctx = null;
                    try
                    {
                        hctx = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        break;  // listener stopped
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (hctx is not null)
                    {
                        try
                        {
                            hctx.Response.StatusCode = statusCode;
                            hctx.Response.ContentLength64 = 0;
                            hctx.Response.Close();
                        }
                        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
                        {
                            // The responder's Dispose() cancels cts BEFORE calling listener.Stop()/
                            // Close(), but that teardown can still race a response this loop is
                            // actively writing (see the equivalent raw-Thread guard in
                            // MailExpectSmtpEmitTests.StartMockMailpit). This loop runs inside a
                            // fire-and-forget Task, so an unhandled exception here would not itself
                            // crash the host — contained anyway so teardown stays deterministic
                            // rather than relying on the TPL's swallow-unobserved-exception behaviour.
                        }
                    }
                }
            }, cts.Token);

            var disposed = false;
            var responder = new CallbackDisposable(() =>
            {
                if (!disposed)
                {
                    disposed = true;
                    cts.Cancel();
                    try { listener.Stop(); } catch { /* already stopped */ }
                    try { listener.Close(); } catch { /* already closed */ }
                    cts.Dispose();
                }
            });

            return (prefix.TrimEnd('/'), responder);
        }
        catch (Exception)
        {
            // Dispose the partially-initialised listener before falling back.
            try { hl?.Stop(); } catch { /* best-effort */ }
            try { hl?.Close(); } catch { /* best-effort */ }
        }

        // ── Fallback: raw TcpListener ─────────────────────────────────────────
        // Accepts one connection, writes one HTTP/1.1 response, then loops.
        var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();

        var tcpCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!tcpCts.Token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await tcp.AcceptTcpClientAsync(tcpCts.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                if (client is null) break;

                _ = Task.Run(async () =>
                {
                    var c = client;
                    try
                    {
                        var stream = c.GetStream();

                        // Drain the request headers (read until double CRLF).
                        var buf = new byte[4096];
                        var total = 0;
                        while (total < buf.Length)
                        {
                            var n = await stream.ReadAsync(buf.AsMemory(total), CancellationToken.None)
                                .ConfigureAwait(false);
                            if (n == 0) break;
                            total += n;
                            var text = Encoding.ASCII.GetString(buf, 0, total);
                            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                        }

                        var reason = statusCode switch
                        {
                            200 => "OK",
                            201 => "Created",
                            204 => "No Content",
                            400 => "Bad Request",
                            404 => "Not Found",
                            500 => "Internal Server Error",
                            _ => "Unknown",
                        };

                        var response = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 {statusCode} {reason}\r\n" +
                            "Content-Length: 0\r\n" +
                            "Connection: close\r\n" +
                            "\r\n");
                        await stream.WriteAsync(response.AsMemory(), CancellationToken.None)
                            .ConfigureAwait(false);
                        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        c.Dispose();
                    }
                });
            }
        }, tcpCts.Token);

        var tcpBaseUrl = $"http://localhost:{port}";
        var tcpResponder = new CallbackDisposable(() =>
        {
            tcpCts.Cancel();
            try { tcp.Stop(); } catch { /* best-effort */ }
            tcpCts.Dispose();
        });

        return (tcpBaseUrl, tcpResponder);
    }

    /// <summary>Disposable wrapper around a cleanup callback.</summary>
    private sealed class CallbackDisposable : IDisposable
    {
        private readonly Action _dispose;

        public CallbackDisposable(Action dispose) => _dispose = dispose;

        public void Dispose() => _dispose();
    }

    // ── Test cases ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When the responder returns 200 and <c>expect.status=200</c>, the outcome
    /// must be <see cref="Verdict.Pass"/>, <c>DurationMs</c> must be non-negative,
    /// and <c>Observation</c> must contain <c>"status":200</c>.
    /// </summary>
    [Fact]
    public async Task Execute_200Response_Expect200_ReturnsPass()
    {
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(statusCode: 200, port: port);
        try
        {
            var model = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));

            var outcome = await RunStepAsync(model, "step-pass", baseUrl);

            Assert.Equal(Verdict.Pass, outcome.Verdict);
            Assert.True(outcome.DurationMs >= 0,
                $"DurationMs must be non-negative; got {outcome.DurationMs}.");
            Assert.NotNull(outcome.Observation);
            Assert.Contains("\"status\":200", outcome.Observation, StringComparison.Ordinal);
        }
        finally
        {
            responder.Dispose();
        }
    }

    /// <summary>
    /// When the responder returns 500 but <c>expect.status=200</c>, the outcome
    /// must be <see cref="Verdict.Fail"/>.
    /// </summary>
    [Fact]
    public async Task Execute_500Response_Expect200_ReturnsFail()
    {
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(statusCode: 500, port: port);
        try
        {
            var model = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));

            var outcome = await RunStepAsync(model, "step-fail", baseUrl);

            Assert.Equal(Verdict.Fail, outcome.Verdict);
            Assert.Contains("\"status\":500", outcome.Observation!, StringComparison.Ordinal);
        }
        finally
        {
            responder.Dispose();
        }
    }

    /// <summary>
    /// When <c>expect.status</c> is <see langword="null"/> and the responder returns 200,
    /// the outcome must be <see cref="Verdict.Pass"/> (2xx range check).
    /// </summary>
    [Fact]
    public async Task Execute_200Response_NullExpect_ReturnsPass()
    {
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(statusCode: 200, port: port);
        try
        {
            var model = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: "/",
                Headers: null,
                Body: null,
                Expect: null);    // no status assertion

            var outcome = await RunStepAsync(model, "step-null-expect", baseUrl);

            Assert.Equal(Verdict.Pass, outcome.Verdict);
        }
        finally
        {
            responder.Dispose();
        }
    }

    /// <summary>
    /// When the target points at a closed/unused port the connection fails at the
    /// network layer.  This is an infrastructure failure, never a test failure, so
    /// the outcome must be <see cref="Verdict.EnvironmentError"/> (§12.1).
    /// </summary>
    [Fact]
    public async Task Execute_ClosedPort_ReturnsEnvironmentError()
    {
        // Use a port with no listener — FindFreePort() releases the port
        // immediately, so it is available but not bound.
        var closedPort = FindFreePort();
        var baseUrl = $"http://localhost:{closedPort}";

        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: "/",
            Headers: null,
            Body: null,
            Expect: new HttpExpect(Status: 200));

        var outcome = await RunStepAsync(model, "step-closed", baseUrl);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("error", outcome.Observation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A path containing double-quotes and backslashes must not cause a Roslyn
    /// compile error.  The network request will fail (no real service) so the
    /// outcome is <see cref="Verdict.EnvironmentError"/>.  The important assertion
    /// is that a <see cref="StepOutcome"/> is written at all (no exception escapes).
    /// </summary>
    [Fact]
    public async Task Execute_SpecialCharacterPath_CompilesAndRunsWithoutException()
    {
        const string dangerousPath = "/foo\"bar\\baz";
        var closedPort = FindFreePort();
        var baseUrl = $"http://localhost:{closedPort}";

        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: dangerousPath,
            Headers: null,
            Body: null,
            Expect: null);

        // If the special chars were not escaped, CompileOnce would throw
        // ScriptCompilationException.  We expect no exception, and the outcome
        // must be EnvironmentError (connection refused on the closed port).
        var outcome = await RunStepAsync(model, "step-special", baseUrl);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── M1: SSRF — Validate rejects unsafe paths ──────────────────────────────

    /// <summary>
    /// <see cref="HttpRestProvider.Validate"/> must reject an absolute URL in
    /// <c>path</c>, as this would allow an author to redirect the request off
    /// the declared target host (SSRF).
    /// </summary>
    [Fact]
    public void Validate_AbsoluteUrlPath_IsRejected()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: "https://evil.example.com/steal",
            Headers: null,
            Body: null,
            Expect: null);

        var result = provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("rooted relative", string.Join(" ", result.Errors), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="HttpRestProvider.Validate"/> must reject a protocol-relative path
    /// that starts with <c>//</c>, which resolves to a different host.
    /// </summary>
    [Fact]
    public void Validate_ProtocolRelativePath_IsRejected()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: "//evil.example.com/steal",
            Headers: null,
            Body: null,
            Expect: null);

        var result = provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// <see cref="HttpRestProvider.Validate"/> must reject a path that does not
    /// start with <c>/</c> (a relative path without a leading slash would be
    /// ambiguous and could be constructed to escape the host).
    /// </summary>
    [Fact]
    public void Validate_NonRootedRelativePath_IsRejected()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: "users/123",
            Headers: null,
            Body: null,
            Expect: null);

        var result = provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// <see cref="HttpRestProvider.Validate"/> must reject a path containing
    /// backslashes, which can be interpreted as path separators on some platforms
    /// and used to construct a UNC-style host escape.
    /// </summary>
    [Fact]
    public void Validate_BackslashPath_IsRejected()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: "/foo\\bar",
            Headers: null,
            Body: null,
            Expect: null);

        var result = provider.Validate(model, new StubProjectContext());

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// <see cref="HttpRestProvider.Validate"/> must accept a normal rooted
    /// relative path starting with <c>/</c>.
    /// </summary>
    [Fact]
    public void Validate_NormalRootedPath_IsAccepted()
    {
        var provider = new HttpRestProvider();
        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: "/api/v1/users",
            Headers: null,
            Body: null,
            Expect: null);

        var result = provider.Validate(model, new StubProjectContext());

        Assert.True(result.IsValid,
            $"Expected valid path to be accepted; errors: {string.Join("; ", result.Errors)}");
    }

    /// <summary>
    /// Regression guard for the Linux platform-dependency bug: on Linux,
    /// <c>Uri.TryCreate("/foo", UriKind.Absolute, out _)</c> returns <c>true</c>
    /// because a leading <c>/</c> is interpreted as an absolute Unix file URI
    /// (<c>file:///foo</c>).  A rooted path such as <c>/api/health</c> must be
    /// accepted by <see cref="HttpRestProvider.Validate"/> on every platform.
    /// </summary>
    [Fact]
    public void Validate_RootedPath_AcceptedRegardlessOfPlatform()
    {
        var provider = new HttpRestProvider();

        foreach (var path in new[] { "/", "/health", "/api/health", "/api/v1/users/123" })
        {
            var model = new HttpRestModel(
                Target: "svc",
                Method: "GET",
                Path: path,
                Headers: null,
                Body: null,
                Expect: null);

            var result = provider.Validate(model, new StubProjectContext());

            Assert.True(result.IsValid,
                $"Rooted path '{path}' must be accepted on all platforms; " +
                $"errors: {string.Join("; ", result.Errors)}");
        }
    }

    // ── M1: Runtime same-authority — normal request still passes ──────────────

    /// <summary>
    /// A normal GET to the in-process responder using a rooted <c>/</c> path
    /// must produce <see cref="Verdict.Pass"/> — the safe URI composition must
    /// not break the happy path.
    /// </summary>
    [Fact]
    public async Task Execute_NormalPath_SameAuthority_ReturnsPass()
    {
        var port = FindFreePort();
        var (baseUrl, responder) = StartResponder(statusCode: 200, port: port);
        try
        {
            var model = new HttpRestModel(
                Target: "whoami",
                Method: "GET",
                Path: "/",
                Headers: null,
                Body: null,
                Expect: new HttpExpect(Status: 200));

            var outcome = await RunStepAsync(model, "safe-path-step", baseUrl);

            Assert.Equal(Verdict.Pass, outcome.Verdict);
        }
        finally
        {
            responder.Dispose();
        }
    }

    /// <summary>
    /// When <c>baseUrl</c> is empty (service not discovered), the safe URI
    /// composition must throw during construction, which is caught and mapped to
    /// <see cref="Verdict.EnvironmentError"/> (§12.1).
    /// </summary>
    [Fact]
    public async Task Execute_EmptyBaseUrl_ReturnsEnvironmentError()
    {
        var model = new HttpRestModel(
            Target: "missing-svc",
            Method: "GET",
            Path: "/health",
            Headers: null,
            Body: null,
            Expect: null);

        // Do not seed serviceKey in Vars — baseUrl will be empty string.
        var outcome = await RunStepAsync(model, "empty-base-url", serviceBaseUrl: string.Empty);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
    }

    // ── M2: Timeout verdict is Inconclusive (§12.1) ───────────────────────────

    /// <summary>
    /// The helper source text must set <c>client.Timeout</c> to 30 seconds —
    /// verify the default-timeout assignment appears in the emitted helper code.
    /// This avoids writing a test that actually waits 30 s.
    /// </summary>
    [Fact]
    public void Helper_DefaultTimeout_IsSetTo30Seconds()
    {
        var provider = new HttpRestProvider();
        var ctx = new StubCompileContext("t1");
        var model = new HttpRestModel("svc", "GET", "/", null, null, null);
        var fragment = provider.Emit(model, ctx);

        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("FromSeconds(30)", helperSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// The helper source text must handle <see cref="System.Threading.Tasks.TaskCanceledException"/>
    /// and <see cref="System.TimeoutException"/> as <see cref="Verdict.Inconclusive"/>
    /// (§12.1) — verify the emitted code contains the correct verdict assignment.
    /// </summary>
    [Fact]
    public void Helper_TimeoutException_MapsToInconclusive_InSource()
    {
        var provider = new HttpRestProvider();
        var ctx = new StubCompileContext("t1");
        var model = new HttpRestModel("svc", "GET", "/", null, null, null);
        var fragment = provider.Emit(model, ctx);

        var helperSource = string.Join("\n", fragment.RequiredHelpers);

        Assert.Contains("Verdict.Inconclusive", helperSource, StringComparison.Ordinal);
        Assert.Contains("TaskCanceledException", helperSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection failure to a closed port (not a timeout) must still
    /// produce <see cref="Verdict.EnvironmentError"/>, confirming the general
    /// catch still routes connection errors correctly.
    /// </summary>
    [Fact]
    public async Task Execute_ConnectionFailure_ReturnsEnvironmentError_NotInconclusive()
    {
        var closedPort = FindFreePort();
        var baseUrl = $"http://localhost:{closedPort}";

        var model = new HttpRestModel(
            Target: "svc",
            Method: "GET",
            Path: "/health",
            Headers: null,
            Body: null,
            Expect: null);

        var outcome = await RunStepAsync(model, "conn-fail", baseUrl);

        Assert.Equal(Verdict.EnvironmentError, outcome.Verdict);
        Assert.NotNull(outcome.Observation);
        Assert.Contains("error", outcome.Observation, StringComparison.OrdinalIgnoreCase);
    }

    // ── Stub context for validate tests ───────────────────────────────────────

    /// <summary>
    /// Minimal <see cref="IProjectContext"/> stub for validation tests.
    /// Returns an empty <see cref="IProjectContext.DeclaredDependencies"/> map
    /// (Sprint-4 addition to the interface).
    /// </summary>
    private sealed class StubProjectContext : IProjectContext
    {
        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
