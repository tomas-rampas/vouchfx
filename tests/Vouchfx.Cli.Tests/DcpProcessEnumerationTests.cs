using System.ComponentModel;
using System.Diagnostics;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class DcpProcessEnumerationTests
{
    [Fact]
    public void EnumerationRace_RetriesWithoutClaimingAnEmptyInventory()
    {
        var process = new TrackedProcess();
        var attempts = 0;

        var result = EnvironmentTopologyDurabilityDockerTests.ReadDcpSnapshot(
            () => ++attempts == 1 ? throw new Win32Exception("Process enumeration raced an exit.") : new Process[] { process },
            _ => "123:456");

        Assert.Equal(new[] { "123:456" }, result);
        Assert.Equal(2, attempts);
        Assert.True(process.WasDisposed);
    }

    [Fact]
    public void IdentityReadRace_DiscardsPartialSnapshotAndDisposesUnvisitedCandidates()
    {
        var first = new[] { new TrackedProcess(), new TrackedProcess(), new TrackedProcess() };
        var retry = new[] { new TrackedProcess(), new TrackedProcess() };
        var attempts = 0;
        var reads = 0;

        var result = EnvironmentTopologyDurabilityDockerTests.ReadDcpSnapshot(
            () => ++attempts == 1 ? first : retry,
            process =>
            {
                reads++;
                if (ReferenceEquals(process, first[1])) throw new Win32Exception("Birth read raced an exit.");
                return ReferenceEquals(process, first[0]) ? "stale-partial:1"
                    : ReferenceEquals(process, retry[0]) ? "owned:2" : "ambient:3";
            });

        Assert.Equal(new[] { "ambient:3", "owned:2" }, result);
        Assert.Equal(2, attempts);
        Assert.Equal(4, reads);
        Assert.All(first.Concat(retry), process => Assert.True(process.WasDisposed));
    }

    [Fact]
    public void PersistentIdentityFailure_ThrowsOriginalErrorAndDisposesEverySnapshot()
    {
        var snapshots = new List<TrackedProcess>();
        var failure = new Win32Exception("Process inventory is unavailable.");

        var error = Assert.Throws<Win32Exception>(() => EnvironmentTopologyDurabilityDockerTests.ReadDcpSnapshot(
            () =>
            {
                var process = new TrackedProcess();
                snapshots.Add(process);
                return new Process[] { process };
            },
            _ => throw failure));

        Assert.Same(failure, error);
        Assert.Equal(3, snapshots.Count);
        Assert.All(snapshots, process => Assert.True(process.WasDisposed));
    }

    [Fact]
    public void UnrelatedFailure_PropagatesWithoutRetryAndStillDisposesCandidates()
    {
        var process = new TrackedProcess();
        var attempts = 0;
        var failure = new InvalidOperationException("Unrelated snapshot failure.");

        var error = Assert.Throws<InvalidOperationException>(() => EnvironmentTopologyDurabilityDockerTests.ReadDcpSnapshot(
            () => { attempts++; return new Process[] { process }; },
            _ => throw failure));

        Assert.Same(failure, error);
        Assert.Equal(1, attempts);
        Assert.True(process.WasDisposed);
    }

    private sealed class TrackedProcess : Process
    {
        internal bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
