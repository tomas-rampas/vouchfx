// Platform.Engine.Compilation — CollectibleScriptLoadContext (§5).
// The tiny emitted submission assembly lives here; everything else resolves
// against the Default context so Unload() can reclaim the emitted IL.
using System.Reflection;
using System.Runtime.Loader;

namespace Platform.Engine.Compilation;

/// <summary>
/// A collectible <see cref="AssemblyLoadContext"/> that hosts exactly one emitted
/// Roslyn submission assembly per script run.
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
///     <see cref="Load(AssemblyName)"/> returns <see langword="null"/> for every
///     request, forcing resolution to fall through to the Default context.  This means
///     only the tiny emitted submission assembly ever lives here; all dependencies
///     (including <c>Platform.Engine.Abstractions</c> and the Roslyn runtime assemblies)
///     are resolved in the Default context and are therefore never collected — which is
///     exactly what is needed for the compile-once / run-N-times model.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class CollectibleScriptLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Initialises a new collectible context.
    /// </summary>
    /// <param name="name">
    /// A human-readable label used in diagnostic output; typically includes a run
    /// counter or correlation identifier.
    /// </param>
    internal CollectibleScriptLoadContext(string name)
        : base(name, isCollectible: true)
    {
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Always returns <see langword="null"/> so that every assembly other than the one
    /// explicitly loaded via
    /// <see cref="AssemblyLoadContext.LoadFromStream(System.IO.Stream)"/> resolves
    /// against the Default context.
    /// </remarks>
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
