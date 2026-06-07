// Platform.Engine.Compilation — AssemblyClosure (§5.6).
//
// The single suite-start hook for engine-assembly graph validation.
// Guards run in order: reserved-namespace guard (S02-F-03) then version-conflict
// guard (S02-B-02).  Adding further guards means extending GuardAtSuiteStart without
// altering callers.
using System.Reflection;
using System.Runtime.Loader;

namespace Platform.Engine.Compilation;

/// <summary>
/// Resolves the engine assembly closure and runs the suite-start guards over it
/// (§5.6).
/// </summary>
/// <remarks>
/// <para>
/// This class is the single, stable entry point for all suite-start assembly-graph
/// checks.  Guards are invoked in the following order inside
/// <see cref="GuardAtSuiteStart"/>:
/// <list type="number">
///   <item><description>
///     <see cref="ReservedNamespaceGuard.ThrowIfSquatting"/> — refuses customer DLLs
///     that declare types under the reserved namespaces <c>Platform.Engine.*</c> or
///     <c>Platform.Steps.*</c> (S02-F-03).
///   </description></item>
///   <item><description>
///     <see cref="AssemblyGraphGuard.ThrowIfConflicting(IEnumerable{Assembly})"/> —
///     fails fast when the same simple assembly name appears at two or more distinct
///     version values (S02-B-02).
///   </description></item>
/// </list>
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
    /// Guards are invoked in this order:
    /// <list type="number">
    ///   <item><description>
    ///     Reserved-namespace guard (<see cref="ReservedNamespaceGuard.ThrowIfSquatting"/>) —
    ///     refuses customer DLLs that squat on <c>Platform.Engine.*</c> or
    ///     <c>Platform.Steps.*</c>.  Engine and provider assemblies whose simple name
    ///     starts with <c>Platform.Engine.</c> or <c>Platform.Steps.</c>, plus
    ///     <c>Platform.Sdk</c>, are exempt (see <see cref="BuildTrustedSimpleNames"/>).
    ///   </description></item>
    ///   <item><description>
    ///     Version-conflict guard (<see cref="AssemblyGraphGuard.ThrowIfConflicting(IEnumerable{Assembly})"/>) —
    ///     fails fast when the same simple assembly name appears at two or more distinct
    ///     <see cref="Version"/> values.
    ///   </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Both exceptions map to the <em>Environment-error</em> verdict bucket (§12.1).
    /// </para>
    /// </remarks>
    /// <param name="closure">
    /// The assembly graph to inspect; typically the result of
    /// <see cref="ResolveEngineClosure"/> augmented with any customer DLLs.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="closure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ReservedNamespaceSquatException">
    /// Thrown when the namespace guard finds a customer DLL squatting on a reserved
    /// namespace.  Maps to the Environment-error verdict (§12.1).
    /// </exception>
    /// <exception cref="AssemblyVersionConflictException">
    /// Thrown when the version-conflict guard finds the same simple assembly name at
    /// two or more distinct versions.  Maps to the Environment-error verdict (§12.1).
    /// </exception>
    public static void GuardAtSuiteStart(IEnumerable<Assembly> closure)
    {
        ArgumentNullException.ThrowIfNull(closure);

        // Materialise once so both guards iterate the same snapshot without
        // re-enumerating a potentially lazy source.
        var list = closure as IReadOnlyList<Assembly> ?? closure.ToList();

        var trusted = BuildTrustedSimpleNames(list);

        // Guard 1 — reserved-namespace squat check (S02-F-03).
        ReservedNamespaceGuard.ThrowIfSquatting(list, trusted);

        // Guard 2 — version-conflict check (S02-B-02).
        AssemblyGraphGuard.ThrowIfConflicting(list);
    }

    /// <summary>
    /// Builds the set of assembly simple names that are exempt from the
    /// reserved-namespace scan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An assembly is trusted — and therefore permitted to declare types under
    /// <c>Platform.Engine.*</c> or <c>Platform.Steps.*</c> — when its own simple name
    /// starts with <c>Platform.Engine.</c> or <c>Platform.Steps.</c>, or equals
    /// <c>Platform.Sdk</c>.  These are the engine and provider assemblies that own those
    /// namespaces by design.
    /// </para>
    /// <para>
    /// Residual: an assembly that impersonates a <c>Platform.Engine.*</c> or
    /// <c>Platform.Steps.*</c> <em>name</em> will be included in the trusted set here,
    /// but that is an assembly-name collision which <see cref="AssemblyGraphGuard"/>
    /// handles — it is not this guard's responsibility.
    /// </para>
    /// </remarks>
    private static HashSet<string> BuildTrustedSimpleNames(IReadOnlyList<Assembly> list)
    {
        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in list)
        {
            var name = assembly.GetName().Name;
            if (name is null)
                continue;

            if (name.StartsWith("Platform.Engine.", StringComparison.Ordinal) ||
                name.StartsWith("Platform.Steps.", StringComparison.Ordinal) ||
                name.Equals("Platform.Sdk", StringComparison.Ordinal))
            {
                trusted.Add(name);
            }
        }

        return trusted;
    }
}
