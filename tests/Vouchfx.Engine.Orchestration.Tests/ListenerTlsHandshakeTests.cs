using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Vouchfx.Engine.Orchestration.HostResources;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>Live loopback handshakes against the two engine-owned Kestrel listeners.</summary>
public sealed class ListenerTlsHandshakeTests
{
    private const string Export = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"tls-test"}}]},"scopeSpans":[{"spans":[{"traceId":"4bf92f3577b34da6a3ce929d0e0e4736","spanId":"00f067aa0ba902b7","name":"accepted"}]}]}]}
        """;

    [Fact]
    public async Task Webhook_RequiresTrustedClientCertificate_AndCapturesExactPeerFingerprint()
    {
        using var trusted = HostedHttpTlsCertificateBed.CreateClientChain("supplied");
        using var foreign = HostedHttpTlsCertificateBed.CreateClientChain("supplied");
        var buffer = new WebhookCaptureBuffer();
        await using var listener = await WebhookListener.StartAsync(buffer, CreateTls(trusted));
        Assert.StartsWith("https://127.0.0.1:", listener.Url, StringComparison.Ordinal);
        Assert.EndsWith("/", listener.Url, StringComparison.Ordinal);

        using (var client = CreateClient(trusted, trusted))
        using (var response = await client.PostAsync(listener.Url + "callback?source=tls", Content("ok")))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var capture = Assert.Single(buffer.Snapshot());
        Assert.Equal("/callback?source=tls", capture.Path);
        Assert.Equal("ok", capture.Body);
        Assert.Equal(ExpectedFingerprint(trusted), capture.PeerCertificateSha256);
        buffer.Clear();

        using (var anonymous = CreateClient(trusted))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                anonymous.PostAsync(listener.Url + "anonymous", Content("rejected")));
            Assert.Empty(buffer.Snapshot());
        }

        using (var wrongCa = CreateClient(trusted, foreign))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                wrongCa.PostAsync(listener.Url + "wrong-ca", Content("rejected")));
            Assert.Empty(buffer.Snapshot());
        }

        await AssertNoPlaintextCapture(listener.Url, buffer.Snapshot);
    }

    [Fact]
    public async Task Otlp_RequiresTrustedClientCertificate_AndCapturesExactPeerFingerprint()
    {
        using var trusted = HostedHttpTlsCertificateBed.CreateClientChain("supplied");
        using var foreign = HostedHttpTlsCertificateBed.CreateClientChain("supplied");
        var buffer = new TraceCaptureBuffer();
        await using var receiver = await OtlpReceiver.StartAsync(buffer, CreateTls(trusted));
        Assert.StartsWith("https://127.0.0.1:", receiver.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1/traces", receiver.Url, StringComparison.Ordinal);

        using (var client = CreateClient(trusted, trusted))
        using (var response = await client.PostAsync(receiver.Url + "/v1/traces", JsonContent()))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var capture = Assert.Single(buffer.Snapshot());
        Assert.Equal("accepted", capture.Name);
        Assert.Equal(ExpectedFingerprint(trusted), capture.PeerCertificateSha256);
        var acceptedCount = buffer.Count;

        using (var anonymous = CreateClient(trusted))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                anonymous.PostAsync(receiver.Url + "/v1/traces", JsonContent()));
            Assert.Equal(acceptedCount, buffer.Count);
        }

        using (var wrongCa = CreateClient(trusted, foreign))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                wrongCa.PostAsync(receiver.Url + "/v1/traces", JsonContent()));
            Assert.Equal(acceptedCount, buffer.Count);
        }

        await AssertNoPlaintextCapture(receiver.Url + "/v1/traces", buffer.Snapshot, acceptedCount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Mtls_RejectsExpiredOrWrongEkuClient_WithoutAppendingCaptures(bool otlp, bool expired)
    {
        using var trusted = HostedHttpTlsCertificateBed.CreateClientChain("supplied");
        var rejectedCertificate = trusted.IssueClientCertificate(expired: expired, wrongEku: !expired);
        Assert.Equal(trusted.Client!.Issuer, rejectedCertificate.Issuer);
        if (expired)
        {
            Assert.True(rejectedCertificate.NotAfter.ToUniversalTime() < DateTime.UtcNow);
        }
        else
        {
            var eku = Assert.Single(rejectedCertificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
            Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(eku.EnhancedKeyUsages.Cast<Oid>()).Value);
        }

        var webhooks = new WebhookCaptureBuffer();
        var traces = new TraceCaptureBuffer();
        await using var webhook = otlp ? null : await WebhookListener.StartAsync(webhooks, CreateTls(trusted));
        await using var receiver = otlp ? await OtlpReceiver.StartAsync(traces, CreateTls(trusted)) : null;
        var url = otlp ? receiver!.Url + "/v1/traces" : webhook!.Url + "rejected";

        // Force presentation: client-side certificate selection must not hide an invalid leaf from the server.
        using var client = CreateClientWithCertificate(trusted, rejectedCertificate);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(url, otlp ? JsonContent() : Content("rejected")));
        Assert.Empty(webhooks.Snapshot());
        Assert.Empty(traces.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mtls_AcceptsDifferentPrincipalFromSameCa_WithDifferentVerifiedFingerprint(bool otlp)
    {
        using var trusted = HostedHttpTlsCertificateBed.CreateClientChain("supplied");
        var otherPrincipal = trusted.IssueClientCertificate();
        Assert.Equal(trusted.Client!.Issuer, otherPrincipal.Issuer);
        var expectedPrincipalFingerprint = ExpectedFingerprint(trusted);
        var otherPrincipalFingerprint = Convert.ToHexString(SHA256.HashData(otherPrincipal.RawData)).ToLowerInvariant();
        Assert.NotEqual(expectedPrincipalFingerprint, otherPrincipalFingerprint);

        var webhooks = new WebhookCaptureBuffer();
        var traces = new TraceCaptureBuffer();
        await using var webhook = otlp ? null : await WebhookListener.StartAsync(webhooks, CreateTls(trusted));
        await using var receiver = otlp ? await OtlpReceiver.StartAsync(traces, CreateTls(trusted)) : null;
        var url = otlp ? receiver!.Url + "/v1/traces" : webhook!.Url + "other-principal";
        using var client = CreateClientWithCertificate(trusted, otherPrincipal);
        using var response = await client.PostAsync(url, otlp ? JsonContent() : Content("accepted"));
        Assert.Equal(otlp ? HttpStatusCode.OK : HttpStatusCode.NoContent, response.StatusCode);
        var actualFingerprint = otlp
            ? Assert.Single(traces.Snapshot()).PeerCertificateSha256
            : Assert.Single(webhooks.Snapshot()).PeerCertificateSha256;
        Assert.Equal(otherPrincipalFingerprint, actualFingerprint);
        Assert.False(string.Equals(expectedPrincipalFingerprint, actualFingerprint, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tls_AcceptsAnonymousHttpsRequest_WithoutPeerIdentity(bool otlp)
    {
        using var trusted = HostedHttpTlsCertificateBed.CreateClientChain("supplied");
        var webhooks = new WebhookCaptureBuffer();
        var traces = new TraceCaptureBuffer();
        await using var webhook = otlp ? null : await WebhookListener.StartAsync(webhooks, CreateTls(trusted, false));
        await using var receiver = otlp ? await OtlpReceiver.StartAsync(traces, CreateTls(trusted, false)) : null;
        var baseUrl = otlp ? receiver!.Url : webhook!.Url;
        Assert.StartsWith("https://127.0.0.1:", baseUrl, StringComparison.Ordinal);
        Assert.True(new Uri(baseUrl).Port > 0);
        if (otlp) { Assert.DoesNotContain("/v1/traces", baseUrl, StringComparison.Ordinal); }
        else { Assert.EndsWith("/", baseUrl, StringComparison.Ordinal); }

        using var client = CreateClient(trusted);
        using var response = await client.PostAsync(otlp ? baseUrl + "/v1/traces" : baseUrl + "callback?source=tls", otlp ? JsonContent() : Content("ok"));
        Assert.Equal(otlp ? HttpStatusCode.OK : HttpStatusCode.NoContent, response.StatusCode);
        if (otlp)
        {
            var capture = Assert.Single(traces.Snapshot());
            Assert.Equal("accepted", capture.Name);
            Assert.Null(capture.PeerCertificateSha256);
        }
        else
        {
            var capture = Assert.Single(webhooks.Snapshot());
            Assert.Equal("/callback?source=tls", capture.Path);
            Assert.Equal("ok", capture.Body);
            Assert.Null(capture.PeerCertificateSha256);
        }
    }

    private static ListenerTlsConfiguration CreateTls(HostedHttpTlsCertificateBed bed, bool requireClientCertificate = true)
    {
        var serverCertPath = Path.Combine(bed.SuiteDirectory, "server.pem");
        var serverKeyPath = Path.Combine(bed.SuiteDirectory, "server-key.pem");
        var clientCaPath = Path.Combine(bed.SuiteDirectory, "client-ca.pem");
        File.WriteAllText(clientCaPath, bed.Root.ExportCertificatePem() + "\n" + bed.Issuer.ExportCertificatePem());
        return ListenerTlsConfiguration.Load(serverCertPath, serverKeyPath, requireClientCertificate ? clientCaPath : null, null, requireClientCertificate);
    }

    private static HttpClient CreateClientWithCertificate(HostedHttpTlsCertificateBed serverBed, X509Certificate2 clientCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    ValidateServerCertificate(serverBed, certificate, errors)
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static HttpClient CreateClient(HostedHttpTlsCertificateBed serverBed, HostedHttpTlsCertificateBed? clientBed = null)
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            ValidateServerCertificate(serverBed, certificate, errors);
        if (clientBed is not null)
        {
            using var pem = X509Certificate2.CreateFromPemFile(
                Path.Combine(clientBed.SuiteDirectory, "client.pem"),
                Path.Combine(clientBed.SuiteDirectory, "client-key.pem"));
            handler.ClientCertificates.Add(new X509Certificate2(pem.Export(X509ContentType.Pkcs12)));
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static bool ValidateServerCertificate(HostedHttpTlsCertificateBed serverBed, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(serverBed.Root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        using var leaf = new X509Certificate2(certificate);
        return chain.Build(leaf);
    }

    private static string ExpectedFingerprint(HostedHttpTlsCertificateBed bed)
    {
        using var certificate = X509Certificate2.CreateFromPemFile(
            Path.Combine(bed.SuiteDirectory, "client.pem"),
            Path.Combine(bed.SuiteDirectory, "client-key.pem"));
        return Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    }

    private static StringContent Content(string body) => new(body, Encoding.UTF8, "text/plain");

    private static StringContent JsonContent() => new(Export, Encoding.UTF8, "application/json");

    private static async Task AssertNoPlaintextCapture<T>(string httpsUrl, Func<IReadOnlyList<T>> snapshot, int expectedCount = 0)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var httpUrl = "http" + httpsUrl[5..];
        try
        {
            using var response = await client.PostAsync(httpUrl, Content("plaintext"));
            Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
        catch (HttpRequestException)
        {
            // A TLS-only socket may close before returning an HTTP response.
        }

        Assert.Equal(expectedCount, snapshot().Count);
    }
}
