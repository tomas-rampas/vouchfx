// Vouchfx.Engine.Orchestration — RespawnRelationalIsolation (S04-A-01, generalised).
//
// Generalises the former RespawnPostgresIsolation to cover every relational store the
// engine ships a db-assert provider for: PostgreSQL, SQL Server, and MySQL.  All three
// share Respawn 6.x's DbConnection-based API surface (Respawner.CreateAsync /
// respawner.ResetAsync), so one class parameterised on RelationalStoreKind replaces
// three near-identical implementations.
//
// Design notes:
//   • Lazy initialisation is preserved byte-identical to the original Postgres class:
//     BeginScenarioAsync is a validated no-op; the connection and Respawner are created
//     on the FIRST EndScenarioAsync (a freshly-started Aspire dependency is already
//     clean, and Respawner.CreateAsync throws on a database with no user tables yet).
//   • The Respawner checkpoint is RE-CREATED on EVERY EndScenarioAsync so tables created
//     mid-suite (via seed or an early script.csharp step) are always included in the
//     reset set.
//   • Per-kind RespawnerOptions:
//       - Postgres:  DbAdapter.Postgres only (byte-identical to the original class).
//       - SqlServer: DbAdapter.SqlServer + CheckTemporalTables = true — an extra
//         sys.periods metadata query that is harmless when no temporal table exists,
//         but required so a DELETE against a temporal table does not fail Respawn.
//       - MySql:     DbAdapter.MySql + SchemasToInclude = [ <database from the
//         connection string> ].  MySQL has no schema-qualified default the way
//         Postgres/SQL Server do, so the adapter's own table discovery is scoped
//         defensively to the target database — belt-and-braces so a shared MySQL
//         server can never have the reset wander into another database's tables.
//   • Every driver/Respawn failure is wrapped via ScenarioIsolationErrors.Wrap so the
//     ResourceName on the resulting OrchestrationException is the LOGICAL DEPENDENCY
//     NAME (not a fixed "respawn-reset" placeholder) — the environment-error
//     observation always names which dependency's reset failed (§12.1).

using System.Data.Common;
using Respawn;

namespace Vouchfx.Engine.Orchestration;

// ---------------------------------------------------------------------------
// RelationalStoreKind
// ---------------------------------------------------------------------------

/// <summary>
/// The relational store technology a <see cref="RespawnRelationalIsolation"/> resets
/// between scenarios.
/// </summary>
public enum RelationalStoreKind
{
    /// <summary>PostgreSQL — reset via <see cref="Respawn.DbAdapter.Postgres"/>.</summary>
    Postgres,

    /// <summary>SQL Server — reset via <see cref="Respawn.DbAdapter.SqlServer"/>.</summary>
    SqlServer,

    /// <summary>MySQL — reset via <see cref="Respawn.DbAdapter.MySql"/>.</summary>
    MySql,
}

// ---------------------------------------------------------------------------
// RespawnRelationalIsolation
// ---------------------------------------------------------------------------

/// <summary>
/// Per-scenario isolation implementation that uses Respawn 6.x to reset all
/// user-created tables of a relational dependency — PostgreSQL, SQL Server, or
/// MySQL — between scenarios without dropping the schema (S04-A-01, generalised).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lazy initialisation:</strong> the underlying <see cref="DbConnection"/> and
/// <see cref="Respawner"/> are created on the first call to
/// <see cref="EndScenarioAsync"/> — not <see cref="BeginScenarioAsync"/> — and reused
/// for the lifetime of the topology.  This deferral is deliberate: a freshly-started
/// Aspire dependency yields a clean database, so no pre-scenario reset is needed, and
/// (critically) <see cref="Respawner.CreateAsync(DbConnection, RespawnerOptions)"/>
/// under Respawn 6.x throws on a database with no user tables.  By deferring creation
/// to the first <see cref="EndScenarioAsync"/>, the first scenario has had a chance to
/// establish its schema (via <c>seed</c> or an early <c>script.csharp</c> step), so
/// Respawn always finds tables to checkpoint.
/// </para>
/// <para>
/// <strong>§12.1 invariant:</strong> any driver or Respawn failure is wrapped in
/// <see cref="OrchestrationException"/> with kind
/// <see cref="OrchestrationErrorKind.Provision"/> and
/// <see cref="OrchestrationErrorInfo.ResourceName"/> set to the logical dependency name
/// supplied at construction, so the runner classifies it as <c>EnvironmentError</c> —
/// never as <c>Fail</c> — and names the offending dependency.
/// </para>
/// <para>
/// <strong>Disposal:</strong> call <see cref="DisposeAsync"/> once the topology is torn
/// down to release the underlying connection.
/// </para>
/// </remarks>
public sealed class RespawnRelationalIsolation : IScenarioIsolation, IAsyncDisposable
{
    private readonly string _dependencyName;
    private readonly RelationalStoreKind _kind;
    private readonly string _connectionString;

