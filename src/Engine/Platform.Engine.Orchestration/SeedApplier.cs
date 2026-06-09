// Platform.Engine.Orchestration — SeedApplier (S05-A-01).
//
// Applies declarative `environment.seed` SQL AFTER the topology is healthy and
// BEFORE the first step runs, inside the same health-gated orchestration
// lifecycle (SuiteTopology.StartAsync, Step 4½).  Because it runs inside the
// orchestration try/catch, any failure is wrapped in OrchestrationException
// (Provision kind) and surfaces as an Environment error (§12.1) — never a
// misattributed assertion Fail.
//
// Design notes:
//   • This is ordinary engine code (NOT a Roslyn script body): `await using` is
//     fine here.  The CSX `using var` prohibition (CLAUDE.md §CsxFragment) does
//     NOT apply.
//   • The file-existence check precedes any connection open, so a missing SQL
//     file fails fast without standing up a database — the no-docker test relies
//     on this ordering.
//   • Dependencies are applied in declared order; within a dependency, SQL files
//     are applied in declared order.  Each file's text is executed as one batch.
//
// Respawn / multi-scenario limitation (documented for A-01, out of scope to fix):
//   Suite-startup seeding runs ONCE.  For a SINGLE-scenario run (the M2 case)
//   seeded rows are present for step 1.  For a MULTI-scenario suite sharing one
//   topology, RespawnPostgresIsolation.EndScenarioAsync truncates the ROWS of all
//   user tables between scenarios (schema/tables persist) — so seeded REFERENCE
//   rows are also truncated after the first scenario.  Persisting reference data
//   across scenarios (Respawn TablesToIgnore, or re-seeding in BeginScenarioAsync)
//   is a future enhancement and is OUT OF SCOPE for A-01.

using Platform.Engine.Authoring.Model;

namespace Platform.Engine.Orchestration;

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
/// dependency, Npgsql connection/execution error) is wrapped in an
/// <see cref="OrchestrationException"/> with kind
/// <see cref="OrchestrationErrorKind.Provision"/> — the same kind
/// <c>RespawnPostgresIsolation</c> uses for state-reset failures.
/// </para>
/// </remarks>
internal static class SeedApplier
{
    /// <summary>
    /// Applies <paramref name="seed"/> against the dependencies in
    /// <paramref name="discoveredServices"/>.  A no-op when <paramref name="seed"/>
    /// is <see langword="null"/> or declares no SQL files.
    /// </summary>
    /// <param name="seed">
    /// The parsed seed block, or <see langword="null"/> when the scenario declares
    /// no <c>environment.seed</c>.
    /// </param>
    /// <param name="discoveredServices">
    /// The flat map of discovered service endpoints and managed-dependency
    /// connection strings, keyed by logical dependency name (postgres entries hold
    /// the ADO.NET connection string).
    /// </param>
    /// <param name="seedBaseDirectory">
    /// The base directory against which relative SQL file paths are resolved.
    /// </param>
    /// <param name="ct">
    /// Propagated to all async I/O.  Must be the last parameter (CA1068).
    /// </param>
    /// <exception cref="OrchestrationException">
    /// Thrown (kind <see cref="OrchestrationErrorKind.Provision"/>) when a named
    /// dependency is absent from <paramref name="discoveredServices"/>, when a
    /// referenced SQL file does not exist, or when Npgsql reports an error while
    /// opening the connection or executing a file's SQL.  Always an Environment
    /// error (§12.1) — never a test <c>Fail</c>.
    /// </exception>
    internal static async Task ApplyAsync(
        SeedSpec? seed,
        IReadOnlyDictionary<string, object> discoveredServices,
        string seedBaseDirectory,
        CancellationToken ct)
    {
        if (seed is null || seed.Dependencies.Count == 0)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(discoveredServices);
        ArgumentException.ThrowIfNullOrEmpty(seedBaseDirectory);

        // Apply dependencies in declared order.
        foreach (var (dependencyName, dependencySeed) in seed.Dependencies)
        {
            var sqlFiles = dependencySeed.Sql;
            if (sqlFiles is null || sqlFiles.Count == 0)
            {
                continue;
            }

            // Resolve the connection string from the discovered services BEFORE
            // touching the filesystem so an unknown dependency fails fast with a
            // clear message.
            if (!discoveredServices.TryGetValue(dependencyName, out var connObj) ||
                connObj is not string connectionString ||
                string.IsNullOrEmpty(connectionString))
            {
                throw ProvisionError(
                    resourceName: dependencyName,
                    detail: $"seed references unknown dependency '{dependencyName}'.");
            }

            // Resolve every SQL file path and verify existence BEFORE opening any
            // connection — a missing file is an Environment error that must not
            // require a live database to detect (the no-docker test relies on this).
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

            await ApplyDependencyAsync(dependencyName, connectionString, resolvedPaths, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opens one connection to the dependency and executes each resolved SQL
    /// file's text as a single batch, in declared order.
    /// </summary>
    private static async Task ApplyDependencyAsync(
        string dependencyName,
        string connectionString,
        IReadOnlyList<string> resolvedPaths,
        CancellationToken ct)
    {
        var connection = new Npgsql.NpgsqlConnection(connectionString);
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

                // Apply each file atomically: a multi-statement file is all-or-
                // nothing, so a partial failure never leaves a half-seeded database
                // (matters once a topology is reused).  Postgres DDL is transactional,
                // so CREATE TABLE + INSERT reference files are safe inside a tx.  On
                // any exception the `await using` dispose rolls the transaction back
                // before the OrchestrationException is thrown.
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
