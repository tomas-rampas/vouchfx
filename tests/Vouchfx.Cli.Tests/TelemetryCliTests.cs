// Vouchfx.Cli.Tests — telemetry CLI wiring (S10-G-04). No Docker.
//
// Covers the CLI-side telemetry surface that does NOT need a topology:
//   • the `telemetry` command exposes enable/disable/status subcommands;
//   • `run` parses --no-telemetry into the right bool;
//   • TelemetryRunHook captures the buffered event stream into a temp file path,
//     reads it back, builds the allowlisted event, and appends it to the (injected)
//     sink — and suppresses all of that when consent is not Enabled or the run is
//     opted out.  Every path uses an injected temp directory; the real %APPDATA% is
//     never touched.

using System.CommandLine;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Telemetry;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class TelemetryCliTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TelemetryCommand_ExposesEnableDisableStatusSubcommands()
    {
        var command = TelemetryCommand.Build();

        var names = command.Subcommands.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(new[] { "disable", "enable", "status" }, names);
    }

    [Fact]
    public void RunCommand_NoTelemetryOption_ParsesToTrueWhenPresent()
    {
        var option = RunCommand.BuildNoTelemetryOption();
        var command = new Command("run");
        command.Add(option);

        Assert.True(command.Parse(new[] { "--no-telemetry" }).GetValue(option));
        Assert.False(command.Parse(Array.Empty<string>()).GetValue(option));
    }

    [Fact]
    public void RootRun_AcceptsNoTelemetryAlongsideOtherFlags()
    {
        var command = RunCommand.Build();
        var result = command.Parse(new[] { "scenarios", "--no-telemetry", "--tag", "smoke" });
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Hook_Emits_WhenEnabled_FromCapturedEventsFile()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        var sink = new RecordingSinkCli();

        var hook = new TelemetryRunHook(store, temp, sink, noTelemetryFlag: false, TextWriter.Null);

        // No user --events path → the hook resolves a private temp capture path.
        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.NotNull(eventsPath);
        Assert.True(isTemp);

        // Simulate the runner writing the verbatim buffered stream to that path.
        await File.WriteAllLinesAsync(eventsPath!, SyntheticStream());

        await hook.EmitAsync(eventsPath, isTemp, CancellationToken.None);

        Assert.Single(sink.Sent);
        Assert.Equal(1, sink.Sent[0].ScenarioCount);
        Assert.Equal(1, sink.Sent[0].StepProviders["http.rest"]);
        // The hook deletes the temp capture file after reading.
        Assert.False(File.Exists(eventsPath));
    }

    [Theory]
    [InlineData(false)] // Undecided: a fresh store.
    [InlineData(true)]  // Explicitly disabled.
    public async Task Hook_DoesNotEmit_WhenConsentNotEnabled(bool disabled)
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        if (disabled)
        {
            store.Disable();
        }

        var sink = new RecordingSinkCli();
        var hook = new TelemetryRunHook(store, temp, sink, noTelemetryFlag: false, TextWriter.Null);

        // When not emitting, the hook reuses the user's (null) path and never asks for a temp file.
        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.Null(eventsPath);
        Assert.False(isTemp);

        // Even if a stream existed, EmitAsync with a null path is a no-op.
        await hook.EmitAsync(eventsPath, isTemp, CancellationToken.None);
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public async Task Hook_DoesNotEmit_WhenEnabledButNoTelemetryFlagSet()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        var sink = new RecordingSinkCli();

        var hook = new TelemetryRunHook(store, temp, sink, noTelemetryFlag: true, TextWriter.Null);

        var (eventsPath, _) = hook.ResolveEventsCapturePath(userEventsPath: null);
        Assert.Null(eventsPath);
        await hook.EmitAsync(eventsPath, isTempFile: false, CancellationToken.None);
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void Hook_ReusesUserEventsPath_WhenProvided_NoTempFile()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        var hook = new TelemetryRunHook(
            store, temp, new RecordingSinkCli(), noTelemetryFlag: false, TextWriter.Null);

        var (eventsPath, isTemp) = hook.ResolveEventsCapturePath(userEventsPath: "user-report.jsonl");

        // The user's own --events path is reused verbatim; no temp file is created/owned.
        Assert.Equal("user-report.jsonl", eventsPath);
        Assert.False(isTemp);
    }

    [Fact]
    public async Task Disable_FiresForgetWithInstallId_ThenLocalOptOutAlwaysRuns()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        var enabled = store.Enable();          // mints an install id + sets Enabled
        Assert.NotNull(enabled.InstallId);

        Guid? forgottenId = null;
        var output = new StringWriter();

        await TelemetryCommand.DisableAsync(
            store,
            id => { forgottenId = id; return Task.CompletedTask; },
            output,
            CancellationToken.None);

        // The forget fired FIRST, with the still-present install id...
        Assert.Equal(enabled.InstallId, forgottenId);
        // ...and the local opt-out ran: consent is Disabled and the install id is gone.
        var after = store.Read();
        Assert.Equal(TelemetryConsent.Disabled, after.Consent);
        Assert.Null(after.InstallId);
        Assert.Contains("DISABLED", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disable_LocalOptOutStillRuns_WhenForgetFaults()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        var output = new StringWriter();

        // The forget faults (e.g. an unexpected throw escaping the best-effort wrapper).  The
        // local opt-out MUST still run regardless — disabling is a privacy guarantee, not
        // best-effort — whether or not the fault surfaces afterwards (it runs in a finally).
        await Record.ExceptionAsync(() => TelemetryCommand.DisableAsync(
            store,
            _ => throw new InvalidOperationException("forget blew up"),
            output,
            CancellationToken.None));

        var after = store.Read();
        Assert.Equal(TelemetryConsent.Disabled, after.Consent);
        Assert.Null(after.InstallId);
        Assert.Contains("DISABLED", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disable_ReportsSuccessAndOptsOut_WhenAlreadyCancelled()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        store.Enable();
        // Seed an outbox so we can prove it is cleared by the local opt-out.
        File.WriteAllText(temp.OutboxPath, "{\"k\":1}\n");
        var output = new StringWriter();

        // The token is ALREADY cancelled at entry (e.g. a Ctrl-C before the command even
        // begins).  `telemetry disable` is a privacy command whose local opt-out always
        // succeeds, so it must NOT abort: there is no entry-guard throw, and the best-effort
        // forget observes cancellation cooperatively (production swallows OperationCanceled),
        // so DisableAsync runs to completion and the wired action returns ExitCodes.Success.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var forgetSawCancellation = false;
        var ex = await Record.ExceptionAsync(() => TelemetryCommand.DisableAsync(
            store,
            _ =>
            {
                // Mirror the production forget wrapper: notice the cancel but stay fail-silent
                // (it never lets an OperationCanceledException escape the disable path).
                forgetSawCancellation = cts.IsCancellationRequested;
                return Task.CompletedTask;
            },
            output,
            cts.Token));

        // No throw: DisableAsync completed, so the action's `return ExitCodes.Success` is reached.
        Assert.Null(ex);
        Assert.True(forgetSawCancellation);

        // The local opt-out ran: consent is Disabled, the install id is gone, the outbox cleared.
        var after = store.Read();
        Assert.Equal(TelemetryConsent.Disabled, after.Consent);
        Assert.Null(after.InstallId);
        Assert.False(File.Exists(temp.OutboxPath));
        Assert.Contains("DISABLED", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Hook_FirstRunNotice_ShownOnceWhenUndecided()
    {
        using var temp = new TempPathsCli();
        var store = new TelemetryConsentStore(temp);
        var hook = new TelemetryRunHook(
            store, temp, new RecordingSinkCli(), noTelemetryFlag: false, TextWriter.Null);

        var first = new StringWriter();
        hook.MaybeShowFirstRunNotice(first);
        var second = new StringWriter();
        hook.MaybeShowFirstRunNotice(second);

        Assert.Contains("telemetry", first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, second.ToString());
    }

    private static string[] SyntheticStream() => new[]
    {
        EventStreamJson.ToLine(new ScenarioStartedEvent
        {
            RunId = "run0", Timestamp = T0, ScenarioId = "A",
        }),
        EventStreamJson.ToLine(new StepStartedEvent
        {
            RunId = "run0", Timestamp = T0.AddMilliseconds(10), StepId = "s1", Kind = "http.rest",
        }),
        EventStreamJson.ToLine(new ScenarioCompletedEvent
        {
            RunId = "run0",
            Timestamp = T0.AddMilliseconds(20),
            ScenarioId = "A",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        }),
    };

    /// <summary>A temp-dir-rooted <see cref="ITelemetryPaths"/> for the CLI tests.</summary>
    private sealed class TempPathsCli : ITelemetryPaths, IDisposable
    {
        private readonly string _baseDir;
        private readonly DefaultTelemetryPaths _inner;

        public TempPathsCli()
        {
            _baseDir = Path.Combine(
                Path.GetTempPath(), "vouchfx-cli-telemetry-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(_baseDir);
            _inner = new DefaultTelemetryPaths(_baseDir);
        }

        public string ConsentStorePath => _inner.ConsentStorePath;

        public string OutboxPath => _inner.OutboxPath;

        public string DrainStatePath => _inner.DrainStatePath;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_baseDir))
                {
                    Directory.Delete(_baseDir, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>An in-memory sink that records what it was sent.</summary>
    private sealed class RecordingSinkCli : ITelemetrySink
    {
        public List<TelemetryEvent> Sent { get; } = new();

        public Task SendAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            Sent.Add(telemetryEvent);
            return Task.CompletedTask;
        }
    }
}
