using System.Xml.Linq;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Fails when the Aspire version differs between the places this guard reads: the
/// <c>Aspire.Hosting*</c> package versions and the <c>VouchfxDcpVersion</c> property in the root
/// <c>Directory.Packages.props</c>, and the <c>Aspire.AppHost.Sdk</c> version of every declaration
/// of that SDK in an MSBuild file.
/// </summary>
/// <remarks>
/// <para>
/// Dependabot moves the package versions but not the property or the SDK versions, so a bump can
/// leave the three kinds of place out of step. The only permitted exception is
/// <c>Aspire.Hosting.Elasticsearch</c>, which is held back because no release exists for the
/// current Aspire minor.
/// </para>
/// <para>
/// This is a bounded text check over MSBuild XML. It reads the root <c>Directory.Packages.props</c>
/// for the package versions, with their ids decoded and split at <c>;</c>, and every MSBuild file
/// that <c>AspireAuditSuppressionGuardTests</c> reads for the SDK declarations, so hidden
/// directories and <c>bin</c>, <c>obj</c> and <c>node_modules</c> are skipped. It does not resolve
/// packages or evaluate MSBuild properties. The minimum counts, of package versions and of files
/// that declare the SDK, guard against a broken search passing without checking anything. Accepted
/// limit: a version an <c>Aspire.Hosting*</c> package gets anywhere else (a <c>VersionOverride</c>, a
/// project outside central package management) is not compared here;
/// <c>AspireAuditSuppressionGuardTests</c> refuses one for <c>Aspire.Hosting</c> and
/// <c>Aspire.Hosting.AppHost</c> while the audit suppression is present.
/// </para>
/// </remarks>
public sealed class AspireVersionLockstepGuardTests
{
    private const string PackagesPropsName = "Directory.Packages.props";

    private const string HostingPrefix = "Aspire.Hosting";

    private const string ExemptPackageId = "Aspire.Hosting.Elasticsearch";

    private const string DcpPropertyName = "VouchfxDcpVersion";

    private const string AppHostSdkName = "Aspire.AppHost.Sdk";

    private const int MinimumHostingPackages = 10;

    private const int MinimumSdkFiles = 3;

    private const string MoveTogetherHint =
        "The Aspire.Hosting* package versions, VouchfxDcpVersion and every Aspire.AppHost.Sdk version "
        + "must move together in the same change.";

    private readonly ITestOutputHelper _output;

    public AspireVersionLockstepGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GivenCentralPackageVersions_WhenHostingPackagesAreListed_ThenEnoughAreFoundAndDcpVersionIsPresent()
    {
        var packages = ReadHostingPackages();
        _output.WriteLine("Aspire.Hosting* PackageVersion entries: " + packages.Count);

        Assert.True(
            packages.Count >= MinimumHostingPackages,
            $"Only {packages.Count} {HostingPrefix}* PackageVersion entries were found in {PackagesPropsName}, "
            + $"expected at least {MinimumHostingPackages}. The search is probably broken, so this guard would "
            + "check nothing.");

        var dcp = ReadDcpVersion();
        Assert.False(
            string.IsNullOrWhiteSpace(dcp),
            $"{DcpPropertyName} is missing or empty in {PackagesPropsName}. {MoveTogetherHint}");
    }

    [Fact]
    public void GivenCentralPackageVersions_WhenHostingPackagesAreCompared_ThenAllMatchTheDcpVersionExceptElasticsearch()
    {
        var dcp = ReadDcpVersion();
        Assert.False(
            string.IsNullOrWhiteSpace(dcp),
            $"{DcpPropertyName} is missing or empty in {PackagesPropsName}. {MoveTogetherHint}");

        var mismatches = ReadHostingPackages()
            .Where(p => !string.Equals(p.Id, ExemptPackageId, StringComparison.OrdinalIgnoreCase))
            .Where(p => !string.Equals(p.Version, dcp, StringComparison.Ordinal))
            .Select(p => $"{PackagesPropsName}: {p.Id} is {p.Version} but {DcpPropertyName} is {dcp}")
            .ToList();

        Assert.True(
            mismatches.Count == 0,
            $"Aspire versions are out of step. {MoveTogetherHint} Only {ExemptPackageId} may differ. "
            + $"Found: [{string.Join("; ", mismatches)}].");
    }

