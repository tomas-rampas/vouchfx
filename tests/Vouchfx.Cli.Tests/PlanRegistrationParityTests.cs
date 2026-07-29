// Vouchfx.Cli.Tests — PlanRegistrationParityTests (M3 Planner, REQ-005, flag F-18). No Docker.
//
// REQ-005's drift tests (Vouchfx.Engine.Planning.Tests\DependencyKindStepMapDriftTests.cs)
// build their own registry via PlannerTestFixtures.BuildCoreRegistry() — a 25-anchor-type
// registry that lives entirely inside Vouchfx.Engine.Planning.Tests, with nothing forcing it
// to match Vouchfx.Cli.ProviderRegistryFactory.CoreProviderAssemblies(), which is what the
// SHIPPED `vouchfx` executable actually freezes. A provider dropped from the CLI's own
// registration would leave the Planning.Tests drift test green while `vouchfx plan` emitted
// a hint for a type the binary no longer registers — precisely the failure REQ-005 exists to
// prevent.
//
// This test cannot compare the two registries directly: PlannerTestFixtures is internal to
// Vouchfx.Engine.Planning.Tests, which grants InternalsVisibleTo to nobody, and widening that
// accessibility (or adding a ProjectReference from this project to that one) are both
// forbidden shared-file edits for this todo. Instead, both registries are independently
// asserted against the SAME literal 25-entry dotted-type-key array — the identical array is
// mirrored verbatim in DependencyKindStepMapDriftTests.cs's own assertion over
// PlannerTestFixtures.BuildCoreRegistry(). If either registry's provider set ever drifts from
// this literal, that file's own assertion fails, which is exactly the drift REQ-005 needs
// caught — without a cross-project reference in either direction.

using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class PlanRegistrationParityTests
{
    /// <summary>
    /// The 25 Core provider dotted type keys, ordinal-sorted. Mirrored verbatim in
    /// <c>Vouchfx.Engine.Planning.Tests\DependencyKindStepMapDriftTests.cs</c> — see that
    /// file's own copy of this comment for why the same literal lives in both places.
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
    public void ProviderRegistryFactory_BuildCoreRegistry_MatchesTheExpectedTypeKeySet()
    {
        var registry = ProviderRegistryFactory.BuildCoreRegistry();

        var actual = registry.All
            .Select(p => $"{p.Kind.Family}.{p.Kind.Provider}")
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(s_expectedCoreTypeKeys, actual);
    }
}
