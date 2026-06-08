// S02-B-01 — Provider-closure memory leak test (the permanent M1 CI gate).
//
// Runs MemoryProbe.RunClosureAsync with a modest iteration count (200) so the unit
// suite stays fast whilst still exercising the full transitive dependency closure of
// every Core provider's canonical client library (Npgsql, Confluent.Kafka,
// MongoDB.Driver, StackExchange.Redis, HttpClient).
//
// For the authoritative 5,000-iteration run, use the standalone harness:
//   dotnet run -c Release --project tests/Platform.Engine.Compilation.MemoryHarness \
//       -- --mode closure --iterations 5000
using System.Threading.Tasks;
using Platform.Engine.Compilation.MemoryHarness;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S02-B-01 smoke tests for <see cref="MemoryProbe.RunClosureAsync"/>.
/// </summary>
/// <remarks>
/// These tests run with intra-assembly parallelism disabled (see <c>AssemblyInfo.cs</c>)
/// because GC heap measurements and collectible-assembly counts are process-global.
/// Cross-assembly parallelism (i.e. with other test DLLs) is not an issue as each DLL
/// runs in its own process.
/// </remarks>
public sealed class ClosureMemoryProbeTests
{
    private readonly ITestOutputHelper _output;

    public ClosureMemoryProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Runs 200 closure load-unload cycles and asserts that the managed heap delta is
    /// below the 2 MB threshold and that all collectible
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> instances were reclaimed.
    /// </summary>
    /// <remarks>
    /// 200 iterations keeps this test well under a few seconds on typical developer
    /// hardware whilst exercising enough cycles to detect a steady-state leak in any
    /// of the canonical client singletons.  A leak of even one unreclaimed assembly per
    /// iteration would accumulate a visible delta well before the 200th cycle.
    /// The <see cref="HeapMeasurement.SingletonsReset"/> list is logged so reviewers can
    /// audit which pinners are reset by <see cref="SingletonReset.ResetAll"/>.
    /// </remarks>
    [Fact]
    public async Task RunClosureAsync_200Iterations_PassesAndReclaims()
    {
        const int iterations = 200;
        const long threshold = 2_000_000; // 2 MB

        var result = await MemoryProbe.RunClosureAsync(iterations: iterations, thresholdBytes: threshold);

        _output.WriteLine(
            $"ClosureProbe smoke: mode={result.Mode}, iterations={result.Iterations}, " +
            $"baseline={result.BaselineBytes:N0} B, " +
            $"post={result.PostBytes:N0} B, " +
            $"netDelta={result.NetDeltaBytes:+#;-#;0} B " +
            $"({result.NetDeltaBytes / 1024.0:F1} KB), " +
            $"threshold={result.ThresholdBytes:N0} B, " +
            $"passed={result.Passed}, " +
            $"collectibleBefore={result.CollectibleBefore}, " +
            $"collectibleAfter={result.CollectibleAfter}, " +
            $"contextReclaimed={result.ContextReclaimed}.");

        _output.WriteLine($"Mode: {result.Mode}");
        _output.WriteLine($"SingletonsReset ({result.SingletonsReset.Count}):");
        foreach (var entry in result.SingletonsReset)
            _output.WriteLine($"  • {entry}");

        // Primary gate: heap delta below threshold.
        Assert.True(result.Passed,
            $"Net heap delta {result.NetDeltaBytes:N0} B exceeded threshold " +
            $"{result.ThresholdBytes:N0} B after {result.Iterations} closure load-unload cycles. " +
            "This indicates that a canonical client singleton is pinning objects across the " +
            "collectible AssemblyLoadContext boundary (memory leak).");

        // Secondary gate: collectible assembly count returned to baseline.
        Assert.True(result.ContextReclaimed,
            $"Collectible assembly count after GC ({result.CollectibleAfter}) exceeded the " +
            $"pre-workload baseline ({result.CollectibleBefore}). " +
            "Unloaded ALCs were not fully reclaimed by the GC. " +
            "Check SingletonReset.ResetAll() for missing pinner resets.");

        // Mode guard: confirm the closure mode was actually used.
        Assert.Equal(MemoryProbe.ClosureMode, result.Mode);

        // Singletons must have been documented (non-empty list).
        Assert.NotEmpty(result.SingletonsReset);
    }
}
