// Vouchfx.Cli — SystemProcessRunner (S07-C-02; lifetime and bounding, #481).
//
// The production IProcessRunner: launches a real process via System.Diagnostics.Process,
// captures stdout/stderr fully, and surfaces a launch failure (e.g. git not on PATH) as a
// ProcessLaunchException so GitChangeSet can map it to a clear usage error rather than a
// crash. Arguments are passed through Process.StartInfo.ArgumentList (no shell, no manual
// quoting — each element is escaped by the runtime).
//
// WHAT #481 CHANGED, AND WHY NONE OF IT IS DECORATIVE
// ───────────────────────────────────────────────────
// Three defects lived here. The third is the one with teeth.
//
//   1. The Process was never disposed on any path — a handle leak per call.
//   2. The launch sat outside any `try` that owned cleanup, so a throw out of the capture
//      abandoned a live child with nothing left holding a reference to it.
//   3. `StandardOutput.ReadToEnd()` was the FIRST blocking call and therefore the real hang
//      site, NOT `WaitForExit()`. Issue #392 measured the shape: a child that exits promptly
//      while leaving a GRANDCHILD holding the inherited pipe handles never delivers
//      end-of-file, because EOF arrives only when EVERY writer has closed the pipe. Bounding
//      `WaitForExit` alone therefore fixes nothing on that shape — the direct child is
//      already gone within a second and a bounded wait would be satisfied immediately, while
//      the read that runs first still never returns. The budget has to cover the READS.
//
// ABANDON, DO NOT AWAIT. `ReadToEndAsync(CancellationToken)` cancellation is not reliably
// honoured on a pending anonymous-pipe read — #392 measured the read task sitting at
// `WaitingForActivation` indefinitely after the token was signalled. So the timeout path races
// the reads against a delay and WALKS AWAY from the losers; it never waits for them to
// acknowledge anything, because waiting for an unresponsive read is the hang this file exists
// to remove. Abandoning is CHEAPER THAN HANGING, NOT FREE: `Process`'s captured streams are
// `FileStream`s opened `isAsync: false`, so `ReadToEndAsync` runs as a blocking read on a
// thread-pool thread, and that thread stays occupied until the pipe delivers end-of-file. The
// abandoned read therefore outlives this method by however long the writer that is still
// holding the pipe survives.
//
// STDIN IS REDIRECTED AND CLOSED IMMEDIATELY, and what that buys is narrower than it looks. It
// does NOT stop git prompting for credentials: git's terminal prompt reads /dev/tty on POSIX and
// CONIN$ on Windows, deliberately bypassing a redirected stdin, and `CreateNoWindow = true`
// gives the child a console with no window rather than no console at all. (Inferred from git's
// documented terminal-prompt behaviour; not measured here.) What redirection DOES buy: anything
// that reads the INHERITED stdin — a `credential.helper` in `!shell` form, say — gets an
// immediate end-of-file and therefore fails closed, and the child can no longer consume the
// CLI's own stdin. The bound on a genuinely interactive prompt is the time budget below, not the
// redirection. (Deliberately NOT accompanied by GIT_TERMINAL_PROMPT=0: `Run` has no environment
// seam, and a git-specific variable does not belong in a generic runner.)

using System.Diagnostics;
using System.Globalization;

namespace Vouchfx.Cli.Selection;

