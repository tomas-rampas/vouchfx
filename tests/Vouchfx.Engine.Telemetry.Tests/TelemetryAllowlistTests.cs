// Vouchfx.Engine.Telemetry.Tests — the PRIVACY ALLOWLIST gate (S10-G-04).
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

namespace Vouchfx.Engine.Telemetry.Tests;

public sealed class TelemetryAllowlistTests
{
    /// <summary>
    /// The v1 allowlist — the fourteen properties <see cref="TelemetryEvent"/> carried
    /// before issue #588.  Frozen: the reference backend
    /// (vouchfx-telemetry-backend, Ingestion/AllowlistParser.cs) parses schema version 1
    /// with <c>UnmappedMemberHandling.Disallow</c> and refuses the WHOLE batch at the
    /// first field it does not recognise, so this row must never change — a v1 consumer
    /// depends on it staying exactly this shape forever.
    /// </summary>
    private static readonly string[] AllowlistV1 =
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

    /// <summary>
    /// The v2 allowlist (issue #588): the fourteen v1 properties plus
    /// <see cref="TelemetryEvent.SkippedEventLines"/>, written out in full rather than
    /// derived from the v1 row.  The reference backend parses the schema versions it knows
    /// strictly, and only versions above the highest it knows leniently
    /// (vouchfx-telemetry-backend#30).  A shipped version-2 row is therefore frozen
    /// exactly as version 1 is.
    /// </summary>
    private static readonly string[] AllowlistV2 =
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
        nameof(TelemetryEvent.SkippedEventLines),
    };

    /// <summary>
    /// THE versioned allowlist table — one row per <see cref="TelemetryEvent.SchemaVersion"/>
    /// ever shipped.  Every row is frozen once shipped (a consumer may be reading events
    /// tagged with that version); a NEW field always means a NEW row here PLUS a
    /// <see cref="TelemetryEventBuilder.CurrentSchemaVersion"/> bump — the two tests below
    /// enforce both halves of that rule so a field can never change without deliberately
    /// touching this table.
    /// </summary>
    private static readonly Dictionary<int, string[]> AllowlistByVersion = new()
    {
        [1] = AllowlistV1,
        [2] = AllowlistV2,
    };

    /// <summary>
    /// The CLR type pinned for each <see cref="TelemetryEvent"/> property at the CURRENT
    /// schema version.  Widening or narrowing a property's type (e.g. <c>int</c> to
    /// <c>long</c> or <c>string</c>) changes the wire shape exactly like adding a field,
    /// so it must fail here just as hard.
    /// </summary>
    private static readonly Dictionary<string, Type> ExpectedTypesForCurrentVersion = new()
    {
        [nameof(TelemetryEvent.SchemaVersion)] = typeof(int),
        [nameof(TelemetryEvent.Timestamp)] = typeof(DateTimeOffset),
        [nameof(TelemetryEvent.InstallId)] = typeof(Guid),
        [nameof(TelemetryEvent.ToolVersion)] = typeof(string),
        [nameof(TelemetryEvent.EngineVersion)] = typeof(string),
        [nameof(TelemetryEvent.DotnetVersion)] = typeof(string),
        [nameof(TelemetryEvent.RunCount)] = typeof(int),
        [nameof(TelemetryEvent.ScenarioCount)] = typeof(int),
        [nameof(TelemetryEvent.StepVerdicts)] = typeof(TelemetryVerdictCounts),
        [nameof(TelemetryEvent.ScenarioVerdicts)] = typeof(TelemetryVerdictCounts),
        [nameof(TelemetryEvent.StepFamilies)] = typeof(IReadOnlyDictionary<string, int>),
        [nameof(TelemetryEvent.StepProviders)] = typeof(IReadOnlyDictionary<string, int>),
        [nameof(TelemetryEvent.StartupMs)] = typeof(long),
        [nameof(TelemetryEvent.TimeToFirstTestMs)] = typeof(long),
        [nameof(TelemetryEvent.SkippedEventLines)] = typeof(int),
    };

    [Fact]
    public void CurrentSchemaVersion_EqualsTheHighestVersionRowInTheAllowlistTable()
    {
        var highestTableVersion = AllowlistByVersion.Keys.Max();

        Assert.True(
            TelemetryEventBuilder.CurrentSchemaVersion == highestTableVersion,
            $"TelemetryEventBuilder.CurrentSchemaVersion ({TelemetryEventBuilder.CurrentSchemaVersion}) must "
            + $"equal the highest row in AllowlistByVersion ({highestTableVersion}). A TelemetryEvent field "
            + "change needs BOTH a new version row here AND a CurrentSchemaVersion bump: the reference "
            + "backend parses schema version 1 strictly (UnmappedMemberHandling.Disallow) and refuses the "
            + "whole batch on an unrecognised field, so an unversioned field change would silently break "
            + "every v1 consumer.");
    }

    [Fact]
    public void TelemetryEvent_PublicProperties_MatchTheAllowlistForCurrentSchemaVersion()
    {
        var currentVersion = TelemetryEventBuilder.CurrentSchemaVersion;

        Assert.True(
            AllowlistByVersion.TryGetValue(currentVersion, out var expectedForCurrentVersion),
            $"No AllowlistByVersion row exists for schema version {currentVersion}. Add one when bumping "
            + "CurrentSchemaVersion — a field change needs a new version row and a bump, because the "
            + "reference backend parses schema version 1 strictly and refuses an unrecognised field.");

        var actual = PublicInstancePropertyNames(typeof(TelemetryEvent))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var expected = expectedForCurrentVersion!
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            expected.SequenceEqual(actual),
            "TelemetryEvent's public properties must match AllowlistByVersion's row for the CURRENT schema "
            + $"version ({currentVersion}) exactly. Adding, removing, or renaming a property needs a new "
            + "version row and a CurrentSchemaVersion bump, because the reference backend parses schema "
            + $"version 1 strictly and refuses an unrecognised field.\nExpected: {string.Join(", ", expected)}"
            + $"\nActual:   {string.Join(", ", actual)}");
    }

    [Fact]
    public void TelemetryEvent_PropertyTypes_MatchThePinnedShapeForCurrentSchemaVersion()
    {
        var actualTypesByName = typeof(TelemetryEvent)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .ToDictionary(p => p.Name, p => p.PropertyType);

        foreach (var (name, expectedType) in ExpectedTypesForCurrentVersion)
        {
            Assert.True(
                actualTypesByName.TryGetValue(name, out var actualType),
                $"TelemetryEvent has no property named '{name}' (ExpectedTypesForCurrentVersion is stale).");

            Assert.True(
                expectedType == actualType,
                $"TelemetryEvent.{name} changed CLR type: expected {expectedType}, found {actualType}. A "
                + "type change alters the wire shape exactly like adding or removing a field, so it needs "
                + "the same version bump — the reference backend parses schema version 1 strictly.");
        }
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
