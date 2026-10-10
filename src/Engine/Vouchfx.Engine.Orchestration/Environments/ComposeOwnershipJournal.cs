using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vouchfx.Engine.Orchestration.Environments;

/// <summary>No rendered configuration, environment values or credentials are persisted.</summary>
internal sealed record ComposeOwnershipRecord(int Version, string Project, string DockerHost, int CleanupTimeoutSeconds);

internal static class ComposeOwnershipJournal
{
    internal static string DirectoryPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "vouchfx", "environments");

    internal static string Write(ComposeOwnershipRecord record, string? directory = null)
    {
        directory ??= DirectoryPath;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, record.Project + ".json");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, record);
        stream.Flush(flushToDisk: true);
        return path;
    }

    internal static ComposeOwnershipRecord Read(string path)
    {
        if (new FileInfo(path).Length > 4096) throw new ArgumentException("Ownership record exceeds its 4 KiB limit.");
        var record = JsonSerializer.Deserialize<ComposeOwnershipRecord>(File.ReadAllBytes(path))
            ?? throw new ArgumentException("Ownership record is empty.");
        if (record.Version != 1 || !Regex.IsMatch(record.Project, "^vouchfx-[a-f0-9]{32}$")
            || !ComposeLease.IsLocalDockerHost(record.DockerHost)
            || record.CleanupTimeoutSeconds is < 1 or > 120)
            throw new ArgumentException("Ownership record is unsupported or does not identify an owned local Compose project.");
        return record;
    }

    internal static void Complete(string path) => File.Delete(path);
}
