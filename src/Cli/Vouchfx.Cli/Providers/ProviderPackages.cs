using System.IO.Compression;
using System.Security.Cryptography;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Vouchfx.Cli.Providers;

internal sealed record ProviderPackage(PackageIdentity Identity, string Archive, string Sha256,
    IReadOnlyList<PackageDependency> Dependencies, bool Compatible);

internal sealed class ProviderPackages : IDisposable
{
    internal const string DefaultSource = "https://api.nuget.org/v3/index.json";
    internal static readonly NuGetFramework TargetFramework = NuGetFramework.ParseFolder("net8.0");
    private readonly SourceRepository _repository;
    private readonly SourceCacheContext _cache = new() { NoCache = true, DirectDownload = true };
    private readonly Dictionary<string, ProviderPackage> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NuGetVersion[]> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _workDirectory;
    private long _downloadedBytes;
    private int _attempts;

    internal ProviderPackages(string source, string workDirectory)
    {
        Source = ValidateSource(source);
        _workDirectory = workDirectory;
        var configured = new PackageSourceProvider(Settings.LoadDefaultSettings(null)).LoadPackageSources()
            .FirstOrDefault(item => string.Equals(item.Source, Source, StringComparison.Ordinal));
        var providers = Repository.Provider.GetCoreV3().Prepend(new Lazy<INuGetResourceProvider>(() => new ProviderHttpResource()));
        var packageSource = new PackageSource(Source) { Credentials = configured?.Credentials };
        _repository = new SourceRepository(packageSource, providers);
    }

