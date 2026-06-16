// Platform.Engine.Telemetry.Tests — DrainingTelemetrySink (S12-G-01, Phase A).
//
// The criterion-6 behaviour file.  Proves:
//   • the local append happens on EVERY SendAsync, regardless of HTTP outcome
//     (criterion 6 — no S10-G-04 regression);
//   • a drain clears the delivered lines on 2xx;
//   • a NON-2xx preserves the outbox (no clear), and back-off advances;
//   • back-off is respected: a drain is SKIPPED while now < nextAttemptUtc;
//   • an unconfigured endpoint ⇒ pure local-only behaviour (append + local-only cap, no
//     network);
//   • a drain fault never throws out of SendAsync.

using Xunit;

namespace Platform.Engine.Telemetry.Tests;

public sealed class DrainingTelemetrySinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static TelemetryEvent SampleEvent(int scenarioCount = 1) => new()
    {
        SchemaVersion = TelemetryEventBuilder.CurrentSchemaVersion,
        Timestamp = Now,
        InstallId = Guid.NewGuid(),
        ToolVersion = "1.0.0",
        EngineVersion = "1.0.0",
        DotnetVersion = ".NET 8.0.7",
        RunCount = 1,
        ScenarioCount = scenarioCount,
        StepVerdicts = new TelemetryVerdictCounts(),
        ScenarioVerdicts = new TelemetryVerdictCounts(),
        StepFamilies = new Dictionary<string, int>(),
        StepProviders = new Dictionary<string, int>(),
        StartupMs = 0,
        TimeToFirstTestMs = 0,
    };

    [Fact]
    public async Task SendAsync_UnconfiguredEndpoint_AppendsLocally_AndMakesNoNetworkCall()
    {
        using var paths = new TempPaths();
        var local = new LocalFileTelemetrySink(paths);

        // http == null ⇒ unconfigured: pure local-only (criterion 6).
        var sink = new DrainingTelemetrySink(
            local, http: null, paths, OutboxCap.Default, () => Now, () => 0.0);

        await sink.SendAsync(SampleEvent(1));
        await sink.SendAsync(SampleEvent(2));

        // The outbox has both lines, exactly as the bare local sink would have written them.
        var lines = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public async Task SendAsync_LocalAppendByteIdentical_ToBareLocalSink_Criterion6()
    {
        // Drive the bare local sink and the decorator (unconfigured) with the SAME event and
        // assert the resulting outbox bytes are identical — the regression guarantee.
        var evt = SampleEvent(7);

        using var bare = new TempPaths();
        await new LocalFileTelemetrySink(bare).SendAsync(evt);
        var bareBytes = await File.ReadAllBytesAsync(bare.OutboxPath);

        using var wrapped = new TempPaths();
        var sink = new DrainingTelemetrySink(
            new LocalFileTelemetrySink(wrapped), http: null, wrapped, OutboxCap.Default,
            () => Now, () => 0.0);
        await sink.SendAsync(evt);
        var wrappedBytes = await File.ReadAllBytesAsync(wrapped.OutboxPath);

        Assert.Equal(bareBytes, wrappedBytes);
    }

    [Fact]
    public async Task SendAsync_Configured2xx_ClearsDeliveredLines()
    {
        using var paths = new TempPaths();
        var local = new LocalFileTelemetrySink(paths);
        var http = new RecordingOutboxHttpClient().QueueAck(true);

        var sink = new DrainingTelemetrySink(
            local, http, paths, OutboxCap.Default, () => Now, () => 0.0);

        await sink.SendAsync(SampleEvent());

        // The single event was appended then delivered (2xx) ⇒ outbox drained to empty.
        var lines = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Empty(lines);
        Assert.Single(http.PostedBatches);

        // Back-off reset after a clean drain (next attempt allowed immediately).
        var state = OutboxDrainState.Read(paths.DrainStatePath);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.True(state.MayAttempt(Now));
    }

    [Fact]
    public async Task SendAsync_ConfiguredNon2xx_PreservesOutbox_AndAdvancesBackoff()
    {
        using var paths = new TempPaths();
        var local = new LocalFileTelemetrySink(paths);
        var http = new RecordingOutboxHttpClient().QueueAck(false); // server rejects

        var sink = new DrainingTelemetrySink(
            local, http, paths, OutboxCap.Default, () => Now, () => 0.0);

        await sink.SendAsync(SampleEvent());

        // The event stays in the outbox — a non-2xx must NOT clear it.
        var lines = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Single(lines);

        // Back-off advanced: one failure, next attempt pushed into the future.
        var state = OutboxDrainState.Read(paths.DrainStatePath);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.False(state.MayAttempt(Now));
        Assert.True(state.NextAttemptUtc >= Now + OutboxDrainState.DefaultBaseBackoff);
    }

    [Fact]
    public async Task SendAsync_WithinBackoffWindow_SkipsNetworkDrain_ButStillEnforcesCap()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OutboxPath)!);

        // Seed a back-off state whose next attempt is in the future.
        new OutboxDrainState
        {
            ConsecutiveFailures = 2,
            NextAttemptUtc = Now.AddHours(1),
        }.Write(paths.DrainStatePath);

        // Pre-seed an OVER-cap outbox (10 old lines) so the cap has work to do even though
        // the network drain is skipped.  A tight MaxLines:5 makes eviction observable.
        var seeded = string.Concat(
            Enumerable.Range(1, 10).Select(i => $"{{\"n\":{i},\"timestamp\":\"{Now:o}\"}}\n"));
        await File.WriteAllTextAsync(paths.OutboxPath, seeded);

        var local = new LocalFileTelemetrySink(paths);
        var http = new RecordingOutboxHttpClient().QueueAck(true);

        var sink = new DrainingTelemetrySink(
            local, http, paths,
            new OutboxCap(MaxBytes: long.MaxValue, MaxAgeDays: 365, MaxLines: 5),
            () => Now, () => 0.0);

        await sink.SendAsync(SampleEvent());

        // No NETWORK drain was attempted (still inside the back-off window)...
        Assert.Empty(http.PostedBatches);

        // ...but the local-only cap STILL ran: 10 seeded + 1 appended = 11 lines, evicted
        // oldest-first down to the MaxLines:5 ceiling.  Without the in-back-off cap this would
        // have grown to 11 (and onward, unbounded, until the back-off window finally elapsed).
        var lines = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Equal(5, lines.Length);

        // The newest 5 survive (oldest-first eviction): the 6th..10th seeded lines plus the
        // appended one.  Assert the very oldest seeded lines (n=1..6) are gone.
        var joined = string.Join('\n', lines);
        Assert.DoesNotContain("\"n\":1,", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("\"n\":6,", joined, StringComparison.Ordinal);

        // The back-off schedule is untouched by a cap-only pass (no drain ran).
        var state = OutboxDrainState.Read(paths.DrainStatePath);
        Assert.Equal(2, state.ConsecutiveFailures);
        Assert.Equal(Now.AddHours(1), state.NextAttemptUtc);
    }

    [Fact]
    public async Task SendAsync_DrainFault_NeverThrowsOutOfSendAsync_AndAppendSurvives()
    {
        using var paths = new TempPaths();
        var local = new LocalFileTelemetrySink(paths);

        // A client that throws an unexpected fault on POST.
        var http = new RecordingOutboxHttpClient
        {
            OnPost = (_, _) => throw new InvalidOperationException("boom in the transport"),
        };

        var sink = new DrainingTelemetrySink(
            local, http, paths, OutboxCap.Default, () => Now, () => 0.0);

        // Must not throw.
        await sink.SendAsync(SampleEvent());

        // The append survived the drain fault; the outbox still holds the event.
        var lines = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Single(lines);
    }

    [Fact]
    public async Task SendAsync_Configured_MultipleEvents_DeliveredAcrossRuns()
    {
        using var paths = new TempPaths();
        var local = new LocalFileTelemetrySink(paths);
        var http = new RecordingOutboxHttpClient().QueueAck(true);

        var sink = new DrainingTelemetrySink(
            local, http, paths, OutboxCap.Default, () => Now, () => 0.0);

        // First send: appends + drains (delivered) ⇒ empty.
        await sink.SendAsync(SampleEvent(1));
        Assert.Empty(await File.ReadAllLinesAsync(paths.OutboxPath));

        // Second send: appends a new line + drains it ⇒ empty again.
        await sink.SendAsync(SampleEvent(2));
        Assert.Empty(await File.ReadAllLinesAsync(paths.OutboxPath));

        // Two separate batches were posted (one per send).
        Assert.Equal(2, http.PostedBatches.Count);
    }

    [Fact]
    public async Task SendAsync_MultiBatch_StopsAtFirstNon2xx_KeepingThatBatchOnward()
    {
        // Seed > MaxBatchLines lines so the drain forms two batches; reject the SECOND.
        // Everything in (and after) the failing batch must be preserved; the first
        // delivered batch is dropped.
        using var paths = new TempPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OutboxPath)!);

        var total = DrainingTelemetrySink.MaxBatchLines + 50; // 550 → batch sizes 500 + 50
        var seeded = string.Concat(
            Enumerable.Range(1, total).Select(i => $"{{\"n\":{i},\"timestamp\":\"{Now:o}\"}}\n"));
        await File.WriteAllTextAsync(paths.OutboxPath, seeded);

        var http = new RecordingOutboxHttpClient();
        var batchIndex = 0;
        http.OnPost = (_, _) =>
        {
            // First batch delivered, second rejected.
            var delivered = batchIndex == 0;
            batchIndex++;
            return new DrainAck(delivered, delivered ? 1 : 0);
        };

        var sink = new DrainingTelemetrySink(
            new LocalFileTelemetrySink(paths), http, paths, OutboxCap.Default, () => Now, () => 0.0);

        // SendAsync appends ONE more line (n becomes total+1 logically) then drains.
        await sink.SendAsync(SampleEvent());

        // Two batches were attempted (the first 500 delivered, the next rejected).
        Assert.Equal(2, http.PostedBatches.Count);

        // The remaining outbox = the rejected batch onward.  After the append there were
        // total+1 lines; the first 500 were delivered, so total+1-500 remain.
        var remaining = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Equal(total + 1 - DrainingTelemetrySink.MaxBatchLines, remaining.Length);

        // Back-off advanced because a batch failed.
        var state = OutboxDrainState.Read(paths.DrainStatePath);
        Assert.Equal(1, state.ConsecutiveFailures);
    }

    [Fact]
    public async Task SendAsync_IdempotencyKey_IsStableSha256HexOfTheBatchBytes()
    {
        using var paths = new TempPaths();
        var local = new LocalFileTelemetrySink(paths);
        var http = new RecordingOutboxHttpClient().QueueAck(true);

        var sink = new DrainingTelemetrySink(
            local, http, paths, OutboxCap.Default, () => Now, () => 0.0);

        await sink.SendAsync(SampleEvent());

        var key = Assert.Single(http.IdempotencyKeys);
        // 64 lowercase hex chars = SHA-256.
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);

        // Recompute the expected key from the exact batch bytes the client received.
        var batch = Assert.Single(http.PostedBatches);
        var expected = System.Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(string.Join('\n', batch))))
            .ToLowerInvariant();
        Assert.Equal(expected, key);
    }

    [Fact]
    public async Task SendAsync_UnconfiguredEndpoint_StillEnforcesOutboxCap()
    {
        // Criterion 7 holds even with no endpoint: pre-seed an over-cap outbox, then a send
        // (unconfigured) appends one line AND runs the local-only cap, bounding growth.
        using var paths = new TempPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OutboxPath)!);

        // 10 old lines already in the outbox.
        var seeded = string.Concat(
            Enumerable.Range(1, 10).Select(i => $"{{\"n\":{i},\"timestamp\":\"{Now:o}\"}}\n"));
        await File.WriteAllTextAsync(paths.OutboxPath, seeded);

        var local = new LocalFileTelemetrySink(paths);
        var sink = new DrainingTelemetrySink(
            local, http: null, paths,
            new OutboxCap(MaxBytes: long.MaxValue, MaxAgeDays: 365, MaxLines: 5),
            () => Now, () => 0.0);

        await sink.SendAsync(SampleEvent());

        // After append (11 lines) the cap evicts oldest-first down to 5.
        var lines = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Equal(5, lines.Length);
    }

    [Fact]
    public async Task SendAsync_SlowMultiBatchDrain_StopsAtBudget_AndPreservesRemainder()
    {
        // A healthy-but-slow endpoint: the FIRST batch delivers fast (2xx), the SECOND blocks
        // until the overall drain budget elapses.  The drain must stop at the budget, keep the
        // second batch onward as the undelivered remainder, and NOT advance the back-off (some
        // batches delivered — budget expiry is not a hard failure).
        using var paths = new TempPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OutboxPath)!);

        // 550 seeded lines → after the append (551) the drain forms batches of 500 + 51.
        var total = DrainingTelemetrySink.MaxBatchLines + 50;
        var seeded = string.Concat(
            Enumerable.Range(1, total).Select(i => $"{{\"n\":{i},\"timestamp\":\"{Now:o}\"}}\n"));
        await File.WriteAllTextAsync(paths.OutboxPath, seeded);

        var http = new BlockingOnNthBatchClient(blockOnBatch: 2);

        // A tiny injected budget so the test does not wait the real ~15s.
        var sink = new DrainingTelemetrySink(
            new LocalFileTelemetrySink(paths), http, paths,
            OutboxCap.Default, () => Now, () => 0.0,
            drainBudget: TimeSpan.FromMilliseconds(150));

        await sink.SendAsync(SampleEvent());

        // Both batches were ATTEMPTED (the second one blocked until the budget cancelled it).
        Assert.Equal(2, http.PostedBatches.Count);

        // The first 500 lines delivered and were dropped; the remainder (total+1-500) is kept.
        var remaining = await File.ReadAllLinesAsync(paths.OutboxPath);
        Assert.Equal(total + 1 - DrainingTelemetrySink.MaxBatchLines, remaining.Length);

        // The back-off is UNTOUCHED — budget expiry is not a failure (no state file written,
        // so it reads back as the permissive default: zero failures, attempt allowed).
        var state = OutboxDrainState.Read(paths.DrainStatePath);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.True(state.MayAttempt(Now));
    }

    // A fake transport that delivers fast until the Nth batch, then blocks that batch until
    // its cancellation token fires (simulating an endpoint that hangs past the drain budget).
    private sealed class BlockingOnNthBatchClient : IOutboxHttpClient
    {
        private readonly int _blockOnBatch;
        private int _batchCount;

        public BlockingOnNthBatchClient(int blockOnBatch) => _blockOnBatch = blockOnBatch;

        public List<IReadOnlyList<string>> PostedBatches { get; } = new();

        public async Task<DrainAck> PostBatchAsync(
            IReadOnlyList<string> ndjsonLines,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            PostedBatches.Add(ndjsonLines.ToList());
            _batchCount++;

            if (_batchCount >= _blockOnBatch)
            {
                // Block until the budget cancels us, then surface the OCE the real client would
                // (its per-attempt CTS is linked to this token, so a cancel throws here too).
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return new DrainAck(Delivered: true, AcceptedCount: ndjsonLines.Count);
        }

        public Task ForgetAsync(Guid installId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
