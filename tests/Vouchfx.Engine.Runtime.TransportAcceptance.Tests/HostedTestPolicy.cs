using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: TestCollectionOrderer("Vouchfx.Engine.Runtime.TransportAcceptance.Tests.HostedTlsCollectionOrderer", "Vouchfx.Engine.Runtime.TransportAcceptance.Tests")]

namespace Vouchfx.Engine.Runtime.TransportAcceptance.Tests;

public sealed class HostedTlsFactAttribute : Xunit.FactAttribute
{
    public HostedTlsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VOUCHFX_TLS_HOSTED_ACCEPTANCE") != "1" ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
        {
            Skip = "Native TLS acceptance requires an explicitly opted-in ephemeral GitHub-hosted job.";
        }
    }
}

[CollectionDefinition(HostedTlsGuardCollectionDefinition.Name)]
public sealed class HostedTlsGuardCollectionDefinition
{
    public const string Name = "hosted-tls-owned-store-guard";
}

public sealed class HostedTlsCollectionOrderer : ITestCollectionOrderer
{
    public static IEnumerable<T> GuardLast<T>(IEnumerable<T> items, Func<T, string> displayName) =>
        items.OrderBy(item => string.Equals(displayName(item), HostedTlsGuardCollectionDefinition.Name, StringComparison.Ordinal) ? 1 : 0);
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        GuardLast(testCollections, collection => collection.DisplayName);
}

[Collection(HostedTlsGuardCollectionDefinition.Name)]
public sealed class OwnedCertificateStoreGuardTests
{
    // The same process-token scoped final guard as existing certificate-minting hosts.
    // It is a no-op off Windows and skips unreadable stores: never claim global zero residue.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Descriptive xUnit name; HostedTlsFact derives Xunit.FactAttribute.")]
    [HostedTlsFact]
    public void FinalGuard_NoOwnedCertificateResidue() => Assert.Null(TestCertificateStoreGuard.SweepProcessResidue());
}
