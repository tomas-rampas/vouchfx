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
// prefix. The Windows implementation sits on http.sys and has no such re-bind, so the throwing
// assertion is guarded by platform.
public sealed class LoopbackHttpListenerTests
{
    [Fact]
    public void Stop_then_Dispose_throws_when_another_socket_took_the_port_on_the_managed_listener()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        listener.Stop();

        var thief = new TcpListener(IPAddress.Loopback, port);
        thief.Start();
        try
        {
            Assert.Throws<HttpListenerException>(() => ((IDisposable)listener).Dispose());
        }
        finally
        {
            thief.Stop();
        }
    }

    [Fact]
    public void Single_close_does_not_throw_when_another_socket_takes_the_port_afterwards()
    {
        var helper = LoopbackHttpListener.Start();
        var port = helper.Port;

        helper.Dispose();

        var thief = new TcpListener(IPAddress.Loopback, port);
        thief.Start();
        thief.Stop();
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
    public void Start_retries_on_a_fresh_port_when_the_probe_port_is_taken()
    {
        // Hold a port, then prove a started listener never lands on it.
        var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();
        try
        {
            var heldPort = ((IPEndPoint)held.LocalEndpoint).Port;
            using var helper = LoopbackHttpListener.Start();
            Assert.NotEqual(heldPort, helper.Port);
        }
        finally
        {
            held.Stop();
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }
}
