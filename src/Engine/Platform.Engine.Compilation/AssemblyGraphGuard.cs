// Platform.Engine.Compilation — AssemblyGraphGuard (§5.6).
//
// Detects version conflicts in the assembly graph presented at suite start.
// A conflict exists when the same simple assembly name appears at two or more
// distinct Version values.  Detection must happen at startup — never at script
// runtime where a FileLoadException would surface deep inside a step and corrupt
// the verdict.
//
// Design note: the guard is purely functional (no instance state, no side effects
// beyond throwing).  FindConflicts returns the raw conflicts for unit-testability;
// ThrowIfConflicting is the suite-start entry point.
using System.Reflection;

namespace Platform.Engine.Compilation;

/// <summary>
/// Guards the assembly graph against version conflicts at suite start (§5.6).
/// </summary>
/// <remarks>
/// <para>
/// Reserved namespaces: <c>Platform.Engine.*</c> (engine) and
/// <c>Platform.Steps.*</c> (providers).  Customer DLLs declaring these namespaces
/// are refused at startup; that namespace check is a separate responsibility.
/// This class focuses solely on version uniqueness within a given set of assembly
/// names.
/// </para>
/// <para>
/// <b>When to call:</b> once, during suite initialisation, after the full closure
/// of engine assemblies and customer DLLs has been determined.  Do NOT call
/// per-step or per-script-run.
/// </para>
/// <para>
/// <b>Sprint-2 note:</b> <see cref="AssemblyVersionConflictException"/> maps to the
/// <em>Environment-error</em> verdict bucket (§12.1).  Until the verdict taxonomy is
/// wired, treat it as a fatal suite-start failure.
/// </para>
/// </remarks>
public static class AssemblyGraphGuard
{
    /// <summary>
    /// Returns the set of assembly simple names that appear at more than one
    /// distinct <see cref="Version"/> in <paramref name="assemblies"/>.
    /// </summary>
    /// <param name="assemblies">
    /// The <see cref="AssemblyName"/> collection to scan.  May contain duplicate
    /// names; the method groups by <see cref="AssemblyName.Name"/> and collects
    /// distinct versions.  Entries with a <see langword="null"/>
    /// <see cref="AssemblyName.Name"/> or a <see langword="null"/>
    /// <see cref="AssemblyName.Version"/> are ignored.
    /// </param>
    /// <returns>
    /// A (possibly empty) read-only list of <see cref="AssemblyConflict"/> records.
    /// An empty list means no conflicts were found.
    /// </returns>
    public static IReadOnlyList<AssemblyConflict> FindConflicts(IEnumerable<AssemblyName> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        // Group by simple name and collect distinct versions per name.
        var byName = new Dictionary<string, HashSet<Version>>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assemblies)
        {
            if (name.Name is null || name.Version is null)
                continue;

            if (!byName.TryGetValue(name.Name, out var versions))
            {
                versions = new HashSet<Version>();
                byName[name.Name] = versions;
            }

            versions.Add(name.Version);
        }

        var conflicts = new List<AssemblyConflict>();
        foreach (var kvp in byName)
        {
            if (kvp.Value.Count > 1)
            {
                // Preserve a deterministic order (ascending) for the message.
                var ordered = kvp.Value.OrderBy(v => v).ToList();
                conflicts.Add(new AssemblyConflict(kvp.Key, ordered));
            }
        }

        // Sort by simple name for a deterministic message across runs.
        conflicts.Sort((a, b) =>
            string.Compare(a.SimpleName, b.SimpleName, StringComparison.OrdinalIgnoreCase));

        return conflicts;
    }

    /// <summary>
    /// Convenience overload that accepts loaded <see cref="Assembly"/> instances and
    /// forwards their <see cref="Assembly.GetName()"/> results to
    /// <see cref="FindConflicts(IEnumerable{AssemblyName})"/>.
    /// </summary>
    /// <param name="assemblies">
    /// The loaded assemblies to inspect.  Dynamic assemblies (those with no
    /// on-disk location) and assemblies whose <see cref="Assembly.GetName()"/>
    /// returns a null or version-less name are silently ignored.
    /// </param>
    /// <returns>
    /// A (possibly empty) read-only list of <see cref="AssemblyConflict"/> records.
    /// </returns>
    public static IReadOnlyList<AssemblyConflict> FindConflicts(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return FindConflicts(
            assemblies
                .Where(a => !a.IsDynamic)
                .Select(a => a.GetName()));
    }

    /// <summary>
    /// Throws <see cref="AssemblyVersionConflictException"/> if any assembly simple
    /// name in <paramref name="assemblies"/> appears at more than one distinct
    /// <see cref="Version"/>.
    /// </summary>
    /// <param name="assemblies">
    /// The <see cref="AssemblyName"/> collection to scan.
    /// </param>
    /// <exception cref="AssemblyVersionConflictException">
    /// Thrown when one or more conflicts are found.  The exception message names
    /// every conflicting assembly and its competing versions, and states the
    /// recommended corrective action.
    /// </exception>
    public static void ThrowIfConflicting(IEnumerable<AssemblyName> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var conflicts = FindConflicts(assemblies);
        if (conflicts.Count > 0)
            throw new AssemblyVersionConflictException(conflicts);
    }

    /// <summary>
    /// Convenience overload that accepts loaded <see cref="Assembly"/> instances.
    /// </summary>
    /// <param name="assemblies">
    /// The loaded assemblies to inspect.
    /// </param>
    /// <exception cref="AssemblyVersionConflictException">
    /// Thrown when one or more conflicts are found.
    /// </exception>
    public static void ThrowIfConflicting(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var conflicts = FindConflicts(assemblies);
        if (conflicts.Count > 0)
            throw new AssemblyVersionConflictException(conflicts);
    }
}
