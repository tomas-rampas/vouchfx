using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Vouchfx.Sdk;

namespace Vouchfx.Cli;

// Provider code is trusted and lives for the process lifetime, just like bundled providers.
internal static class ProviderDirectoryScanner
{
    private sealed record Image(string Path, AssemblyName Identity, Guid ModuleId, string[] Namespaces, AssemblyName[] References, bool HasProviderDeclaration);
    private static readonly object s_gate = new();
    private static readonly Dictionary<string, Image> s_admitted = new(StringComparer.OrdinalIgnoreCase);
    private static bool s_resolverRegistered;

    internal static Assembly[] Load(IReadOnlyList<string> directories)
    {
        if (directories.Count == 0)
            return Array.Empty<Assembly>();
        using var readLocks = Providers.ProviderStore.AcquireExistingReadLocks(directories);
        lock (s_gate)
        {
            var images = InspectDirectories(directories, considerAdmitted: true);
            foreach (var image in images.Values)
                s_admitted.TryAdd(image.Identity.Name!, image);
            if (!s_resolverRegistered)
            {
                AssemblyLoadContext.Default.Resolving += Resolve;
                s_resolverRegistered = true;
            }
            var assemblies = new List<Assembly>();
            foreach (var image in images.Values)
            {
                try
                {
                    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(image.Path);
                    // Unlike the SDK's general-purpose discovery, external loading must be all-or-nothing.
                    var types = assembly.GetTypes();
                    if (types.Any(type => !type.IsAbstract && type.IsClass
                        && ((typeof(IStepProvider).IsAssignableFrom(type)
                            && type.IsDefined(typeof(StepProviderAttribute), false))
                            || typeof(IProviderModule).IsAssignableFrom(type))))
                        assemblies.Add(assembly);
                }
                catch (Exception exception) when (exception is not ProviderLoadException)
                {
                    throw new ProviderLoadException($"Cannot load external assembly '{image.Path}': {exception.Message}", exception);
                }
            }
            return assemblies.ToArray();
        }
    }

    // Install-time validation deliberately inspects metadata without executing or loading provider code.
    internal static void PrepareInstallation(string directory, IReadOnlyList<string> otherDirectories)
    {
        lock (s_gate)
        {
            var canonical = CanonicalImages();
            foreach (var path in Directory.GetFiles(directory))
            {
                var image = ReadImage(path);
                if (image is null || !canonical.TryGetValue(image.Identity.Name!, out var host))
                    continue;
                if (!CompatibleHost(image.Identity, host))
                    throw Conflict(image, host);
                File.Delete(path);
            }
            var images = InspectDirectories(otherDirectories.Append(directory).ToArray(), considerAdmitted: false);
            if (!images.Values.Any(image => Path.GetDirectoryName(image.Path) == directory && image.HasProviderDeclaration))
                throw new ProviderLoadException("Package has no discoverable SDK v1 step provider or provider module.");
        }
    }

    // Snapshot host metadata once per resolution; candidate checks never load provider code.
    internal static Func<string, bool> CreateHostCompatibilityCheck()
    {
        Dictionary<string, Image> canonical;
        lock (s_gate) canonical = CanonicalImages();
        return directory =>
        {
            foreach (var path in Directory.GetFiles(directory))
            {
                var image = ReadImage(path);
                if (image is null) continue;
                if (canonical.TryGetValue(image.Identity.Name!, out var host))
                {
                    if (!CompatibleHost(image.Identity, host)) return false;
                    continue; // Admission prunes compatible host copies, including their references.
                }
                foreach (var reference in image.References)
                    if (canonical.TryGetValue(reference.Name!, out host) && !CompatibleHost(reference, host))
                        return false;
            }
            return true;
        };
    }

