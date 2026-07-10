// Vouchfx.Engine.Abstractions — VarKeys (§5, §13.3.1).
//
// Single source of truth for the reserved key conventions the engine uses inside
// ScriptGlobalVariables.Vars for bookkeeping.  Author-defined variable names must
// not start with these prefixes.
//
// Memory-model note: pure static methods and constants only — no mutable static
// state that could root a collectible AssemblyLoadContext.

namespace Vouchfx.Engine.Abstractions;

/// <summary>
/// Single source of truth for the reserved <c>Vars</c> key conventions that the
/// vouchfx engine uses for bookkeeping within
/// <see cref="ScriptGlobalVariables.Vars"/> (§5, §13.3.1).
/// </summary>
/// <remarks>
/// <para>
/// Five namespaces are reserved within the shared <c>Vars</c> dictionary.
/// Author-defined variable names in <c>.e2e.yaml</c> must not begin with
/// any of these prefixes (enforced at build time by the <c>AstBuilder</c>):
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>svc::</c> — keyed base-URL strings staged by the orchestration layer
///     for each discovered service endpoint before step execution begins.
///     Retrieve with <see cref="Service(string)"/>.
///   </description></item>
///   <item><description>
///     <c>conn::</c> — connection strings staged by the orchestration layer for
///     managed dependencies (e.g. Postgres).
///     Retrieve with <see cref="Connection(string)"/>.
///   </description></item>
///   <item><description>
///     <c>__outcome::</c> — per-step <see cref="StepOutcome"/> instances written
///     by emitted CSX step blocks and consumed by the engine runner after
///     <c>RunIsolatedAsync</c> returns.
///     Retrieve with <see cref="Outcome(string)"/>.
///   </description></item>
///   <item><description>
///     <c>__capture_status::</c> — per-step capture-matched-flag records written
///     by emitted CSX step blocks for provenance reporting (G-01).
///     Retrieve with <see cref="CaptureStatus(string)"/>.
///   </description></item>
///   <item><description>
///     <c>__attempts::</c> — per-step list of <c>AttemptRecord</c> written by the
///     engine-owned RETRY runner for the runner to emit one <c>step-attempt</c>
///     event per attempt (§7, §14).
///     Retrieve with <see cref="Attempts(string)"/>.
///   </description></item>
/// </list>
/// <para>
/// All members are either <see langword="const"/> or pure static methods.  No
/// mutable static state is present, which preserves the memory-model invariant
/// (§5): nothing in this class can root a collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
/// </para>
/// </remarks>
public static class VarKeys
{
    /// <summary>
    /// The prefix for engine-managed service base-URL entries in
    /// <c>ScriptGlobalVariables.Vars</c>.
    /// </summary>
    /// <remarks>
    /// Author variables must not begin with this prefix.
    /// Use <see cref="Service(string)"/> to build the full key.
    /// </remarks>
    public const string ServicesPrefix = "svc::";

    /// <summary>
    /// The prefix for engine-managed managed-dependency connection-string entries in
    /// <c>ScriptGlobalVariables.Vars</c>.
    /// </summary>
    /// <remarks>
    /// Author variables must not begin with this prefix.
    /// Use <see cref="Connection(string)"/> to build the full key.
    /// </remarks>
    public const string ConnectionsPrefix = "conn::";

