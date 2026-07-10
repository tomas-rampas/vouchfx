using System.Globalization;
using Vouchfx.Engine.Orchestration;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated reliability tests for S02-A-01 (health-gate hardening and flake budget).
/// Run with: <c>dotnet test --filter "requires=docker"</c>.
/// Excluded from the unit-CI job via: <c>dotnet test --filter "requires!=docker"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The primary assertion is <b>zero engine-attributable flakes</b>: every iteration must reach
/// healthy without error.  A flake here means a defect in the health-gate or topology wiring,
/// not a transient infrastructure issue; the test is designed to surface the former.
/// </para>
/// <para>
/// The run count defaults to 50 (the CI gate) and can be overridden via the environment variable
/// <c>VOUCHFX_RELIABILITY_RUNS</c> for local development (e.g. set to 5 for a fast smoke-check).
/// </para>
/// </remarks>
public sealed class HealthGateReliabilityTests
{
    private readonly ITestOutputHelper _output;

    public HealthGateReliabilityTests(ITestOutputHelper output) => _output = output;

    // The short name of this test assembly — the one whose AssemblyInfo carries dcpclipath.
    private const string AppHostAssemblyName = "Vouchfx.Engine.Orchestration.Tests";

    // Per-run startup timeout — set generously so genuine image-pull latency on a cold CI agent
    // does not mask a real hang.  A real hang (e.g. wrong WaitFor target) will surface within
    // this window.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Starts the stub topology N consecutive times and asserts that every run reaches healthy
    /// with zero engine-attributable flakes.  N defaults to 50 (CI gate) and is overridable via
    /// <c>VOUCHFX_RELIABILITY_RUNS</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each iteration also validates that <see cref="StubTopology.Timing"/> is populated and
    /// that all three durations are non-negative.  The min / median / max / mean of
    /// <see cref="StartupTiming.Total"/> are logged so the figures can be compared against the
    /// &lt;90 s diagnostic target (the stub has only one service + one dependency, so it should
    /// be well within this window).  The &lt;90 s figure is <b>not</b> hard-asserted here because
    /// image-pull latency on a cold CI agent is outside the engine's control.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "docker")]
    public async Task HealthGate_ConsecutiveStartups_AllReachHealthyWithZeroFlakes()
    {
        int totalRuns = int.TryParse(
            Environment.GetEnvironmentVariable("VOUCHFX_RELIABILITY_RUNS"),
            out var parsed) ? parsed : 50;

        _output.WriteLine(
            $"Starting reliability run: {totalRuns} consecutive topology startups.");
        _output.WriteLine(
            $"Run timeout per iteration: {RunTimeout.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s.");
        _output.WriteLine(string.Empty);

        var passed = 0;
        var failures = new List<(int Run, Exception Exception)>();
        var totalDurations = new List<double>(totalRuns);

        for (int run = 1; run <= totalRuns; run++)
        {
            Exception? runException = null;

            try
            {
                await using var topology = await StubTopology.StartAsync(
                    appHostAssemblyName: AppHostAssemblyName,
                    startupTimeout: RunTimeout);

                // Reaching this line proves both health gates passed — the topology is fully healthy.
                // Validate that timing was captured and that the arithmetic relationships hold:
                //   • DatabaseReady must not exceed Total (gate 1 completes before gate 2).
                //   • ServiceReady == Total - DatabaseReady (it is the gap between the two gates).
                // These assertions exercise the real timing arithmetic rather than the trivially
                // true ">= TimeSpan.Zero" check.
                var timing = topology.Timing;
                Assert.True(
                    timing.DatabaseReady <= timing.Total,
                    $"Run {run}: Timing.DatabaseReady ({timing.DatabaseReady}) must not exceed " +
                    $"Timing.Total ({timing.Total}); gate 1 cannot take longer than the full startup.");
                Assert.Equal(
                    timing.Total - timing.DatabaseReady,
                    timing.ServiceReady);

                totalDurations.Add(timing.Total.TotalSeconds);

                _output.WriteLine(
                    $"Run {run:D2}/{totalRuns}: PASS  " +
                    $"total={timing.Total.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s  " +
                    $"db={timing.DatabaseReady.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s  " +
                    $"svc={timing.ServiceReady.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s");

                passed++;
            }
            catch (Exception ex)
            {
                runException = ex;
                failures.Add((run, ex));
                _output.WriteLine(
                    $"Run {run:D2}/{totalRuns}: FAIL  {ex.GetType().Name}: {ex.Message}");
            }

            _ = runException; // suppress unused-variable warning if exception is null
        }

        _output.WriteLine(string.Empty);

        // Log summary statistics for the startup time diagnostic.
        if (totalDurations.Count > 0)
        {
            totalDurations.Sort();
            var min = totalDurations[0];
            var max = totalDurations[totalDurations.Count - 1];
            var mean = totalDurations.Average();
            var median = totalDurations.Count % 2 == 0
                ? (totalDurations[(totalDurations.Count / 2) - 1] + totalDurations[totalDurations.Count / 2]) / 2.0
                : totalDurations[totalDurations.Count / 2];

            _output.WriteLine("=== Startup time statistics (seconds) ===");
            _output.WriteLine($"  Min    : {min.ToString("F1", CultureInfo.InvariantCulture)}s");
            _output.WriteLine($"  Median : {median.ToString("F1", CultureInfo.InvariantCulture)}s");
            _output.WriteLine($"  Mean   : {mean.ToString("F1", CultureInfo.InvariantCulture)}s");
            _output.WriteLine($"  Max    : {max.ToString("F1", CultureInfo.InvariantCulture)}s");
            _output.WriteLine($"  Samples: {totalDurations.Count}");
            _output.WriteLine(string.Empty);
        }

        if (failures.Count > 0)
        {
            _output.WriteLine("=== Failed runs ===");
            foreach (var (failRun, ex) in failures)
            {
                _output.WriteLine($"  Run {failRun:D2}: {ex.GetType().Name}: {ex.Message}");
            }

            _output.WriteLine(string.Empty);
        }

        _output.WriteLine(
            $"Result: {passed}/{totalRuns} passed ({failures.Count} failed).");

        // Zero engine-attributable flakes: every run must reach healthy.
        Assert.Equal(totalRuns, passed);
    }
}