    private static Dictionary<string, Image> InspectDirectories(IReadOnlyList<string> directories, bool considerAdmitted)
    {
        var canonical = CanonicalImages();
        var images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                throw new ProviderLoadException($"Provider directory does not exist: '{directory}'.");
            foreach (var folder in new[] { directory }.Concat(Directory.GetDirectories(directory).Order(StringComparer.Ordinal)))
                foreach (var path in Directory.GetFiles(folder).Order(StringComparer.Ordinal))
                {
                    var image = ReadImage(path);
                    if (image is null)
                        continue;
                    var name = image.Identity.Name!;
                    if (canonical.TryGetValue(name, out var host))
                    {
                        if (!CompatibleHost(image.Identity, host))
                            throw Conflict(image, host);
                        continue; // Never load provider-local copies of host-owned assemblies.
                    }
                    if (name.Equals("vouchfx", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Vouchfx.Engine.", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Vouchfx.Steps.", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Vouchfx.Sdk", StringComparison.OrdinalIgnoreCase))
                        throw new ProviderLoadException($"External assembly '{path}' impersonates a reserved host assembly '{name}'.");
                    var reserved = image.Namespaces.FirstOrDefault(IsReservedNamespace);
                    if (reserved is not null)
                        throw new ProviderLoadException($"External assembly '{path}' declares reserved namespace '{reserved}' (Vouchfx.Engine.* / Vouchfx.Steps.*).");
                    if (images.TryGetValue(name, out var other) && !SameImage(image, other))
                        throw Conflict(image, other);
                    if (considerAdmitted && s_admitted.TryGetValue(name, out var resident) && !SameImage(image, resident))
                        throw Conflict(image, resident);
                    images.TryAdd(name, image);
                }
        }
        foreach (var image in images.Values)
            foreach (var reference in image.References)
            {
                var name = reference.Name!;
                if (name == "Vouchfx.Sdk" && reference.Version?.Major != 1)
                    throw new ProviderLoadException($"External assembly '{image.Path}' references unsupported SDK {reference.Version}; use the Vouchfx.Sdk v1 contract.");
                if (canonical.TryGetValue(name, out var host))
                {
                    if (!CompatibleHost(reference, host))
                        throw new ProviderLoadException($"External assembly '{image.Path}' requires '{reference}', conflicting with host '{host.Identity}' at '{host.Path}'.");
                }
                else if (images.TryGetValue(name, out var dependency) || (considerAdmitted && s_admitted.TryGetValue(name, out dependency)))
                {
                    if (!SameIdentity(reference, dependency.Identity))
                        throw new ProviderLoadException($"External assembly '{image.Path}' requires '{reference}', conflicting with '{dependency.Identity}' at '{dependency.Path}'.");
                }
                else
                    throw new ProviderLoadException($"External assembly '{image.Path}' has missing dependency '{reference}'. Supply it beside the provider DLL.");
            }
        return images;
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        lock (s_gate)
            return name.Name is not null && s_admitted.TryGetValue(name.Name, out var image)
                && SameIdentity(name, image.Identity) ? context.LoadFromAssemblyPath(image.Path) : null;
    }

    private static Dictionary<string, Image> CanonicalImages()
    {
        var paths = ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(AssemblyLoadContext.Default.Assemblies.Where(assembly => !assembly.IsDynamic)
                .Select(assembly => assembly.Location).Where(path => path.Length > 0));
        var result = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct())
        {
            var image = ReadImage(path);
            if (image is not null && !s_admitted.ContainsKey(image.Identity.Name!))
                result.TryAdd(image.Identity.Name!, image);
        }
        return result;
    }

