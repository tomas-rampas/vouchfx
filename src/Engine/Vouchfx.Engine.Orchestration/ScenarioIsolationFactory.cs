// Vouchfx.Engine.Orchestration — ScenarioIsolationFactory (state-reset generalisation).
//
// Replaces ScenarioRunner's old "sniff DiscoveredServices for a Host= substring" picker
// with proper name+type dispatch: for every declared dependency that has both a
// declared type AND a discovered connection string, look up the matching
// IScenarioIsolation implementation. Pure over plain collections (no SuiteTopology
// dependency) so it is unit-testable without a topology or Docker.
//
// Dispatch table (case-insensitive on the declared type string):
//   postgres, sqlserver, mysql        → RespawnRelationalIsolation (this chunk).
//   mongodb, redis, elasticsearch     → SKIPPED — wired in the follow-up chunk.
//   kafka, rabbitmq, nats, mailpit,
//   azureservicebus, dynamodb, minio  → SKIPPED — no mutable dependency state to
//                                        reset between scenarios.
//   (no declared type / no discovered
//    connection string)               → SKIPPED defensively (mirrors the pre-existing
//                                        BuildIsolation behaviour of silently ignoring
//                                        anything it cannot positively identify).

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Builds the composite <see cref="IScenarioIsolation"/> for a topology's declared
/// dependencies, dispatching on the declared <c>type</c> rather than sniffing the
/// discovered connection-string shape (S04 generalisation).
/// </summary>
public static class ScenarioIsolationFactory
{
    /// <summary>
    /// Builds the <see cref="IScenarioIsolation"/> that resets every resettable
    /// dependency declared in <paramref name="dependencyNames"/>.
    /// </summary>
    /// <param name="dependencyNames">
    /// The logical dependency names in declaration order (drives the deterministic
    /// order children run in when more than one dependency is resettable).
    /// </param>
    /// <param name="dependencyTypes">
    /// Logical dependency name → declared <c>environment.dependencies[*].type</c>
    /// string (e.g. <c>"postgres"</c>). A name absent from this map is skipped.
    /// </param>
    /// <param name="discoveredServices">
    /// Logical dependency name → discovered connection string / endpoint value. A
    /// name whose value is missing, not a <see cref="string"/>, or empty is skipped.
    /// </param>
    /// <returns>
    /// <see cref="NullScenarioIsolation"/> when no dependency is resettable; the single
    /// <see cref="IScenarioIsolation"/> directly when exactly one is resettable; a
    /// <see cref="CompositeScenarioIsolation"/> (iterating in <paramref name="dependencyNames"/>
    /// order) when more than one is resettable.
    /// </returns>
    public static IScenarioIsolation Create(
        IReadOnlyList<string> dependencyNames,
        IReadOnlyDictionary<string, string> dependencyTypes,
        IReadOnlyDictionary<string, object> discoveredServices)
    {
        ArgumentNullException.ThrowIfNull(dependencyNames);
        ArgumentNullException.ThrowIfNull(dependencyTypes);
        ArgumentNullException.ThrowIfNull(discoveredServices);

        List<IScenarioIsolation>? resettable = null;

        foreach (var name in dependencyNames)
        {
            if (!dependencyTypes.TryGetValue(name, out var declaredType) ||
                string.IsNullOrEmpty(declaredType))
            {
                // No declared type: nothing to dispatch on — skip defensively.
                continue;
            }

            if (!discoveredServices.TryGetValue(name, out var value) ||
                value is not string connectionString ||
                string.IsNullOrEmpty(connectionString))
            {
                // No discovered connection string: skip defensively (mirrors the
                // pre-existing BuildIsolation behaviour).
                continue;
            }

            var kind = MapRelationalKind(declaredType);
            if (kind is null)
            {
                continue;
            }

            resettable ??= new List<IScenarioIsolation>();
            resettable.Add(new RespawnRelationalIsolation(name, kind.Value, connectionString));
        }

        return resettable switch
        {
            null or { Count: 0 } => new NullScenarioIsolation(),
            { Count: 1 } => resettable[0],
            _ => new CompositeScenarioIsolation(resettable),
        };
    }

    /// <summary>
    /// Maps a declared dependency <c>type</c> string to the <see cref="RelationalStoreKind"/>
    /// <see cref="RespawnRelationalIsolation"/> resets it with, or <see langword="null"/>
    /// when the type is not (yet) a resettable relational store.
    /// </summary>
    private static RelationalStoreKind? MapRelationalKind(string declaredType)
    {
        if (string.Equals(declaredType, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            return RelationalStoreKind.Postgres;
        }

        if (string.Equals(declaredType, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            return RelationalStoreKind.SqlServer;
        }

        if (string.Equals(declaredType, "mysql", StringComparison.OrdinalIgnoreCase))
        {
            return RelationalStoreKind.MySql;
        }

        // mongodb / redis / elasticsearch: wired in the follow-up chunk.
        // kafka / rabbitmq / nats / mailpit / azureservicebus / dynamodb / minio:
        // no mutable dependency state to reset between scenarios.
        return null;
    }
}
