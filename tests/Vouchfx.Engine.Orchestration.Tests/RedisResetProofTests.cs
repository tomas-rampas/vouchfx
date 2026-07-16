// S04-A-01 follow-up chunk Docker-gated proof: RedisScenarioIsolation flushes the designated
// database of a Redis dependency between scenarios via FLUSHDB.
//
// Test proves:
//   • Topology built once via SuiteTopology.StartAsync (single redis dependency).
//   • Scenario 1: sets several keys; asserts DBSIZE == 3.
//   • RedisScenarioIsolation.EndScenarioAsync flushes the database — DBSIZE must be 0
//     afterwards.
//   • Scenario 2: asserts DBSIZE == 0, then sets keys again, resets again, and asserts DBSIZE
//     == 0 a second time — proving the isolation instance is reusable across many resets, not
//     just a single one.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~RedisResetProof"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Design notes (mirrors SqlServerResetProofTests.cs / MySqlResetProofTests.cs):
//   • Direct StackExchange.Redis connections are used for setup/assertions — this test targets
//     the isolation seam itself, not the cache-assert provider pipeline, so no CSX compile/run
//     is needed.
//   • NOTE: Do NOT run this test locally or in CI unless Docker is available and redis:7 is
//     pre-pulled (or Docker Hub is reachable).

using StackExchange.Redis;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated proof that <see cref="RedisScenarioIsolation"/> flushes all keys from a Redis
/// dependency's designated database between scenarios.
/// </summary>
public sealed class RedisResetProofTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    /// <summary>Startup timeout: Redis starts quickly, but leave generous headroom.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Logical name of the Redis dependency under test.</summary>
    private const string DepName = "resetcache";

    public RedisResetProofTests(ITestOutputHelper output) => _output = output;

    private static EnvironmentSpec BuildEnv() =>
        new(
            Services: null,
            Dependencies: new Dictionary<string, DependencySpec>
            {
                [DepName] = new DependencySpec(Type: "redis", Version: null, Extra: null),
            },
            Seed: null,
            ImageRegistry: null,
            ImagePullPolicy: null);

    /// <summary>Sets three string keys.</summary>
    private static async Task SetKeysAsync(string connStr)
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(connStr).ConfigureAwait(false);
        try
        {
            var db = mux.GetDatabase();
            await db.StringSetAsync("k1", "v1").ConfigureAwait(false);
            await db.StringSetAsync("k2", "v2").ConfigureAwait(false);
            await db.StringSetAsync("k3", "v3").ConfigureAwait(false);
        }
        finally
        {
            mux.Dispose();
        }
    }

    /// <summary>Returns DBSIZE for database 0 via a fresh multiplexer.</summary>
    private static async Task<long> CountKeysAsync(string connStr)
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(connStr).ConfigureAwait(false);
        try
        {
            var server = mux.GetServer(mux.GetEndPoints()[0]);
            return await server.DatabaseSizeAsync().ConfigureAwait(false);
        }
        finally
        {
            mux.Dispose();
        }
    }

    /// <summary>
    /// Core Redis reset-proof: FLUSHDB clears all keys, across two independent reset
    /// round-trips.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task RedisIsolation_FlushesKeys_AcrossTwoRoundTrips()
    {
        var env = BuildEnv();

        // ── Build topology once ────────────────────────────────────────────────
        await using var suite = await SuiteTopology.StartAsync(
            environment: env,
            appHostAssemblyName: AppHostAssemblyName,
            startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DepName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr),
            $"DiscoveredServices['{DepName}'] must be a non-empty connection string.");
        // Deliberately NOT logging the connection string — Aspire-provisioned strings
        // carry credentials, and test output lands in CI logs/artifacts (§17).
        _output.WriteLine("Redis connection string discovered (redacted).");

        await using var isolation = new RedisScenarioIsolation(DepName, connStr!);

        // ── ROUND 1 ──────────────────────────────────────────────────────────
        await isolation.BeginScenarioAsync(CancellationToken.None);

        await SetKeysAsync(connStr!);
        var count1 = await CountKeysAsync(connStr!);
        _output.WriteLine($"After set 1: DBSIZE={count1}");
        Assert.Equal(3L, count1);

        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("Round 1: EndScenarioAsync completed — FLUSHDB cleared all keys.");

        var countAfterReset1 = await CountKeysAsync(connStr!);
        Assert.Equal(0L, countAfterReset1);

        // ── ROUND 2 — proves the isolation instance is reusable across many resets ──
        await isolation.BeginScenarioAsync(CancellationToken.None);

        await SetKeysAsync(connStr!);
        var count2 = await CountKeysAsync(connStr!);
        Assert.Equal(3L, count2);

        await isolation.EndScenarioAsync(CancellationToken.None);
        _output.WriteLine("Round 2: EndScenarioAsync completed — FLUSHDB cleared all keys again.");

        var countAfterReset2 = await CountKeysAsync(connStr!);
        Assert.Equal(0L, countAfterReset2);

        _output.WriteLine(
            "Reset-proof PASS: FLUSHDB cleared all keys across two independent reset " +
            "round-trips.");
    }
}
