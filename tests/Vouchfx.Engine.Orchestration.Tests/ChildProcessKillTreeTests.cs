// The behavioural half of the issue #475 guard set: the shared tree-kill really kills, and really
// does not throw out of a `finally`.
//
// The census beside this file (ChildProcessKillCallSiteCensusTests) proves something narrower than
// it is tempting to say: that a killing `finally` is PRESENT in the member that launches. Not that
// the launch is covered by it, not that it kills the right variable, not that the kill precedes the
// dispose. Its own limits paragraph is the authority on that wording, and this sentence is written
// to match it rather than to summarise it upwards — two files describing one gate at two strengths
// is how the weaker claim ends up quoted.
//
// These rows are the other half. They prove the helper is worth calling: they cover both arms of its
// `if (!HasExited)` short-circuit — kill a live child, and return silently on a dead one — plus ONE
// of the four types in its catch filter.
//
// WHICH ONE, because "the filter is covered" would be false. The filter admits
// InvalidOperationException, Win32Exception, NotSupportedException and AggregateException. Only
// InvalidOperationException is reached by a row here (the no-associated-process case, via
// HasExited). The other three are NOT exercised by anything:
//
//   Win32Exception       needs a kill to lose the exit race, or an OS refusal - neither is
//                        deterministically producible from a test.
//   AggregateException   needs a descendant that refuses to die. This is the one the round-1
//                        filters in #378 MISSED, so it is the one an absent row is most expensive
//                        for - and it is still absent, on purpose: faking it would mean a fake.
//   NotSupportedException needs a remote process.
//
// Their justification is documentary rather than behavioural: each is read off the .NET 8 reference
// XML for Process.Kill(bool) and recorded in the helper's own remarks. A reader deleting one of them
// as dead weight will not be stopped by a test - only by that comment.
//
// The reason the filter matters at all is that a throw from a `finally` replaces the real test
// failure with a teardown one, which is the misattribution issue #378 was about.
//
// WHAT THESE ROWS DO NOT PROVE, stated rather than left to be assumed: the helper passes
// `entireProcessTree: true`, and the child launched below is a shell that itself has a child, so
// the tree path is EXERCISED — but only the direct child's death is ASSERTED. Obtaining a
// grandchild's pid portably needs either a shell that prints `$!` (POSIX only) or Start-Process
// -PassThru (Windows only), and a row whose assertion differs by operating system is worse evidence
// than an honest gap. The tree behaviour is the BCL's, exercised in anger by the drill lane.
//
// No Docker, no trait: these rows belong to the fast `requires!=docker` lane and finish in well
// under a second.
using System;
using System.Diagnostics;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Behavioural cover for <see cref="ChildProcess.KillTreeQuietly(Process)"/>.
/// </summary>
public sealed class ChildProcessKillTreeTests
{
    /// <summary>How long a killed child is given to actually go away before the row fails.</summary>
    private const int DeathBudgetMs = 15_000;

    /// <summary>
    /// A long-lived child, launched exactly as the docker sites launch theirs, is dead after the
    /// helper runs.
    /// </summary>
    /// <remarks>
    /// The child is a SHELL running a long command, so the process being killed is itself a parent —
    /// the shape every real caller has (a CLI over DCP, a docker client over a daemon call).
    /// </remarks>
    [Fact]
    public void KillTreeQuietly_KillsALongLivedChild()
    {
        WithLongLivedChild(process =>
        {
            Assert.False(
                process.HasExited,
                "the long-lived child exited before the kill, so this row would pass without "
                + "measuring anything. Check that the shell command below is still long-running.");

            ChildProcess.KillTreeQuietly(process);

            Assert.True(
                process.WaitForExit(DeathBudgetMs),
                $"the child (pid {process.Id}) was still alive {DeathBudgetMs} ms after "
                + $"{nameof(ChildProcess.KillTreeQuietly)} returned. The helper swallows the "
                + "exceptions a kill can legitimately lose, so a survivor here means the kill was "
                + "refused rather than raced - and every launch site in the suite is relying on it.");
        });
    }

