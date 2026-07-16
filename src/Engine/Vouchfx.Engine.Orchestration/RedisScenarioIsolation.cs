// Vouchfx.Engine.Orchestration — RedisScenarioIsolation (S04-A-01 follow-up chunk).
//
// Per-scenario isolation implementation for Redis: issues FLUSHDB (never FLUSHALL) against the
// single logical database the connection string designates, between scenarios.
//
// Design notes:
//   • FLUSHDB, not FLUSHALL: Redis Aspire dependencies are single-purpose per suite topology, so
//     "the database this connection string designates" IS the whole reset scope the engine ever
//     needs — FLUSHALL would additionally wipe every OTHER logical database on a shared Redis
//     server (index 0-15 by default), which this engine has no business touching.
//   • FLUSHDB is an admin command — StackExchange.Redis refuses to issue it unless
//     ConfigurationOptions.AllowAdmin is explicitly set (documented in
//     docs/troubleshooting.md's manual-cleanup snippet, which this class supersedes for the
//     Core store set).
//   • Redis has no notion of "schema" to preserve — a flushed database has no structure beyond
//     its keys, so "delete the data, preserve the structure" reduces to "delete every key."
//   • Lazy initialisation mirrors RespawnRelationalIsolation: the ConnectionMultiplexer is
//     created on the FIRST EndScenarioAsync, not at construction or Begin. Construction never
//     opens a connection or parses the connection string — a malformed string is accepted at
//     construction and only fails, as a wrapped Provision error, at the first EndScenarioAsync.
//   • Every driver failure is wrapped via ScenarioIsolationErrors.Wrap so the ResourceName on the
//     resulting OrchestrationException is always the LOGICAL DEPENDENCY NAME (§12.1).

using StackExchange.Redis;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Per-scenario isolation implementation that issues <c>FLUSHDB</c> against a Redis dependency's
/// designated database between scenarios (S04-A-01 follow-up chunk).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lazy initialisation:</strong> the underlying <see cref="ConnectionMultiplexer"/> is
/// created on the first call to <see cref="EndScenarioAsync"/> — not
/// <see cref="BeginScenarioAsync"/> — and reused for the lifetime of the topology. Construction
/// never performs I/O or connection-string parsing: a malformed connection string is accepted at
/// construction and only fails, as a wrapped Provision error, at the first
/// <see cref="EndScenarioAsync"/>.
/// </para>
/// <para>
/// <strong>§12.1 invariant:</strong> any driver failure is wrapped in
/// <see cref="OrchestrationException"/> with kind <see cref="OrchestrationErrorKind.Provision"/>
/// and <see cref="OrchestrationErrorInfo.ResourceName"/> set to the logical dependency name
/// supplied at construction, so the runner classifies it as <c>EnvironmentError</c> — never as
/// <c>Fail</c> — and names the offending dependency.
/// </para>
/// <para>
/// <strong>Disposal:</strong> call <see cref="DisposeAsync"/> once the topology is torn down to
/// release the underlying multiplexer.
/// </para>
/// </remarks>
public sealed class RedisScenarioIsolation : IScenarioIsolation, IAsyncDisposable
{
    private const string StoreToken = "redis";

    private readonly string _dependencyName;
    private readonly string _connectionString;

    // Guard: the multiplexer is created once, lazily, on first reset. The database index is
    // resolved alongside it (from the SAME parsed ConfigurationOptions) so the two never drift.
    private ConnectionMultiplexer? _muxer;
    private int _databaseIndex;
    private bool _disposed;

    /// <summary>
    /// The logical dependency name, exposed for tests (InternalsVisibleTo) so factory dispatch
    /// and composite ordering can be asserted per dependency.
    /// </summary>
    internal string DependencyName => _dependencyName;

    /// <summary>
    /// Initialises a new <see cref="RedisScenarioIsolation"/> for the given Redis dependency.
    /// Never opens a connection — construction is pure validation.
    /// </summary>
    /// <param name="dependencyName">
    /// The logical dependency name declared in <c>environment.dependencies</c> (used to name the
    /// dependency in any reset-failure observation). Must not be <see langword="null"/> or empty.
    /// </param>
    /// <param name="connectionString">
    /// The Redis connection string for the managed dependency (obtained from
    /// <see cref="SuiteTopology.DiscoveredServices"/> after <see cref="SuiteTopology.StartAsync"/>
    /// completes). Must not be <see langword="null"/> or empty.
    /// </param>
    public RedisScenarioIsolation(string dependencyName, string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(dependencyName);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        _dependencyName = dependencyName;
        _connectionString = connectionString;
    }

    // ── IScenarioIsolation ────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// This is a no-op.  A freshly-started Aspire dependency yields a clean database, so no
    /// pre-scenario reset is required; the multiplexer is created lazily, and state reset
    /// performed, in <see cref="EndScenarioAsync"/>.
    /// </remarks>
    public Task BeginScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Connects on first use, then issues <c>FLUSHDB</c> against the designated database index.
    /// Any failure is wrapped in <see cref="OrchestrationException"/> (§12.1: Environment error,
    /// never Fail).
    /// </remarks>
    public async Task EndScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureConnectionAsync().ConfigureAwait(false);
        await ResetAsync().ConfigureAwait(false);
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the underlying <see cref="ConnectionMultiplexer"/>. Safe to call multiple times.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _muxer?.Dispose();
        _muxer = null;

        return ValueTask.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses the connection string, sets <c>AllowAdmin</c> (FLUSHDB is an admin command), opens
    /// the shared <see cref="ConnectionMultiplexer"/> once, and caches it — plus the resolved
    /// database index — for the lifetime of the topology. A no-op once connected.
    /// </summary>
    private async Task EnsureConnectionAsync()
    {
        if (_muxer is not null)
        {
            return;
        }

        ConfigurationOptions options;
        try
        {
            options = ConfigurationOptions.Parse(_connectionString);
        }
        catch (Exception)
        {
            // Deliberately NOT the parse exception itself: a malformed-connection-string
            // message can echo fragments of the string (§17 discipline — never let
            // credential-bearing material reach an observation).
            throw ScenarioIsolationErrors.Wrap(
                StoreToken,
                "init",
                _dependencyName,
                new InvalidOperationException("connection string could not be parsed."));
        }

        // FLUSHDB is an admin command; StackExchange.Redis refuses to issue it otherwise
        // (docs/troubleshooting.md documents this exact requirement for manual cleanup).
        options.AllowAdmin = true;
        _databaseIndex = options.DefaultDatabase ?? 0;

        try
        {
            // ConnectionMultiplexer.ConnectAsync does not accept a CancellationToken in
            // StackExchange.Redis 2.x — there is nothing to propagate here.
            _muxer = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ScenarioIsolationErrors.Wrap(StoreToken, "init", _dependencyName, ex);
        }
    }

    /// <summary>
    /// Issues <c>FLUSHDB</c> against the resolved database index via the first configured
    /// endpoint's <see cref="IServer"/>.
    /// </summary>
    private async Task ResetAsync()
    {
        try
        {
            // FlushDatabaseAsync does not accept a CancellationToken in StackExchange.Redis
            // 2.x — there is nothing to propagate here.
            var endpoint = _muxer!.GetEndPoints()[0];
            var server = _muxer.GetServer(endpoint);
            await server.FlushDatabaseAsync(_databaseIndex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw ScenarioIsolationErrors.Wrap(StoreToken, "reset", _dependencyName, ex);
        }
    }
}