/// <summary>
/// The default <see cref="IProcessRunner"/> backed by <see cref="Process"/>, bounded by a
/// per-call time budget.
/// </summary>
internal sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>The production ceiling on a single <see cref="Run"/>.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Sized for the slowest legitimate call this runner makes, with headroom.</strong>
    /// The only production caller is <see cref="GitChangeSet"/>, which issues
    /// <c>rev-parse --show-toplevel</c>, <c>diff --name-only</c> and <c>status --porcelain</c>.
    /// The last of those is the slow one: a full <c>status</c> walk is bounded by the working
    /// tree's file count rather than by the change-set's size, so a cold page cache, an absent
    /// filesystem monitor, a network mount or an on-access virus scanner each cost it a large
    /// constant factor.
    /// </para>
    /// <para>
    /// No figure here is measured, and this comment does not pretend otherwise — a ceiling that
    /// waited for a benchmark of somebody else's monorepo would never have been set. Two minutes
    /// is an ESTIMATE with a stated shape: it is intended to sit an order of magnitude above the
    /// slowest legitimate call, and it should never be reached by a repository that is merely
    /// large. Should a real one reach it, the fix is to raise this constant, not to remove the
    /// bound.
    /// </para>
    /// <para>
    /// It is a CEILING, not a latency target: the only thing it decides is how long a genuinely
    /// wedged child is allowed to hold the CLI before the run is failed and a tree-kill issued
    /// against that child. Two minutes is short enough that an operator waits it out rather than
    /// reaching for Ctrl+C, which is the behaviour that leaves the orphan behind.
    /// </para>
    /// <para>
    /// Tests inject a short budget through the internal constructor instead of inheriting this
    /// one — coupling a test's wall-clock to the production ceiling would make the suite slow in
    /// order to prove nothing extra.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The smallest wait the exit race is ever given, however much of the budget the reads spent.
    /// </summary>
    /// <remarks>
    /// A floor rather than an exemption: it applies only once BOTH streams have reached
    /// end-of-file, which requires every writer to have closed its copy of the pipe. The child is
    /// therefore very likely already gone and the only thing outstanding is the reap, so the
    /// worst this can add to <see cref="DefaultBudget"/> is one second against a child that has
    /// closed its pipes and then genuinely refuses to exit. Without it, a run whose reads consumed
    /// the whole budget is reported as "it had not exited" without <c>WaitForExitAsync</c> ever
    /// having been given a chance to answer — a fabricated timeout, and exit 2 for a successful
    /// run.
    /// </remarks>
    private static readonly TimeSpan MinimumExitWait = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _budget;

    /// <summary>
    /// Initialises a runner with an explicit budget.
    /// </summary>
    /// <param name="budget">The ceiling on a single <see cref="Run"/>; must be positive.</param>
    /// <remarks>
    /// <see langword="internal"/> rather than public because the seam exists for the tests: this
    /// whole type is internal to Vouchfx.Cli and nothing outside it can construct a runner at all.
    /// </remarks>
    internal SystemProcessRunner(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budget),
                budget,
                "The process budget must be positive; a non-positive ceiling would time out every call before it started.");
        }

        _budget = budget;
    }

    /// <summary>A shared, stateless instance carrying <see cref="DefaultBudget"/>.</summary>
    public static SystemProcessRunner Instance { get; } = new(DefaultBudget);

    /// <inheritdoc />
    public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        // The seam is synchronous (see IProcessRunner's header) but the bounding is naturally
        // expressed as a race between tasks, so the core is async and this blocks on it.
        //
        // .GetAwaiter().GetResult() rather than .Result so the original exception propagates
        // instead of an AggregateException wrapper — row 4 of SystemProcessRunnerTests and
        // GitChangeSet.RunGit both catch ProcessLaunchException by exact type.
        //
        // NO DEADLOCK RISK, AND THE REASON IS THE ConfigureAwait(false) ON EVERY AWAIT INSIDE
        // RunCoreAsync — not the host. The classic sync-over-async deadlock needs a
        // SynchronizationContext that marshals a continuation back to the thread this call is
        // blocking; ConfigureAwait(false) means no continuation below captures a context at all,
        // whatever the caller installed. Stated that way round on purpose: "a console application
        // has no SynchronizationContext" is FALSE for the test host, where xunit v2 installs an
        // AsyncTestSyncContext around every test method and rows 3 and 4 call Run directly on the
        // xunit test thread. Drop one ConfigureAwait(false) below and this paragraph stops holding.
        return RunCoreAsync(fileName, arguments, workingDirectory).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Launches the child, drains both streams and waits for exit, all within
    /// <see cref="_budget"/>, issuing the guarded tree-kill and releasing both pipes on every
    /// exit path.
    /// </summary>
    private async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The launch sits ABOVE the try/finally on purpose: until Process.Start returns there is
        // nothing to kill and nothing to dispose, and a failed start must surface as a
        // ProcessLaunchException rather than be swallowed by cleanup for a child that never
        // existed.
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

        // `using` emits its Dispose in a finally that ENCLOSES the explicit one below, so the kill
        // always runs before the dispose and the dangerous order cannot be written here. That
        // matters because the dangerous order fails SILENTLY: dispose-then-kill makes HasExited
        // raise InvalidOperationException, the guard swallows it, and the child is left alive with
        // nothing thrown and nothing logged. Same shape, and same reasoning, as the test suite's
        // Vouchfx.TestSupport.ChildProcess.KillTreeQuietly.
        using (process)
        {
            // One source for both delays so the loser of a race can be cancelled rather than left
            // holding a timer that fires minutes after the CLI has gone.
            using var timers = new CancellationTokenSource();

            Task<string>? standardOutput = null;
            Task<string>? standardError = null;
            Exception? captureFault = null;
            string? exceeded = null;
            var exitCode = 0;

            try
            {
                CloseStandardInput(process);

                // Both streams are drained CONCURRENTLY and before the exit wait: a child that
                // fills one pipe's buffer while this runner reads only the other one deadlocks,
                // which is the older hazard the original sequential ReadToEnd pair was written
                // for and which this preserves.
                standardOutput = process.StandardOutput.ReadToEndAsync();
                standardError = process.StandardError.ReadToEndAsync();
                var reads = Task.WhenAll(standardOutput, standardError);

                // The WhenAll promise is a task in its own right and needs its OWN observer:
                // observing the two children below does not mark it observed, so if both of them
                // fault it carries an AggregateException nobody has looked at. Attached at
                // creation rather than on one branch because that covers every path out of here,
                // including the timeout path where `reads` settles long after this method returned.
                Observe(reads);

                var elapsed = Stopwatch.StartNew();

                // SETTLED, NOT SUCCEEDED — and the distinction is the bug this names. WhenAny
                // returns the first task to reach ANY terminal state, and WhenAll faults as soon
                // as either read faults, so winning this race says only that the reads STOPPED.
                var settled = await Task.WhenAny(reads, Task.Delay(_budget, timers.Token))
                    .ConfigureAwait(false) == reads;

                if (!settled)
                {
                    exceeded = "its output streams never reached end-of-file, which a grandchild "
                        + "holding the inherited pipe handles is enough to cause";
                }
                else if (!reads.IsCompletedSuccessfully)
                {
                    // A read that faulted (a broken pipe, say) used to resurface unwrapped at the
                    // `await standardOutput` below — as neither of the two exceptions
                    // GitChangeSet.RunGit maps, so it escaped as an unhandled crash. Captured
                    // here and re-raised as ProcessCaptureException AFTER the finally, so the
                    // child is dead before the throw exactly as on the timeout path.
                    captureFault = reads.Exception is { } aggregate
                        ? aggregate.InnerExceptions[0]
                        : new OperationCanceledException("The output capture was cancelled.");
                }
                else
                {
                    // Whatever the reads spent comes out of the same budget: the ceiling is on the
                    // CALL, not on each phase of it. THE FLOOR IS NOT OPTIONAL, though: both
                    // streams reaching end-of-file requires every writer — the child included — to
                    // have closed its copy, so by the time control arrives here the child has very
                    // likely already exited and only the reap is outstanding. A budget the reads
                    // happened to consume entirely would otherwise short-circuit this wait and
                    // report "it had not exited" without ever having asked, turning a successful
                    // run into a spurious timeout and exit 2.
                    var remaining = _budget - elapsed.Elapsed;
                    if (remaining < MinimumExitWait)
                    {
                        remaining = MinimumExitWait;
                    }

                    var exit = process.WaitForExitAsync();
                    var finished = await Task.WhenAny(exit, Task.Delay(remaining, timers.Token))
                        .ConfigureAwait(false) == exit;

                    if (finished)
                    {
                        exitCode = process.ExitCode;
                    }
                    else
                    {
                        // Says nothing about WHEN the wait ended, because the floor above means it
                        // is not exactly at the budget.
                        exceeded = "both of its output streams had closed but it had not exited";
                    }
                }
            }
            finally
            {
                // THE ORDER HERE IS LOAD-BEARING AND LOOKS ARBITRARY, SO: the tree-kill comes
                // FIRST because it is what closes the child's copies of the pipe handles. Only
                // once every writer is gone can a pending read reach end-of-file, so a sequence
                // that tried to settle the reads before killing would be waiting on the very thing
                // the kill releases. Killing first also means the timed-out child is dead BEFORE
                // this method throws, which is the leak #481 is about: `Run` hands the caller no
                // handle, so a child abandoned here can never be reclaimed by anybody.
                //
                // The kill runs on the SUCCESS path too. It is a no-op there (the child has
                // exited, so HasExited short-circuits) and it is what makes "every path issues the
                // kill" true by construction rather than by inspection.
                KillTreeQuietly(process);

                // Then THIS PROCESS'S OWN ends of the two pipes, which `using (process)` does not
                // release: Process.Close() closes the captured streams only while their read mode
                // is Undefined or AsyncMode, and reading the StandardOutput/StandardError property
                // (as above) puts them in SyncMode, where disposing them is documented as the
                // caller's job. Without this the StreamReader/FileStream pairs and their pipe
                // handles survive until finalisation.
                DisposeCapturedStreams(process);

                // Then the losing delay, so no timer outlives the call.
                timers.Cancel();
            }

            if (captureFault is not null)
            {
                AbandonRead(standardOutput);
                AbandonRead(standardError);

                throw new ProcessCaptureException(
                    $"Reading the output of '{fileName}' failed: {captureFault.Message}",
                    captureFault);
            }

            if (exceeded is not null)
            {
                // ONLY NOW do we give up on the reads. This does not wait for them; it attaches an
                // observer so that whatever they eventually reach does not become an unobserved
                // task exception, and then walks away.
                AbandonRead(standardOutput);
                AbandonRead(standardError);

                throw new ProcessTimeoutException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{fileName}' exceeded the {_budget.TotalSeconds:0.###}s process budget: {exceeded}. No output was captured. A tree-kill was issued for the direct child; if that child had already exited (the shape in which a surviving grandchild holds the pipes) the kill reached an empty tree, and that grandchild is still running beyond this runner's reach."),
                    _budget);
            }

            // Both reads completed inside the budget, so these awaits are already-completed
            // lookups rather than waits — the captured text is held by the tasks, so the stream
            // disposal in the finally above cannot take it away.
            return new ProcessResult(
                exitCode,
                await standardOutput!.ConfigureAwait(false),
                await standardError!.ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Hands the child an immediate end-of-file on stdin so a prompt cannot block it.
    /// </summary>
    /// <remarks>
    /// Guarded because a child that has already exited leaves a broken pipe behind, and an
    /// <see cref="IOException"/> escaping here would leave <c>Run</c> throwing something that is
    /// neither <see cref="ProcessLaunchException"/> nor <see cref="ProcessTimeoutException"/> —
    /// exactly the unhandled shape <see cref="GitChangeSet"/> would turn into a crash.
    /// </remarks>
    private static void CloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The child is already gone; it has its end-of-file the hard way.
        }
    }

    /// <summary>
    /// Observes an abandoned read's eventual outcome without waiting for it.
    /// </summary>
    /// <remarks>
    /// No claim is made here about WHEN, or whether, the read settles — that depends on a writer
    /// this runner may no longer be able to reach. The only guarantee is that if it faults, the
    /// fault is observed and so kept off <see cref="TaskScheduler.UnobservedTaskException"/>,
    /// where it would surface later as a finalizer-thread event attributed to nothing in
    /// particular.
    /// </remarks>
    private static void AbandonRead(Task? read) => Observe(read);

    /// <summary>
    /// Attaches an observer to <paramref name="task"/>'s eventual fault, and nothing else.
    /// </summary>
    private static void Observe(Task? task) =>
        task?.ContinueWith(static settled => _ = settled.Exception, TaskScheduler.Default);

    /// <summary>
    /// Disposes this process's ends of the captured output pipes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><c>using (process)</c> does not do this.</strong> <c>Process.Close()</c> closes the
    /// captured streams only while their read mode is <c>Undefined</c> or <c>AsyncMode</c>;
    /// reading the <see cref="Process.StandardOutput"/> / <see cref="Process.StandardError"/>
    /// property switches the mode to <c>SyncMode</c>, where the .NET 8 implementation deliberately
    /// leaves disposal to the caller because the caller holds a reference. Without this the
    /// <see cref="StreamReader"/>/<see cref="FileStream"/> pairs and their pipe handles survive
    /// until finalisation.
    /// </para>
    /// <para>
    /// <strong>What this does NOT do is settle an abandoned read.</strong> A read already blocked
    /// in the operating system holds a reference on the underlying <c>SafeFileHandle</c>, so the
    /// descriptor is not closed until that read returns — which happens when the last WRITER
    /// closes the pipe, not when this reader lets go. (Inferred from <c>SafeHandle</c> reference
    /// counting, not measured here.) The kill above is what releases the writer in the tree this
    /// runner can reach; an orphaned grandchild is beyond it either way.
    /// </para>
    /// <para>
    /// Guarded for the same reason <see cref="CloseStandardInput"/> is: this runs in a
    /// <c>finally</c>, where any throw replaces the real failure with a teardown one.
    /// </para>
    /// </remarks>
    private static void DisposeCapturedStreams(Process process)
    {
        DisposeQuietly(process, static owner => owner.StandardOutput);
        DisposeQuietly(process, static owner => owner.StandardError);

        static void DisposeQuietly(Process owner, Func<Process, StreamReader> select)
        {
            try
            {
                select(owner).Dispose();
            }
            catch (Exception ex)
                when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Already closed, never redirected, or no longer associated with a process.
            }
        }
    }

    /// <summary>
    /// Kills <paramref name="process"/> and everything beneath it, swallowing the races and
    /// refusals that a kill can legitimately lose.
    /// </summary>
    /// <param name="process">The child to terminate. Already-exited is not an error.</param>
    /// <remarks>
    /// <para>
    /// <strong>A deliberate second copy of <c>Vouchfx.TestSupport.ChildProcess.KillTreeQuietly</c>,
    /// not an oversight.</strong> That one lives in a test-support project which is
    /// <c>IsPackable=false</c> and referenced only by test assemblies; product code cannot take a
    /// dependency on it. The copy is therefore forced, and the risk it carries is exactly the one
    /// the original's header names: two copies of a guard whose whole value is that its catch
    /// filter is exhaustive is how the filter drifts, and the round-1 filters in #378 already
    /// missed <see cref="AggregateException"/> once. <c>ProcessKillGuardParityTests</c> pins the
    /// two filters identical by parsing both sources, so a divergence is a red test rather than a
    /// silent hole in one lane.
    /// </para>
    /// <para>
    /// <strong>The name is not free either.</strong> <c>ChildProcessKillCallSiteCensusTests</c>
    /// matches the literal identifier <c>KillTreeQuietly</c> when it decides whether a
    /// child-process launch sits in a member with a killing <c>finally</c>; a differently-named
    /// helper would leave the launch above reported as unguarded.
    /// </para>
    /// <para>
    /// <c>entireProcessTree: true</c> because the child is routinely itself a parent, and the
    /// grandchild is precisely what holds the pipes open in the #392 shape this runner now bounds.
    /// Killing only the direct child would leave that grandchild running with nothing left to
    /// reclaim it.
    /// </para>
    /// <para>
    /// The <c>HasExited</c> test cannot be made atomic with the kill, so the catch is the real
    /// guard rather than a courtesy — and this runs in a <c>finally</c>, where ANY throw would
    /// replace the real failure with a teardown one. The filter covers every exception
    /// <c>Kill(bool)</c> documents, read from the .NET 8 reference XML rather than assumed:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="System.ComponentModel.Win32Exception"/> — "could not be terminated -or- the
    ///     process is terminating". This, NOT <c>InvalidOperationException</c>, is the
    ///     ended-between-the-check-and-the-kill case on this runtime.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="AggregateException"/> — "not all processes in the descendant tree could be
    ///     terminated". Reachable only through <c>entireProcessTree: true</c>, which is how this
    ///     calls it, and a partial tree kill is not worth a thrown teardown.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="NotSupportedException"/> — a remote process.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="InvalidOperationException"/> — documented for .NET Framework and .NET Core
    ///     3.0 and earlier only, so it is NOT the exit race here. Kept because <c>HasExited</c>
    ///     itself raises it when no process is associated with the object.
    ///   </description></item>
    /// </list>
    /// </remarks>
    private static void KillTreeQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException
                                       or AggregateException)
        {
            // Terminating, partially killed, remote, or no longer associated — see the remarks.
        }
    }
}
