// Vouchfx.Engine.Planning.Tests — DependencyKindStepMapDriftTests (M3 Planner, REQ-005,
// flag F-18).
//
// The whole reason REQ-005 survived review: the dependency-kind -> candidate-step-type
// mapping is hand-maintained (derivation from the catalogue `provider` token is provably
// unsound — mailpit -> mail-expect.smtp, minio -> storage-assert.s3), so nothing but an
// explicit CI gate stops it drifting from either (a) the registration it targets, or (b) the
// dependency-kind vocabulary it must fully cover.
//
// flag F-18: these tests build their registry via PlannerTestFixtures.BuildCoreRegistry(),
// which lives entirely inside this project. Nothing here forces that registry to match
// Vouchfx.Cli.ProviderRegistryFactory.BuildCoreRegistry() — the registration the SHIPPED
// `vouchfx` executable actually freezes. tests/Vouchfx.Cli.Tests/PlanRegistrationParityTests.cs
// asserts the CLI's own registry against the identical literal 25-entry array mirrored below;
// if either registry's provider set ever drifts from this literal, that file's own assertion
// (or this one) fails — closing the gap without a cross-project reference in either direction.

using System.Diagnostics.CodeAnalysis;
using Vouchfx.Engine.Compilation.Scaffold;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

// Type-scoped suppressions (not a shared GlobalSuppressions.cs — see PlanExportTests.cs /
// PlanReportContractFreezeTests.cs, which establish the same pattern for this project).
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
[SuppressMessage(
    "Performance",
    "CA1861:Avoid constant arrays as arguments",
    Justification = "Inline literal arrays are the readable form for test fixtures.")]
public sealed class DependencyKindStepMapDriftTests
{
    /// <summary>
    /// The 25 Core provider dotted type keys, ordinal-sorted. Mirrored verbatim in
    /// <c>Vouchfx.Cli.Tests\PlanRegistrationParityTests.cs</c> — see that file's own copy of
    /// this comment for why the same literal lives in both places (flag F-18).
    /// </summary>
    private static readonly string[] s_expectedCoreTypeKeys =
    {
        "cache-assert.elasticsearch",
        "cache-assert.redis",
        "db-assert.dynamodb",
        "db-assert.mongodb",
        "db-assert.mysql",
        "db-assert.postgres",
        "db-assert.sqlserver",
        "http.rest",
        "http.soap",
        "mail-expect.smtp",
        "metrics-assert.prometheus",
        "mq-expect.azureservicebus",
        "mq-expect.kafka",
        "mq-expect.nats",
        "mq-expect.rabbitmq",
        "mq-expect.redis",
        "mq-publish.azureservicebus",
        "mq-publish.kafka",
        "mq-publish.nats",
        "mq-publish.rabbitmq",
        "mq-publish.redis",
        "script.csharp",
        "storage-assert.s3",
        "trace-expect.otlp",
        "webhook-listen.http",
    };

    [Fact]
    public void PlannerTestFixtures_BuildCoreRegistry_MatchesTheExpectedTypeKeySet()
    {
        var registry = PlannerTestFixtures.BuildCoreRegistry();

        var actual = registry.All
            .Select(p => $"{p.Kind.Family}.{p.Kind.Provider}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(s_expectedCoreTypeKeys, actual);
    }

    [Fact]
    public void EveryMappedStepType_ExistsInTheCurrentRegistration()
    {
        var registry = PlannerTestFixtures.BuildCoreRegistry();

        foreach (var (kind, candidates) in DependencyKindStepMap.CandidateStepTypes)
        {
            foreach (var type in candidates)
            {
                Assert.True(
                    registry.TryGet(type, out _),
                    $"DependencyKindStepMap maps dependency kind '{kind}' to step type "
                    + $"'{type}', which is not registered in the current registration.");
            }
        }

        // REQ-005's hardcoded, non-derived service rule (VocabularyGapAnalyser /
        // HandOffHints both suggest this literal type for a service with no http.* step) is
        // guarded by the same drift discipline as the dependency mapping above.
        Assert.True(
            registry.TryGet("http.rest", out _),
            "REQ-005's service-missing-http-step rule suggests 'http.rest', which is not "
            + "registered in the current registration.");
    }

    [Fact]
    public void EveryKnownDependencyKind_IsMappedOrExplicitlyExempt()
    {
        foreach (var kind in KnownDependencyKinds.All)
        {
            var isMapped = DependencyKindStepMap.CandidateStepTypes.ContainsKey(kind);
            var isExempt = DependencyKindStepMap.KindsWithoutAssertingProvider.Contains(kind);

            Assert.True(
                isMapped || isExempt,
                $"Dependency kind '{kind}' (from KnownDependencyKinds.All) is neither mapped "
                + "in DependencyKindStepMap.CandidateStepTypes nor explicitly exempted in "
                + "KindsWithoutAssertingProvider. Adding a dependency kind must force a "
                + "mapping decision in the same change.");
        }
    }

    [Fact]
    public void EveryRegisteredFamily_IsClassifiedInStepFamilyRoles()
    {
        var registry = PlannerTestFixtures.BuildCoreRegistry();
        var families = registry.All
            .Select(p => p.Kind.Family)
            .Distinct(StringComparer.Ordinal);

        foreach (var family in families)
        {
            var isAsserting = StepFamilyRoles.AssertingFamilies.Contains(family);
            var isNonAsserting = StepFamilyRoles.NonAssertingFamilies.Contains(family);

            Assert.True(
                isAsserting ^ isNonAsserting,
                $"Step family '{family}' is registered in the current registration but is "
                + "not classified in exactly one of StepFamilyRoles.AssertingFamilies / "
                + "NonAssertingFamilies (a new family must never silently default to "
                + "either side).");
        }
    }
}
