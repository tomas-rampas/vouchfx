// Certificate-store hygiene guard (#419) — the xUnit wiring for
// Vouchfx.TestSupport.TestCertificateStoreGuard.
//
// This assembly is the MEASURED leaker: one cached `Vouchfx Test Issuing Intermediate` per run,
// 175 of which once faked two environmental failures for several sessions. The scan itself lives
// in TestSupport so any assembly can adopt it; what lives here is the three pieces that make it
// red a run at the right moment.
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// The collection the hygiene guard belongs to, ordered last by
/// <see cref="CertificateStoreGuardLastOrderer"/>.
/// </summary>
/// <remarks>
/// A collection of its own purely so the guard is ADDRESSABLE by the orderer — xUnit orders
/// collections, not classes, and a class with no <c>[Collection]</c> attribute gets an
/// auto-generated collection whose display name is an implementation detail.
/// </remarks>
[CollectionDefinition(CertificateStoreGuardCollectionDefinition.Name)]
public sealed class CertificateStoreGuardCollectionDefinition
{
    /// <summary>The collection name, shared by the guard's test class and the orderer.</summary>
    public const string Name = "certificate-store-hygiene-guard";
}

/// <summary>
/// Orders <see cref="CertificateStoreGuardCollectionDefinition"/> after every other collection in
/// this assembly, leaving the relative order of the rest untouched.
/// </summary>
/// <remarks>
/// <para>
/// Registered by the <c>[assembly: TestCollectionOrderer]</c> attribute in AssemblyInfo.cs.
/// Combined with this assembly's <c>[assembly: CollectionBehavior(DisableTestParallelization =
/// true)]</c>, collections run sequentially in the order returned here, so the guard observes the
/// stores after every bed in the assembly has been disposed.
/// </para>
/// <para>
/// What is at stake if the ordering is ever lost is DETECTION, not correctness. Serialised
/// execution means no bed is alive while the guard runs, so a guard that ran too early would find
/// an already-swept store and pass — a missed leak, never a false failure. That asymmetry is why
/// this is an <c>OrderBy</c> and not a hard gate.
/// </para>
/// </remarks>
public sealed class CertificateStoreGuardLastOrderer : ITestCollectionOrderer
{
    /// <summary>
    /// The ordering rule, over display names alone.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="OrderTestCollections"/> so it can be tested directly:
    /// <see cref="ITestCollection"/> is an xUnit-serialisable interface whose analyzers require
    /// any implementor to be a public marshal-by-ref type with a parameterless constructor, which
    /// is a large fake to maintain for a one-line sort. <c>OrderBy</c> is a stable sort, so every
    /// non-guard item keeps the position it arrived in.
    /// </remarks>
    public static IEnumerable<T> GuardLast<T>(IEnumerable<T> items, Func<T, string> displayName) =>
        items.OrderBy(item => string.Equals(
            displayName(item), CertificateStoreGuardCollectionDefinition.Name, StringComparison.Ordinal) ? 1 : 0);

    /// <inheritdoc />
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections) =>
        GuardLast(testCollections, collection => collection.DisplayName);
}

/// <summary>
/// Fails the run when this process finishes with certificates of its own still cached in
/// Windows' intermediate-CA stores.
/// </summary>
[Collection(CertificateStoreGuardCollectionDefinition.Name)]
public sealed class CertificateStoreHygieneGuardTests
{
    /// <summary>
    /// The guard itself: in the collection ordered last in the assembly, asserting the host is no
    /// dirtier than the run found it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A red here is NOT an environment problem to be shrugged off — it means a bed's Dispose did
    /// not run, or cached a copy under a thumbprint no bed listed, and the message names the
    /// store, subject and thumbprint of each one. Residue the guard could remove is gone by the
    /// time the assertion fires, so re-running is green; residue whose line reads "removal failed"
    /// (the unelevated <c>LocalMachine</c> case) is still there and will red the next run too,
    /// which is the correct behaviour and not a flake.
    /// </para>
    /// <para>
    /// <strong>Enforcement is local-Windows-only today.</strong>
    /// <see cref="TestCertificateStoreGuard.SweepProcessResidue"/> is a no-op off Windows and
    /// every job in <c>.github/workflows/build.yml</c> runs on <c>ubuntu-latest</c>, so in CI this
    /// test passes without examining anything. It catches the leak on a developer's Windows
    /// machine — which is where #374 was found by hand, twice — and nowhere else. A Windows CI
    /// lane would change that and is deliberately not part of this change.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("requires", "none")]
    public void SuiteLeavesNoCertificateOfItsOwnInTheIntermediateCaStores()
    {
        var residue = TestCertificateStoreGuard.SweepProcessResidue();

        Assert.True(residue is null, residue);
    }
}

