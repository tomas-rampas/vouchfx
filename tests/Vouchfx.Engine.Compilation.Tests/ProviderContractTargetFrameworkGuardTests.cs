using System.Xml.Linq;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Fails when the provider contract libraries or the two example provider templates stop
/// targeting <c>net8.0</c>, or when the repository-wide default moves off <c>net10.0</c>.
/// </summary>
/// <remarks>
/// <para>
/// External providers built against the frozen v1 contract target <c>net8.0</c>, and
/// <c>vouchfx provider install</c> selects <c>net8.0</c> assets. The five published contract
/// libraries (<c>Vouchfx.Sdk</c>, <c>Vouchfx.Sdk.Testing</c>, <c>Vouchfx.Engine.Abstractions</c>,
/// <c>.Authoring</c> and <c>.Compilation</c>) therefore keep a <c>net8.0</c> asset, and the two
/// provider templates (<c>Example.Steps.Echo</c> and <c>Example.Steps.Hello</c>) build a provider
/// that the installer accepts. They all take <c>VouchfxProviderContractTargetFramework</c> from the
/// root <c>Directory.Build.props</c>. Raising that property to <c>net10.0</c> would leave every
/// build, test and golden file green while new packages stopped loading in .NET 8 CLIs, so this
/// guard pins it.
/// </para>
/// <para>
/// This is a bounded text check over MSBuild XML. It reads the root <c>Directory.Build.props</c>
/// and the seven named project files. It does not evaluate MSBuild properties, so a project must
/// spell its framework as <c>$(VouchfxProviderContractTargetFramework)</c> or literally as
/// <c>net8.0</c>. Missing files and a missing property fail the test, so a moved project cannot
/// make the guard pass without checking anything.
/// </para>
/// </remarks>
public sealed class ProviderContractTargetFrameworkGuardTests
{
    private const string BuildPropsName = "Directory.Build.props";

    private const string ContractPropertyName = "VouchfxProviderContractTargetFramework";

    private const string ContractFramework = "net8.0";

    private const string DefaultFramework = "net10.0";

    private const string ContractPropertyReference = "$(" + ContractPropertyName + ")";

    private const string WhyHint =
        "Providers built against the frozen v1 contract target net8.0, and `vouchfx provider install` "
        + "selects net8.0 assets, so the provider contract libraries and the example provider templates "
        + "must keep building for net8.0 for the whole v1.x series.";

    private static readonly string[] ContractProjects =
    {
        "src/Sdk/Vouchfx.Sdk/Vouchfx.Sdk.csproj",
        "src/Sdk/Vouchfx.Sdk.Testing/Vouchfx.Sdk.Testing.csproj",
        "src/Engine/Vouchfx.Engine.Abstractions/Vouchfx.Engine.Abstractions.csproj",
        "src/Engine/Vouchfx.Engine.Authoring/Vouchfx.Engine.Authoring.csproj",
        "src/Engine/Vouchfx.Engine.Compilation/Vouchfx.Engine.Compilation.csproj",
        "examples/Example.Steps.Echo/Example.Steps.Echo.csproj",
        "examples/Example.Steps.Hello/Example.Steps.Hello.csproj",
    };

    private readonly ITestOutputHelper _output;

    public ProviderContractTargetFrameworkGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GivenRootBuildProps_WhenTheContractPropertyIsRead_ThenItIsExactlyNet8()
    {
        var values = PropertyValues(LoadBuildProps(), ContractPropertyName);
        _output.WriteLine($"{ContractPropertyName}: [{string.Join(", ", values)}]");

        Assert.True(
            values.Count == 1,
            $"{BuildPropsName} must define {ContractPropertyName} exactly once, found {values.Count}. {WhyHint}");
        Assert.True(
            string.Equals(values[0], ContractFramework, StringComparison.Ordinal),
            $"{BuildPropsName} sets {ContractPropertyName} to '{values[0]}', but it must be '{ContractFramework}'. "
            + $"{WhyHint} Restore '{ContractFramework}'; move the engine, CLI and tests with TargetFramework instead.");
    }

    [Fact]
    public void GivenRootBuildProps_WhenTheDefaultTargetFrameworkIsRead_ThenItIsNet10()
    {
        var values = PropertyValues(LoadBuildProps(), "TargetFramework");
        _output.WriteLine($"TargetFramework: [{string.Join(", ", values)}]");

        Assert.True(
            values.Count == 1,
            $"{BuildPropsName} must define the repository-wide default TargetFramework exactly once, found {values.Count}.");
        Assert.True(
            string.Equals(values[0], DefaultFramework, StringComparison.Ordinal),
            $"{BuildPropsName} sets the repository-wide default TargetFramework to '{values[0]}', but the engine, "
            + $"providers, CLI and tests target '{DefaultFramework}'. Only the projects using {ContractPropertyName} "
            + $"stay on {ContractFramework}.");
    }

    [Fact]
    public void GivenContractProjects_WhenTheirTargetFrameworkIsRead_ThenEachUsesTheNet8ContractFramework()
    {
        var root = RepoRoot.Resolve();
        var failures = new List<string>();
        var checkedProjects = 0;

        foreach (var relative in ContractProjects)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                failures.Add($"{relative}: not found. Update {nameof(ProviderContractTargetFrameworkGuardTests)} "
                    + "if the project moved; do not drop it from the list.");
                continue;
            }

            var document = XDocument.Load(path);
            var single = PropertyValues(document, "TargetFramework");
            var multiple = PropertyValues(document, "TargetFrameworks");
            _output.WriteLine($"{relative}: TargetFramework=[{string.Join(", ", single)}] TargetFrameworks=[{string.Join(", ", multiple)}]");

            if (multiple.Count != 0)
            {
                failures.Add($"{relative}: sets TargetFrameworks, which overrides the single net8.0 target. Remove it.");
            }

            if (single.Count == 0)
            {
                failures.Add($"{relative}: sets no TargetFramework, so it inherits {DefaultFramework} from {BuildPropsName}. "
                    + $"Set <TargetFramework>{ContractPropertyReference}</TargetFramework>.");
            }

            foreach (var value in single.Where(v => v is not ContractPropertyReference and not ContractFramework))
            {
                failures.Add($"{relative}: TargetFramework is '{value}'. "
                    + $"Set <TargetFramework>{ContractPropertyReference}</TargetFramework>.");
            }

            checkedProjects++;
        }

        Assert.True(
            failures.Count == 0,
            $"Provider contract projects must target {ContractFramework}. {WhyHint} Found: [{string.Join("; ", failures)}].");
        Assert.Equal(ContractProjects.Length, checkedProjects);
    }

    private static XDocument LoadBuildProps() =>
        XDocument.Load(Path.Combine(RepoRoot.Resolve(), BuildPropsName));

    private static List<string> PropertyValues(XDocument document, string localName) =>
        document
            .Descendants()
            .Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Parent is { } parent && string.Equals(parent.Name.LocalName, "PropertyGroup", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim())
            .ToList();
}
