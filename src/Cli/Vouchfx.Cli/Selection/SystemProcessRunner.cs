// Vouchfx.Cli — SystemProcessRunner (S07-C-02).
//
// The production IProcessRunner: launches a real process via System.Diagnostics.Process,
// captures stdout/stderr fully, and surfaces a launch failure (e.g. git not on PATH) as a
// ProcessLaunchException so GitChangeSet can map it to a clear usage error rather than a
// crash. Arguments are passed through Process.StartInfo.ArgumentList (no shell, no manual
// quoting — each element is escaped by the runtime).

using System.Diagnostics;

namespace Vouchfx.Cli.Selection;

/// <summary>
/// The default <see cref="IProcessRunner"/> backed by <see cref="Process"/>.
/// </summary>
internal sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>A shared, stateless instance.</summary>
    public static SystemProcessRunner Instance { get; } = new();

    /// <inheritdoc />
    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new ProcessLaunchException(
                    $"Could not start '{fileName}': the process handle was null.");
        }
        catch (Exception ex) when (ex is not ProcessLaunchException)
        {
            // Win32Exception (executable not found), InvalidOperationException, etc.
            throw new ProcessLaunchException(
                $"Could not start '{fileName}': {ex.Message}", ex);
        }

        // Read both streams to completion before WaitForExit to avoid a pipe-buffer deadlock.
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}
