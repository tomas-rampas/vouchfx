using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Fails when any <c>YamlDotNet</c> version in an MSBuild file leaves major 16, the ceiling for the
/// whole v1.x engine series.
/// </summary>
/// <remarks>
/// <para>
/// YamlDotNet types are part of the frozen <c>Vouchfx.Sdk</c> v1 surface (<c>IStepBinder&lt;T&gt;.Bind</c>
/// takes a <c>YamlNode</c>), and a new assembly major makes the CLI refuse external providers built
/// against YamlDotNet 16. This also keeps the Aspire hold honest: Aspire 13.6.0 and 13.6.5 require
/// YamlDotNet 18.1.0 or later, so a security update to 13.6.5 that bumps YamlDotNet in the same change
/// fails here instead of landing. The check reads source pins only, like <c>MySqlConnectorPinGuardTests</c>.
/// It reads the <c>Version</c> and the <c>VersionOverride</c> of every <c>PackageVersion</c>,
/// <c>PackageReference</c> and <c>GlobalPackageReference</c> whose ids, decoded and split at <c>;</c>,
/// include YamlDotNet, in every MSBuild file that <c>AspireAuditSuppressionGuardTests</c> reads, so an
/// override in a project file is seen too. It does not open a nupkg, and it does not evaluate MSBuild,
/// so a version set through a property fails closed as not a plain numeric version. Accepted limit: an
/// id written through a property, an item list or a wildcard, and a <c>VersionOverride</c> that an
/// <c>&lt;ItemDefinitionGroup&gt;</c> gives every package item, are not resolved and not seen. Raising
/// the ceiling is a v2 engine decision.
/// </para>
/// </remarks>
public sealed class YamlDotNetPinGuardTests
{
    private const string PackageId = "YamlDotNet";

    private const string PackagesPropsName = "Directory.Packages.props";

    private const int CeilingMajor = 16;

    private readonly ITestOutputHelper _output;

    public YamlDotNetPinGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GivenMsBuildFiles_WhenYamlDotNetVersionsAreRead_ThenEveryOneIsOnTheV1Major()
    {
        var pins = new List<(string File, string Kind, string Version)>();
        foreach (var (file, document) in AspireAuditSuppressionGuardTests.LoadedMsBuildDocuments())
        {
            pins.AddRange(YamlDotNetVersions(document).Select(p => (file, p.Kind, p.Version)));
        }

        _output.WriteLine($"{PackageId} versions: " + string.Join(", ", pins.Select(p => $"{p.File} {p.Kind}={p.Version}")));

        var central = pins.Where(p => p.File == PackagesPropsName && p.Kind == "PackageVersion Version").ToList();
        Assert.True(
            central.Count == 1,
            $"{PackagesPropsName} must pin {PackageId} exactly once, found {central.Count}.");

        var unreadable = pins.Where(p => !Version.TryParse(p.Version, out _)).ToList();
        Assert.True(
            unreadable.Count == 0,
            $"A {PackageId} version is not a plain numeric version, so this guard cannot check the ceiling and fails closed: "
            + $"[{string.Join("; ", unreadable.Select(p => $"{p.File}: {p.Kind}='{p.Version}'"))}].");

        var outside = pins.Where(p => Version.Parse(p.Version).Major != CeilingMajor).ToList();
        Assert.True(
            outside.Count == 0,
            $"{PackageId} is held at {CeilingMajor}.x for the whole v1.x engine series because its types are part of the frozen "
            + "Vouchfx.Sdk v1 surface, and Aspire 13.6.0 and 13.6.5 need YamlDotNet 18.1.0 or later. Found outside that major: "
            + $"[{string.Join("; ", outside.Select(p => $"{p.File}: {p.Kind}={p.Version}"))}]. Revert the bump, or the override. "
            + "The YamlDotNet hold in .github/dependabot.yml moves together with this ceiling. Raising the ceiling "
            + "is a v2 engine decision and needs a deliberate change to this test.");
    }

    [Theory]
    [InlineData("<PackageVersion Include=\"YamlDotNet\" Version=\"16.3.0\" />", 0)]
    [InlineData("<PackageVersion Include=\"YamlDotNet\" Version=\"18.1.0\" />", 1)]
    [InlineData("<PackageReference Include=\"YamlDotNet\" />", 0)]
    [InlineData("<PackageReference Include=\"YamlDotNet\" VersionOverride=\"18.1.0\" />", 1)]
    [InlineData("<PackageReference Include=\"%59amlDotNet\" VersionOverride=\"18.1.0\" />", 1)]
    [InlineData("<PackageReference Include=\"yamldotnet\"><VersionOverride>18.1.0</VersionOverride></PackageReference>", 1)]
    [InlineData("<GlobalPackageReference Include=\"YamlDotNet\" Version=\"18.1.0\" />", 1)]
    [InlineData("<PackageReference Update=\"YamlDotNet\" Version=\"18.1.0\" />", 1)]
    [InlineData("<PackageReference Include=\"Newtonsoft.Json; YamlDotNet\" VersionOverride=\"18.1.0\" />", 1)]
    [InlineData("<PackageReference Include=\"Newtonsoft.Json\" VersionOverride=\"18.1.0\" />", 0)]
    public void GivenPackageElement_WhenYamlDotNetVersionsAreRead_ThenEveryFormIsSeen(string element, int expectedVersions)
    {
        var document = XDocument.Parse($"<Project><ItemGroup>{element}</ItemGroup></Project>");

        Assert.Equal(expectedVersions, YamlDotNetVersions(document).Count(p => Version.TryParse(p.Version, out var v) && v.Major != CeilingMajor));
    }

    private static List<(string Kind, string Version)> YamlDotNetVersions(XDocument document)
    {
        var result = new List<(string Kind, string Version)>();
        foreach (var element in document.Descendants().Where(AspireAuditSuppressionGuardTests.IsPackageElement))
        {
            if (!AspireAuditSuppressionGuardTests.ItemPackageIds(element).Contains(PackageId, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var name in new[] { "Version", "VersionOverride" })
            {
                if (AspireAuditSuppressionGuardTests.Metadata(element, name) is { } value)
                {
                    result.Add(($"{element.Name.LocalName} {name}", value.Trim()));
                }
            }
        }

        return result;
    }
}
