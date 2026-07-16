// Tests for S04-A-01 (generalised) + the follow-up chunk: IScenarioIsolation,
// NullScenarioIsolation, RespawnRelationalIsolation, MongoScenarioIsolation,
// RedisScenarioIsolation, ElasticsearchScenarioIsolation.
//
// Non-docker tests cover the no-op NullScenarioIsolation and basic parameter validation
// for all three RelationalStoreKind values (Postgres, SqlServer, MySql) plus the three
// document/cache store resetters (MongoDB, Redis, Elasticsearch) — construction must
// never open a connection, so these run without Docker.
// Docker-gated tests for actual reset behaviour are in RespawnResetProofTests.cs
// (Postgres), SqlServerResetProofTests.cs, MySqlResetProofTests.cs,
// MongodbResetProofTests.cs, RedisResetProofTests.cs, ElasticsearchResetProofTests.cs,
// and MultiStoreResetProofTests.cs.
//
// Run non-docker tests:
//   dotnet test --filter "requires!=docker&FullyQualifiedName~ScenarioIsolation"

using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Unit tests for <see cref="IScenarioIsolation"/>, <see cref="NullScenarioIsolation"/>,
/// and the <see cref="RespawnRelationalIsolation"/> constructor contract across all
/// three <see cref="RelationalStoreKind"/> values (S04-A-01, generalised).
/// </summary>
public sealed class ScenarioIsolationTests
{
    // ── NullScenarioIsolation ─────────────────────────────────────────────────

    /// <summary>
    /// <see cref="NullScenarioIsolation.BeginScenarioAsync"/> completes without
    /// throwing for any <see cref="CancellationToken"/>, including
    /// <see cref="CancellationToken.None"/>.
    /// </summary>
    [Fact]
    public async Task NullScenarioIsolation_BeginScenarioAsync_IsNoOp()
    {
        var sut = new NullScenarioIsolation();

        // Should not throw, should return a completed task.
        await sut.BeginScenarioAsync(CancellationToken.None);
    }

    /// <summary>
    /// <see cref="NullScenarioIsolation.EndScenarioAsync"/> completes without
    /// throwing for any <see cref="CancellationToken"/>, including
    /// <see cref="CancellationToken.None"/>.
    /// </summary>
    [Fact]
    public async Task NullScenarioIsolation_EndScenarioAsync_IsNoOp()
    {
        var sut = new NullScenarioIsolation();

        // Should not throw, should return a completed task.
        await sut.EndScenarioAsync(CancellationToken.None);
    }

    /// <summary>
    /// Calling <see cref="NullScenarioIsolation.BeginScenarioAsync"/> and
    /// <see cref="NullScenarioIsolation.EndScenarioAsync"/> multiple times
    /// (simulating a multi-scenario suite) remains a no-op throughout.
    /// </summary>
    [Fact]
    public async Task NullScenarioIsolation_MultipleCallsAreAllNoOps()
    {
        var sut = new NullScenarioIsolation();

        for (int i = 0; i < 5; i++)
        {
            await sut.BeginScenarioAsync(CancellationToken.None);
            await sut.EndScenarioAsync(CancellationToken.None);
        }
    }

    // ── RespawnRelationalIsolation — constructor contract (all three kinds) ───

    /// <summary>
    /// Syntactically valid but unreachable connection strings, one per
    /// <see cref="RelationalStoreKind"/>, so the ctor-validation and lazy-no-connect
    /// cases below can be run identically across all three kinds.
    /// </summary>
    public static TheoryData<RelationalStoreKind, string> UnreachableConnectionStrings() => new()
    {
        { RelationalStoreKind.Postgres, "Host=unreachable;Database=test;Username=u;Password=p" },
        { RelationalStoreKind.SqlServer, "Server=unreachable;Database=test;User Id=u;Password=p;TrustServerCertificate=true" },
        { RelationalStoreKind.MySql, "Server=unreachable;Database=test;User=u;Password=p" },
    };

