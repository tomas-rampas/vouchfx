using System.Security.Cryptography;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

// Pure PEM framing/base64 checks. Bytes are inert markers, not certificates; no import,
// chain build, certificate creation, stores, TLS, listener or network operation occurs.
public sealed class ClientCertificateBundleTests
{
    private static string Block(byte value) => PemEncoding.WriteString("CERTIFICATE", new[] { value });

    [Fact]
    public void Parse_LeafFirstBundle_PreservesOrder()
    {
        var result = ClientCertificateBundle.Parse(Block(1) + "\n" + Block(2) + "\n" + Block(3));
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Select(bytes => Assert.Single(bytes)).ToArray());
    }

    [Fact]
    public void ParseForKeyValidatedLeaf_AcceptsOnlyMatchingFirstCertificate()
    {
        var result = ClientCertificateBundle.Parse(Block(1) + "\n" + Block(2), new byte[] { 1 });
        Assert.Equal(2, result.Count);
        Assert.Throws<CryptographicException>(() => ClientCertificateBundle.Parse(Block(2) + "\n" + Block(1), new byte[] { 1 }));
    }

    [Fact]
    public void Parse_SingleCertificateWithUnrelatedPemLabel_PreservesLegacyPairFile()
    {
        var result = ClientCertificateBundle.Parse(Block(1) + "\n" + PemEncoding.WriteString("PRIVATE KEY", new byte[] { 9 }));
        Assert.Equal(new byte[] { 1 }, Assert.Single(result));
    }

    [Fact]
    public void Parse_DuplicateCertificate_Refuses()
    {
        var error = Assert.Throws<CryptographicException>(() => ClientCertificateBundle.Parse(Block(1) + "\n" + Block(1)));
        Assert.Contains("duplicate certificate", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-----BEGIN CERTIFICATE-----\ninvalid!\n-----END CERTIFICATE-----")]
    [InlineData("-----BEGIN CERTIFICATE-----\nAA==")]
    public void Parse_MissingOrMalformedCertificate_Refuses(string pem)
    {
        Assert.Throws<CryptographicException>(() => ClientCertificateBundle.Parse(pem));
    }

    [Fact]
    public void Parse_ValidLeafAndMalformedTail_RefusesRatherThanDiscardingTail()
    {
        Assert.Throws<CryptographicException>(() => ClientCertificateBundle.Parse(Block(1) + "\n-----BEGIN CERTIFICATE-----\nbroken"));
    }

    [Fact]
    public void Parse_Exactly32Certificates_Accepts()
    {
        Assert.Equal(32, ClientCertificateBundle.Parse(string.Join("\n", Enumerable.Range(1, 32).Select(value => Block((byte)value)))).Count);
    }

    [Fact]
    public void Parse_33Certificates_Refuses()
    {
        Assert.Throws<CryptographicException>(() => ClientCertificateBundle.Parse(string.Join("\n", Enumerable.Range(1, 33).Select(value => Block((byte)value)))));
    }

    [Fact]
    public void Parse_OverBudgetPem_RefusesBeforeParsing()
    {
        Assert.Throws<CryptographicException>(() => ClientCertificateBundle.Parse(new string(' ', ClientCertificateBundle.MaximumPemCharacters + 1)));
    }
}
