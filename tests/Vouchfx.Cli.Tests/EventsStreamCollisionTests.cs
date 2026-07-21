// Vouchfx.Cli.Tests — --events-stream same-path collision guard (peer-review follow-up, #258).
// No Docker.
//
// EventStreamAppender holds --events-stream's path open with FileShare.Read for the WHOLE run.
// If a user (mistakenly) points --events-stream at the SAME file as --events / --html /
// --junit, FileReportWriter's later end-of-run FileShare.None open on that path would hit a
// sharing violation and silently never write that archive (only a diagnostic — the docs
// promise the two may be used together, so this must never silently degrade). RunCommand
// rejects this combination at PARSE time — before discovery, before Docker, before anything
// else runs — as a usage error (exit 2), with a clear message on the SAME output stream every
// other RunCommand usage error uses.
//
// These tests exercise RunCommand.ExecuteAsync directly against an EMPTY temp directory root
// (mirroring RunPathRootExecuteTests / ParallelArgParsingTests): the guard fires BEFORE
// ScenarioDiscovery.Discover ever runs, so no topology is needed either way — a collision
// returns exit 2 immediately, and the "different paths" control case falls through to the
// (Docker-free) "no scenarios found" success path.

using Vouchfx.Cli;
using Vouchfx.Cli.Selection;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class EventsStreamCollisionTests : IDisposable
{
    private readonly string _root;

    public EventsStreamCollisionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-cli-tests-esc-" + Guid.NewGuid().ToString("n"));
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

    private Task<int> ExecuteAsync(
        TextWriter output,
        string? htmlReportPath = null,
        string? junitReportPath = null,
        string? eventsReportPath = null,
        string? eventsStreamPath = null)
        => RunCommand.ExecuteAsync(
            path: _root,
            criteria: SelectionCriteria.None,
            parallel: null,
            watch: false,
            failOnEnvironmentError: false,
            failOnInconclusive: false,
            htmlReportPath: htmlReportPath,
            junitReportPath: junitReportPath,
            eventsReportPath: eventsReportPath,
            eventsStreamPath: eventsStreamPath,
            decorate: false,
            output: output,
            telemetryHook: null,
            cancellationToken: default);

    private const string CollisionMessage =
        "--events-stream must not point at the same file as --events/--html/--junit.";

    [Fact]
    public async Task EventsStreamSamePathAsEvents_ReturnsUsageError_WithClearMessage()
    {
        var sw = new StringWriter();
        var samePath = Path.Combine(_root, "shared.jsonl");

        var exitCode = await ExecuteAsync(sw, eventsReportPath: samePath, eventsStreamPath: samePath);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains(CollisionMessage, sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsStreamSamePathAsHtml_ReturnsUsageError_WithClearMessage()
    {
        var sw = new StringWriter();
        var samePath = Path.Combine(_root, "shared.html");

        var exitCode = await ExecuteAsync(sw, htmlReportPath: samePath, eventsStreamPath: samePath);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains(CollisionMessage, sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsStreamSamePathAsJunit_ReturnsUsageError_WithClearMessage()
    {
        var sw = new StringWriter();
        var samePath = Path.Combine(_root, "shared.xml");

        var exitCode = await ExecuteAsync(sw, junitReportPath: samePath, eventsStreamPath: samePath);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains(CollisionMessage, sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsStreamSamePath_ButDifferentCasing_StillDetectedOnWindows()
    {
        // Windows filesystems are case-insensitive by default: "Shared.JSONL" and
        // "shared.jsonl" name the SAME file, so the guard must catch this too — proving the
        // platform-appropriate comparison (OrdinalIgnoreCase on Windows) is actually wired in,
        // not just an exact ordinal match.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var sw = new StringWriter();
        var eventsPath = Path.Combine(_root, "shared.jsonl");
        var streamPath = Path.Combine(_root, "SHARED.JSONL");

        var exitCode = await ExecuteAsync(sw, eventsReportPath: eventsPath, eventsStreamPath: streamPath);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains(CollisionMessage, sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsStreamDifferentPath_IsAccepted_AndRunProceeds()
    {
        // Control: --events-stream at a DIFFERENT path from --events / --html / --junit must
        // NOT trip the guard. The temp root is empty, so the run proceeds past the guard,
        // through (Docker-free) discovery, and reports "no scenarios found" — success, not a
        // usage error — proving the collision guard did not fire.
        var sw = new StringWriter();

        var exitCode = await ExecuteAsync(
            sw,
            htmlReportPath: Path.Combine(_root, "report.html"),
            junitReportPath: Path.Combine(_root, "report.xml"),
            eventsReportPath: Path.Combine(_root, "events.jsonl"),
            eventsStreamPath: Path.Combine(_root, "events-stream.jsonl"));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("scenarios found", sw.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CollisionMessage, sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsStreamAbsent_NeverTripsTheGuard_RegardlessOfOtherPaths()
    {
        // No --events-stream at all: the guard is a no-op even if --events / --html / --junit
        // happen to share a path with EACH OTHER (that collision is pre-existing, out-of-scope
        // behaviour this guard deliberately does not touch).
        var sw = new StringWriter();
        var shared = Path.Combine(_root, "shared-among-the-others.txt");

        var exitCode = await ExecuteAsync(
            sw,
            htmlReportPath: shared,
            junitReportPath: shared,
            eventsReportPath: shared,
            eventsStreamPath: null);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain(CollisionMessage, sw.ToString(), StringComparison.Ordinal);
    }

    // ── RunCommand.PathsEqual (the comparison primitive itself) ─────────────────────────────

    [Fact]
    public void PathsEqual_IdenticalPaths_ReturnsTrue()
    {
        Assert.True(RunCommand.PathsEqual("a/b.jsonl", "a/b.jsonl"));
    }

    [Fact]
    public void PathsEqual_DifferentPaths_ReturnsFalse()
    {
        Assert.False(RunCommand.PathsEqual("a/b.jsonl", "a/c.jsonl"));
    }

    [Fact]
    public void PathsEqual_RelativeAndEquivalentAbsolutePath_ReturnsTrue()
    {
        var relative = "collision-probe.jsonl";
        var absolute = Path.GetFullPath(relative);

        Assert.True(RunCommand.PathsEqual(relative, absolute));
    }

    [Theory]
    [InlineData(null, "a")]
    [InlineData("a", null)]
    [InlineData(null, null)]
    [InlineData("", "a")]
    public void PathsEqual_NullOrEmptyEitherSide_ReturnsFalse(string? a, string? b)
    {
        Assert.False(RunCommand.PathsEqual(a, b));
    }
}
