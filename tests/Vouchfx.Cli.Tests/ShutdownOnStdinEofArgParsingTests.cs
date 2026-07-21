// Vouchfx.Cli.Tests — --shutdown-on-stdin-eof option parsing tests (vouchfx-mcp#17). No Docker.
//
// Asserts that the `run` command's --shutdown-on-stdin-eof flag parses into the right bool value
// (--shutdown-on-stdin-eof → true; absent → false), and that it is accepted alongside the
// existing flags (path / selection / parallel / watch / report / events). Mirrors
// WatchArgParsingTests / NoDecorationsArgParsingTests. The flag is STRICTLY opt-in: absent means
// RunCommand.ExecuteAsync never touches standard input at all (see StdinShutdownWatcherTests for
// the background-watcher behaviour, and RunCommand.ExecuteAsync's remarks for the linked-token
// wiring). The full run path needs Docker and is NOT invoked here — only the parse is tested.

using System.CommandLine;
using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ShutdownOnStdinEofArgParsingTests
{
    // Mirrors RunCommand.Build's option wiring so the --shutdown-on-stdin-eof parse can be
    // exercised alone.
    private static bool ParseShutdownOnStdinEof(params string[] args)
    {
        var option = RunCommand.BuildShutdownOnStdinEofOption();

        var command = new Command("run");
        command.Add(option);

        var result = command.Parse(args);
        Assert.Empty(result.Errors);

        return result.GetValue(option);
    }

    [Fact]
    public void ShutdownOnStdinEofFlag_Present_ParsesToTrue()
    {
        Assert.True(ParseShutdownOnStdinEof("--shutdown-on-stdin-eof"));
    }

    [Fact]
    public void ShutdownOnStdinEofFlag_Absent_ParsesToFalse()
    {
        Assert.False(ParseShutdownOnStdinEof());
    }

    [Fact]
    public void FullRunCommand_AcceptsShutdownOnStdinEofAlongsideOtherFlags()
    {
        // The built `run` command must accept --shutdown-on-stdin-eof alongside the positional
        // <path> and the existing selection / parallel / report / events flags.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios",
            "--shutdown-on-stdin-eof",
            "--events", "out/events.jsonl",
            "--html", "out/report.html",
            "--junit", "out/report.xml",
            "--parallel", "2",
            "--tag", "smoke",
        });

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FullRunCommand_AcceptsShutdownOnStdinEofAlongsideWatch()
    {
        // --shutdown-on-stdin-eof is not mutually exclusive with --watch (unlike --parallel):
        // both compose to a single graceful-stop cancellation path for the watch loop too.
        var command = RunCommand.Build();
        var result = command.Parse(new[]
        {
            "scenarios/login.e2e.yaml", "--watch", "--shutdown-on-stdin-eof",
        });

        Assert.Empty(result.Errors);
    }

    // ── ExecuteAsync wiring (Docker-free) ───────────────────────────────────────────────────
    //
    // These exercise ExecuteAsync's fast, Docker-free "no scenarios found" return path with
    // shutdownOnStdinEof: true — proving the new linked-CTS + ShutdownBackstop + StdinShutdownWatcher
    // plumbing (constructed over the REAL Console.OpenStandardInput()) composes cleanly with an
    // early return and, critically, that disposing it never hangs the method's return (the exact
    // "uncancellable read wedges shutdown" failure mode the watcher's design guards against — see
    // StdinShutdownWatcherTests for the isolated, Console-free watcher behaviour itself).
    //
    // Test-hygiene note (security-review MINOR follow-up): the test below DOES open a real OS
    // standard-input handle via ExecuteAsync's hard-coded Console.OpenStandardInput() call — it is
    // NOT abstracted behind an injectable seam the way StdinShutdownWatcher/ShutdownBackstop
    // themselves are. That was a deliberate choice, not an oversight: ExecuteAsync's parameter
    // list is already large (14 parameters), and the ACTUAL behavioural risk a real stdin handle
    // poses — a blocking OS read that ignores cancellation and so wedges Dispose — is exhaustively
    // covered in isolation, without touching Console, by StdinShutdownWatcherTests'
    // UncancellableStream regression test. This test's only remaining job is to prove the
    // composition (linked CTS + backstop + watcher, all constructed together) doesn't regress
    // that guarantee at the ExecuteAsync call-site; it is safe and cheap to run under CI (whose
    // own stdin is typically already redirected/closed, giving an immediate, harmless EOF either
    // way) precisely because StdinShutdownWatcher.DisposeAsync never blocks on a non-cooperative
    // read regardless of what the real handle happens to do.
    [Fact]
    public async Task ExecuteAsync_ShutdownOnStdinEofTrue_NoScenariosFound_ReturnsSuccess_WithoutHanging()
    {
        var root = Path.Combine(Path.GetTempPath(), "vouchfx-cli-tests-eof-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var sw = new StringWriter();

            var exitCode = await RunCommand.ExecuteAsync(
                path: root,
                criteria: SelectionCriteria.None,
                parallel: null,
                watch: false,
                failOnEnvironmentError: false,
                failOnInconclusive: false,
                htmlReportPath: null,
                junitReportPath: null,
                eventsReportPath: null,
                eventsStreamPath: null,
                decorate: false,
                output: sw,
                telemetryHook: null,
                cancellationToken: default,
                shutdownOnStdinEof: true);

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Contains("scenarios found", sw.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a locked file must not fail the test.
            }
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShutdownOnStdinEofOmitted_DefaultsToFalse_SameAsExplicitFalse()
    {
        // The new parameter is optional and defaults to false: an existing call site that never
        // mentions it (every OTHER ExecuteAsync test in this project) must behave identically to
        // one that passes shutdownOnStdinEof: false explicitly — proving the default preserves
        // today's behaviour byte-for-byte.
        var root = Path.Combine(Path.GetTempPath(), "vouchfx-cli-tests-eof-default-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var swDefault = new StringWriter();
            var swExplicit = new StringWriter();

            var exitCodeDefault = await RunCommand.ExecuteAsync(
                path: root,
                criteria: SelectionCriteria.None,
                parallel: null,
                watch: false,
                failOnEnvironmentError: false,
                failOnInconclusive: false,
                htmlReportPath: null,
                junitReportPath: null,
                eventsReportPath: null,
                eventsStreamPath: null,
                decorate: false,
                output: swDefault,
                telemetryHook: null,
                cancellationToken: default);

            var exitCodeExplicit = await RunCommand.ExecuteAsync(
                path: root,
                criteria: SelectionCriteria.None,
                parallel: null,
                watch: false,
                failOnEnvironmentError: false,
                failOnInconclusive: false,
                htmlReportPath: null,
                junitReportPath: null,
                eventsReportPath: null,
                eventsStreamPath: null,
                decorate: false,
                output: swExplicit,
                telemetryHook: null,
                cancellationToken: default,
                shutdownOnStdinEof: false);

            Assert.Equal(exitCodeExplicit, exitCodeDefault);
            Assert.Equal(swExplicit.ToString(), swDefault.ToString());
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a locked file must not fail the test.
            }
        }
    }
}
