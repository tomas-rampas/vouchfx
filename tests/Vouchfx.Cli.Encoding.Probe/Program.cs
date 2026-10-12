// Child-only fixture: a private hidden console keeps cp852 changes out of the test host.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vouchfx.TestSupport;

var request = JsonSerializer.Deserialize<ProbeRequest>(await File.ReadAllTextAsync(args[0]))
    ?? throw new InvalidOperationException("Missing probe request.");
if (OperatingSystem.IsWindows())
{
    if (Native.GetConsoleOutputCP() == 0 || Native.GetConsoleCP() == 0)
        throw new InvalidOperationException("The probe requires its own hidden console.");
    if (!Native.SetConsoleOutputCP(852) || !Native.SetConsoleCP(852))
        throw new InvalidOperationException("Cannot set the private console to cp852.");
}
var outputPageBefore = OperatingSystem.IsWindows() ? Native.GetConsoleOutputCP() : 0;
var inputPageBefore = OperatingSystem.IsWindows() ? Native.GetConsoleCP() : 0;
var start = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false,
    RedirectStandardInput = request.RedirectInput,
    RedirectStandardOutput = request.RedirectOutput,
    RedirectStandardError = request.RedirectError,
    CreateNoWindow = request.CreateNoWindow,
};
foreach (var argument in request.Arguments)
    start.ArgumentList.Add(argument);
using var consoleInput = OperatingSystem.IsWindows() ? Native.CreateFile("CONIN$", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero) : null;
using var consoleOutput = OperatingSystem.IsWindows() ? Native.CreateFile("CONOUT$", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero) : null;
var originalInput = OperatingSystem.IsWindows() ? Native.GetStdHandle(-10) : IntPtr.Zero;
var originalOutput = OperatingSystem.IsWindows() ? Native.GetStdHandle(-11) : IntPtr.Zero;
var originalError = OperatingSystem.IsWindows() ? Native.GetStdHandle(-12) : IntPtr.Zero;
if (OperatingSystem.IsWindows())
{
    if (consoleInput!.IsInvalid || consoleOutput!.IsInvalid)
        throw new InvalidOperationException("Cannot open private console handles.");
    Native.SetStdHandle(-10, consoleInput.DangerousGetHandle());
    Native.SetStdHandle(-11, consoleOutput.DangerousGetHandle());
    Native.SetStdHandle(-12, consoleOutput.DangerousGetHandle());
    Native.SetHandleInformation(consoleInput.DangerousGetHandle(), 1, 1);
    Native.SetHandleInformation(consoleOutput.DangerousGetHandle(), 1, 1);
}
var started = DateTimeOffset.UtcNow;
using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch CLI.");
try
{
    if (OperatingSystem.IsWindows())
    {
        Native.SetStdHandle(-10, originalInput);
        Native.SetStdHandle(-11, originalOutput);
        Native.SetStdHandle(-12, originalError);
    }
    using var output = new MemoryStream();
    using var error = new MemoryStream();
    var stdout = request.RedirectOutput ? child.StandardOutput.BaseStream.CopyToAsync(output) : Task.CompletedTask;
    var stderr = request.RedirectError ? child.StandardError.BaseStream.CopyToAsync(error) : Task.CompletedTask;
    if (request.RedirectInput && request.StdinBase64 is not null)
    {
        await child.StandardInput.BaseStream.WriteAsync(Convert.FromBase64String(request.StdinBase64));
        await child.StandardInput.BaseStream.FlushAsync();
    }
    if (request.RedirectInput)
        child.StandardInput.Close();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await child.WaitForExitAsync(timeout.Token);
    await Task.WhenAll(stdout, stderr);
    var journalRoot = Environment.GetEnvironmentVariable("VOUCHFX_ENCODING_PROBE_JOURNAL");
    if (!string.IsNullOrEmpty(journalRoot))
    {
        Directory.CreateDirectory(journalRoot);
        var record = Path.Combine(journalRoot, Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(record + ".stdout", output.ToArray());
        await File.WriteAllBytesAsync(record + ".stderr", error.ToArray());
        await File.WriteAllBytesAsync(record + ".request.json", await File.ReadAllBytesAsync(args[0]));
        await File.WriteAllTextAsync(record + ".command.json", JsonSerializer.Serialize(new
        {
            Executable = start.FileName,
            Arguments = start.ArgumentList.ToArray(),
            Cwd = Directory.GetCurrentDirectory(),
            StartedUtc = started,
            FinishedUtc = DateTimeOffset.UtcNow,
            Pid = child.Id,
            ParentPid = Environment.ProcessId,
            child.ExitCode,
            request.CreateNoWindow,
            request.RedirectInput,
            request.RedirectOutput,
            request.RedirectError,
            Stdout = record + ".stdout",
            Stderr = record + ".stderr",
        }));
    }
    // Inherited CLI output shares stdout on POSIX; keep the result off those handles.
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
    {
        child.ExitCode,
        StdoutBase64 = Convert.ToBase64String(output.ToArray()),
        StderrBase64 = Convert.ToBase64String(error.ToArray()),
        OutputPageBefore = outputPageBefore,
        InputPageBefore = inputPageBefore,
        OutputPageAfter = OperatingSystem.IsWindows() ? Native.GetConsoleOutputCP() : 0,
        InputPageAfter = OperatingSystem.IsWindows() ? Native.GetConsoleCP() : 0,
    }));
}
finally
{
    ChildProcess.KillTreeQuietly(child);
}

internal sealed record ProbeRequest(string[] Arguments, string? StdinBase64, bool CreateNoWindow,
    bool RedirectInput = true, bool RedirectOutput = true, bool RedirectError = true);

internal static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern bool SetStdHandle(int handle, IntPtr value);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint GetConsoleCP();

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCP(uint codePage);
}
