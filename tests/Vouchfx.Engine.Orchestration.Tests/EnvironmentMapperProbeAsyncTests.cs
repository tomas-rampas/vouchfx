// Vouchfx.Engine.Orchestration.Tests — EnvironmentMapper.ProbeAsync (M4 fix, fix round 3).
//
// The B1 discriminator (EnvironmentMapper.ApplyTcpHealthCheck's connect/read logic) had, until
// this file, exactly two tests: the two Docker-trait tests in
// EnvironmentMapperTcpHealthCheckDockerTests.cs, which require a working DCP and live in a CI
// job that runs `continue-on-error: true` (issue #350) — meaning a regression in the
// discriminator itself could land, and stay landed, with zero CI signal. EnvironmentMapper.
// ProbeAsync(host, port, ct) was extracted from ApplyTcpHealthCheck's health-check delegate for
// exactly this reason: it takes a plain host/port pair rather than an Aspire-resolved endpoint,
// so it can be exercised against an ordinary loopback TcpListener, with no Docker/DCP/Aspire
// topology at all — the same pattern already established in this repo for a raw-socket test
// double (HttpRestExecutionTests.cs:229's TcpListener fallback server, HttpRestCaptureTests.cs's
// sibling use of the same pattern).
//
// The three tests below exercise the three branches ProbeAsync's own remarks enumerate:
//   • accept-then-close (zero bytes, then EOF)      → Unhealthy
//   • accept-and-hold (nothing written, stays open) → Healthy (the "does not speak first" case)
//   • accept-then-write (>0 bytes immediately)      → Healthy (the "speaks first" case, e.g. SMTP)

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Unit coverage (no Docker/DCP) for <see cref="EnvironmentMapper.ProbeAsync"/> — the B1
/// discriminator extracted from <c>ApplyTcpHealthCheck</c>'s health-check delegate (M4 fix, fix
/// round 3) precisely so this logic has a test that runs in the ordinary CI job.
/// </summary>
public sealed class EnvironmentMapperProbeAsyncTests
{
    private const string Loopback = "127.0.0.1";

    /// <summary>
    /// Accept-then-close: the server accepts the connection and closes it immediately without
    /// writing anything — the exact signature DCP's host-published proxy produced pre-fix when
    /// nothing was listening behind it (zero bytes read, connection closed). Must report
    /// <see cref="HealthStatus.Unhealthy"/>.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_AcceptThenClose_ReportsUnhealthy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            // Accept, then let the 'using' dispose close it immediately — zero bytes written.
        });

        try
        {
            var result = await EnvironmentMapper.ProbeAsync(Loopback, port, CancellationToken.None);

            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Contains("zero bytes read", result.Description, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            listener.Stop();
            await acceptTask;
        }
    }

    /// <summary>
    /// Accept-and-hold: the server accepts the connection and holds it open, writing nothing —
    /// the live-backend signature for the overwhelming majority of TCP protocols, which do not
    /// speak first. Must report <see cref="HealthStatus.Healthy"/> once the probe's internal
    /// 1-second read bound elapses with the connection still open (this test therefore takes
    /// slightly over 1 second to run).
    /// </summary>
    [Fact]
    public async Task ProbeAsync_AcceptAndHold_ReportsHealthy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var holdCts = new CancellationTokenSource();
        var acceptTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.Infinite, holdCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Test cleanup below cancels this once ProbeAsync has returned.
            }
        });

        try
        {
            var result = await EnvironmentMapper.ProbeAsync(Loopback, port, CancellationToken.None);

            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            holdCts.Cancel();
            listener.Stop();
            await acceptTask;
        }
    }

    /// <summary>
    /// Accept-then-write: the server accepts the connection and writes a greeting immediately,
    /// unprompted — a server-speaks-first protocol (e.g. SMTP's <c>220 ...</c> banner, SSH's
    /// version string). Must report <see cref="HealthStatus.Healthy"/> without waiting for the
    /// probe's read bound to elapse.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_AcceptThenWrite_ReportsHealthy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            var stream = client.GetStream();
            var greeting = Encoding.ASCII.GetBytes("220 test-smtp ready\r\n");
            await stream.WriteAsync(greeting).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            // Keep the socket open a moment so the probe's read has time to land before this
            // task's 'using' disposes the accepted client out from under it.
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        });

        try
        {
            var result = await EnvironmentMapper.ProbeAsync(Loopback, port, CancellationToken.None);

            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            listener.Stop();
            await acceptTask;
        }
    }
}
