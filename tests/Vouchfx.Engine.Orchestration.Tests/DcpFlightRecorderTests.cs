using Microsoft.Extensions.Logging;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Drills for the #420 DCP flight recorder: the bounded ring buffer, the tail it hands to an
/// Environment-error detail, the opt-out, and the provider-scoped filter composition that
/// decides what reaches it.
/// </summary>
/// <remarks>
/// <para>
/// None of these needs Docker, an Aspire host, or a topology — which is the point. The fault
/// they serve is not reproducible on demand (it has cleared on its own both times it has been
/// seen), so the recorder cannot be proven by triggering the thing it records. What CAN be
/// proven, deterministically and in milliseconds, is every property the capture depends on:
/// that the buffer stays bounded under load, that eviction is visible rather than silent, that
/// the tail is the newest warnings and not the oldest, and that the filter rules route DCP
/// traffic to the recorder and nothing else anywhere near the console.
/// </para>
/// <para>
/// The filter drill goes through <see cref="DcpFlightRecorder.Register"/> — the same method
/// <c>HeadlessTopology.StartAsync</c> calls — rather than restating the three rules here. A
/// drill that pinned a copy of the production wiring would pass while production drifted away
/// from it.
/// </para>
/// </remarks>
public sealed class DcpFlightRecorderTests
{
    // -----------------------------------------------------------------------
    // Bounds
    // -----------------------------------------------------------------------

