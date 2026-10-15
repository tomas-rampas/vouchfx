using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Vouchfx.Engine.Runtime.TransportAcceptance.Tests;

internal sealed record AiaRequest(string Path, bool Control, int Status, bool ServerCredentialSetup);
// Pure admission state. An explicit liveness request is always available; native
// server credential setup cannot pre-fetch the issuer before it writes TLS bytes.
// A paired native-client arm must prove that setup refusal has not cached away AIA.
internal sealed class AiaResponseAdmission
{
    private int _serverHandshakeStarted;
    internal bool ServerHandshakeStarted => Volatile.Read(ref _serverHandshakeStarted) != 0;
    internal bool AllowsResponse(bool control) => control || ServerHandshakeStarted;
    internal void BeginServerHandshake() => Interlocked.Exchange(ref _serverHandshakeStarted, 1);
}
internal sealed class OwnedAiaServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, byte[]> _issuers = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Task> _connections = new();
    private readonly ConcurrentDictionary<string, AiaResponseAdmission> _admissions = new(StringComparer.Ordinal);
    private readonly Task _loop;
    internal ConcurrentQueue<AiaRequest> Requests { get; } = new();
    internal string ControlToken { get; } = Guid.NewGuid().ToString("N");
    internal OwnedAiaServer() { _listener.Start(); _loop = AcceptAsync(); }
    internal string IssuerUrl(string arm) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/issuer/{arm}.cer";
    internal void Publish(string url, byte[] issuer) => _issuers[new Uri(url).AbsolutePath] = issuer;
    internal int AutomaticRequests(string url) => Requests.Count(request => request.Path == new Uri(url).AbsolutePath && !request.Control && !request.ServerCredentialSetup);
    internal int ServerCredentialRequests(string url) => Requests.Count(request => request.Path == new Uri(url).AbsolutePath && request.ServerCredentialSetup);
    internal Action DeferAutomaticResponsesUntilServerHandshake(string url)
    {
        var admission = new AiaResponseAdmission();
        if (!_admissions.TryAdd(new Uri(url).AbsolutePath, admission)) { throw new InvalidOperationException("Fixture admission already exists."); }
        return admission.BeginServerHandshake;
    }
    internal async Task ProveAvailableAsync(string url, byte[] expected)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Vouchfx-Control", ControlToken);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var actual = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (!actual.AsSpan().SequenceEqual(expected)) { throw new InvalidDataException("Owned AIA endpoint returned different DER."); }
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _connections.Add(RespondAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException && _stop.IsCancellationRequested) { }
    }
    private async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = await ReadHeaderAsync(stream, _stop.Token).ConfigureAwait(false);
            var lines = header.Split("\r\n", StringSplitOptions.None);
            var parts = lines[0].Split(' ');
            var path = parts.Length > 1 ? parts[1] : "";
            var control = lines.Any(line => line.Equals("X-Vouchfx-Control: " + ControlToken, StringComparison.OrdinalIgnoreCase));
            byte[]? bytes = null;
            _admissions.TryGetValue(path, out var admission);
            var responseAllowed = admission is null || admission.AllowsResponse(control);
            var serverCredentialSetup = !control && !responseAllowed;
            var found = parts.Length > 1 && parts[0] == "GET" && responseAllowed && _issuers.TryGetValue(path, out bytes);
            var body = found ? bytes! : Array.Empty<byte>();
            var status = found ? 200 : 404;
            Requests.Enqueue(new AiaRequest(path, control, status, serverCredentialSetup));
            var text = FormattableString.Invariant($"HTTP/1.1 {status} Result\r\nContent-Length: {body.Length}\r\nContent-Type: application/pkix-cert\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
        }
    }
    internal static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken token, Action<int>? observeBytesRead = null)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16384)
        {
            var read = await stream.ReadAsync(one, token).ConfigureAwait(false);
            observeBytesRead?.Invoke(read);
            if (read == 0) { throw new IOException("Fixture HTTP headers ended early."); }
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) { return Encoding.ASCII.GetString(bytes.ToArray()); }
        }
        throw new InvalidDataException("Fixture HTTP headers exceeded their bound.");
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false); _listener.Stop();
        await _loop.ConfigureAwait(false);
        await Task.WhenAll(_connections).ConfigureAwait(false);
        _stop.Dispose();
    }
}

