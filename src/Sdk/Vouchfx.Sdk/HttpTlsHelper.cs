namespace Vouchfx.Sdk;

/// <summary>
/// Supplies the HTTP TLS helper for engines 1.0.2 and later. The separately named
/// source coexists with the frozen v1 <see cref="SecurityHelper"/> source.
/// </summary>
public static class HttpTlsHelper
{
    /// <summary>
    /// Full source for the shared SocketsHttpHandler configuration helper. A provider
    /// using this helper must declare MinEngineVersion 1.0.2 or later.
    /// </summary>
    public const string Source = """
        static class HttpTls_Helpers
        {
            public static void ConfigureHandler(
                Vouchfx.Engine.Abstractions.Security.ISecurityConfigurationAccessor security,
                string targetName,
                System.Net.Http.SocketsHttpHandler handler)
            {
                var configuration = security?.For(targetName);
                if (configuration == null) { return; }

                var certificates = configuration.Certificates;
                var profile = configuration.Profile;
                if (string.Equals(profile, "mtls", System.StringComparison.Ordinal))
                {
                    var leaf = certificates?.ClientCertificate;
                    if (leaf == null)
                    {
                        throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(
                            "target '" + targetName + "': profile 'mtls' requires a client identity, but no " +
                            "'clientCert'/'clientKey' pair resolved for this target. Declare both, or use " +
                            "'profile: tls' to present no client identity.");
                    }

                    var intermediates = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
                    foreach (var candidate in certificates.ClientCertificateIntermediates)
                    {
                        intermediates.Add(candidate);
                    }
                    // Certificates are borrowed from the scenario-owned accessor. Offline
                    // context construction uses supplied candidates without downloading links.
                    handler.SslOptions.ClientCertificateContext =
                        System.Net.Security.SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
                }
                else if (string.Equals(profile, "tls", System.StringComparison.Ordinal))
                {
                    if (certificates != null &&
                        (certificates.ClientCertificatePath != null || certificates.ClientKeyPath != null))
                    {
                        throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(
                            "target '" + targetName + "': profile 'tls' presents no client identity, but " +
                            "'clientCert'/'clientKey' is declared for it. Use 'profile: mtls' to present the " +
                            "declared client certificate, or remove both fields.");
                    }
                }
                else
                {
                    throw new Vouchfx.Engine.Abstractions.Security.SecurityMaterialException(
                        "target '" + targetName + "': security profile '" + profile + "' has no defined " +
                        "HTTPS client behaviour in this engine version. The HTTP step families " +
                        "(http.rest, http.soap, metrics-assert.prometheus) implement 'tls' and 'mtls'.");
                }

                // Set before the first handshake, rather than only on a callback-owned
                // rebuilt chain. Keep the HTTP transport's default no-revocation policy.
                var policy = new System.Security.Cryptography.X509Certificates.X509ChainPolicy
                {
                    DisableCertificateDownloads = true,
                    RevocationMode = handler.SslOptions.CertificateRevocationCheckMode,
                };
                policy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                handler.SslOptions.CertificateChainPolicy = policy;

                if (certificates != null && certificates.CaCertificate != null)
                {
                    // A declared CA is a pin on every verdict, including platform success.
                    // The accessor retains its hostname, serverAuth and custom-root checks.
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, chain, errors) =>
                    {
                        if (remote is System.Security.Cryptography.X509Certificates.X509Certificate2 typed)
                        {
                            return certificates.TrustsRemoteCertificate(typed, chain, errors);
                        }
                        if (remote == null)
                        {
                            return certificates.TrustsRemoteCertificate(null, chain, errors);
                        }
                        var copy = new System.Security.Cryptography.X509Certificates.X509Certificate2(remote);
                        try { return certificates.TrustsRemoteCertificate(copy, chain, errors); }
                        finally { copy.Dispose(); }
                    };
                }
            }
        }
        """ + "\n";
}