    /// <summary>
    /// <see cref="RespawnRelationalIsolation"/> rejects a <see langword="null"/>
    /// dependency name at construction time.
    /// </summary>
    [Theory]
    [InlineData(RelationalStoreKind.Postgres)]
    [InlineData(RelationalStoreKind.SqlServer)]
    [InlineData(RelationalStoreKind.MySql)]
    public void RespawnRelationalIsolation_NullDependencyName_Throws(RelationalStoreKind kind)
    {
        Assert.Throws<ArgumentNullException>(
            () => new RespawnRelationalIsolation(null!, kind, "Host=unreachable;Database=test"));
    }

    /// <summary>
    /// <see cref="RespawnRelationalIsolation"/> rejects an empty dependency name at
    /// construction time.
    /// </summary>
    [Theory]
    [InlineData(RelationalStoreKind.Postgres)]
    [InlineData(RelationalStoreKind.SqlServer)]
    [InlineData(RelationalStoreKind.MySql)]
    public void RespawnRelationalIsolation_EmptyDependencyName_Throws(RelationalStoreKind kind)
    {
        Assert.Throws<ArgumentException>(
            () => new RespawnRelationalIsolation(string.Empty, kind, "Host=unreachable;Database=test"));
    }

    /// <summary>
    /// <see cref="RespawnRelationalIsolation"/> rejects a <see langword="null"/>
    /// connection string at construction time.
    /// </summary>
    [Theory]
    [InlineData(RelationalStoreKind.Postgres)]
    [InlineData(RelationalStoreKind.SqlServer)]
    [InlineData(RelationalStoreKind.MySql)]
    public void RespawnRelationalIsolation_NullConnectionString_Throws(RelationalStoreKind kind)
    {
        Assert.Throws<ArgumentNullException>(
            () => new RespawnRelationalIsolation("dep", kind, null!));
    }

    /// <summary>
    /// <see cref="RespawnRelationalIsolation"/> rejects an empty connection string
    /// at construction time.
    /// </summary>
    [Theory]
    [InlineData(RelationalStoreKind.Postgres)]
    [InlineData(RelationalStoreKind.SqlServer)]
    [InlineData(RelationalStoreKind.MySql)]
    public void RespawnRelationalIsolation_EmptyConnectionString_Throws(RelationalStoreKind kind)
    {
        Assert.Throws<ArgumentException>(
            () => new RespawnRelationalIsolation("dep", kind, string.Empty));
    }

    /// <summary>
    /// <see cref="RespawnRelationalIsolation"/> is constructed successfully for a
    /// non-empty dependency name and connection string, for every
    /// <see cref="RelationalStoreKind"/>, and implements <see cref="IScenarioIsolation"/>
    /// and <see cref="IAsyncDisposable"/>.  Construction must not open a connection
    /// (lazy initialisation) — the connection string is unreachable, so any eager
    /// connect attempt would hang or throw here instead of at
    /// <c>EndScenarioAsync</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreachableConnectionStrings))]
    public async Task RespawnRelationalIsolation_ConstructsSuccessfully_ImplementsInterface(
        RelationalStoreKind kind, string connectionString)
    {
        var sut = new RespawnRelationalIsolation("dep", kind, connectionString);

        Assert.IsAssignableFrom<IScenarioIsolation>(sut);
        Assert.IsAssignableFrom<IAsyncDisposable>(sut);

        // Dispose must be safe to call even before initialisation.
        await sut.DisposeAsync();
    }

    /// <summary>
    /// Double-dispose of <see cref="RespawnRelationalIsolation"/> is idempotent
    /// and does not throw, for every <see cref="RelationalStoreKind"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreachableConnectionStrings))]
    public async Task RespawnRelationalIsolation_DoubleDispose_IsIdempotent(
        RelationalStoreKind kind, string connectionString)
    {
        var sut = new RespawnRelationalIsolation("dep", kind, connectionString);

        await sut.DisposeAsync();
        await sut.DisposeAsync(); // Must not throw.
    }

