// Vouchfx.Cli — IProcessRunner (S07-C-02; bounding and the timeout exception, #481).
//
// A minimal seam over "run an external process and capture its result", introduced so that
// GitChangeSet's git shell-out is unit-testable WITHOUT a real git repository: tests inject
// a fake runner that returns canned `git diff` / `git status` output (or a non-zero exit /
// a launch failure) and assert how GitChangeSet parses and surfaces each case.
//
// THE SEAM CARRIES NO CancellationToken, AND THAT IS A DECISION RATHER THAN AN OMISSION.
// ─────────────────────────────────────────────────────────────────────────────────────
// The only call site is RunCommand.SelectScenarios, which is synchronous and has no token in
// scope; threading one in would ripple through ScenarioSelector.Apply for a path whose natural
// bound is a constant. So the DOCUMENTED CEILING is the whole mechanism here: the runner owns a
// budget, and exceeding it is reported as ProcessTimeoutException rather than negotiated with a
// caller that has nothing to say about it.
//
// This interface is `internal` to Vouchfx.Cli with one production implementation and test
// fakes. It is NOT part of the frozen v1 SDK surface (blueprint §13.8) and no golden pins it,
// so adding the exception type below moves no contract.

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
/// throws <see cref="ProcessLaunchException"/> rather than returning a result; one whose child
/// outlives the implementation's time budget throws <see cref="ProcessTimeoutException"/>; one
/// whose output capture fails part-way throws <see cref="ProcessCaptureException"/>.
/// </remarks>
internal interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> in
    /// <paramref name="workingDirectory"/> and captures its result, within a bounded budget.
    /// </summary>
    /// <param name="fileName">The executable to launch (e.g. <c>git</c>).</param>
    /// <param name="arguments">The argument vector (each element passed verbatim — no shell quoting).</param>
    /// <param name="workingDirectory">The working directory to launch the process in.</param>
    /// <returns>The captured <see cref="ProcessResult"/>.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The call is bounded, and the bound covers the READS.</strong> An implementation
    /// backed by a real process must budget the time spent draining stdout/stderr, not merely the
    /// time spent waiting for the child to exit: a child that exits promptly while leaving a
    /// grandchild holding the inherited pipe handles never delivers end-of-file, so the pending
    /// READ, not the pending exit, is what wedges the caller (issue #392). The budget belongs to
    /// the implementation — see <see cref="SystemProcessRunner"/> for the production ceiling.
    /// </para>
    /// <para>
    /// <strong>A timed-out read is ABANDONED, not awaited.</strong> Cancellation of a pending
    /// anonymous-pipe read is not reliably honoured (#392 measured the read task sitting at
    /// <c>WaitingForActivation</c> indefinitely after its token was signalled), so an
    /// implementation that waited for the cancelled read to acknowledge would simply move the hang.
    /// The contract is therefore that on the timeout path a tree-kill is issued for the direct
    /// child and whatever the reads had produced is discarded — <see cref="ProcessTimeoutException"/>
    /// carries no partial output, because a partial capture is exactly the input that makes a
    /// change-set silently wrong rather than loudly absent.
    /// </para>
    /// <para>
    /// <strong>The kill reaches the tree the implementation still owns, which need not be the
    /// whole tree.</strong> In the #392 shape the direct child has already exited by the time the
    /// budget expires, so the tree-kill reaches nothing and the grandchild holding the pipes keeps
    /// running. No portable mechanism reclaims a process whose parent is gone and for which no
    /// handle was retained, so the contract stops at the direct child rather than promising a
    /// termination it cannot perform.
    /// </para>
    /// </remarks>
    /// <exception cref="ProcessLaunchException">
    /// Thrown when the process cannot be started (executable not found, etc.).
    /// </exception>
    /// <exception cref="ProcessTimeoutException">
    /// Thrown when the child does not deliver both streams and exit within the implementation's
    /// budget. A tree-kill is issued for the direct child before this is thrown.
    /// </exception>
    /// <exception cref="ProcessCaptureException">
    /// Thrown when the child launches but reading one of its output streams fails.
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

/// <summary>
/// Thrown when an external process launches successfully but does not complete within the
/// runner's time budget.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A sibling of <see cref="ProcessLaunchException"/>, deliberately not a subclass of it.</strong>
/// A timeout is not a launch failure: the executable was found, the child ran, and the operator's
/// remedy is different (a wedged git, a filesystem that is not answering, a child holding pipes
/// open) from "git is not installed". Reusing the launch exception would have collapsed the two
/// into one diagnostic and told every user of a hung repository to check their PATH.
/// </para>
/// <para>
/// A tree-kill has already been issued for the direct child by the time this is thrown, and the
/// caller is handed no handle with which to do anything further. That kill reaches nothing when
/// the direct child had itself already exited — the #392 shape — so a process it orphaned can
/// still be running; see <see cref="IProcessRunner.Run"/> for why that limit is structural.
/// </para>
/// </remarks>
[System.Serializable]
internal sealed class ProcessTimeoutException : Exception
{
    /// <summary>Initialises a new instance with a message and the budget that was exceeded.</summary>
    /// <param name="message">The user-facing description of what timed out.</param>
    /// <param name="budget">The ceiling the run exceeded.</param>
    public ProcessTimeoutException(string message, TimeSpan budget)
        : base(message)
    {
        Budget = budget;
    }

    /// <summary>The time budget the run exceeded.</summary>
    /// <remarks>
    /// Exposed as a property rather than left only in the message so a mapping caller can name the
    /// ceiling in ITS OWN vocabulary without parsing prose — which is what
    /// <see cref="GitChangeSet"/> does when it converts this into a usage error.
    /// </remarks>
    public TimeSpan Budget { get; }
}

/// <summary>
/// Thrown when an external process launches successfully but reading its output fails.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A third sibling, added by #481 because the alternative was an unhandled crash.</strong>
/// A stream read can fault — a broken pipe, a handle closed underneath it — and a faulted read is
/// neither a launch failure nor a timeout. Before this type existed such a fault escaped
/// <see cref="IProcessRunner.Run"/> as a raw <see cref="IOException"/>, which
/// <see cref="GitChangeSet"/> does not catch, so it reached the top of the CLI as a stack trace.
/// </para>
/// <para>
/// <strong>Not folded into <see cref="ProcessLaunchException"/>, for the same reason the timeout
/// was not.</strong> That exception's mapped message asks the operator whether git is installed
/// and on PATH, which is exactly the wrong question for a process that started and then failed
/// mid-capture.
/// </para>
/// <para>
/// Like the timeout, this carries no partial output: whatever the successful stream produced is
/// discarded rather than handed on as a capture that is quietly incomplete.
/// </para>
/// </remarks>
[System.Serializable]
internal sealed class ProcessCaptureException : Exception
{
    /// <summary>Initialises a new instance with a message and the underlying read failure.</summary>
    public ProcessCaptureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
