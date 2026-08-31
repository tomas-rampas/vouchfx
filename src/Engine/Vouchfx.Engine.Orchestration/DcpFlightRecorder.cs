// An always-armed, bounded, in-memory recorder for the log traffic Aspire's DCP layer
// emits while a topology is coming up (issue #420).
//
// WHY THIS EXISTS, and why it is armed by default.
// ------------------------------------------------
// #420 presented as an intermittent, Windows-host-specific fault that appeared to clear on
// its own. It is none of those things. This recorder captured it on its first live encounter
// and the cause was established from that capture (see below); what looked like intermittency
// was a deterministic refusal whose trigger came and went. The symptom:
//
//     warn: Unable to allocate a network port for service '<name>'; ...
//     fail: System.IO.InvalidDataException: Service <name> should have valid address at this point
//              at Aspire.Hosting.Dcp.DcpModelUtilities.TryAddLocalhostAllocatedEndpoint(...)
//
// It reddens every port-publishing container test on the affected host while a raw
// `docker run -p 0:80` publishes fine. Two capture attempts failed for the SAME reason: by
// the time an operator raised
// `Logging__LogLevel__Aspire_Hosting_Dcp=Debug` and re-ran, the fault had gone -- and even
// mid-fault, DCP's Debug lines did not surface in `dotnet test`'s output stream. The single
// most valuable piece of evidence -- which port DCP tried and which OS error came back --
// has therefore never been captured.
//
// A diagnostic that has to be switched on AFTER the fault appears cannot capture a fault
// that clears before it can be switched on. So this recorder is armed on every topology
// start, costs a bounded in-memory buffer while the topology is coming up, and is dropped
// (buffer cleared, nothing written) once the topology is ready.
//
// THE ARMING WINDOW SPANS THE HEALTH GATES, AND THAT IS LOAD-BEARING.
// -------------------------------------------------------------------
// The `fail:` in the transcript above is a console LEVEL TOKEN -- it is a log line, not
// proof of an exception leaving StartAsync. Aspire may catch that InvalidDataException
// internally, log it, and let StartAsync return; the fault would then surface later, as a
// health-gate timeout on a resource whose endpoint never materialised. Arming only for the
// duration of StartAsync would therefore buffer the golden evidence and then throw it away
// on exactly the fault this exists to capture. The window runs from StartAsync until the
// topology is ready, and every failure in between flushes. See
// SuiteTopology/StubTopology for the two callers that own the far end of that window.
//
// WHAT IT FOUND, on its first live encounter.
// -------------------------------------------
// DCP's controller host exits with code 1 about 130 ms after start, refusing its state-store
// directory: "failed to initialize state store: could not prepare state store directory
// '...\.dcp\state.elevated': ... has invalid ownership: directory owner does not match current
// user or token owner". With the controller dead nothing allocates ports, so Aspire's watch for
// the allocation waits on state that never arrives and dies on a fixed 60-second Polly timeout
// (measured 60010/60026/60029 ms across three captures); two such windows are the constant
// ~2 minutes before the throw. "Unable to allocate a network port" is Aspire's DOWNSTREAM
// wording for that, logged under Aspire.Hosting.DistributedApplication -- which is why the
// broad Aspire-at-Warning rule below earns its place: the DCP-prefixed rule alone would have
// missed the line an operator sees first. Verified remedy: move the offending state-store
// directory aside, and DCP recreates it.
//
// The fault is inside DCP, a closed binary this repository does not own, so this type does not
// fix it. What it does is turn a fault nobody could catch into one that diagnoses itself.