internal sealed record ClientTrustDecision(bool Trusted, IReadOnlyList<string> ChainErrors);
internal sealed record ServerObservation(bool Authenticated, string? FailureType, SslProtocols? Protocol, Tls12CertificateCapture Received, Tls12CertificateCapture Sent, ClientTrustDecision? ClientTrust, int ApplicationRequestBytesRead);
internal sealed class OwnedTlsServer : IAsyncDisposable
{
    private static readonly string[] s_missingClientErrors = { "RemoteCertificateMissing" };
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task<ServerObservation> _task;
    internal OwnedTlsServer(X509Certificate2 server, X509Certificate2 root, bool clientRequired, Action? beforeHandshakeWrite = null)
    {
        _listener.Start();
        Url = $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _task = ServeAsync(server, root, clientRequired, beforeHandshakeWrite);
    }
    internal string Url { get; }
    internal Task<ServerObservation> Completed => _task.WaitAsync(TimeSpan.FromSeconds(20));
    private async Task<ServerObservation> ServeAsync(X509Certificate2 server, X509Certificate2 root, bool clientRequired, Action? beforeHandshakeWrite)
    {
        using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
        // This fixture serves exactly one connection. Stop listening as soon as it
        // is claimed, so an HTTP retry cannot connect to an unserved accept backlog.
        _listener.Stop();
        var capture = new CapturingStream(client.GetStream(), beforeHandshakeWrite);
        ClientTrustDecision? clientTrust = null;
        using var tls = new SslStream(capture, leaveInnerStreamOpen: true, (_, remote, platformChain, _) =>
        {
            if (!clientRequired) { return true; }
            clientTrust = TrustClient(remote, platformChain, root);
            return clientTrust.Trusted;
        });
        var authenticated = false;
        var applicationRequestBytesRead = 0;
        try
        {
            // A fresh server key/leaf and no additional issuers. Wire capture must confirm
            // the actual chain sent; context construction alone is not that proof.
            var context = SslStreamCertificateContext.Create(server, additionalCertificates: null, offline: true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateContext = context,
                ClientCertificateRequired = clientRequired,
                EnabledSslProtocols = SslProtocols.Tls12,
                CertificateChainPolicy = new X509ChainPolicy { DisableCertificateDownloads = true, RevocationMode = X509RevocationMode.NoCheck },
            }, _stop.Token).ConfigureAwait(false);
            authenticated = true;
            await OwnedAiaServer.ReadHeaderAsync(tls, _stop.Token, read => applicationRequestBytesRead += read).ConfigureAwait(false);
            await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), _stop.Token).ConfigureAwait(false);
            return new ServerObservation(true, null, tls.SslProtocol, capture.Received, capture.Sent, clientTrust, applicationRequestBytesRead);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            return new ServerObservation(authenticated, ex.GetType().Name, authenticated ? tls.SslProtocol : null, capture.Received, capture.Sent, clientTrust, applicationRequestBytesRead);
        }
    }
    private static ClientTrustDecision TrustClient(X509Certificate? remote, X509Chain? supplied, X509Certificate2 root)
    {
        if (remote is not X509Certificate2 leaf) { return new ClientTrustDecision(false, s_missingClientErrors); }
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        if (supplied is not null) { foreach (var item in supplied.ChainElements) { chain.ChainPolicy.ExtraStore.Add(item.Certificate); } }
        var trusted = chain.Build(leaf);
        return new ClientTrustDecision(trusted, chain.ChainStatus.Select(status => status.Status.ToString()).ToArray());
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false); _listener.Stop();
        try { await _task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException && _stop.IsCancellationRequested) { }
        _stop.Dispose();
    }
}
