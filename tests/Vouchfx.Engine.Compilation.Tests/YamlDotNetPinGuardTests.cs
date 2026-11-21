using System.Xml.Linq;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Fails when the central <c>YamlDotNet</c> pin in <c>Directory.Packages.props</c> leaves major 16,
/// the ceiling for the whole v1.x engine series.
/// </summary>
/// <remarks>
/// <para>
/// YamlDotNet types are part of the frozen <c>Vouchfx.Sdk</c> v1 surface (<c>IStepBinder&lt;T&gt;.Bind</c>
/// takes a <c>YamlNode</c>), and a new assembly major makes the CLI refuse external providers built
/// against YamlDotNet 16. This also keeps the Aspire hold honest: Aspire 13.6.0 and 13.6.5 require
/// YamlDotNet 18.1.0 or later, so a security update to 13.6.5 that bumps YamlDotNet in the same change
/// fails here instead of landing. The check reads source pins only, like <c>MySqlConnectorPinGuardTests</c>.
/// It does not open a nupkg, and it does not evaluate MSBuild, so a version set through a property
/// fails closed as not a plain numeric version. Raising the ceiling is a v2 engine decision.
/// </para>
/// </remarks>
public sealed class YamlDotNetPinGuardTests
{
    private const string PackageId = "YamlDotNet";

    private const int CeilingMajor = 16;

    private readonly ITestOutputHelper _output;

    public YamlDotNetPinGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GivenDirectoryPackagesProps_WhenYamlDotNetPinIsRead_ThenItIsOnTheV1Major()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot.Resolve(), "Directory.Packages.props"));
        var versions = document
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageVersion")
            .Where(element => string.Equals((string?)element.Attribute("Include"), PackageId, StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Version"))
            .ToList();

        var versionText = Assert.Single(versions)?.Trim();
        _output.WriteLine($"Central {PackageId} PackageVersion: {versionText}");
        Assert.True(
            Version.TryParse(versionText, out var parsed),
            $"Directory.Packages.props {PackageId} Version '{versionText}' is not a plain numeric version, so this guard "
            + "cannot check the ceiling and fails closed.");
        Assert.True(
            parsed!.Major == CeilingMajor,
            $"Directory.Packages.props pins {PackageId} {versionText}, outside major {CeilingMajor}. YamlDotNet is held at "
            + $"{CeilingMajor}.x for the whole v1.x engine series because its types are part of the frozen Vouchfx.Sdk v1 "
            + "surface, and Aspire 13.6.0 and later need YamlDotNet 18.1.0 or later. Revert the bump. Raising the ceiling "
            + "is a v2 engine decision and needs a deliberate change to this test.");
    }
}
