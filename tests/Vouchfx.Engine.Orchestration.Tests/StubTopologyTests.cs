using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated integration tests for S01-A-02 (stub topology, health-gated startup)
/// and S01-A-03 (endpoint and connection-string resolution).
/// Run with: <c>dotnet test --filter "requires=docker"</c>.
/// Excluded from the unit-CI job via: <c>dotnet test --filter "requires!=docker"</c>.
/// </summary>
/// <remarks>
/// <para>
/// R-1 finding: this test assembly carries <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c>
/// and <c>Aspire.AppHost.Sdk</c>, which embeds <c>dcpclipath</c>/<c>dcpextensionpaths</c>
/// assembly metadata required by <see cref="Aspire.Hosting.DistributedApplicationOptions"/>.
/// The assembly name is passed to <see cref="StubTopology.StartAsync"/> so the DCP binary
/// is resolved from this assembly rather than from the xUnit runner entry assembly.
/// </para>
/// </remarks>
public sealed class StubTopologyTests
{
    private readonly ITestOutputHelper _output;

    public StubTopologyTests(ITestOutputHelper output) => _output = output;

    // The short name of *this* test assembly — the one whose AssemblyInfo carries dcpclipath.
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    // Per-run startup timeout.  Set high enough that a genuine pull/init does not mask a hang,
    // but low enough that a real hang (e.g. wrong WaitFor target) fails loudly.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(120);

    // -----------------------------------------------------------------------
    // S01-A-02 — stub topology, health-gated startup
    // -----------------------------------------------------------------------

    /// <summary>
    /// A2 (basic) — the stub topology (Postgres + web container) starts, reaches healthy,
    /// and disposes cleanly in a single run.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task StubTopology_StartsHealthyAndDisposesCleanly()
    {
        // Act
        await using var topology = await StubTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: RunTimeout);

