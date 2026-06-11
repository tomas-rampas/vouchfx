// Vouchfx.Cli — IProcessRunner (S07-C-02).
//
// A minimal seam over "run an external process and capture its result", introduced so that
// GitChangeSet's git shell-out is unit-testable WITHOUT a real git repository: tests inject
// a fake runner that returns canned `git diff` / `git status` output (or a non-zero exit /
// a launch failure) and assert how GitChangeSet parses and surfaces each case.

namespace Vouchfx.Cli.Selection;

/// <summary>
/// The outcome of running an external process: its exit code and captured streams.
/// </summary>
/// <param name="ExitCode">The process exit code (0 = success for git).</param>
/// <param name="StandardOutput">The full captured standard output.</param>
/// <param name="StandardError">The full captured standard error.</param>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs an external command and returns its captured result.
/// </summary>
/// <remarks>
/// The single production implementation is <see cref="SystemProcessRunner"/>; tests supply
/// a fake.  A runner that cannot launch the executable at all (e.g. git not installed)
/// throws <see cref="ProcessLaunchException"/> rather than returning a result.
/// </remarks>
internal interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> in
    /// <paramref name="workingDirectory"/> and captures its result.
    /// </summary>
    /// <param name="fileName">The executable to launch (e.g. <c>git</c>).</param>
    /// <param name="arguments">The argument vector (each element passed verbatim — no shell quoting).</param>
    /// <param name="workingDirectory">The working directory to launch the process in.</param>
    /// <returns>The captured <see cref="ProcessResult"/>.</returns>
    /// <exception cref="ProcessLaunchException">
    /// Thrown when the process cannot be started (executable not found, etc.).
    /// </exception>
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory);
}

/// <summary>
/// Thrown when an external process cannot be launched at all (distinct from a process that
/// launches and then exits non-zero, which surfaces as a <see cref="ProcessResult"/>).
/// </summary>
[System.Serializable]
internal sealed class ProcessLaunchException : Exception
{
    /// <summary>Initialises a new instance with a message.</summary>
    public ProcessLaunchException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and inner exception.</summary>
    public ProcessLaunchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
