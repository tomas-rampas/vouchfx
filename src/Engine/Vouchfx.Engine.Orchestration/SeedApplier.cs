// Vouchfx.Engine.Orchestration — SeedApplier (S05-A-01 + S05-A-02).
//
// Applies declarative `environment.seed` data AFTER the topology is healthy and
// BEFORE the first step runs, inside the same health-gated orchestration lifecycle
// (SuiteTopology.StartAsync, Step 4½).  Because it runs inside the orchestration
// try/catch, any failure is wrapped in OrchestrationException (Provision kind) and
// surfaces as an Environment error (§12.1) — never a misattributed assertion Fail.
//
// Type-dispatch (A-02; `sql` generalised beyond Postgres): each seeded dependency
// is dispatched on its declared `type` (from environment.dependencies):
//   • a relational store (postgres, sqlserver, mysql) + sql → apply SQL now via
//                                   the matching ADO.NET driver (Npgsql / SqlClient
//                                   / MySqlConnector) — the same three drivers
//                                   RespawnRelationalIsolation already resets with,
//                                   so the dispatch shape stays consistent between
//                                   seeding and per-scenario reset.
//   • a broker (kafka) + publish  → content-hash each payload fixture and record
//                                   the warm-up intent via IBrokerWarmupSink
//                                   (DEFERRED SEAM — no actual publish; Sprint 6).
//   • a document store + documents → content-hash each fixture and record via
//                                    IDocumentSeedSink (DEFERRED SEAM — no actual
//                                    write; later sprint).
//   • A seed KIND that does not match the dependency's declared TYPE (e.g. `sql`
//     under a kafka dependency) → a clear Provision error naming the dependency,
//     its type and the unsupported kind (NIT-1 fix from A-01: never dial a
//     relational driver against a non-relational connection string).
//   • An unknown/unsupported dependency type that carries any seed → Provision.
//
// Deferred-seam honesty (A-02): the broker-publish and document forms do NOT
// publish or write in M2.  "Seeds successfully" means the fixture was read,
// content-hashed (the hash the reproducibility envelope, S05-B-03, will record per
// docs/02 §3.2.2) and recorded through the sink — proving the path is wired.  The
// real broker publish lands with the Kafka provider (Sprint 6); the real
// document write with the document-store provider (later).
//
// Design notes:
//   • This is ordinary engine code (NOT a Roslyn script body): `await using` is
//     fine here.  The CSX `using var` prohibition (CLAUDE.md §CsxFragment) does
//     NOT apply.
//   • The file-existence check precedes any connection open / sink call, so a
//     missing fixture fails fast without standing up a database — the no-docker
//     tests rely on this ordering.
//   • Dependencies are applied in declared order; within a dependency, files are
//     applied in declared order.  Each SQL file's text is executed as one batch.
//
// Multi-scenario note (documented for A-01, out of scope to fix):
//   Suite-startup seeding runs ONCE.  For a SINGLE-scenario run (the M2 case)
//   seeded rows are present for step 1.  For a MULTI-scenario suite sharing one
//   topology, the per-store isolation resets data between scenarios
//   (structure — schema/tables/indexes/mappings — persists) — so seeded
//   REFERENCE rows are also cleared after the first scenario.  Persisting
//   reference data across scenarios (Respawn TablesToIgnore, or re-seeding in
//   BeginScenarioAsync) is a future enhancement and is OUT OF SCOPE for A-01.

