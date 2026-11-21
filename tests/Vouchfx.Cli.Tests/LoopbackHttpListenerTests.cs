using System.Net;
using System.Net.Sockets;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

// Pins the mechanism behind an intermittent "Address already in use" from HttpListener.Dispose()
// in the listener-backed CLI and provider tests, and the helper that avoids it.
//
// The mechanism exists only in the managed HttpListener (Linux and macOS): there Stop() removes the
// endpoint, and a following Dispose() runs the close path again and re-binds the port to remove the
// prefix. The Windows implementation sits on http.sys and has no such re-bind, so the tests that
// depend on a failed bind or a re-bind are guarded by platform.
//
// If the regression test below starts failing, the runtime no longer re-binds in Close: delete it.
public sealed class LoopbackHttpListenerTests
{
    [Fact]
    public void Stop_then_Dispose_throws_when_another_socket_took_the_port_on_the_managed_listener()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var helper = LoopbackHttpListener.Start();
        var listener = helper.Listener;
        var port = helper.Port;
        listener.Stop();

        var thief = new TcpListener(IPAddress.Loopback, port);
        thief.Start();
        try
        {
            var thrown = Record.Exception(() => ((IDisposable)listener).Dispose());
            Assert.True(
                thrown is HttpListenerException,
                "if this starts failing, the runtime no longer re-binds in Close; delete this test. Got: " + (thrown?.ToString() ?? "no exception"));
        }
        finally
        {
            thief.Stop();
        }
    }

    [Fact]
    public void Dispose_releases_the_port()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var helper = LoopbackHttpListener.Start();
        var port = helper.Port;

        helper.Dispose();

        var next = new TcpListener(IPAddress.Loopback, port);
        next.Start();
        next.Stop();
    }

    [Fact]
    public void Single_close_ends_a_pending_GetContextAsync_and_a_second_dispose_is_a_no_op()
    {
        var helper = LoopbackHttpListener.Start();
        var pending = helper.Listener.GetContextAsync();

        helper.Dispose();
        helper.Dispose();

        var ex = Assert.ThrowsAny<Exception>(() => pending.GetAwaiter().GetResult());
        Assert.True(ex is HttpListenerException or ObjectDisposedException or OperationCanceledException, ex.ToString());
    }

    [Fact]
    public void Start_retries_on_a_fresh_port_when_the_first_probe_port_is_taken()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();
        try
        {
            var heldPort = ((IPEndPoint)held.LocalEndpoint).Port;
            var calls = 0;
            using var helper = LoopbackHttpListener.Start(() =>
            {
                calls++;
                if (calls == 1)
                {
                    return heldPort;
                }

                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
                finally { probe.Stop(); }
            });

            Assert.NotEqual(heldPort, helper.Port);
            Assert.True(calls >= 2, "the retry path was not taken");
        }
        finally
        {
            held.Stop();
        }
    }

    [Fact]
    public void Start_reports_every_attempt_when_every_probe_port_is_taken()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();
        try
        {
            var heldPort = ((IPEndPoint)held.LocalEndpoint).Port;
            var ex = Assert.Throws<InvalidOperationException>(() => LoopbackHttpListener.Start(() => heldPort));

            var all = Assert.IsType<AggregateException>(ex.InnerException);
            Assert.Equal(LoopbackHttpListener.MaxAttempts, all.InnerExceptions.Count);
            Assert.All(all.InnerExceptions, inner => Assert.IsType<HttpListenerException>(inner));
        }
        finally
        {
            held.Stop();
        }
    }
}
