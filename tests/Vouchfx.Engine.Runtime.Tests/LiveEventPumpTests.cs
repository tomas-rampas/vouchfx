// Tests for LiveEventPump (issue #262). NO DOCKER — exercises the bounded-channel conduit
// directly, with either a real temp-file-backed appender (functional tests) or the internal
// synthetic-consumer seam (the overflow test) — no topology, no Roslyn compile involved.
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Runtime;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// No-docker unit tests for <see cref="LiveEventPump"/>: functional Post/PostRange behaviour
/// against a real temp file, and the DropWrite overflow policy via the internal synthetic-batch
/// seam (issue #262).
/// </summary>
public sealed class LiveEventPumpTests
{
    // ── Functional: Post/PostRange land in the file, in order, LF-terminated ──────────────

    [Fact]
    public async Task Post_SingleLine_AppearsInFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "vouchfx-live-pump-" + Guid.NewGuid().ToString("n") + ".jsonl");
        try
        {
            await using (var pump = new LiveEventPump(path))
            {
                pump.Post("{\"a\":1}");
                // Give the background drain task a moment to observe and flush the write —
                // DisposeAsync below is the deterministic wait point, but a functional smoke
                // check before dispose proves the conduit does not require disposal to flush.
                await WaitUntilAsync(() => File.Exists(path) && new FileInfo(path).Length > 0);
            }

            var content = await File.ReadAllTextAsync(path);
            Assert.Equal("{\"a\":1}\n", content);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static readonly string[] s_threeLines = { "{\"a\":1}", "{\"a\":2}", "{\"a\":3}" };

    [Fact]
    public async Task PostRange_MultipleLines_AppearInOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), "vouchfx-live-pump-" + Guid.NewGuid().ToString("n") + ".jsonl");
        try
        {
            await using (var pump = new LiveEventPump(path))
            {
                pump.PostRange(s_threeLines);
            }

            var content = await File.ReadAllTextAsync(path);
            Assert.Equal("{\"a\":1}\n{\"a\":2}\n{\"a\":3}\n", content);
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ── Null-tolerance: Post/PostRange are documented never-throw, including on null input ──

    [Fact]
    public async Task Post_NullLine_IsANoOp_NeverThrows()
    {
        var collected = new List<string>();
        await using var pump = new LiveEventPump(batch => collected.AddRange(batch), capacity: 8);

        pump.Post(null);
        pump.Post("{\"a\":1}");
        await pump.DisposeAsync();

        Assert.Single(collected);
        Assert.Equal("{\"a\":1}", collected[0]);
    }

    [Fact]
    public async Task PostRange_NullLines_IsANoOp_NeverThrows()
    {
        var collected = new List<string>();
        await using var pump = new LiveEventPump(batch => collected.AddRange(batch), capacity: 8);

        pump.PostRange(null);
        await pump.DisposeAsync();

        Assert.Empty(collected);
    }

    private static readonly string?[] s_linesWithNullElement = { "{\"a\":1}", null, "{\"a\":2}" };
    private static readonly string[] s_expectedAfterSkippingNulls = { "{\"a\":1}", "{\"a\":2}" };

    [Fact]
    public async Task PostRange_ContainingNullElements_SkipsOnlyTheNullOnes()
    {
        var collected = new List<string>();
        await using var pump = new LiveEventPump(batch => collected.AddRange(batch), capacity: 8);

        pump.PostRange(s_linesWithNullElement);
        await pump.DisposeAsync();

        Assert.Equal(s_expectedAfterSkippingNulls, collected);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_NeverThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), "vouchfx-live-pump-" + Guid.NewGuid().ToString("n") + ".jsonl");
        try
        {
            var pump = new LiveEventPump(path);
            pump.Post("{\"a\":1}");
            await pump.DisposeAsync();
            // Second dispose must be a silent no-op, not throw.
            await pump.DisposeAsync();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task NoLinesPosted_DisposeAsync_WritesNoDiagnostic()
    {
        var path = Path.Combine(Path.GetTempPath(), "vouchfx-live-pump-" + Guid.NewGuid().ToString("n") + ".jsonl");
        var diagnostics = new StringWriter();
        try
        {
            await using (var pump = new LiveEventPump(path, diagnostics))
            {
                // No Post/PostRange calls at all.
            }

            Assert.Equal(string.Empty, diagnostics.ToString());
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ── Overflow: DropWrite policy via the internal synthetic-consumer seam ───────────────

    /// <summary>
    /// Builds a synthetic batch consumer that PROVABLY parks the single drain thread inside
    /// itself the moment it is first invoked (it signals <paramref name="readerEntered"/> BEFORE
    /// blocking on <paramref name="releaseReader"/>), so a caller can deterministically post past
    /// capacity while the reader is known to be stuck — no reliance on thread-pool scheduling
    /// races. Without this handshake, the drain task's inner read loop can race ahead of a tight
    /// posting loop and drain every line before ever blocking, hiding the overflow entirely.
    /// </summary>
    private static Action<IReadOnlyList<string>> MakeParkingConsumer(
        ManualResetEventSlim readerEntered, ManualResetEventSlim releaseReader, List<string> appended)
    {
        return batch =>
        {
            readerEntered.Set();
            releaseReader.Wait();
            appended.AddRange(batch);
        };
    }

    /// <summary>
    /// With capacity 1 and the drain thread PROVABLY parked (via the handshake in
    /// <see cref="MakeParkingConsumer"/>), posting many more lines than the capacity must NEVER
    /// block or throw — every call to <see cref="LiveEventPump.Post"/> returns immediately — and
    /// the dropped-line counter must have incremented.
    /// </summary>
    [Fact]
    public async Task Post_CapacityExceededWithBlockedDrain_NeverBlocksOrThrows_AndDropsIncrement()
    {
        var readerEntered = new ManualResetEventSlim(false);
        var releaseReader = new ManualResetEventSlim(false);
        var appended = new List<string>();

        var pump = new LiveEventPump(MakeParkingConsumer(readerEntered, releaseReader, appended), capacity: 1);

        // Prime the drain: post one line so the reader picks it up and parks inside the consumer.
        pump.Post("priming-line");
        Assert.True(
            readerEntered.Wait(TimeSpan.FromSeconds(5)),
            "The drain thread never entered the consumer callback.");

        // The reader is now PROVABLY blocked (it signalled readerEntered BEFORE waiting on
        // releaseReader), so it cannot call TryRead again until released — every Post below
        // genuinely exercises the bounded channel, deterministically.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
        {
            pump.Post($"line-{i}");
        }
        sw.Stop();

        // Post must never block: 500 synchronous TryWrite calls complete near-instantly.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"Post appeared to block: {sw.ElapsedMilliseconds}ms for 500 calls.");

        // At least one line must have been dropped (capacity 1, 500 posts, reader parked).
        Assert.True(pump.DroppedCount > 0, "Expected at least one dropped line under overflow.");

        releaseReader.Set();
        await pump.DisposeAsync(); // no diagnostics sink attached to THIS pump instance.
    }

    /// <summary>
    /// Exactly ONE dispose-time diagnostic is written when lines were dropped — never one per
    /// drop, never one per remaining Post call.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_AfterDrops_WritesExactlyOneDiagnostic()
    {
        var readerEntered = new ManualResetEventSlim(false);
        var releaseReader = new ManualResetEventSlim(false);
        var appended = new List<string>();
        var diagnostics = new StringWriter();

        var pump = new LiveEventPump(
            MakeParkingConsumer(readerEntered, releaseReader, appended), capacity: 1, diagnostics);

        pump.Post("priming-line");
        Assert.True(readerEntered.Wait(TimeSpan.FromSeconds(5)));

        for (var i = 0; i < 50; i++)
        {
            pump.Post($"line-{i}");
        }

        Assert.True(pump.DroppedCount > 0, "Expected drops before disposal.");

        releaseReader.Set();
        await pump.DisposeAsync();

        var diagText = diagnostics.ToString();
        Assert.Contains("dropped", diagText, StringComparison.OrdinalIgnoreCase);
        // Exactly one diagnostic line — DisposeAsync writes at most once.
        var lines = diagText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(10);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
