// Platform.Engine.Orchestration — ScenarioIsolation (S04-A-01).
//
// Per-scenario state-reset seam.  The suite runner calls BeginScenarioAsync before
// each scenario and EndScenarioAsync after each scenario.  The concrete implementation
// selects the appropriate reset strategy for the declared topology.
//
// Design notes:
//   • IScenarioIsolation is placed in the Orchestration project — the only project
//     that already depends on Aspire and manages the topology lifecycle.  Putting it
//     in Abstractions would pull Respawn into a low-level shared contract, which is
//     undesirable.
//   • NullScenarioIsolation is the default: it preserves the existing single-scenario
//     RunAsync behaviour unchanged (the invariant §4 / MVP §8.2).
//   • RespawnPostgresIsolation uses Respawn 6.x (Respawner.CreateAsync /
//     respawner.ResetAsync) to truncate and reseed all user-created tables between
//     scenarios without dropping the schema.
//   • Any Respawn or Npgsql failure is wrapped in OrchestrationException (§12.1):
//     isolation failure is always an Environment error, never a test Fail.

using Respawn;

namespace Platform.Engine.Orchestration;

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

// ---------------------------------------------------------------------------
// RespawnPostgresIsolation
// ---------------------------------------------------------------------------

/// <summary>
/// Per-scenario isolation implementation that uses Respawn 6.x to reset all
/// user-created Postgres tables between scenarios without dropping the schema
/// (S04-A-01).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Respawn API used (6.x):</strong>
/// <code>
/// var respawner = await Respawner.CreateAsync(connection,
///     new RespawnerOptions { DbAdapter = DbAdapter.Postgres });
/// await respawner.ResetAsync(connection);
/// </code>
/// </para>
/// <para>
/// <strong>Lazy initialisation:</strong> the <see cref="Npgsql.NpgsqlConnection"/>
/// and <see cref="Respawner"/> are created on the first call to
/// <see cref="EndScenarioAsync"/> — not <see cref="BeginScenarioAsync"/> — and
/// reused for the lifetime of the topology.  This deferral is deliberate: a
/// freshly-started Aspire topology yields a clean Postgres container, so no
/// pre-scenario reset is needed, and (critically) <see cref="Respawner.CreateAsync"/>
/// under Respawn 6.x throws on a database with no user tables.  By deferring
/// creation to the first <see cref="EndScenarioAsync"/>, the first scenario has
/// had a chance to establish its schema (via <c>seed</c> or an early
/// <c>script.csharp</c> step), so Respawn always finds tables to checkpoint.
/// </para>
/// <para>
/// <strong>§12.1 invariant:</strong> any Npgsql or Respawn failure is wrapped
/// in <see cref="OrchestrationException"/> with kind
/// <see cref="OrchestrationErrorKind.Provision"/> so the runner classifies it as
/// <c>EnvironmentError</c>, never as <c>Fail</c>.
/// </para>
/// <para>
/// <strong>Disposal:</strong> call <see cref="DisposeAsync"/> once the topology
/// is torn down to release the underlying Npgsql connection.
/// </para>
/// </remarks>
public sealed class RespawnPostgresIsolation : IScenarioIsolation, IAsyncDisposable
{
    private readonly string _connectionString;

    // Guard: connection and respawner are created once, lazily, on first reset.
    private Npgsql.NpgsqlConnection? _connection;
    private Respawner? _respawner;
    private bool _disposed;

    /// <summary>
    /// Initialises a new <see cref="RespawnPostgresIsolation"/> for the
    /// given Postgres connection string (obtained from
    /// <see cref="SuiteTopology.DiscoveredServices"/> after
    /// <see cref="SuiteTopology.StartAsync"/> completes).
    /// </summary>
    /// <param name="connectionString">
    /// The Postgres ADO.NET connection string for the managed dependency.
    /// Must not be <see langword="null"/> or empty.
    /// </param>
    public RespawnPostgresIsolation(string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        _connectionString = connectionString;
    }