    internal string Source { get; }
    internal static string ValidateSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ProviderLoadException("Provider sources must be HTTPS endpoints without embedded credentials, query strings or fragments, or explicit local directories.");
            return uri.AbsoluteUri;
        }
        var path = Path.GetFullPath(uri?.IsFile == true ? uri.LocalPath : source);
        if (uri?.IsUnc == true || path.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(path))
            throw new ProviderLoadException("Local provider source must be an existing local directory.");
        ProviderStore.RefuseLinks(path);
        return path;
    }

    internal async Task<IReadOnlyList<ProviderPackage>> ResolveAsync(string id, string? version, CancellationToken token)
    {
        ProviderStore.ValidateId(id);
        var available = await VersionsAsync(id, token);
        var chosen = version is null ? await LatestStableAsync(id, available, token)
            : NuGetVersion.TryParse(version, out var parsed) ? parsed : throw new ProviderLoadException("--version must be an exact NuGet version.");
        if (chosen is null || !available.Contains(chosen))
            throw new ProviderLoadException($"Package '{id}' has no matching version in the configured source.");
        var constraints = new Dictionary<string, List<VersionRange>>(StringComparer.OrdinalIgnoreCase)
        { [id] = new() { new VersionRange(chosen, true, chosen, true) } };
        var resolved = await SearchAsync(constraints, new(StringComparer.OrdinalIgnoreCase), token);
        return resolved?.Values.OrderBy(package => package.Identity.Id, StringComparer.OrdinalIgnoreCase).ToArray()
            ?? throw new ProviderLoadException("Provider dependency graph has incompatible version ranges for .NET 8.");
    }

    private async Task<NuGetVersion?> LatestStableAsync(string id, NuGetVersion[] available, CancellationToken token)
    {
        if (!Uri.TryCreate(Source, UriKind.Absolute, out var uri) || uri.IsFile)
            return available.Where(version => !version.IsPrerelease).Max();
        var metadata = await _repository.GetResourceAsync<PackageMetadataResource>(token)
            ?? throw new ProviderLoadException("Configured feed needs package metadata support, or an explicit --version.");
        var listed = await metadata.GetMetadataAsync(id, includePrerelease: false, includeUnlisted: false, _cache, NullLogger.Instance, token);
        return listed.Select(item => item.Identity.Version).Where(version => available.Contains(version)).Max();
    }

    private async Task<Dictionary<string, ProviderPackage>?> SearchAsync(Dictionary<string, List<VersionRange>> constraints,
        Dictionary<string, ProviderPackage> selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (++_attempts > 1024 || constraints.Count > 128)
            throw new ProviderLoadException("Provider dependency graph exceeds the 128-package / 1024-resolution-attempt limit.");
        if (selected.Any(item => constraints[item.Key].Any(range => !range.Satisfies(item.Value.Identity.Version))))
            return null;
        var pending = constraints.Keys.FirstOrDefault(id => !selected.ContainsKey(id));
        if (pending is null)
            return selected;
        var allowPrerelease = constraints[pending].Any(range => range.MinVersion?.IsPrerelease == true);
        foreach (var version in (await VersionsAsync(pending, token)).Where(version =>
                     (allowPrerelease || !version.IsPrerelease) && constraints[pending].All(range => range.Satisfies(version))).Order())
        {
            var package = await DownloadAsync(pending, version, token);
            if (!package.Compatible) continue;
            var next = constraints.ToDictionary(item => item.Key, item => item.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in package.Dependencies)
            {
                ProviderStore.ValidateId(dependency.Id);
                if (!next.TryGetValue(dependency.Id, out var ranges)) next[dependency.Id] = ranges = new();
                ranges.Add(dependency.VersionRange);
            }
            var result = await SearchAsync(next, new(selected, StringComparer.OrdinalIgnoreCase) { [pending] = package }, token);
            if (result is not null) return result;
        }
        return null;
    }

    internal async Task<bool> ExistsAsync(string id, CancellationToken token) => (await VersionsAsync(id, token)).Length > 0;

    private async Task<NuGetVersion[]> VersionsAsync(string id, CancellationToken token)
    {
        if (!_versions.TryGetValue(id, out var versions))
        {
            var resource = await _repository.GetResourceAsync<FindPackageByIdResource>(token)
                ?? throw new ProviderLoadException("Configured source does not support NuGet package downloads.");
            versions = (await resource.GetAllVersionsAsync(id, _cache, NullLogger.Instance, token)).Take(10001).ToArray();
            if (versions.Length > 10000) throw new ProviderLoadException("Package version listing exceeds the 10000-version limit.");
            _versions.Add(id, versions);
        }
        return versions;
    }

    private async Task<ProviderPackage> DownloadAsync(string id, NuGetVersion version, CancellationToken token)
    {
        var key = id + "/" + version.ToNormalizedString();
        if (_downloads.TryGetValue(key, out var downloaded)) return downloaded;
        var path = Path.Combine(_workDirectory, Guid.NewGuid().ToString("N") + ".nupkg");
        var resource = await _repository.GetResourceAsync<FindPackageByIdResource>(token)
                ?? throw new ProviderLoadException("Configured source does not support NuGet package downloads.");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var bounded = new BoundedStream(file, ProviderHttpResource.MaximumDownloadBytes, leaveOpen: true))
        {
            if (!await resource.CopyNupkgToStreamAsync(id, version, bounded, _cache, NullLogger.Instance, token))
                throw new ProviderLoadException($"Package '{id}' {version} was not found in the configured source.");
            _downloadedBytes += file.Length;
            if (_downloadedBytes > 512L * 1024 * 1024)
                throw new ProviderLoadException("Provider dependency downloads exceed the 512 MiB transaction limit.");
        }
        ProviderArchive.Validate(path);
        using var package = new PackageArchiveReader(path);
        var identity = package.GetIdentity();
        if (!StringComparer.OrdinalIgnoreCase.Equals(id, identity.Id) || identity.Version != version)
            throw new ProviderLoadException("Downloaded package identity does not match the requested ID and version.");
        var groups = package.GetPackageDependencies().ToArray();
        var nearest = new FrameworkReducer().GetNearest(TargetFramework, groups.Select(group => group.TargetFramework));
        var dependencies = groups.FirstOrDefault(group => group.TargetFramework.Equals(nearest))?.Packages.ToArray() ?? Array.Empty<PackageDependency>();
        using var input = File.OpenRead(path);
        var libraries = package.GetLibItems().ToArray();
        var compatible = libraries.Length == 0
            ? groups.Length == 0 || nearest is not null
            : new FrameworkReducer().GetNearest(TargetFramework, libraries.Select(group => group.TargetFramework)) is not null;
        downloaded = new ProviderPackage(identity, path, Convert.ToHexString(SHA256.HashData(input)), dependencies, compatible);
        _downloads.Add(key, downloaded);
        return downloaded;
    }

    public void Dispose() => _cache.Dispose();
}

