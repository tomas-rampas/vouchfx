using System.Reflection;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests;

// A shipped-surface change detector, not the SDK provider-implemented mutation freeze.
public sealed class AbstractionsContractFreezeTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_ABSTRACTIONS_CONTRACT";
    private const string GoldenFile = "vouchfx-engine-abstractions-public-api.v1.txt";

    [Fact]
    public void PublicApi_MatchesGolden_ByteForByte()
    {
        var actual = SdkPublicApiSignature.Build(
            typeof(Verdict).Assembly,
            "Vouchfx.Engine.Abstractions public surface — reviewed change detector, outside the SDK mutation freeze.",
            "Evolution is allowed: review compatibility and regenerate deliberately.");
        if (IsRegenRequested())
        {
            File.WriteAllText(Path.Combine(RepoRoot.Resolve(), "tests",
                "Vouchfx.Engine.Abstractions.Tests", "Golden", GoldenFile), actual);
            return;
        }

        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", GoldenFile));
        Assert.Equal(Normalise(golden), Normalise(actual));
    }

    [Fact]
    public void DefaultImplementedMembers_MatchReviewedCensus()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var actual = typeof(Verdict).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.IsVisible)
            .SelectMany(type => type.GetMethods(flags), (type, method) => (type, method))
            .Where(member => !member.method.IsAbstract)
            .Select(member => $"{member.type.FullName}.{member.method}")
            .OrderBy(signature => signature, StringComparer.Ordinal).ToArray();

        // Changing this set is legal, but must be reviewed alongside the API golden.
        string[] expected =
        {
            "Vouchfx.Engine.Abstractions.Security.ISecurityCertificateMaterial.System.Collections.Generic.IReadOnlyList`1[System.Security.Cryptography.X509Certificates.X509Certificate2] get_ClientCertificateIntermediates()",
            "Vouchfx.Engine.Abstractions.Security.ISecurityCertificateMaterial.Vouchfx.Engine.Abstractions.Secrets.SecretString get_ClientKeyPassword()",
        };
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal), string.Join(Environment.NewLine, actual));
    }

    private static bool IsRegenRequested() => GoldenRegeneration.IsRequested(RegenEnvVar);
    private static string Normalise(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
}
