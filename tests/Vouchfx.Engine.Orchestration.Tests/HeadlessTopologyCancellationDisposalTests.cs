using System.Diagnostics;
using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class HeadlessTopologyCancellationDisposalTests
{
    [Theory]
    [InlineData(false, 15)]
    [InlineData(true, 30)]
    public async Task Disposal_StopsTheActualHostWithItsIndependentBoundedBudget(bool callerCancelled, int seconds)
    {
        var lifetime = new DeadlineLifetime();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        { DisableDashboard = true, Args = Array.Empty<string>(), AssemblyName = "Vouchfx.Engine.Orchestration.Tests" });
        builder.Services.AddSingleton<IHostLifetime>(lifetime);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = Timeout.InfiniteTimeSpan);
        var topology = HeadlessTopology.ForTestingDisposal(builder.Build());

        if (callerCancelled) await topology.DisposeAfterCallerCancellationAsync();
        else await topology.DisposeAsync();

        Assert.Equal(1, lifetime.Stops);
        Assert.True(lifetime.TokenCanBeCancelled);
        Assert.True(lifetime.DeadlineExpired);
        Assert.InRange(lifetime.Elapsed.TotalSeconds, seconds - 1, seconds + 10);
        await topology.DisposeAsync();
        Assert.Equal(1, lifetime.Stops);
    }

    private sealed class DeadlineLifetime : IHostLifetime
    {
        internal int Stops { get; private set; }
        internal bool TokenCanBeCancelled { get; private set; }
        internal bool DeadlineExpired { get; private set; }
        internal TimeSpan Elapsed { get; private set; }

        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            TokenCanBeCancelled = cancellationToken.CanBeCanceled;
            var stopwatch = Stopwatch.StartNew();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { DeadlineExpired = true; }
            finally { Elapsed = stopwatch.Elapsed; }
        }
    }
}
