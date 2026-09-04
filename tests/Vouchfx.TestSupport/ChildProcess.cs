// The ONE guarded tree-kill every test assembly's child-process launch site calls from its
// `finally` — issues #378 (the drill lane) and #475 (the orchestration docker lane).
//
// WHY IT LIVES HERE RATHER THAN IN A TEST ASSEMBLY
// ───────────────────────────────────────────────
// It was born `internal` in Vouchfx.Engine.Runtime.Tests/DrillHostHygiene.cs, where #378 collapsed
// four functionally identical copies of the same race guard into one. #475 then found the same
// gap in Vouchfx.Engine.Orchestration.Tests, which cannot see an `internal` of another assembly —
// so the choice was a second copy or a lift. Two copies of a guard whose whole value is that its
// catch filter is exhaustive is how the filter drifts: the round-1 filters in #378 already missed
// AggregateException once, and a divergent copy is that miss made permanent in one lane only.
//
// Vouchfx.TestSupport is the right home because it is referenced by every test assembly that
// launches a child (Runtime, Orchestration, Cli) and it is not packable — nothing here ships, so
// making the type `public` moves no product surface and touches no golden.
//
// Pure BCL: this file references no Vouchfx.* type, which is the standing constraint on this
// project (see the .csproj header).
using System;
using System.Diagnostics;

namespace Vouchfx.TestSupport;

/// <summary>
/// The guarded tree-kill shared by every child-process launch site in the test suite.
/// </summary>
/// <remarks>
/// <para>
/// Callers launch a child, do their work in a <c>try</c>, and call
/// <see cref="KillTreeQuietly(Process)"/> from the matching <c>finally</c> — before disposing the
/// <see cref="Process"/> object. Disposing a <see cref="Process"/> releases the handle; it does
/// NOT stop the process, which is precisely the defect both #378 and #475 record.
/// </para>
/// <para>
/// <strong>KILL BEFORE DISPOSE, and the cost of the other order is SILENCE. Measured, not
/// reasoned.</strong> Calling <c>Dispose()</c> first and then this helper on the same object:
/// <c>HasExited</c> raises <see cref="InvalidOperationException"/>, this method's own filter
/// swallows it, <strong>nothing is thrown, nothing is logged, and the child is still alive
/// afterwards</strong> — verified by re-attaching to the pid. There is no second signal. The
/// exception filter that makes this helper safe in a <c>finally</c> is the same thing that makes
/// the wrong order invisible, and that trade is deliberate.
/// </para>
/// <para>
/// Nothing enforces the order. <c>ChildProcessKillCallSiteCensusTests</c> in
/// <c>Vouchfx.Engine.Orchestration.Tests</c> gates that a killing <c>finally</c> EXISTS in each
/// launching member; it is syntactic and cannot see ordering, and it does not read the drill lane's
/// assembly at all. Every call site in the repository orders it correctly today. If you are adding
/// the next one, this paragraph is the only thing standing between you and a leak that reports
/// nothing.
/// </para>
/// </remarks>
public static class ChildProcess
{
    /// <summary>
    /// Kills <paramref name="process"/> and everything beneath it, swallowing the races and
    /// refusals that a kill can legitimately lose.
    /// </summary>
    /// <param name="process">The child to terminate. Already-exited is not an error.</param>
    /// <remarks>
    /// <para>
    /// <c>entireProcessTree: true</c> because a test's child is routinely itself a parent: a CLI
    /// child has DCP beneath it, and a <c>docker build</c> launched through a shell has the build
    /// beneath the shell. Killing only the direct child would leave the grandchild holding
    /// containers, a network, or a build cache lock with nothing left to release them.
    /// </para>
    /// <para>
    /// The <c>HasExited</c> test cannot be made atomic with the kill, so the catch is the real
    /// guard rather than a courtesy. Callers invoke this from a <c>finally</c>, where ANY throw
    /// replaces the real failure with a teardown one - which is the misattribution issue #378 is
    /// about, arriving through the fix for it. The filter therefore covers every exception
    /// <c>Kill(bool)</c> documents, read from the .NET 8 reference XML rather than assumed:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="System.ComponentModel.Win32Exception"/> - "could not be terminated -or- the
    ///     process is terminating". This, NOT <c>InvalidOperationException</c>, is the
    ///     ended-between-the-check-and-the-kill case on this runtime.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="AggregateException"/> - "not all processes in the descendant tree could be
    ///     terminated". Reachable only through <c>entireProcessTree: true</c>, which is exactly how
    ///     every caller here invokes it, and a partial tree kill is not worth a thrown teardown.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="NotSupportedException"/> - a remote process.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="InvalidOperationException"/> - documented for .NET Framework and .NET Core
    ///     3.0 and earlier only, so it is NOT the exit race here. Kept because <c>HasExited</c>
    ///     itself raises it when no process is associated with the object.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static void KillTreeQuietly(Process process)
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
            // Terminating, partially killed, remote, or no longer associated - see the remarks.
        }
    }
}
