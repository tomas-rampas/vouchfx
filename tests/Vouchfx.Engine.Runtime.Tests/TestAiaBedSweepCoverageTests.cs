// #419 — the AIA bed's teardown sweep covers the intermediate it minted.
//
// The bed's leaf advertises a caIssuers URL and its issuer is absent from the bed, which is the
// whole fixture. The earlier reasoning — "not the bed's to remove" — held only while nobody
// fetched it; a chain builder that has not had downloads disabled fetches the intermediate over
// that URL and CryptoAPI caches what it fetched. The bed minted it, so it knows the thumbprint,
// so the copy IS the bed's to remove.
//
// Non-Docker: no listener is started and nothing is installed; the bed is constructed against a
// URL that is never dereferenced here.
using System.Security.Cryptography.X509Certificates;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class TestAiaBedSweepCoverageTests
{
    private const string UnusedCaIssuersUrl = "http://127.0.0.1:1/never-fetched.cer";

    [Fact]
    [Trait("requires", "none")]
    public void BedCapturesTheIntermediateThumbprintAtMintTime()
    {
        using var bed = TestCertificateAuthority.CreateAuthorityInfoAccessBed(UnusedCaIssuersUrl);

        // A thumbprint is 40 hex characters (SHA-1 over the DER). Capturing it after the
        // certificate was disposed would yield empty, which is what this rejects.
        Assert.Equal(40, bed.IntermediateThumbprint.Length);
        Assert.True(
            bed.IntermediateThumbprint.All(Uri.IsHexDigit),
            $"Expected a hexadecimal thumbprint, got '{bed.IntermediateThumbprint}'.");
    }

    [Fact]
    [Trait("requires", "none")]
    public void IntermediateThumbprintIsNeitherTheRootsNorTheLeafs()
    {
        using var bed = TestCertificateAuthority.CreateAuthorityInfoAccessBed(UnusedCaIssuersUrl);

        // Three distinct certificates, so three distinct sweep entries — a regression that
        // captured the wrong one would still be 40 hex characters.
        Assert.NotEqual(bed.RootCertificate.Thumbprint, bed.IntermediateThumbprint);
        Assert.NotEqual(bed.LeafWithAuthorityInfoAccess.Thumbprint, bed.IntermediateThumbprint);
    }

    [Fact]
    [Trait("requires", "none")]
    public void CapturedIdentityIsTheLeafsIssuerAndNotTheRoot()
    {
        using var bed = TestCertificateAuthority.CreateAuthorityInfoAccessBed(UnusedCaIssuersUrl);

        // THE tie. The bed captures subject and thumbprint from one certificate in one statement,
        // so a subject equal to the leaf's issuer DN is what says the thumbprint beside it came
        // from the missing link — not from the root, the leaf, or any other certificate the bed
        // minted. Without this, a regression that captured some OTHER bed-minted thumbprint would
        // still be 40 hex characters and still differ from the root's and the leaf's.
        Assert.Equal(bed.LeafWithAuthorityInfoAccess.Issuer, bed.IntermediateSubject);

        // And the missing link really is a link: issued by the bed's root, distinct from it, and
        // carrying this process's token as every authority subject must.
        Assert.NotEqual(bed.RootCertificate.Subject, bed.IntermediateSubject);
        Assert.Equal(bed.RootCertificate.Subject, bed.RootCertificate.Issuer);
        Assert.Contains(
            TestCertificateAuthority.ProcessTokenMarker, bed.IntermediateSubject, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires", "none")]
    public void TheAbsentIntermediateIsNotReachableFromTheBed()
    {
        using var bed = TestCertificateAuthority.CreateAuthorityInfoAccessBed(UnusedCaIssuersUrl);

        // The fixture's whole premise: the bed exposes the intermediate's IDENTITY (enough for the
        // teardown sweep) and never the certificate. A chain built from what the bed holds cannot
        // reach the root, which is why a builder with downloads enabled goes to the caIssuers URL.
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(bed.RootCertificate);

        // NOTE: no VerificationFlags are relaxed. X509VerificationFlags.AllFlags would suppress
        // PartialChain itself and make this build SUCCEED — measured, when this test was first
        // written that way.
        var built = chain.Build(bed.LeafWithAuthorityInfoAccess);

        Assert.False(built);
        Assert.NotEmpty(chain.ChainStatus);
    }
}
