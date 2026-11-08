using System.Xml.Linq;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Fails when the central <c>MySqlConnector</c> pin in <c>Directory.Packages.props</c>
/// drops below 2.6.1, the first patched release named by GHSA-473q-m89c-ghf8.
/// </summary>
/// <remarks>
/// Published <c>vouchfx</c> 1.2.1 bundles MySqlConnector FileVersion 2.4.0, which is
/// inside that advisory's vulnerable range (versions below 2.6.1). This guard reads the
/// source pin only. It does not open a nupkg, and it does not exercise a TLS handshake.
/// A later patched bump (the current pin is 2.6.2) still passes. A downgrade below
/// 2.6.1 fails.
/// </remarks>
public sealed class MySqlConnectorPinGuardTests
{
    private const string PackageId = "MySqlConnector";

    /// <summary>First patched MySqlConnector release named by GHSA-473q-m89c-ghf8.</summary>
    private static readonly Version FirstPatchedVersion = new(2, 6, 1);

    private readonly ITestOutputHelper _output;

    public MySqlConnectorPinGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GivenDirectoryPackagesProps_WhenMySqlConnectorPinIsRead_ThenItIsAtLeastTheFirstPatchedRelease()
    {
        var propsPath = Path.Combine(RepoRoot.Resolve(), "Directory.Packages.props");
        var document = XDocument.Load(propsPath);
        var versions = document
            .Descendants("PackageVersion")
            .Where(element => string.Equals((string?)element.Attribute("Include"), PackageId, StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Version"))
            .ToList();

        var versionText = Assert.Single(versions);
        _output.WriteLine($"Central {PackageId} PackageVersion: {versionText}");
        Assert.False(
            string.IsNullOrWhiteSpace(versionText),
            "Directory.Packages.props MySqlConnector PackageVersion is missing.");
        Assert.True(
            Version.TryParse(versionText, out var parsed),
            $"Directory.Packages.props MySqlConnector Version '{versionText}' is not a plain numeric version.");
        Assert.True(
            IsAtLeast(parsed!, FirstPatchedVersion),
            $"Directory.Packages.props pins MySqlConnector {versionText}, below 2.6.1, the first patched "
            + "release named by GHSA-473q-m89c-ghf8 (vulnerable range < 2.6.1).");
    }

    /// <summary>
    /// Compares major, minor and build. <see cref="Version"/> stores an omitted build as -1,
    /// so that component is treated as 0 and 2.6 is below 2.6.1. Revision is not compared.
    /// </summary>
    private static bool IsAtLeast(Version actual, Version minimum)
    {
        var normalised = new Version(actual.Major, actual.Minor, Math.Max(actual.Build, 0));
        return normalised >= minimum;
    }
}
