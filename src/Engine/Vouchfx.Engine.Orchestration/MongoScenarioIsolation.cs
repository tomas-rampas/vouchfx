// Vouchfx.Engine.Orchestration — MongoScenarioIsolation (S04-A-01 follow-up chunk).
//
// Per-scenario isolation implementation for MongoDB: deletes every document from every
// non-system collection in the dependency's target database between scenarios, preserving
// both the collections AND their indexes ("delete the data, preserve the structure" — the
// same semantics RespawnRelationalIsolation applies to relational stores).
//
// Design notes:
//   • DeleteMany-not-drop is deliberate: a running SUT typically creates its indexes ONCE at
//     startup.  Dropping a collection would silently recreate it index-less on the SUT's next
//     write, changing behaviour under test — exactly the trap RespawnRelationalIsolation avoids
//     by truncating rows rather than dropping tables.  E2E scenario row counts are small, so the
//     O(rows) cost of a full-collection delete is irrelevant.
//   • Only the SINGLE database named in the connection string is touched — the one
//     MongoUrl(connectionString).DatabaseName resolves to, i.e. the database Aspire provisioned
//     for THIS dependency (EnvironmentMapper's builder.AddMongoDB(name).AddDatabase(name+"db")).
//     admin/config/local are never enumerated, and no other database on a shared server is ever
//     at risk.
//   • Collections whose name starts with "system." are skipped defensively (Mongo itself
//     forbids writes to most system collections; this guards the general case).
//   • Views are skipped: enumeration uses ListCollectionsAsync (not ListCollectionNamesAsync),
//     whose per-entry BSON document carries a "type" discriminator, and any entry with
//     type == "view" is excluded before DeleteMany is attempted.  A view holds no data of its
//     own — resetting its source collection(s) already resets what the view exposes — and
//     DeleteMany against a view fails outright (CommandNotSupportedOnView, error 166), which
//     would otherwise fail EVERY reset for a store with nothing dirty.  Deliberately NOT
//     filtered to type == "collection": that would also silently skip time-series collections.
//   • KNOWN LIMITATION: a capped or time-series collection rejects DeleteMany outright.  This is
//     deliberate — there is no silent "drop and let the SUT recreate it" fallback, because that
//     would hit the same index-loss trap DeleteMany-not-drop exists to avoid.  Such a collection
//     surfaces as an EnvironmentError naming the dependency (§12.1), exactly like any other reset
//     failure — never silently swallowed.
//   • Lazy initialisation mirrors RespawnRelationalIsolation: the MongoClient and the resolved
//     IMongoDatabase are created on the FIRST EndScenarioAsync, not at construction or Begin.
//     Construction never opens a connection or does connection-string-shape validation beyond
//     null/empty — a connection string with no database name is accepted at construction and
//     only fails, as a wrapped Provision error, at the first EndScenarioAsync.
//   • Every driver failure is wrapped via ScenarioIsolationErrors.Wrap so the ResourceName on the
//     resulting OrchestrationException is always the LOGICAL DEPENDENCY NAME (§12.1).

using MongoDB.Bson;
using MongoDB.Driver;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Per-scenario isolation implementation that deletes all documents from every non-system
/// collection of a MongoDB dependency's target database between scenarios, without dropping
/// collections or their indexes (S04-A-01 follow-up chunk).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Lazy initialisation:</strong> the underlying <see cref="MongoClient"/> and the
/// resolved <see cref="IMongoDatabase"/> are created on the first call to
/// <see cref="EndScenarioAsync"/> — not <see cref="BeginScenarioAsync"/> — and reused for the
/// lifetime of the topology.  Construction never performs I/O: a connection string with no
/// database name is accepted at construction and only fails, as a wrapped Provision error, at
/// the first <see cref="EndScenarioAsync"/>.
/// </para>
/// <para>
/// <strong>§12.1 invariant:</strong> any driver failure is wrapped in
/// <see cref="OrchestrationException"/> with kind <see cref="OrchestrationErrorKind.Provision"/>
/// and <see cref="OrchestrationErrorInfo.ResourceName"/> set to the logical dependency name
/// supplied at construction, so the runner classifies it as <c>EnvironmentError</c> — never as
/// <c>Fail</c> — and names the offending dependency.
/// </para>
/// <para>
/// <strong>Views are skipped:</strong> a view holds no data of its own — resetting its source
/// collection(s) already resets what the view exposes — and <c>DeleteMany</c> against a view
/// fails outright (<c>CommandNotSupportedOnView</c>). Enumeration therefore uses
/// <c>ListCollectionsAsync</c> and excludes any entry whose <c>type</c> is <c>"view"</c>.
/// </para>
/// <para>
/// <strong>Known limitation:</strong> a capped or time-series collection rejects
/// <c>DeleteMany</c> outright.  This surfaces as an <c>EnvironmentError</c> naming the
/// dependency; there is deliberately no silent drop-and-recreate fallback (that would lose the
/// collection's options/indexes — the same trap this class otherwise avoids).
/// </para>
/// <para>
/// <strong>Disposal:</strong> call <see cref="DisposeAsync"/> once the topology is torn down to
/// release the underlying client. <see cref="MongoClient"/> implements <see cref="IDisposable"/>
/// in driver 3.x.
/// </para>
/// </remarks>
public sealed class MongoScenarioIsolation : IScenarioIsolation, IAsyncDisposable
{
    private const string StoreToken = "mongodb";

