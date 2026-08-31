// The drill for issue #378's orphan-host sweep.
//
// The sweep KILLS processes, so the only property worth testing exhaustively is the one that
// decides whether it may: containment under this repository's CLI build output. Every row below
// is a way that decision could go wrong, and each was watched fail against a deliberately broken
// predicate before being kept.
//
// Docker-free by construction. DrillHostSweep.Sweep takes the process table as data and the kill
// as a delegate, so the decision logic is exercised without starting or ending anything.
//
// Run with: dotnet test --filter "requires!=docker&FullyQualifiedName~DrillHostSweep".
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>Covers the selection rule that authorises the sweep's kills.</summary>
public sealed class DrillHostSweepTests : IDisposable
{
    /// <summary>A plausible CLI bin root, in the platform's own separator form.</summary>
    private static readonly string s_root =
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo", "src", "Cli", "Vouchfx.Cli", "bin"));

    /// <summary>A kill that always succeeds, and records what it was asked to kill.</summary>
    private static Func<HostCandidate, KillOutcome> RecordingKill(List<int> killed) =>
        candidate =>
        {
            killed.Add(candidate.Pid);
            return KillOutcome.Confirmed;
        };

    private static HostCandidate Candidate(int pid, params string[] imagePaths) =>
        new(pid, "dotnet", imagePaths, InspectionFailure: null, StartTime: DateTime.UtcNow);

    /// <summary>Scratch log paths this class created, deleted in <see cref="Dispose"/>.</summary>
    private readonly List<string> _scratchLogs = new();