    private static Image? ReadImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 2 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                return null;
            stream.Position = 0;
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return null;
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly)
                return null;
            var identity = AssemblyName.GetAssemblyName(path);
            var namespaces = metadata.TypeDefinitions.Select(handle => metadata.GetTypeDefinition(handle))
                .Select(type => metadata.GetString(type.Namespace)).Distinct().ToArray();
            var references = metadata.AssemblyReferences.Select(handle =>
            {
                var reference = metadata.GetAssemblyReference(handle);
                var name = new AssemblyName
                {
                    Name = metadata.GetString(reference.Name),
                    Version = reference.Version,
                    CultureName = reference.Culture.IsNil ? null : metadata.GetString(reference.Culture)
                };
                var key = metadata.GetBlobBytes(reference.PublicKeyOrToken);
                if ((reference.Flags & AssemblyFlags.PublicKey) != 0)
                    name.SetPublicKey(key);
                else
                    name.SetPublicKeyToken(key);
                return name;
            }).ToArray();
            return new Image(Path.GetFullPath(path), identity,
                metadata.GetGuid(metadata.GetModuleDefinition().Mvid), namespaces, references,
                metadata.TypeDefinitions.Any(handle => IsProviderDeclaration(metadata, metadata.GetTypeDefinition(handle))));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            throw new ProviderLoadException($"Cannot inspect external assembly '{path}': {exception.Message}", exception);
        }
    }

    private static bool IsProviderDeclaration(MetadataReader metadata, TypeDefinition type)
    {
        bool IsSdkType(EntityHandle handle, string expected)
        {
            if (handle.Kind != HandleKind.TypeReference)
                return false;
            var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
            return metadata.GetString(reference.Namespace) == "Vouchfx.Sdk" && metadata.GetString(reference.Name) == expected
                && reference.ResolutionScope.Kind == HandleKind.AssemblyReference
                && metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name) == "Vouchfx.Sdk";
        }
        return type.GetInterfaceImplementations().Any(handle => IsSdkType(metadata.GetInterfaceImplementation(handle).Interface, "IProviderModule"))
            || type.GetCustomAttributes().Any(handle =>
            {
                var constructor = metadata.GetCustomAttribute(handle).Constructor;
                return constructor.Kind == HandleKind.MemberReference &&
                    IsSdkType(metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent, "StepProviderAttribute");
            });
    }

    private static bool IsReservedNamespace(string ns) => ns is "Vouchfx.Engine" or "Vouchfx.Steps"
        || ns.StartsWith("Vouchfx.Engine.", StringComparison.Ordinal) || ns.StartsWith("Vouchfx.Steps.", StringComparison.Ordinal);
    // Assemblies the host resolves from a shared framework listed in its runtimeconfig (Microsoft.NETCore.App and, for the
    // CLI, Microsoft.AspNetCore.App) are runtime-owned: a provider built against an older framework package may reference
    // a lower version, never a higher one. In the framework-dependent tool (dotnet tool install) the assemblies the CLI
    // ships itself sit in the app directory, are app-local and keep needing an exact identity. In the self-contained
    // archives and installers the runtime directory is the app directory, so every bundled assembly follows the same-or-lower
    // rule, as it did before shared frameworks were recognised. A provider admitted at a lower version that calls an API
    // removed since fails when the step runs, not at suite start; a higher version is always refused at suite start.
    private static bool CompatibleHost(AssemblyName requested, Image host) =>
        requested.Name is "Vouchfx.Sdk" or "Vouchfx.Engine.Abstractions"
            ? requested.Version?.Major == 1 && host.Identity.Version?.Major == 1 && SameToken(requested, host.Identity)
            : IsSharedFrameworkImage(host)
                ? requested.Version <= host.Identity.Version && SameToken(requested, host.Identity)
                : SameIdentity(requested, host.Identity);
    private static bool IsSharedFrameworkImage(Image host) =>
        Path.GetDirectoryName(host.Path) is { } directory && s_frameworkDirectories.Value.Contains(NormalizeDirectory(directory));
    private static string NormalizeDirectory(string directory) =>
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    // The host publishes the deps.json of every file it resolved from (APP_CONTEXT_DEPS_FILES: the app's own, then one per
    // shared framework in the runtimeconfig; FX_DEPS_FILE: the root framework). Each framework's deps.json sits in that
    // framework's directory. In the framework-dependent tool the app's own directory is app-local and is not added from the
    // deps files; in a self-contained build GetRuntimeDirectory() is the app directory and FX_DEPS_FILE is empty, so the
    // app directory is added through the runtime directory below and its assemblies are treated as framework assemblies.
    private static readonly Lazy<HashSet<string>> s_frameworkDirectories = new(() =>
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var appDirectory = NormalizeDirectory(AppContext.BaseDirectory);
        var directories = new HashSet<string>(comparer)
        {
            NormalizeDirectory(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())
        };
        foreach (var key in new[] { "APP_CONTEXT_DEPS_FILES", "FX_DEPS_FILE" })
            foreach (var depsFile in ((AppContext.GetData(key) as string) ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Path.GetDirectoryName(depsFile) is { Length: > 0 } directory && NormalizeDirectory(directory) is var normalized
                    && !comparer.Equals(normalized, appDirectory))
                    directories.Add(normalized);
        return directories;
    });
    private static bool SameIdentity(AssemblyName left, AssemblyName right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) && left.Version == right.Version
        && string.Equals(left.CultureName ?? "", right.CultureName ?? "", StringComparison.OrdinalIgnoreCase) && SameToken(left, right);
    private static bool SameToken(AssemblyName left, AssemblyName right) =>
        (left.GetPublicKeyToken() ?? Array.Empty<byte>()).SequenceEqual(right.GetPublicKeyToken() ?? Array.Empty<byte>());
    private static bool SameImage(Image left, Image right) => SameIdentity(left.Identity, right.Identity) && left.ModuleId == right.ModuleId;
    private static ProviderLoadException Conflict(Image left, Image right) => new(
        $"Provider assembly conflict: '{left.Identity}' at '{left.Path}' conflicts with '{right.Identity}' at '{right.Path}'. Remove duplicate or incompatible dependencies.");
}

internal sealed class ProviderLoadException : Exception
{
    internal ProviderLoadException(string message, Exception? inner = null) : base(message, inner) { }
}