    /// <summary>
    /// <see cref="RespawnRelationalIsolation.BeginScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal, for every
    /// <see cref="RelationalStoreKind"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreachableConnectionStrings))]
    public async Task RespawnRelationalIsolation_BeginAfterDispose_ThrowsObjectDisposedException(
        RelationalStoreKind kind, string connectionString)
    {
        var sut = new RespawnRelationalIsolation("dep", kind, connectionString);
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.BeginScenarioAsync(CancellationToken.None));
    }

    /// <summary>
    /// <see cref="RespawnRelationalIsolation.EndScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal, for every
    /// <see cref="RelationalStoreKind"/> — mirrors the Begin case above so both
    /// entry points are proven guarded.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreachableConnectionStrings))]
    public async Task RespawnRelationalIsolation_EndAfterDispose_ThrowsObjectDisposedException(
        RelationalStoreKind kind, string connectionString)
    {
        var sut = new RespawnRelationalIsolation("dep", kind, connectionString);
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.EndScenarioAsync(CancellationToken.None));
    }

    /// <summary>
    /// <see cref="RespawnRelationalIsolation.BeginScenarioAsync"/> is a validated
    /// no-op before any reset has happened, for every <see cref="RelationalStoreKind"/>
    /// — it must not attempt to connect (the connection string is unreachable).
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreachableConnectionStrings))]
    public async Task RespawnRelationalIsolation_BeginScenarioAsync_DoesNotConnect(
        RelationalStoreKind kind, string connectionString)
    {
        var sut = new RespawnRelationalIsolation("dep", kind, connectionString);

        // Should not throw and should not attempt to open the unreachable connection.
        await sut.BeginScenarioAsync(CancellationToken.None);

        await sut.DisposeAsync();
    }

    // ── MongoScenarioIsolation — constructor contract ─────────────────────────

    /// <summary>Syntactically valid but unreachable connection string for lazy-no-connect tests.</summary>
    private const string UnreachableMongoConnectionString = "mongodb://unreachable-host:27017/testdb";

    /// <summary>
    /// <see cref="MongoScenarioIsolation"/> rejects a <see langword="null"/> dependency name.
    /// </summary>
    [Fact]
    public void MongoScenarioIsolation_NullDependencyName_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MongoScenarioIsolation(null!, UnreachableMongoConnectionString));
    }

    /// <summary>
    /// <see cref="MongoScenarioIsolation"/> rejects an empty dependency name.
    /// </summary>
    [Fact]
    public void MongoScenarioIsolation_EmptyDependencyName_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new MongoScenarioIsolation(string.Empty, UnreachableMongoConnectionString));
    }

    /// <summary>
    /// <see cref="MongoScenarioIsolation"/> rejects a <see langword="null"/> connection string.
    /// </summary>
    [Fact]
    public void MongoScenarioIsolation_NullConnectionString_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MongoScenarioIsolation("dep", null!));
    }

    /// <summary>
    /// <see cref="MongoScenarioIsolation"/> rejects an empty connection string.
    /// </summary>
    [Fact]
    public void MongoScenarioIsolation_EmptyConnectionString_Throws()
    {
        Assert.Throws<ArgumentException>(() => new MongoScenarioIsolation("dep", string.Empty));
    }

    /// <summary>
    /// <see cref="MongoScenarioIsolation"/> is constructed successfully and implements
    /// <see cref="IScenarioIsolation"/> and <see cref="IAsyncDisposable"/>. Construction must not
    /// open a connection (lazy initialisation) — the connection string is unreachable, so any
    /// eager connect attempt would hang or throw here instead of at <c>EndScenarioAsync</c>.
    /// </summary>
    [Fact]
    public async Task MongoScenarioIsolation_ConstructsSuccessfully_ImplementsInterface()
    {
        var sut = new MongoScenarioIsolation("dep", UnreachableMongoConnectionString);

        Assert.IsAssignableFrom<IScenarioIsolation>(sut);
        Assert.IsAssignableFrom<IAsyncDisposable>(sut);

        await sut.DisposeAsync();
    }

    /// <summary>Double-dispose of <see cref="MongoScenarioIsolation"/> is idempotent.</summary>
    [Fact]
    public async Task MongoScenarioIsolation_DoubleDispose_IsIdempotent()
    {
        var sut = new MongoScenarioIsolation("dep", UnreachableMongoConnectionString);

        await sut.DisposeAsync();
        await sut.DisposeAsync(); // Must not throw.
    }

    /// <summary>
    /// <see cref="MongoScenarioIsolation.BeginScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    [Fact]
    public async Task MongoScenarioIsolation_BeginAfterDispose_ThrowsObjectDisposedException()
    {
        var sut = new MongoScenarioIsolation("dep", UnreachableMongoConnectionString);
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.BeginScenarioAsync(CancellationToken.None));
    }

    /// <summary>
    /// <see cref="MongoScenarioIsolation.EndScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    [Fact]
    public async Task MongoScenarioIsolation_EndAfterDispose_ThrowsObjectDisposedException()
    {
        var sut = new MongoScenarioIsolation("dep", UnreachableMongoConnectionString);
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.EndScenarioAsync(CancellationToken.None));
    }

    /// <summary>
    /// <see cref="MongoScenarioIsolation.BeginScenarioAsync"/> is a validated no-op — it must not
    /// attempt to connect (the connection string is unreachable).
    /// </summary>
    [Fact]
    public async Task MongoScenarioIsolation_BeginScenarioAsync_DoesNotConnect()
    {
        var sut = new MongoScenarioIsolation("dep", UnreachableMongoConnectionString);

        await sut.BeginScenarioAsync(CancellationToken.None);

        await sut.DisposeAsync();
    }

    /// <summary>
    /// <see cref="MongoScenarioIsolation.EndScenarioAsync"/> throws a wrapped
    /// <see cref="OrchestrationException"/> (§12.1: <see cref="OrchestrationErrorKind.Provision"/>,
    /// naming the dependency) when the connection string has no database name. This is a
    /// deterministic, no-I/O failure: <c>EnsureDatabase</c>'s <c>MongoUrl</c> parse stage rejects
    /// the missing database name before a <see cref="MongoDB.Driver.MongoClient"/> is ever
    /// constructed, so this runs without Docker.
    /// </summary>
    [Fact]
    public async Task MongoScenarioIsolation_NoDatabaseNameInConnectionString_ThrowsWrappedProvisionError()
    {
        await using var sut = new MongoScenarioIsolation("dep", "mongodb://localhost:27017");

        var ex = await Assert.ThrowsAsync<OrchestrationException>(
            () => sut.EndScenarioAsync(CancellationToken.None));

        Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
        Assert.Equal("dep", ex.Info.ResourceName);
    }

    // ── RedisScenarioIsolation — constructor contract ─────────────────────────

    /// <summary>Syntactically valid but unreachable connection string for lazy-no-connect tests.</summary>
    private const string UnreachableRedisConnectionString = "unreachable-host:6379";

    /// <summary>
    /// <see cref="RedisScenarioIsolation"/> rejects a <see langword="null"/> dependency name.
    /// </summary>
    [Fact]
    public void RedisScenarioIsolation_NullDependencyName_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new RedisScenarioIsolation(null!, UnreachableRedisConnectionString));
    }

    /// <summary>
    /// <see cref="RedisScenarioIsolation"/> rejects an empty dependency name.
    /// </summary>
    [Fact]
    public void RedisScenarioIsolation_EmptyDependencyName_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new RedisScenarioIsolation(string.Empty, UnreachableRedisConnectionString));
    }

    /// <summary>
    /// <see cref="RedisScenarioIsolation"/> rejects a <see langword="null"/> connection string.
    /// </summary>
    [Fact]
    public void RedisScenarioIsolation_NullConnectionString_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RedisScenarioIsolation("dep", null!));
    }

    /// <summary>
    /// <see cref="RedisScenarioIsolation"/> rejects an empty connection string.
    /// </summary>
    [Fact]
    public void RedisScenarioIsolation_EmptyConnectionString_Throws()
    {
        Assert.Throws<ArgumentException>(() => new RedisScenarioIsolation("dep", string.Empty));
    }

    /// <summary>
    /// <see cref="RedisScenarioIsolation"/> is constructed successfully and implements
    /// <see cref="IScenarioIsolation"/> and <see cref="IAsyncDisposable"/>. Construction must not
    /// open a connection (lazy initialisation) — the connection string is unreachable, so any
    /// eager connect attempt would hang or throw here instead of at <c>EndScenarioAsync</c>.
    /// </summary>
    [Fact]
    public async Task RedisScenarioIsolation_ConstructsSuccessfully_ImplementsInterface()
    {
        var sut = new RedisScenarioIsolation("dep", UnreachableRedisConnectionString);

        Assert.IsAssignableFrom<IScenarioIsolation>(sut);
        Assert.IsAssignableFrom<IAsyncDisposable>(sut);

        await sut.DisposeAsync();
    }

    /// <summary>Double-dispose of <see cref="RedisScenarioIsolation"/> is idempotent.</summary>
    [Fact]
    public async Task RedisScenarioIsolation_DoubleDispose_IsIdempotent()
    {
        var sut = new RedisScenarioIsolation("dep", UnreachableRedisConnectionString);

        await sut.DisposeAsync();
        await sut.DisposeAsync(); // Must not throw.
    }

    /// <summary>
    /// <see cref="RedisScenarioIsolation.BeginScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    [Fact]
    public async Task RedisScenarioIsolation_BeginAfterDispose_ThrowsObjectDisposedException()
    {
        var sut = new RedisScenarioIsolation("dep", UnreachableRedisConnectionString);
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.BeginScenarioAsync(CancellationToken.None));
    }

    /// <summary>
    /// <see cref="RedisScenarioIsolation.EndScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    [Fact]
    public async Task RedisScenarioIsolation_EndAfterDispose_ThrowsObjectDisposedException()
    {
        var sut = new RedisScenarioIsolation("dep", UnreachableRedisConnectionString);
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.EndScenarioAsync(CancellationToken.None));
    }

    /// <summary>
    /// <see cref="RedisScenarioIsolation.BeginScenarioAsync"/> is a validated no-op — it must not
    /// attempt to connect (the connection string is unreachable).
    /// </summary>
    [Fact]
    public async Task RedisScenarioIsolation_BeginScenarioAsync_DoesNotConnect()
    {
        var sut = new RedisScenarioIsolation("dep", UnreachableRedisConnectionString);

        await sut.BeginScenarioAsync(CancellationToken.None);

        await sut.DisposeAsync();
    }

    // ── ElasticsearchScenarioIsolation — constructor contract ─────────────────

    /// <summary>Syntactically valid but unreachable endpoint URL for lazy-no-connect tests.</summary>
    private const string UnreachableEsEndpointUrl = "http://unreachable-host:9200";

    /// <summary>
    /// <see cref="ElasticsearchScenarioIsolation"/> rejects a <see langword="null"/> dependency
    /// name.
    /// </summary>
    [Fact]
    public void ElasticsearchScenarioIsolation_NullDependencyName_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ElasticsearchScenarioIsolation(null!, UnreachableEsEndpointUrl));
    }

    /// <summary>
    /// <see cref="ElasticsearchScenarioIsolation"/> rejects an empty dependency name.
    /// </summary>
    [Fact]
    public void ElasticsearchScenarioIsolation_EmptyDependencyName_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new ElasticsearchScenarioIsolation(string.Empty, UnreachableEsEndpointUrl));
    }

    /// <summary>
    /// <see cref="ElasticsearchScenarioIsolation"/> rejects a <see langword="null"/> endpoint URL.
    /// </summary>
    [Fact]
    public void ElasticsearchScenarioIsolation_NullEndpointUrl_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ElasticsearchScenarioIsolation("dep", null!));
    }

    /// <summary>
    /// <see cref="ElasticsearchScenarioIsolation"/> rejects an empty endpoint URL.
    /// </summary>
    [Fact]
    public void ElasticsearchScenarioIsolation_EmptyEndpointUrl_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new ElasticsearchScenarioIsolation("dep", string.Empty));
    }

    /// <summary>
    /// <see cref="ElasticsearchScenarioIsolation"/> is constructed successfully and implements
    /// <see cref="IScenarioIsolation"/> and <see cref="IAsyncDisposable"/>. Construction must not
    /// open a connection (lazy initialisation) — the endpoint is unreachable, so any eager
    /// connect attempt would hang or throw here instead of at <c>EndScenarioAsync</c>.
    /// </summary>
    [Fact]
    public async Task ElasticsearchScenarioIsolation_ConstructsSuccessfully_ImplementsInterface()
    {
        var sut = new ElasticsearchScenarioIsolation("dep", UnreachableEsEndpointUrl);

        Assert.IsAssignableFrom<IScenarioIsolation>(sut);
        Assert.IsAssignableFrom<IAsyncDisposable>(sut);

        await sut.DisposeAsync();
    }

    /// <summary>Double-dispose of <see cref="ElasticsearchScenarioIsolation"/> is idempotent.</summary>
    [Fact]
    public async Task ElasticsearchScenarioIsolation_DoubleDispose_IsIdempotent()
    {
        var sut = new ElasticsearchScenarioIsolation("dep", UnreachableEsEndpointUrl);

        await sut.DisposeAsync();
        await sut.DisposeAsync(); // Must not throw.
    }

    /// <summary>
    /// <see cref="ElasticsearchScenarioIsolation.BeginScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    [Fact]
    public async Task ElasticsearchScenarioIsolation_BeginAfterDispose_ThrowsObjectDisposedException()
    {
        var sut = new ElasticsearchScenarioIsolation("dep", UnreachableEsEndpointUrl);
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.BeginScenarioAsync(CancellationToken.None));
    }

    /// <summary>
    /// <see cref="ElasticsearchScenarioIsolation.EndScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    [Fact]
    public async Task ElasticsearchScenarioIsolation_EndAfterDispose_ThrowsObjectDisposedException()
    {
        var sut = new ElasticsearchScenarioIsolation("dep", UnreachableEsEndpointUrl);
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.EndScenarioAsync(CancellationToken.None));
    }

    /// <summary>
    /// <see cref="ElasticsearchScenarioIsolation.BeginScenarioAsync"/> is a validated no-op — it
    /// must not attempt to connect (the endpoint is unreachable).
    /// </summary>
    [Fact]
    public async Task ElasticsearchScenarioIsolation_BeginScenarioAsync_DoesNotConnect()
    {
        var sut = new ElasticsearchScenarioIsolation("dep", UnreachableEsEndpointUrl);

        await sut.BeginScenarioAsync(CancellationToken.None);

        await sut.DisposeAsync();
    }

    /// <summary>
    /// <see cref="ElasticsearchScenarioIsolation.EndScenarioAsync"/> throws a wrapped
    /// <see cref="OrchestrationException"/> (§12.1: <see cref="OrchestrationErrorKind.Provision"/>,
    /// naming the dependency) when the endpoint URL cannot be parsed. This is a deterministic,
    /// no-I/O failure: <c>EnsureClient</c>'s <see cref="Uri"/> parse stage rejects the malformed
    /// URL before an <see cref="HttpClient"/> ever issues a request, so this runs without Docker.
    /// </summary>
    [Fact]
    public async Task ElasticsearchScenarioIsolation_UnparseableEndpointUrl_ThrowsWrappedProvisionError()
    {
        await using var sut = new ElasticsearchScenarioIsolation("dep", "not a url");

        var ex = await Assert.ThrowsAsync<OrchestrationException>(
            () => sut.EndScenarioAsync(CancellationToken.None));

        Assert.Equal(OrchestrationErrorKind.Provision, ex.Info.Kind);
        Assert.Equal("dep", ex.Info.ResourceName);
    }
}