    /// <summary>
    /// Returns the <c>Vars</c> key under which the orchestration layer stages
    /// the discovered base URL for <paramref name="serviceName"/>.
    /// </summary>
    /// <param name="serviceName">
    /// The service name as declared in the <c>environment.services</c> section
    /// of the <c>.e2e.yaml</c> file.  Must not be <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// A string of the form <c>svc::&lt;serviceName&gt;</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="serviceName"/> is <see langword="null"/> or empty.
    /// </exception>
    public static string Service(string serviceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceName);
        return ServicesPrefix + serviceName;
    }

    /// <summary>
    /// Returns the <c>Vars</c> key under which the orchestration layer stages the
    /// connection string for the managed dependency <paramref name="dependencyName"/>.
    /// </summary>
    /// <param name="dependencyName">
    /// The dependency name as declared in the <c>environment.dependencies</c> section
    /// of the <c>.e2e.yaml</c> file.  Must not be <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// A string of the form <c>conn::&lt;dependencyName&gt;</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="dependencyName"/> is <see langword="null"/> or empty.
    /// </exception>
    public static string Connection(string dependencyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(dependencyName);
        return ConnectionsPrefix + dependencyName;
    }

    /// <summary>
    /// The prefix for engine-managed per-step outcome entries in
    /// <c>ScriptGlobalVariables.Vars</c>.
    /// </summary>
    /// <remarks>
    /// Author variables must not begin with this prefix.
    /// Use <see cref="Outcome(string)"/> to build the full key.
    /// </remarks>
    public const string OutcomePrefix = "__outcome::";

    /// <summary>
    /// Returns the <c>Vars</c> key under which an emitted CSX step block writes
    /// its <see cref="StepOutcome"/> result.
    /// </summary>
    /// <param name="sanitisedStepId">
    /// The step identifier after sanitisation (hyphens replaced with underscores
    /// via <c>CsxFragment.SanitiseId</c>, §13.3.1).  Must not be
    /// <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// A string of the form <c>__outcome::&lt;sanitisedStepId&gt;</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sanitisedStepId"/> is <see langword="null"/> or
    /// empty.
    /// </exception>
    public static string Outcome(string sanitisedStepId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sanitisedStepId);
        return $"{OutcomePrefix}{sanitisedStepId}";
    }

    /// <summary>
    /// The prefix for engine-managed per-step capture-status entries in
    /// <c>ScriptGlobalVariables.Vars</c>.
    /// </summary>
    /// <remarks>
    /// Author variables must not begin with this prefix.
    /// Use <see cref="CaptureStatus(string)"/> to build the full key.
    /// </remarks>
    public const string CaptureStatusPrefix = "__capture_status::";

    /// <summary>
    /// Returns the <c>Vars</c> key under which an emitted CSX step block writes its
    /// per-capture matched-flag record for the runner to consume when emitting
    /// provenance events (G-01, S04-B-02).
    /// </summary>
    /// <param name="sanitisedStepId">
    /// The step identifier after sanitisation (hyphens replaced with underscores
    /// via <c>CsxFragment.SanitiseId</c>, §13.3.1).  Must not be
    /// <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// A string of the form <c>__capture_status::&lt;sanitisedStepId&gt;</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sanitisedStepId"/> is <see langword="null"/> or
    /// empty.
    /// </exception>
    public static string CaptureStatus(string sanitisedStepId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sanitisedStepId);
        return $"{CaptureStatusPrefix}{sanitisedStepId}";
    }

    /// <summary>
    /// The prefix for engine-managed per-step attempt-timeline entries in
    /// <c>ScriptGlobalVariables.Vars</c>.
    /// </summary>
    /// <remarks>
    /// Author variables must not begin with this prefix.
    /// Use <see cref="Attempts(string)"/> to build the full key.
    /// </remarks>
    public const string AttemptsPrefix = "__attempts::";

    /// <summary>
    /// Returns the <c>Vars</c> key under which the engine-owned RETRY runner writes
    /// the per-step list of attempt records for the runner to consume when emitting
    /// one <c>step-attempt</c> event per attempt (§7, §14).
    /// </summary>
    /// <param name="sanitisedStepId">
    /// The step identifier after sanitisation (hyphens replaced with underscores
    /// via <c>CsxFragment.SanitiseId</c>, §13.3.1).  Must not be
    /// <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// A string of the form <c>__attempts::&lt;sanitisedStepId&gt;</c>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sanitisedStepId"/> is <see langword="null"/> or
    /// empty.
    /// </exception>
    public static string Attempts(string sanitisedStepId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sanitisedStepId);
        return $"{AttemptsPrefix}{sanitisedStepId}";
    }
}
