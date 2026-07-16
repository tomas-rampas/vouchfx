// Vouchfx.Engine.Orchestration — ScenarioIsolation (S04-A-01, generalised).
//
// Per-scenario state-reset seam.  The suite runner calls BeginScenarioAsync before
// each scenario and EndScenarioAsync after each scenario.  ScenarioIsolationFactory
// dispatches on each declared dependency's name + type to build the concrete
// implementation(s) for the declared topology.
//
// Design notes:
//   • IScenarioIsolation is placed in the Orchestration project — the only project
//     that already depends on Aspire and manages the topology lifecycle.  Putting it
//     in Abstractions would pull Respawn into a low-level shared contract, which is
//     undesirable.
//   • NullScenarioIsolation is the default: it preserves the existing single-scenario
//     RunAsync behaviour unchanged (the invariant §4 / MVP §8.2) — used when a
//     topology has no resettable dependency.
//   • RespawnRelationalIsolation (RespawnRelationalIsolation.cs) covers every
//     relational store the engine ships a db-assert provider for — PostgreSQL, SQL
//     Server, MySQL — via Respawn 6.x (Respawner.CreateAsync / respawner.ResetAsync).
//   • MongoScenarioIsolation, RedisScenarioIsolation, ElasticsearchScenarioIsolation
//     (one file each) cover the three remaining stores with a per-scenario resettable
//     dependency: MongoDB (DeleteMany per non-system collection), Redis (FLUSHDB), and
//     Elasticsearch (_delete_by_query). Every one of them preserves structure and
//     deletes only data — "delete the data, preserve the structure" is the semantic
//     every resetter in this project shares.
//   • CompositeScenarioIsolation (CompositeScenarioIsolation.cs) fans Begin/End out to
//     every resettable dependency when a topology declares more than one, so ALL of
//     them are reset between scenarios — not just the first one found.
//   • ScenarioIsolationFactory (ScenarioIsolationFactory.cs) is the single dispatch
//     point: name+type lookup, never a connection-string-shape sniff.
//   • Any driver or Respawn failure is wrapped in OrchestrationException (§12.1) whose
//     ResourceName names the failing dependency: isolation failure is always an
//     Environment error, never a test Fail.

namespace Vouchfx.Engine.Orchestration;

// ---------------------------------------------------------------------------
// IScenarioIsolation
// ---------------------------------------------------------------------------

/// <summary>
/// Seam that the suite runner uses to bracket each scenario with state-reset
/// operations on mutable dependencies (§4 / S04-A-01).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BeginScenarioAsync"/> is called immediately <em>before</em> the
/// scenario begins execution; <see cref="EndScenarioAsync"/> is called
/// immediately <em>after</em> the scenario completes (regardless of the
/// scenario's verdict).  Implementations must be idempotent and safe to call
/// for every scenario in the suite.
/// </para>
/// <para>
/// <strong>§12.1 invariant:</strong> any isolation failure (connection drop,
/// Respawn error) must surface as <see cref="OrchestrationException"/> so the
/// runner maps it to <c>EnvironmentError</c>, never to <c>Fail</c>.
/// </para>
/// </remarks>
public interface IScenarioIsolation
{
    /// <summary>
    /// Called immediately before a scenario begins.  May perform an initial
    /// state reset on the first invocation to ensure a clean starting state.
    /// </summary>
    /// <param name="ct">
    /// Propagated to all async operations within the implementation.
    /// </param>
    Task BeginScenarioAsync(CancellationToken ct);

    /// <summary>
    /// Called immediately after a scenario completes.  Resets all mutable
    /// dependency state so the next scenario starts from a known-clean baseline.
    /// </summary>
    /// <param name="ct">
    /// Propagated to all async operations within the implementation.
    /// </param>
    Task EndScenarioAsync(CancellationToken ct);
}

// ---------------------------------------------------------------------------
// NullScenarioIsolation
// ---------------------------------------------------------------------------

/// <summary>
/// No-op implementation of <see cref="IScenarioIsolation"/> used when the
/// topology has no resettable dependency (or when the caller runs a single
/// scenario via the legacy <c>RunAsync</c> path).
/// </summary>
/// <remarks>
/// Both methods complete synchronously and perform no work, preserving the
/// existing single-scenario <c>RunAsync</c> behaviour unchanged.
/// </remarks>
public sealed class NullScenarioIsolation : IScenarioIsolation
{
    /// <inheritdoc />
    public Task BeginScenarioAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public Task EndScenarioAsync(CancellationToken ct) => Task.CompletedTask;
}
