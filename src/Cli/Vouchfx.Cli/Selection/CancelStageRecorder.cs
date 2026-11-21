// Vouchfx.Cli — CancelStageRecorder: stage timestamps for the cancel path of SystemProcessRunner.
//
// WHY THIS EXISTS. The cancellation row of SystemProcessRunnerTests failed once on a hosted
// Windows runner because Run took longer than a ten-second slack to return after its token was
// signalled. The failure said "too slow" and nothing else: a tree-kill that blocked, a pool that
// had no thread to run the continuation, and a wait that was never raced all look identical from
// outside. This recorder stamps each stage of the unwind so the next failure names the slow one.
//
// WHAT IT IS NOT. It is opt-in and off in production: SystemProcessRunner.Instance carries no
// recorder, and every call site in the runner is guarded by a null test that sits on the cancel
// path only, so an uncancelled run executes none of it. It records instants and one exception
// TYPE name. It never records a command line, an argument, a working directory, an environment
// value or an exception message, so there is nothing in it that could carry a secret.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vouchfx.Cli.Selection;

/// <summary>The stages of a cancelled <see cref="SystemProcessRunner.Run"/>, in the order they are expected.</summary>
internal enum CancelStage
{
    /// <summary>The caller is about to signal the token. Stamped by the caller, not the runner.</summary>
    Signalled,

    /// <summary>The caller's signalling call has returned. Stamped by the caller.</summary>
    SignalReturned,

    /// <summary>The runner's continuation saw the cancellation.</summary>
    Observed,

    /// <summary>The runner is about to start the tree-kill.</summary>
    KillIssued,

    /// <summary>The tree-kill returned, or threw something the guard swallows.</summary>
    KillReturned,

    /// <summary>The runner's own ends of the captured pipes have been disposed.</summary>
    StreamsDisposed,

    /// <summary>The runner is about to throw <see cref="OperationCanceledException"/>.</summary>
    Thrown,

    /// <summary>The caller's thread got control back from <c>Run</c>. Stamped by the caller.</summary>
    RunReturned,

    /// <summary>The child was seen to have exited. Asynchronous: may follow <see cref="RunReturned"/>.</summary>
    ChildExited,

    /// <summary>Both output readers completed. Asynchronous: may follow <see cref="RunReturned"/>.</summary>
    ReadersDrained,
}

/// <summary>
/// Records a first-write-wins timestamp per <see cref="CancelStage"/>, thread-safely, and
/// renders them as a single diagnostic line.
/// </summary>
internal sealed class CancelStageRecorder
{
    private static readonly int StageCount = Enum.GetValues<CancelStage>().Length;

    // 0 means "not reached". Stopwatch.GetTimestamp() is positive on every supported platform; a
    // zero reading is nudged to 1 so the sentinel stays unambiguous.
    private readonly long[] _timestamps = new long[StageCount];

    private string? _killSwallowed;
    private string? _poolAtSignalled;
    private string? _poolAtObserved;

    /// <summary>Stamps <paramref name="stage"/> now; a second stamp of the same stage is ignored.</summary>
    internal void Mark(CancelStage stage)
    {
        var now = Stopwatch.GetTimestamp();
        if (now == 0)
        {
            now = 1;
        }

        if (Interlocked.CompareExchange(ref _timestamps[(int)stage], now, 0) != 0)
        {
            return;
        }

        // Pool state is what separates "the kill was slow" from "nothing was free to run the
        // continuation", so it is captured at the two stages whose gap that distinction explains.
        if (stage == CancelStage.Signalled)
        {
            _poolAtSignalled = PoolSnapshot();
        }
        else if (stage == CancelStage.Observed)
        {
            _poolAtObserved = PoolSnapshot();
        }
    }

    /// <summary>Records that the tree-kill guard swallowed an exception, by TYPE name only.</summary>
    internal void NoteKillSwallowed(Exception error) =>
        Interlocked.CompareExchange(ref _killSwallowed, error.GetType().FullName, null);

    /// <summary>The type name of the exception the tree-kill guard swallowed, if it swallowed one.</summary>
    internal string? KillSwallowedType => Volatile.Read(ref _killSwallowed);

    /// <summary>The raw timestamp of <paramref name="stage"/>, or <see langword="null"/> if not reached.</summary>
    internal long? TimestampOf(CancelStage stage)
    {
        var value = Volatile.Read(ref _timestamps[(int)stage]);
        return value == 0 ? null : value;
    }

    /// <summary>Whether <paramref name="stage"/> has been reached.</summary>
    internal bool Reached(CancelStage stage) => TimestampOf(stage) is not null;

    /// <summary>
    /// Renders every stage reached, in chronological order with the gap since the previous one,
    /// then every stage not reached, so a report taken while <c>Run</c> is still outstanding still
    /// says how far it got.
    /// </summary>
    internal string Describe()
    {
        var report = Stopwatch.GetTimestamp();
        var reached = new List<(CancelStage Stage, long At)>();
        var missing = new List<CancelStage>();
        foreach (var stage in Enum.GetValues<CancelStage>())
        {
            if (TimestampOf(stage) is { } at)
            {
                reached.Add((stage, at));
            }
            else
            {
                missing.Add(stage);
            }
        }

        reached.Sort(static (left, right) => left.At.CompareTo(right.At));

        var text = new StringBuilder("Cancel-path stage timings (ms; '+' is the gap since the previous stage, 't=' is since the token was signalled): ");
        var origin = TimestampOf(CancelStage.Signalled);
        long? previous = null;
        foreach (var (stage, at) in reached)
        {
            var gap = previous is { } p ? Stopwatch.GetElapsedTime(p, at).TotalMilliseconds : 0;
            var sinceSignal = origin is { } o ? Stopwatch.GetElapsedTime(o, at).TotalMilliseconds : double.NaN;
            text.Append(CultureInfo.InvariantCulture, $"{stage} +{gap:F1} (t={sinceSignal:F1}); ");
            previous = at;
        }

        if (missing.Count > 0)
        {
            var sinceSignalNow = origin is { } o2 ? Stopwatch.GetElapsedTime(o2, report).TotalMilliseconds : double.NaN;
            text.Append(CultureInfo.InvariantCulture, $"NOT REACHED as of t={sinceSignalNow:F1}: {string.Join(", ", missing)}; ");
        }

        text.Append(CultureInfo.InvariantCulture, $"kill swallowed: {KillSwallowedType ?? "nothing"}; ");
        text.Append(CultureInfo.InvariantCulture, $"thread pool at signal: {_poolAtSignalled ?? "n/a"}; at observe: {_poolAtObserved ?? "n/a"}.");
        return text.ToString();
    }

    private static string PoolSnapshot() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"threads={ThreadPool.ThreadCount} pending={ThreadPool.PendingWorkItemCount} cpus={Environment.ProcessorCount}");
}
