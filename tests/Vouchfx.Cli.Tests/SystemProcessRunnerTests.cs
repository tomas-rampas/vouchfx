// Vouchfx.Cli.Tests — SystemProcessRunner process lifetime and bounding (#481).
//
// SystemProcessRunner is the single production IProcessRunner. Three defects lived in it, and
// these rows are the cover that keeps each one closed:
//
//   1. The Process was never disposed on any path — a handle leak per call.
//   2. The launch sat outside any try that owned cleanup, so a throw out of ReadToEnd() or
//      WaitForExit() abandoned a live child with nothing holding it.
//   3. WaitForExit() was unbounded — and, more to the point, `StandardOutput.ReadToEnd()` is the
//      FIRST blocking call and therefore the real hang site. Issue #392 measured exactly that
//      shape: a child that exits promptly while leaving a grandchild holding the inherited pipe
//      handles leaves the reads pending indefinitely, so the pending read, not the pending exit,
//      is what wedges the caller.
//
// WHY THESE ROWS CANNOT SIMPLY CALL Run AND WAIT
// ──────────────────────────────────────────────
// This assembly is a blocking CI gate and neither it nor xunit imposes a per-test timeout. A row
// that called Run against a child which never releases the pipes would not FAIL, it would HANG the
// gate — a regression that wedges CI is worse than no cover at all. So every hang row launches Run
// on a background Task and asserts FIRST that the task COMPLETED inside a bounded wall-clock
// window, and only then inspects what it completed with. Ordering the two assertions that way is
// what keeps a re-broken runner a ten-second failure rather than an infinite one.
//
// The abandoned child is killed by the TEST, in a `finally`, on every path WHERE A PID WAS
// CAPTURED — including the path where a later assertion has already thrown. A failing run must not
// leak a live child onto the CI agent. The child announces its own pid into a scratch file
// precisely so the teardown has something to kill: Run hands back no handle, and there is no
// portable way to recover one after the fact. ChildProcess.KillTreeQuietly (Vouchfx.TestSupport) is
// the shared guarded tree-kill. The gap is the pid assertion itself: if the child never publishes a
// pid, teardown has nothing to kill and the only backstop is the child's own bounded
// ChildLifetimeSeconds, which is why that lifetime is finite rather than infinite.
//
// WHAT IS NOT COVERED HERE, STATED RATHER THAN IMPLIED
// ───────────────────────────────────────────────────
// A read that FAULTS mid-capture (issue #481's closing request) has no row in this file. Provoking
// a genuine fault on a pending anonymous-pipe read needs a handle these rows do not own: the pipe
// belongs to the Process the runner created and never hands out, and closing it from outside would
// mean either a production seam that exists only for a test or reaching into Process's private
// state. What IS covered is the half that turned the fault into a crash — the mapping: `RunGit`
// caught only ProcessLaunchException and ProcessTimeoutException, so a faulted read escaped as a
// raw IOException, and GitChangeSetTests.GitOutputCaptureFails_SurfacesChangeSetException_NotCrash
// pins the new ProcessCaptureException to a ChangeSetException. The runner's own conversion of the
// faulted read into that type is covered by inspection only.
//
// THE BUDGET IS INJECTED, NOT INHERITED
// ─────────────────────────────────────
// Rows 1 and 2 construct their own runner with TestBudget rather than using
// SystemProcessRunner.Instance, which carries the production ceiling (minutes). Coupling a test's
// wall-clock to the production ceiling would make this file slow in order to prove nothing extra,
// and it would force RunGraceWindow to track a constant chosen for a cold `git status` on a huge
// repository. Rows 3, 4 and 6 keep using Instance: they exercise the happy path, the
// launch-failure path and the argument guard, where the budget is never approached and the shared
// instance is the thing shipped. Row 5
// injects a budget for the OPPOSITE reason — one so long it cannot be reached, so that a call
// which ends is one the cancellation token ended.
//
// ROW 6 IS NOT ABOUT #481 AT ALL
// ──────────────────────────────
// It pins the seam's OTHER contract, the one #499 added and left as prose: fileName must be FULLY
// QUALIFIED, because an unqualified name is resolved by the operating system's own search, which on
// Windows reaches the calling executable's directory and the calling process's current directory
// ahead of PATH. #499's defect was a caller passing the bare name `git`, so the requirement is now
// enforced in SystemProcessRunner.Run and asserted here. Rooting the Windows child shapes below
// (powershell.exe, cmd.exe) is a consequence of that guard rather than bookkeeping: until it landed,
// these rows were reaching their own children through the very search the change forbids.
//
// PORTABILITY
// ───────────
// Every row runs on both operating systems; none is skipped anywhere. Row 6 asserts one extra
// spelling on Windows — the drive-relative `C:git`, which POSIX would refuse for an unrelated
// reason — but the row itself runs everywhere. CI is Linux-only today
// (#366) but the maintainer develops on Windows, so an OS-conditional skip would silently retire
// half the coverage on whichever host mattered. The OS branch follows the established pattern in
// Vouchfx.Engine.Orchestration.Tests/ChildProcessKillTreeTests.cs: OperatingSystem.IsWindows()
// selecting a Windows command against a /bin/sh one. PowerShell rather than cmd.exe carries the
// two lifetime shapes because cmd.exe cannot report a pid — neither its own nor a backgrounded
// grandchild's — and a shape whose child cannot be killed in teardown is exactly the leak these
// rows exist to avoid.
//
// No Docker, no trait: these rows belong to the fast `requires!=docker` lane.
using System.Diagnostics;
using System.Globalization;
using Vouchfx.Cli.Selection;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Lifetime and bounding cover for <see cref="SystemProcessRunner"/> (#481).
/// </summary>
public sealed class SystemProcessRunnerTests
{
    /// <summary>
    /// How long <see cref="SystemProcessRunner.Run"/> is given to return before the row fails.
    /// </summary>
    /// <remarks>
    /// The failure latency of a re-broken runner, and it must stay comfortably above
    /// <see cref="TestBudget"/>: the runner has to spend its whole budget, tree-kill the child and
    /// unwind inside this window. Better than three times the injected budget, which leaves room
    /// for a loaded agent without letting a genuine hang masquerade as slowness.
    /// </remarks>
    private static readonly TimeSpan RunGraceWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The budget rows 1 and 2 inject into the runner under test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Short on purpose: the property under test is "the budget is enforced and the tree is
    /// killed", which is indifferent to the number. The production ceiling
    /// (<see cref="SystemProcessRunner.DefaultBudget"/>) is sized for a cold <c>git status</c> on
    /// a very large repository and is not something a unit test should sit through.
    /// </para>
    /// <para>
    /// <strong>Its floor is not the runner, it is the CHILD.</strong> Both rows wait for the child
    /// to publish its pid before they judge anything, and a budget that expired first would kill
    /// the child before it ever wrote the file — turning a healthy runner into a row that fails on
    /// its own teardown machinery. The window that matters is process start to first write:
    /// measured at 190-220ms over five warm runs of the Windows shape (<c>powershell.exe
    /// -NoProfile -NonInteractive</c>) on the maintainer's host. Three seconds is roughly fifteen
    /// times that warm figure, which absorbs a cold interpreter start without making either row
    /// slow. The <c>/bin/sh</c> shape CI runs has NOT been measured, and no figure is claimed for
    /// it here — a number carried over from the Windows shape would inherit an authority it never
    /// earned in the lane that gates merges.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(3);

    /// <summary>How long the child is given to announce its pid before the row gives up.</summary>
    /// <remarks>
    /// Generous because a cold Windows PowerShell start is seconds, not milliseconds. It costs
    /// nothing on the happy path: the wait ends the moment the file appears.
    /// </remarks>
    private static readonly TimeSpan PidBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long teardown waits for the abandoned <c>Run</c> to unwind once its child is dead.
    /// </summary>
    private static readonly TimeSpan DrainWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long row 1 waits for the killed child to actually disappear before failing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A poll, not a single sample, because the kill is ASYNCHRONOUS.</strong>
    /// <c>KillTreeQuietly</c> issues the kill and does not <c>WaitForExit</c>; both
    /// <c>TerminateProcess</c> on Windows and <c>SIGKILL</c> on POSIX return once the request is
    /// queued, not once the target is gone. Sampling liveness the instant <c>Run</c> returns can
    /// therefore observe a child that is dying, and on POSIX the corpse additionally lingers as a
    /// zombie until it is reaped. A single sample passing on the maintainer's Windows host is not
    /// evidence about the Linux lane that gates merges, where the reaping mechanism differs, so
    /// the window closes the class rather than resting on the host that happened to be green.
    /// </para>
    /// <para>
    /// Costs nothing on the green path: the poll ends at the first dead sample. Two seconds is the
    /// budget for the failure to be believed, not a latency the healthy case pays.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan DeathWindow = TimeSpan.FromSeconds(2);

    /// <summary>How often the pid file is polled.</summary>
    private const int PollIntervalMs = 100;

    /// <summary>
    /// The lifetime of a child that is supposed to outlast the row, as text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded rather than infinite so that a defect in the teardown below leaks a process which
    /// expires on its own within the minute instead of one that outlives the session — the same
    /// reasoning ChildProcessKillTreeTests records for its own child.
    /// </para>
    /// <para>
    /// Held as a string because it is only ever spliced into a command line; that keeps every
    /// interpolation in this file culture-free without a formatting ceremony.
    /// </para>
    /// </remarks>
    private const string ChildLifetimeSeconds = "60";

    /// <summary>
    /// Row 1 — an unbounded read against a child that never exits must not wedge the caller.
    /// </summary>
    /// <remarks>
    /// This row covers the plain absence of any budget: without one, nothing in <c>Run</c> ever
    /// stops waiting. It is distinct from row 2, which fails for a different reason — see there.
    /// </remarks>
    [Fact]
    public async Task Run_WhenTheChildNeverExits_ThrowsWithinTheBudgetAndLeavesNoLiveChild()
    {
        var startedUtc = DateTime.UtcNow;
        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, "child.pid");
        var shape = NeverExitingChild(pidFile);
        var runner = new SystemProcessRunner(TestBudget);

        var work = Task.Run(() => runner.Run(shape.FileName, shape.Arguments, directory));
        int? pid = null;
        try
        {
            pid = await Task.Run(() => WaitForPid(pidFile, PidBudget));
            Assert.True(
                pid is not null,
                FormattableString.Invariant(
                    $"The never-exiting child did not write its pid to '{pidFile}' within {PidBudget.TotalSeconds:F0}s, so this row could not establish that a child was ever running. That is a defect in the row, not in SystemProcessRunner."));

            var finished = await Task.WhenAny(work, Task.Delay(RunGraceWindow)) == work;
            Assert.True(
                finished,
                FormattableString.Invariant(
                    $"SystemProcessRunner.Run did not return within {RunGraceWindow.TotalSeconds:F0}s against a child that never exits, although it was given a {TestBudget.TotalSeconds:F0}s budget. Without a budget, StandardOutput.ReadToEnd() blocks until the child closes the pipe and WaitForExit() is unbounded besides."));

            // Only now that the task is known to be complete is it safe to await it: this is what
            // turns "returned" into "returned by refusing", and a timeout must be reported as a
            // timeout rather than as an empty capture that would silently narrow the change-set.
            var timeout = await Assert.ThrowsAsync<ProcessTimeoutException>(() => work);
            Assert.Equal(TestBudget, timeout.Budget);

            var dead = await Task.Run(() => WaitForDeath(pid, startedUtc, DeathWindow));
            Assert.True(
                dead,
                FormattableString.Invariant(
                    $"SystemProcessRunner.Run returned but child pid {pid} was still alive {DeathWindow.TotalSeconds:F0}s later. Abandoning the timed-out child is the leak #481 is about; the timeout path must tree-kill it. The window is there because the kill is asynchronous, not because a live child is tolerable."));
        }
        finally
        {
            KillTreeQuietly(pid, startedUtc);
            await DrainAsync(work);
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Row 2 — the #392 shape: the child exits promptly, a grandchild keeps the inherited pipes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DELIBERATELY A SEPARATE ROW FROM row 1, because the two fail for different reasons and a
    /// fix that satisfies one can leave the other wedged. Row 1 fails because nothing bounds the
    /// wait. This row fails even if <c>WaitForExit</c> is given a budget: the direct child is
    /// already gone within a second or two, so a bounded <c>WaitForExit</c> would be satisfied
    /// immediately — and <c>Run</c> would still hang, because it never reaches
    /// <c>WaitForExit</c>. <c>StandardOutput.ReadToEnd()</c> runs first and does not see EOF
    /// until EVERY writer closes the pipe, and the orphaned grandchild inherited a copy of it.
    /// </para>
    /// <para>
    /// Unlike row 1 this row asserts nothing about the grandchild's fate, and that is the honest
    /// limit rather than an omission: by the time the budget expires the DIRECT child has already
    /// exited, so the runner's tree-kill reaches an empty tree. Nothing portable reclaims a
    /// process whose parent is gone, and pretending otherwise would be an assertion about the
    /// operating system rather than about <c>Run</c>. Teardown below kills it instead.
    /// </para>
    /// <para>
    /// So this row is the one that says the budget must cover the READS, not merely the exit.
    /// Deleting it as a duplicate of row 1 would delete the only cover for that distinction.
    /// </para>
    /// <para>
    /// The pid captured here is the GRANDCHILD's, not the child's: the child is expected to be
    /// dead by the time teardown runs, and a tree-kill of a dead parent reaches nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Run_WhenAGrandchildHoldsTheInheritedPipes_ThrowsWithinTheBudget()
    {
        var startedUtc = DateTime.UtcNow;
        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, "grandchild.pid");
        var shape = GrandchildHoldingPipesChild(pidFile);
        var runner = new SystemProcessRunner(TestBudget);

        var work = Task.Run(() => runner.Run(shape.FileName, shape.Arguments, directory));
        int? pid = null;
        try
        {
            pid = await Task.Run(() => WaitForPid(pidFile, PidBudget));
            Assert.True(
                pid is not null,
                FormattableString.Invariant(
                    $"The pipe-holding grandchild did not write its pid to '{pidFile}' within {PidBudget.TotalSeconds:F0}s, so this row could not establish the #392 shape. That is a defect in the row, not in SystemProcessRunner."));

            var finished = await Task.WhenAny(work, Task.Delay(RunGraceWindow)) == work;
            Assert.True(
                finished,
                FormattableString.Invariant(
                    $"SystemProcessRunner.Run did not return within {RunGraceWindow.TotalSeconds:F0}s although its direct child exited almost immediately and it was given a {TestBudget.TotalSeconds:F0}s budget. The orphaned grandchild still holds the inherited stdout/stderr handles, so ReadToEnd() never sees EOF — bounding WaitForExit alone does not fix this row (#392)."));

            var timeout = await Assert.ThrowsAsync<ProcessTimeoutException>(() => work);
            Assert.Equal(TestBudget, timeout.Budget);
        }
        finally
        {
            KillTreeQuietly(pid, startedUtc);
            await DrainAsync(work);
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// The budget row 5 injects: far above <see cref="RunGraceWindow"/> on purpose.
    /// </summary>
    /// <remarks>
    /// If the row passes, it is the TOKEN that ended the call and nothing else — a budget anywhere
    /// near the grace window would let a runner that ignores the token pass on the ceiling's
    /// timing, which is the one confusion this row exists to rule out.
    /// </remarks>
    private static readonly TimeSpan UnreachableBudget = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Row 5 — a signalled token ends the call promptly and leaves no live child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The behaviour this pins is not "Run returns sooner", it is WHOSE cleanup runs. Before the
    /// token was threaded through, a Ctrl+C during <c>--changed-since</c> signalled a token that
    /// nothing on this path observed: System.CommandLine waited out its termination budget and
    /// then force-killed the CLI, at which point <c>Run</c>'s <c>finally</c> — and therefore the
    /// tree-kill — never ran and the git child was orphaned. That is the very leak #481 is about,
    /// on the path where an operator is most likely to intervene.
    /// </para>
    /// <para>
    /// FAIL-FAST LIKE ROWS 1 AND 2, and for the same reason: this assembly is a blocking CI gate
    /// with no per-test timeout, so <c>Run</c> is launched on a background task and the row
    /// asserts FIRST that it completed inside <see cref="RunGraceWindow"/>. A runner that ignored
    /// the token would fail this row in ten seconds rather than sitting on
    /// <see cref="UnreachableBudget"/> for five minutes.
    /// </para>
    /// <para>
    /// The child is killed by the row's own <c>finally</c> on every path, exactly as in rows 1
    /// and 2 — an assertion that fails must not leave a live child on the agent.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Run_WhenTheCallerCancels_ThrowsOperationCanceledAndLeavesNoLiveChild()
    {
        var startedUtc = DateTime.UtcNow;
        var directory = CreateScratchDirectory();
        var pidFile = Path.Combine(directory, "child.pid");
        var shape = NeverExitingChild(pidFile);
        var runner = new SystemProcessRunner(UnreachableBudget);
        using var cancellation = new CancellationTokenSource();

        var work = Task.Run(
            () => runner.Run(
                shape.FileName,
                shape.Arguments,
                directory,
                cancellationToken: cancellation.Token));
        int? pid = null;
        try
        {
            // Cancelled only once the child is known to be RUNNING. Cancelling earlier could be
            // answered by the pre-launch ThrowIfCancellationRequested, which would leave the row
            // asserting nothing about a live child.
            pid = await Task.Run(() => WaitForPid(pidFile, PidBudget));
            Assert.True(
                pid is not null,
                FormattableString.Invariant(
                    $"The never-exiting child did not write its pid to '{pidFile}' within {PidBudget.TotalSeconds:F0}s, so this row could not establish that a child was ever running. That is a defect in the row, not in SystemProcessRunner."));

            cancellation.Cancel();

            var finished = await Task.WhenAny(work, Task.Delay(RunGraceWindow)) == work;
            Assert.True(
                finished,
                FormattableString.Invariant(
                    $"SystemProcessRunner.Run did not return within {RunGraceWindow.TotalSeconds:F0}s of its cancellation token being signalled, against a child that never exits and a {UnreachableBudget.TotalMinutes:F0}-minute budget it cannot have reached. The token is not being raced against the budget, so a Ctrl+C reaches this runner's cleanup only after the process is force-killed — which is to say, never."));

            // ThrowsAny rather than Throws: TaskCanceledException derives from
            // OperationCanceledException, and the property under test is that cancellation
            // surfaces AS cancellation — never as ProcessTimeoutException, which GitChangeSet
            // maps to a usage error and would exit 2 for a Ctrl+C.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);

            var dead = await Task.Run(() => WaitForDeath(pid, startedUtc, DeathWindow));
            Assert.True(
                dead,
                FormattableString.Invariant(
                    $"SystemProcessRunner.Run returned on cancellation but child pid {pid} was still alive {DeathWindow.TotalSeconds:F0}s later. Cancellation must reach the same tree-kill the timeout path does; a cancelled call that abandons its child is the orphan #481 exists to prevent. The window is there because the kill is asynchronous, not because a live child is tolerable."));
        }
        finally
        {
            KillTreeQuietly(pid, startedUtc);
            await DrainAsync(work);
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Row 3 — the normal path still captures both streams and the exit code. Green before AND
    /// after the fix; this is the regression guard the bounding change must not break.
    /// </summary>
    [Fact]
    public void Run_WhenTheChildPrintsAndExitsNonZero_CapturesBothStreamsAndTheExitCode()
    {
        var directory = CreateScratchDirectory();
        try
        {
            var shape = PrintsAndExitsChild();

            var result = SystemProcessRunner.Instance.Run(shape.FileName, shape.Arguments, directory);

            Assert.Equal(7, result.ExitCode);

            // Trimmed rather than matched exactly: the line terminator differs by shell, and
            // cmd.exe's `echo` is famous for carrying a trailing space through a redirection.
            // The property under test is "the stream reached the caller", not its whitespace.
            Assert.Equal("OUT", result.StandardOutput.Trim());
            Assert.Equal("ERR", result.StandardError.Trim());
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Row 4 — a missing executable is still a <see cref="ProcessLaunchException"/>, not a raw
    /// Win32Exception. Green before AND after the fix: GitChangeSet's usage-error mapping depends
    /// on it, so the try/finally the fix wraps the launch in must not swallow or retype it.
    /// </summary>
    [Fact]
    public void Run_WhenTheExecutableDoesNotExist_ThrowsProcessLaunchException()
    {
        var directory = CreateScratchDirectory();
        try
        {
            // Fully qualified and absent, not a bare name: a bare name is now refused by the
            // argument guard (row 6) and would never reach the launch this row is about.
            var missing = Path.Combine(
                directory,
                "vouchfx-no-such-executable-"
                    + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

            var exception = Assert.Throws<ProcessLaunchException>(
                () => SystemProcessRunner.Instance.Run(missing, Array.Empty<string>(), directory));

            Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Row 4b (#498, site 1) — the launch-failure message is the ENGINE's, not the BCL's quoted
    /// back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET composes a failed start from <c>SR.ErrorStartingProcess</c> — "An error occurred trying
    /// to start process '{0}' with working directory '{1}'. {2}" — so splicing
    /// <c>ex.Message</c> into the engine's own sentence carried the absolute WORKING DIRECTORY into
    /// it, unasked. That is the #498 defect at this site, and all this row is about.
    /// </para>
    /// <para>
    /// <strong>The executable path deliberately REMAINS in this message, and row 4 above asserts
    /// that it does.</strong> That is not an oversight in the fix: this exception is the seam's
    /// internal diagnostic, <c>GitChangeSet.RunGit</c> discards its message entirely rather than
    /// printing it, and <c>GitChangeSetTests.LaunchFailure_DoesNotDiscloseTheResolvedPath</c> uses
    /// the raw message naming the resolved path as its CONTROL for the mapped one not naming it.
    /// Stripping it here would delete that control and leave a launch failure identifying nothing.
    /// </para>
    /// <para>
    /// The working directory is a SECOND scratch directory rather than the one holding the absent
    /// executable, so "the message does not name the working directory" is not satisfied trivially
    /// by the executable path being absent from it.
    /// </para>
    /// </remarks>
    [Fact]
    public void Run_WhenTheExecutableDoesNotExist_DoesNotQuoteTheFrameworkMessage()
    {
        var executableDirectory = CreateScratchDirectory();
        var workingDirectory = CreateScratchDirectory();
        try
        {
            var missing = Path.Combine(
                executableDirectory,
                "vouchfx-no-such-executable-"
                    + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

            var exception = Assert.Throws<ProcessLaunchException>(
                () => SystemProcessRunner.Instance.Run(
                    missing, Array.Empty<string>(), workingDirectory));

            // The premise, asserted rather than assumed: the BCL's own text really does name the
            // working directory, so the assertions below measure a removal rather than an absence
            // that was always there.
            Assert.NotNull(exception.InnerException);
            Assert.Contains(
                workingDirectory, exception.InnerException!.Message, StringComparison.Ordinal);

            Assert.DoesNotContain(workingDirectory, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "with working directory", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
            TryDeleteDirectory(executableDirectory);
        }
    }

    /// <summary>
    /// Row 7 (#500) — a caller-supplied environment is the child's WHOLE environment: an
    /// allow-listed name and a <c>GIT_</c>-prefixed name reach it, and nothing else does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The child prints its own environment block rather than one variable, so the row asserts the
    /// absence of the planted secret over everything the child can see rather than over one lookup
    /// that could be spelt wrong and pass.
    /// </para>
    /// <para>
    /// The planted names are process-wide for the duration of the row, which is safe in this
    /// assembly's parallel scheduling because nothing else reads them; they are removed with
    /// <see langword="null"/> rather than <c>""</c>, since an empty value DELETES the variable on
    /// net8 and would make the cleanup indistinguishable from a set-to-empty.
    /// </para>
    /// </remarks>
    [Fact]
    public void Run_WithAConfinedEnvironment_PassesTheAllowListAndTheGitPrefixAndNothingElse()
    {
        const string SecretName = "VOUCHFX_TEST_FAKE_SECRET";
        const string GitPrefixedName = "GIT_VOUCHFX_TEST_PROBE";

        var directory = CreateScratchDirectory();
        Environment.SetEnvironmentVariable(SecretName, "hunter2-not-for-the-child");
        Environment.SetEnvironmentVariable(GitPrefixedName, "probe-value");
        try
        {
            var confined = GitChangeSet.ConfinedGitEnvironment();
            var shape = PrintsItsEnvironmentChild();

            var result = SystemProcessRunner.Instance.Run(
                shape.FileName, shape.Arguments, directory, confined);

            Assert.Equal(0, result.ExitCode);

            var names = EnvironmentNamesIn(result.StandardOutput);

            // PATH is allow-listed, so it survives. Asserted as a NAME rather than as the substring
            // "PATH=", which a drill showed is satisfied by the allow-listed HOMEPATH and therefore
            // stayed green with PATH deleted from the allow-list.
            Assert.Contains("PATH", names, StringComparer.OrdinalIgnoreCase);

            // The GIT_ prefix passes through.
            Assert.Contains(GitPrefixedName, names, StringComparer.OrdinalIgnoreCase);

            // Everything else is gone -- this is the property #500 is about.
            Assert.DoesNotContain(SecretName, names, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretName, null);
            Environment.SetEnvironmentVariable(GitPrefixedName, null);
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Row 6 — an unqualified <c>fileName</c> is refused before anything is launched, which is the
    /// gate <see cref="IProcessRunner"/>'s contract lacked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This row exists because the requirement it pins shipped as prose.</strong> #499's fix
    /// resolves git to an absolute path in <c>GitChangeSet</c> and documents on this seam that every
    /// caller must do the same; nothing enforced it. The defect #499 closed was exactly a caller
    /// handing over the bare name <c>git</c>, so a doc-comment is demonstrably not cover: on Windows
    /// an unqualified name is resolved by the operating system's own search, which reaches the
    /// calling executable's directory and the calling process's current directory ahead of
    /// <c>PATH</c>. A future caller reintroducing that shape now fails here rather than silently
    /// reopening the hole.
    /// </para>
    /// <para>
    /// The three spellings are chosen for what each would do if it got through, not for variety.
    /// <c>git</c> is the original defect verbatim, and the M1 mutation drill confirmed it: with the
    /// guard removed, that call LAUNCHED — the operating system's search found a real git, which is
    /// the hole rather than a hypothetical one. <c>./git</c> is relative, and a relative name is
    /// resolved against the calling process's current directory — the term <c>GitChangeSet</c>'s
    /// header records as reachable, since <c>cd untrusted-repo &amp;&amp; vouchfx run .
    /// --changed-since main</c> hands the CLI that directory. <c>C:git</c> is the Windows
    /// drive-relative form, admitted by
    /// <see cref="Path.IsPathRooted(string)"/> and refused by
    /// <see cref="Path.IsPathFullyQualified(string)"/>; it is why the guard uses the latter, and it
    /// is asserted only on Windows because POSIX reads it as an ordinary relative file name with a
    /// colon in it — refused there too, but for a different reason, which would make the row assert
    /// a coincidence.
    /// </para>
    /// <para>
    /// <see cref="ArgumentException"/> rather than <see cref="ProcessLaunchException"/> is itself
    /// part of the contract and so is asserted by type: <c>GitChangeSet.RunGit</c> catches the
    /// runner's three failure types narrowly and maps them to a usage error (exit 2). A broken
    /// caller inside this assembly must escape that mapping rather than be reported to the user as
    /// their own mistake.
    /// </para>
    /// </remarks>
    [Fact]
    public void Run_WhenTheExecutableIsNotFullyQualified_ThrowsArgumentExceptionAndLaunchesNothing()
    {
        var directory = CreateScratchDirectory();
        try
        {
            var unqualified = new List<string> { "git", "./git" };
            if (OperatingSystem.IsWindows())
            {
                unqualified.Add("C:git");
            }

            foreach (var fileName in unqualified)
            {
                var exception = Assert.Throws<ArgumentException>(
                    () => SystemProcessRunner.Instance.Run(
                        fileName, Array.Empty<string>(), directory));

                Assert.Equal("fileName", exception.ParamName);
                Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            TryDeleteDirectory(directory);
        }
    }

    // ── child shapes ─────────────────────────────────────────────────────────────────────────

    /// <summary>An executable plus its argument vector.</summary>
    private sealed record ChildShape(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>
    /// A child that writes its own pid and then holds the pipes open for its whole lifetime.
    /// </summary>
    private static ChildShape NeverExitingChild(string pidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            // Single quotes throughout: the argument reaches CreateProcess quoted by the runtime,
            // and an embedded double quote would have to survive both that escaping and
            // powershell.exe's own command-line parsing. Nothing here needs one.
            return new ChildShape(
                WindowsPowerShell,
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"$PID | Set-Content -LiteralPath '{pidFile}'; Start-Sleep -Seconds {ChildLifetimeSeconds}",
                });
        }

        return new ChildShape(
            "/bin/sh",
            new[] { "-c", $"echo $$ > '{pidFile}'; sleep {ChildLifetimeSeconds}" });
    }

    /// <summary>
    /// A child that starts a grandchild, records the GRANDCHILD's pid, and exits — leaving the
    /// grandchild holding an inherited copy of the runner's stdout/stderr pipes (#392).
    /// </summary>
    /// <remarks>
    /// On Windows, <c>Start-Process -NoNewWindow</c> means UseShellExecute=false with no
    /// redirection, which hands the child the parent's std handles — here, the runner's pipes.
    /// On POSIX the background job inherits them through fork/exec, and <c>$!</c> is its pid.
    /// </remarks>
    private static ChildShape GrandchildHoldingPipesChild(string pidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ChildShape(
                WindowsPowerShell,
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    $"(Start-Process -FilePath 'ping.exe' -ArgumentList '-n','{ChildLifetimeSeconds}','127.0.0.1' -NoNewWindow -PassThru).Id | Set-Content -LiteralPath '{pidFile}'",
                });
        }

        return new ChildShape(
            "/bin/sh",
            new[] { "-c", $"sleep {ChildLifetimeSeconds} & echo $! > '{pidFile}'" });
    }

    /// <summary>A child that prints to both streams and exits 7.</summary>
    private static ChildShape PrintsAndExitsChild()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ChildShape(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                new[] { "/c", "echo OUT& echo ERR>&2& exit 7" });
        }

        return new ChildShape("/bin/sh", new[] { "-c", "echo OUT; echo ERR 1>&2; exit 7" });
    }

    /// <summary>A child that prints its own environment block and exits 0 (#500).</summary>
    /// <remarks>
    /// Both spellings are SHELL BUILTINS (<c>set</c> under cmd, <c>export -p</c> under POSIX sh)
    /// and neither is resolved through <c>PATH</c>. That matters because the row hands the child a
    /// confined environment: a shape that reached for <c>/usr/bin/env</c> through a <c>PATH</c>
    /// lookup would be testing the allow-list with the allow-list.
    /// </remarks>
    private static ChildShape PrintsItsEnvironmentChild()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ChildShape(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                new[] { "/c", "set" });
        }

        return new ChildShape("/bin/sh", new[] { "-c", "export -p" });
    }

    /// <summary>
    /// The variable NAMES in a <see cref="PrintsItsEnvironmentChild"/> capture.
    /// </summary>
    /// <remarks>
    /// Parsing to names rather than matching substrings is not tidiness: a drill deleting
    /// <c>PATH</c> from the allow-list left the row green, because the allow-listed
    /// <c>HOMEPATH=...</c> contains the substring <c>PATH=</c>. A name set cannot be satisfied by a
    /// suffix.
    /// </remarks>
    private static HashSet<string> EnvironmentNamesIn(string capture)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in capture.Split('\n'))
        {
            // POSIX `export -p` prefixes each line; cmd's `set` does not.
            var line = rawLine.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..];
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                names.Add(line[..separator]);
            }
        }

        return names;
    }

    /// <summary>
    /// Windows PowerShell's fully qualified path, because the runner refuses an unqualified one.
    /// </summary>
    /// <remarks>
    /// These shapes named <c>powershell.exe</c> and <c>cmd.exe</c> bare until
    /// <see cref="SystemProcessRunner.Run"/> began enforcing the fully-qualified contract
    /// <see cref="IProcessRunner"/> documents. Rooting them is not test bookkeeping: a bare name
    /// here would have been resolved by the very operating-system search #499 exists to remove, so
    /// these rows were measuring the runner through the mechanism the change forbids. The POSIX
    /// branches already named <c>/bin/sh</c>, which is fully qualified, and are unchanged.
    /// </remarks>
    private static string WindowsPowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    // ── teardown machinery ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="pidFile"/> until it holds a pid, or <paramref name="budget"/>
    /// expires. Synchronous on purpose — callers wrap it in <c>Task.Run</c> so no blocking file
    /// read sits directly inside an async method.
    /// </summary>
    private static int? WaitForPid(string pidFile, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                string text;
                try
                {
                    text = File.ReadAllText(pidFile);
                }
                catch (IOException)
                {
                    // The child is mid-write; try again.
                    text = string.Empty;
                }

                // Digits only: PowerShell's Set-Content may prepend a BOM and appends a newline.
                var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
                if (digits.Length > 0
                    && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                {
                    return pid;
                }
            }

            Thread.Sleep(PollIntervalMs);
        }

        return null;
    }

    /// <summary>
    /// Opens <paramref name="pid"/> if it is still a live process that this row could plausibly
    /// have started.
    /// </summary>
    /// <remarks>
    /// The <see cref="Process.StartTime"/> check is a pid-reuse guard. The window between the
    /// child writing its pid and teardown reading it is seconds, but the consequence of losing
    /// that race is killing an unrelated process on a shared CI agent, which is worth three lines
    /// to rule out.
    /// </remarks>
    private static Process? TryOpen(int? pid, DateTime startedUtc)
    {
        if (pid is not int id)
        {
            return null;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // No such process — it has already exited.
            return null;
        }

        try
        {
            if (process.StartTime.ToUniversalTime() < startedUtc.AddSeconds(-5))
            {
                // Older than this row: a recycled pid, not our child.
                process.Dispose();
                return null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            // StartTime unreadable (exited, access denied, remote) — treat it as not ours.
            process.Dispose();
            return null;
        }

        return process;
    }

    /// <summary>Whether the recorded pid is still a live process started by this row.</summary>
    private static bool IsAlive(int? pid, DateTime startedUtc)
    {
        var process = TryOpen(pid, startedUtc);
        if (process is null)
        {
            return false;
        }

        using (process)
        {
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Polls <see cref="IsAlive"/> until the recorded pid is gone, or <paramref name="window"/>
    /// expires; returns whether it went.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose, for the reason <see cref="WaitForPid"/> gives — callers wrap it in
    /// <c>Task.Run</c> rather than sleeping inside an async method.
    /// </remarks>
    private static bool WaitForDeath(int? pid, DateTime startedUtc, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            if (!IsAlive(pid, startedUtc))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    /// <summary>
    /// Kills the recorded pid and everything beneath it. Called from every <c>finally</c> so that
    /// a red run leaves nothing behind.
    /// </summary>
    private static void KillTreeQuietly(int? pid, DateTime startedUtc)
    {
        var process = TryOpen(pid, startedUtc);
        if (process is null)
        {
            return;
        }

        // Kill inside, dispose outside: `using` emits its Dispose in the enclosing finally, so the
        // dangerous dispose-then-kill order cannot be written here. See ChildProcess's remarks.
        using (process)
        {
            ChildProcess.KillTreeQuietly(process);
        }
    }

    /// <summary>
    /// Waits, boundedly, for an abandoned <c>Run</c> to unwind now that its child is dead, and
    /// observes whatever it eventually faults with.
    /// </summary>
    /// <remarks>
    /// The observer is a CONTINUATION rather than a read of <c>work.Exception</c> after the wait:
    /// a run that has not completed inside <see cref="DrainWindow"/> has a null <c>Exception</c>,
    /// so reading the property there observes nothing and a fault reached afterwards would still
    /// land on <see cref="TaskScheduler.UnobservedTaskException"/> — attributed to a finalizer
    /// thread and to no test in particular. The bound stays because teardown must not wedge on a
    /// runner that has.
    /// </remarks>
    private static async Task DrainAsync(Task<ProcessResult> work)
    {
        _ = work.ContinueWith(static settled => _ = settled.Exception, TaskScheduler.Default);
        await Task.WhenAny(work, Task.Delay(DrainWindow)).ConfigureAwait(false);
    }

    /// <summary>Creates a per-row scratch directory outside the repository.</summary>
    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "vouchfx-procrunner-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Best-effort scratch cleanup; a stuck child can still hold the pid file.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Teardown must never replace the real failure with its own.
        }
    }
}
