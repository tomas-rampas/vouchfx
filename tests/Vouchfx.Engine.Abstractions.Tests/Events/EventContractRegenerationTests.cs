using System.Security.Cryptography;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

public sealed partial class EventContractFreezeTests
{
    [Theory]
    [InlineData("CI", "true")]
    [InlineData("CI", "false")]
    [InlineData("GITHUB_ACTIONS", "true")]
    [InlineData("GITHUB_ACTIONS", "0")]
    public void EventWireGoldens_ActiveCiRegenerationRefusesBothWrites(string marker, string value)
    {
        var directory = Path.Combine(FindRepoRoot(), "tests", "Vouchfx.Engine.Abstractions.Tests", "Golden");
        var paths = new[]
        {
            Path.Combine(directory, "event-stream-wire-contract.v1.txt"),
            Path.Combine(directory, "event-stream-wire-bytes.v1.jsonl"),
        };
        var hashes = paths.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToArray();
        var originals = new Dictionary<string, string?>
        {
            [RegenEnvVar] = Environment.GetEnvironmentVariable(RegenEnvVar),
            ["CI"] = Environment.GetEnvironmentVariable("CI"),
            ["GITHUB_ACTIONS"] = Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
        };
        try
        {
            Environment.SetEnvironmentVariable(RegenEnvVar, "1");
            Environment.SetEnvironmentVariable("CI", null);
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", null);
            Environment.SetEnvironmentVariable(marker, value);
            foreach (var writer in new Action[]
                { EventWireContract_MatchesGolden_ByteForByte, EventWireBytes_ToLineMatchesGolden_ExactUtf8IncludingFinalLf })
            {
                var refusal = Assert.Throws<InvalidOperationException>(writer);
                Assert.Equal($"Golden regeneration via {RegenEnvVar} is refused while {marker} is set. "
                    + "Regenerate locally with CI markers unset.", refusal.Message);
                for (var index = 0; index < paths.Length; index++)
                {
                    Assert.Equal(hashes[index], SHA256.HashData(File.ReadAllBytes(paths[index])));
                }
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
