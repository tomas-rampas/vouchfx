using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vouchfx.Cli.Providers;

internal sealed record InstalledProvider(string Id, string Version, string Source, string Directory,
    IReadOnlyDictionary<string, string>? PackageSha256 = null, IReadOnlyDictionary<string, string>? FilesSha256 = null);

internal sealed class ProviderStore
{
    private static readonly JsonSerializerOptions s_manifestOptions = new() { WriteIndented = true };
    private const string ManifestName = "provider-install.json";
    private static readonly Regex s_stepKind = new("\\A[a-z0-9-]+\\.[a-z0-9-]+\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex s_id = new("\\A[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal string Root { get; }
    private string Transactions => Path.Combine(Path.GetDirectoryName(Root)!, "." + Path.GetFileName(Root) + ".transactions");
    private string LockPath => Path.Combine(Path.GetDirectoryName(Root)!, "." + Path.GetFileName(Root) + ".lock");
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vouchfx", "providers");

    internal static IDisposable AcquireExistingReadLocks(IReadOnlyList<string> directories)
    {
        var handles = new ReadLocks();
        try
        {
            var roots = directories.Select(Path.GetFullPath).SelectMany(root =>
                new[] { root, Path.GetDirectoryName(root) }).Where(root => root is not null);
            foreach (var root in roots.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            {
                var parent = Path.GetDirectoryName(root!);
                if (parent is null) continue;
                var path = Path.Combine(parent, "." + Path.GetFileName(root) + ".lock");
                // Never create state merely to load an unmanaged/read-only directory.
                if (!File.Exists(path)) continue;
                RefuseLinks(path);
                handles.Files.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            return handles;
        }
        catch (IOException exception)
        {
            handles.Dispose();
            throw new ProviderLoadException("Provider installation is being updated. Retry after the provider operation completes.", exception);
        }
        catch { handles.Dispose(); throw; }
    }

    private sealed class ReadLocks : IDisposable
    {
        internal List<FileStream> Files { get; } = new();
        public void Dispose() { foreach (var file in Files) file.Dispose(); }
    }

    internal ProviderStore(string? root = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? DefaultRoot));
        if (Path.GetDirectoryName(Root) is null || Path.GetFileName(Root).Length == 0)
            throw new ProviderLoadException("Provider installation root must be a named directory, not a volume root.");
        RefuseLinks(Root);
    }

    internal static void ValidateId(string id)
    {
        if (!s_id.IsMatch(id) || id.EndsWith('.') || IsReservedName(id.Split('.')[0]))
            throw new ProviderLoadException("Provider ID must be a portable NuGet package ID (1-100 ASCII letters, digits, '.', '_' or '-').");
    }
    internal static bool IsReservedName(string name) => name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
        or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";

    // Check each existing ancestor and descend manually: never enumerate through a link first.
    internal static void RefuseLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null ||
                ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
                throw new ProviderLoadException("Provider operations refuse symlinks and filesystem reparse points.");
        }
    }

