using System.Reflection;
using Xunit;

namespace Vouchfx.Tests;

// Linked into every assembly with a regeneration leaf. This collection never overlaps
// other collections in the same test process, including the actual golden-writing tests.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GoldenRegenerationEnvironment
{
    public const string Name = "Golden regeneration environment";
}

[Collection(GoldenRegenerationEnvironment.Name)]
public sealed class GoldenRegenerationPolicyTests
{
    public static IEnumerable<object?[]> PolicyCases()
    {
        var flags = new (string? Value, bool Requested)[]
        {
            ("1", true), ("true", true), ("TRUE", true), ("TrUe", true),
            (null, false), ("", false), ("false", false), ("FALSE", false),
            ("0", false), ("2", false), (" true", false), ("1 ", false),
        };
        var markers = new (string? Ci, string? GitHubActions)[]
        {
            (null, null), ("", ""), ("ci-marker-private", null),
            (null, "github-marker-private"), ("ci-marker-private", "github-marker-private"),
            ("false", null), (null, "0"), (" ", null), (null, " "),
        };

        var leaves = typeof(GoldenRegenerationPolicyTests).Assembly.GetTypes()
            .Select(type => (Type: type, Method: type.GetMethod(
                "IsRegenRequested", BindingFlags.NonPublic | BindingFlags.Static)))
            .Where(leaf => leaf.Method is not null)
            .OrderBy(leaf => leaf.Type.FullName, StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            var switchName = (string)leaf.Type.GetField(
                "RegenEnvVar", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
            foreach (var (value, requested) in flags)
            {
                foreach (var (ci, gitHubActions) in markers)
                {
                    yield return new object?[] { leaf.Type.FullName!, switchName, value, ci, gitHubActions, requested };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(PolicyCases))]
    public void ActualLeafPreservesLocalFlagsAndRefusesActiveRequestsInCi(
        string typeName, string switchName, string? value, string? ci, string? gitHubActions, bool requested)
    {
        var method = typeof(GoldenRegenerationPolicyTests).Assembly.GetType(typeName)!
            .GetMethod("IsRegenRequested", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originals = new Dictionary<string, string?>
        {
            [switchName] = Environment.GetEnvironmentVariable(switchName),
            ["CI"] = Environment.GetEnvironmentVariable("CI"),
            ["GITHUB_ACTIONS"] = Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
        };
        try
        {
            Environment.SetEnvironmentVariable(switchName, value);
            Environment.SetEnvironmentVariable("CI", ci);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", gitHubActions);
            var marker = !string.IsNullOrEmpty(ci) ? "CI"
                : !string.IsNullOrEmpty(gitHubActions) ? "GITHUB_ACTIONS" : null;
            if (requested && marker is not null)
            {
                var invocation = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, null));
                var refusal = Assert.IsType<InvalidOperationException>(invocation.InnerException);
                Assert.Equal(
                    $"Golden regeneration via {switchName} is refused while {marker} is set. "
                    + "Regenerate locally with CI markers unset.", refusal.Message);
            }
            else
            {
                Assert.Equal(requested, (bool)method.Invoke(null, null)!);
            }
        }
        finally
        {
            foreach (var (name, original) in originals)
            {
                Environment.SetEnvironmentVariable(name, original);
            }
        }
    }
}
