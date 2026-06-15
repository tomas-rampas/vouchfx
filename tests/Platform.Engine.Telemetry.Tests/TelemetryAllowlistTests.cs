// Platform.Engine.Telemetry.Tests — the PRIVACY ALLOWLIST gate (S10-G-04).
//
// THE central privacy gate.  TelemetryEvent's public property names MUST equal a
// hard-coded expected allowlist exactly.  Adding ANY property (even an innocuous one)
// fails this test and forces a deliberate privacy review — the only way a new field can
// reach the wire is by also editing the expected set below, which is a reviewed act.
//
// This is what makes "sensitive data is provably never sent" enforceable rather than
// aspirational: there is no property that can carry test contents, captured values,
// secrets, URLs, image names, scenario names, step ids, or observations, and this test
// guarantees no such property is ever added without review.

using System.Reflection;
using Xunit;

namespace Platform.Engine.Telemetry.Tests;

public sealed class TelemetryAllowlistTests
{
    /// <summary>
    /// The EXACT, closed set of public property names allowed on
    /// <see cref="TelemetryEvent"/>.  Editing this set is a deliberate privacy decision
    /// — any drift between this set and the record fails the test below.
    /// </summary>
    private static readonly string[] ExpectedAllowlist =
    {
        nameof(TelemetryEvent.SchemaVersion),
        nameof(TelemetryEvent.Timestamp),
        nameof(TelemetryEvent.InstallId),
        nameof(TelemetryEvent.ToolVersion),
        nameof(TelemetryEvent.EngineVersion),
        nameof(TelemetryEvent.DotnetVersion),
        nameof(TelemetryEvent.RunCount),
        nameof(TelemetryEvent.ScenarioCount),
        nameof(TelemetryEvent.StepVerdicts),
        nameof(TelemetryEvent.ScenarioVerdicts),
        nameof(TelemetryEvent.StepFamilies),
        nameof(TelemetryEvent.StepProviders),
        nameof(TelemetryEvent.StartupMs),
        nameof(TelemetryEvent.TimeToFirstTestMs),
    };

    [Fact]
    public void TelemetryEvent_PublicProperties_MatchTheAllowlistExactly()
    {
        var actual = PublicInstancePropertyNames(typeof(TelemetryEvent));

        // Exact equality (order-independent): no property may be added, removed, or
        // renamed without updating ExpectedAllowlist — a reviewed privacy decision.
        var expected = ExpectedAllowlist.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var found = actual.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, found);
    }

    [Fact]
    public void TelemetryVerdictCounts_PublicProperties_AreOnlyTheFourTaxonomyCounts()
    {
        var actual = PublicInstancePropertyNames(typeof(TelemetryVerdictCounts))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // The nested counts record may carry ONLY the four §12.1 verdict counts —
        // never a label of WHAT passed/failed.
        Assert.Equal(
            new[]
            {
                nameof(TelemetryVerdictCounts.EnvError),
                nameof(TelemetryVerdictCounts.Fail),
                nameof(TelemetryVerdictCounts.Inconclusive),
                nameof(TelemetryVerdictCounts.Pass),
            },
            actual);
    }

    [Fact]
    public void TelemetryEvent_HasNoPropertyWhoseNameSuggestsContentOrIdentity()
    {
        // A defensive belt-and-braces scan: even if ExpectedAllowlist were edited
        // carelessly, no property name may hint at content/identity leakage.  This
        // documents the intent and catches an obviously-wrong addition.
        string[] forbiddenSubstrings =
        {
            "url", "uri", "address", "host", "image", "secret", "token", "password",
            "captur", "name", "scenarioId", "stepId", "body", "payload", "content",
            "text", "observation", "value",
        };

        foreach (var prop in PublicInstancePropertyNames(typeof(TelemetryEvent)))
        {
            foreach (var bad in forbiddenSubstrings)
            {
                Assert.False(
                    prop.Contains(bad, StringComparison.OrdinalIgnoreCase),
                    $"TelemetryEvent.{prop} name contains forbidden substring '{bad}'.");
            }
        }
    }

    private static string[] PublicInstancePropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            // Exclude the compiler-synthesised record equality contract.
            .Where(p => p.Name != "EqualityContract")
            .Select(p => p.Name)
            .ToArray();
}