internal static class ProviderArchive
{
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;
    internal static void Validate(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 4096) throw new ProviderLoadException("Provider archive exceeds the 4096-entry limit.");
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            var segments = name.Split('/');
            if (name.Length == 0 || entry.FullName.Contains('\\') || Path.IsPathRooted(name)
                || segments.Any(segment => !SafeSegment(segment)) || !names.Add(name))
                throw new ProviderLoadException("Provider archive contains an unsafe or duplicate path.");
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new ProviderLoadException("Provider archive symlinks and reparse points are refused.");
            expanded = checked(expanded + entry.Length);
            if (entry.Length > ProviderHttpResource.MaximumDownloadBytes || expanded > MaximumExpandedBytes)
                throw new ProviderLoadException("Provider archive exceeds the 64 MiB entry / 256 MiB expanded-size limit.");
        }
    }

    private static bool SafeSegment(string value) => value.Length > 0 && value is not "." and not ".."
        && value.TrimEnd(' ', '.') == value && !value.Any(character => character < 32 || ":<>|?*".Contains(character))
        && !ProviderStore.IsReservedName(value.Split('.')[0]);

    internal static async Task ExtractAsync(ProviderPackage package, string directory, CancellationToken token)
    {
        using var reader = new PackageArchiveReader(package.Archive);
        using var zip = ZipFile.OpenRead(package.Archive);
        var groups = reader.GetLibItems().ToArray();
        var nearest = new FrameworkReducer().GetNearest(ProviderPackages.TargetFramework, groups.Select(group => group.TargetFramework));
        var selected = groups.FirstOrDefault(group => group.TargetFramework.Equals(nearest))?.Items
            .Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray() ?? Array.Empty<string>();
        if (groups.Length > 0 && nearest is null)
            throw new ProviderLoadException($"Package '{package.Identity}' has no compatible .NET 8 runtime assets.");
        foreach (var name in selected)
        {
            token.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(name) ?? throw new ProviderLoadException("Provider package asset is missing from its archive.");
            var target = Path.Combine(directory, Path.GetFileName(name));
            await using var input = new BoundedStream(entry.Open(), ProviderHttpResource.MaximumDownloadBytes);
            if (File.Exists(target))
            {
                using var existing = File.OpenRead(target);
                if (!(await SHA256.HashDataAsync(input, token)).SequenceEqual(await SHA256.HashDataAsync(existing, token)))
                    throw new ProviderLoadException($"Provider packages contain conflicting asset '{Path.GetFileName(name)}'.");
            }
            else
            {
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, token);
            }
        }
        // Retain package identity and licence/notice text without putting metadata in the scan level.
        var metadata = Path.Combine(directory, "_metadata", package.Identity.Id.ToLowerInvariant());
        Directory.CreateDirectory(metadata);
        var declaredLicense = reader.NuspecReader.GetLicenseMetadata();
        var licenceFile = declaredLicense?.Type == LicenseType.File ? declaredLicense.License : null;
        foreach (var entry in zip.Entries.Where(entry => !entry.FullName.EndsWith('/') &&
                     (entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(entry.FullName).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(entry.FullName).StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(entry.FullName).StartsWith("COPYING", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(entry.FullName, licenceFile, StringComparison.OrdinalIgnoreCase))))
        {
            var name = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.FullName)))[..12] + "-" + Path.GetFileName(entry.FullName);
            await using var input = new BoundedStream(entry.Open(), ProviderHttpResource.MaximumDownloadBytes);
            await using var output = new FileStream(Path.Combine(metadata, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, token);
        }
    }
}
