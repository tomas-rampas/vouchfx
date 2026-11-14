using System.Xml.Linq;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Fails when the Aspire version is not pinned to one value everywhere it is declared: the
/// <c>Aspire.Hosting*</c> package versions and the <c>VouchfxDcpVersion</c> property in the root
/// <c>Directory.Packages.props</c>, and the <c>Aspire.AppHost.Sdk</c> version of every project that
/// declares that SDK.
/// </summary>
/// <remarks>
/// <para>
/// Dependabot moves the package versions but not the property or the SDK versions, so a bump can
/// leave the three kinds of place out of step. The only permitted exception is
/// <c>Aspire.Hosting.Elasticsearch</c>, which is held back because no release exists for the
/// current Aspire minor.
/// </para>
/// <para>
/// This is a bounded text check over MSBuild XML. It reads <c>Directory.Packages.props</c> and every
/// <c>*.csproj</c> under the repository root, skipping hidden directories and <c>bin</c>, <c>obj</c>
/// and <c>node_modules</c>. It does not resolve packages or evaluate MSBuild properties. The
/// minimum counts guard against a broken search passing without checking anything.
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

    private const int MinimumSdkProjects = 3;

    private const string MoveTogetherHint =
        "The Aspire.Hosting* package versions, VouchfxDcpVersion and every Aspire.AppHost.Sdk version "
        + "must move together in the same change.";

    private static readonly string[] SkippedDirectoryNames = { "bin", "obj", "node_modules" };

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

        Assert.True(
            sdks.Count >= MinimumSdkProjects,
            $"Only {sdks.Count} project files declaring {AppHostSdkName} were found, expected at least "
            + $"{MinimumSdkProjects}. The search is probably broken, so this guard would check nothing.");

        var mismatches = sdks
            .Where(s => !string.Equals(s.Version, dcp, StringComparison.Ordinal))
            .Select(s => $"{s.File}: {AppHostSdkName} is {s.Version} but {DcpPropertyName} in {PackagesPropsName} is {dcp}")
            .ToList();

        Assert.True(
            mismatches.Count == 0,
            $"The {AppHostSdkName} version is out of step with the Aspire packages. {MoveTogetherHint} "
            + $"Found: [{string.Join("; ", mismatches)}].");
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
            .Select(e => (Id: (AttributeValue(e, "Include") ?? string.Empty).Trim(), Version: (AttributeValue(e, "Version") ?? string.Empty).Trim()))
            .Where(p => p.Id.StartsWith(HostingPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static List<(string File, string Version)> ReadAppHostSdkDeclarations()
    {
        var root = RepoRoot.Resolve();
        var result = new List<(string File, string Version)>();
        foreach (var path in EnumerateProjectFiles(root))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            XDocument document;
            try
            {
                document = XDocument.Load(path);
            }
            catch (System.Xml.XmlException exception)
            {
                Assert.Fail($"{relative} is not well-formed XML, so the Aspire version guard cannot read it: {exception.Message}");
                return result;
            }

            foreach (var element in document.Descendants().Where(e => NameIs(e, "Sdk")))
            {
                if (string.Equals(AttributeValue(element, "Name")?.Trim(), AppHostSdkName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((relative, (AttributeValue(element, "Version") ?? string.Empty).Trim()));
                }
            }

            // The attribute form: <Project Sdk="Aspire.AppHost.Sdk/13.4.6">, possibly one of several SDKs.
            var projectSdk = document.Root is { } project ? AttributeValue(project, "Sdk") : null;
            foreach (var entry in (projectSdk ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Trim().Split('/', 2);
                if (string.Equals(parts[0], AppHostSdkName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((relative, parts.Length > 1 ? parts[1].Trim() : string.Empty));
                }
            }
        }

        return result;
    }

    private static XDocument LoadPackagesProps() =>
        XDocument.Load(Path.Combine(RepoRoot.Resolve(), PackagesPropsName));

    private static List<string> EnumerateProjectFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        var files = new List<string>();
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            files.AddRange(Directory.EnumerateFiles(current).Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && !SkippedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(child);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static bool NameIs(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
}
