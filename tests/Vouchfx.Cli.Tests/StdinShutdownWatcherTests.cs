// Vouchfx.Cli.Tests — StdinShutdownWatcher tests (vouchfx-mcp#17). No Docker, no Console.
//
// StdinShutdownWatcher is the background half of the opt-in --shutdown-on-stdin-eof seam: it
// watches an input Stream to end-of-file (or a read error) and invokes a caller-supplied
// callback exactly once when that happens — never on a deliberate DisposeAsync stop. Every test
// here drives it via the injectable Stream seam (never the real Console), so behaviour is
// exercised deterministically and cross-platform:
//   - a closed/empty stream (immediate EOF) triggers the callback;
//   - a read failure is treated the same as EOF (also triggers the callback);
//   - a stream that never EOFs (and cooperatively honours cancellation) does NOT trigger the
//     callback, and DisposeAsync stops it cleanly (no throw, no false-positive callback).

using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class StdinShutdownWatcherTests
{
    // A cooperative test double that never reaches end-of-file on its own, but honours the
    // CancellationToken passed to ReadAsync (as a real, well-behaved async Stream should) — so a
    // deliberate DisposeAsync stop unwinds it promptly without ever reporting a byte or an EOF.
    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Blocks cooperatively until the caller cancels — simulates a stdin that is never
            // closed and never receives data, without touching the real console or any I/O.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0; // Unreachable: Task.Delay(Timeout.Infinite, ...) only returns via cancellation.
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // A test double whose read always fails — proving a read ERROR is treated the same as EOF
    // (the watcher's documented contract), without needing a real broken pipe.
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("simulated broken pipe");

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Start_StreamAlreadyAtEof_InvokesCallbackExactlyOnce()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        var callbackCount = 0;

        await using var watcher = StdinShutdownWatcher.Start(stream, () => Interlocked.Increment(ref callbackCount));

        await watcher.ReaderCompletion.WaitAsync(WaitBound);

        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public async Task Start_ReadThrows_IsTreatedAsShutdownSignal_InvokesCallback()
    {
        using var stream = new ThrowingStream();
        var triggered = false;

        await using var watcher = StdinShutdownWatcher.Start(stream, () => triggered = true);

        await watcher.ReaderCompletion.WaitAsync(WaitBound);

        Assert.True(triggered);
    }

    [Fact]
    public async Task Start_StreamNeverEofs_DoesNotInvokeCallback()
    {
        using var stream = new NeverEndingStream();
        var triggered = false;

        await using var watcher = StdinShutdownWatcher.Start(stream, () => triggered = true);

        // Give the background loop a moment to (incorrectly) fire before we assert it hasn't —
        // NeverEndingStream blocks forever on its own, so this only guards against a bug that
        // fires the callback spuriously right after Start.
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.False(triggered);
    }

    [Fact]
    public async Task DisposeAsync_StreamNeverEofs_StopsCleanly_WithoutThrowing_AndWithoutCancelling()
    {
        using var stream = new NeverEndingStream();
        var triggered = false;

        var watcher = StdinShutdownWatcher.Start(stream, () => triggered = true);

        // Must not throw.
        await watcher.DisposeAsync();

        // The cooperative stream honours the stop token, so the read loop unwinds promptly (a
        // "clean stop") — confirmed by awaiting its completion with a bound, rather than assuming.
        await watcher.ReaderCompletion.WaitAsync(WaitBound);

        // A deliberate stop must NEVER be reported as a shutdown request.
        Assert.False(triggered);
    }

    [Fact]
    public async Task DisposeAsync_AlreadyCompletedLoop_IsIdempotent_DoesNotThrow()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        var watcher = StdinShutdownWatcher.Start(stream, () => { });

        await watcher.ReaderCompletion.WaitAsync(WaitBound);

        // Disposing after the loop already finished (EOF) — and disposing TWICE — must both be
        // silent no-ops, never throw.
        await watcher.DisposeAsync();
        await watcher.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_DoesNotBlockOnANonCooperativeRead()
    {
        // Regression guard for the exact failure mode the design explicitly calls out: a stream
        // whose ReadAsync ignores the CancellationToken entirely (the real Console standard-input
        // stream's well-known .NET limitation) must never wedge DisposeAsync — it has to return
        // promptly regardless, leaving the read abandoned rather than hanging shutdown.
        using var uncancellableStream = new UncancellableStream();
        var watcher = StdinShutdownWatcher.Start(uncancellableStream, () => { });

        var disposeTask = watcher.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(WaitBound));

        Assert.Same(disposeTask, completed);
        await disposeTask; // Propagate any failure (there should be none).
    }

    // A stream whose ReadAsync blocks forever and IGNORES its CancellationToken entirely —
    // modelling the real Console standard-input limitation that a token cannot always interrupt
    // a blocking OS read.
    private sealed class UncancellableStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Deliberately ignores cancellationToken (Timeout.Infinite, CancellationToken.None):
            // never returns on its own, and does not observe a stop request either.
            await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
