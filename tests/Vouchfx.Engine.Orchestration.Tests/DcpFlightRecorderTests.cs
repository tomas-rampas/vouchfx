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
