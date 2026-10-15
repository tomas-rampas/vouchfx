using System.Reflection;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

// A reviewed API change detector. This assembly is outside the SDK mutation freeze.
public sealed class PlanningPublicApiContractTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_PLANNING_CONTRACT";
    private const string GoldenFile = "vouchfx-engine-planning-public-api.v1.txt";

    [Fact]
    public void PublicApiMatchesGoldenByteForByte()
    {
        var actual = SdkPublicApiSignature.Build(Assembly.Load("Vouchfx.Engine.Planning"),
            "Vouchfx.Engine.Planning public surface — reviewed change detector, outside the SDK mutation freeze.",
            "Evolution is allowed: review compatibility and regenerate deliberately.");
        if (IsRegenRequested())
        {
            File.WriteAllText(Path.Combine(RepoRoot.Resolve(), "tests", "Vouchfx.Engine.Planning.Tests", "Golden", GoldenFile), actual);
            return;
        }

        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", GoldenFile));
        Assert.Equal(Normalise(golden), Normalise(actual));
    }

    [Fact]
    public void PublicInterfacesHaveNoUnreviewedDefaultImplementations()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var defaults = Assembly.Load("Vouchfx.Engine.Planning").GetTypes()
            .Where(type => type.IsInterface && type.IsVisible)
            .SelectMany(type => type.GetMethods(flags))
            .Where(method => !method.IsAbstract);
        Assert.Empty(defaults);
    }

    private static bool IsRegenRequested()
    {
        return Vouchfx.TestSupport.GoldenRegeneration.IsRequested(RegenEnvVar);
    }

    private static string Normalise(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
}
