// Tests for ScenarioIsolationFactory (state-reset generalisation): proper name+type
// dispatch over plain collections, unit-testable without a topology or Docker.
//
// Run with: dotnet test --filter "requires!=docker&FullyQualifiedName~ScenarioIsolationFactory"

using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Unit tests for <see cref="ScenarioIsolationFactory.Create"/>.
/// </summary>
public sealed class ScenarioIsolationFactoryTests
{
    private static Dictionary<string, string> Types(
        params (string Name, string Type)[] entries) =>
        entries.ToDictionary(e => e.Name, e => e.Type, StringComparer.Ordinal);

    private static Dictionary<string, object> Services(
        params (string Name, object Value)[] entries) =>
        entries.ToDictionary(e => e.Name, e => e.Value, StringComparer.Ordinal);

    // ── Zero resettable dependencies ──────────────────────────────────────────

    /// <summary>An empty topology (no dependencies at all) yields <see cref="NullScenarioIsolation"/>.</summary>
    [Fact]
    public void NoDependencies_ReturnsNullScenarioIsolation()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string>(),
            Types(),
            Services());

        Assert.IsType<NullScenarioIsolation>(result);
    }

    /// <summary>A topology whose only dependency is a message broker (no reset needed) yields Null.</summary>
    [Fact]
    public void BrokerOnlyTopology_ReturnsNullScenarioIsolation()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "events" },
            Types(("events", "kafka")),
            Services(("events", "localhost:9092")));

        Assert.IsType<NullScenarioIsolation>(result);
    }

    // ── Single resettable relational dependency ───────────────────────────────

    /// <summary>A single Postgres dependency yields a <see cref="RespawnRelationalIsolation"/> directly.</summary>
    [Fact]
    public void SinglePostgresDependency_ReturnsRespawnRelationalIsolation()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "ordersdb" },
            Types(("ordersdb", "postgres")),
            Services(("ordersdb", "Host=localhost;Database=orders")));

        Assert.IsType<RespawnRelationalIsolation>(result);
    }

    /// <summary>A single SQL Server dependency yields a <see cref="RespawnRelationalIsolation"/> directly.</summary>
    [Fact]
    public void SingleSqlServerDependency_ReturnsRespawnRelationalIsolation()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "testdb" },
            Types(("testdb", "sqlserver")),
            Services(("testdb", "Server=localhost;Database=test")));

        Assert.IsType<RespawnRelationalIsolation>(result);
    }

    /// <summary>A single MySQL dependency yields a <see cref="RespawnRelationalIsolation"/> directly.</summary>
    [Fact]
    public void SingleMysqlDependency_ReturnsRespawnRelationalIsolation()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "testdb" },
            Types(("testdb", "mysql")),
            Services(("testdb", "Server=localhost;Database=test")));

        Assert.IsType<RespawnRelationalIsolation>(result);
    }

    /// <summary>Type matching is case-insensitive.</summary>
    [Theory]
    [InlineData("Postgres")]
    [InlineData("POSTGRES")]
    [InlineData("SqlServer")]
    [InlineData("SQLSERVER")]
    [InlineData("MySql")]
    [InlineData("MYSQL")]
    public void TypeMatching_IsCaseInsensitive(string declaredType)
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "dep" },
            Types(("dep", declaredType)),
            Services(("dep", "Host=localhost;Database=test")));

        Assert.IsType<RespawnRelationalIsolation>(result);
    }

    // ── Not-yet-wired store types (mongodb / redis / elasticsearch) ───────────

    // NOTE (follow-up chunk): mongodb/redis/elasticsearch resetters are NOT wired in
    // this chunk — ScenarioIsolationFactory deliberately skips them for now, so a
    // topology whose only dependency is one of these three currently yields
    // NullScenarioIsolation (no reset happens between scenarios).  When the follow-up
    // chunk lands their resetters, these three assertions flip to the store-specific
    // isolation type, mirroring the relational assertions above.

    /// <summary>A MongoDB-only topology currently yields Null (resetter lands in a later chunk).</summary>
    [Fact]
    public void MongoDbOnlyTopology_CurrentlySkipped_ReturnsNullScenarioIsolation()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "docs" },
            Types(("docs", "mongodb")),
            Services(("docs", "mongodb://localhost:27017")));

        Assert.IsType<NullScenarioIsolation>(result);
    }

    /// <summary>A Redis-only topology currently yields Null (resetter lands in a later chunk).</summary>
    [Fact]
    public void RedisOnlyTopology_CurrentlySkipped_ReturnsNullScenarioIsolation()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "cache" },
            Types(("cache", "redis")),
            Services(("cache", "localhost:6379")));

        Assert.IsType<NullScenarioIsolation>(result);
    }

    /// <summary>An Elasticsearch-only topology currently yields Null (resetter lands in a later chunk).</summary>
    [Fact]
    public void ElasticsearchOnlyTopology_CurrentlySkipped_ReturnsNullScenarioIsolation()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "search" },
            Types(("search", "elasticsearch")),
            Services(("search", "http://localhost:9200")));

        Assert.IsType<NullScenarioIsolation>(result);
    }

    // ── Composite (more than one resettable dependency) ───────────────────────

    /// <summary>
    /// Two resettable dependencies (Postgres + SQL Server) yield a
    /// <see cref="CompositeScenarioIsolation"/>.
    /// </summary>
    [Fact]
    public void TwoResettableDependencies_ReturnsCompositeScenarioIsolation()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "ordersdb", "auditdb" },
            Types(("ordersdb", "postgres"), ("auditdb", "sqlserver")),
            Services(("ordersdb", "Host=localhost;Database=orders"), ("auditdb", "Server=localhost;Database=audit")));

        Assert.IsType<CompositeScenarioIsolation>(result);
    }

    /// <summary>
    /// A mix of a resettable dependency and a non-resettable one (broker) yields the
    /// single resettable isolation directly — the broker contributes nothing.
    /// </summary>
    [Fact]
    public void ResettablePlusBroker_ReturnsSingleIsolation_NotComposite()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "ordersdb", "events" },
            Types(("ordersdb", "postgres"), ("events", "kafka")),
            Services(("ordersdb", "Host=localhost;Database=orders"), ("events", "localhost:9092")));

        Assert.IsType<RespawnRelationalIsolation>(result);
    }

    // ── Defensive skip cases ───────────────────────────────────────────────────

    /// <summary>A dependency with no entry in the type map is skipped defensively.</summary>
    [Fact]
    public void MissingTypeEntry_IsSkipped()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "ordersdb" },
            Types(), // no type declared for "ordersdb"
            Services(("ordersdb", "Host=localhost;Database=orders")));

        Assert.IsType<NullScenarioIsolation>(result);
    }

    /// <summary>A dependency whose discovered value is missing from the services map is skipped.</summary>
    [Fact]
    public void MissingDiscoveredValue_IsSkipped()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "ordersdb" },
            Types(("ordersdb", "postgres")),
            Services()); // no discovered value for "ordersdb"

        Assert.IsType<NullScenarioIsolation>(result);
    }

    /// <summary>A dependency whose discovered value is not a string is skipped.</summary>
    [Fact]
    public void NonStringDiscoveredValue_IsSkipped()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "ordersdb" },
            Types(("ordersdb", "postgres")),
            Services(("ordersdb", 42))); // not a string

        Assert.IsType<NullScenarioIsolation>(result);
    }

    /// <summary>A dependency whose discovered value is an empty string is skipped.</summary>
    [Fact]
    public void EmptyStringDiscoveredValue_IsSkipped()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "ordersdb" },
            Types(("ordersdb", "postgres")),
            Services(("ordersdb", string.Empty)));

        Assert.IsType<NullScenarioIsolation>(result);
    }

    /// <summary>An unrecognised type string is skipped (mirrors an unhandled future store type).</summary>
    [Fact]
    public void UnrecognisedType_IsSkipped()
    {
        var result = ScenarioIsolationFactory.Create(
            new List<string> { "dep" },
            Types(("dep", "some-future-store")),
            Services(("dep", "conn-string")));

        Assert.IsType<NullScenarioIsolation>(result);
    }

    // ── Ordering ────────────────────────────────────────────────────────────

    /// <summary>
    /// The composite's children are ordered by <c>dependencyNames</c> declaration
    /// order, not by dictionary iteration order.
    /// </summary>
    [Fact]
    public void CompositeChildren_FollowDependencyNamesOrder()
    {
        // Declare "auditdb" BEFORE "ordersdb" in dependencyNames, but insert the
        // dictionaries in the opposite order — the factory must still follow
        // dependencyNames, not dictionary enumeration order.
        var types = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ordersdb"] = "postgres",
            ["auditdb"] = "sqlserver",
        };
        var services = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["ordersdb"] = "Host=localhost;Database=orders",
            ["auditdb"] = "Server=localhost;Database=audit",
        };

        var result = ScenarioIsolationFactory.Create(
            new List<string> { "auditdb", "ordersdb" },
            types,
            services);

        Assert.IsType<CompositeScenarioIsolation>(result);
    }

    // ── Constructor / argument validation ─────────────────────────────────────

    /// <summary>A <see langword="null"/> dependencyNames list throws.</summary>
    [Fact]
    public void NullDependencyNames_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ScenarioIsolationFactory.Create(null!, Types(), Services()));
    }

    /// <summary>A <see langword="null"/> dependencyTypes map throws.</summary>
    [Fact]
    public void NullDependencyTypes_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ScenarioIsolationFactory.Create(new List<string>(), null!, Services()));
    }

    /// <summary>A <see langword="null"/> discoveredServices map throws.</summary>
    [Fact]
    public void NullDiscoveredServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ScenarioIsolationFactory.Create(new List<string>(), Types(), null!));
    }
}
