// #374 — per-process unique CA and intermediate subjects.
//
// Windows' CryptoAPI caches peer-supplied intermediates into the CA store keyed by SUBJECT.
// While every run minted its authorities under constant common names, copies from prior runs
// accumulated and eventually broke chain building deterministically — a failure that reads as
// environmental and cost about an hour of diagnosis, twice. These tests pin the fix and,
// equally, pin what the fix must NOT touch.
//
// Non-Docker: certificates are generated in-process and nothing is installed anywhere.
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class TestCertificateAuthorityProcessTokenTests
{
    [Fact]
    [Trait("requires", "none")]
    public void ProcessTokenIsEightHexadecimalCharacters()
    {
        Assert.Equal(8, TestCertificateAuthority.ProcessToken.Length);
        Assert.True(
            TestCertificateAuthority.ProcessToken.All(Uri.IsHexDigit),
            $"Expected hexadecimal, got '{TestCertificateAuthority.ProcessToken}'.");
    }

    /// <summary>
    /// Pins the SHAPE of the marker — a space then exactly the eight hex digits — because the
    /// marker is the needle a DESTRUCTIVE scan uses.
    /// </summary>
    /// <remarks>
    /// Asserting <c>" " + ProcessToken == ProcessTokenMarker</c> would be tautological against a
    /// property whose body is that expression. The shape is not: if the marker ever loses its
    /// token (the failure mode the old <c>static readonly</c> field had, where a reordering left
    /// it as a bare <c>" "</c>) or gains a wider separator, it stops being nine characters of
    /// space-then-hex and this reddens. A guard whose needle is a single space matches every
    /// subject containing a space and then deletes it.
    /// </remarks>
    [Fact]
    [Trait("requires", "none")]
    public void ProcessTokenMarkerIsASpaceFollowedByExactlyTheEightHexDigits()
    {
        var marker = TestCertificateAuthority.ProcessTokenMarker;

        Assert.Equal(9, marker.Length);
        Assert.Equal(' ', marker[0]);
        Assert.True(
            marker[1..].All(Uri.IsHexDigit),
            $"Expected a space then eight hexadecimal digits, got '{marker}'.");
    }

    [Fact]
    [Trait("requires", "none")]
    public void TwoTierIntermediateSubjectIsNotTheBareLiteralAndCarriesTheProcessToken()
    {
        using var bed = TestCertificateAuthority.CreateTwoTierSuiteDirectory();

        var subject = bed.IntermediateCertificate.Subject;

        // The bare literal is the subject 101 of the 175 measured residue copies shared. If it
        // ever comes back, so does the cross-run collision.
        Assert.NotEqual("CN=Vouchfx Test Issuing Intermediate", subject);
        Assert.Contains("Vouchfx Test Issuing Intermediate", subject, StringComparison.Ordinal);
        Assert.Contains(TestCertificateAuthority.ProcessToken, subject, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires", "none")]
    public void TwoTierRootSubjectCarriesTheProcessToken()
    {
        using var bed = TestCertificateAuthority.CreateTwoTierSuiteDirectory();

        Assert.NotEqual("CN=Vouchfx Test Offline Root", bed.RootCertificate.Subject);
        Assert.Contains(
            TestCertificateAuthority.ProcessToken, bed.RootCertificate.Subject, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires", "none")]
    public void BedsInTheSameProcessShareOneToken()
    {
        // Per PROCESS, not per bed. Beds running side by side keep the same-subject coexistence
        // the thumbprint-keyed sweep is built for; what the token kills is the CROSS-RUN
        // collision. A per-bed token would silently change the sweep's problem.
        using var first = TestCertificateAuthority.CreateTwoTierSuiteDirectory();
        using var second = TestCertificateAuthority.CreateTwoTierSuiteDirectory();

        Assert.Equal(first.IntermediateCertificate.Subject, second.IntermediateCertificate.Subject);
        Assert.NotEqual(first.IntermediateCertificate.Thumbprint, second.IntermediateCertificate.Thumbprint);
    }

    [Fact]
    [Trait("requires", "none")]
    public void RootCaSubjectFieldAndTheMintedAnchorAgree()
    {
        // The field is what SecurityConfigurationAccessorTests asserts against, so mint and
        // assertion reading the same field is the property that keeps that test honest rather
        // than merely passing.
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        Assert.Equal($"CN={TestCertificateAuthority.CaSubjectCommonName}", bed.CaCertificate.Subject);
        Assert.Contains(
            TestCertificateAuthority.ProcessToken,
            TestCertificateAuthority.CaSubjectCommonName,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("requires", "none")]
    public void LeafSubjectsAreLeftAlone()
    {
        // The server leaf's common name is matched against the host a probe connects to, and the
        // client identities are matched by a broker's authorisation rules. Suffixing either
        // would break the thing under test to close a leak leaves never caused: CryptoAPI caches
        // path links, not end entities.
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();

        Assert.Equal("CN=localhost", bed.ServerCertificate.Subject);
        Assert.DoesNotContain(
            TestCertificateAuthority.ProcessToken, bed.ServerCertificate.Subject, StringComparison.Ordinal);
        Assert.Equal("localhost", TestCertificateAuthority.ServerSubjectCommonName);
        Assert.Equal("vouchfx-test-client", TestCertificateAuthority.ClientSubjectCommonName);
        Assert.Equal("vouchfx-test-unauthorised", TestCertificateAuthority.UnauthorisedClientSubjectCommonName);
        Assert.Equal("vouchfx-test-foreign-client", TestCertificateAuthority.ForeignClientSubjectCommonName);
    }

    [Fact]
    [Trait("requires", "none")]
    public void EveryAuthoritySubjectIsDistinctWithinTheProcess()
    {
        // The token makes subjects unique across RUNS; this pins that it did not accidentally
        // make any two of them equal to each other within one run — the property
        // ForeignCaSubjectCommonName's remarks depend on — and that every one of them carries the
        // SPACE-ANCHORED marker, which is the needle TestCertificateStoreGuard searches for. An
        // authority minted without the separator would be invisible to the guard.
        using var bed = TestCertificateAuthority.CreateSuiteDirectory();
        using var twoTier = TestCertificateAuthority.CreateTwoTierSuiteDirectory();
        var (imposterRoot, imposterLeaf) = TestCertificateAuthority.CreateImposterAuthority();
        using var unrelatedLeaf = TestCertificateAuthority.CreateUnrelatedLeaf();

        try
        {
            var subjects = new[]
            {
                bed.CaCertificate.Subject,
                twoTier.RootCertificate.Subject,
                twoTier.IntermediateCertificate.Subject,
                imposterRoot.Subject,
                unrelatedLeaf.Issuer,
                $"CN={TestCertificateAuthority.ForeignCaSubjectCommonName}",
            };

            Assert.Equal(subjects.Length, subjects.Distinct(StringComparer.Ordinal).Count());
            Assert.All(
                subjects,
                subject => Assert.Contains(
                    TestCertificateAuthority.ProcessTokenMarker, subject, StringComparison.Ordinal));
        }
        finally
        {
            imposterRoot.Dispose();
            imposterLeaf.Dispose();
        }
    }
}