using System.Data.Common;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Applies the parsed <c>environment.seed</c> block against the discovered managed
/// dependencies, once, after the topology is healthy and before the first step
/// runs (S05-A-01).
/// </summary>
/// <remarks>
/// <para>
/// Invoked from <see cref="SuiteTopology.StartAsync"/> immediately after service
/// discovery (Step 4) and before the fixture is returned, inside the outer
/// try/catch that disposes the topology on failure.  A thrown
/// <see cref="OrchestrationException"/> therefore disposes the topology and
/// propagates as an Environment error (§12.1) — never a test <c>Fail</c>.
/// </para>
/// <para>
/// <strong>Failure mapping:</strong> every failure (missing SQL file, unknown
/// dependency, relational driver connection/execution error) is wrapped in an
/// <see cref="OrchestrationException"/> with kind
/// <see cref="OrchestrationErrorKind.Provision"/> — the same kind
/// <c>RespawnRelationalIsolation</c> uses for state-reset failures.
/// </para>
/// </remarks>
internal static class SeedApplier
{
    /// <summary>
    /// Dependency types treated as message brokers for seed dispatch (A-02).  Only
    /// <c>kafka</c> in M2; the broker provider (Sprint 6) is the future plug-in.
    /// </summary>
    private static readonly HashSet<string> BrokerTypes =
        new(StringComparer.OrdinalIgnoreCase) { "kafka" };

    /// <summary>
    /// Dependency types treated as document stores for seed dispatch (A-02).  The
    /// concrete provider lands in a later sprint; listed here so a mismatch error
    /// is precise.
    /// </summary>
    private static readonly HashSet<string> DocumentStoreTypes =
        new(StringComparer.OrdinalIgnoreCase) { "mongodb", "mongo", "elasticsearch", "cosmos" };