using Microsoft.Extensions.Logging;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// One captured log entry, already rendered to a single ASCII line.
/// </summary>
/// <param name="Timestamp">When the entry was recorded (UTC).</param>
/// <param name="Level">The <see cref="LogLevel"/> the entry was written at.</param>
/// <param name="Category">The logger category, ASCII-sanitised.</param>
/// <param name="Message">The formatted message, ASCII-sanitised and flattened to one line.</param>
/// <param name="Exception">
/// <see cref="System.Exception.ToString()"/> of the entry's exception, ASCII-sanitised and
/// flattened to one line; <see langword="null"/> when the entry carried no exception.
/// </param>
/// <param name="Line">
/// The rendered form of the five members above -- what the capture file contains. Stored
/// rather than recomputed because it is built once, on the logging thread, and then read on
/// both the eviction path and the flush path.
/// </param>
/// <param name="RetainedChars">
/// Every character this entry keeps alive, counted honestly: <see cref="Line"/> PLUS the
/// component strings it was rendered from, because the record holds all of them. Charging only
/// <see cref="Line"/> would under-count the buffer's real footprint by roughly half and let a
/// budget named for memory quietly stop bounding it.
/// </param>
internal sealed record DcpFlightEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception,
    string Line,
    int RetainedChars)
{
    /// <summary>
    /// The per-entry cap, in characters, applied to <see cref="Line"/>.
    /// </summary>
    /// <remarks>
    /// Bounds a single pathological entry -- a stack trace inside an exception's
    /// <c>ToString()</c>, most obviously -- so that one entry can never consume the whole
    /// buffer budget on its own and evict every DCP warning around it. Well below the
    /// buffer's own character budget, which is what makes that guarantee hold.
    /// </remarks>
    internal const int MaxLineChars = 4096;

    /// <summary>
    /// Builds an entry, sanitising every piece of text it carries to printable ASCII on one line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Sanitised at CAPTURE time, not at flush time, and that is deliberate on two
    /// counts.</strong> First, the tail of this buffer reaches an Environment-error detail, which
    /// reaches a console: issue #379 measured a Windows console codepage silently best-fit-mapping
    /// a non-ASCII character on its way out, so a diagnostic that has to survive a console is
    /// written in ASCII. Second, sanitising here makes one character exactly one byte, so the
    /// buffer's character budget IS its byte budget and neither has to be estimated.
    /// </para>
    /// <para>
    /// <strong>Sanitising MUTATES, and one caller downstream cares.</strong> A non-ASCII
    /// character becomes <c>?</c> rather than being dropped or escaped, so the text recorded here
    /// is not byte-identical to what Aspire logged. <c>ResolvedSecretLedger</c> redacts EXACT
    /// occurrences, so a value carrying any non-ASCII byte survives this transform unredacted.
    /// That is why the tail is documented as best-effort rather than as scrubbed, here and in
    /// <c>ScenarioRunner.EnvironmentErrorLine</c>'s own enumeration of the sites that defeat it.
    /// </para>
    /// </remarks>
    internal static DcpFlightEntry Create(
        DateTimeOffset timestamp,
        LogLevel level,
        string? category,
        string? message,
        Exception? exception)
    {
        var safeCategory = ToPrintableAsciiLine(category);
        var safeMessage = ToPrintableAsciiLine(message);
        var safeException = exception is null
            ? null
            : ToPrintableAsciiLine(exception.ToString());

        var rendered = Render(timestamp, level, safeCategory, safeMessage, safeException);

        var retained =
            rendered.Length + safeCategory.Length + safeMessage.Length + (safeException?.Length ?? 0);

        return new DcpFlightEntry(
            timestamp, level, safeCategory, safeMessage, safeException, rendered, retained);
    }

    /// <summary>
    /// The four-character level token, matching the shape the .NET console logger prints
    /// (<c>warn</c>, <c>fail</c>) so a capture file reads like the console output an operator
    /// already recognises from the issue.
    /// </summary>
    internal static string LevelToken(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };

    /// <summary>
    /// Replaces every character outside printable ASCII -- control characters and line breaks
    /// included -- with a space or <c>?</c>, so the result is one printable ASCII line.
    /// </summary>
    private static string ToPrintableAsciiLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = c switch
            {
                '\r' or '\n' or '\t' => ' ',
                >= ' ' and <= '~' => c,
                _ => '?',
            };
        }

        return new string(buffer).Trim();
    }

    private static string Render(
        DateTimeOffset timestamp,
        LogLevel level,
        string category,
        string message,
        string? exception)
    {
        var stamp = timestamp.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

        var line = exception is null
            ? $"{stamp} {LevelToken(level)} {category}: {message}"
            : $"{stamp} {LevelToken(level)} {category}: {message} || {exception}";

        return line.Length <= MaxLineChars
            ? line
            : string.Concat(line.AsSpan(0, MaxLineChars), "...");
    }
}

