// Platform.Engine.Telemetry.Tests — OutboxStore (S12-G-01, Phase A).
//
// Proves the locked-read / atomic-rewrite / oldest-first cap behaviour:
//   • EnforceCap drops by max-lines, by max-bytes, and by max-age-days — always
//     oldest-first (the retained set is a newest-suffix of the input);
//   • a line with an unparseable timestamp is NOT dropped by the age rule;
//   • DrainUnderLockAsync rewrites the outbox to exactly the UNDELIVERED lines an atomic
//     rewrite preserves (criterion 7);
//   • when the sibling lock is already held, the operation SKIPS without corrupting the
//     outbox (the file is left exactly as it was);
//   • a drain that throws leaves the outbox intact.

using System.Text;
using Xunit;

namespace Platform.Engine.Telemetry.Tests;

public sealed class OutboxStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    // A timestamped outbox line, in the exact compact JSON-with-timestamp shape the local
    // sink writes (the cap reads the "timestamp" field for age eviction).
    private static string Line(int n, DateTimeOffset ts) =>
        $"{{\"n\":{n},\"timestamp\":\"{ts:o}\"}}";

    private static async Task WriteOutboxAsync(TempPaths paths, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OutboxPath)!);
        await File.WriteAllTextAsync(
            paths.OutboxPath,
            string.Concat(lines.Select(l => l + "\n")),
            new UTF8Encoding(false));
    }

    private static async Task<string[]> ReadOutboxAsync(TempPaths paths) =>
        File.Exists(paths.OutboxPath)
            ? await File.ReadAllLinesAsync(paths.OutboxPath)
            : Array.Empty<string>();

    [Fact]
    public async Task EnforceCap_MaxLines_DropsOldestFirst()
    {
        using var paths = new TempPaths();
        var lines = Enumerable.Range(1, 5).Select(i => Line(i, Now)).ToList();
        await WriteOutboxAsync(paths, lines);

        var store = new OutboxStore(paths, () => Now);
        var outcome = store.EnforceCap(new OutboxCap(MaxBytes: long.MaxValue, MaxAgeDays: 365, MaxLines: 3));

        Assert.True(outcome.LockAcquired);
        var remaining = await ReadOutboxAsync(paths);
        // The NEWEST 3 are kept; the oldest 2 (n=1, n=2) are dropped.
        Assert.Equal(new[] { Line(3, Now), Line(4, Now), Line(5, Now) }, remaining);
    }

    [Fact]
    public async Task EnforceCap_MaxBytes_KeepsNewestSuffixThatFits()
    {
        using var paths = new TempPaths();
        var lines = Enumerable.Range(1, 5).Select(i => Line(i, Now)).ToList();
        await WriteOutboxAsync(paths, lines);

        // Each line is the same length; size one line (+ '\n') and allow exactly two.
        var oneLineBytes = Encoding.UTF8.GetByteCount(lines[0]) + 1;

        var store = new OutboxStore(paths, () => Now);
        var outcome = store.EnforceCap(
            new OutboxCap(MaxBytes: oneLineBytes * 2, MaxAgeDays: 365, MaxLines: int.MaxValue));

        Assert.True(outcome.LockAcquired);
        var remaining = await ReadOutboxAsync(paths);
        // The newest 2 fit; the older 3 are evicted oldest-first.
        Assert.Equal(new[] { Line(4, Now), Line(5, Now) }, remaining);
    }

    [Fact]
    public async Task EnforceCap_MaxAgeDays_DropsLinesOlderThanCutoff()
    {
        using var paths = new TempPaths();
        var old1 = Line(1, Now.AddDays(-40));
        var old2 = Line(2, Now.AddDays(-31));
        var fresh1 = Line(3, Now.AddDays(-10));
        var fresh2 = Line(4, Now);
        await WriteOutboxAsync(paths, new[] { old1, old2, fresh1, fresh2 });

        var store = new OutboxStore(paths, () => Now);
        store.EnforceCap(new OutboxCap(MaxBytes: long.MaxValue, MaxAgeDays: 30, MaxLines: int.MaxValue));

        var remaining = await ReadOutboxAsync(paths);
        // Both >30-day lines drop; both within-window lines stay, in order.
        Assert.Equal(new[] { fresh1, fresh2 }, remaining);
    }

    [Fact]
    public async Task EnforceCap_UnparseableTimestamp_IsNotDroppedByAgeAlone()
    {
        using var paths = new TempPaths();
        // A line with no parseable "timestamp" must be kept by the age rule (never silently
        // evicted because we cannot date it).
        var undatable = "{\"n\":1,\"timestamp\":\"not-a-date\"}";
        var fresh = Line(2, Now);
        await WriteOutboxAsync(paths, new[] { undatable, fresh });

        var store = new OutboxStore(paths, () => Now);
        store.EnforceCap(new OutboxCap(MaxBytes: long.MaxValue, MaxAgeDays: 1, MaxLines: int.MaxValue));

        var remaining = await ReadOutboxAsync(paths);
        Assert.Equal(new[] { undatable, fresh }, remaining);
    }

    [Fact]
    public async Task EnforceCap_WithinAllCeilings_DoesNotRewrite()
    {
        using var paths = new TempPaths();
        var lines = Enumerable.Range(1, 3).Select(i => Line(i, Now)).ToList();
        await WriteOutboxAsync(paths, lines);

        var store = new OutboxStore(paths, () => Now);
        var outcome = store.EnforceCap(OutboxCap.Default);

        Assert.True(outcome.LockAcquired);
        Assert.Equal(3, outcome.SnapshotLineCount);
        Assert.Equal(3, outcome.RetainedLineCount);
        var remaining = await ReadOutboxAsync(paths);
        Assert.Equal(lines, remaining);
    }

    [Fact]
    public async Task DrainUnderLockAsync_RewritesOutboxToExactlyTheUndeliveredLines()
    {
        using var paths = new TempPaths();
        var lines = Enumerable.Range(1, 5).Select(i => Line(i, Now)).ToList();
        await WriteOutboxAsync(paths, lines);

        var store = new OutboxStore(paths, () => Now);

        // Simulate: the first three were delivered; the last two remain undelivered.
        var outcome = await store.DrainUnderLockAsync(
            OutboxCap.Default,
            (snapshot, _) =>
            {
                Assert.Equal(lines, snapshot);
                IReadOnlyList<string> undelivered = snapshot.Skip(3).ToList();
                return Task.FromResult(undelivered);
            },
            CancellationToken.None);

        Assert.True(outcome.Drained);
        Assert.Equal(5, outcome.SnapshotLineCount);
        Assert.Equal(2, outcome.RetainedLineCount);

        var remaining = await ReadOutboxAsync(paths);
        Assert.Equal(new[] { Line(4, Now), Line(5, Now) }, remaining);
    }

    [Fact]
    public async Task DrainUnderLockAsync_AllDelivered_EmptiesTheOutbox()
    {
        using var paths = new TempPaths();
        var lines = Enumerable.Range(1, 3).Select(i => Line(i, Now)).ToList();
        await WriteOutboxAsync(paths, lines);

        var store = new OutboxStore(paths, () => Now);
        await store.DrainUnderLockAsync(
            OutboxCap.Default,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            CancellationToken.None);

        var remaining = await ReadOutboxAsync(paths);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task DrainUnderLockAsync_DrainThrows_LeavesOutboxIntact()
    {
        using var paths = new TempPaths();
        var lines = Enumerable.Range(1, 3).Select(i => Line(i, Now)).ToList();
        await WriteOutboxAsync(paths, lines);

        var store = new OutboxStore(paths, () => Now);

        var outcome = await store.DrainUnderLockAsync(
            OutboxCap.Default,
            (_, _) => throw new IOException("simulated network blip in the drain callback"),
            CancellationToken.None);

        // The store swallows the file-class fault and reports a skip; the outbox is untouched.
        Assert.False(outcome.Drained);
        var remaining = await ReadOutboxAsync(paths);
        Assert.Equal(lines, remaining);
    }

    [Fact]
    public async Task DrainUnderLockAsync_LockHeld_SkipsWithoutTouchingTheOutbox()
    {
        using var paths = new TempPaths();
        var lines = Enumerable.Range(1, 3).Select(i => Line(i, Now)).ToList();
        await WriteOutboxAsync(paths, lines);

        // Hold the sibling lock exclusively to simulate a concurrent drainer/appender.
        var lockPath = paths.OutboxPath + ".lock";
        using (var held = new FileStream(
                   lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var store = new OutboxStore(paths, () => Now);
            var drainCalled = false;

            var outcome = await store.DrainUnderLockAsync(
                OutboxCap.Default,
                (_, _) =>
                {
                    drainCalled = true;
                    return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
                },
                CancellationToken.None);

            Assert.False(outcome.LockAcquired);
            Assert.False(drainCalled, "the drain must not run when the lock cannot be taken");
        }

        // The outbox is exactly as it was — no corruption, no truncation.
        var remaining = await ReadOutboxAsync(paths);
        Assert.Equal(lines, remaining);
    }

    [Fact]
    public async Task DrainUnderLockAsync_NoOutbox_ReportsNothingToDo()
    {
        using var paths = new TempPaths();
        var store = new OutboxStore(paths, () => Now);

        var outcome = await store.DrainUnderLockAsync(
            OutboxCap.Default,
            (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            CancellationToken.None);

        Assert.True(outcome.LockAcquired);
        Assert.False(outcome.Drained);
        Assert.Equal(0, outcome.SnapshotLineCount);
    }
}