    private readonly string _dependencyName;
    private readonly string _connectionString;

    // Guard: client and database are created once, lazily, on first reset.
    private MongoClient? _client;
    private IMongoDatabase? _database;
    private bool _disposed;

    /// <summary>
    /// The logical dependency name, exposed for tests (InternalsVisibleTo) so factory dispatch
    /// and composite ordering can be asserted per dependency.
    /// </summary>
    internal string DependencyName => _dependencyName;

    /// <summary>
    /// Initialises a new <see cref="MongoScenarioIsolation"/> for the given MongoDB dependency.
    /// Never opens a connection — construction is pure validation.
    /// </summary>
    /// <param name="dependencyName">
    /// The logical dependency name declared in <c>environment.dependencies</c> (used to name the
    /// dependency in any reset-failure observation). Must not be <see langword="null"/> or empty.
    /// </param>
    /// <param name="connectionString">
    /// The MongoDB connection string for the managed dependency (obtained from
    /// <see cref="SuiteTopology.DiscoveredServices"/> after <see cref="SuiteTopology.StartAsync"/>
    /// completes). Must not be <see langword="null"/> or empty.
    /// </param>
    public MongoScenarioIsolation(string dependencyName, string connectionString)
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
    /// pre-scenario reset is required; the client and target database are resolved lazily, and
    /// state reset performed, in <see cref="EndScenarioAsync"/>.
    /// </remarks>
    public Task BeginScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolves the target database on first use, then deletes every document from every
    /// non-<c>system.</c> collection via an empty-filter <c>DeleteManyAsync</c>. Any failure is
    /// wrapped in <see cref="OrchestrationException"/> (§12.1: Environment error, never Fail).
    /// </remarks>
    public async Task EndScenarioAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureDatabase();
        await ResetAsync(ct).ConfigureAwait(false);
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the underlying <see cref="MongoClient"/>. Safe to call multiple times.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _client?.Dispose();
        _client = null;
        _database = null;

        return ValueTask.CompletedTask;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the target database name from the connection string and creates the
    /// <see cref="MongoClient"/> / <see cref="IMongoDatabase"/> pair once, caching both for the
    /// lifetime of the topology. A no-op once the database has been resolved. Neither step
    /// performs network I/O — the driver defers server discovery to the first real operation
    /// (<see cref="ResetAsync"/>).
    /// </summary>
    private void EnsureDatabase()
    {
        if (_database is not null)
        {
            return;
        }

        string? databaseName;
        try
        {
            databaseName = new MongoUrl(_connectionString).DatabaseName;
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
                new InvalidOperationException(
                    "connection string could not be parsed to resolve the target database."));
        }

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw ScenarioIsolationErrors.Wrap(
                StoreToken,
                "init",
                _dependencyName,
                new InvalidOperationException(
                    "connection string has no database name — the MongoDB reset cannot be " +
                    "scoped to a single database."));
        }

        try
        {
            var client = new MongoClient(_connectionString);
            _database = client.GetDatabase(databaseName);
            _client = client;
        }
        catch (Exception ex)
        {
            throw ScenarioIsolationErrors.Wrap(StoreToken, "init", _dependencyName, ex);
        }
    }

    /// <summary>
    /// Deletes every document from every non-<c>system.</c>, non-view collection in the resolved
    /// database via an empty-filter <c>DeleteManyAsync</c>, preserving collections and their
    /// indexes.  The collection list is re-enumerated on EVERY call (not cached) so a collection
    /// created mid-suite is always included in the reset set — mirroring why
    /// <see cref="RespawnRelationalIsolation"/> re-creates its Respawn checkpoint on every call.
    /// </summary>
    private async Task ResetAsync(CancellationToken ct)
    {
        try
        {
            var collectionInfos = new List<BsonDocument>();
            using (var cursor = await _database!.ListCollectionsAsync(cancellationToken: ct)
                .ConfigureAwait(false))
            {
                while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
                {
                    collectionInfos.AddRange(cursor.Current);
                }
            }

            foreach (var info in collectionInfos)
            {
                var name = info["name"].AsString;

                if (name.StartsWith("system.", StringComparison.Ordinal))
                {
                    continue;
                }

                // A view holds no data of its own — resetting its source collection(s) already
                // resets what the view exposes — and DeleteMany against a view fails outright
                // (CommandNotSupportedOnView). Deliberately NOT filtered to type == "collection":
                // that would also silently skip time-series collections, which stay eligible
                // (and deliberately error, per the class remarks, if capped/time-series).
                var type = info.TryGetValue("type", out var typeValue) ? typeValue.AsString : null;
                if (string.Equals(type, "view", StringComparison.Ordinal))
                {
                    continue;
                }

                var collection = _database.GetCollection<BsonDocument>(name);
                await collection.DeleteManyAsync(new BsonDocument(), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation propagates raw — it is neither a product Fail nor an
            // EnvironmentError (§12.1); see RespawnRelationalIsolation.EnsureConnectionAsync for
            // the identical rationale. A capped/time-series collection's DeleteMany rejection
            // (deliberate — see class remarks) falls through to the wrap below, same as any
            // other driver failure.
            throw;
        }
        catch (Exception ex)
        {
            throw ScenarioIsolationErrors.Wrap(StoreToken, "reset", _dependencyName, ex);
        }
    }
}