    /// <summary>
    /// A private log path for one construction of the fixture.
    /// </summary>
    /// <remarks>
    /// <strong>Never <see cref="DrillHostSweepFixture.ReportPath"/>.</strong> That file's whole
    /// value is that a line in it is a real finding; a drill appending fabricated kills to it would
    /// bury the one line anybody goes looking for, in the record whose own rationale says so. The
    /// omission was a real defect in this file's first version, caught in review.
    /// </remarks>
    private string ScratchLogPath()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "vouchfx-drill-host-sweep-drill-" + Guid.NewGuid().ToString("N") + ".log");
        _scratchLogs.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _scratchLogs)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover scratch log is not test-fatal.
            }
        }
    }

    [Fact]
    public void Sweep_KillsAHostHoldingAnImageUnderTheCliBinRoot()
    {
        var killed = new List<int>();
        var candidate = Candidate(
            4242,
            Path.Combine(s_root, "Release", "net8.0", "vouchfx.dll"));

        var report = DrillHostSweep.Sweep(
            new[] { candidate }, s_root, selfPid: 1, RecordingKill(killed));

        Assert.Equal(4242, Assert.Single(killed));
        var line = Assert.Single(report.Killed);
        Assert.Contains("4242", line, StringComparison.Ordinal);
        Assert.Contains("vouchfx.dll", line, StringComparison.Ordinal);
        Assert.Empty(report.Unkillable);
    }

    /// <summary>
    /// The Debug configuration is in scope too: the lock that motivated #378 was a Release build,
    /// but a Debug drill leaks exactly the same way and the root is the whole <c>bin</c>.
    /// </summary>
    [Fact]
    public void Sweep_KillsAHostHoldingADebugConfigurationImage()
    {
        var killed = new List<int>();

        DrillHostSweep.Sweep(
            new[] { Candidate(7, Path.Combine(s_root, "Debug", "net8.0", "vouchfx.dll")) },
            s_root,
            selfPid: 1,
            RecordingKill(killed));

        Assert.Equal(7, Assert.Single(killed));
    }

    /// <summary>
    /// A process whose FIRST images are innocent still counts: the CLI host maps hundreds of
    /// framework modules before any repository one, so selection must scan the list rather than
    /// look at its head.
    /// </summary>
    [Fact]
    public void Sweep_KillsAHostWhoseRepositoryImageIsNotTheFirstOne()
    {
        var killed = new List<int>();
        var candidate = Candidate(
            9,
            Path.Combine(Path.GetTempPath(), "dotnet", "dotnet.exe"),
            Path.Combine(Path.GetTempPath(), "dotnet", "shared", "System.Private.CoreLib.dll"),
            Path.Combine(s_root, "Release", "net8.0", "Vouchfx.Engine.Runtime.dll"));

        DrillHostSweep.Sweep(new[] { candidate }, s_root, selfPid: 1, RecordingKill(killed));

        Assert.Equal(9, Assert.Single(killed));
    }

    [Fact]
    public void Sweep_NeverTouchesAProcessOutsideTheCliBinRoot()
    {
        var killed = new List<int>();
        var candidate = Candidate(
            11,
            Path.Combine(Path.GetTempPath(), "dotnet", "dotnet.exe"),
            Path.Combine(Path.GetTempPath(), "some-other-repo", "bin", "vouchfx.dll"));

        var report = DrillHostSweep.Sweep(
            new[] { candidate }, s_root, selfPid: 1, RecordingKill(killed));

        Assert.Empty(killed);
        Assert.Empty(report.Killed);
        Assert.Empty(report.Unkillable);
    }

    /// <summary>
    /// A sibling directory whose name merely STARTS WITH the root is out of scope. This is the row
    /// that a prefix comparison without a separator would fail, and it is the difference between a
    /// scoped sweep and one that kills by coincidence.
    /// </summary>
    [Fact]
    public void Sweep_NeverTouchesASiblingDirectorySharingTheRootsPrefix()
    {
        var killed = new List<int>();
        var sibling = s_root + "-scratch";

        DrillHostSweep.Sweep(
            new[] { Candidate(13, Path.Combine(sibling, "net8.0", "vouchfx.dll")) },
            s_root,
            selfPid: 1,
            RecordingKill(killed));

        Assert.Empty(killed);
    }

    /// <summary>
    /// The test host runs from tests/**/bin and would be a candidate under any repository-wide
    /// root; the pid guard is the second line of defence behind the narrow root.
    /// </summary>
    [Fact]
    public void Sweep_NeverKillsItself()
    {
        var killed = new List<int>();
        var candidate = Candidate(
            99, Path.Combine(s_root, "Release", "net8.0", "vouchfx.dll"));

        var report = DrillHostSweep.Sweep(
            new[] { candidate }, s_root, selfPid: 99, RecordingKill(killed));

        Assert.Empty(killed);
        Assert.Empty(report.Killed);
        Assert.Empty(report.Skipped);
    }

    /// <summary>
    /// Access denied to another user's process degrades to a named skip. It must not become a
    /// kill (the sweep does not know what the process holds) and must not become silence.
    /// </summary>
    [Fact]
    public void Sweep_SkipsAndReportsAProcessWhoseImagesCannotBeRead()
    {
        var killed = new List<int>();
        var candidate = new HostCandidate(
            21, "dotnet", Array.Empty<string>(), "Access is denied.");

        var report = DrillHostSweep.Sweep(
            new[] { candidate }, s_root, selfPid: 1, RecordingKill(killed));

        Assert.Empty(killed);
        var line = Assert.Single(report.Skipped);
        Assert.Contains("21", line, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Sweep_ReportsAnOrphanThatSurvivedTheKillAsUnkillable()
    {
        var report = DrillHostSweep.Sweep(
            new[] { Candidate(31, Path.Combine(s_root, "Release", "net8.0", "vouchfx.dll")) },
            s_root,
            selfPid: 1,
            _ => KillOutcome.Failed("still running 10s after the kill"));

        Assert.Empty(report.Killed);
        var line = Assert.Single(report.Unkillable);
        Assert.Contains("31", line, StringComparison.Ordinal);
        Assert.Contains("still running", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The unkillable case is the one that has to be impossible to miss: it throws out of the
    /// fixture constructor, so every row in the drill collection errors carrying the pid and path.
    /// </summary>
    [Fact]
    public void Fixture_ThrowsNamingTheProcessWhenAnOrphanSurvivesTheKill()
    {
        var unkillable = new[] { "orphaned CLI host pid 31 (dotnet) holding C:\\repo\\vouchfx.dll" };
        var report = new SweepReport(
            Killed: Array.Empty<string>(),
            Unkillable: unkillable,
            Skipped: Array.Empty<string>());

        var ex = Assert.Throws<InvalidOperationException>(() => new DrillHostSweepFixture(report, ScratchLogPath()));

        Assert.Contains("pid 31", ex.Message, StringComparison.Ordinal);
        Assert.Contains("vouchfx.dll", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A sweep that found nothing is not a failure, and does not pretend to be one.</summary>
    [Fact]
    public void Fixture_ConstructsQuietlyWhenNothingWasFound()
    {
        var report = new SweepReport(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

        var fixture = new DrillHostSweepFixture(report, ScratchLogPath());

        Assert.Empty(fixture.Report.Lines);
    }

    /// <summary>
    /// A killed orphan does NOT fail the lane. Failing today's rows for a previous session's leak
    /// would repeat the misattribution the sweep exists to end.
    /// </summary>
    [Fact]
    public void Fixture_DoesNotFailTheLaneForAnOrphanItSuccessfullyKilled()
    {
        var killed = new[] { "killed orphaned CLI host pid 5 (dotnet) holding x" };
        var report = new SweepReport(
            Killed: killed,
            Unkillable: Array.Empty<string>(),
            Skipped: Array.Empty<string>());

        var fixture = new DrillHostSweepFixture(report, ScratchLogPath());

        Assert.Single(fixture.Report.Killed);
    }

    /// <summary>
    /// A sweep that found something leaves a durable record. The console cannot carry it - a
    /// fixture constructor runs outside any test, so its output has no ITestOutputHelper to attach
    /// to and does not survive to `dotnet test`'s default output - so the file is the channel that
    /// has to work.
    /// </summary>
    [Fact]
    public void Record_AppendsWhatTheSweepFoundWithTheRunsPid()
    {
        var path = ScratchLogPath();
        var killed = new[] { "killed orphaned CLI host pid 5 (dotnet) holding x" };
        var report = new SweepReport(
            Killed: killed,
            Unkillable: Array.Empty<string>(),
            Skipped: Array.Empty<string>());

        DrillHostSweepFixture.Record(report, path, phase: "entry");

        var line = Assert.Single(File.ReadAllLines(path));
        Assert.Contains("killed orphaned CLI host pid 5", line, StringComparison.Ordinal);
        Assert.Contains($"pid {Environment.ProcessId}", line, StringComparison.Ordinal);

        // The phase is recorded because entry and exit findings mean different things: residue
        // from an earlier session, versus a launch site in THIS run that did not kill its child.
        Assert.Contains("[entry]", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sweep that found nothing writes nothing. An append-only record of non-events would bury
    /// the one line anybody ever goes looking for.
    /// </summary>
    [Fact]
    public void Record_WritesNothingWhenTheSweepFoundNothing()
    {
        var path = ScratchLogPath();

        DrillHostSweepFixture.Record(
            new SweepReport(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
            path);

        Assert.False(File.Exists(path));
    }

    /// <summary>The record goes to the temp directory, never into the repository's own tree.</summary>
    [Fact]
    public void ReportPath_IsOutsideTheRepository()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(DrillHostSweepTests).Assembly.Location)!,
            "..", "..", "..", "..", ".."));

        Assert.False(DrillHostSweep.IsUnder(DrillHostSweepFixture.ReportPath, repoRoot));
    }

    /// <summary>
    /// The root the live sweep uses is this repository's CLI output and nothing broader - the
    /// property that keeps the sweep from naming the test host itself.
    /// </summary>
    [Fact]
    public void CliBinRoot_IsThisRepositorysCliBuildOutput()
    {
        var root = DrillHostSweep.ResolveCliBinRoot();

        Assert.EndsWith(
            Path.Combine("src", "Cli", "Vouchfx.Cli", "bin"), root, StringComparison.Ordinal);
        Assert.True(
            Directory.Exists(Path.GetDirectoryName(root)),
            $"The resolved CLI project directory does not exist: '{root}'. The sweep would then "
            + "match nothing, silently.");
    }

    [Theory]
    [InlineData("Release/net8.0/vouchfx.dll", true)]
    [InlineData("vouchfx.dll", true)]
    [InlineData("../vouchfx.dll", false)]
    [InlineData("../obj/vouchfx.dll", false)]
    public void IsUnder_AnswersForPathsRelativeToTheRoot(string relative, bool expected) =>
        Assert.Equal(expected, DrillHostSweep.IsUnder(Path.Combine(s_root, relative), s_root));

    /// <summary>The root itself is not "under" the root; only its contents are.</summary>
    [Fact]
    public void IsUnder_RejectsTheRootItselfAndTheEmptyPath()
    {
        Assert.False(DrillHostSweep.IsUnder(s_root, s_root));
        Assert.False(DrillHostSweep.IsUnder(string.Empty, s_root));
    }

    /// <summary>
    /// The record lives under the per-user local application data directory, not the shared temp
    /// directory. It names process ids and absolute paths from this repository; on a multi-user
    /// host a temp filename can be pre-created by somebody else, as a symlink or simply to read.
    /// </summary>
    [Fact]
    public void ReportPath_IsUnderThePerUserLocalApplicationDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        Assert.False(string.IsNullOrEmpty(localAppData));
        Assert.True(
            DrillHostSweep.IsUnder(DrillHostSweepFixture.ReportPath, localAppData),
            $"the sweep record is at '{DrillHostSweepFixture.ReportPath}', which is not under the "
            + $"per-user directory '{localAppData}'.");
        Assert.False(
            DrillHostSweep.IsUnder(DrillHostSweepFixture.ReportPath, Path.GetTempPath()),
            "the sweep record must not live in the shared temp directory.");
    }

    // ── The pid-reuse guard ─────────────────────────────────────────────────────────────────
    //
    // A pid is not unique over time. The sweep decides on a snapshot and kills afterwards, so an
    // orphan that exits in between can leave its number to an unrelated process - one that was
    // never inspected and whose images were never shown to be under this repository's output.

    /// <summary>
    /// A killer that reports pid reuse must produce a SKIP, never a failure: the orphan is gone,
    /// which is the outcome the sweep wanted. A failure here would throw out of the fixture and
    /// red the lane over a process that no longer exists.
    /// </summary>
    [Fact]
    public void Sweep_ReportsAReusedPidAsASkipRatherThanAFailure()
    {
        var report = DrillHostSweep.Sweep(
            new[] { Candidate(41, Path.Combine(s_root, "Release", "net8.0", "vouchfx.dll")) },
            s_root,
            selfPid: 1,
            _ => KillOutcome.NotTheSameProcess("the pid now denotes a DIFFERENT process"));

        Assert.Empty(report.Killed);
        Assert.Empty(report.Unkillable);
        var line = Assert.Single(report.Skipped);
        Assert.Contains("41", line, StringComparison.Ordinal);
        Assert.Contains("DIFFERENT process", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A confirmed kill whose tree was only PARTLY terminated must say so on its line. The verdict
    /// is still "killed" - the root held the build output and the root is gone - but the survivor
    /// of a CLI host's tree is typically DCP, still holding containers and a session network. A
    /// bare "killed" line would leave that invisible, which is the shape of silence #378 is about.
    /// </summary>
    [Fact]
    public void Sweep_SaysSoWhenAConfirmedKillLeftDescendantsBehind()
    {
        var report = DrillHostSweep.Sweep(
            new[] { Candidate(61, Path.Combine(s_root, "Release", "net8.0", "vouchfx.dll")) },
            s_root,
            selfPid: 1,
            _ => KillOutcome.ConfirmedWithSurvivingDescendants);

        var line = Assert.Single(report.Killed);
        Assert.Contains("killed orphaned CLI host pid 61", line, StringComparison.Ordinal);
        Assert.Contains("descendants could not be terminated", line, StringComparison.Ordinal);
        Assert.Empty(report.Unkillable);
    }

    /// <summary>A clean kill carries no caveat clause - the common case stays terse.</summary>
    [Fact]
    public void Sweep_AddsNoCaveatClauseToACleanKill()
    {
        var report = DrillHostSweep.Sweep(
            new[] { Candidate(63, Path.Combine(s_root, "Release", "net8.0", "vouchfx.dll")) },
            s_root,
            selfPid: 1,
            _ => KillOutcome.Confirmed);

        var line = Assert.Single(report.Killed);
        Assert.DoesNotContain("descendants", line, StringComparison.Ordinal);
        Assert.EndsWith("vouchfx.dll", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The candidate handed to the killer carries the identity it was judged on, so the killer can
    /// re-compare before acting. Passing a bare pid would make the check impossible to write.
    /// </summary>
    [Fact]
    public void Sweep_HandsTheKillerTheStartTimeItInspected()
    {
        var started = new DateTime(2026, 8, 31, 5, 0, 0, DateTimeKind.Utc);
        var candidate = new HostCandidate(
            43,
            "dotnet",
            new[] { Path.Combine(s_root, "Release", "net8.0", "vouchfx.dll") },
            InspectionFailure: null,
            StartTime: started);

        HostCandidate? seen = null;
        DrillHostSweep.Sweep(
            new[] { candidate },
            s_root,
            selfPid: 1,
            c =>
            {
                seen = c;
                return KillOutcome.Confirmed;
            });

        Assert.Equal(started, seen?.StartTime);
    }

    /// <summary>
    /// Inspection that SUCCEEDS and yields no path at all is a process the sweep could not judge -
    /// distinct from one it judged and cleared. Collapsing the two would let a platform that
    /// silently returns no module paths read as a permanently clean sweep.
    /// </summary>
    [Fact]
    public void Sweep_SkipsAndReportsAProcessWhoseInspectionYieldedNoPathAtAll()
    {
        var killed = new List<int>();
        var candidate = new HostCandidate(
            51, "dotnet", Array.Empty<string>(), InspectionFailure: null, StartTime: DateTime.UtcNow);

        var report = DrillHostSweep.Sweep(
            new[] { candidate }, s_root, selfPid: 1, RecordingKill(killed));

        Assert.Empty(killed);
        var line = Assert.Single(report.Skipped);
        Assert.Contains("51", line, StringComparison.Ordinal);
        Assert.Contains("no image path", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A process inspected, judged, and found not to be ours produces NO line. That is the common
    /// case - every unrelated `dotnet` on the machine - and reporting it would drown the record.
    /// </summary>
    [Fact]
    public void Sweep_SaysNothingAboutAProcessItInspectedAndCleared()
    {
        var report = DrillHostSweep.Sweep(
            new[] { Candidate(53, Path.Combine(Path.GetTempPath(), "elsewhere", "app.dll")) },
            s_root,
            selfPid: 1,
            RecordingKill(new List<int>()));

        Assert.Empty(report.Lines);
    }

    // ── The opt-out ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Only the exact string <c>0</c> disables the sweep. A guard that kills processes must not be
    /// switchable off by a typo, an empty value, or an unset variable.
    /// </summary>
    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("00", false)]
    [InlineData(" 0", false)]
    public void IsDisabledBy_RecognisesOnlyTheExactOffValue(string? value, bool expected) =>
        Assert.Equal(expected, DrillHostSweepFixture.IsDisabledBy(value));

    // ── The exit sweep ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Disposal runs a second sweep, so the session that leaks a host clears it before its OWN
    /// next build rather than leaving it for the next drill run - which may be days away.
    /// </summary>
    [Fact]
    public void Dispose_RunsAnExitSweepAndRecordsIt()
    {
        var fixture = new DrillHostSweepFixture(
            new SweepReport(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
            ScratchLogPath());

        Assert.Null(fixture.ExitReport);

        fixture.Dispose();

        Assert.NotNull(fixture.ExitReport);
    }

    /// <summary>
    /// Disposal never throws. xUnit attributes a fixture disposal failure to the collection, so an
    /// exit sweep that reddened the lane would repaint already-decided rows as failures - the
    /// misattribution this guard exists to end, arriving through the guard.
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotentAndNeverThrows()
    {
        var fixture = new DrillHostSweepFixture(
            new SweepReport(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
            ScratchLogPath());

        fixture.Dispose();
        var first = fixture.ExitReport;
        fixture.Dispose();

        Assert.Same(first, fixture.ExitReport);
    }
}
