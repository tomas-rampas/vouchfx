// Platform.Engine.Compilation — AssemblyClosure (§5.6).
//
// The single suite-start hook for engine-assembly graph validation.
// S02-F-03 (reserved-namespace guard) will extend GuardAtSuiteStart in a later
// sprint without altering callers — this method is the stable hook.
using System.Reflection;
using System.Runtime.Loader;

namespace Platform.Engine.Compilation;

/// <summary>
/// Resolves the engine assembly closure and runs the suite-start guard over it
/// (§5.6).
/// </summary>
/// <remarks>
/// <para>
/// This class is the single, stable entry point for all suite-start assembly-graph
/// checks.  At the moment it delegates version-conflict detection to
/// <see cref="AssemblyGraphGuard"/>; later sprints will prepend a reserved-namespace
/// guard (S02-F-03) without changing callers.
/// </para>
/// <para>
/// <b>When to call:</b> call <see cref="GuardAtSuiteStart"/> exactly once during
/// suite initialisation, after the full closure of engine assemblies and customer
/// DLLs has been determined (i.e. after any dynamic provider loading is complete).
/// Never call it per-step or per-script-run.
/// </para>
/// <para>
/// <b>Failure mapping:</b> any exception thrown by <see cref="GuardAtSuiteStart"/>
/// maps to the <em>Environment-error</em> verdict bucket (§12.1).  Only <em>Fail</em>
/// breaks CI by default; an Environment-error surfaces a separate exit code so the
/// caller can distinguish an infra problem from a defect.
/// </para>
/// </remarks>
public static class AssemblyClosure
{
    /// <summary>
    /// Returns all non-dynamic, non-collectible assemblies currently loaded in
    /// <see cref="AssemblyLoadContext.Default"/> as a snapshot list.
    /// </summary>
    /// <remarks>
    /// This list represents the assembly graph that is visible at suite start: BCL,
    /// Roslyn, engine, provider, and any canonical client libraries loaded into the
    /// Default context.  Per-run satellite assemblies loaded into collectible contexts
    /// are intentionally excluded — they are loaded and unloaded per script run and
    /// must not appear in the suite-level graph.
    /// </remarks>
    /// <returns>
    /// A read-only list of loaded <see cref="Assembly"/> instances, ordered by the
    /// runtime's internal enumeration order (non-deterministic across runs but stable
    /// within a single process lifetime).
    /// </returns>
    public static IReadOnlyList<Assembly> ResolveEngineClosure()
    {
        return AssemblyLoadContext.Default.Assemblies
            .Where(a => !a.IsDynamic)
            .ToList();
    }

    /// <summary>
    /// Runs all suite-start assembly-graph guards over <paramref name="closure"/>
    /// and throws the first applicable exception if any guard fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Current guards (in invocation order):
    /// <list type="number">
    ///   <item><description>
    ///     <see cref="AssemblyGraphGuard.ThrowIfConflicting(IEnumerable{Assembly})"/> —
    ///     fails fast when the same simple assembly name appears at two or more distinct
    ///     <see cref="Version"/> values.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// A later task (S02-F-03) prepends a reserved-namespace guard here; this method
    /// is the single suite-start hook.  The exceptions it throws map to the
    /// Environment-error verdict bucket (§12.1).
    /// </para>
    /// </remarks>
    /// <param name="closure">
    /// The assembly graph to inspect; typically the result of
    /// <see cref="ResolveEngineClosure"/> augmented with any customer DLLs.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="closure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="AssemblyVersionConflictException">
    /// Thrown when the version-conflict guard finds the same simple assembly name at
    /// two or more distinct versions.  Maps to the Environment-error verdict (§12.1).
    /// </exception>
    public static void GuardAtSuiteStart(IEnumerable<Assembly> closure)
    {
        ArgumentNullException.ThrowIfNull(closure);

        AssemblyGraphGuard.ThrowIfConflicting(closure);
    }
}
