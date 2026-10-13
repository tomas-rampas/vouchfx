using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class SecuredEndpointProbeTlsOptionsTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public void AnonymousOptions_PreserveHostnameDefaultsAndOfflineServerPolicy(string host)
    {
        var options = SecuredEndpointProbe.CreateAuthenticationOptions(host, clientContext: null);
        Assert.Equal(host, options.TargetHost);
        Assert.Equal(SslProtocols.None, options.EnabledSslProtocols);
        Assert.Null(options.ClientCertificateContext);
        Assert.Null(options.ClientCertificates);
        Assert.Null(options.RemoteCertificateValidationCallback);
        var policy = Assert.IsType<X509ChainPolicy>(options.CertificateChainPolicy);
        Assert.True(policy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, policy.RevocationMode);
        Assert.Equal(X509ChainTrustMode.System, policy.TrustMode);
        Assert.Equal(X509VerificationFlags.NoFlag, policy.VerificationFlags);
        Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(policy.ApplicationPolicy.Cast<System.Security.Cryptography.Oid>()).Value);
        Assert.Empty(policy.ExtraStore);
        Assert.Empty(policy.CustomTrustStore);
    }
}