    [Fact]
    public void GivenCentralPackageVersions_WhenHostingPackagesAreCompared_ThenTheyShareOneVersion()
    {
        var versions = ReadHostingPackages()
            .Where(p => !string.Equals(p.Id, ExemptPackageId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Version, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            versions.Count <= 1,
            $"{PackagesPropsName} pins the {HostingPrefix}* packages to more than one version, and only "
            + $"{ExemptPackageId} may differ. {MoveTogetherHint} Found: "
            + $"[{string.Join("; ", versions.Select(g => $"{g.Key}: {string.Join(", ", g.Select(p => p.Id))}"))}].");
    }

    [Fact]
    public void GivenProjects_WhenAppHostSdkVersionsAreCompared_ThenEnoughAreFoundAndAllMatchTheDcpVersion()
    {
        var dcp = ReadDcpVersion();
        Assert.False(
            string.IsNullOrWhiteSpace(dcp),
            $"{DcpPropertyName} is missing or empty in {PackagesPropsName}. {MoveTogetherHint}");

        var sdks = ReadAppHostSdkDeclarations();
        _output.WriteLine($"{AppHostSdkName} declarations: " + string.Join(", ", sdks.Select(s => $"{s.File}={s.Version}")));

        var declaringFiles = sdks.Select(s => s.File).Distinct(StringComparer.Ordinal).Count();
        Assert.True(
            declaringFiles >= MinimumSdkFiles,
            $"Only {declaringFiles} MSBuild files declaring {AppHostSdkName} were found, expected at least "
            + $"{MinimumSdkFiles}. The search is probably broken, so this guard would check nothing.");

        var mismatches = sdks
            .Where(s => !string.Equals(s.Version, dcp, StringComparison.Ordinal))
            .Select(s => $"{s.File}: {AppHostSdkName} is {s.Version} but {DcpPropertyName} in {PackagesPropsName} is {dcp}")
            .ToList();

        Assert.True(
            mismatches.Count == 0,
            $"The {AppHostSdkName} version is out of step with the Aspire packages. {MoveTogetherHint} "
            + $"Found: [{string.Join("; ", mismatches)}].");
    }

    [Theory]
    [InlineData("<Project Sdk=\"Aspire.AppHost.Sdk/13.4.6\" />", "13.4.6")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk;Aspire.AppHost.Sdk/13.4.6\" />", "13.4.6")]
    [InlineData("<Project><Sdk Name=\"Aspire.AppHost.Sdk\" Version=\"13.4.6\" /></Project>", "13.4.6")]
    [InlineData("<Project><Import Project=\"Sdk.props\" Sdk=\"Aspire.AppHost.Sdk\" Version=\"13.6.5\" /></Project>", "13.6.5")]
    [InlineData("<Project><Import Project=\"Sdk.props\" Sdk=\"Microsoft.NET.Sdk\" Version=\"10.0.0\" /></Project>", null)]
    public void GivenProjectXml_WhenAppHostSdkVersionsAreRead_ThenEverySpellingIsSeen(string xml, string? expected)
    {
        var versions = AppHostSdkVersions(XDocument.Parse(xml));

        Assert.Equal(expected is null ? Array.Empty<string>() : new[] { expected }, versions);
    }

    private static string? ReadDcpVersion()
    {
        var values = LoadPackagesProps()
            .Descendants()
            .Where(e => NameIs(e, DcpPropertyName))
            .Select(e => e.Value.Trim())
            .ToList();

        return values.Count == 0 ? null : Assert.Single(values);
    }

    private static List<(string Id, string Version)> ReadHostingPackages() =>
        LoadPackagesProps()
            .Descendants()
            .Where(e => NameIs(e, "PackageVersion"))
            .SelectMany(e => AspireAuditSuppressionGuardTests.ItemPackageIds(e)
                .Select(id => (Id: id, Version: (AttributeValue(e, "Version") ?? string.Empty).Trim())))
            .Where(p => p.Id.StartsWith(HostingPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static List<(string File, string Version)> ReadAppHostSdkDeclarations() =>
        AspireAuditSuppressionGuardTests.LoadedMsBuildDocuments()
            .SelectMany(d => AppHostSdkVersions(d.Document).Select(version => (d.File, version)))
            .ToList();

    /// <summary>
    /// The versions of every <c>Aspire.AppHost.Sdk</c> declaration in a project: the <c>&lt;Sdk Name&gt;</c>
    /// element, the <c>Sdk="Aspire.AppHost.Sdk/1.2.3"</c> attribute of the project, possibly one of several SDKs,
    /// and the <c>&lt;Import Project="Sdk.props" Sdk="Aspire.AppHost.Sdk" Version="1.2.3" /&gt;</c> form.
    /// </summary>
    private static List<string> AppHostSdkVersions(XDocument document)
    {
        var result = new List<string>();
        foreach (var element in document.Descendants().Where(e => NameIs(e, "Sdk")))
        {
            if (string.Equals(AttributeValue(element, "Name")?.Trim(), AppHostSdkName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add((AttributeValue(element, "Version") ?? string.Empty).Trim());
            }
        }

        foreach (var element in document.Descendants().Where(e => NameIs(e, "Import")))
        {
            if (string.Equals(AttributeValue(element, "Sdk")?.Trim(), AppHostSdkName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add((AttributeValue(element, "Version") ?? string.Empty).Trim());
            }
        }

        // The attribute form: <Project Sdk="Aspire.AppHost.Sdk/13.4.6">, possibly one of several SDKs.
        var projectSdk = document.Root is { } project ? AttributeValue(project, "Sdk") : null;
        foreach (var entry in (projectSdk ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Trim().Split('/', 2);
            if (string.Equals(parts[0], AppHostSdkName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(parts.Length > 1 ? parts[1].Trim() : string.Empty);
            }
        }

        return result;
    }

    private static XDocument LoadPackagesProps() =>
        XDocument.Load(Path.Combine(RepoRoot.Resolve(), PackagesPropsName));

    private static bool NameIs(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
}
