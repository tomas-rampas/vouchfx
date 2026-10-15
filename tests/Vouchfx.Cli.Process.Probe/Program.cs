// Test-only Windows lifetime fixture. It never reads stdin or starts an interactive shell.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vouchfx.TestSupport;

if (args.Length is < 2 or > 3
    || !int.TryParse(args[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var lifetimeSeconds)
    || lifetimeSeconds is < 1 or > 60)
{
    throw new ArgumentException("Expected self|grandchild <pid-file> <1..60 seconds>, or hold <1..60 seconds>.");
}

if (args[0] == "hold" && args.Length == 2)
{
    Thread.Sleep(TimeSpan.FromSeconds(lifetimeSeconds));
    return;
}

if (args.Length != 3
    || string.IsNullOrWhiteSpace(args[1])
    || !string.Equals(Path.GetFileName(args[1]), args[1], StringComparison.Ordinal))
{
    throw new ArgumentException("The announcement must be a filename in the fixture's working directory.");
}

if (args[0] == "self")
{
    WritePid(args[1], Environment.ProcessId);
    Thread.Sleep(TimeSpan.FromSeconds(lifetimeSeconds));
    return;
}

if (args[0] != "grandchild")
{
    throw new ArgumentException("Unknown process fixture mode.");
}

var start = new ProcessStartInfo(Environment.ProcessPath
    ?? throw new InvalidOperationException("The fixture requires its own apphost."))
{
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = Environment.CurrentDirectory,
    // Deliberately inherit stdout/stderr: the runner must bound reads after this parent exits.
    RedirectStandardOutput = false,
    RedirectStandardError = false,
};
start.ArgumentList.Add("hold");
start.ArgumentList.Add(lifetimeSeconds.ToString(CultureInfo.InvariantCulture));

using var grandchild = Process.Start(start)
    ?? throw new InvalidOperationException("Cannot start the bounded pipe-holding grandchild.");
var announced = false;
try
{
    WritePid(args[1], grandchild.Id);
    announced = true;
}
finally
{
    // A complete PID file transfers cleanup to the test's guarded teardown. On write failure
    // this process still owns the child handle and kills before disposing it. The child's
    // independent 60-second ceiling remains the backstop if termination is refused.
    if (!announced)
    {
        ChildProcess.KillTreeQuietly(grandchild);
    }
}

static void WritePid(string filename, int pid) =>
    File.WriteAllText(filename, pid.ToString(CultureInfo.InvariantCulture) + Environment.NewLine,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
