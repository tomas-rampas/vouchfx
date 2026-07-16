// Tests for S04-A-01 (generalised): IScenarioIsolation, NullScenarioIsolation,
// RespawnRelationalIsolation.
//
// Non-docker tests cover the no-op NullScenarioIsolation and basic parameter validation
// for all three RelationalStoreKind values (Postgres, SqlServer, MySql) — construction
// must never open a connection, so these run without Docker.
// Docker-gated tests for RespawnRelationalIsolation's actual reset behaviour are in
// RespawnResetProofTests.cs (Postgres), SqlServerResetProofTests.cs, and
// MySqlResetProofTests.cs.
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
}