    // ── IScenarioIsolation ────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// This is a no-op.  A freshly-started Aspire topology yields a clean
    /// Postgres container, so no pre-scenario reset is required — and under
    /// Respawn 6.x, <see cref="Respawner.CreateAsync"/> on an empty database
    /// throws.  The <see cref="Respawner"/> is therefore created lazily, and
    /// state reset performed, in <see cref="EndScenarioAsync"/>; the next
    /// scenario always begins from a known-clean baseline.
    /// </remarks>
    public Task BeginScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// On its first invocation this opens the connection and creates the
    /// <see cref="Respawner"/> (by which point the first scenario has
    /// established its schema, so Respawn finds tables to checkpoint).  It then
    /// resets all user-created Postgres tables via
    /// <c>respawner.ResetAsync(connection)</c>.  Any failure is wrapped in
    /// <see cref="OrchestrationException"/> (§12.1: Environment error, never Fail).
    /// </remarks>
    public async Task EndScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureInitialisedAsync(ct).ConfigureAwait(false);
        await ResetAsync(ct).ConfigureAwait(false);
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the underlying <see cref="Npgsql.NpgsqlConnection"/>.
    /// Safe to call multiple times.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Opens a new <see cref="Npgsql.NpgsqlConnection"/> and creates the
    /// <see cref="Respawner"/> instance if they have not already been created.
    /// </summary>
    private async Task EnsureInitialisedAsync(CancellationToken ct)
    {
        if (_respawner is not null)
        {
            return;
        }

        var conn = new Npgsql.NpgsqlConnection(_connectionString);
        try
        {
            try
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var info = new OrchestrationErrorInfo(
                    Kind: OrchestrationErrorKind.Provision,
                    ResourceName: "respawn-init",
                    RegistryHost: null,
                    AuthStatus: null,
                    Detail: TrimDetail(ex.Message));
                throw new OrchestrationException(info, ex);
            }

            Respawner respawner;
            try
            {
                respawner = await Respawner.CreateAsync(
                    conn,
                    new RespawnerOptions { DbAdapter = DbAdapter.Postgres })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var info = new OrchestrationErrorInfo(
                    Kind: OrchestrationErrorKind.Provision,
                    ResourceName: "respawn-create",
                    RegistryHost: null,
                    AuthStatus: null,
                    Detail: TrimDetail(ex.Message));
                throw new OrchestrationException(info, ex);
            }

            _connection = conn;
            _respawner = respawner;
        }
        catch
        {
            // Dispose the connection on any failure so we don't leak it.
            conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Calls <c>respawner.ResetAsync(connection)</c>, wrapping any failure
    /// in an <see cref="OrchestrationException"/> (§12.1).
    /// </summary>
    private async Task ResetAsync(CancellationToken ct)
    {
        // Respawner.ResetAsync does not accept a CancellationToken in v6;
        // the operation is fast (truncation, not drop+recreate) so the
        // omission is acceptable.
        _ = ct; // suppress unused-parameter warning

        try
        {
            await _respawner!.ResetAsync(_connection!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var info = new OrchestrationErrorInfo(
                Kind: OrchestrationErrorKind.Provision,
                ResourceName: "respawn-reset",
                RegistryHost: null,
                AuthStatus: null,
                Detail: TrimDetail(ex.Message));
            throw new OrchestrationException(info, ex);
        }
    }

    /// <summary>
    /// Returns a trimmed, single-line summary of <paramref name="message"/>
    /// capped at 200 characters for display in event streams and logs.
    /// </summary>
    private static string TrimDetail(string message) =>
        (message ?? string.Empty).ReplaceLineEndings(" ").Trim() is { Length: > 200 } s
            ? s[..200]
            : (message ?? string.Empty).ReplaceLineEndings(" ").Trim();
}