        // Assert — reaching this line proves health-gate completed without timeout.
        Assert.NotNull(topology.Inner.Application);
        _output.WriteLine("Single-run healthy: PASS");
    }

    /// <summary>
    /// A2 (determinism) — the stub topology reaches healthy in ALL 20 consecutive runs and
    /// BOTH discovery paths (connection string + HTTP endpoint) succeed in every run.
    /// This is the anti-flakiness proof that catches:
    /// <list type="bullet">
    ///   <item>The Postgres server-vs-database race described in §4.</item>
    ///   <item>The HTTP-serving race — port-mapped but not yet serving — that can flake A-03
    ///   on loaded CI agents when the health-check annotation is absent (S2 fix).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The §4 race: Aspire's Postgres server resource (<c>"pg"</c>) becomes healthy before
    /// the DCP lifecycle script finishes creating the database (<c>"appdb"</c>).  On fast
    /// hardware this window is wide enough to cause intermittent failures when the engine
    /// polls the connection string before the database exists.  Health-gating on
    /// <c>"appdb"</c> (the database resource) eliminates the race entirely.
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task StubTopology_DeterministicStartup_AllTwentyRunsReachHealthy()
    {
        const int TotalRuns = 20;
        var overallSw = Stopwatch.StartNew();
        var passed = 0;

        using var httpClient = new HttpClient();

        for (int run = 1; run <= TotalRuns; run++)
        {
            var runSw = Stopwatch.StartNew();
            Exception? runException = null;

            try
            {
                await using var topology = await StubTopology.StartAsync(
                    appHostAssemblyName: AppHostAssemblyName,
                    startupTimeout: RunTimeout);

                // --- Discovery path A: managed-dependency connection string ---
                var cs = await topology.GetConnectionStringAsync();
                if (string.IsNullOrWhiteSpace(cs))
                {
                    throw new InvalidOperationException(
                        $"Run {run}: GetConnectionStringAsync() returned null/empty after healthy gate.");
                }

                // --- Discovery path B: service HTTP endpoint (N3 anti-flakiness extension) ---
                // The HTTP health check registered via WithHttpHealthCheck("/") guarantees the
                // container is actually serving before the gate completes, but we issue a real
                // GET here so the 20/20 proof covers BOTH discovery paths end-to-end.
                var url = topology.GetHttpEndpointUrl();
                if (string.IsNullOrWhiteSpace(url))
                {
                    throw new InvalidOperationException(
                        $"Run {run}: GetHttpEndpointUrl() returned null/empty after healthy gate.");
                }

                using var response = await httpClient.GetAsync(new Uri(url));
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Run {run}: HTTP GET {url} returned {(int)response.StatusCode} {response.ReasonPhrase}; expected 2xx.");
                }

                passed++;
            }
            catch (Exception ex)
            {
                runException = ex;
            }

            runSw.Stop();
            var status = runException is null
                ? "PASS"
                : $"FAIL ({runException.GetType().Name}: {runException.Message})";
            _output.WriteLine(
                $"Run {run:D2}/{TotalRuns}: {status} in {runSw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");
        }

        overallSw.Stop();
        _output.WriteLine(
            $"Result: {passed}/{TotalRuns} passed in {overallSw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s total.");

        // All 20 runs MUST succeed on BOTH discovery paths.  Any failure here indicates either
        // the server-vs-database race (check WaitFor target — must be "appdb", not "pg") or the
        // HTTP-serving race (check WithHttpHealthCheck is present on the "web" container).
        Assert.Equal(TotalRuns, passed);
    }

    // -----------------------------------------------------------------------
    // S01-A-03 — endpoint and connection-string resolution
    // -----------------------------------------------------------------------

    /// <summary>
    /// A3 — resolves the Postgres connection string via <c>GetConnectionStringAsync()</c>
    /// and proves it is live by opening an <see cref="NpgsqlConnection"/> and executing
    /// <c>SELECT 1</c>.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task GetConnectionString_AppDb_ConnectsAndSelectOneReturnsOne()
    {
        // Arrange
        await using var topology = await StubTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: RunTimeout);

        // Act — connection string
        var connectionString = await topology.GetConnectionStringAsync();
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "GetConnectionStringAsync() must return a non-empty string after healthy gate.");

        // Redact password from log output for safety.
        _output.WriteLine($"Connection string (redacted): {RedactPassword(connectionString!)}");

        // Act — live connection
        var conn = new NpgsqlConnection(connectionString);
        await using (conn)
        {
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            var result = await cmd.ExecuteScalarAsync();

            // Assert
            Assert.Equal(1, Convert.ToInt32(result, CultureInfo.InvariantCulture));
            _output.WriteLine("SELECT 1 → 1: PASS");
        }
    }

    /// <summary>
    /// A3 — resolves the <c>"web"</c> container HTTP endpoint via the retained
    /// <see cref="Aspire.Hosting.ApplicationModel.IResourceBuilder{T}"/> pattern
    /// and proves it is reachable by issuing an <see cref="HttpClient.GetAsync(Uri)"/> request.
    /// </summary>
    /// <remarks>
    /// §4 footgun note: <c>app.GetEndpoint(name, scheme)</c> does not exist as a
    /// non-extension member on <see cref="Aspire.Hosting.DistributedApplication"/>.
    /// The only correct discovery path without taking a dependency on
    /// <c>Aspire.Hosting.Testing</c> is through the retained builder, as implemented
    /// in <see cref="StubTopology.GetHttpEndpointUrl"/>.
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task GetHttpEndpointUrl_Web_Returns200FromWhoami()
    {
        // Arrange
        await using var topology = await StubTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: RunTimeout);

        // Act — endpoint resolution via retained builder
        var url = topology.GetHttpEndpointUrl();
        Assert.False(
            string.IsNullOrWhiteSpace(url),
            "GetHttpEndpointUrl() must return a non-empty URL after healthy gate.");

        _output.WriteLine($"Web endpoint URL: {url}");

        // Act — live HTTP request
        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(url));

        // Assert — traefik/whoami returns 200 OK with a plain-text body.
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected 2xx from {url} but got {(int)response.StatusCode} {response.ReasonPhrase}.");

        var body = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"Response body (first 200 chars): {body[..Math.Min(200, body.Length)]}");
        _output.WriteLine("HTTP GET → 2xx: PASS");
    }

    /// <summary>
    /// A3 (combined) — resolves BOTH the connection string and the HTTP endpoint in a single
    /// topology run and asserts each independently, mirroring the task acceptance criteria.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task BothDiscoveryPaths_ConnectionStringAndHttpEndpoint_BothSucceed()
    {
        // Arrange
        await using var topology = await StubTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: RunTimeout);

        // --- Path A: managed-dependency connection string ---
        var connectionString = await topology.GetConnectionStringAsync();
        Assert.False(
            string.IsNullOrWhiteSpace(connectionString),
            "Connection string must be non-empty.");

        _output.WriteLine($"Connection string (redacted): {RedactPassword(connectionString!)}");

        var conn = new NpgsqlConnection(connectionString);
        await using (conn)
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            var result = await cmd.ExecuteScalarAsync();
            Assert.Equal(1, Convert.ToInt32(result, CultureInfo.InvariantCulture));
        }

        _output.WriteLine("Path A (connection string + SELECT 1): PASS");

        // --- Path B: service endpoint via retained builder ---
        var url = topology.GetHttpEndpointUrl();
        Assert.False(string.IsNullOrWhiteSpace(url), "HTTP endpoint URL must be non-empty.");

        _output.WriteLine($"Web endpoint URL: {url}");

        using var client = new HttpClient();
        using var response = await client.GetAsync(new Uri(url));
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected 2xx from {url} but got {(int)response.StatusCode}.");

        _output.WriteLine("Path B (HTTP endpoint → 2xx): PASS");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Redacts the password segment of a Npgsql connection string for safe log output.
    /// Leaves all other segments intact.
    /// </summary>
    private static string RedactPassword(string connectionString)
    {
        try
        {
            var csBuilder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Password = "***REDACTED***"
            };
            return csBuilder.ToString();
        }
        catch
        {
            return "[redaction-failed]";
        }
    }
}