    private static void RefuseTreeLinks(string directory)
    {
        RefuseLinks(directory);
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var parent))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ProviderLoadException("Provider operations refuse symlinks and filesystem reparse points.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private FileStream AcquireLock()
    {
        RefuseLinks(Root); RefuseLinks(Transactions); RefuseLinks(LockPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        var handle = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            RefuseLinks(Root); RefuseLinks(Transactions); RefuseLinks(LockPath);
            Directory.CreateDirectory(Root); Directory.CreateDirectory(Transactions);
            RecoverTransactions();
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal IReadOnlyList<InstalledProvider> List()
    {
        if (!Directory.Exists(Root)) return Array.Empty<InstalledProvider>();
        using var readLocks = AcquireExistingReadLocks(new[] { Root });
        RefuseTreeLinks(Root);
        return Directory.GetDirectories(Root).Order(StringComparer.Ordinal).Select(directory =>
        {
            var manifest = Path.Combine(directory, ManifestName);
            if (!File.Exists(manifest)) return new InstalledProvider(Path.GetFileName(directory), "unmanaged", "directory", directory);
            if (new FileInfo(manifest).Length > 1024 * 1024) throw new ProviderLoadException("Installed provider manifest exceeds the 1 MiB limit.");
            var record = JsonSerializer.Deserialize<InstalledProvider>(File.ReadAllText(manifest))
                ?? throw new ProviderLoadException("Installed provider manifest is invalid.");
            return record with { Directory = directory };
        }).ToArray();
    }

    internal async Task<InstalledProvider> InstallAsync(string id, string? version, string source, CancellationToken token, Action? beforePromotion = null,
        Func<CancellationToken, Task<IReadOnlyList<ProviderRegistryEntry>>>? registry = null)
    {
        ValidateId(id);
        using var handle = AcquireLock();
        var target = Path.Combine(Root, id.ToLowerInvariant());
        var transaction = Path.Combine(Transactions, Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(transaction, "stage"); var backup = Path.Combine(transaction, "backup");
        Directory.CreateDirectory(stage);
        var promoted = false;
        try
        {
            using var packages = new ProviderPackages(source, transaction);
            IReadOnlyList<ProviderRegistryEntry>? entries = null;
            if (registry is not null && s_stepKind.IsMatch(id))
            {
                entries = await registry(token);
                if (entries.Any(entry => entry.StepKindId == id))
                    id = ProviderRegistry.Resolve(id, entries);
            }
            if (!await packages.ExistsAsync(id, token) && registry is not null)
            {
                entries ??= await registry(token);
                id = ProviderRegistry.Resolve(id, entries);
            }
            target = Path.Combine(Root, id.ToLowerInvariant());
            var graph = await packages.ResolveAsync(id, version, token);
            foreach (var package in graph)
            {
                await ProviderArchive.ExtractAsync(package, stage, token);
                var stagedFiles = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Take(4097).ToArray();
                if (stagedFiles.Length > 4096 || stagedFiles.Sum(path => new FileInfo(path).Length) > 512L * 1024 * 1024)
                    throw new ProviderLoadException("Installed provider closure exceeds the 4096-file / 512 MiB limit.");
            }
            var others = Directory.GetDirectories(Root).Where(directory => !SamePath(directory, target)).ToArray();
            RefuseTreeLinks(Root);
            ProviderDirectoryScanner.PrepareInstallation(stage, others);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(stage))
            {
                await using var input = File.OpenRead(path);
                files[Path.GetFileName(path)] = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
            }
            var root = graph.Single(package => string.Equals(package.Identity.Id, id, StringComparison.OrdinalIgnoreCase));
            var installed = new InstalledProvider(root.Identity.Id, root.Identity.Version.ToNormalizedString(), packages.Source, target,
                graph.ToDictionary(package => package.Identity.ToString(), package => package.Sha256), files);
            var manifestJson = JsonSerializer.Serialize(installed, s_manifestOptions);
            if (System.Text.Encoding.UTF8.GetByteCount(manifestJson) > 1024 * 1024)
                throw new ProviderLoadException("Installed provider manifest exceeds the 1 MiB limit.");
            await File.WriteAllTextAsync(Path.Combine(stage, ManifestName), manifestJson, token);
            token.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(transaction, "transaction.tmp"), id.ToLowerInvariant(), token);
            File.Move(Path.Combine(transaction, "transaction.tmp"), Path.Combine(transaction, "transaction.id"));
            if (Directory.Exists(target)) { RefuseTreeLinks(target); Directory.Move(target, backup); }
            try
            {
                token.ThrowIfCancellationRequested();
                beforePromotion?.Invoke();
                token.ThrowIfCancellationRequested();
                Directory.Move(stage, target);
                promoted = true;
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
                throw;
            }
            return installed;
        }
        finally
        {
            // If restoration failed, preserve the backup and journal for recovery; never delete the last good copy.
            if (promoted || !Directory.Exists(backup)) CleanupTransaction(transaction);
        }
    }

    internal bool Uninstall(string id)
    {
        ValidateId(id);
        using var handle = AcquireLock();
        var target = Path.Combine(Root, id.ToLowerInvariant());
        if (!Directory.Exists(target)) return false;
        RefuseTreeLinks(target);
        var transaction = Path.Combine(Transactions, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        Directory.Move(target, Path.Combine(transaction, "removed"));
        CleanupTransaction(transaction);
        return true;
    }

    private void RecoverTransactions()
    {
        RefuseTreeLinks(Root);
        RefuseTreeLinks(Transactions);
        foreach (var transaction in Directory.GetDirectories(Transactions))
        {
            if (!Guid.TryParseExact(Path.GetFileName(transaction), "N", out _))
                continue;
            var journal = Path.Combine(transaction, "transaction.id");
            if (File.Exists(journal))
            {
                if (new FileInfo(journal).Length > 1024)
                    throw new ProviderLoadException("Provider transaction journal is invalid.");
                var id = File.ReadAllText(journal);
                ValidateId(id);
                var target = Path.Combine(Root, id.ToLowerInvariant());
                var backup = Path.Combine(transaction, "backup");
                if (Directory.Exists(backup))
                {
                    if (!Directory.Exists(target)) Directory.Move(backup, target);
                    else
                    {
                        var manifest = Path.Combine(target, ManifestName);
                        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 1024 ||
                            !string.Equals(JsonSerializer.Deserialize<InstalledProvider>(File.ReadAllText(manifest))?.Id,
                                id, StringComparison.OrdinalIgnoreCase))
                            throw new ProviderLoadException("An unexpected destination blocks provider recovery. The previous version remains quarantined in the transaction directory.");
                    }
                }
            }
            CleanupTransaction(transaction);
        }
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void CleanupTransaction(string directory)
    {
        // All recursive deletion targets are resolved, bounded children of our transaction root.
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(Transactions + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ProviderLoadException("Provider cleanup escaped the transaction directory.");
        try { RefuseTreeLinks(full); Directory.Delete(full, recursive: true); }
        catch (IOException) { /* A locked backup stays quarantined outside the provider scan path. */ }
        catch (UnauthorizedAccessException) { /* A locked backup stays quarantined outside the provider scan path. */ }
    }
}
