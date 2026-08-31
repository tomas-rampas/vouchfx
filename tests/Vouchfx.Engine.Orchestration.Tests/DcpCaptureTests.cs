using Microsoft.Extensions.Logging;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Drills for the flush half of the #420 flight recorder: where a capture is written, how many
/// are kept, what reaches an Environment-error detail, and what happens on every path where the
/// filesystem refuses to cooperate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here touches the real capture directory.</strong> Every drill that writes
/// passes its own scratch directory and deletes it afterwards. That is not tidiness: the whole
/// value of a file under the per-user directory is that a capture in it is a real finding rather
/// than a fabricated one, and a drill that wrote there would poison exactly that signal for
/// whoever meets #420 next.
/// </para>
/// <para>
/// The production <see cref="DcpCapture.WriteAsync"/> overload resolves the real directory only
/// when its <c>directory</c> argument is null, so passing a scratch path exercises the same code
/// with the same delegates against a different root.
/// </para>
/// </remarks>
public sealed class DcpCaptureTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "vouchfx-dcp-capture-drill-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a held handle must not redden the drill that already passed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // -----------------------------------------------------------------------
    // Directory resolution
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveDirectory_WithARootedPerUserRoot_UsesItAndStaysAbsolute()
    {
        var resolved = DcpCapture.ResolveDirectory(
            Path.Combine(Path.GetTempPath(), "appdata"));

        Assert.NotNull(resolved);
        Assert.True(Path.IsPathRooted(resolved));
        Assert.EndsWith(DcpCapture.DirectoryName, resolved!, StringComparison.Ordinal);
        Assert.Contains("appdata", resolved!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveDirectory_WithNoPerUserRoot_RefusesRatherThanFallingBackToTemp(string? root)
    {
        // Environment.GetFolderPath returns the EMPTY STRING rather than throwing when a folder
        // is undefined for the platform. The tempting fallback is the temp directory - which
        // this type's own remarks call world-readable and pre-creatable by another user, under a
        // name fully predictable from a timestamp. Refusing is the safer answer, and the annex
        // says so rather than leaving the operator to wonder where the file went.
        Assert.Null(DcpCapture.ResolveDirectory(root));
    }

    [Fact]
    public void ResolveDirectory_WithAnAbsoluteOverride_UsesItExactly_WithNoSubdirectoryAppended()
    {
        // An operator who named a directory meant that directory. Appending "vouchfx" to it
        // would put captures somewhere other than where a CI job's artefact glob is pointed.
        var target = Path.Combine(Path.GetTempPath(), "captures-here");

        var resolved = DcpCapture.ResolveDirectory(
            localApplicationData: Path.GetTempPath(), overrideDirectory: target);

        Assert.Equal(Path.GetFullPath(target), resolved);
        Assert.DoesNotContain(
            Path.Combine("captures-here", DcpCapture.DirectoryName),
            resolved!,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:captures")]
    [InlineData("\\captures")]
    public void ResolveDirectory_WithADriveRelativeOverride_Refuses(string driveRelative)
    {
        // ROOTED IS NOT ABSOLUTE ON WINDOWS, and an earlier version of this gate used
        // Path.IsPathRooted and so accepted both of these. Measured: "C:captures" and
        // "\captures" are rooted=True, fullyQualified=False, and GetFullPath resolves them
        // against a PER-DRIVE current directory - "\captures" landing on whichever drive the
        // process is running from, which under dotnet test is this repository's. That is #475's
        // trap reached through a different door, and .gitignore's blacklist would hide the
        // result rather than flag it.
        if (!OperatingSystem.IsWindows())
        {
            // These forms are ordinary relative paths off Windows, where the gate refuses them
            // for the plainer reason. Asserting the same outcome is still correct, but the
            // drive-relative semantics being pinned here only exist on Windows.
            Assert.Null(DcpCapture.ResolveDirectory(Path.GetTempPath(), driveRelative));
            return;
        }

        Assert.True(Path.IsPathRooted(driveRelative), "fixture no longer exercises the trap");
        Assert.False(Path.IsPathFullyQualified(driveRelative), "fixture no longer exercises the trap");

        Assert.Null(DcpCapture.ResolveDirectory(Path.GetTempPath(), driveRelative));
    }

    [Fact]
    public void DescribeLocation_WhenRedirected_NamesTheOverrideRatherThanTheDefaultRoot()
    {
        // Naming %LOCALAPPDATA% for a file that went somewhere else misdirects on exactly the CI
        // path the troubleshooting guide recommends - the one place nobody can check by looking.
        var windows = DcpCapture.DescribeLocation(
            "x.log", isWindows: true, DcpCaptureRoot.EnvironmentOverride);
        var unix = DcpCapture.DescribeLocation(
            "x.log", isWindows: false, DcpCaptureRoot.EnvironmentOverride);

        Assert.Contains(DcpCapture.DirectoryOverrideVariable, windows, StringComparison.Ordinal);
        Assert.Contains(DcpCapture.DirectoryOverrideVariable, unix, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCALAPPDATA", windows, StringComparison.Ordinal);

        // Still a token, never a resolved path - MINOR-1's guarantee is unchanged.
        Assert.False(Path.IsPathFullyQualified(windows));
        Assert.False(Path.IsPathFullyQualified(unix));
    }

    [Fact]
    public void DescribeLocation_WhenTheHostSuppliedTheDirectory_NamesNoVariableAtAll()
    {
        // The third root, and it exists because naming a variable that is NOT SET is the same
        // defect as naming the wrong directory: an operator pastes `$VOUCHFX_DCP_CAPTURE_DIR/x.log`
        // into a shell and gets `/x.log`. Only the drills reach this state - production never
        // passes an explicit directory - but a drill that asserts on a misleading token teaches
        // the token is fine.
        foreach (var isWindows in new[] { true, false })
        {
            var token = DcpCapture.DescribeLocation(
                "dcp-capture-x.log", isWindows, DcpCaptureRoot.HostSupplied);

            Assert.Equal("dcp-capture-x.log", token);
            Assert.DoesNotContain(
                DcpCapture.DirectoryOverrideVariable, token, StringComparison.Ordinal);
            Assert.DoesNotContain("LOCALAPPDATA", token, StringComparison.Ordinal);
            Assert.DoesNotContain("XDG_DATA_HOME", token, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FlushOnFailureAsync_WithBothADirectoryAndTheVariable_NamesTheRootTheFileIsIn()
    {
        // WriteAsync resolves its target as `directory ?? ResolveDirectory()`, so the ARGUMENT
        // decides where the file lands. The token has to agree. When it did not - the variable
        // outranking the argument here while the argument outranked it there - the file went to
        // one place and the summary named another, which is exactly the misdirection
        // DescribeLocation exists to prevent, committed by its own caller.
        var original = Environment.GetEnvironmentVariable(DcpCapture.DirectoryOverrideVariable);
        var decoy = Path.Combine(Path.GetTempPath(), "vouchfx-decoy-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(DcpCapture.DirectoryOverrideVariable, decoy);
        try
        {
            var recorder = new DcpFlightRecorder();
            var ex = new InvalidDataException("boom");

            await DcpCapture.FlushOnFailureAsync(
                recorder, ex, DateTimeOffset.UnixEpoch, _scratch);

            // The file really is in the argument's directory, and the decoy was never created.
            Assert.NotEmpty(Directory.GetFiles(
                _scratch, DcpCapture.FileNamePrefix + "*" + DcpCapture.FileNameSuffix));
            Assert.False(Directory.Exists(decoy));

            // ... so the summary must not name the variable that did NOT decide it.
            var summary = DcpCapture.Read(ex);
            Assert.NotNull(summary);
            Assert.DoesNotContain(
                DcpCapture.DirectoryOverrideVariable, summary!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DcpCapture.DirectoryOverrideVariable, original);
        }
    }

    [Fact]
    public async Task FlushOnFailureAsync_WithAnInjectedDirectoryAndNoOverrideSet_NamesNoVariable()
    {
        // The end-to-end shape of the row above, through the production flush: the drills inject a
        // directory without setting the variable, and the summary must not claim the variable put
        // it there.
        var original = Environment.GetEnvironmentVariable(DcpCapture.DirectoryOverrideVariable);
        Environment.SetEnvironmentVariable(DcpCapture.DirectoryOverrideVariable, null);
        try
        {
            var recorder = new DcpFlightRecorder();
            var ex = new InvalidDataException("boom");

            await DcpCapture.FlushOnFailureAsync(
                recorder, ex, DateTimeOffset.UnixEpoch, _scratch);

            var summary = DcpCapture.Read(ex);
            Assert.NotNull(summary);
            Assert.Contains("dcp-capture: dcp-capture-", summary!, StringComparison.Ordinal);
            Assert.DoesNotContain(
                DcpCapture.DirectoryOverrideVariable, summary!, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DcpCapture.DirectoryOverrideVariable, original);
        }
    }

    [Fact]
    public void ResolveDirectory_WithARelativeOverride_RefusesRatherThanFallingBackToThePerUserRoot()
    {
        // A silent downgrade is the worst of the three outcomes: the operator redirected
        // captures, finds none where they pointed, and concludes the failure never wrote one.
        // Refusing means the annex says "not written" and names the variable.
        Assert.Null(DcpCapture.ResolveDirectory(
            localApplicationData: Path.GetTempPath(), overrideDirectory: "relative/captures"));

        var original = Environment.GetEnvironmentVariable(DcpCapture.DirectoryOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                DcpCapture.DirectoryOverrideVariable, "relative/captures");

            Assert.Equal(
                DcpCapture.DirectoryOverrideVariable + " is not an absolute path",
                DcpCapture.NotWrittenReason(writtenName: null, directoryArgument: null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DcpCapture.DirectoryOverrideVariable, original);
        }
    }

    [Fact]
    public void ResolveDirectory_WithARelativeRoot_Refuses()
    {
        // #475's trap: Path.Combine on a relative first segment yields a RELATIVE path, which
        // resolves against the current directory - under dotnet test, inside this repository,
        // where .gitignore's blacklist would hide the stray artefact rather than flag it.
        Assert.Null(DcpCapture.ResolveDirectory("relative/appdata"));
        Assert.Null(DcpCapture.ResolveDirectory("appdata"));
    }

    // -----------------------------------------------------------------------
    // File naming, location token, retention
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildFileName_IsPrefixedSuffixedAndSortsChronologicallyAsAString()
    {
        var earlier = DcpCapture.BuildFileName(
            new DateTimeOffset(2026, 8, 31, 9, 5, 4, 7, TimeSpan.Zero));
        var later = DcpCapture.BuildFileName(
            new DateTimeOffset(2026, 8, 31, 10, 5, 4, 7, TimeSpan.Zero));

        Assert.Equal("dcp-capture-20260831T090504007Z.log", earlier);
        Assert.EndsWith(DcpCapture.FileNameSuffix, earlier, StringComparison.Ordinal);
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void DescribeLocation_EmitsAPlatformTokenAndNeverAResolvedPath()
    {
        // The resolved path carries the operator's account name, and this string reaches
        // report.html and results.xml - uploaded with if: always() and world-downloadable on a
        // public repository. The token tells the operator where to look and nothing about who
        // they are.
        var windows = DcpCapture.DescribeLocation("dcp-capture-x.log", isWindows: true);
        var unix = DcpCapture.DescribeLocation("dcp-capture-x.log", isWindows: false);

        Assert.Equal("%LOCALAPPDATA%\\vouchfx\\dcp-capture-x.log", windows);

        // The non-Windows token spells the FALLBACK out. The bare `$XDG_DATA_HOME/...` it used to
        // emit is wrong in the common case: that variable is unset on an ordinary Linux desktop
        // and on every CI runner here, so pasting the token into a shell yields
        // `/vouchfx/dcp-capture-x.log` - a path at the filesystem root, which is neither where the
        // file is nor somewhere the operator can write. It also contradicted the troubleshooting
        // guide's own table. The form below is the same rule .NET applies for
        // SpecialFolder.LocalApplicationData on Unix, so the token resolves to the directory the
        // file was actually written to whether or not the variable is set.
        Assert.Equal(
            "${XDG_DATA_HOME:-$HOME/.local/share}/vouchfx/dcp-capture-x.log", unix);

        foreach (var token in new[] { windows, unix })
        {
            Assert.DoesNotContain(":\\Users", token, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/home/", token, StringComparison.OrdinalIgnoreCase);
            Assert.False(Path.IsPathRooted(token), "location token must not be a rooted path");
        }
    }

    [Fact]
    public void SelectForDeletion_KeepsTheNewestAndNamesTheRestForDeletion()
    {
        var names = new[]
        {
            "dcp-capture-20260101T000000000Z.log",
            "dcp-capture-20260601T000000000Z.log",
            "dcp-capture-20260301T000000000Z.log",
            "dcp-capture-20260501T000000000Z.log",
            "dcp-capture-20260201T000000000Z.log",
            "dcp-capture-20260401T000000000Z.log",
        };

        var doomed = DcpCapture.SelectForDeletion(names, retain: 5, justWritten: null);

        Assert.Single(doomed);
        Assert.Equal("dcp-capture-20260101T000000000Z.log", doomed[0]);
    }

    [Fact]
    public void SelectForDeletion_NeverDeletesTheCaptureJustWritten_EvenWhenItSortsOldest()
    {
        // Ordering is by NAME, and a name is a timestamp: a host clock that stepped backwards
        // makes the newest file sort OLDEST, and retention would then delete the capture the
        // current failure just produced - the one occasion on which losing a capture costs
        // everything. Excluding it by name is what makes the ordering safe to rely on.
        var justWritten = "dcp-capture-20200101T000000000Z.log";
        var names = new[]
        {
            "dcp-capture-20260601T000000000Z.log",
            "dcp-capture-20260501T000000000Z.log",
            justWritten,
        };

        var doomed = DcpCapture.SelectForDeletion(names, retain: 2, justWritten: justWritten);

        // Kept: the just-written one (never a candidate) and the newest other. Deleted: the
        // remaining one. The just-written file COUNTS against the budget, so retain means
        // "files left on disk", not "others left beside the new one".
        Assert.DoesNotContain(justWritten, doomed);
        Assert.Equal("dcp-capture-20260501T000000000Z.log", Assert.Single(doomed));
    }

    [Fact]
    public void SelectForDeletion_ReservesTheJustWrittenFile_EvenWhenTheListingDoesNotSeeIt()
    {
        // "retain means files left on disk" has to hold whether or not the directory enumeration
        // happened to include the file that was written a moment earlier - it can miss it on a
        // filesystem that has not settled, or if the listing was taken first. Deriving the
        // reservation from a COUNT COMPARISON made it hold only in the lucky case: with the new
        // file absent from the listing, nothing was reserved and retain + 1 files were left,
        // quietly making every stated retention figure off by one in exactly the burst where
        // retention is under most pressure.
        var justWritten = "dcp-capture-20260701T000000000Z.log";
        var listingWithoutIt = new[]
        {
            "dcp-capture-20260601T000000000Z.log",
            "dcp-capture-20260501T000000000Z.log",
            "dcp-capture-20260401T000000000Z.log",
        };

        var doomed = DcpCapture.SelectForDeletion(
            listingWithoutIt, retain: 2, justWritten: justWritten);

        // Two files survive in total: the just-written one and the newest of the listing.
        var survivors = 1 + listingWithoutIt.Length - doomed.Count;

        Assert.Equal(2, survivors);
        Assert.Equal(2, doomed.Count);
        Assert.DoesNotContain("dcp-capture-20260601T000000000Z.log", doomed);
    }

    [Fact]
    public void RetainedFiles_CoversTheMeasuredReproductionWindow()
    {
        // #420's second occurrence was EIGHT consecutive topology-start failures in one session.
        // Retention below that number destroys captures the window itself produced, which is the
        // one thing this feature exists to prevent.
        Assert.True(
            DcpCapture.RetainedFiles >= 8,
            $"retention ({DcpCapture.RetainedFiles}) is below #420's measured 8-failure window");
    }

    // -----------------------------------------------------------------------
    // Writing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteAsync_AgainstTheRealFilesystem_WritesTheCaptureAndPrunesToTheBound()
    {
        var written = new List<string>();
        for (var i = 0; i < DcpCapture.RetainedFiles + 3; i++)
        {
            var name = await DcpCapture.WriteAsync(
                "capture " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(i),
                _scratch);

            Assert.NotNull(name);
            written.Add(name!);
        }

        // Returns the NAME, never the resolved path (see DescribeLocation).
        Assert.All(written, n => Assert.False(Path.IsPathRooted(n)));

        var remaining = Directory
            .EnumerateFiles(_scratch, DcpCapture.FileNamePrefix + "*" + DcpCapture.FileNameSuffix)
            .Select(Path.GetFileName)
            .ToList();

        var oldest = written[0];
        var newest = written[^1];
        Assert.Equal(DcpCapture.RetainedFiles, remaining.Count);
        Assert.DoesNotContain(oldest, remaining);
        Assert.Contains(newest, remaining);
        Assert.Equal(
            "capture " + (DcpCapture.RetainedFiles + 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
            File.ReadAllText(Path.Combine(_scratch, written[^1])));
    }

    [Fact]
    public async Task WriteAsync_OnAPlatformWithFileModes_LeavesTheCaptureAndDirectoryOwnerOnly()
    {
        var name = await DcpCapture.WriteAsync("body", DateTimeOffset.UnixEpoch, _scratch);
        Assert.NotNull(name);

        if (OperatingSystem.IsWindows())
        {
            // Windows has no Unix file mode; the per-user root's ACL already restricts it, and
            // asserting a mode here would be asserting nothing. The Linux CI leg runs this same
            // non-docker suite and DOES exercise the branch below.
            return;
        }

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_scratch, name!)));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_scratch));
    }

    [Fact]
    public void WriteCore_OnASameMillisecondCollision_WritesUnderASuffixedName()
    {
        // Two failures inside the same millisecond otherwise overwrite silently, and the one
        // lost would be the EARLIER - the first occurrence in a burst, the most interesting one.
        var taken = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine("dir", "dcp-capture-20260831T120000000Z.log"),
        };

        var name = DcpCapture.WriteCore(
            "dir",
            "dcp-capture-20260831T120000000Z.log",
            "content",
            retain: 12,
            createDirectory: _ => { },
            createFile: (p, _) => taken.Add(p),
            listCaptureFileNames: _ => Array.Empty<string>(),
            deleteFile: _ => { });

        Assert.Equal("dcp-capture-20260831T120000000Z-2.log", name);
    }

    [Fact]
    public void WriteCore_WhenEveryCandidateNameIsTaken_GivesUpRatherThanLooping()
    {
        var name = DcpCapture.WriteCore(
            "dir",
            "dcp-capture-20260831T120000000Z.log",
            "content",
            retain: 12,
            createDirectory: _ => { },
            createFile: (_, _) => false,
            listCaptureFileNames: _ => Array.Empty<string>(),
            deleteFile: _ => { });

        Assert.Null(name);
    }

    [Fact]
    public void WriteCore_WhenTheWriteThrows_ReturnsNullRatherThanPropagating()
    {
        // This runs on the failure path of a topology start, where the caller is already
        // carrying the real exception and is about to rethrow it. A disk-full or permission
        // failure must not replace that exception with one about the diagnostic.
        var name = DcpCapture.WriteCore(
            "dir",
            "file.log",
            "content",
            retain: 12,
            createDirectory: _ => { },
            createFile: (_, _) => throw new IOException("disk full"),
            listCaptureFileNames: _ => Array.Empty<string>(),
            deleteFile: _ => { });

        Assert.Null(name);
    }

    [Fact]
    public void WriteCore_WhenRetentionThrows_StillReportsTheCaptureItJustWrote()
    {
        // Pruning is housekeeping; the capture is the point. Losing the former must never cost
        // the latter, which is why retention sits in its own guarded block after the write.
        var name = DcpCapture.WriteCore(
            "dir",
            "file.log",
            "content",
            retain: 12,
            createDirectory: _ => { },
            createFile: (_, _) => true,
            listCaptureFileNames: _ => throw new UnauthorizedAccessException("no listing"),
            deleteFile: _ => { });

        Assert.Equal("file.log", name);
    }

    // -----------------------------------------------------------------------
    // The annotation and the detail annex
    // -----------------------------------------------------------------------

    [Fact]
    public void AttachAndRead_RoundTripWithoutTouchingTheExceptionItself()
    {
        var ex = new InvalidDataException("Service broker should have valid address at this point");
        var originalMessage = ex.Message;

        DcpCapture.Attach(ex, "dcp-capture: %LOCALAPPDATA%\\vouchfx\\dcp-capture-x.log");

        Assert.Equal("dcp-capture: %LOCALAPPDATA%\\vouchfx\\dcp-capture-x.log", DcpCapture.Read(ex));

        // The classifier's every heuristic reads Message, so the annotation must leave it alone.
        Assert.Equal(originalMessage, ex.Message);
        Assert.IsType<InvalidDataException>(ex);
    }

    [Fact]
    public void Read_OnAnUnannotatedException_IsNull()
    {
        Assert.Null(DcpCapture.Read(new InvalidOperationException("nothing to see")));
        Assert.Null(DcpCapture.Read(null));
    }

    [Theory]
    [InlineData("Service broker-https should have valid address at this point", true)]
    [InlineData("SHOULD HAVE VALID ADDRESS", true)]
    [InlineData("warn: Unable to allocate a network port for service 'x'", true)]
    [InlineData("unable to allocate a network port", true)]
    [InlineData("Stopped waiting for resource 'db' to become healthy", false)]
    [InlineData("failed to pull image: manifest unknown", false)]
    [InlineData("could not allocate a network buffer", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MentionsKnownFault_MatchesBothSignaturesAndNothingElse(string? message, bool expected)
    {
        Assert.Equal(expected, DcpCapture.MentionsKnownFault(message));
    }

    [Fact]
    public void KnownFaultNote_SaysSomethingDifferentOffWindows_AndNeverAdvisesReRunningThere()
    {
        // #420's own record is explicit that the Linux CI runners never reproduced it - 134
        // container-publishing tests, zero failures, neither signature in the logs. "Re-run
        // before investigating" is therefore false advice off Windows: it would train someone to
        // re-run past a fault nobody has seen self-clear on their platform.
        var windows = DcpCapture.KnownFaultNote(isWindows: true);
        var other = DcpCapture.KnownFaultNote(isWindows: false);

        Assert.NotEqual(windows, other);

        // The Windows note carries the REMEDY, because the mechanism is a Windows ACL ownership
        // check on DCP's state-store directory.
        Assert.Contains("state store", windows, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("state.elevated", windows, StringComparison.Ordinal);

        // And it must NOT advise a re-run. That was the note's original advice, written when the
        // fault looked transient; the cause is now known to be a deterministic refusal, so an
        // elevated session that hits it will hit it identically forever. Telling that operator to
        // re-run costs two minutes an attempt and never converges - which is why the absence is
        // pinned rather than left to review.
        Assert.DoesNotContain("Re-run before", windows, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT transient", windows, StringComparison.Ordinal);

        // Off Windows the remedy would be misleading advice, so it is withheld and the signature
        // is reported as new instead.
        Assert.Contains("NEW observation", other, StringComparison.Ordinal);
        Assert.DoesNotContain("state.elevated", other, StringComparison.Ordinal);

        Assert.All(windows + other, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void BuildSummary_NamesTheLocationWhenThereIsOneAndTheReasonWhenThereIsNot()
    {
        var tail = new[] { "warn line one", "fail line two" };

        var withLocation = DcpCapture.BuildSummary("%LOCALAPPDATA%\\vouchfx\\dcp-capture-1.log", tail);

        Assert.Contains(
            "dcp-capture: %LOCALAPPDATA%\\vouchfx\\dcp-capture-1.log",
            withLocation,
            StringComparison.Ordinal);
        Assert.Contains("warn line one", withLocation, StringComparison.Ordinal);

        Assert.Equal(
            "dcp-capture: not written",
            DcpCapture.BuildSummary(null, Array.Empty<string>()));

        Assert.Equal(
            "dcp-capture: not written (no per-user directory)",
            DcpCapture.BuildSummary(null, Array.Empty<string>(), "no per-user directory"));
    }

    [Fact]
    public void BuildAnnex_AddsTheNoteOnlyForTheSignatureAndTheSummaryOnlyWhenAttached()
    {
        var plain = new InvalidOperationException("something unrelated failed");
        Assert.Equal(string.Empty, DcpCapture.BuildAnnex(plain.Message, plain, isWindows: true));

        var signatureOnly = new InvalidDataException(
            "Service x should have valid address at this point");
        var noteOnly = DcpCapture.BuildAnnex(signatureOnly.Message, signatureOnly, isWindows: true);
        Assert.Contains("issue 420", noteOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("dcp-capture:", noteOnly, StringComparison.Ordinal);

        var annotated = new InvalidOperationException("some other startup failure");
        DcpCapture.Attach(annotated, "dcp-capture: %LOCALAPPDATA%\\vouchfx\\x.log");
        var summaryOnly = DcpCapture.BuildAnnex(annotated.Message, annotated, isWindows: true);
        Assert.DoesNotContain("issue 420", summaryOnly, StringComparison.Ordinal);
        Assert.Contains("dcp-capture: %LOCALAPPDATA%", summaryOnly, StringComparison.Ordinal);

        var both = new InvalidDataException("Service x should have valid address at this point");
        DcpCapture.Attach(both, "dcp-capture: %LOCALAPPDATA%\\vouchfx\\y.log");
        var everything = DcpCapture.BuildAnnex(both.Message, both, isWindows: true);
        Assert.Contains("issue 420", everything, StringComparison.Ordinal);
        Assert.Contains("dcp-capture: %LOCALAPPDATA%", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAnnex_CarriesThePlatformAppropriateNote()
    {
        // Taken as an argument rather than read from the ambient platform precisely so both
        // notes are exercised on one machine. A platform-conditional string that only one CI leg
        // can reach is a string nobody checks.
        var ex = new InvalidDataException("Service x should have valid address at this point");

        Assert.Contains(
            "state.elevated",
            DcpCapture.BuildAnnex(ex.Message, ex, isWindows: true),
            StringComparison.Ordinal);
        Assert.Contains(
            "NEW observation",
            DcpCapture.BuildAnnex(ex.Message, ex, isWindows: false),
            StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // The root-cause scan (issue #420, after the cause was established)
    // -----------------------------------------------------------------------

    [Fact]
    public void MentionsStateStoreRefusal_MatchesTheMeasuredDcpLine_AndRequiresBothHalves()
    {
        // The verbatim line from the capture that root-caused #420: DEBUG level, category
        // Aspire.Hosting.Dcp.dcp. It reaches neither the exception message nor the Warning-level
        // tail, which is why the scan looks at the buffer instead of either of those.
        var real = Entry(
            "the program finished with an error {\"ExitCode\": 1, \"error\": \"failed to "
            + "initialize state store: could not prepare state store directory "
            + "'C:\\\\Users\\\\User\\\\.dcp\\\\state.elevated': directory has invalid ownership: "
            + "directory owner does not match current user or token owner\"}",
            LogLevel.Debug);

        Assert.True(DcpCapture.MentionsStateStoreRefusal(new[] { real }));

        // Both halves are required, so a line mentioning either phrase alone cannot fire a
        // remedy that would then be wrong.
        Assert.False(DcpCapture.MentionsStateStoreRefusal(
            new[] { Entry("preparing the state store directory", LogLevel.Debug) }));
        Assert.False(DcpCapture.MentionsStateStoreRefusal(
            new[] { Entry("certificate has invalid ownership", LogLevel.Debug) }));
        Assert.False(DcpCapture.MentionsStateStoreRefusal(Array.Empty<DcpFlightEntry>()));
        Assert.False(DcpCapture.MentionsStateStoreRefusal(null));
    }

    [Fact]
    public void BuildSummary_WhenTheRefusalWasSeen_LeadsWithTheRemedy()
    {
        var tail = new[] { new string('w', 200), new string('x', 200) };

        var summary = DcpCapture.BuildSummary(
            "%LOCALAPPDATA%\\vouchfx\\dcp-capture-1.log",
            tail,
            notWrittenReason: null,
            stateStoreRefusal: true);

        // FIRST, ahead of the location and the tail: the annex is truncated from the END, so a
        // remedy placed after a long tail is exactly the part that gets cut off.
        Assert.StartsWith("dcp-cause: ", summary, StringComparison.Ordinal);
        Assert.Contains("Re-own or delete", summary, StringComparison.Ordinal);

        // Absent unless actually seen - the scan must not editorialise.
        Assert.DoesNotContain(
            "dcp-cause: ",
            DcpCapture.BuildSummary("%LOCALAPPDATA%\\vouchfx\\x.log", tail),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlushOnFailureAsync_WhenTheBufferShowsTheRefusal_PutsTheRemedyInTheAnnex()
    {
        var recorder = new DcpFlightRecorder();
        DcpTestLog.Emit(
            recorder.CreateLogger("Aspire.Hosting.Dcp.dcp"),
            LogLevel.Debug,
            "the program finished with an error {\"ExitCode\": 1, \"error\": \"failed to "
            + "initialize state store: could not prepare state store directory "
            + "'C:\\\\Users\\\\User\\\\.dcp\\\\state.elevated': has invalid ownership\"}");

        var ex = new InvalidDataException("Service x should have valid address at this point");
        await DcpCapture.FlushOnFailureAsync(recorder, ex, DateTimeOffset.UnixEpoch, _scratch);

        var summary = DcpCapture.Read(ex);
        Assert.NotNull(summary);
        Assert.Contains("dcp-cause: ", summary!, StringComparison.Ordinal);

        // End to end: it survives into the classified Detail an operator actually reads.
        var info = OrchestrationErrorClassifier.Classify(ex, imageRef: null, resourceName: "x");
        Assert.Contains("Re-own or delete", info.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_WritesNoByteOrderMark_SoTheHeaderIsTheFirstThingInTheFile()
    {
        // MEASURED before the fix: Encoding.UTF8's encoder emits EF BB BF, so byte 0 of every
        // capture was a BOM and the header was not the first thing in it. That contradicts this
        // type's own printable-ASCII / one-character-one-byte premise, and it breaks the first
        // triage move anyone makes - grepping or head-ing the top of the file.
        var name = await DcpCapture.WriteAsync(
            "vouchfx DCP flight recorder capture\nbody\n", DateTimeOffset.UnixEpoch, _scratch);

        Assert.NotNull(name);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_scratch, name!));

        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "the capture file begins with a UTF-8 BOM; use UTF8Encoding(false), not Encoding.UTF8");

        Assert.Equal((byte)'v', bytes[0]);
        Assert.All(bytes, b => Assert.InRange(b, (byte)0x09, (byte)0x7E));
    }

    private static DcpFlightEntry Entry(string message, LogLevel level) =>
        DcpFlightEntry.Create(
            DateTimeOffset.UnixEpoch, level, "Aspire.Hosting.Dcp.dcp", message, exception: null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MaxAnnexLength_CoversTheWorstCaseComposition_SoTheTargetPathNeverTruncates(
        bool isWindows)
    {
        // The composed worst case, built from the constants rather than from a guess, because the
        // constants are what move. Every part is independently bounded, so the worst case is
        // arithmetic rather than a search.
        var note = DcpCapture.KnownFaultNote(isWindows);

        // 12 lines, each 32 characters, is the tail that maximises the joined length: the char
        // budget binds at 384 either way, and the maximum ENTRY count maximises the " ;; "
        // separators on top of it.
        var tail = Enumerable
            .Range(0, DcpCapture.TailEntryLimit)
            .Select(_ => new string(
                't', DcpCapture.TailCharLimit / DcpCapture.TailEntryLimit))
            .ToArray();

        Assert.Equal(DcpCapture.TailCharLimit, tail.Sum(l => l.Length));

        // The longest file name this type can produce: the timestamp layout plus the
        // same-millisecond collision suffix WriteCore appends.
        var fileName = Path.GetFileNameWithoutExtension(
                DcpCapture.BuildFileName(DateTimeOffset.UnixEpoch))
            + "-5" + DcpCapture.FileNameSuffix;

        // Both roots for this platform - the per-user default and the redirect - because which is
        // longer has changed once already.
        var locations = new[]
        {
            DcpCapture.DescribeLocation(fileName, isWindows, DcpCaptureRoot.PerUser),
            DcpCapture.DescribeLocation(fileName, isWindows, DcpCaptureRoot.EnvironmentOverride),
        };

        var worst = 0;
        var breakdown = string.Empty;
        foreach (var location in locations)
        {
            var summary = DcpCapture.BuildSummary(
                location, tail, notWrittenReason: null, stateStoreRefusal: true);
            var composed = note + " | " + summary;

            if (composed.Length > worst)
            {
                worst = composed.Length;
                breakdown =
                    $"note={note.Length}, remedy={DcpCapture.StateStoreRemedy.Length}, "
                    + $"location='{location}' ({location.Length}), summary={summary.Length}";
            }
        }

        Assert.True(
            worst <= DcpCapture.MaxAnnexLength,
            $"the #420 annex truncates on its own TARGET path (isWindows={isWindows}): the worst "
            + $"composition is {worst} characters against a MaxAnnexLength of "
            + $"{DcpCapture.MaxAnnexLength}, so {worst - DcpCapture.MaxAnnexLength} characters are "
            + "cut from the END - which is the NEWEST tail lines, the ones Tail() selects "
            + "newest-first precisely in order to keep. Truncation also defeats the exact-match "
            + "secret ledger for anything it touches. Raise MaxAnnexLength; it admits no extra log "
            + $"volume, because every part is separately bounded already. {breakdown}");
    }

    [Fact]
    public void BuildAnnex_IsBoundedAndPrintableAscii()
    {
        var ex = new InvalidDataException("Service x should have valid address at this point");
        DcpCapture.Attach(ex, "dcp-capture: " + new string('p', 4000));

        var annex = DcpCapture.BuildAnnex(ex.Message, ex, isWindows: true);

        Assert.True(
            annex.Length <= DcpCapture.MaxAnnexLength + 3,
            $"annex not bounded: {annex.Length}");
        Assert.All(annex, c => Assert.InRange(c, ' ', '~'));
    }

    // -----------------------------------------------------------------------
    // The whole failure-path flush
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FlushOnFailureAsync_WritesTheCapture_AnnotatesTheException_AndDropsTheRecorder()
    {
        var recorder = new DcpFlightRecorder();
        var logger = recorder.CreateLogger("Aspire.Hosting.Dcp.DcpExecutor");
        DcpTestLog.Emit(logger, LogLevel.Debug, "allocating host port for service broker-https");
        DcpTestLog.Emit(
            logger,
            LogLevel.Warning,
            "Unable to allocate a network port for service 'broker-https'; service may be unreachable");

        var ex = new InvalidDataException(
            "Service broker-https should have valid address at this point");

        await DcpCapture.FlushOnFailureAsync(
            recorder, ex, new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero), _scratch);

        // 1. The file holds the WHOLE buffer, Debug traffic included - the evidence #420 has
        //    never captured.
        var files = Directory.GetFiles(_scratch);
        Assert.Single(files);
        var body = File.ReadAllText(files[0]);
        Assert.Contains("allocating host port", body, StringComparison.Ordinal);
        Assert.Contains("Unable to allocate a network port", body, StringComparison.Ordinal);

        // 2. The exception carries a LOCATION TOKEN and a warning-level tail - never the
        //    resolved path, which would reach a public CI artifact carrying the account name.
        var summary = DcpCapture.Read(ex);
        Assert.NotNull(summary);
        Assert.Contains("dcp-capture-", summary!, StringComparison.Ordinal);
        Assert.DoesNotContain(_scratch, summary!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unable to allocate a network port", summary!, StringComparison.Ordinal);
        Assert.DoesNotContain("allocating host port", summary!, StringComparison.Ordinal);

        // 3. The recorder is dropped whether or not the write worked.
        Assert.True(recorder.IsDropped);
        Assert.Empty(recorder.Snapshot());
    }

    [Fact]
    public async Task FlushOnFailureAsync_CalledTwice_WritesOneFileOnly()
    {
        // Both the health-gate catch and the outer safety net call this, by design: the gate
        // path is what gets the tail into the detail, the net is what guarantees a file for
        // every other post-start failure. That only works if the second call is a no-op.
        var recorder = new DcpFlightRecorder();
        DcpTestLog.Emit(
            recorder.CreateLogger("Aspire.Hosting.Dcp.DcpExecutor"),
            LogLevel.Warning,
            "Unable to allocate a network port for service 'x'");

        var ex = new InvalidDataException("Service x should have valid address at this point");

        await DcpCapture.FlushOnFailureAsync(recorder, ex, DateTimeOffset.UnixEpoch, _scratch);
        await DcpCapture.FlushOnFailureAsync(recorder, ex, DateTimeOffset.UnixEpoch, _scratch);

        Assert.Single(Directory.GetFiles(_scratch));
    }

    [Fact]
    public async Task FlushOnFailureAsync_WhenTheDirectoryCannotBeUsed_StillAnnotatesWithTheTail()
    {
        var recorder = new DcpFlightRecorder();
        DcpTestLog.Emit(
            recorder.CreateLogger("Aspire.Hosting.Dcp.DcpExecutor"),
            LogLevel.Warning,
            "Unable to allocate a network port for service 'x'");

        // A path whose parent is a FILE cannot become a directory on any platform.
        var blocker = Path.Combine(_scratch, "blocker");
        Directory.CreateDirectory(_scratch);
        File.WriteAllText(blocker, "not a directory");

        var ex = new InvalidDataException("Service x should have valid address at this point");
        await DcpCapture.FlushOnFailureAsync(
            recorder, ex, DateTimeOffset.UnixEpoch, Path.Combine(blocker, "nested"));

        var summary = DcpCapture.Read(ex);
        Assert.NotNull(summary);
        Assert.Contains("dcp-capture: not written", summary!, StringComparison.Ordinal);
        Assert.Contains("Unable to allocate a network port", summary!, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessfulStartShape_WritesNoFileAtAll()
    {
        // The ready path in SuiteTopology is exactly this: fill the buffer while the topology
        // comes up, then drop without ever calling FlushOnFailureAsync. A healthy run must leave
        // nothing behind - no capture, no output.
        Directory.CreateDirectory(_scratch);

        var recorder = new DcpFlightRecorder();
        DcpTestLog.Emit(
            recorder.CreateLogger("Aspire.Hosting.Dcp.DcpExecutor"),
            LogLevel.Warning,
            "noisy but harmless");
        recorder.Dispose();

        Assert.Empty(Directory.GetFiles(_scratch));
    }
}