    [Fact]
    public void Record_BeyondEntryLimit_EvictsOldestAndCountsTheEviction()
    {
        var recorder = new DcpFlightRecorder(entryLimit: 3, charLimit: 1_000_000);

        for (var i = 0; i < 10; i++)
        {
            recorder.Record(Entry("m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var snapshot = recorder.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal(7, recorder.EvictedCount);

        // Oldest evicted, newest kept, and still in chronological order.
        Assert.Equal("m7", snapshot[0].Message);
        Assert.Equal("m8", snapshot[1].Message);
        Assert.Equal("m9", snapshot[2].Message);
    }

    [Fact]
    public void Record_BeyondCharLimit_EvictsUntilTheBudgetHolds()
    {
        // A generous entry bound so the CHARACTER bound is unambiguously the one that binds.
        var recorder = new DcpFlightRecorder(entryLimit: 10_000, charLimit: 800);

        for (var i = 0; i < 40; i++)
        {
            recorder.Record(Entry(new string('x', 80)));
        }

        Assert.True(
            recorder.CharCount <= 800,
            $"character budget exceeded: {recorder.CharCount}");
        Assert.True(recorder.EvictedCount > 0, "no eviction was recorded");
        Assert.NotEmpty(recorder.Snapshot());
    }

    [Fact]
    public void Record_SingleEntryLargerThanTheCharBudget_IsRetainedRatherThanEvictingItself()
    {
        // The character bound always leaves the newest entry standing. Without that rule a
        // buffer whose budget is smaller than one entry would evict every entry as it arrived
        // and report a capture of nothing at all - the worst possible outcome for a recorder
        // that only ever runs when something has already gone wrong.
        var recorder = new DcpFlightRecorder(entryLimit: 10, charLimit: 10);

        recorder.Record(Entry(new string('y', 500)));

        var snapshot = recorder.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(0, recorder.EvictedCount);
    }

    [Fact]
    public void Create_LineLongerThanTheEntryCap_IsTruncated()
    {
        var entry = DcpFlightEntry.Create(
            DateTimeOffset.UnixEpoch,
            LogLevel.Warning,
            "Aspire.Hosting.Dcp.DcpExecutor",
            new string('z', DcpFlightEntry.MaxLineChars * 2),
            exception: null);

        Assert.True(
            entry.Line.Length <= DcpFlightEntry.MaxLineChars + 3,
            $"entry line not capped: {entry.Line.Length}");
        Assert.EndsWith("...", entry.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_PathologicalMessageAndException_BoundsRetainedCharsNotJustTheLine()
    {
        // The bound that actually protects the buffer, and the one the previous cap did NOT
        // deliver. RetainedChars charges Message and Exception as well as Line, so capping only
        // Line left the real bound at `charLimit + largest single message`: a 40 KiB Debug line
        // was billed 40 KiB while contributing 4 KiB of readable text, and a few of them emptied
        // the whole 128 Ki-character budget - the capture arriving as a handful of lines and a
        // large eviction count, on exactly the failure it exists to record.
        //
        // Line-only assertions cannot see this, which is why the sibling row above stayed green
        // throughout. This one asserts the CHARGE.
        var entry = DcpFlightEntry.Create(
            DateTimeOffset.UnixEpoch,
            LogLevel.Debug,
            "Aspire.Hosting.Dcp.DcpExecutor",
            new string('z', DcpFlightEntry.MaxLineChars * 10),
            new InvalidOperationException(new string('y', DcpFlightEntry.MaxLineChars * 10)));

        // Three capped components (line, message, exception) plus the category, with the
        // three-character truncation marker allowed on each.
        var bound = (3 * (DcpFlightEntry.MaxLineChars + 3)) + entry.Category.Length;

        Assert.True(
            entry.RetainedChars <= bound,
            $"a single entry is charged {entry.RetainedChars} characters against a bound of "
            + $"{bound}. One pathological log line can therefore evict every DCP warning around "
            + "it, which is precisely what MaxLineChars' remarks promise cannot happen. Cap the "
            + "COMPONENTS in Create, not only the rendered Line.");

        // And the buffer's own budget really is the buffer's budget: a run of these cannot push
        // CharCount beyond the limit by more than one entry's worth.
        var recorder = new DcpFlightRecorder(entryLimit: 64, charLimit: 32 * 1024);
        for (var i = 0; i < 32; i++)
        {
            recorder.Record(entry);
        }

        Assert.True(
            recorder.CharCount <= (32 * 1024) + bound,
            $"buffer overshot its character budget: {recorder.CharCount}");
    }

    // -----------------------------------------------------------------------
    // Thread safety
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Record_FromManyThreadsConcurrently_StaysBoundedAndLosesNoAccounting()
    {
        // DCP logs from background tasks, so entries arrive concurrently with each other and,
        // at the moment of failure, with the flush. This drill runs writers against a reader.
        const int Writers = 8;
        const int PerWriter = 500;
        var recorder = new DcpFlightRecorder(entryLimit: 64, charLimit: 1_000_000);

        var tasks = new List<Task>(Writers + 1);
        for (var w = 0; w < Writers; w++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (var i = 0; i < PerWriter; i++)
                {
                    recorder.Record(Entry("concurrent"));
                }
            }));
        }

        tasks.Add(Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                _ = recorder.Snapshot();
                _ = recorder.Tail(12, 384);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(64, recorder.Snapshot().Count);

        // Nothing is lost and nothing is double-counted: every entry either sits in the buffer
        // or was counted as an eviction. A lost increment under a race shows up here.
        Assert.Equal(Writers * PerWriter, recorder.Snapshot().Count + recorder.EvictedCount);
    }

    // -----------------------------------------------------------------------
    // Sanitising
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_NonAsciiAndMultiLineText_IsFlattenedToPrintableAscii()
    {
        var entry = DcpFlightEntry.Create(
            DateTimeOffset.UnixEpoch,
            LogLevel.Error,
            "Aspire—Hosting",
            "line one\r\nline two\ttabbed é",
            new InvalidOperationException("boom\nsecond line"));

        Assert.All(entry.Line, c => Assert.InRange(c, ' ', '~'));
        Assert.DoesNotContain('\n', entry.Line);
        Assert.DoesNotContain('\r', entry.Line);
        // Two spaces, not one: CR and LF are each replaced, never collapsed. A sanitiser that
        // collapsed runs would also collapse the spacing inside a DCP message.
        Assert.Contains("line one  line two tabbed ?", entry.Line, StringComparison.Ordinal);
        Assert.Contains("boom second line", entry.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatCapture_IsPrintableAsciiAndNamesTruncationWhenEntriesWereEvicted()
    {
        var recorder = new DcpFlightRecorder(entryLimit: 2, charLimit: 1_000_000);
        recorder.Record(Entry("first"));
        recorder.Record(Entry("second"));
        recorder.Record(Entry("third"));

        var capture = recorder.FormatCapture(DateTimeOffset.UnixEpoch);

        Assert.All(capture, c => Assert.True(c == '\n' || (c >= ' ' && c <= '~')));
        Assert.Contains("issue: 420", capture, StringComparison.Ordinal);
        Assert.Contains("TRUNCATED", capture, StringComparison.Ordinal);
        Assert.Contains("second", capture, StringComparison.Ordinal);
        Assert.Contains("third", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("first", capture, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Tail
    // -----------------------------------------------------------------------

    [Fact]
    public void Tail_KeepsTheNewestWarningsInChronologicalOrderAndDropsInformation()
    {
        var recorder = new DcpFlightRecorder();
        recorder.Record(Entry("old warning", LogLevel.Warning));
        recorder.Record(Entry("chatter", LogLevel.Information));
        recorder.Record(Entry("debug detail", LogLevel.Debug));
        recorder.Record(Entry("newer warning", LogLevel.Warning));
        recorder.Record(Entry("the throw", LogLevel.Error));

        var tail = recorder.Tail(maxEntries: 2, maxChars: 10_000);

        Assert.Equal(2, tail.Count);
        Assert.Contains("newer warning", tail[0], StringComparison.Ordinal);
        Assert.Contains("the throw", tail[1], StringComparison.Ordinal);
        Assert.DoesNotContain(tail, l => l.Contains("chatter", StringComparison.Ordinal));
        Assert.DoesNotContain(tail, l => l.Contains("debug detail", StringComparison.Ordinal));
    }

    [Fact]
    public void Tail_IsBoundedByBothTheLineCountAndTheCharacterBudget()
    {
        var recorder = new DcpFlightRecorder();
        for (var i = 0; i < 50; i++)
        {
            recorder.Record(Entry(new string('w', 200), LogLevel.Warning));
        }

        var byCount = recorder.Tail(maxEntries: 3, maxChars: 1_000_000);
        Assert.Equal(3, byCount.Count);

        var byChars = recorder.Tail(maxEntries: 50, maxChars: 300);
        Assert.True(
            byChars.Sum(l => l.Length) <= 300,
            $"tail character budget exceeded: {byChars.Sum(l => l.Length)}");

        // Each line is itself capped before being charged against the budget.
        Assert.All(byChars, l => Assert.True(l.Length <= DcpFlightRecorder.TailLineChars + 3));
    }

    [Fact]
    public void Tail_WithNoWarnings_IsEmpty()
    {
        var recorder = new DcpFlightRecorder();
        recorder.Record(Entry("just chatter", LogLevel.Information));

        Assert.Empty(recorder.Tail(12, 384));
    }

    // -----------------------------------------------------------------------
    // Drop on success
    // -----------------------------------------------------------------------

    [Fact]
    public void Dispose_ClearsTheBufferAndStopsRecording()
    {
        // The success path in HeadlessTopology.StartAsync does exactly this: dispose and drop.
        // The host goes on running and keeps the ILogger instances this provider handed out, so
        // "stopped recording" has to hold for those loggers too, not just for the buffer.
        var recorder = new DcpFlightRecorder();
        var logger = recorder.CreateLogger("Aspire.Hosting.Dcp.DcpExecutor");
        DcpTestLog.Emit(logger, LogLevel.Warning, "before");

        Assert.NotEmpty(recorder.Snapshot());

        recorder.Dispose();

        Assert.Empty(recorder.Snapshot());
        Assert.False(logger.IsEnabled(LogLevel.Warning));

        DcpTestLog.Emit(logger, LogLevel.Warning, "after");
        recorder.Record(Entry("also after"));

        Assert.Empty(recorder.Snapshot());

        // Idempotent: the host's own service provider may dispose it a second time.
        recorder.Dispose();
    }

    [Fact]
    public void Log_AfterTheDrop_DoesNotEvenInvokeTheFormatter()
    {
        // The whole cost claim rests on this. The logger factory does NOT consult a provider
        // logger's IsEnabled before calling Log, and the filter rule enabling DCP Debug lives in
        // the host's options for the life of the host - so without an early return, every Debug
        // statement in a long healthy run (a --watch session runs for hours) would still render
        // a string, allocate an entry and take the lock, only to be discarded inside Record.
        //
        // Asserting "nothing was recorded" would NOT catch that: the buffer is empty either way.
        // Asserting the formatter was never called is what distinguishes a cheap no-op from an
        // expensive one.
        var recorder = new DcpFlightRecorder();
        var logger = recorder.CreateLogger("Aspire.Hosting.Dcp.DcpExecutor");
        var formatterCalls = 0;

        string Formatter(string state, Exception? _)
        {
            formatterCalls++;
            return state;
        }

        logger.Log(LogLevel.Debug, new EventId(0), "before", null, Formatter);
        Assert.Equal(1, formatterCalls);

        recorder.Dispose();

        logger.Log(LogLevel.Debug, new EventId(0), "after", null, Formatter);

        Assert.Equal(1, formatterCalls);
        Assert.True(recorder.IsDropped);
        Assert.False(logger.IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void RetainedChars_CountsEveryStringTheEntryHolds_NotJustTheRenderedLine()
    {
        // The record keeps Category, Message and Exception alongside the rendered Line, so
        // charging only Line.Length under-counts the buffer's real footprint by roughly half -
        // a budget named for memory that silently stops bounding it.
        var entry = DcpFlightEntry.Create(
            DateTimeOffset.UnixEpoch,
            LogLevel.Warning,
            "Aspire.Hosting.Dcp.DcpExecutor",
            "a message worth counting",
            new InvalidOperationException("and an exception"));

        Assert.Equal(
            entry.Line.Length + entry.Category.Length + entry.Message.Length + entry.Exception!.Length,
            entry.RetainedChars);
        Assert.True(entry.RetainedChars > entry.Line.Length);

        var recorder = new DcpFlightRecorder();
        recorder.Record(entry);
        Assert.Equal(entry.RetainedChars, recorder.CharCount);
    }

    [Fact]
    public void FormatCapture_HeaderCountsAgreeWithTheSnapshotItPrints()
    {
        // The header is precisely what tells a reader the record is truncated, so its entry and
        // eviction counts have to come from ONE lock acquisition. Two reads would let a
        // concurrent DCP thread evict in between and pair a post-eviction list with a
        // pre-eviction count - a diagnostic misreporting its own completeness.
        var recorder = new DcpFlightRecorder(entryLimit: 2, charLimit: 1_000_000);
        recorder.Record(Entry("first"));
        recorder.Record(Entry("second"));
        recorder.Record(Entry("third"));

        var (entries, evicted) = recorder.SnapshotWithEvictions();
        var capture = recorder.FormatCapture(DateTimeOffset.UnixEpoch);

        Assert.Equal(2, entries.Count);
        Assert.Equal(1, evicted);
        Assert.Contains(
            $"entries: {entries.Count}, evicted: {evicted}",
            capture,
            StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Opt-out
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("0", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("00", false)]
    [InlineData(" 0", false)]
    [InlineData("0 ", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("1", false)]
    public void IsDisabledBy_OnlyTheExactZeroStringDisarmsTheRecorder(string? value, bool expected)
    {
        Assert.Equal(expected, DcpFlightRecorder.IsDisabledBy(value));
    }

    [Fact]
    public void CreateUnlessDisabled_HonoursTheEnvironmentVariableInBothDirections()
    {
        var original = Environment.GetEnvironmentVariable(DcpFlightRecorder.OptOutVariable);
        try
        {
            Environment.SetEnvironmentVariable(DcpFlightRecorder.OptOutVariable, "0");
            Assert.Null(DcpFlightRecorder.CreateUnlessDisabled());

            Environment.SetEnvironmentVariable(DcpFlightRecorder.OptOutVariable, "1");
            using (var armed = DcpFlightRecorder.CreateUnlessDisabled())
            {
                Assert.NotNull(armed);
            }

            // Unset is armed: the whole point of the default is that nobody has to know.
            Environment.SetEnvironmentVariable(DcpFlightRecorder.OptOutVariable, null);
            using (var byDefault = DcpFlightRecorder.CreateUnlessDisabled())
            {
                Assert.NotNull(byDefault);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(DcpFlightRecorder.OptOutVariable, original);
        }
    }

    // -----------------------------------------------------------------------
    // Filter composition (the production wiring, through the production method)
    // -----------------------------------------------------------------------

    [Fact]
    public void Register_RoutesDcpAtDebug_OtherAspireAtWarning_AndNothingElseAtAll()
    {
        using var recorder = new DcpFlightRecorder();
        using var factory = LoggerFactory.Create(lb => DcpFlightRecorder.Register(lb, recorder));

        var dcp = factory.CreateLogger("Aspire.Hosting.Dcp.DcpExecutor");
        var aspire = factory.CreateLogger("Aspire.Hosting.DistributedApplication");
        var unrelated = factory.CreateLogger("Microsoft.Extensions.Hosting.Lifetime");

        DcpTestLog.Emit(dcp, LogLevel.Debug, "dcp-debug");
        DcpTestLog.Emit(dcp, LogLevel.Warning, "dcp-warning");
        DcpTestLog.Emit(aspire, LogLevel.Information, "aspire-information");
        DcpTestLog.Emit(aspire, LogLevel.Warning, "aspire-warning");
        DcpTestLog.Emit(unrelated, LogLevel.Error, "unrelated-error");

        var messages = recorder.Snapshot().Select(e => e.Message).ToList();

        // 3: the DCP Debug traffic that is the whole reason for the recorder ...
        Assert.Contains("dcp-debug", messages);
        Assert.Contains("dcp-warning", messages);

        // 2: the belt-and-braces Aspire rule, at Warning and not below ...
        Assert.Contains("aspire-warning", messages);
        Assert.DoesNotContain("aspire-information", messages);

        // 1: ... and the floor, which keeps everything else out even at Error.
        Assert.DoesNotContain("unrelated-error", messages);
    }

    [Fact]
    public void Register_RoutesDcpAtDebug_WhicheverOrderTheRulesWereAdded()
    {
        // An earlier version of Register's remarks claimed registration order was load-bearing -
        // that the broad "Aspire" rule had to precede the narrow "Aspire.Hosting.Dcp" one or
        // every DCP category would settle at Warning. MEASURED FALSE on the
        // Microsoft.Extensions.Logging this solution resolves: rule selection takes the LONGEST
        // MATCHING CATEGORY whatever the order. This drill is what stops the false claim coming
        // back, by making the reversed order a passing configuration rather than a theory.
        using var production = new DcpFlightRecorder();
        using var productionFactory = LoggerFactory.Create(
            lb => DcpFlightRecorder.Register(lb, production));

        using var reversed = new DcpFlightRecorder();
        using var reversedFactory = LoggerFactory.Create(lb => lb
            .AddProvider(reversed)
            .AddFilter<DcpFlightRecorder>(category: null, LogLevel.None)
            .AddFilter<DcpFlightRecorder>(
                DcpFlightRecorder.DcpCategoryPrefix, LogLevel.Debug)
            .AddFilter<DcpFlightRecorder>(
                DcpFlightRecorder.AspireCategoryPrefix, LogLevel.Warning));

        foreach (var (factory, recorder, which) in new[]
        {
            (productionFactory, production, "production order"),
            (reversedFactory, reversed, "reversed order"),
        })
        {
            var dcp = factory.CreateLogger("Aspire.Hosting.Dcp.DcpExecutor");
            var aspire = factory.CreateLogger("Aspire.Hosting.DistributedApplication");

            Assert.True(dcp.IsEnabled(LogLevel.Debug), which + ": DCP Debug was not enabled");

            DcpTestLog.Emit(dcp, LogLevel.Debug, "dcp-debug");
            DcpTestLog.Emit(aspire, LogLevel.Information, "aspire-information");

            var messages = recorder.Snapshot().Select(e => e.Message).ToList();
            Assert.Contains("dcp-debug", messages);
            Assert.DoesNotContain("aspire-information", messages);
        }
    }

    [Fact]
    public void Register_LeavesEveryOtherProvidersLevelsUntouched()
    {
        // The console must not get noisier. The rules are attached to the recorder's provider
        // TYPE, so a second provider registered alongside it keeps the factory's own defaults -
        // which is what this asserts, by watching a plain capturing provider see the
        // Information-level entry the recorder is required NOT to see.
        var collector = new CapturingLoggerProvider();
        using var recorder = new DcpFlightRecorder();
        using var factory = LoggerFactory.Create(lb =>
        {
            lb.AddProvider(collector);
            DcpFlightRecorder.Register(lb, recorder);
        });

        DcpTestLog.Emit(
            factory.CreateLogger("Aspire.Hosting.DistributedApplication"),
            LogLevel.Information,
            "banner");

        Assert.Contains(collector.Entries, e => e.Message == "banner");
        Assert.DoesNotContain(recorder.Snapshot(), e => e.Message == "banner");
    }

    // -----------------------------------------------------------------------
    // Sanitise-and-cap: output parity, and the cost bound
    // -----------------------------------------------------------------------

    /// <summary>
    /// Every case the sanitise-and-cap path has to get right, each pinned to the CONCRETE string
    /// the pre-bounding implementation produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserting concrete expected strings rather than "whatever the method returns" is the whole
    /// point: the bounding change is a pure COST change, so the only way a drill can prove that is
    /// to know the answer independently of the code under test. A round-trip assertion would have
    /// stayed green through any of the three mis-shapes considered while writing it.
    /// </para>
    /// <para>
    /// <strong>The blank-run rows are not padding.</strong> The obvious licence for capping before
    /// sanitising -- "the map is 1:1, so truncate-then-map equals map-then-truncate" -- is FALSE
    /// here, because the sanitiser ends in <c>Trim()</c>. A leading blank run shifts the cut window
    /// (row <c>leading-blank-run-then-overflow</c>: a naive cap-first drops five characters of
    /// text and mislabels the result), and a trailing blank run can pull an arbitrarily long input
    /// back UNDER the cap (row <c>trailing-blank-run-past-the-cap</c>: 10003 characters in, three
    /// out, no marker). Both rows fail any implementation that treats the raw length as the length
    /// that decides the marker.
    /// </para>
    /// </remarks>
    public static TheoryData<string, string?, string> SanitiseCases()
    {
        const int Cap = DcpFlightEntry.MaxLineChars;

        // Built from code points rather than written as literal characters: this file would
        // otherwise be the one place in the drill whose meaning depends on its own encoding, and
        // the surrogate row in particular has to be an unambiguous PAIR rather than whatever a
        // re-encoding left behind.
        var esc = new string((char)0x1B, 1);
        var eAcute = new string((char)0x00E9, 1);
        var grin = new string(new[] { (char)0xD83D, (char)0xDE00 });

        return new TheoryData<string, string?, string>
        {
            { "null", null, "" },
            { "empty", "", "" },
            { "short", "hello world", "hello world" },
            { "exactly-at-the-cap", new string('z', Cap), new string('z', Cap) },
            { "one-past-the-cap", new string('z', Cap + 1), new string('z', Cap) + "..." },
            { "well-past-the-cap", new string('z', Cap * 3), new string('z', Cap) + "..." },

            // ESC is a control character with no explicit case, so it folds to '?'; CR, LF and TAB
            // each fold to a single space and are never collapsed into one.
            { "control-characters", "a" + esc + "b\tc\rd\ne", "a?b c d e" },

            // A surrogate PAIR is two chars, and each half folds independently to '?' - so one
            // emoji costs two question marks, at the same two indices either way round.
            { "non-ascii-and-surrogate-pair", "caf" + eAcute + " " + grin + " ok", "caf? ?? ok" },

            // Over the cap with the non-ASCII PAST the cut: it never reaches the output at all.
            {
                "overflow-with-non-ascii-past-the-cut",
                new string('z', Cap) + eAcute + new string('q', 100),
                new string('z', Cap) + "..."
            },

            // Over the cap with the non-ASCII BEFORE the cut: it does, and it occupies exactly the
            // one character position it occupied in the input, so the cut lands one 'z' earlier.
            {
                "overflow-with-non-ascii-before-the-cut",
                eAcute + new string('z', Cap + 900),
                "?" + new string('z', Cap - 1) + "..."
            },

            // Trim() runs AFTER the fold, so a leading run of CR/LF/TAB/space disappears and the
            // cut window slides by its length. Cap-first without accounting for it loses text.
            {
                "leading-blank-run-then-overflow",
                " \t\r\n " + new string('z', Cap + 4),
                new string('z', Cap) + "..."
            },

            // The mirror image, and the sharper one: a 10003-character input yields three
            // characters and NO truncation marker, because Trim() removed everything past them.
            { "trailing-blank-run-past-the-cap", "abc" + new string(' ', 10_000), "abc" },

            // Nothing but blanks: Trim() empties it, and an empty result is not a truncated one
            // however long the input was.
            { "all-blank", "   \t\r\n  ", "" },
            { "all-blank-past-the-cap", new string(' ', Cap * 4), "" },
        };
    }

    [Theory]
    [MemberData(nameof(SanitiseCases))]
    public void Create_SanitiseAndCap_ProducesTheSameStringAsTheUnboundedImplementation(
        string caseName,
        string? message,
        string expected)
    {
        var entry = DcpFlightEntry.Create(
            DateTimeOffset.UnixEpoch,
            LogLevel.Warning,
            "Aspire.Hosting.Dcp.DcpExecutor",
            message,
            exception: null);

        // Named first, so a regression reports WHICH row moved before xunit prints a
        // four-kilobyte diff of 'z' characters.
        Assert.True(
            string.Equals(expected, entry.Message, StringComparison.Ordinal),
            $"[{caseName}] sanitise-and-cap changed its output: expected {expected.Length} "
            + $"characters, got {entry.Message.Length}. This path is a pure COST change; any "
            + "difference here is a behaviour change that was not intended.");
        Assert.Equal(expected, entry.Message);

        // The exception component runs through the same routine, so pin it on the same inputs
        // rather than trusting that it shares the code path.
        var viaException = DcpFlightEntry.Create(
            DateTimeOffset.UnixEpoch,
            LogLevel.Error,
            "Aspire.Hosting.Dcp.DcpExecutor",
            "m",
            new SanitiserProbeException(message ?? string.Empty));

        Assert.Equal(expected, viaException.Exception);

        // And the rendered line really is built from the capped component, not from the raw input.
        Assert.Contains(
            expected.Length <= 64 ? expected : expected[..64],
            entry.Line,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Create_HugeMessage_CostsTheCapRatherThanTheInput()
    {
        // THE BOUND THIS DRILL EXISTS FOR. Sanitising allocated a char[] sized from the RAW input
        // and walked all of it, then built a string of that same length, only for the result to be
        // capped at MaxLineChars two lines later. A 4 MiB DCP payload therefore cost ~8 MiB of
        // allocation on the logging thread to retain ~8 KiB - which contradicts the premise that
        // an always-armed recorder costs a BOUNDED amount.
        //
        // Measured on the thread, not wall-clock: GC.GetAllocatedBytesForCurrentThread() is a
        // deterministic counter of this thread's allocations, so this drill is not a benchmark and
        // does not depend on host speed, GC timing, or what any other test is doing.
        const int Huge = 2_000_000;
        var message = new string('z', Huge);
        var exception = new SanitiserProbeException(new string('y', Huge));

        // Warm up first: the JIT compiles Create and its callees on first use and charges that to
        // this thread. Measuring the first-ever call would fold compilation into the figure.
        for (var i = 0; i < 3; i++)
        {
            DcpFlightEntry.Create(
                DateTimeOffset.UnixEpoch, LogLevel.Debug, "c", "warm", exception: null);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var entry = DcpFlightEntry.Create(
            DateTimeOffset.UnixEpoch,
            LogLevel.Debug,
            "Aspire.Hosting.Dcp.DcpExecutor",
            message,
            exception);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The retained result is tiny, which is exactly why paying for the input is wrong.
        Assert.True(entry.RetainedChars < 4 * (DcpFlightEntry.MaxLineChars + 3));

        // The bound is set with MEASURED two-sided margin, so it is a property rather than a
        // benchmark. On this call: 90,656 bytes capped (three capped components plus the rendered
        // line, each a fresh UTF-16 string) against 16,098,184 bytes uncapped - two 2 M-character
        // inputs, each paying a char[] and then a string of its own length. The bound sits 5.8x
        // above the first and 30x below the second, which is wide enough that neither a runtime
        // upgrade nudging string internals nor a fourth capped component can move it either way.
        const int Bound = 512 * 1024;

        Assert.True(
            allocated <= Bound,
            $"sanitising a {Huge:N0}-character message and a {Huge:N0}-character exception "
            + $"allocated {allocated:N0} bytes against a bound of {Bound:N0}. The work is being "
            + "sized from the RAW input rather than from DcpFlightEntry.MaxLineChars, so an "
            + "always-armed recorder costs O(input) on the logging thread to retain O(cap). "
            + "Cap the window BEFORE folding it, not after.");
    }

    // -----------------------------------------------------------------------
    private static DcpFlightEntry Entry(string message, LogLevel level = LogLevel.Warning) =>
        DcpFlightEntry.Create(
            DateTimeOffset.UnixEpoch,
            level,
            "Aspire.Hosting.Dcp.DcpExecutor",
            message,
            exception: null);
}

/// <summary>
/// Emits a log entry through <see cref="ILogger.Log{TState}"/> directly.
/// </summary>
/// <remarks>
/// The <c>LogWarning</c>/<c>LogDebug</c> convenience extensions trip this repository's CA1848
/// gate (use a LoggerMessage delegate), which is the right rule for production code and pure
/// ceremony for a drill that emits one line. Calling the interface method is the same call the
/// extensions make, without the analyser argument, and it also states plainly what these drills
/// are exercising: an arbitrary producer writing into the logging pipeline.
/// </remarks>
internal static class DcpTestLog
{
    internal static void Emit(ILogger logger, LogLevel level, string message) =>
        logger.Log(level, new EventId(0), message, null, static (state, _) => state);
}

/// <summary>
/// An exception whose <see cref="Exception.ToString()"/> is EXACTLY the text handed to it.
/// </summary>
/// <remarks>
/// <c>DcpFlightEntry.Create</c> sanitises <c>exception.ToString()</c>, and the framework's own
/// <c>ToString</c> prefixes the type name and appends a stack trace. A drill pinning concrete
/// expected strings would then be asserting on the runtime's formatting rather than on the
/// sanitiser, and the assertion would move with any change to either. Overriding it makes the
/// exception path and the message path comparable on identical input, which is the only way to
/// show they share one routine.
/// </remarks>
internal sealed class SanitiserProbeException : Exception
{
    private readonly string _text;

    internal SanitiserProbeException(string text)
        : base(text) => _text = text;

    public override string ToString() => _text;
}
