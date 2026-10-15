using System.Reflection;
using System.Text.Json.Serialization;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

public sealed class OrchestrationEventContractFreezeTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_ORCHESTRATION_EVENT_CONTRACT";
    private const string GoldenFile = "orchestration-event-stream-wire-contract.v1.txt";
    private static readonly Type[] s_records = { typeof(EnvironmentErrorEvent) };

    [Fact]
    public void WireContract_MatchesGolden_ByteForByte()
    {
        var actual = EventWireContractSignature.BuildSignature(s_records,
            "Vouchfx.Engine.Orchestration v1 JSON-Lines wire contract — FROZEN for v1.x.");
        if (IsRegenRequested())
        {
            File.WriteAllText(Path.Combine(RepoRoot.Resolve(), "tests",
                "Vouchfx.Engine.Orchestration.Tests", "Golden", GoldenFile), actual);
            return;
        }

        var golden = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", GoldenFile));
        Assert.Equal(Normalise(golden), Normalise(actual));
    }

    [Fact]
    public void WireContract_Census_MatchesProductionStjMappedMembers()
    {
        var differences = s_records.SelectMany(DescribeMappedMemberDifferences).ToArray();
        Assert.True(differences.Length == 0, string.Join(Environment.NewLine, differences));
    }

    [Fact]
    public void Census_DetectsUnconditionalJsonIgnore_AndActualWireRemoval()
    {
        var baseline = new BaselineProbe("detail-present");
        var ignored = new IgnoredProbe("detail-present");
        Assert.Empty(DescribeMappedMemberDifferences(typeof(BaselineProbe)));
        Assert.Contains("detail-present", EventStreamJson.ToLine(baseline), StringComparison.Ordinal);
        Assert.DoesNotContain("detail-present", EventStreamJson.ToLine(ignored), StringComparison.Ordinal);
        Assert.Contains(DescribeMappedMemberDifferences(typeof(IgnoredProbe)),
            difference => difference.Contains("Detail", StringComparison.Ordinal)
                && difference.Contains("does not map", StringComparison.Ordinal));
    }

    [Fact]
    public void Census_DetectsJsonIncludeField_AndActualWireAddition()
    {
        var included = new IncludedFieldProbe("detail-present");
        Assert.Contains("wire-addition", EventStreamJson.ToLine(included), StringComparison.Ordinal);
        Assert.Contains(DescribeMappedMemberDifferences(typeof(IncludedFieldProbe)),
            difference => difference.Contains("Added", StringComparison.Ordinal)
                && difference.Contains("cannot show", StringComparison.Ordinal));
    }

    [Fact]
    public void Census_DetectsNonPublicJsonIncludeMember_AndActualWireAddition()
    {
        var included = new IncludedPrivateProbe("detail-present");
        Assert.Contains("private-wire-addition", EventStreamJson.ToLine(included), StringComparison.Ordinal);
        Assert.Contains(DescribeMappedMemberDifferences(typeof(IncludedPrivateProbe)),
            difference => difference.Contains("Added", StringComparison.Ordinal)
                && difference.Contains("cannot show", StringComparison.Ordinal));
    }

    private static List<string> DescribeMappedMemberDifferences(Type record) =>
        EventWireContractSignature.DescribeMemberSetDifferences(record.Name,
            EventWireContractSignature.GetRenderedMemberSignatures(record),
            EventWireContractSignature.GetStjMappedMemberSignatures(record, EventStreamJson.Options));

    private sealed record BaselineProbe(string Detail);
    private sealed record IgnoredProbe([property: JsonIgnore] string Detail);
    private sealed record IncludedFieldProbe(string Detail)
    {
        [JsonInclude] public string Added = "wire-addition";
    }
    private sealed record IncludedPrivateProbe(string Detail)
    {
        [JsonInclude] private string Added => Detail + "-private-wire-addition";
    }

    // Every public record is classified, so a payload with a new naming convention
    // cannot silently escape. These records describe topology/results, not stream data.
    private static readonly string[] s_nonWireRecords =
    {
        "Vouchfx.Engine.Orchestration.EndpointSelectionNotice",
        "Vouchfx.Engine.Orchestration.EndpointTrustNotice",
        "Vouchfx.Engine.Orchestration.Environments.EnvironmentStartContext",
        "Vouchfx.Engine.Orchestration.Environments.EnvironmentStatePolicy",
        "Vouchfx.Engine.Orchestration.MappedTopology",
        "Vouchfx.Engine.Orchestration.OrchestrationErrorInfo",
        "Vouchfx.Engine.Orchestration.SecurityConfirmation",
        "Vouchfx.Engine.Orchestration.StartupTiming",
        "Vouchfx.Engine.Orchestration.TopologyRequest",
    };

    [Fact]
    public void SnapshotSet_CoversEveryPublicOrchestrationEventRecord()
    {
        var actual = typeof(EnvironmentErrorEvent).Assembly.GetTypes()
            .Where(type => type.IsVisible)
            .Where(type => type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null)
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        var classified = s_records.Select(type => type.FullName!).Concat(s_nonWireRecords)
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(classified, actual.Select(type => type.FullName!).ToArray());
    }

    private static bool IsRegenRequested()
    {
        return Vouchfx.TestSupport.GoldenRegeneration.IsRequested(RegenEnvVar);
    }

    private static string Normalise(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
}