    /// <summary>
    /// Calling the helper on a child that has already exited is silent — the <c>HasExited</c>
    /// short-circuit, which is what makes the helper safe to call unconditionally from a
    /// <c>finally</c> and safe to call twice.
    /// </summary>
    /// <remarks>
    /// <strong>This is NOT the exit race.</strong> With <c>HasExited</c> already <see langword="true"/>
    /// the guard short-circuits and <c>Kill</c> is never invoked, so the
    /// ended-between-the-check-and-the-kill window is not entered and no exception filter is
    /// exercised. The real race is by definition not deterministically producible; what this row
    /// pins is the branch that makes the common case — <c>finally</c> after a clean completion, or a
    /// second call layered on an earlier one (as <c>TopologyTeardownLeakTests.RunDocker</c> now
    /// does) — cost nothing and throw nothing.
    /// </remarks>
    [Fact]
    public void KillTreeQuietly_IsSilentOnAnAlreadyExitedChild()
    {
        WithLongLivedChild(process =>
        {
            ChildProcess.KillTreeQuietly(process);
            Assert.True(process.WaitForExit(DeathBudgetMs), "the child did not die on the first kill.");

            var thrown = Record.Exception(() => ChildProcess.KillTreeQuietly(process));

            Assert.Null(thrown);
        });
    }

    /// <summary>
    /// A <see cref="Process"/> with nothing attached is silent too — the
    /// <see cref="InvalidOperationException"/> branch of the filter, which <c>HasExited</c> raises.
    /// </summary>
    /// <remarks>
    /// This is the branch whose comment says it is NOT the exit race on .NET 8. Without a row it
    /// reads as dead weight somebody would eventually delete, taking the no-associated-process guard
    /// with it.
    /// </remarks>
    [Fact]
    public void KillTreeQuietly_IsSilentOnAProcessWithNoAssociatedChild()
    {
        using var never = new Process();

        var thrown = Record.Exception(() => ChildProcess.KillTreeQuietly(never));

        Assert.Null(thrown);
    }

    /// <summary>How long the child would sleep if nothing killed it, in seconds.</summary>
    /// <remarks>
    /// A ceiling on the damage this file can do, not a duration anything waits for. These rows run
    /// in the blocking <c>requires!=docker</c> CI job, so if the kill ever regresses, EVERY failing
    /// row strands a child for this long on the runner and on the developer's machine. Sixty
    /// seconds is four times <see cref="DeathBudgetMs"/> — long enough that the child cannot expire
    /// on its own and fake a pass — and short enough that a regression cleans up after itself well
    /// inside one run.
    /// </remarks>
    private const int ChildLifetimeSeconds = 60;

    /// <summary>
    /// Starts a shell running a long command, hands it to <paramref name="body"/>, and kills the
    /// tree on every path out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lifetime is owned HERE rather than by a factory that returns a live
    /// <see cref="Process"/>, because a factory would be a launch site with no <c>finally</c> - the
    /// exact shape the census beside this file refuses, and it would be refusing it correctly. A
    /// row that measures the rule obeys it.
    /// </para>
    /// <para>
    /// The command is a bounded sleep rather than an infinite loop so that a failure of these rows
    /// leaks a process that expires on its own rather than one that outlives the session.
    /// </para>
    /// </remarks>
    private static void WithLongLivedChild(Action<Process> body)
    {
        var info = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            // `ping -n <n> 127.0.0.1` is the portable-on-Windows sleep: no `sleep` binary ships
            // with the OS, and `timeout` refuses to run with stdin redirected. The count is one
            // ping per second after the first, so it is the seconds figure within a second.
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add($"ping -n {ChildLifetimeSeconds} 127.0.0.1 > nul");
        }
        else
        {
            info.FileName = "/bin/sh";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"sleep {ChildLifetimeSeconds}");
        }

        var process = Process.Start(info)
            ?? throw new InvalidOperationException(
                $"Failed to start the long-lived test child '{info.FileName}'.");

        using (process)
        {
            try
            {
                body(process);
            }
            finally
            {
                ChildProcess.KillTreeQuietly(process);
            }
        }
    }
}
