// Platform.Engine.Telemetry.Tests — OutboxForget + disable→forget (S12-G-01, Phase A).
//
// Criterion 3 (client half): when telemetry is disabled and the transport is configured
// and an install id exists, a best-effort forget is fired; but a transport fault/offline
// endpoint must NOT block the LOCAL opt-out (the install id deletion + outbox clear).
//
//   • OutboxForget.TryForgetAsync fires the forget when a client + id are present;
//   • it is a no-op when the client is null (unconfigured) or the id is null;
//   • a transport fault is swallowed (returns false), never thrown;
//   • the LOCAL opt-out (TelemetryConsentStore.Disable) deletes the install id and clears
//     the outbox INDEPENDENTLY — proven to still happen even when a forget would fault.

using Xunit;

namespace Platform.Engine.Telemetry.Tests;

public sealed class OutboxForgetTests
{
    // A forget client that always faults, to prove the local opt-out is never blocked.
    private sealed class FaultingForgetClient : IOutboxHttpClient
    {
        public bool ForgetAttempted { get; private set; }

        public Task<DrainAck> PostBatchAsync(
            IReadOnlyList<string> ndjsonLines, string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(DrainAck.NotDelivered);

        public Task ForgetAsync(Guid installId, CancellationToken ct)
        {
            ForgetAttempted = true;
            throw new System.Net.Http.HttpRequestException("endpoint offline");
        }
    }

    [Fact]
    public async Task TryForgetAsync_ConfiguredWithInstallId_FiresForget()
    {
        var http = new RecordingOutboxHttpClient();
        var id = Guid.NewGuid();

        var fired = await OutboxForget.TryForgetAsync(http, id, CancellationToken.None);

        Assert.True(fired);
        Assert.Equal(id, Assert.Single(http.Forgotten));
    }

    [Fact]
    public async Task TryForgetAsync_NoClient_IsNoOp()
    {
        var fired = await OutboxForget.TryForgetAsync(
            client: null, installId: Guid.NewGuid(), CancellationToken.None);

        Assert.False(fired);
    }

    [Fact]
    public async Task TryForgetAsync_NoInstallId_IsNoOp()
    {
        var http = new RecordingOutboxHttpClient();

        var fired = await OutboxForget.TryForgetAsync(http, installId: null, CancellationToken.None);

        Assert.False(fired);
        Assert.Empty(http.Forgotten);
    }

    [Fact]
    public async Task TryForgetAsync_TransportFault_IsSwallowed_ReturnsFalse()
    {
        var faulting = new FaultingForgetClient();

        // Must not throw despite the client faulting.
        var fired = await OutboxForget.TryForgetAsync(
            faulting, Guid.NewGuid(), CancellationToken.None);

        Assert.False(fired);
        Assert.True(faulting.ForgetAttempted);
    }

    [Fact]
    public async Task DisableFlow_ForgetFaults_LocalOptOutStillDeletesIdAndClearsOutbox()
    {
        // Mirror the CLI disable order: read id → best-effort (faulting) forget → local
        // Disable().  Criterion 3: the local opt-out completes regardless of the forget.
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);

        // Opt in (mints an install id) and queue an event in the outbox.
        var enabled = store.Enable();
        Assert.NotNull(enabled.InstallId);
        await new LocalFileTelemetrySink(paths).SendAsync(SampleEvent());
        Assert.True(File.Exists(paths.OutboxPath));

        var faulting = new FaultingForgetClient();
        var idBeforeDisable = store.Read().InstallId;

        // The forget faults internally but TryForgetAsync swallows it...
        var fired = await OutboxForget.TryForgetAsync(
            faulting, idBeforeDisable, CancellationToken.None);
        Assert.False(fired);
        Assert.True(faulting.ForgetAttempted);

        // ...and the LOCAL opt-out still proceeds: id deleted, outbox cleared.
        var disabled = store.Disable();

        Assert.Equal(TelemetryConsent.Disabled, disabled.Consent);
        Assert.Null(disabled.InstallId);
        Assert.False(File.Exists(paths.OutboxPath));
    }

    private static TelemetryEvent SampleEvent() => new()
    {
        SchemaVersion = TelemetryEventBuilder.CurrentSchemaVersion,
        Timestamp = DateTimeOffset.UtcNow,
        InstallId = Guid.NewGuid(),
        ToolVersion = "1.0.0",
        EngineVersion = "1.0.0",
        DotnetVersion = ".NET 8.0.7",
        RunCount = 1,
        ScenarioCount = 1,
        StepVerdicts = new TelemetryVerdictCounts(),
        ScenarioVerdicts = new TelemetryVerdictCounts(),
        StepFamilies = new Dictionary<string, int>(),
        StepProviders = new Dictionary<string, int>(),
        StartupMs = 0,
        TimeToFirstTestMs = 0,
    };
}
