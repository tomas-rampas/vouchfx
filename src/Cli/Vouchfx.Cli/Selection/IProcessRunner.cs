// Vouchfx.Cli — IProcessRunner (S07-C-02; bounding and the timeout exception, #481).
//
// A minimal seam over "run an external process and capture its result", introduced so that
// GitChangeSet's git shell-out is unit-testable WITHOUT a real git repository: tests inject
// a fake runner that returns canned `git diff` / `git status` output (or a non-zero exit /
// a launch failure) and assert how GitChangeSet parses and surfaces each case.
//
// TWO INDEPENDENT BOUNDS: THE RUNNER'S BUDGET AND THE CALLER'S TOKEN.
// ──────────────────────────────────────────────────────────────────
// The budget is what stops a wedged child from holding the CLI when nobody is watching, and
// exceeding it is reported as ProcessTimeoutException. The TOKEN is what lets somebody who IS
// watching stop sooner: `vouchfx run --changed-since` passes the run's cancellation token down
// through RunCommand.SelectScenarios and GitChangeSet to here, so a Ctrl+C reaches an
// implementation's cleanup and gets the child tree-killed. Without it the token was signalled and
// nothing on this path observed it, leaving System.CommandLine's ProcessTerminationTimeout
// watchdog (Program.cs) to force-kill the CLI with the runner's `finally` never run — the orphan
// #481 exists to prevent, on the path where an operator is most likely to intervene. (Inferred
// from that watchdog's documented contract and from Program.cs setting it for a non-watch `run`;
// not measured here.)
//
// Cancellation surfaces AS CANCELLATION: an implementation throws OperationCanceledException, and
// GitChangeSet.RunGit deliberately does not map it to ChangeSetException — a cancelled run is not
// a usage error. Which handler in RunCommand.ExecuteAsync then receives it, and how that handler
// words the result, is that file's business and is deliberately not restated here — describing
// another file's control flow is how this header would rot. Issue #502 tracks one such wording.
//
// This interface is `internal` to Vouchfx.Cli with one production implementation and test
// fakes. It is NOT part of the frozen v1 SDK surface (blueprint §13.8) and no golden pins it,
// so adding the two exception types below — and, for #500, the `environment` parameter — moves no
// contract. That parameter is OPTIONAL, and deliberately so: its default reproduces the inheriting
// behaviour every caller had before it existed, so confinement is something a caller opts into
// rather than something this seam imposes on one it has never been told about.

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
    /// <param name="fileName">
    /// The executable to launch, as a FULLY QUALIFIED path. This seam resolves nothing, and an
    /// unqualified name is resolved by the operating system's own search — which on Windows reaches
    /// the calling executable's directory and the calling process's current directory ahead of
    /// <c>PATH</c>, so a bare <c>git</c> here is the hole #499 closed. Every caller resolves first,
    /// and <see cref="SystemProcessRunner"/> ENFORCES it with an <see cref="ArgumentException"/>
    /// rather than trusting the sentence above: this requirement arrived with #499 as a
    /// doc-comment, and the defect #499 closed was a caller handing over the bare name <c>git</c>
    /// — precisely what a doc-comment cannot catch. The test is
    /// <see cref="Path.IsPathFullyQualified(string)"/>, matching <c>GitChangeSet.LocateOnPath</c>,
    /// so the resolver cannot produce a value this seam refuses.
    /// </param>
    /// <param name="arguments">The argument vector (each element passed verbatim — no shell quoting).</param>
    /// <param name="workingDirectory">The working directory to launch the process in.</param>
    /// <param name="environment">
    /// The child's WHOLE environment block, or <see langword="null"/> to let it inherit this
    /// process's environment in full.
    /// <para>
    /// <strong>A replacement, never an overlay.</strong> When non-null the child sees these
    /// variables and no others — a caller that wants a variable it did not name must add it. That
    /// is the point (#500): a <c>git</c> child executes repository-influenced code through
    /// <c>core.fsmonitor</c>, <c>.git/hooks/*</c> and <c>credential.helper</c> in its <c>!shell</c>
    /// form, so inheriting in full hands whatever <c>${secret:env/NAME}</c> reads to a helper the
    /// repository under test chose. WHICH variables belong in the set is the CALLER's knowledge,
    /// not this seam's — see <c>GitChangeSet.ConfineEnvironment</c> for git's.
    /// </para>
    /// <para>
    /// <strong><see langword="null"/> is the default so that confinement is opted INTO.</strong>
    /// An implementation-side default of "confine to nothing" would silently break any caller that
    /// had not thought about it, and a wrong environment fails in ways that look like the child
    /// misbehaving rather than like a missing variable.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the call; an implementation must still reclaim its child before it throws.
    /// </param>
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
    /// <strong>A timed-out read is ABANDONED, not awaited.</strong> Cancelling a pending
    /// anonymous-pipe read does not reliably end it: a <see cref="System.Diagnostics.Process"/>
    /// capture stream is a <see cref="FileStream"/> opened <c>isAsync: false</c>, so
    /// <c>ReadToEndAsync</c> blocks a thread-pool thread and a token can be observed only BETWEEN
    /// reads, never during one (inferred from that <see cref="FileStream"/> construction; not
    /// measured). #392 measured the adjacent fact: reads issued with no token at all were still
    /// <c>WaitingForActivation</c> in a single sample four seconds after the child had exited. So
    /// an implementation that waited for a cancelled read to acknowledge would simply move the
    /// hang. The contract is therefore that on the timeout path a tree-kill is issued for the direct
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
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="fileName"/> is not fully qualified. A caller error, not a child
    /// failure, so it is deliberately outside the three exception types above and outside
    /// <c>GitChangeSet.RunGit</c>'s catches: an unqualified name reaching here is a broken caller,
    /// and mapping it to exit 2 would present it as the user's problem.
    /// </exception>
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
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is signalled, after the same tree-kill the
    /// timeout path issues. Distinct from every exception above BECAUSE it is not a failure of the
    /// child: it is the caller withdrawing, and the CLI's cancellation path — not its usage-error
    /// path — is what receives it.
    /// </exception>
    ProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default);
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
