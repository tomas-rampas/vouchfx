// Vouchfx.Cli — StdinShutdownWatcher (vouchfx-mcp#17).
//
// The background half of the opt-in `--shutdown-on-stdin-eof` graceful-shutdown seam: watches
// an input stream to end-of-file (or a read error) and, when that happens, invokes a
// caller-supplied callback exactly once. RunCommand.ExecuteAsync wires that callback to cancel a
// LINKED CancellationTokenSource — propagating cancellation the SAME way a Ctrl-C / SIGTERM does —
// so an MCP host (or any process that spawns this CLI with a redirected standard input) can
// request a graceful stop simply by closing the child's stdin. Cancellation PROPAGATION is where
// the parity with Ctrl-C / SIGTERM ends, though: linking only propagates downstream, so it never
// touches the process's OWN termination-enforcement watchdog. RunCommand.ExecuteAsync's wiring
// also arms a SEPARATE ShutdownBackstop (its own wall-clock force-exit timer) from this same
// callback — see ShutdownBackstop's remarks for why that backstop exists and how it is scoped.
//
// Testable via a seam: the constructor takes any Stream (production: Console.OpenStandardInput();
// tests: an in-memory stream or a small cooperative test double) and a plain Action callback — no
// dependency on CancellationTokenSource or the real console, so EOF / read-error / dispose
// behaviour can be exercised without touching stdin.

namespace Vouchfx.Cli;