/// <summary>
/// Proves the guard can FAIL — that a green
/// <see cref="CertificateStoreHygieneGuardTests"/> is evidence and not merely a test that never
/// looks at anything.
/// </summary>
/// <remarks>
/// <para>
/// The one thing a guard must not be is inert. This drill plants a certificate carrying this
/// process's token in <c>CurrentUser\CA</c>, exactly where CryptoAPI's cache would put one, and
/// asserts the guard notices it, names it, removes it, and is silent on the next scan. Writing to
/// a certificate store from a test is a hazard the fixture otherwise avoids, and it is accepted
/// here for the one case where the store IS the subject: the write is to the unelevated per-user
/// store, the subject carries this process's token so no concurrent run can see it, and removal
/// happens in a <c>finally</c>. A crash that defeats even the <c>finally</c> leaves only a
/// public-key-only certificate that expires in two days, in the untrusted per-user intermediate
/// store — the guard cannot be counted on to catch it, because a later run scans for a DIFFERENT
/// process's token and, within this run, the guard may already have executed.
/// </para>
/// <para>
/// <strong>In the guard's own collection, deliberately.</strong> The drill's planted certificate
/// could make the guard false-fail if the two classes ever ran concurrently; sharing the
/// collection rules that out under any future <c>xunit.runner.json</c> that re-enables collection
/// parallelism, since a collection never runs in parallel with itself. The drill's sweep call
/// also removes EVERY own-token certificate, planted or not — against a live bed in some OTHER
/// collection, that could delete the bed's cached intermediate mid-test, and the shared collection
/// is no protection there: only this assembly's <c>DisableTestParallelization</c> prevents it, so
/// re-enabling collection parallelism is NOT made safe by this placement.
/// The two classes' relative order within the collection is unspecified
/// and both orders are safe: the drill removes its plant in a <c>finally</c> before it returns, so
/// a guard running after it sees a clean store, and a guard running before it sees a store the
/// drill has not touched yet.
/// </para>
/// <para>
/// Like the guard, this drill is a no-op off Windows and therefore proves nothing in this
/// repository's Linux-only CI.
/// </para>
/// </remarks>
[Collection(CertificateStoreGuardCollectionDefinition.Name)]
public sealed class CertificateStoreGuardTeethTests
{
    [Fact]
    [Trait("requires", "none")]
    public void GuardNoticesNamesAndRemovesACertificateCarryingItsOwnToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The documented contract off Windows, where nothing performs this caching.
            Assert.Null(TestCertificateStoreGuard.SweepProcessResidue());
            return;
        }

        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=Vouchfx Guard Drill {TestCertificateAuthority.ProcessToken}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var signed = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));

        // Re-created from its own DER so nothing with a private key is written to a store.
        using var planted = new X509Certificate2(signed.Export(X509ContentType.Cert));
        var thumbprint = planted.Thumbprint;

        string? census;
        try
        {
            using (var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadWrite);
                store.Add(planted);
            }

            census = TestCertificateStoreGuard.SweepProcessResidue();
        }
        finally
        {
            RemoveIfStillPresent(thumbprint);
        }

        Assert.False(
            census is null,
            "The guard reported a clean store while holding a certificate carrying its own token.");

        var reported = census ?? string.Empty;
        Assert.Contains(thumbprint, reported, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"CurrentUser\CA", reported, StringComparison.Ordinal);
        Assert.Contains("(removed)", reported, StringComparison.Ordinal);
        Assert.Contains(TestCertificateAuthority.ProcessToken, reported, StringComparison.Ordinal);

        // The census must name the PLANTED certificate and NOTHING ELSE. Without this, a genuine
        // leak sitting in the store when the drill runs would be swept away by the drill's own
        // SweepProcessResidue call, reported to nobody, and the assembly guard would then find a
        // clean store and pass — the drill would have eaten the very failure it exists to prove is
        // possible. Anything unexpected reddens here, at the drill, where the census is in hand.
        var entries = CensusEntries(reported);
        Assert.True(
            entries.Count == 1 && entries[0].Contains(thumbprint, StringComparison.OrdinalIgnoreCase),
            "The census names something other than the planted certificate — real residue was " +
            "present and this drill has just swept it. Census:" + Environment.NewLine + reported);

        // Removal was real, not merely reported: the next scan finds nothing.
        Assert.Null(TestCertificateStoreGuard.SweepProcessResidue());
    }

    /// <remarks>
    /// The census is a preamble line followed by one indented line per certificate; the entries
    /// are the indented ones. Matching on the two-space prefix rather than parsing the preamble's
    /// count keeps this reading what the guard actually emitted.
    /// </remarks>
    private static List<string> CensusEntries(string census) =>
        census
            .Split(Environment.NewLine, StringSplitOptions.None)
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal))
            .ToList();

    private static void RemoveIfStillPresent(string thumbprint)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            try
            {
                foreach (var certificate in matches)
                {
                    store.Remove(certificate);
                }
            }
            finally
            {
                foreach (var certificate in matches)
                {
                    certificate.Dispose();
                }
            }
        }
        catch (Exception ex) when (
            ex is CryptographicException or UnauthorizedAccessException or SecurityException)
        {
            // This runs in a finally, so an escaping exception would REPLACE the drill's real
            // assertion failure with a store error and hide what the drill actually found. The
            // assembly-wide guard is the backstop for anything left behind.
        }
    }
}

