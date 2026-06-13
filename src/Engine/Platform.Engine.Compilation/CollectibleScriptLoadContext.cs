// Platform.Engine.Compilation — CollectibleScriptLoadContext (§5).
// The tiny emitted submission assembly lives here; everything else resolves
// against the Default context so Unload() can reclaim the emitted IL.
using System.Reflection;
using System.Runtime.Loader;

namespace Platform.Engine.Compilation;

/// <summary>
/// A collectible <see cref="AssemblyLoadContext"/> that hosts exactly one emitted
/// Roslyn submission assembly per script run, and optionally any number of
/// per-run/customer satellite assemblies identified by probing paths.
/// </summary>
/// <remarks>
/// <para>
/// Design rationale (§5 memory model):
/// <list type="bullet">
///   <item><description>
///     The context is created with <c>isCollectible: true</c> so the runtime can
///     reclaim the emitted IL once <see cref="AssemblyLoadContext.Unload"/> is called
///     and all references to assemblies loaded into this context are released.
///   </description></item>
///   <item><description>
///     Long-lived shared dependencies (engine assemblies, canonical client libraries,
///     any assembly already resident in <see cref="AssemblyLoadContext.Default"/>)
///     resolve in the Default context and are never duplicated here.
///     This is the <em>pin-avoidance rule</em>: loading a Default assembly into a
///     collectible context creates a type-identity duplicate and risks a static field
///     in the Default copy rooting a per-run object, silently preventing Unload from
///     reclaiming memory.
///   </description></item>
///   <item><description>
///     Per-run or customer satellite assemblies that the script depends on at runtime
///     are supplied via the <c>collectibleProbingPaths</c> constructor argument.  For each path
///     whose simple assembly name is <em>not</em> already present in the Default
///     context, the context records a <c>simpleName → absolutePath</c> mapping and
///     loads the assembly into itself on demand, so it unloads with the context.
///   </description></item>
///   <item><description>
///     <see cref="Load(AssemblyName)"/> returns <see langword="null"/> for any name
///     not in the probing map, forcing resolution to fall through to the Default
///     context.  Only the emitted submission assembly and the opted-in satellite
///     assemblies ever live here; all shared dependencies remain in Default and are
///     never collected — which is exactly what the compile-once / run-N-times model
///     requires.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class CollectibleScriptLoadContext : AssemblyLoadContext
{
    // Map from simple assembly name (lower-cased) → absolute path on disk.
    // This is an INSTANCE field — never static.  A static field would bridge the
    // collectible boundary and prevent Unload from reclaiming the context.
    private readonly Dictionary<string, string> _probingMap;

    /// <summary>
    /// Initialises a new collectible context with no satellite probing paths.
    /// All assembly resolution falls through to the Default context.
    /// </summary>
    /// <param name="name">
    /// A human-readable label used in diagnostic output; typically includes a run
    /// counter or correlation identifier.
    /// </param>
    internal CollectibleScriptLoadContext(string name)
        : this(name, null)
    {
    }

    /// <summary>
    /// Initialises a new collectible context that can resolve the specified
    /// satellite assemblies into itself, whilst delegating everything else to the
    /// Default context.
    /// </summary>
    /// <param name="name">
    /// A human-readable label used in diagnostic output; typically includes a run
    /// counter or correlation identifier.
    /// </param>
    /// <param name="collectibleProbingPaths">
    /// Optional list of absolute paths to satellite assemblies that should be loaded
    /// into THIS collectible context rather than the Default context.  Each path is
    /// inspected with <see cref="AssemblyName.GetAssemblyName(string)"/> to extract
    /// its simple name without loading the assembly.  Any path whose simple name is
    /// already present in <see cref="AssemblyLoadContext.Default"/> is silently
    /// excluded from the probing map — the pin-avoidance rule.  Pass
    /// <see langword="null"/> or an empty list when no satellite probing is required.
    /// </param>
    internal CollectibleScriptLoadContext(string name, IReadOnlyList<string>? collectibleProbingPaths)
        : base(name, isCollectible: true)
    {
        _probingMap = BuildProbingMap(collectibleProbingPaths);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// If <paramref name="assemblyName"/>'s simple name is present in the probing map
    /// (i.e. it was supplied via <c>collectibleProbingPaths</c> and was NOT already
    /// resident in <see cref="AssemblyLoadContext.Default"/>), the assembly is loaded
    /// from the recorded path into this collectible context via
    /// <see cref="AssemblyLoadContext.LoadFromAssemblyPath"/>.  It will therefore
    /// unload with this context when <see cref="AssemblyLoadContext.Unload"/> is called.
    /// </para>
    /// <para>
    /// For every other name, <see langword="null"/> is returned, causing the runtime to
    /// fall through to the Default context.  This applies to all shared engine/provider
    /// assemblies and to any satellite path that was excluded by the pin-avoidance rule.
    /// </para>
    /// </remarks>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null &&
            _probingMap.TryGetValue(assemblyName.Name, out var path))
        {
            return LoadFromAssemblyPath(path);
        }

        // Return null → fall through to the Default context.
        return null;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds the <c>simpleName → absolutePath</c> probing map from
    /// <paramref name="paths"/>, applying the pin-avoidance rule: any path whose
    /// assembly simple name is already resident in
    /// <see cref="AssemblyLoadContext.Default"/> is excluded from the map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pin-avoidance rule (§5 memory model):</b> loading a Default-resident assembly
    /// into a collectible context creates a duplicate type identity.  If the Default
    /// copy holds a static field that references a per-run object (e.g. a delegate,
    /// a cached type reference), that static field roots the per-run object across
    /// the collectible boundary and silently prevents Unload from reclaiming memory.
    /// Excluding Default-resident names from the map ensures the runtime always
    /// resolves them in Default, producing a single identity with no collectible copy.
    /// </para>
    /// <para>
    /// Simple-name comparison uses <see cref="StringComparer.OrdinalIgnoreCase"/> for
    /// cross-platform safety (file-system case sensitivity varies by OS).
    /// </para>
    /// </remarks>
    private static Dictionary<string, string> BuildProbingMap(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Collect the simple names already loaded in the Default context so we can
        // apply the pin-avoidance rule without loading the candidate assemblies.
        var defaultNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in AssemblyLoadContext.Default.Assemblies)
        {
            var n = a.GetName().Name;
            if (n is not null)
                defaultNames.Add(n);
        }

        var map = new Dictionary<string, string>(paths.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path))
                continue;

            string? simpleName;
            try
            {
                // GetAssemblyName reads the PE headers without loading the assembly,
                // which is essential here: we must not load into any context just to
                // inspect the name.
                simpleName = AssemblyName.GetAssemblyName(path).Name;
            }
            catch (Exception)
            {
                // If the path is invalid or the file is not a valid assembly, skip it.
                // This keeps the ctor non-throwing for caller convenience; the caller
                // will discover the problem when the assembly fails to resolve at script
                // run time and the step receives an Environment-error verdict.
                continue;
            }

            if (simpleName is null)
                continue;

            // Pin-avoidance rule: skip any name already present in the Default context.
            if (defaultNames.Contains(simpleName))
                continue;

            // Last-write wins for duplicate simple names in the paths list (consistent
            // with how the Default probing order works).
            map[simpleName] = path;
        }

        return map;
    }
}
