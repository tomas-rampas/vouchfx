// Vouchfx.Engine.Orchestration — ScenarioIsolationErrors (state-reset generalisation).
//
// Single shared helper that formats every IScenarioIsolation reset failure into a
// byte-consistent OrchestrationException (§12.1: Environment error, never a test Fail).
// All six wired stores share this SAME helper so the detail wording never diverges
// between store implementations: RespawnRelationalIsolation (Postgres, SQL Server,
// MySQL), MongoScenarioIsolation, RedisScenarioIsolation, and
// ElasticsearchScenarioIsolation.

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Formats <see cref="OrchestrationException"/> instances for
/// <see cref="IScenarioIsolation"/> reset failures, so every store-specific
/// implementation reports failures in an identical shape (§12.1).
/// </summary>
internal static class ScenarioIsolationErrors
{
    /// <summary>
    /// Wraps <paramref name="inner"/> in an <see cref="OrchestrationException"/> whose
    /// <see cref="OrchestrationErrorInfo.ResourceName"/> names the failing dependency
    /// (not a fixed placeholder such as <c>"respawn-reset"</c>), so the environment-error
    /// observation always tells the author which dependency's reset failed.
    /// </summary>
    /// <param name="storeKind">
    /// A short lower-case token for the store technology (e.g. <c>"postgres"</c>,
    /// <c>"sqlserver"</c>, <c>"mysql"</c>).
    /// </param>
    /// <param name="stage">
    /// The reset stage that failed: <c>"init"</c> (opening the connection),
    /// <c>"create"</c> (building the Respawn checkpoint), or <c>"reset"</c>
    /// (executing the reset itself).
    /// </param>
    /// <param name="dependencyName">
    /// The logical dependency name declared in <c>environment.dependencies</c> —
    /// used both as <see cref="OrchestrationErrorInfo.ResourceName"/> and in the
    /// detail message.
    /// </param>
    /// <param name="inner">The underlying driver/Respawn exception.</param>
    /// <returns>
    /// A new <see cref="OrchestrationException"/> with
    /// <see cref="OrchestrationErrorKind.Provision"/>, ready to throw.
    /// </returns>
    public static OrchestrationException Wrap(
        string storeKind,
        string stage,
        string dependencyName,
        Exception inner)
    {
        var info = new OrchestrationErrorInfo(
            Kind: OrchestrationErrorKind.Provision,
            ResourceName: dependencyName,
            RegistryHost: null,
            AuthStatus: null,
            Detail: $"state reset ({storeKind}) {stage} failed for dependency '{dependencyName}': "
                    + TrimDetail(inner.Message));
        return new OrchestrationException(info, inner);
    }

    /// <summary>
    /// Returns a trimmed, single-line summary of <paramref name="message"/> capped at 200
    /// characters for display in event streams and logs.  The single copy every
    /// <see cref="IScenarioIsolation"/> implementation shares, so detail formatting never
    /// drifts between store-specific classes.
    /// </summary>
    public static string TrimDetail(string message)
    {
        var collapsed = (message ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return collapsed.Length > 200 ? collapsed[..200] : collapsed;
    }
}
