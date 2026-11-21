// A loopback HttpListener that is started on a probed free port and ended with exactly one Close().
//
// WHY ONE CLOSE
// -------------
// On the managed (non-Windows) HttpListener, Stop() removes the listener's endpoint and frees the
// port. Close()/Dispose() only returns early once the state is Closed, so after a Stop() it runs
// the close path again, which re-binds the port to remove the prefix. If any other socket took the
// port in between, that bind fails and Dispose() throws HttpListenerException ("Address already
// in use"). Ending the listener with a single Close() and no preceding Stop() never takes that
// path. Do not add a Stop() ahead of Dispose()/Close(), and do not put `using` on a listener that
// is also closed explicitly.
//
// WHY A RETRY
// -----------
// A free port is probed with a TcpListener that is released before HttpListener binds it, so
// another process can take the port in between and Start() then fails. A fresh probe port is tried
// up to MaxAttempts times before the failure is reported.
using System.Net;
using System.Net.Sockets;

namespace Vouchfx.TestSupport;

/// <summary>
/// An <see cref="HttpListener"/> bound to a loopback port and closed with exactly one
/// <see cref="HttpListener.Close"/>. See the file header for why a preceding Stop() is avoided.
/// </summary>
public sealed class LoopbackHttpListener : IDisposable
{
    /// <summary>Number of fresh probe ports tried before a bind failure is reported.</summary>
    public const int MaxAttempts = 5;

    private int _closed;

    private LoopbackHttpListener(HttpListener listener, int port)
    {
        Listener = listener;
        Port = port;
    }

    /// <summary>The started listener.</summary>
    public HttpListener Listener { get; }

    /// <summary>The loopback port the listener is bound to.</summary>
    public int Port { get; }

    /// <summary>The listener prefix, <c>http://127.0.0.1:{port}/</c>.</summary>
    public string Prefix => $"http://127.0.0.1:{Port}/";

    /// <summary>The base URL without a trailing slash.</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>
    /// Starts a listener on a freshly probed loopback port, retrying on a new probe port when the
    /// bind collides with another socket.
    /// </summary>
    public static LoopbackHttpListener Start()
    {
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var port = ProbeFreePort();
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                return new LoopbackHttpListener(listener, port);
            }
            catch (HttpListenerException ex)
            {
                last = ex;
                // The failed attempt owns nothing worth reporting; release it and try a new port.
                try { listener.Close(); } catch (HttpListenerException) { }
            }
        }

        throw new InvalidOperationException(
            $"Could not bind an HttpListener on a free loopback port after {MaxAttempts} attempts.", last);
    }

    /// <summary>Closes the listener once. Later calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Listener.Close();
        }
    }

    private static int ProbeFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }
}