    /// <summary>
    /// Applies <paramref name="seed"/> against the dependencies in
    /// <paramref name="discoveredServices"/>, dispatching each by its declared
    /// <paramref name="dependencyTypes"/> entry.  A no-op when
    /// <paramref name="seed"/> is <see langword="null"/> or declares no seed data.
    /// </summary>
    /// <param name="seed">
    /// The parsed seed block, or <see langword="null"/> when the scenario declares
    /// no <c>environment.seed</c>.
    /// </param>
    /// <param name="discoveredServices">
    /// The flat map of discovered service endpoints and managed-dependency
    /// connection strings, keyed by logical dependency name (relational entries
    /// hold the ADO.NET connection string).
    /// </param>
    /// <param name="dependencyTypes">
    /// Map from logical dependency name to its declared <c>type</c> (e.g.
    /// <c>postgres</c>, <c>kafka</c>), from <c>environment.dependencies</c>.  Used to
    /// dispatch each seeded dependency and to reject a seed kind that does not match
    /// the dependency's type (NIT-1).
    /// </param>
    /// <param name="seedBaseDirectory">
    /// The base directory against which relative fixture file paths are resolved.
    /// </param>
    /// <param name="brokerSink">
    /// The deferred seam that records broker warm-up publishes (A-02).  When
    /// <see langword="null"/>, a default <see cref="RecordingBrokerWarmupSink"/> is
    /// used — the Sprint-6 Kafka provider supplies a publishing implementation.
    /// </param>
    /// <param name="documentSink">
    /// The deferred seam that records document fixtures (A-02).  When
    /// <see langword="null"/>, a default <see cref="RecordingDocumentSeedSink"/> is
    /// used — a later document-store provider supplies a writing implementation.
    /// </param>
    /// <param name="ct">
    /// Propagated to all async I/O.  Must be the last parameter (CA1068).
    /// </param>
    /// <exception cref="OrchestrationException">
    /// Thrown (kind <see cref="OrchestrationErrorKind.Provision"/>) when a named
    /// dependency is absent from <paramref name="discoveredServices"/>, when a
    /// referenced fixture file does not exist, when a seed kind does not match the
    /// dependency's declared type, when the dependency type is unknown, or when
    /// the matching ADO.NET driver (Npgsql / SqlClient / MySqlConnector) reports an
    /// error while opening the connection or executing SQL.
    /// Always an Environment error (§12.1) — never a test <c>Fail</c>.
    /// </exception>
    internal static async Task ApplyAsync(
        SeedSpec? seed,
        IReadOnlyDictionary<string, object> discoveredServices,
        IReadOnlyDictionary<string, string> dependencyTypes,
        string seedBaseDirectory,
        IBrokerWarmupSink? brokerSink,
        IDocumentSeedSink? documentSink,
        CancellationToken ct)
    {
        if (seed is null || seed.Dependencies.Count == 0)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(discoveredServices);
        ArgumentNullException.ThrowIfNull(dependencyTypes);
        ArgumentException.ThrowIfNullOrEmpty(seedBaseDirectory);

        var broker = brokerSink ?? new RecordingBrokerWarmupSink();
        var documents = documentSink ?? new RecordingDocumentSeedSink();

        // Apply dependencies in declared order.
        foreach (var (dependencyName, dependencySeed) in seed.Dependencies)
        {
            await ApplyDependencySeedAsync(
                    dependencyName,
                    dependencySeed,
                    discoveredServices,
                    dependencyTypes,
                    seedBaseDirectory,
                    broker,
                    documents,
                    ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatches a single dependency's seed on its declared type, after rejecting
    /// any seed kind that does not match that type (NIT-1).
    /// </summary>
    private static async Task ApplyDependencySeedAsync(
        string dependencyName,
        DependencySeed dependencySeed,
        IReadOnlyDictionary<string, object> discoveredServices,
        IReadOnlyDictionary<string, string> dependencyTypes,
        string seedBaseDirectory,
        IBrokerWarmupSink broker,
        IDocumentSeedSink documents,
        CancellationToken ct)
    {
        var hasSql = dependencySeed.Sql is { Count: > 0 };
        var hasPublish = dependencySeed.Publish is { Count: > 0 };
        var hasDocuments = dependencySeed.Documents is { Count: > 0 };

        // A dependency with no seed data at all is a no-op (e.g. an empty mapping).
        if (!hasSql && !hasPublish && !hasDocuments)
        {
            return;
        }

        // Resolve the declared dependency type up front so a mismatch (NIT-1) or an
        // unknown type is reported with a clear message before any I/O.
        if (!dependencyTypes.TryGetValue(dependencyName, out var declaredType) ||
            string.IsNullOrEmpty(declaredType))
        {
            throw ProvisionError(
                resourceName: dependencyName,
                detail: $"seed references dependency '{dependencyName}', which is not declared " +
                        $"in environment.dependencies (no known type to dispatch its seed).");
        }

        var relationalKind = ScenarioIsolationFactory.MapRelationalKind(declaredType);
        var isRelational = relationalKind is not null;
        var isBroker = BrokerTypes.Contains(declaredType);
        var isDocumentStore = DocumentStoreTypes.Contains(declaredType);

        // NIT-1: a seed kind that does not match the dependency's declared type is a
        // Provision error — never blindly dial a relational driver against (say) a
        // Kafka conn string.
        if (hasSql && !isRelational)
        {
            throw MismatchError(
                dependencyName,
                declaredType,
                seedKind: "sql",
                expectedType: "a relational store (postgres, sqlserver, or mysql)");
        }

        if (hasPublish && !isBroker)
        {
            throw MismatchError(dependencyName, declaredType, seedKind: "publish", expectedType: "a broker (e.g. kafka)");
        }

        if (hasDocuments && !isDocumentStore)
        {
            throw MismatchError(dependencyName, declaredType, seedKind: "documents", expectedType: "a document store");
        }

        // The declared type must be one this version of the engine can seed.  (A
        // relational/broker/document-store dependency with NO matching seed kind
        // has already returned above; reaching here with an unknown type means it
        // carried a seed kind but the type is not seedable at all.)
        //
        // Defensive guard: this branch is provably unreachable given the mismatch
        // checks above.  Past the early empty-seed return at least one of hasSql /
        // hasPublish / hasDocuments is set, and each mismatch check above throws
        // unless its matching type flag (isRelational / isBroker / isDocumentStore)
        // is true — so at least one flag is always true here.  Kept as a
        // belt-and-braces safety net; do not mistake it for live-covered behaviour.
        if (!isRelational && !isBroker && !isDocumentStore)
        {
            throw ProvisionError(
                resourceName: dependencyName,
                detail: $"seed for dependency '{dependencyName}' has an unsupported dependency " +
                        $"type '{declaredType}'; no seed provider handles it.");
        }

        if (hasSql)
        {
            // relationalKind is guaranteed non-null here: hasSql required isRelational
            // (hence relationalKind is not null) above, or this line would have
            // already thrown the NIT-1 mismatch.
            await ApplySqlSeedAsync(
                    dependencyName,
                    dependencySeed.Sql!,
                    relationalKind!.Value,
                    discoveredServices,
                    seedBaseDirectory,
                    ct)
                .ConfigureAwait(false);
        }

        if (hasPublish)
        {
            await ApplyPublishSeedAsync(dependencyName, dependencySeed.Publish!, seedBaseDirectory, broker, ct)
                .ConfigureAwait(false);
        }

        if (hasDocuments)
        {
            await ApplyDocumentSeedAsync(dependencyName, dependencySeed.Documents!, seedBaseDirectory, documents, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies the <c>sql</c> seed for a relational dependency (postgres, sqlserver,
    /// or mysql — A-01 behaviour, generalised): resolve + verify each file, then
    /// execute each as one batch against the matching ADO.NET driver.
    /// </summary>
    private static async Task ApplySqlSeedAsync(
        string dependencyName,
        IReadOnlyList<string> sqlFiles,
        RelationalStoreKind relationalKind,
        IReadOnlyDictionary<string, object> discoveredServices,
        string seedBaseDirectory,
        CancellationToken ct)
    {
        // Resolve the connection string from the discovered services BEFORE touching
        // the filesystem so an unknown dependency fails fast with a clear message.
        if (!discoveredServices.TryGetValue(dependencyName, out var connObj) ||
            connObj is not string connectionString ||
            string.IsNullOrEmpty(connectionString))
        {
            throw ProvisionError(
                resourceName: dependencyName,
                detail: $"seed references unknown dependency '{dependencyName}'.");
        }

        // Resolve every SQL file path and verify existence BEFORE opening any
        // connection — a missing file is an Environment error that must not require
        // a live database to detect (the no-docker test relies on this).
        var resolvedPaths = new List<string>(sqlFiles.Count);
        foreach (var sqlFile in sqlFiles)
        {
            var resolvedPath = Path.GetFullPath(Path.Combine(seedBaseDirectory, sqlFile));
            if (!File.Exists(resolvedPath))
            {
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed SQL file not found: '{resolvedPath}'.");
            }

            resolvedPaths.Add(resolvedPath);
        }

        await ApplyDependencyAsync(dependencyName, relationalKind, connectionString, resolvedPaths, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Processes a broker dependency's <c>publish</c> seed through the deferred
    /// warm-up seam (A-02): content-hash each payload fixture (validating it exists
    /// and is readable) and record the intent — does NOT publish.  The real publish
    /// lands with the Kafka provider in Sprint 6.
    /// </summary>
    private static async Task ApplyPublishSeedAsync(
        string dependencyName,
        IReadOnlyList<PublishSeed> publishes,
        string seedBaseDirectory,
        IBrokerWarmupSink broker,
        CancellationToken ct)
    {
        foreach (var publish in publishes)
        {
            var resolvedPath = Path.GetFullPath(Path.Combine(seedBaseDirectory, publish.PayloadFrom));
            string contentHash;
            try
            {
                // Content-hashing reads the file's bytes, so this also validates the
                // fixture exists and is readable (the deferred-but-wired guarantee).
                contentHash = SeedFixtures.ComputeContentHash(seedBaseDirectory, publish.PayloadFrom);
            }
            catch (FileNotFoundException ex)
            {
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed publish payload file not found: '{resolvedPath}'.",
                    inner: ex);
            }
            catch (Exception ex)
            {
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed could not read publish payload file '{resolvedPath}': " +
                            $"{TrimDetail(ex.Message)}",
                    inner: ex);
            }

            var recorded = new RecordedPublish(dependencyName, publish.Topic, resolvedPath, contentHash);
            await broker.PublishAsync(recorded, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes a document-store dependency's <c>documents</c> seed through the
    /// deferred document seam (A-02): content-hash each fixture (validating it
    /// exists and is readable) and record the intent — does NOT write to a store.
    /// The real write lands with the document-store provider in a later sprint.
    /// </summary>
    private static async Task ApplyDocumentSeedAsync(
        string dependencyName,
        IReadOnlyList<DocumentSeed> seedDocuments,
        string seedBaseDirectory,
        IDocumentSeedSink documents,
        CancellationToken ct)
    {
        foreach (var document in seedDocuments)
        {
            var resolvedPath = Path.GetFullPath(Path.Combine(seedBaseDirectory, document.From));
            string contentHash;
            try
            {
                contentHash = SeedFixtures.ComputeContentHash(seedBaseDirectory, document.From);
            }
            catch (FileNotFoundException ex)
            {
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed document file not found: '{resolvedPath}'.",
                    inner: ex);
            }
            catch (Exception ex)
            {
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed could not read document file '{resolvedPath}': " +
                            $"{TrimDetail(ex.Message)}",
                    inner: ex);
            }

            var recorded = new RecordedDocument(dependencyName, document.Collection, resolvedPath, contentHash);
            await documents.WriteAsync(recorded, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens one connection to the dependency (via the ADO.NET driver matching
    /// <paramref name="relationalKind"/>) and executes each resolved SQL file's
    /// text as a single batch, in declared order.
    /// </summary>
    /// <remarks>
    /// The connection, transaction and command are all handled through the plain
    /// <see cref="DbConnection"/>/<see cref="DbTransaction"/>/<see cref="DbCommand"/>
    /// ADO.NET base types — the SAME code path now runs for Postgres, SQL Server and
    /// MySQL, so the per-file transaction and error-surface semantics documented
    /// below are, by construction, identical across all three drivers (never a
    /// parallel Postgres-only implementation to drift out of step).
    /// </remarks>
    private static async Task ApplyDependencyAsync(
        string dependencyName,
        RelationalStoreKind relationalKind,
        string connectionString,
        IReadOnlyList<string> resolvedPaths,
        CancellationToken ct)
    {
        var connection = CreateRelationalConnection(relationalKind, connectionString);
        try
        {
            try
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed could not open a connection to dependency " +
                            $"'{dependencyName}': {TrimDetail(ex.Message)}",
                    inner: ex);
            }

            foreach (var resolvedPath in resolvedPaths)
            {
                string sqlText;
                try
                {
                    sqlText = await File.ReadAllTextAsync(resolvedPath, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw ProvisionError(
                        resourceName: dependencyName,
                        detail: $"seed could not read SQL file '{resolvedPath}': " +
                                $"{TrimDetail(ex.Message)}",
                        inner: ex);
                }

                // Apply each file atomically: wrap it in one transaction so a
                // mid-file failure never leaves a half-seeded database (matters once
                // a topology is reused).  On any exception the `finally` below
                // disposes the transaction WITHOUT committing it, which rolls it
                // back — the standard ADO.NET DbTransaction.Dispose contract,
                // honoured identically by NpgsqlTransaction, SqlTransaction and
                // MySqlTransaction.
                //
                // KNOWN DIVERGENCE — MySQL voids per-file atomicity entirely once a
                // file contains DDL.  Postgres and SQL Server both support fully
                // transactional DDL: a CREATE TABLE inside this transaction is undone
                // by the rollback above exactly like any DML statement, so the
                // "whole file or nothing" guarantee above holds for them.
                //
                // MySQL does not.  Per its "statements that cause an implicit commit"
                // rules, a DDL statement (CREATE TABLE, ALTER TABLE, …) commits the
                // current transaction the moment it runs.  Crucially that implicit
                // commit ENDS the transaction opened just below and MySQL does not
                // open a replacement — the session reverts to autocommit, so every
                // statement AFTER the DDL commits individually as it executes.  When
                // a later statement then fails there is no open transaction left for
                // the rollback-on-dispose to undo, and nothing is reverted: not the
                // DDL, and not the successful DML that followed it.
                //
                // Measured, not theorised.  An earlier revision of this comment
                // claimed MySQL "implicitly starts a fresh transaction" so the DML
                // would still roll back.  It does not.  SeedApplierMysqlDockerTests
                // runs CREATE TABLE + INSERT + a duplicate-key INSERT against a real
                // MySQL: the table survives AND so does the first row (row count 1,
                // not 0).  The plausible half of that theory was wrong.
                //
                // Author guidance: a MySQL seed fixture must be idempotent
                // THROUGHOUT, not merely in its DDL — CREATE TABLE IF NOT EXISTS and
                // INSERT … ON DUPLICATE KEY UPDATE — because a fixture that fails
                // part-way leaves everything before the failure applied, and the next
                // scenario will run on top of it.
                var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                var command = connection.CreateCommand();
                try
                {
                    command.Transaction = tx;
                    command.CommandText = sqlText;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw ProvisionError(
                        resourceName: dependencyName,
                        detail: $"seed SQL file '{resolvedPath}' failed against dependency " +
                                $"'{dependencyName}': {TrimDetail(ex.Message)}",
                        inner: ex);
                }
                finally
                {
                    await command.DisposeAsync().ConfigureAwait(false);
                    await tx.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the kind-specific <see cref="DbConnection"/> instance for
    /// <paramref name="relationalKind"/>. Does not open it. Mirrors
    /// <c>RespawnRelationalIsolation.CreateConnection</c> exactly (same three
    /// drivers), so a dependency seeded here and reset between scenarios there is
    /// always dialled the same way.
    /// </summary>
    private static DbConnection CreateRelationalConnection(RelationalStoreKind relationalKind, string connectionString) =>
        relationalKind switch
        {
            RelationalStoreKind.Postgres => new Npgsql.NpgsqlConnection(connectionString),
            RelationalStoreKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(connectionString),
            RelationalStoreKind.MySql => new MySqlConnector.MySqlConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(
                nameof(relationalKind), relationalKind, "Unsupported RelationalStoreKind."),
        };

    /// <summary>
    /// Builds the NIT-1 mismatch <see cref="OrchestrationException"/>: a seed kind
    /// was declared under a dependency whose type cannot accept it (e.g. <c>sql</c>
    /// under a kafka dependency).  Names the dependency, its declared type, the
    /// unsupported seed kind, and the type that kind expects.
    /// </summary>
    private static OrchestrationException MismatchError(
        string dependencyName,
        string declaredType,
        string seedKind,
        string expectedType) =>
        ProvisionError(
            resourceName: dependencyName,
            detail: $"seed kind '{seedKind}' under dependency '{dependencyName}' is not supported " +
                    $"for its declared type '{declaredType}'; '{seedKind}' applies to {expectedType}.");

    /// <summary>
    /// Builds an <see cref="OrchestrationException"/> with kind
    /// <see cref="OrchestrationErrorKind.Provision"/> — the canonical
    /// Environment-error wrapping for seed failures (§12.1).
    /// </summary>
    private static OrchestrationException ProvisionError(
        string resourceName,
        string detail,
        Exception? inner = null)
    {
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.Provision,
            ResourceName: resourceName,
            RegistryHost: null,
            AuthStatus: null,
            Detail: detail);
        return new OrchestrationException(info, inner);
    }

    /// <summary>
    /// Returns a trimmed, single-line summary of <paramref name="message"/>
    /// capped at 200 characters for display in event streams and logs.
    /// </summary>
    private static string TrimDetail(string message)
    {
        var collapsed = message.ReplaceLineEndings(" ").Trim();
        return collapsed.Length > 200 ? collapsed[..200] : collapsed;
    }
}
