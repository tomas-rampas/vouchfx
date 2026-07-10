// SUT configuration surface — DOCKER-GATED end-to-end proof (see also
// EnvironmentMapperTests' non-docker structural assertions and
// SutEnvConfigWebhookContainerAliasDockerTests, the sibling docker test for point 3).
//
// Builds a tiny SUT image ON THE FLY (python:3.12-alpine + stdlib http.server — no pip
// installs, so the build never depends on PyPI availability) exposing:
//   GET /          -> 200 (health check)
//   GET /env       -> {"ORDERS_CONN": "...", "ORDERS_HOST": "...", "ORDERS_PORT": "..."}
//                     (the resolved env: values EnvironmentMapper wired via WithEnvironment)
//   GET /check?host=..&port=.. -> {"ok": true/false} — opens a TCP connection FROM INSIDE
//                     the container to host:port and reports whether it succeeded.
//
// Wires that SUT into a topology with a real postgres dependency and asserts, from the HOST
// test process (calling the SUT's host-published HTTP endpoint — the framework's normal path
// for talking TO a service under test):
//   (a) ORDERS_HOST/ORDERS_CONN resolved to something OTHER than localhost/127.0.0.1 — proving
//       the container received the container-network address, never the host-published
//       localhost:randomport (the exact defect this feature fixes, per the feature brief).
//   (b) GET /check against that resolved host:port returns {"ok": true} — proving the SUT
//       container can actually reach postgres over the Aspire-managed Docker network using
//       ONLY the env: value it was given (no other wiring).
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~SutEnvConfigDockerTests"
// Excluded from non-docker CI:  dotnet test --filter "requires!=docker"

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end proof of the SUT configuration surface's <c>env:</c> mapping: a
/// containerised SUT built on the fly receives a postgres connection resolved to the
/// container-network address (never the host-published <c>localhost:randomport</c>), and can
/// actually reach it over the Aspire-managed Docker network.
/// </summary>
public sealed class SutEnvConfigDockerTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    public SutEnvConfigDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>Short name of THIS assembly — it carries the DCP metadata (R-1 finding).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout: allows image pulls on first run.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Bounded budget for the on-the-fly `docker build` (base-image pull + tiny COPY).</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fixed local tag for the on-the-fly SUT image. A fixed (not per-run-unique) tag lets
    /// Docker's build cache make repeated local runs near-instant after the first.
    /// </summary>
    private const string SutImageTag = "vouchfx-sut-env-config-test:local";

    private const string DependencyName = "orders";
    private const string ServiceName = "sut";

    // ── The on-the-fly SUT image ────────────────────────────────────────────────────────

    private const string Dockerfile =
        "FROM python:3.12-alpine\n" +
        "COPY app.py /app.py\n" +
        "EXPOSE 8080\n" +
        "CMD [\"python\", \"/app.py\"]\n";

    // Stdlib-only (no pip install — the build must never depend on PyPI reachability).
    private const string AppPy = """
        import json
        import os
        import socket
        from http.server import BaseHTTPRequestHandler, HTTPServer
        from urllib.parse import urlparse, parse_qs

        SELECTED_ENV_KEYS = ["ORDERS_CONN", "ORDERS_HOST", "ORDERS_PORT"]

        class Handler(BaseHTTPRequestHandler):
            def _send_json(self, status, payload):
                body = json.dumps(payload).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self):
                parsed = urlparse(self.path)
                if parsed.path == "/":
                    self.send_response(200)
                    self.send_header("Content-Length", "2")
                    self.end_headers()
                    self.wfile.write(b"ok")
                elif parsed.path == "/env":
                    values = {k: os.environ.get(k) for k in SELECTED_ENV_KEYS}
                    self._send_json(200, values)
                elif parsed.path == "/check":
                    qs = parse_qs(parsed.query)
                    host = qs.get("host", [None])[0]
                    port = qs.get("port", [None])[0]
                    if not host or not port:
                        self._send_json(400, {"ok": False, "error": "host and port query params required"})
                        return
                    try:
                        with socket.create_connection((host, int(port)), timeout=5):
                            self._send_json(200, {"ok": True})
                    except Exception as exc:
                        self._send_json(200, {"ok": False, "error": str(exc)})
                else:
                    self.send_response(404)
                    self.end_headers()

            def log_message(self, format, *args):
                pass  # keep container logs quiet

        if __name__ == "__main__":
            server = HTTPServer(("0.0.0.0", 8080), Handler)
            server.serve_forever()
        """;

    /// <summary>Builds <see cref="SutImageTag"/> from <see cref="Dockerfile"/>/<see cref="AppPy"/> once per test run.</summary>
    public async Task InitializeAsync()
    {
        var buildDir = Path.Combine(Path.GetTempPath(), "vouchfx-sut-env-config-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(buildDir, "Dockerfile"), Dockerfile);
            await File.WriteAllTextAsync(Path.Combine(buildDir, "app.py"), AppPy);

            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(SutImageTag);
            psi.ArgumentList.Add(buildDir);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start 'docker build' process.");
            using var cts = new CancellationTokenSource(BuildTimeout);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            _output.WriteLine("=== docker build stdout ===");
            _output.WriteLine(stdout);
            if (proc.ExitCode != 0)
            {
                _output.WriteLine("=== docker build stderr ===");
                _output.WriteLine(stderr);
                throw new InvalidOperationException(
                    $"'docker build -t {SutImageTag} {buildDir}' exited {proc.ExitCode}. See test output for the log.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(buildDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup: a stray temp build-context directory is not test-fatal.
            }
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── The test ─────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("requires", "docker")]
    public async Task ImageFormServiceWithEnv_ReceivesContainerNetworkConnectionString_AndCanReachIt()
    {
        // Arrange — one postgres dependency, one image-form SUT service whose env: mapping
        // references it in full form (ORDERS_CONN) and in parts (ORDERS_HOST/ORDERS_PORT).
        var environment = new EnvironmentSpec(
            Services: new System.Collections.Generic.Dictionary<string, ServiceSpec>
            {
                [ServiceName] = new ServiceSpec(
                    Image: SutImageTag,
                    Project: null,
                    ImagePullPolicy: null,
                    HttpPort: 8080,
                    Env: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["ORDERS_CONN"] = $"${{conn:{DependencyName}}}",
                        ["ORDERS_HOST"] = $"${{conn:{DependencyName}.host}}",
                        ["ORDERS_PORT"] = $"${{conn:{DependencyName}.port}}",
                    }),
            },
            Dependencies: new System.Collections.Generic.Dictionary<string, DependencySpec>
            {
                [DependencyName] = new DependencySpec(Type: "postgres", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

        await using var suite = await SuiteTopology.StartAsync(
            environment: environment,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var sutBaseUrl = ((string)suite.DiscoveredServices[ServiceName]).TrimEnd('/');
        _output.WriteLine($"SUT base URL (host-published): {sutBaseUrl}");

        using var http = new HttpClient();

        // Act 1 — health check.
        var healthResp = await http.GetAsync($"{sutBaseUrl}/");
        Assert.True(healthResp.IsSuccessStatusCode, $"SUT health check failed: {(int)healthResp.StatusCode}");

        // Act 2 — read back the resolved env values.
        var envJson = await http.GetStringAsync($"{sutBaseUrl}/env");
        _output.WriteLine($"SUT /env response: {envJson}");
        using var envDoc = JsonDocument.Parse(envJson);
        var conn = envDoc.RootElement.GetProperty("ORDERS_CONN").GetString();
        var host = envDoc.RootElement.GetProperty("ORDERS_HOST").GetString();
        var port = envDoc.RootElement.GetProperty("ORDERS_PORT").GetString();

        Assert.False(string.IsNullOrWhiteSpace(conn), "ORDERS_CONN must be a non-empty resolved connection string.");
        Assert.False(string.IsNullOrWhiteSpace(host), "ORDERS_HOST must be a non-empty resolved host.");
        Assert.False(string.IsNullOrWhiteSpace(port), "ORDERS_PORT must be a non-empty resolved port.");

        // Assert (a) — the resolved host is the CONTAINER-network address, never the
        // host-published localhost:randomport (the exact defect this feature fixes).
        Assert.NotEqual("localhost", host, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("127.0.0.1", host, StringComparer.Ordinal);
        Assert.DoesNotContain("127.0.0.1", conn, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", conn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Username=postgres", conn, StringComparison.Ordinal);
        Assert.Contains($"Database={DependencyName}db", conn, StringComparison.Ordinal);

        // Act 3 / Assert (b) — the SUT container can TCP-connect to that resolved host:port
        // FROM INSIDE the Aspire-managed Docker network, using only the env: value it was given.
        var checkResp = await http.GetStringAsync($"{sutBaseUrl}/check?host={host}&port={port}");
        _output.WriteLine($"SUT /check response: {checkResp}");
        using var checkDoc = JsonDocument.Parse(checkResp);
        Assert.True(
            checkDoc.RootElement.GetProperty("ok").GetBoolean(),
            $"SUT container failed to TCP-connect to '{host}:{port}' (the resolved ORDERS_HOST/ORDERS_PORT): {checkResp}");
    }
}
