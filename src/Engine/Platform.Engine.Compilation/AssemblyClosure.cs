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
    ///     <c>Platform.Steps.*</c>.  Assemblies in the trusted set (built by
    ///     <see cref="BuildTrustedSimpleNames"/>) are exempt; trust requires both a
    ///     reserved-prefix name AND co-location with the engine directory (defence-in-depth).
    ///     A hostile DLL with a reserved prefix that is loaded from a different directory is
    ///     NOT trusted and will be scanned.  The residual case (byte-loaded or single-file
    ///     published assembly with a reserved-prefix name) falls back to prefix-only trust —
    ///     a tracked limitation pending engine strong-naming.
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
    /// <c>Platform.Engine.*</c> or <c>Platform.Steps.*</c> — when <em>both</em>
    /// conditions hold:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     Its simple name starts with <c>Platform.Engine.</c> or
    ///     <c>Platform.Steps.</c>, or equals <c>Platform.Sdk</c>.
    ///   </description></item>
    ///   <item><description>
    ///     It is loaded from the <em>same directory</em> as the engine's own assembly
    ///     (<c>typeof(AssemblyClosure).Assembly.Location</c>'s directory), compared
    ///     with <see cref="StringComparison.OrdinalIgnoreCase"/>.  This co-location
    ///     check closes the file-based impersonation case: a hostile DLL named
    ///     <c>Platform.Engine.Evil</c> that lives outside the engine directory is NOT
    ///     trusted and will be scanned by <see cref="ReservedNamespaceGuard"/>.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Residual (tracked limitation):</b> when <c>engineDir</c> is null or empty
    /// (single-file publish) OR the candidate assembly's <c>Location</c> is empty
    /// (loaded from a byte array, e.g. via <see cref="Assembly.Load(byte[])"/>), the
    /// co-location signal is unavailable and the method falls back to prefix-only
    /// trust for that candidate.  A byte-loaded or single-file-published assembly
    /// with a reserved-prefix name is therefore trusted on name alone — this residual
    /// is a tracked limitation pending engine strong-naming.
    /// </para>
    /// </remarks>
    internal static HashSet<string> BuildTrustedSimpleNames(IReadOnlyList<Assembly> list)
    {
        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Determine the directory from which the engine itself was loaded.
        // When engineDir is null or empty (single-file publish), co-location
        // cannot be verified and the fallback applies to every candidate.
        var engineLocation = typeof(AssemblyClosure).Assembly.Location;
        var engineDir = string.IsNullOrEmpty(engineLocation)
            ? null
            : Path.GetDirectoryName(engineLocation);

        foreach (var assembly in list)
        {
            var name = assembly.GetName().Name;
            if (name is null)
                continue;

            // Step 1 — name must carry a reserved prefix or equal Platform.Sdk.
            var hasReservedPrefix =
                name.StartsWith("Platform.Engine.", StringComparison.Ordinal) ||
                name.StartsWith("Platform.Steps.", StringComparison.Ordinal) ||
                name.Equals("Platform.Sdk", StringComparison.Ordinal);

            if (!hasReservedPrefix)
                continue;

            // Step 2 — co-location check (defence-in-depth).
            // When the engine directory or the candidate's Location is unavailable
            // (single-file publish or byte-loaded assembly), the co-location signal
            // cannot be verified; fall back to prefix-only trust.
            var candidateLocation = assembly.Location;

            if (engineDir is null || string.IsNullOrEmpty(candidateLocation))
            {
                // Residual: prefix-only trust — documented limitation above.
                trusted.Add(name);
                continue;
            }

            var candidateDir = Path.GetDirectoryName(candidateLocation);

            if (string.Equals(candidateDir, engineDir, StringComparison.OrdinalIgnoreCase))
            {
                // Co-located with the engine — genuinely trusted.
                trusted.Add(name);
            }

            // If not co-located, the prefix-named DLL is NOT added to the trusted
            // set.  It will be scanned by ReservedNamespaceGuard and refused if it
            // squats on a reserved namespace.
        }

        return trusted;
    }
}
