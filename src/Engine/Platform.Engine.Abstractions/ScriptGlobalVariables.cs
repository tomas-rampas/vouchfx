// Platform.Engine.Abstractions — ScriptGlobalVariables (§5, §13.3.1).
// This is the SOLE typed bridge between the vouchfx host and any emitted script delegate.
// Rule: no static members — the boundary must stay clean so the collectible AssemblyLoadContext
// has nothing rooting the emitted assembly back into the Default context.
namespace Platform.Engine.Abstractions;

/// <summary>
/// The sole bridge between the vouchfx host and an emitted script delegate.
/// Every piece of state the script may observe or mutate passes through this object.
/// No static members are permitted; doing so would root the collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> back into the Default context
/// and prevent unloading — defeating the entire memory model (§5).
/// </summary>
public sealed class ScriptGlobalVariables
{
    /// <summary>
    /// Mutable per-run state dictionary.  Steps read previously captured values and write
    /// new ones here; <c>{placeholder}</c> substitution resolves against this map at
    /// step-execution time (§6, §13.3.1).
    /// </summary>
    public IDictionary<string, object?> Vars { get; }

    /// <summary>
    /// Typed client surface provided by the orchestration layer.
    /// Steps obtain strongly-typed clients (e.g. <c>HttpClient</c>, <c>NpgsqlConnection</c>)
    /// by key.  The surface is intentionally empty in Sprint 1; the full provider SDK (§13)
    /// populates it in later sprints.
    /// </summary>
    public IReadOnlyDictionary<string, object> Services { get; }

    /// <summary>
    /// Initialises a new instance with caller-supplied dictionaries.
    /// </summary>
    /// <param name="vars">
    /// Mutable state map; must not be <see langword="null"/>.
    /// </param>
    /// <param name="services">
    /// Read-only typed-client surface; must not be <see langword="null"/>.
    /// </param>
    public ScriptGlobalVariables(
        IDictionary<string, object?> vars,
        IReadOnlyDictionary<string, object> services)
    {
        Vars = vars ?? throw new ArgumentNullException(nameof(vars));
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Convenience constructor for tests and simple PoC invocations that do not need
    /// a pre-populated service map.
    /// </summary>
    /// <param name="vars">
    /// Mutable state map; must not be <see langword="null"/>.
    /// </param>
    public ScriptGlobalVariables(IDictionary<string, object?> vars)
        : this(vars, new Dictionary<string, object>(StringComparer.Ordinal))
    {
    }
}