/// <summary>
/// Watches an input <see cref="Stream"/> to end-of-file (or a read error) in the background and,
/// when that happens, invokes a caller-supplied callback exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Reserved for the OPT-IN <c>--shutdown-on-stdin-eof</c> flag (default off): when off, nothing
/// in <see cref="RunCommand"/> constructs this type at all, so behaviour is byte-for-byte
/// identical to today. When on, <see cref="RunCommand.ExecuteAsync"/> starts exactly one watcher
/// over <see cref="Console.OpenStandardInput()"/> and wires its callback to cancel a linked
/// <see cref="CancellationTokenSource"/> — propagating cancellation the SAME way a Ctrl-C /
/// SIGTERM does (the engine's teardown discipline, §4.5). This is cancellation-PROPAGATION
/// parity only, <strong>not</strong> termination-ENFORCEMENT parity: cancelling a linked source
/// never touches the ORIGINAL System.CommandLine token, so
/// <c>InvocationConfiguration.ProcessTerminationTimeout</c> (see Program.cs) — armed only by a
/// real OS Ctrl-C/SIGTERM — is never engaged by this path. <see cref="RunCommand.ExecuteAsync"/>'s
/// wiring therefore also arms a dedicated <c>ShutdownBackstop</c> from the SAME callback, giving
/// a stdin-EOF shutdown its own bounded force-exit guarantee instead.
/// </para>
/// <para>
/// The read loop runs on a background <see cref="Task"/> and is never awaited by the caller's
/// main run — only a single byte at a time is read (its VALUE is irrelevant; only its arrival,
/// or the stream's end, matters), so the loop is cheap and never buffers meaningfully.
/// </para>
/// <para>
/// <see cref="DisposeAsync"/> requests a clean stop (so a cooperative stream — a test double, or
/// a real pipe the caller itself controls — unwinds almost immediately) but deliberately never
/// BLOCKS waiting for that: a genuine <see cref="Console"/> standard-input stream's blocking OS
/// read is a well-known .NET limitation that a <see cref="CancellationToken"/> cannot always
/// interrupt. An abandoned read in that case is harmless — the process either keeps running (the
/// watcher was disposed because the run already finished) or is about to exit anyway, and the OS
/// reaps the thread either way.
/// </para>
/// </remarks>
internal sealed class StdinShutdownWatcher : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Action _onShutdownRequested;
    private readonly CancellationTokenSource _stopSource = new();
    private readonly Task _readLoop;
    private bool _disposed;

    private StdinShutdownWatcher(Stream input, Action onShutdownRequested)
    {
        _input = input;
        _onShutdownRequested = onShutdownRequested;

        // Capture the token EAGERLY, here in the constructor, rather than reading
        // `_stopSource.Token` lazily inside the Task.Run lambda below. Task.Run only SCHEDULES
        // that lambda — under thread-pool contention it may not actually run until AFTER
        // DisposeAsync has already cancelled AND disposed `_stopSource`, and
        // CancellationTokenSource.Token's getter throws ObjectDisposedException once disposed.
        // A CancellationToken VALUE (unlike the source) remains perfectly usable — readable and
        // awaitable — even after its source is disposed, so capturing it now closes that race.
        var stopToken = _stopSource.Token;
        _readLoop = Task.Run(() => ReadLoopAsync(stopToken));
    }

    /// <summary>
    /// Starts watching <paramref name="input"/> in the background.
    /// </summary>
    /// <param name="input">
    /// The stream to read to end-of-file. Production passes <see cref="Console.OpenStandardInput()"/>;
    /// tests pass any <see cref="Stream"/> (an in-memory stream, or a small cooperative test
    /// double) — ownership of <paramref name="input"/> stays with the caller; this type never
    /// disposes it.
    /// </param>
    /// <param name="onShutdownRequested">
    /// Invoked exactly once, on the background thread, when end-of-file (or a read error) is
    /// observed — never on a deliberate <see cref="DisposeAsync"/> stop. Any exception the
    /// callback itself throws is swallowed (belt-and-braces: a background watcher must never
    /// crash the process).
    /// </param>
    /// <returns>The started watcher; dispose it when the run finishes to stop the read loop.</returns>
    public static StdinShutdownWatcher Start(Stream input, Action onShutdownRequested) =>
        new(input, onShutdownRequested);

    /// <summary>
    /// Exposed for tests: completes when the background read loop has returned, whether by
    /// observing end-of-file / a read error (having invoked the callback) or by observing a
    /// <see cref="DisposeAsync"/> stop request. Never faults — every exception inside the loop is
    /// caught internally.
    /// </summary>
    internal Task ReaderCompletion => _readLoop;

    private async Task ReadLoopAsync(CancellationToken stopToken)
    {
        var buffer = new byte[1];
        var stoppedDeliberately = false;
        try
        {
            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _input.ReadAsync(buffer.AsMemory(0, 1), stopToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // DisposeAsync asked us to stop: a deliberate stop, never a shutdown signal.
                    stoppedDeliberately = true;
                    break;
                }
                catch
                {
                    // Any OTHER read failure is treated the same as end-of-file: the input can no
                    // longer be read, so the graceful-shutdown callback should fire rather than
                    // this loop silently going quiet forever.
                    break;
                }

                if (bytesRead == 0)
                {
                    break; // Genuine end-of-file.
                }

                // Discard the byte and keep reading; only its arrival (or the stream's end)
                // matters here — the watcher has no protocol to speak, just liveness to observe.
            }
        }
        catch
        {
            // The loop body above already handles its own read failures; this is an extra guard
            // so the background task can NEVER surface an unobserved exception — it must always
            // be safely reapable at process exit either way.
        }

        if (!stoppedDeliberately && !stopToken.IsCancellationRequested)
        {
            InvokeCallbackSafely();
        }
    }

    private void InvokeCallbackSafely()
    {
        try
        {
            _onShutdownRequested();
        }
        catch
        {
            // The caller's callback (cancelling its own linked CTS) is not expected to throw, but
            // a background watcher must never crash the process on account of it either way.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stopSource.Cancel();

        // Best-effort clean stop: if the read loop has ALREADY finished (e.g. a cooperative
        // stream reacted promptly, or EOF had already been observed), reap it inline so its
        // outcome is properly observed. Otherwise — a genuinely blocking read that the stop
        // token could not interrupt — do NOT wait for it: quietly observe its eventual result in
        // the background (so it can never become an unobserved-task-exception) and return
        // immediately. DisposeAsync must never block a normal run's exit on stdin's own timing.
        //
        // EITHER way, `_stopSource` is only disposed ONCE the read loop has actually finished
        // with it (inline here, or in the background continuation below) — never a moment
        // earlier. `_readLoop`'s own delegate was scheduled via Task.Run and may not start
        // running until well after this method returns, so disposing `_stopSource` any sooner
        // risks the loop's underlying read (e.g. Task.Delay/ReadAsync) still registering a NEW
        // callback against an ALREADY-disposed CancellationTokenSource.
        if (_readLoop.IsCompleted)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch
            {
                // Never throw from Dispose; the loop's own body already swallows its faults —
                // this is belt-and-braces only.
            }

            _stopSource.Dispose();
        }
        else
        {
            _ = _readLoop.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception; // Observe: never an unobserved-task-exception.
                    ((CancellationTokenSource)state!).Dispose();
                },
                _stopSource,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
