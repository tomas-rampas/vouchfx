// Platform.Engine.Telemetry.Tests — end-to-end consent→sink decision (S10-G-04).
//
// Ties the consent store + suppression predicate + sink together to prove the headline
// acceptance clause: the sink is NEVER invoked while consent is Undecided or Disabled,
// and IS invoked (exactly once) when Enabled and not opted out.  This mirrors what the
// CLI's TelemetryRunHook does, without the CLI/Docker dependency.

using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Xunit;

namespace Platform.Engine.Telemetry.Tests;

public sealed class TelemetryEndToEndDecisionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(TelemetryConsent.Undecided)]
    [InlineData(TelemetryConsent.Disabled)]
    public async Task SinkNeverInvoked_WhenConsentNotEnabled(TelemetryConsent consent)
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);
        SeedConsent(store, consent);

        var recordingSink = new RecordingSink();

        await EmitIfPermittedAsync(
            store, recordingSink, noTelemetryFlag: false, envOptOut: null);

        Assert.Empty(recordingSink.Sent);
        // And nothing was written to the on-disk outbox either.
        Assert.False(File.Exists(paths.OutboxPath));
    }

    [Fact]
    public async Task SinkInvokedOnce_WhenEnabledAndNotOptedOut()
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);
        store.Enable();

        var recordingSink = new RecordingSink();

        await EmitIfPermittedAsync(
            store, recordingSink, noTelemetryFlag: false, envOptOut: null);

        Assert.Single(recordingSink.Sent);
        Assert.Equal(1, recordingSink.Sent[0].ScenarioCount);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "1")]
    public async Task SinkNeverInvoked_WhenEnabledButOptedOut(bool noTelemetryFlag, string? envOptOut)
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);
        store.Enable();

        var recordingSink = new RecordingSink();

        await EmitIfPermittedAsync(store, recordingSink, noTelemetryFlag, envOptOut);

        Assert.Empty(recordingSink.Sent);
    }

    /// <summary>
    /// Mirrors the CLI hook's decision: emit ONLY when permitted, building the event from
    /// a one-scenario synthetic stream.
    /// </summary>
    private static async Task EmitIfPermittedAsync(
        TelemetryConsentStore store,
        RecordingSink sink,
        bool noTelemetryFlag,
        string? envOptOut)
    {
        var state = store.Read();
        if (!TelemetrySuppression.ShouldEmit(state.Consent, noTelemetryFlag, envOptOut))
        {
            return;
        }

        var lines = new[]
        {
            SyntheticEvents.ScenarioStarted("A", T0),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 1 }, T0.AddMilliseconds(10)),
        };

        var ev = TelemetryEventBuilder.Build(
            lines,
            state.InstallId ?? Guid.NewGuid(),
            "1.0.0",
            "1.0.0",
            ".NET 8.0.7",
            T0.AddSeconds(1));

        await sink.SendAsync(ev);
    }

    private static void SeedConsent(TelemetryConsentStore store, TelemetryConsent consent)
    {
        switch (consent)
        {
            case TelemetryConsent.Enabled:
                store.Enable();
                break;
            case TelemetryConsent.Disabled:
                store.Disable();
                break;
            case TelemetryConsent.Undecided:
            default:
                // Default state of a fresh store; nothing to do.
                break;
        }
    }
}
