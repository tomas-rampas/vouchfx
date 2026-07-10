// Tests for S04-A-01: IScenarioIsolation, NullScenarioIsolation, RespawnPostgresIsolation.
//
// Non-docker tests cover the no-op NullScenarioIsolation and basic parameter validation.
// Docker-gated tests for RespawnPostgresIsolation are in RespawnResetProofTests.cs.
//
// Run non-docker tests:
//   dotnet test --filter "requires!=docker&FullyQualifiedName~ScenarioIsolation"

using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Unit tests for <see cref="IScenarioIsolation"/>, <see cref="NullScenarioIsolation"/>,
/// and the <see cref="RespawnPostgresIsolation"/> constructor contract (S04-A-01).
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

    // ── RespawnPostgresIsolation — constructor contract ───────────────────────

    /// <summary>
    /// <see cref="RespawnPostgresIsolation"/> rejects a <see langword="null"/>
    /// connection string at construction time.
    /// </summary>
    [Fact]
    public void RespawnPostgresIsolation_NullConnectionString_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new RespawnPostgresIsolation(null!));
    }

    /// <summary>
    /// <see cref="RespawnPostgresIsolation"/> rejects an empty connection string
    /// at construction time.
    /// </summary>
    [Fact]
    public void RespawnPostgresIsolation_EmptyConnectionString_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new RespawnPostgresIsolation(string.Empty));
    }

    /// <summary>
    /// <see cref="RespawnPostgresIsolation"/> is constructed successfully for a
    /// non-empty connection string and implements <see cref="IScenarioIsolation"/>
    /// and <see cref="IAsyncDisposable"/>.
    /// </summary>
    [Fact]
    public async Task RespawnPostgresIsolation_ConstructsSuccessfully_ImplementsInterface()
    {
        // Use a syntactically valid but unreachable connection string.
        // The constructor must not open a connection (lazy initialisation).
        var sut = new RespawnPostgresIsolation("Host=unreachable;Database=test;Username=u;Password=p");

        Assert.IsAssignableFrom<IScenarioIsolation>(sut);
        Assert.IsAssignableFrom<IAsyncDisposable>(sut);

        // Dispose must be safe to call even before initialisation.
        await sut.DisposeAsync();
    }

    /// <summary>
    /// Double-dispose of <see cref="RespawnPostgresIsolation"/> is idempotent
    /// and does not throw.
    /// </summary>
    [Fact]
    public async Task RespawnPostgresIsolation_DoubleDispose_IsIdempotent()
    {
        var sut = new RespawnPostgresIsolation("Host=unreachable;Database=test;Username=u;Password=p");

        await sut.DisposeAsync();
        await sut.DisposeAsync(); // Must not throw.
    }

    /// <summary>
    /// <see cref="RespawnPostgresIsolation.BeginScenarioAsync"/> throws
    /// <see cref="ObjectDisposedException"/> after disposal.
    /// </summary>
    [Fact]
    public async Task RespawnPostgresIsolation_BeginAfterDispose_ThrowsObjectDisposedException()
    {
        var sut = new RespawnPostgresIsolation("Host=unreachable;Database=test;Username=u;Password=p");
        await sut.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => sut.BeginScenarioAsync(CancellationToken.None));
    }
}
