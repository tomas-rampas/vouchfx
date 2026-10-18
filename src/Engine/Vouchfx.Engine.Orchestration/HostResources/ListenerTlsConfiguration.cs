using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Vouchfx.Engine.Orchestration.HostResources;

/// <summary>Per-scenario TLS material owned by an engine listener in the Default ALC.</summary>
public sealed class ListenerTlsConfiguration : IDisposable
{
    private readonly X509Certificate2 _server;
    private readonly X509Certificate2Collection _clientRoots = new();
    private readonly X509Certificate2Collection _clientIntermediates = new();
    private readonly X509Certificate2Collection _serverIntermediates = new();

    private ListenerTlsConfiguration(X509Certificate2 server, bool requireClientCertificate)
    {
        _server = server;
        RequireClientCertificate = requireClientCertificate;
    }

    public bool RequireClientCertificate { get; }

    /// <summary>Loads all material before a listener can bind or stage a URL.</summary>
    public static ListenerTlsConfiguration Load(
        string serverCertPath,
        string serverKeyPath,
        string? clientCaCertPath,
        string? password,
        bool requireClientCertificate)
    {
        var keyText = File.ReadAllText(serverKeyPath);
        var encrypted = keyText.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);
        if (encrypted != (password is not null) || password is { Length: 0 })
        {
            throw new InvalidOperationException("The listener key password declaration does not match the PEM key encryption.");
        }

        X509Certificate2 server;
        try
        {
            server = encrypted
                ? X509Certificate2.CreateFromEncryptedPemFile(serverCertPath, password!, serverKeyPath)
                : X509Certificate2.CreateFromPemFile(serverCertPath, serverKeyPath);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The listener certificate and key could not be loaded as a matching PEM identity.");
        }

        // PEM import leaves an ephemeral key that Windows SChannel cannot use for the
        // Kestrel handshake. Transfer it through encrypted PKCS#12 into the default
        // key store, as the existing outbound certificate loader does. The temporary
        // private-key blob is cleared even if import fails.
        try
        {
            var transferPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var pkcs12 = server.Export(X509ContentType.Pkcs12, transferPassword);
            try
            {
                var loadable = new X509Certificate2(
                    pkcs12, transferPassword, X509KeyStorageFlags.DefaultKeySet);
                server.Dispose();
                server = loadable;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
        catch
        {
            server.Dispose();
            throw;
        }

        var config = new ListenerTlsConfiguration(server, requireClientCertificate);
        try
        {
            var serverChain = new X509Certificate2Collection();
            serverChain.ImportFromPem(File.ReadAllText(serverCertPath));
            foreach (var certificate in serverChain.Cast<X509Certificate2>())
            {
                if (!certificate.RawData.AsSpan().SequenceEqual(server.RawData))
                {
                    config._serverIntermediates.Add(certificate);
                }
                else
                {
                    certificate.Dispose();
                }
            }

            if (requireClientCertificate)
            {
                if (clientCaCertPath is null)
                {
                    throw new InvalidOperationException("The mTLS listener requires a client CA certificate.");
                }

                var clientCa = new X509Certificate2Collection();
                clientCa.ImportFromPem(File.ReadAllText(clientCaCertPath));
                if (clientCa.Count == 0)
                {
                    throw new InvalidOperationException("The listener client CA file contains no PEM certificates.");
                }

                foreach (var certificate in clientCa.Cast<X509Certificate2>())
                {
                    if (string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal))
                    {
                        config._clientRoots.Add(certificate);
                    }
                    else
                    {
                        config._clientIntermediates.Add(certificate);
                    }
                }

                if (config._clientRoots.Count == 0)
                {
                    throw new InvalidOperationException("The listener client CA file contains no root certificate.");
                }
            }

            return config;
        }
        catch
        {
            config.Dispose();
            throw;
        }
    }

    internal void Configure(HttpsConnectionAdapterOptions options)
    {
        options.ServerCertificate = _server;
        options.ServerCertificateChain = _serverIntermediates;
        if (RequireClientCertificate)
        {
            options.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            options.ClientCertificateValidation = (certificate, presentedChain, _) =>
                ValidatePeer(certificate, presentedChain);
        }
        else
        {
            options.ClientCertificateMode = ClientCertificateMode.NoCertificate;
        }
    }

    private bool ValidatePeer(X509Certificate2? certificate, X509Chain? presentedChain)
    {
        if (certificate is null)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        chain.ChainPolicy.CustomTrustStore.AddRange(_clientRoots);
        chain.ChainPolicy.ExtraStore.AddRange(_clientIntermediates);
        if (presentedChain is not null)
        {
            foreach (var element in presentedChain.ChainElements.Cast<X509ChainElement>())
            {
                if (!element.Certificate.RawData.AsSpan().SequenceEqual(certificate.RawData))
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }
        }
        return chain.Build(certificate);
    }

    internal static string? VerifiedPeerFingerprint(Microsoft.AspNetCore.Http.HttpContext context, bool mtls)
    {
        if (!mtls || context.Connection.ClientCertificate is not { } certificate)
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    }

    public void Dispose()
    {
        _server.Dispose();
        foreach (var certificate in _serverIntermediates.Cast<X509Certificate2>()) certificate.Dispose();
        foreach (var certificate in _clientIntermediates.Cast<X509Certificate2>()) certificate.Dispose();
        foreach (var certificate in _clientRoots.Cast<X509Certificate2>()) certificate.Dispose();
    }
}