    // Guard: connection and respawner are created once, lazily, on first reset.
    private DbConnection? _connection;
    private Respawner? _respawner;
    private bool _disposed;

    /// <summary>
    /// Initialises a new <see cref="RespawnRelationalIsolation"/> for the given
    /// relational dependency.  Never opens a connection — construction is pure
    /// validation.
    /// </summary>
    /// <param name="dependencyName">
    /// The logical dependency name declared in <c>environment.dependencies</c>
    /// (used to name the dependency in any reset-failure observation).  Must not be
    /// <see langword="null"/> or empty.
    /// </param>
    /// <param name="kind">The relational store technology to reset.</param>
    /// <param name="connectionString">
    /// The ADO.NET connection string for the managed dependency (obtained from
    /// <see cref="SuiteTopology.DiscoveredServices"/> after
    /// <see cref="SuiteTopology.StartAsync"/> completes). Must not be
    /// <see langword="null"/> or empty.
    /// </param>
    public RespawnRelationalIsolation(string dependencyName, RelationalStoreKind kind, string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(dependencyName);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        _dependencyName = dependencyName;
        _kind = kind;
        _connectionString = connectionString;
    }

    // ── IScenarioIsolation ────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// This is a no-op.  A freshly-started Aspire dependency yields a clean database,
    /// so no pre-scenario reset is required — and under Respawn 6.x,
    /// <see cref="Respawner.CreateAsync(DbConnection, RespawnerOptions)"/> on an empty
    /// database throws.  The <see cref="Respawner"/> is therefore created lazily, and
    /// state reset performed, in <see cref="EndScenarioAsync"/>; the next scenario
    /// always begins from a known-clean baseline.
    /// </remarks>
    public Task BeginScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Opens the connection on first use, then <em>re-creates</em> the
    /// <see cref="Respawner"/> checkpoint and resets all user-created tables via
    /// <c>respawner.ResetAsync(connection)</c>.  Any failure is wrapped in
    /// <see cref="OrchestrationException"/> (§12.1: Environment error, never Fail).
    /// </para>
    /// <para>
    /// The checkpoint is re-created on <em>every</em> call (not cached) because
    /// Respawn computes its table-reset set at checkpoint time: a scenario may create
    /// new tables (via <c>seed</c> or an early <c>script.csharp</c> step), and those
    /// tables would escape a checkpoint taken after an earlier scenario. Re-checkpointing
    /// keeps the reset set current, so no row created by any scenario bleeds into the
    /// next.
    /// </para>
    /// </remarks>
    public async Task EndScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureConnectionAsync(ct).ConfigureAwait(false);
        await CreateRespawnerAsync(ct).ConfigureAwait(false);
        await ResetAsync(ct).ConfigureAwait(false);
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the underlying <see cref="DbConnection"/>. Safe to call multiple times.
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
    /// Opens the shared <see cref="DbConnection"/> once (a kind-specific
    /// <c>NpgsqlConnection</c> / <c>SqlConnection</c> / <c>MySqlConnection</c>) and
    /// caches it for the lifetime of the topology. A no-op once the connection is open.
    /// </summary>
    private async Task EnsureConnectionAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            return;
        }

        var conn = CreateConnection();
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            conn.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            // Dispose the connection on any failure so we don't leak it.
            conn.Dispose();
            throw ScenarioIsolationErrors.Wrap(StoreKindToken(), "init", _dependencyName, ex);
        }

        _connection = conn;
    }

    /// <summary>
    /// Creates the kind-specific <see cref="DbConnection"/> instance. Does not open it.
    /// </summary>
    private DbConnection CreateConnection() => _kind switch
    {
        RelationalStoreKind.Postgres => new Npgsql.NpgsqlConnection(_connectionString),
        RelationalStoreKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(_connectionString),
        RelationalStoreKind.MySql => new MySqlConnector.MySqlConnection(_connectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(_kind), _kind, "Unsupported RelationalStoreKind."),
    };

    /// <summary>
    /// (Re-)creates the <see cref="Respawner"/> checkpoint over the current schema, so
    /// any table created since the previous reset is included in the reset set. The
    /// connection must already be open.
    /// </summary>
    private async Task CreateRespawnerAsync(CancellationToken ct)
    {
        _ = ct; // Respawner.CreateAsync does not accept a CancellationToken in v6.
        try
        {
            var options = BuildRespawnerOptions();
            _respawner = await Respawner.CreateAsync(_connection!, options).ConfigureAwait(false);
        }
        catch (OrchestrationException)
        {
            // Already classified (e.g. BuildRespawnerOptions' MySQL database-name
            // resolution failure) — propagate as-is rather than double-wrapping.
            throw;
        }
        catch (Exception ex)
        {
            throw ScenarioIsolationErrors.Wrap(StoreKindToken(), "create", _dependencyName, ex);
        }
    }

    /// <summary>
    /// Builds the kind-specific <see cref="RespawnerOptions"/>.
    /// </summary>
    private RespawnerOptions BuildRespawnerOptions() => _kind switch
    {
        RelationalStoreKind.Postgres => new RespawnerOptions { DbAdapter = DbAdapter.Postgres },

        // Temporal tables otherwise fail the delete; the extra sys.periods metadata
        // query is harmless when no temporal table is present.
        RelationalStoreKind.SqlServer => new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,
            CheckTemporalTables = true,
        },

        // Defensive scoping: the adapter's own table discovery is belt-and-braced to
        // the target database parsed from the connection string, so the reset can
        // never walk another database's tables on a shared MySQL server.
        RelationalStoreKind.MySql => new RespawnerOptions
        {
            DbAdapter = DbAdapter.MySql,
            SchemasToInclude = new[] { ResolveMySqlDatabaseName() },
        },

        _ => throw new ArgumentOutOfRangeException(nameof(_kind), _kind, "Unsupported RelationalStoreKind."),
    };

    /// <summary>
    /// Parses the target database name out of the MySQL connection string via
    /// <see cref="MySqlConnector.MySqlConnectionStringBuilder"/>. Throws a wrapped
    /// reset error (Provision kind, §12.1) when the connection string carries no
    /// <c>Database</c> — the reset cannot be safely scoped without one.
    /// </summary>
    private string ResolveMySqlDatabaseName()
    {
        string? database;
        try
        {
            database = new MySqlConnector.MySqlConnectionStringBuilder(_connectionString).Database;
        }
        catch (Exception ex)
        {
            throw ScenarioIsolationErrors.Wrap(StoreKindToken(), "create", _dependencyName, ex);
        }

        if (string.IsNullOrWhiteSpace(database))
        {
            throw ScenarioIsolationErrors.Wrap(
                StoreKindToken(),
                "create",
                _dependencyName,
                new InvalidOperationException(
                    "connection string has no Database — the MySQL reset cannot be scoped to a single schema."));
        }

        return database;
    }

    /// <summary>
    /// Calls <c>respawner.ResetAsync(connection)</c>, wrapping any failure in an
    /// <see cref="OrchestrationException"/> (§12.1).
    /// </summary>
    private async Task ResetAsync(CancellationToken ct)
    {
        // Respawner.ResetAsync does not accept a CancellationToken in v6; the
        // operation is fast (truncation, not drop+recreate) so the omission is
        // acceptable.
        _ = ct;

        try
        {
            await _respawner!.ResetAsync(_connection!).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ScenarioIsolationErrors.Wrap(StoreKindToken(), "reset", _dependencyName, ex);
        }
    }

    /// <summary>
    /// Returns the lower-case store token used in reset-failure detail messages
    /// (<c>"postgres"</c>, <c>"sqlserver"</c>, <c>"mysql"</c>).
    /// </summary>
    private string StoreKindToken() => _kind switch
    {
        RelationalStoreKind.Postgres => "postgres",
        RelationalStoreKind.SqlServer => "sqlserver",
        RelationalStoreKind.MySql => "mysql",
        _ => _kind.ToString(),
    };
}
