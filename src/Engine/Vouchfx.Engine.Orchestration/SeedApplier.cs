// Vouchfx.Engine.Orchestration — SeedApplier (S05-A-01).
//
// Applies declarative `environment.seed` data AFTER the topology is healthy and
// BEFORE the first step runs, inside the same health-gated orchestration lifecycle
// (SuiteTopology.StartAsync, Step 4½).  Because it runs inside the orchestration
// try/catch, any failure is wrapped in OrchestrationException (Provision kind) and
// surfaces as an Environment error (§12.1) — never a misattributed assertion Fail.
//
// Type-dispatch (`sql` generalised beyond Postgres to sqlserver and mysql, #332):
// each seeded dependency is dispatched on its declared `type` (from
// environment.dependencies):
//   • a relational store (postgres, sqlserver, mysql) + sql → apply SQL now via
//                                   the matching ADO.NET driver (Npgsql / SqlClient
//                                   / MySqlConnector) — the same three drivers
//                                   RespawnRelationalIsolation already resets with,
//                                   so the dispatch shape stays consistent between
//                                   seeding and per-scenario reset.
//   • A seed KIND that does not match the dependency's declared TYPE (e.g. `sql`
//     under a kafka dependency) → a clear Provision error naming the dependency,
//     its type and the unsupported kind (never dial a relational driver
//     against a non-relational connection string).
//   • An unknown/unsupported dependency type that carries any seed → Provision.
//
// `sql` is the only seed kind in the v1 language. The `publish` (broker warm-up)
// and `documents` (document-store fixture) wired-but-deferred seams introduced
// alongside this dispatcher never performed an actual broker publish or
// document-store write (they only read and content-hashed the referenced
// fixture and recorded the intent through IBrokerWarmupSink/IDocumentSeedSink)
// and were REMOVED before general availability — see SeedSpec.cs's header
// remarks. A suite still writing `publish:`/`documents:` under a seed dependency
// now fails schema validation instead of silently no-opping.
//
// Design notes:
//   • This is ordinary engine code (NOT a Roslyn script body): `await using` is
//     fine here.  The CSX `using var` prohibition (CLAUDE.md §CsxFragment) does
//     NOT apply.
//   • The file-existence check precedes any connection open, so a missing
//     fixture fails fast without standing up a database — the no-docker
//     tests rely on this ordering.
//   • Dependencies are applied in declared order; within a dependency, files are
//     applied in declared order.  Each SQL file's text is executed as one batch.
//
// Kept-topology reuse resets seeded relational schemas before this applier runs again.

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
    /// the dependency's type.
    /// </param>
    /// <param name="seedBaseDirectory">
    /// The base directory against which relative fixture file paths are resolved.
    /// </param>
    /// <param name="pathDisclosures">
    /// The run's <see cref="SecurityPathDisclosureLedger"/>, or <see langword="null"/> for a
    /// caller that has none. Accepted fixture paths are recorded as resolved/declared pairs;
    /// diagnostics name declared paths and safe error types/codes, never raw driver messages.
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
        SecurityPathDisclosureLedger? pathDisclosures,
        CancellationToken ct)
    {
        if (seed is null || seed.Dependencies.Count == 0)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(discoveredServices);
        ArgumentNullException.ThrowIfNull(dependencyTypes);
        ArgumentException.ThrowIfNullOrEmpty(seedBaseDirectory);

        ct.ThrowIfCancellationRequested();

        // Apply dependencies in declared order.
        foreach (var (dependencyName, dependencySeed) in seed.Dependencies)
        {
            await ApplyDependencySeedAsync(
                    dependencyName,
                    dependencySeed,
                    discoveredServices,
                    dependencyTypes,
                    seedBaseDirectory,
                    pathDisclosures,
                    ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dispatches a single dependency's seed on its declared type, after rejecting
    /// any seed kind that does not match that type.
    /// </summary>
    private static async Task ApplyDependencySeedAsync(
        string dependencyName,
        DependencySeed dependencySeed,
        IReadOnlyDictionary<string, object> discoveredServices,
        IReadOnlyDictionary<string, string> dependencyTypes,
        string seedBaseDirectory,
        SecurityPathDisclosureLedger? pathDisclosures,
        CancellationToken ct)
    {
        var hasSql = dependencySeed.Sql is { Count: > 0 };

        // A dependency with no seed data at all is a no-op (e.g. an empty mapping).
        if (!hasSql)
        {
            return;
        }

        // Resolve the declared dependency type up front so a mismatch or an
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

        // A seed kind that does not match the dependency's declared type is a
        // Provision error — never blindly dial a relational driver against (say) a
        // Kafka conn string.
        if (relationalKind is null)
        {
            throw MismatchError(
                dependencyName,
                declaredType,
                seedKind: "sql",
                expectedType: "a relational store (postgres, sqlserver, or mysql)");
        }

        await ApplySqlSeedAsync(
                dependencyName,
                dependencySeed.Sql!,
                relationalKind.Value,
                discoveredServices,
                seedBaseDirectory,
                pathDisclosures,
                ct)
            .ConfigureAwait(false);
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
        SecurityPathDisclosureLedger? pathDisclosures,
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

        // Share security artefacts' lexical suite-relative policy. This does not resolve
        // symlinks/junctions: suite authors already have unrestricted script.csharp access.
        // Refuse rooted/base-escaping paths before existence, reads or driver construction.
        var resolvedBaseDirectory = Path.GetFullPath(seedBaseDirectory);
        var files = new List<(string Declared, string Resolved)>(sqlFiles.Count);
        foreach (var sqlFile in sqlFiles)
        {
            if (SecurityArtifactPath.TryResolveContained(sqlFile, resolvedBaseDirectory, out var resolvedPath) is not null)
            {
                // Do not relay the helper's exception-derived diagnostic tail. Only the
                // declared path is safe to name, including for invalid-path failures.
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed SQL file '{sqlFile}' must be a path relative to the suite directory " +
                            "and cannot resolve outside the suite directory.");
            }

            // Retain the privacy ledger for every accepted fixture, including reseeding.
            pathDisclosures?.Record(resolvedPath!, sqlFile);

            if (!File.Exists(resolvedPath))
            {
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed SQL file not found: '{sqlFile}', relative to the suite directory.");
            }

            files.Add((sqlFile, resolvedPath!));
        }

        await ApplyDependencyAsync(dependencyName, relationalKind, connectionString, files, ct)
            .ConfigureAwait(false);
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
        IReadOnlyList<(string Declared, string Resolved)> files,
        CancellationToken ct)
    {
        var connection = CreateRelationalConnection(relationalKind, connectionString);
        try
        {
            try
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed could not open a connection to dependency " +
                            $"'{dependencyName}': {SeedFailureDetails.Describe(ex)}",
                    inner: ex);
            }

            foreach (var (declaredPath, resolvedPath) in files)
            {
                string sqlText;
                try
                {
                    sqlText = await File.ReadAllTextAsync(resolvedPath, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw ProvisionError(
                        resourceName: dependencyName,
                        detail: $"seed could not read SQL file '{declaredPath}': " +
                                $"{SeedFailureDetails.Describe(ex)}",
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
                // A direct retry of a failed MySQL fixture sees that partial state.
                // Kept-topology ReseedAsync instead resets the seeded database before
                // reapplying the fixtures; that does not make an individual file atomic.
                var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                var command = connection.CreateCommand();
                try
                {
                    command.Transaction = tx;
                    command.CommandText = sqlText;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw ProvisionError(
                        resourceName: dependencyName,
                        detail: $"seed SQL file '{declaredPath}' failed against dependency " +
                                $"'{dependencyName}': {SeedFailureDetails.Describe(ex)}",
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
    internal static DbConnection CreateRelationalConnection(RelationalStoreKind relationalKind, string connectionString) =>
        relationalKind switch
        {
            RelationalStoreKind.Postgres => new Npgsql.NpgsqlConnection(connectionString),
            RelationalStoreKind.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(connectionString),
            RelationalStoreKind.MySql => new MySqlConnector.MySqlConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(
                nameof(relationalKind), relationalKind, "Unsupported RelationalStoreKind."),
        };

    /// <summary>
    /// Builds the type-mismatch <see cref="OrchestrationException"/>: a seed kind
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

}
