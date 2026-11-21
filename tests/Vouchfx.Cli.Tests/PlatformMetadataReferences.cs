using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Vouchfx.Cli.Tests;

/// <summary>Shares one metadata reference per trusted platform assembly across the test host.</summary>
/// <remarks>
/// <see cref="MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider?)"/>
/// copies the whole image into unmanaged memory that only a finalizer frees. The platform list is about
/// 490 files (about 150 MB), so a fresh set per test compile let several GB pile up between gen2 collections
/// and took the host to the runner's memory limit. Only files on the trusted-platform-assemblies list
/// are shared: one reference per file for the life of the process, because these are the files the runtime
/// itself loads for this process (the shared framework and the host's deps.json assemblies); replacing one under
/// a running process is unsupported, and whoever can write them already controls the process. Every other path, such as an assembly a test emits into its own directory, gets a
/// fresh reference, because a test may rewrite that file at the same path. The platform rule is the same
/// as the product's <c>RoslynScriptCompiler.GetReference</c>, duplicated here because test projects must not
/// reach into product internals for it. The product also caches other paths by file content; this helper
/// deliberately does not: kept simple, as test fixtures are tiny and short-lived.
/// </remarks>
internal static class PlatformMetadataReferences
{
    private static readonly HashSet<string> PlatformPaths = new(
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator),
        StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, MetadataReference> Cache = new(StringComparer.Ordinal);

    /// <summary>Returns the shared reference for a platform assembly, or a fresh one for any other path.</summary>
    /// <param name="path">Absolute assembly path.</param>
    /// <returns>A metadata reference to <paramref name="path"/>.</returns>
    internal static MetadataReference Get(string path) =>
        PlatformPaths.Contains(path)
            ? Cache.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p))
            : MetadataReference.CreateFromFile(path);
}
