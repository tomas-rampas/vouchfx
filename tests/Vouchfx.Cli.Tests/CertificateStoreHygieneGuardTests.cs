using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Vouchfx.Cli.Tests;

// xUnit 2.9.2 runs exclusive collections after all parallel collections. Ordering this
// exclusive collection last therefore preserves ordinary parallelism and waits for teardown.
[CollectionDefinition(CertificateStoreGuardCollectionDefinition.Name, DisableParallelization = true)]
public sealed class CertificateStoreGuardCollectionDefinition
{
    public const string Name = "certificate-store-hygiene-guard";
}

public sealed class CertificateStoreGuardLastOrderer : ITestCollectionOrderer
{
    public static IEnumerable<T> GuardLast<T>(IEnumerable<T> items, Func<T, string> displayName) =>
        items.OrderBy(item => string.Equals(
            displayName(item), CertificateStoreGuardCollectionDefinition.Name, StringComparison.Ordinal) ? 1 : 0);

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        GuardLast(new DefaultTestCollectionOrderer().OrderTestCollections(testCollections), collection => collection.DisplayName);
}

[Collection(CertificateStoreGuardCollectionDefinition.Name)]
public sealed class CertificateStoreHygieneGuardTests
{
    // The existing guard scopes cleanup to this process token and exact owned thumbprints.
    // This does not reclaim earlier processes' residue or abandoned private-key files.
    [Fact]
    [Trait("requires", "none")]
    public void SuiteLeavesNoCertificateOfItsOwnInTheIntermediateCaStores()
    {
        var residue = TestCertificateStoreGuard.SweepProcessResidue();
        Assert.True(residue is null, residue);
    }
}
