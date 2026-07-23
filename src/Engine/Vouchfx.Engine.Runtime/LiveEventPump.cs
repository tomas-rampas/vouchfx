// Vouchfx.Engine.Runtime — LiveEventPump (§14, issue #262).
//
// A bounded, non-blocking conduit wrapping the existing Reporting.EventStreamAppender (#258).
// Posting a line NEVER blocks, awaits, or throws — Post/PostRange are called from the step
// thread (via LiveStepEventSink, itself called from inside the emitted CSX's isolated run) and
// must never make a step wait on disk I/O or a slow tailing reader.
//
// Overflow policy (DECIDED, §5 design): drop the newest line rather than block, keep a
// dropped-line counter, and emit exactly ONE end-of-run diagnostic if any line was dropped.
// A SEPARATE counter + diagnostic covers the distinct case of a batch faulting while being
// appended (DrainAsync's inner try/catch) — the two are never conflated, since "buffer full"
// and "append threw" have different causes and different remediations.
// The `--events` archive stays the authoritative, complete record (unaffected — ScenarioRunner
// still writes it once, at the end, from the reconstructed buffer); the `--events-stream` file
// this type feeds is an explicit best-effort live tail.
//
// Implementation note on the channel's FullMode: this conduit only ever calls the
// non-blocking, synchronous Writer.TryWrite — never WriteAsync / WaitToWriteAsync — so
// FullMode's *waiting* behaviour is never exercised regardless of which value is configured.
// What DOES matter is TryWrite's return value when the channel is full, and
// BoundedChannelFullMode.DropWrite is specified to make TryWrite return true unconditionally
// (the runtime silently discards the item instead of enqueuing it) — which would make a
// caller-side "count what TryWrite rejected" scheme silently under-count every drop. Using
// BoundedChannelFullMode.Wait instead gives the IDENTICAL observable Post behaviour (still
// fully non-blocking — TryWrite never waits under any FullMode) while making TryWrite return
// false exactly when the item cannot be enqueued, which is what Post below counts as a drop.
//
// Memory-model note (§5): this type and everything it holds (the Channel<string>, the drain
// Task, the wrapped EventStreamAppender) live in the Default ALC. The channel carries only
// already-serialised JSON strings — never a StepOutcome/AttemptRecord instance, never globals,
// never anything that could reach the collectible submission assembly. Holding a reference to
// this pump (via LiveStepEventSink, via ScriptGlobalVariables.StepEvents) therefore cannot root
// the collectible AssemblyLoadContext.
using System.Threading.Channels;
using Vouchfx.Engine.Reporting;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Bounded-channel, non-blocking conduit over <see cref="EventStreamAppender"/> (issue #262).
/// </summary>
/// <remarks>
/// <para>
/// A single background task drains the channel, batching every currently-available line into
/// one <see cref="EventStreamAppender.AppendLines"/> call per drain pass — one flush per batch,
/// not one per line. <see cref="Post"/> / <see cref="PostRange"/> are synchronous, non-blocking,
/// and never throw: a full channel (capacity exceeded because the drain task cannot keep up)
/// silently drops the newest line and increments a counter, rather than making the caller wait.
/// </para>
/// <para>
/// <see cref="DisposeAsync"/> completes the channel writer, awaits the drain task to finish
/// flushing whatever remains, emits ONE diagnostic if any line was ever dropped, then disposes
/// the wrapped appender.
/// </para>
/// </remarks>
public sealed class LiveEventPump : IAsyncDisposable
{
    /// <summary>
    /// The default bounded-channel capacity (§5 design decision).
    /// </summary>
    public const int DefaultCapacity = 2048;

    private readonly Channel<string> _channel;
    private readonly Action<IReadOnlyList<string>> _appendBatch;
    private readonly IAsyncDisposable? _ownedAppender;
    private readonly TextWriter? _diagnostics;
    private readonly Task _drainTask;
    private long _dropped;
    private long _drainFaults;
    private int _disposed;

    /// <summary>
    /// Opens <paramref name="path"/> via a new <see cref="EventStreamAppender"/> and starts the
    /// background drain task, with the default <see cref="DefaultCapacity"/>.
    /// </summary>
    /// <param name="path">The destination file for the incremental JSON Lines stream.</param>
    /// <param name="diagnostics">
    /// An optional sink for the appender's own open/append/close diagnostics (unchanged from
    /// #258) and for this pump's own one-line "N line(s) dropped" notice at
    /// <see cref="DisposeAsync"/>. <see langword="null"/> discards every notice.
    /// </param>
    public LiveEventPump(string path, TextWriter? diagnostics = null)
        : this(BuildOwnedAppenderConsumer(path, diagnostics, out var appender), appender, diagnostics, DefaultCapacity)
    {
    }

    /// <summary>
    /// Test-only seam (issue #262): injects a synthetic batch consumer and an arbitrary
    /// capacity, so a blocked "drain" can be simulated deterministically — no real file I/O,
    /// no timing race — to prove the overflow (DropWrite) policy.
    /// </summary>
    /// <param name="appendBatch">
    /// Invoked once per drain pass with every line currently available in the channel, in FIFO
    /// order. Must never be <see langword="null"/>.
    /// </param>
    /// <param name="capacity">The bounded-channel capacity.</param>
    /// <param name="diagnostics">Optional sink for the dropped-line notice at disposal.</param>
    internal LiveEventPump(Action<IReadOnlyList<string>> appendBatch, int capacity, TextWriter? diagnostics = null)
        : this(appendBatch, null, diagnostics, capacity)
    {
    }

    private LiveEventPump(
        Action<IReadOnlyList<string>> appendBatch,
        IAsyncDisposable? ownedAppender,
        TextWriter? diagnostics,
        int capacity)
    {
        _appendBatch = appendBatch ?? throw new ArgumentNullException(nameof(appendBatch));
        _ownedAppender = ownedAppender;
        _diagnostics = diagnostics;

        // FullMode.Wait — see the file-header note: TryWrite (the only writer method this type
        // ever calls) stays fully non-blocking regardless, and this is the setting under which
        // it reliably reports "not enqueued" via its return value so Post's drop-counting works.
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _drainTask = Task.Run(DrainAsync);
    }

    private static Action<IReadOnlyList<string>> BuildOwnedAppenderConsumer(
        string path, TextWriter? diagnostics, out EventStreamAppender appender)
    {
        appender = new EventStreamAppender(path, diagnostics);
        var captured = appender;
        return lines => captured.AppendLines(lines);
    }

    /// <summary>
    /// Posts a single line. Never blocks, never awaits, and never throws — including when
    /// <paramref name="line"/> is <see langword="null"/>, which is treated as a silent no-op
    /// (issue #262 review): a step thread must never risk an exception from what is a
    /// best-effort, fire-and-forget diagnostic call. If the channel is full (the drain task
    /// cannot keep up), the line is silently dropped and the drop counter is incremented — a
    /// step thread must never wait on disk I/O or a slow tailing reader either.
    /// </summary>
    /// <param name="line">
    /// The already-serialised JSON Lines record, or <see langword="null"/> (a no-op).
    /// </param>
    public void Post(string? line)
    {
        if (line is null)
        {
            return;
        }

        if (!_channel.Writer.TryWrite(line))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Posts every line in <paramref name="lines"/>, in order, via repeated <see cref="Post"/>
    /// calls. Never blocks, never awaits, and never throws — including when
    /// <paramref name="lines"/> itself is <see langword="null"/> (a no-op) or contains
    /// <see langword="null"/> elements (each individually skipped, per <see cref="Post"/>).
    /// </summary>
    /// <param name="lines">
    /// The lines to post, in order, or <see langword="null"/> (a no-op).
    /// </param>
    public void PostRange(IReadOnlyList<string?>? lines)
    {
        if (lines is null)
        {
            return;
        }

        foreach (var line in lines)
        {
            Post(line);
        }
    }

    /// <summary>
    /// The number of lines dropped so far due to a full channel. Exposed <c>internal</c> for
    /// the overflow unit tests; production code has no need to read this mid-run (the single
    /// end-of-run diagnostic at <see cref="DisposeAsync"/> is the production-facing signal).
    /// </summary>
    internal long DroppedCount => Interlocked.Read(ref _dropped);

    private async Task DrainAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<string>();

        // The try/catch wraps ONLY the _appendBatch call, not the whole loop: a transient
        // append fault (a misbehaving test consumer, an unexpected exception from the wrapped
        // appender) must not permanently kill draining for the rest of the run — the reader
        // must keep servicing WaitToReadAsync/TryRead on every subsequent pass. Wrapping the
        // whole loop would exit it on the first fault, silently going dead: later Posts would
        // then increment _dropped for every remaining line, producing a misleading
        // "dropped N lines (full buffer)" diagnostic at DisposeAsync when the real cause was a
        // dead drain loop, not overflow.
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (reader.TryRead(out var line))
            {
                batch.Add(line);
            }

            if (batch.Count > 0)
            {
                try
                {
                    _appendBatch(batch);
                }
                catch
                {
                    // Best-effort: a fault appending ONE batch must not throw into
                    // DisposeAsync's await, and must not stop the loop from continuing to
                    // drain subsequent batches. The lines in this particular batch are lost
                    // (no worse than the overflow policy already accepts for a dropped line),
                    // but the conduit itself stays alive.
                    Interlocked.Increment(ref _drainFaults);
                }
            }
        }
    }

    /// <summary>
    /// Completes the channel writer, awaits the drain task to flush whatever remains, emits
    /// ONE diagnostic if any line was ever dropped (full-buffer overflow) and/or ONE SEPARATE
    /// diagnostic if any batch ever faulted while appending (a distinct failure mode — see
    /// <see cref="DrainAsync"/>), then disposes the wrapped appender (when this instance owns
    /// one). Safe to call more than once; never throws.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        try
        {
            await _drainTask.ConfigureAwait(false);
        }
        catch
        {
            // The drain task itself already swallows every fault internally; this is
            // defence-in-depth so disposal can never throw regardless.
        }

        // Two DISTINCT diagnostics, deliberately not conflated: a drop means the channel was
        // full (the drain couldn't keep up with the volume); a drain fault means an append
        // itself threw (the drain kept running, but that one batch's lines were lost). Reporting
        // them separately keeps "full buffer" from being a misleading explanation for what was
        // actually an append-time exception, and vice versa.
        var dropped = Interlocked.Read(ref _dropped);
        if (dropped > 0)
        {
            _diagnostics?.WriteLine(
                $"Live events-stream pump dropped {dropped} line(s) due to a full buffer " +
                "(verdict unaffected; the --events archive is unaffected and complete).");
        }

        var drainFaults = Interlocked.Read(ref _drainFaults);
        if (drainFaults > 0)
        {
            _diagnostics?.WriteLine(
                $"Live events-stream pump failed to append {drainFaults} batch(es) due to an " +
                "unexpected error (verdict unaffected; the --events archive is unaffected and " +
                "complete).");
        }

        if (_ownedAppender is not null)
        {
            await _ownedAppender.DisposeAsync().ConfigureAwait(false);
        }
    }
}
