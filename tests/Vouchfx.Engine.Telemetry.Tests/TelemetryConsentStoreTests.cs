// Vouchfx.Engine.Telemetry.Tests — consent store + first-run notice (S10-G-04).
//
// Proves the consent lifecycle acceptance clauses: missing/corrupt file → Undecided
// (no throw); the first-run notice shows once and only while Undecided; opt-in mints an
// install id; disable deletes the install id AND clears the outbox.  Every test injects
// a TempPaths so the real %APPDATA% is never touched.

using Xunit;

namespace Vouchfx.Engine.Telemetry.Tests;

public sealed class TelemetryConsentStoreTests
{
    [Fact]
    public void Read_MissingFile_ReturnsUndecidedDefault_NeverThrows()
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);

        var state = store.Read();

        Assert.Equal(TelemetryConsent.Undecided, state.Consent);
        Assert.False(state.NoticeShown);
        Assert.Null(state.InstallId);
        // No file should have been created merely by reading.
        Assert.False(File.Exists(paths.ConsentStorePath));
    }

    [Fact]
    public void Read_CorruptFile_ReturnsUndecidedDefault_NeverThrows()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ConsentStorePath)!);
        // Garbage that is not valid JSON for the state record.
        File.WriteAllText(paths.ConsentStorePath, "{ this is : not json ]]]");

        var store = new TelemetryConsentStore(paths);

        // Must not throw, and must collapse to the safe default.
        var state = store.Read();
        Assert.Equal(TelemetryConsent.Undecided, state.Consent);
        Assert.Null(state.InstallId);
    }

    [Fact]
    public void Read_EmptyFile_ReturnsUndecidedDefault()
    {
        using var paths = new TempPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.ConsentStorePath)!);
        File.WriteAllText(paths.ConsentStorePath, string.Empty);

        var state = new TelemetryConsentStore(paths).Read();

        Assert.Equal(TelemetryConsent.Undecided, state.Consent);
    }

    [Fact]
    public void FirstRunNotice_ShownOnce_WhenUndecided_NotAgainAfterShown()
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);

        var first = new StringWriter();
        var shown1 = store.ShowFirstRunNoticeIfNeeded(first);

        var second = new StringWriter();
        var shown2 = store.ShowFirstRunNoticeIfNeeded(second);

        Assert.True(shown1);
        Assert.False(shown2);
        Assert.Contains("telemetry", first.ToString(), StringComparison.OrdinalIgnoreCase);
        // The notice must say nothing is sent until the user opts in.
        Assert.Contains("vouchfx telemetry enable", first.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, second.ToString());
        // The persisted flag records that it was shown.
        Assert.True(store.Read().NoticeShown);
    }

    [Fact]
    public void FirstRunNotice_NotShown_WhenConsentDecided()
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);

        // An explicit decision (enable) means the notice is no longer appropriate.
        store.Enable();

        var sw = new StringWriter();
        var shown = store.ShowFirstRunNoticeIfNeeded(sw);

        Assert.False(shown);
        Assert.Equal(string.Empty, sw.ToString());
    }

    [Fact]
    public void Enable_MintsInstallId_AndSetsConsentEnabled()
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);

        var state = store.Enable();

        Assert.Equal(TelemetryConsent.Enabled, state.Consent);
        Assert.NotNull(state.InstallId);
        Assert.NotEqual(Guid.Empty, state.InstallId!.Value);
        // Persisted.
        Assert.Equal(state.InstallId, store.Read().InstallId);
    }

    [Fact]
    public void Enable_Twice_PreservesTheSameInstallId()
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);

        var first = store.Enable().InstallId;
        var second = store.Enable().InstallId;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Disable_DeletesInstallId_AndClearsOutbox()
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);

        // Enable, then write an outbox entry so there is something to clear.
        store.Enable();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.OutboxPath)!);
        File.WriteAllText(paths.OutboxPath, "{\"schemaVersion\":1}\n");
        Assert.True(File.Exists(paths.OutboxPath));

        var state = store.Disable();

        Assert.Equal(TelemetryConsent.Disabled, state.Consent);
        Assert.Null(state.InstallId);
        Assert.Null(store.Read().InstallId);
        // The outbox file is removed immediately on disable (well within 30-day requirement).
        Assert.False(File.Exists(paths.OutboxPath));
    }

    [Fact]
    public void Disable_WithNoOutbox_DoesNotThrow()
    {
        using var paths = new TempPaths();
        var store = new TelemetryConsentStore(paths);
        store.Enable();

        // No outbox file exists; disabling must still succeed.
        var state = store.Disable();

        Assert.Equal(TelemetryConsent.Disabled, state.Consent);
    }
}
