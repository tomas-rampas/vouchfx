namespace Vouchfx.Cli.Watch;

/// <summary>Observes neutral bound input paths, including files outside the scenario directory.</summary>
internal sealed class WatchInputFiles : IDisposable
{
    private readonly Action _changed;
    private readonly Dictionary<string, FileSystemWatcher> _watchers;
    private HashSet<string> _files;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal WatchInputFiles(Action changed)
    {
        _changed = changed;
        _watchers = new Dictionary<string, FileSystemWatcher>(PathComparer);
        _files = new HashSet<string>(PathComparer);
    }

    internal void Refresh(IEnumerable<string> files)
    {
        var targets = new HashSet<string>(files.Select(Path.GetFullPath), PathComparer);
        Volatile.Write(ref _files, targets);
        var directories = targets.Select(ExistingParentDirectory).OfType<string>().ToHashSet(PathComparer);
        foreach (var obsolete in _watchers.Keys.Where(path => !directories.Contains(path)).ToArray())
        {
            _watchers[obsolete].Dispose();
            _watchers.Remove(obsolete);
        }
        foreach (var directory in directories.Where(path => !_watchers.ContainsKey(path)))
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += OnEvent;
            watcher.Error += (_, _) => _changed();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(directory, watcher);
        }
    }

    private void OnEvent(object? sender, FileSystemEventArgs args)
    {
        var files = Volatile.Read(ref _files);
        if (Observes(files, args.FullPath) || (args is RenamedEventArgs renamed && Observes(files, renamed.OldFullPath)))
            _changed();
    }

    private static string? ExistingParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        while (directory is not null && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
        return directory;
    }

    private static bool Observes(HashSet<string> files, string path)
    {
        if (files.Contains(path)) return true;
        // A missing input can be repaired by creating/renaming its parent. Observe only its
        // nearest existing ancestor, without recursive watchers or sibling-prefix matches.
        var prefix = Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return files.Any(file => file.StartsWith(prefix, comparison));
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
    }
}