/// <summary>
/// A bounded, thread-safe ring buffer of log entries, exposed as an
/// <see cref="ILoggerProvider"/> so the Aspire host writes into it directly (issue #420).
/// </summary>
/// <remarks>
/// <para>
/// One instance per topology start, created by <see cref="HeadlessTopology.StartAsync"/> and
/// retained by the resulting topology until its caller reports the topology ready. It is
/// registered alongside -- never instead of -- the host's existing providers, and the filter
/// rules that route traffic to it are PROVIDER-SCOPED, so raising DCP to
/// <see cref="LogLevel.Debug"/> for this recorder leaves every other provider's levels, and
/// therefore the console, exactly as they were.
/// </para>
/// <para>
/// <strong>Bounded on both axes, and both bounds are load-bearing.</strong> An entry count
/// alone does not bound memory (one entry can carry a stack trace); a character budget alone
/// does not bound the flush cost of walking the buffer. Whichever binds first evicts the
/// OLDEST entry, and every eviction increments <see cref="EvictedCount"/> so a truncated
/// capture says that it is truncated rather than reading as a complete record that happens to
/// start late. The character budget counts every string an entry retains, not just its
/// rendered line -- see <see cref="DcpFlightEntry.RetainedChars"/>.
/// </para>
/// <para>
/// <strong>Thread-safe under a plain lock, because DCP logs from background tasks.</strong>
/// Aspire's resource-watching and port-allocation work runs off the thread that called
/// <c>StartAsync</c>, so entries arrive concurrently with each other and, at the moment of
/// failure, concurrently with the flush. A single lock over enqueue, evict, snapshot and
/// dispose is the whole synchronisation story.
/// </para>
/// <para>
/// <strong>Disposal is the drop, and after it the cost is one volatile read per log
/// statement -- not zero.</strong> Disposal clears the buffer and makes
/// <see cref="ILogger.Log"/> return before it formats anything. What it CANNOT do is remove
/// the filter rule: that lives in the host's <c>LoggerFilterOptions</c> for the life of the
/// host, so Aspire goes on believing Debug is enabled for the DCP categories and goes on
/// constructing the state object for each one. The expensive half -- invoking the formatter,
/// rendering, allocating an entry, taking the lock -- is what the early return removes. Saying
/// "a healthy run pays nothing" would be wrong; it pays a volatile read and whatever the
/// caller allocated before calling in.
/// </para>
/// </remarks>
internal sealed class DcpFlightRecorder : ILoggerProvider
{
    /// <summary>The environment variable that turns the recorder off.</summary>
    /// <remarks>
    /// Default ON: the fault this recorder exists to capture clears before anyone can switch a
    /// diagnostic on, so an opt-in recorder would be disarmed in exactly the window that matters.
    /// The switch exists for a host where even a bounded buffer is unwelcome, and for this
    /// repository's own Docker drills, which must not write into an operator's real capture
    /// directory.
    /// </remarks>
    internal const string OptOutVariable = "VOUCHFX_DCP_CAPTURE";

    /// <summary>The default entry bound.</summary>
    internal const int DefaultEntryLimit = 512;

    /// <summary>
    /// The default retained-character bound: 128 Ki CHARACTERS, which under .NET's UTF-16
    /// strings is roughly 256 KiB of managed memory, not 128 KiB. Stated in characters because
    /// that is what is counted; do not restate it as a byte figure.
    /// </summary>
    internal const int DefaultCharLimit = 128 * 1024;

    /// <summary>Per-line cap applied to a tail line before it is charged against the tail budget.</summary>
    internal const int TailLineChars = 120;

