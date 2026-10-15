using System.Reflection;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Authoring.Tests;

// A reviewed API change detector. This assembly is outside the SDK mutation freeze.
public sealed class AuthoringPublicApiContractTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_AUTHORING_CONTRACT";
    private const string GoldenFile = "vouchfx-engine-authoring-public-api.v1.txt";

    [Fact]
    public void PublicApi_MatchesGolden_ByteForByte()
    {
        var actual = SdkPublicApiSignature.Build(Assembly.Load("Vouchfx.Engine.Authoring"),
            "Vouchfx.Engine.Authoring public surface — reviewed change detector, outside the SDK mutation freeze.",
            "Evolution is allowed: review compatibility and regenerate deliberately.");
        if (IsRegenRequested())
        {
            File.WriteAllText(Path.Combine(RepoRoot.Resolve(), "tests", "Vouchfx.Engine.Authoring.Tests", "Golden", GoldenFile), actual);
            return;
        }

        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", GoldenFile));
        Assert.Equal(Normalise(golden), Normalise(actual));
    }

    [Fact]
    public void PublicInterfaces_HaveNoUnreviewedDefaultImplementations()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var defaults = Assembly.Load("Vouchfx.Engine.Authoring").GetTypes()
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
