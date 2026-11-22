// Vouchfx.Cli — CancelStage: the named stages of SystemProcessRunner's cancel path.
//
// DIAGNOSTIC SCAFFOLDING FOR ONE INTERMITTENT FAILURE, AND REMOVABLE. The cancellation row of
// SystemProcessRunnerTests failed once on a hosted Windows runner because Run took longer than a
// ten-second slack to return after its token was signalled. The failure said "too slow" and nothing
// else: a tree-kill that blocked, a pool that had no thread to run the continuation, and a host that
// stalled all look identical from outside. The runner therefore reports each stage of the unwind to
// an optional hook (SystemProcessRunner's internal constructor), and the test project's
// CancelStageRecorder timestamps them so the next failure names the slow stage.
//
// CANCEL-STAGE SCAFFOLDING: REMOVE ONCE CLASSIFIED. That phrase is the tracking marker: nothing else
// records that this code is temporary. Once a failure of the cancellation row has been read off its
// timeline and the slow stage named, this enum, the runner's hook, its call sites and the test
// project's recorder can be deleted together. Every one of those pieces names the CancelStage type,
// so the compiler's own references to it, and a search for the name, are the list of what to delete.
//
// WHAT IT IS NOT. SystemProcessRunner.Instance carries no hook, so production pays only null tests,
// and the hook is invoked on the cancel path only. A hook that throws is ignored (the runner calls
// it through one swallowing helper), so it cannot change what Run returns, throws, kills or
// disposes. Nothing reported carries a command line, an argument, a working directory, an
// environment value or an exception message: the one detail string the runner passes is the TYPE
// name of a kill failure its guard swallowed.

namespace Vouchfx.Cli.Selection;

/// <summary>
/// The stages of a cancelled <see cref="SystemProcessRunner.Run"/>, in the order they are expected.
/// </summary>
/// <remarks>
/// Diagnostic scaffolding: see the file header. Stages marked "stamped by the caller" are never
/// reported by the runner; they are here so the caller's timeline and the runner's share one
/// vocabulary.
/// </remarks>
internal enum CancelStage
{
    /// <summary>The caller is about to signal the token. Stamped by the caller.</summary>
    Signalled,

    /// <summary>The caller's signalling call has returned. Stamped by the caller.</summary>
    SignalReturned,

    /// <summary>The runner's continuation saw the cancellation.</summary>
    Observed,

    /// <summary>The runner is about to start the tree-kill.</summary>
    KillIssued,

    /// <summary>
    /// The tree-kill returned. The detail is the type name of an exception the guard swallowed, or
    /// <see langword="null"/> if it swallowed none.
    /// </summary>
    KillReturned,

    /// <summary>The runner's own ends of the captured pipes have been disposed.</summary>
    StreamsDisposed,

    /// <summary>The runner is about to throw <see cref="OperationCanceledException"/>.</summary>
    Thrown,

    /// <summary>
    /// The <see cref="System.Diagnostics.Process"/> has been disposed (<c>Process.Close</c>) as the
    /// throw unwinds out of the runner's <c>using</c> block.
    /// </summary>
    ProcessDisposed,

    /// <summary>
    /// The caller's thread got control back from <c>Run</c>. Stamped by the caller. The gap since
    /// <see cref="ProcessDisposed"/> holds the disposal of the runner's linked timer source, the
    /// completion of its async state machine, and the blocked caller's thread waking up.
    /// </summary>
    RunReturned,

    /// <summary>
    /// The caller's own process-death poll saw the child gone. Stamped by the caller: it says the
    /// child was gone by then, not when it died, and it is not reached if the caller never polls.
    /// </summary>
    ChildExited,

    /// <summary>
    /// Both output reads completed, on whichever thread completed them. Not reached if something
    /// outside the killed tree (an orphaned grandchild) still holds a pipe open.
    /// </summary>
    ReadersDrained,
}