    /// <summary>The broad Aspire category prefix captured at <see cref="LogLevel.Warning"/>.</summary>
    internal const string AspireCategoryPrefix = "Aspire";

    /// <summary>The DCP category prefix captured at <see cref="LogLevel.Debug"/>.</summary>
    internal const string DcpCategoryPrefix = "Aspire.Hosting.Dcp";

    private readonly object _gate = new();
    private readonly Queue<DcpFlightEntry> _entries = new();
    private readonly int _entryLimit;
    private readonly int _charLimit;

    private int _chars;
    private int _evicted;

    // Volatile, and read OUTSIDE the lock at the top of every Log call. The lock is the wrong
    // instrument for the hot path: after the drop, a --watch session goes on emitting DCP Debug
    // statements for hours, and taking a lock to discover there is nothing to do would be a
    // permanent cost paid by every healthy run. Writes still happen under the lock, so the
    // ordering with the buffer clear is unchanged; the volatile read only makes the "already
    // dropped" answer cheap and correctly published.
    private volatile bool _disposed;

    /// <summary>Creates a recorder with explicit bounds (the drills supply small ones).</summary>
    /// <param name="entryLimit">Maximum retained entries; must be positive.</param>
    /// <param name="charLimit">Maximum retained characters across all entries; must be positive.</param>
    internal DcpFlightRecorder(
        int entryLimit = DefaultEntryLimit,
        int charLimit = DefaultCharLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(charLimit);

        _entryLimit = entryLimit;
        _charLimit = charLimit;
    }

    /// <summary>Whether this recorder has been dropped and is no longer recording.</summary>
    internal bool IsDropped => _disposed;

    /// <summary>Number of entries evicted by either bound since this recorder was created.</summary>
    internal int EvictedCount
    {
        get
        {
            lock (_gate)
            {
                return _evicted;
            }
        }
    }