/// <summary>
/// Pins the ordering rule the guard's detection power depends on.
/// </summary>
/// <remarks>
/// Deliberately NOT in the guard's own collection — unlike the teeth drill, which touches the
/// store and therefore has to share it. These are ordinary tests about a pure function and a
/// reflected attribute; running them last would prove nothing.
/// </remarks>
public sealed class CertificateStoreGuardLastOrdererTests
{
    private static readonly string[] s_withGuardInTheMiddle =
    {
        "alpha", CertificateStoreGuardCollectionDefinition.Name, "beta",
    };

    private static readonly string[] s_guardLast =
    {
        "alpha", "beta", CertificateStoreGuardCollectionDefinition.Name,
    };

    private static readonly string[] s_unsorted = { "zulu", "alpha", "mike" };

    [Fact]
    [Trait("requires", "none")]
    public void GuardCollectionIsOrderedLast()
    {
        var ordered = CertificateStoreGuardLastOrderer.GuardLast(s_withGuardInTheMiddle, name => name);

        Assert.Equal(s_guardLast, ordered);
    }

    [Fact]
    [Trait("requires", "none")]
    public void EveryOtherCollectionKeepsItsOriginalRelativeOrder()
    {
        // The sort is stable, so a set with no guard in it comes back untouched — the orderer
        // must not become an alphabetiser of the whole assembly as a side effect.
        var ordered = CertificateStoreGuardLastOrderer.GuardLast(s_unsorted, name => name);

        Assert.Equal(s_unsorted, ordered);
    }

    /// <summary>
    /// Resolves the two STRINGS the assembly attribute carries, the way xUnit would (xUnit v2
    /// goes through <c>Assembly.Load</c> + <c>GetType</c>; <c>Type.GetType</c> lands on the same
    /// type for this registration).
    /// </summary>
    /// <remarks>
    /// The registration is <c>[assembly: TestCollectionOrderer("Namespace.Type", "AssemblyName")]</c>
    /// — two string literals no compiler checks. If either drifts (a rename, a namespace move, a
    /// project rename) xUnit logs a diagnostic nobody reads and silently falls back to its default
    /// order, and the only visible consequence is a guard that quietly stops running last. Asking
    /// the runtime to resolve the same pair turns that into a red test.
    /// </remarks>
    [Fact]
    [Trait("requires", "none")]
    public void TheAssemblyAttributeResolvesToThisOrderer()
    {
        var registration = typeof(CertificateStoreGuardLastOrderer).Assembly
            .GetCustomAttributesData()
            .Single(data => data.AttributeType == typeof(TestCollectionOrdererAttribute));

        var typeName = (string?)registration.ConstructorArguments[0].Value;
        var assemblyName = (string?)registration.ConstructorArguments[1].Value;

        var resolved = Type.GetType($"{typeName}, {assemblyName}");

        Assert.True(
            resolved is not null,
            $"The registered orderer '{typeName}, {assemblyName}' does not resolve to a type; " +
            "xUnit would fall back to its default collection order and the hygiene guard would " +
            "stop running last.");
        Assert.Equal(typeof(CertificateStoreGuardLastOrderer), resolved);
        Assert.True(typeof(ITestCollectionOrderer).IsAssignableFrom(resolved));
    }
}
