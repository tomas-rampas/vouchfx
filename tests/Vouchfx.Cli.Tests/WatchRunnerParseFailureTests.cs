// Vouchfx.Cli.Tests — WatchRunner parse-failure sanitisation tests (Issue #266, Item 4). No Docker.
//
// Two DISTINCT WatchRunner sites echo author-controlled parse/AST-build text to the terminal:
//   1. RunAsync's OWN early-return, taken when the file selected for --watch failed to parse
//      BEFORE the watch loop even starts (scenario.Failed) — exercised directly against RunAsync,
//      since this path returns before any topology is ever built.
//   2. The `report:` sink wired into WatchSession, which receives WatchCompileResult.Failure's
//      message on every RE-compile performed by the watch loop (a bad edit saved after the
//      initial, successful selection) — exercised by letting RunAsync perform its real INITIAL
//      run against a file whose on-disk content is rewritten to fail compile before RunAsync ever
//      reads it, so the loop's real Compile/report wiring runs with no fakes, and Docker is never
//      touched because the failing compile short-circuits WatchSession.OnChangeAsync before the
//      build seam.
//
// Test 2 bounds the watch loop with a short CancellationTokenSource.CancelAfter instead of a real
// file-change event, so no FileSystemWatcher trigger or indefinite wait is required — this is the
// "watch-mode test without a long-running watcher" the coordinator asked for.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Cli;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class WatchRunnerParseFailureTests : IDisposable
{
    private readonly string _root;
    private readonly StepKindRegistry _registry = ProviderRegistryFactory.BuildCoreRegistry();

    public WatchRunnerParseFailureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-watch-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    /// <summary>
    /// Fix 1: RunAsync's did-not-parse early return (scenario.Failed) splices the raw
    /// ParseError verbatim; that message is fabricated directly here (no real parse involved,
    /// so no file needs to exist — this branch returns before the file is ever touched) with an
    /// embedded ANSI sequence built purely from <see cref="char"/> arithmetic in memory.
    /// </summary>
    [Fact]
    public async Task RunAsync_SelectedFileFailedToParse_HostileParseErrorWithAnsiSequence_RendersInert()
    {
        var esc = (char)0x1B;
        var hostileParseError =
            "Parse / AST error: unknown step type 'hostile.provider" + esc
            + "[31mHACKED" + esc + "[0m' - no registered provider";
        var path = Path.Combine(_root, "hostile.e2e.yaml");
        var failed = new DiscoveredScenario(path, YamlText: "steps: []", Ast: null, hostileParseError);

        var output = new StringWriter();
        var exitCode = await WatchRunner.RunAsync(
            new[] { failed }, _registry, output, CancellationToken.None);

        Assert.Equal(ExitCodes.UsageError, exitCode);

        var rendered = output.ToString();
        // The surrounding diagnostic text survives sanitisation intact...
        Assert.Contains("HACKED", rendered, StringComparison.Ordinal);
        Assert.Contains("no registered provider", rendered, StringComparison.Ordinal);
        // ...but no raw ESC byte reaches the terminal.
        Assert.DoesNotContain(esc, rendered);
    }

    /// <summary>
    /// Fix 2: the <c>report:</c> sink wired into <c>WatchSession</c> receives
    /// <c>WatchCompileResult.Failure</c>'s message on a re-compile that fails after the file was
    /// selected successfully. The on-disk content is swapped AFTER selection (mirroring "the
    /// author saved a bad edit") so <c>RunOnceFromDiskAsync</c>'s real re-read + re-compile
    /// genuinely fails and reaches WatchRunner's real sink — no fakes for compile/report. The
    /// hostile <c>type:</c> is embedded via YAML's own <c>\x1B</c> double-quoted-scalar escape
    /// (the file bytes on disk stay plain ASCII; YamlDotNet decodes the escape into the actual
    /// control character during parsing) — an UNESCAPED control byte is not valid YAML content
    /// and would fail at the YAML-syntax stage before ever reaching AstBuilder's message, which
    /// would defeat the point of this test.
    /// </summary>
    [Fact]
    public async Task RunAsync_ReCompileFailsOnWatchedSave_HostileAstErrorWithAnsiSequence_RendersInert()
    {
        const string ValidContent =
            "metadata:\n  name: minimal\nsteps:\n  - id: call-api\n    type: http.rest\n";
        var path = Path.Combine(_root, "watched.e2e.yaml");
        File.WriteAllText(path, ValidContent);

        // Selection (mirrors RunCommand's real flow: parse once, up front) sees the VALID content.
        var selected = ScenarioDiscovery.ParseFile(path, _registry);
        Assert.False(selected.Failed, "the fixture content must parse cleanly at selection time");

        // Simulate a bad edit saved AFTER selection but BEFORE the watch loop's initial re-run:
        // this is what RunOnceFromDiskAsync will actually read from disk.
        File.WriteAllText(
            path,
            "steps:\n  - id: call-api\n    type: \"hostile.provider\\x1B[31mHACKED\\x1B[0m\"\n");

        var output = new StringWriter();
        using var cts = new CancellationTokenSource();

        // The compile failure short-circuits OnChangeAsync before any build/run seam fires, so
        // this reaches WatchRunner's real report: sink without ever touching Docker. Rather than
        // race a fixed delay against the initial run (flaky under a loaded, fully-parallel test
        // run), poll for the diagnostic the sink writes and only THEN cancel — no real
        // file-change event needed, and no timing assumption about how long the initial run
        // takes.
        var runTask = WatchRunner.RunAsync(new[] { selected }, _registry, output, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!output.ToString().Contains("Parse / AST error", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    "timed out waiting for the watch loop's initial re-compile diagnostic");
            }

            await Task.Delay(10);
        }

        cts.Cancel();
        var exitCode = await runTask;

        Assert.Equal(ExitCodes.Success, exitCode);

        var rendered = output.ToString();
        // The surrounding diagnostic text survives sanitisation intact...
        Assert.Contains("HACKED", rendered, StringComparison.Ordinal);
        Assert.Contains("Parse / AST error", rendered, StringComparison.Ordinal);
        // ...but no raw ESC byte reaches the terminal.
        Assert.DoesNotContain((char)0x1B, rendered);
    }
}