    /// <summary>Characters currently retained across every buffered entry.</summary>
    internal int CharCount
    {
        get
        {
            lock (_gate)
            {
                return _chars;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="value"/> turns the recorder off. Only the exact string
    /// <c>0</c> does.
    /// </summary>
    /// <remarks>
    /// Deliberately not a general truthiness test, and deliberately the same rule as
    /// <c>VOUCHFX_DRILL_SWEEP</c>: a diagnostic that is armed by default should be disarmed by
    /// an unmistakable value and by nothing else, so an unset variable, an empty one, or a typo
    /// all leave it armed.
    /// </remarks>
    internal static bool IsDisabledBy(string? value) =>
        string.Equals(value, "0", StringComparison.Ordinal);

    /// <summary>
    /// The production constructor path: a recorder, unless the opt-out turned it off, in which
    /// case <see langword="null"/> and no provider is registered at all.
    /// </summary>
    internal static DcpFlightRecorder? CreateUnlessDisabled() =>
        IsDisabledBy(Environment.GetEnvironmentVariable(OptOutVariable))
            ? null
            : new DcpFlightRecorder();

    /// <summary>
    /// Registers <paramref name="recorder"/> and the three provider-scoped filter rules that
    /// decide what reaches it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One method rather than three inline calls at the one production call site, because the
    /// composition -- not any single rule -- is what has to be right, and a drill that pinned a
    /// COPY of these rules would pass while production drifted away from it. The drill calls
    /// this method.
    /// </para>
    /// <para>
    /// <strong>Provider-scoped, so nothing else moves.</strong>
    /// <c>AddFilter&lt;DcpFlightRecorder&gt;</c> attaches each rule to this provider TYPE, and
    /// the logger factory evaluates rules per provider: the console keeps exactly the levels it
    /// had. What does change is that Aspire will now format DCP Debug messages, because some
    /// provider is asking for them.
    /// </para>
    /// <para>
    /// <strong>Registration order does NOT matter here, and an earlier version of this remark
    /// claimed it did.</strong> Measured against the Microsoft.Extensions.Logging 10.0.8 this
    /// solution resolves: rule selection takes the LONGEST MATCHING CATEGORY, whichever order
    /// the rules were added -- both orders give <c>IsEnabled(Debug) == true</c> for
    /// <c>Aspire.Hosting.Dcp.DcpExecutor</c>. Registration order decides only between rules
    /// whose category strings are the same LENGTH (with two identical categories the last
    /// registered wins, measured both ways). The rules below are written broad-to-narrow because
    /// it reads in the order a person reasons about them, not because anything depends on it.
    /// </para>
    /// <list type="number">
    ///   <item>null category at <see cref="LogLevel.None"/> -- the floor: nothing reaches the
    ///   recorder by default, so a buffer sized for DCP is not filled with unrelated chatter.</item>
    ///   <item><see cref="AspireCategoryPrefix"/> at <see cref="LogLevel.Warning"/> -- kept as
    ///   belt and braces, and then MEASURED to be load-bearing. #420's warning ("Unable to
    ///   allocate a network port for service ...") is logged under
    ///   <c>Aspire.Hosting.DistributedApplication</c>, NOT under any <c>Aspire.Hosting.Dcp*</c>
    ///   category -- read directly off the live capture that root-caused the issue. Without this
    ///   rule the DCP-prefixed rule below would have captured the Debug traffic but not the one
    ///   line an operator actually sees first, and the tail carried in the Environment-error
    ///   detail would have been EMPTY. Do not narrow this rule to the DCP prefix on the
    ///   reasoning that the DCP prefix is where DCP logs; the golden line disproves it.</item>
    ///   <item><see cref="DcpCategoryPrefix"/> at <see cref="LogLevel.Debug"/> -- the evidence
    ///   #420 has never captured: which port DCP tried and which OS error came back.</item>
    /// </list>
    /// </remarks>
    internal static void Register(ILoggingBuilder builder, DcpFlightRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(recorder);

        builder.AddProvider(recorder)
            .AddFilter<DcpFlightRecorder>(category: null, LogLevel.None)
            .AddFilter<DcpFlightRecorder>(AspireCategoryPrefix, LogLevel.Warning)
            .AddFilter<DcpFlightRecorder>(DcpCategoryPrefix, LogLevel.Debug);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        new RecordingLogger(this, categoryName);

    /// <summary>Appends one entry, evicting the oldest until both bounds hold again.</summary>
    internal void Record(DcpFlightEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _entries.Enqueue(entry);
            _chars += entry.RetainedChars;

            // The count bound is absolute; the character bound always leaves the newest entry
            // standing, so a single entry larger than the whole budget is retained (capped at
            // DcpFlightEntry.MaxLineChars) rather than recorded as an eviction of itself.
            while (_entries.Count > _entryLimit ||
                   (_chars > _charLimit && _entries.Count > 1))
            {
                var evicted = _entries.Dequeue();
                _chars -= evicted.RetainedChars;
                _evicted++;
            }
        }
    }

    /// <summary>Every retained entry, oldest first.</summary>
    internal IReadOnlyList<DcpFlightEntry> Snapshot() => SnapshotWithEvictions().Entries;

    /// <summary>
    /// The buffer and its eviction count, read under ONE lock acquisition.
    /// </summary>
    /// <remarks>
    /// Two separate reads would let a concurrent DCP thread evict between them, so a capture
    /// header could pair a post-eviction entry list with a pre-eviction eviction count. That
    /// header is precisely what tells a reader the record is truncated, and a diagnostic that
    /// can misreport its own completeness is worse than one that reports nothing.
    /// </remarks>
    internal (IReadOnlyList<DcpFlightEntry> Entries, int Evicted) SnapshotWithEvictions()
    {
        lock (_gate)
        {
            return (_entries.ToArray(), _evicted);
        }
    }

    /// <summary>
    /// The most recent warning-or-worse lines, newest-first-selected but returned in
    /// chronological order, bounded by both a line count and a total character budget.
    /// </summary>
    /// <param name="maxEntries">Most lines to return.</param>
    /// <param name="maxChars">Total character budget across the returned lines.</param>
    /// <remarks>
    /// <para>
    /// Warning-and-above only, because this tail is what reaches an Environment-error detail and
    /// a CI log -- the one place the evidence survives a runner whose filesystem is discarded the
    /// moment the job ends. The Debug traffic that motivates the capture FILE would drown it.
    /// </para>
    /// <para>
    /// Selected from the newest end so the lines nearest the failure are the ones kept when the
    /// budget binds -- for #420 those are the <c>Unable to allocate a network port</c> warnings
    /// that precede the throw.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<string> Tail(int maxEntries, int maxChars)
    {
        if (maxEntries <= 0 || maxChars <= 0)
        {
            return Array.Empty<string>();
        }

        var snapshot = Snapshot();
        var picked = new List<string>();
        var budget = maxChars;

        for (var i = snapshot.Count - 1; i >= 0 && picked.Count < maxEntries; i--)
        {
            if (snapshot[i].Level < LogLevel.Warning)
            {
                continue;
            }

            var line = snapshot[i].Line;
            if (line.Length > TailLineChars)
            {
                line = string.Concat(line.AsSpan(0, TailLineChars), "...");
            }

            if (line.Length > budget)
            {
                break;
            }

            budget -= line.Length;
            picked.Add(line);
        }

        picked.Reverse();
        return picked;
    }

    /// <summary>
    /// The full capture file body: a header naming what this is, then every retained entry.
    /// </summary>
    /// <param name="utcNow">The moment of the flush, recorded in the header.</param>
    internal string FormatCapture(DateTimeOffset utcNow)
    {
        var (snapshot, evicted) = SnapshotWithEvictions();

        var builder = new System.Text.StringBuilder();
        builder.Append("vouchfx DCP flight recorder capture\n");
        builder.Append("written: ").Append(
            utcNow.UtcDateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture))
            .Append('\n');
        builder.Append(
            "issue: 420 (DCP state-store ownership refusal on Windows hosts; the controller exits "
            + "and nothing allocates ports)\n");
        builder.Append("entries: ").Append(snapshot.Count)
            .Append(", evicted: ").Append(evicted)
            .Append(", bounds: ").Append(_entryLimit).Append(" entries / ")
            .Append(_charLimit).Append(" chars\n");

        if (evicted > 0)
        {
            builder.Append(
                "NOTE: this capture is TRUNCATED - the oldest ").Append(evicted)
                .Append(" entries were evicted by the bounds above.\n");
        }

        builder.Append("----\n");

        foreach (var entry in snapshot)
        {
            builder.Append(entry.Line).Append('\n');
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent, and the only "stop recording" mechanism: the loggers handed out by
    /// <see cref="CreateLogger"/> stay alive inside the host's logger factory after this
    /// recorder is dropped, so they must become no-ops rather than keep filling a buffer nobody
    /// will ever read.
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _entries.Clear();
            _chars = 0;
        }
    }

    // ------------------------------------------------------------------
    private sealed class RecordingLogger : ILogger
    {
        private readonly DcpFlightRecorder _owner;
        private readonly string _category;

        internal RecordingLogger(DcpFlightRecorder owner, string category)
        {
            _owner = owner;
            _category = category ?? string.Empty;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // No lock: a volatile read is enough to answer "am I still recording", and this is
        // called on the host's hot logging path.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && !_owner.IsDropped;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            // FIRST, before the formatter runs. The logger factory does NOT consult a provider
            // logger's IsEnabled before calling Log, and the filter rule that routes DCP Debug
            // here outlives the drop, so without this check every Debug statement in a long
            // healthy run would still invoke the formatter, render a string, allocate an entry
            // and take the lock -- all to be discarded inside Record.
            if (_owner.IsDropped)
            {
                return;
            }

            _owner.Record(DcpFlightEntry.Create(
                DateTimeOffset.UtcNow,
                logLevel,
                _category,
                formatter(state, exception),
                exception));
        }
    }
}
