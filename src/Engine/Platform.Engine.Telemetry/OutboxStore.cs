// Platform.Engine.Telemetry — OutboxStore (S12-G-01, Phase A).
//
// Owns the drain/cap WINDOW over the local outbox: take an EXCLUSIVE sibling lock,
// snapshot the lines, and — after a drain — atomically rewrite the remaining
// (undelivered) lines AND enforce the size/age/line caps with OLDEST-FIRST eviction.
//
// LOCKING: the local sink appends with FileShare.ReadWrite, so locking the outbox file
// itself would either block appenders or be defeated by them.  Instead we take a sibling
// "<outbox>.lock" with FileShare.None for the rewrite window.  If the lock cannot be
// taken (a concurrent drainer/appender holds it), we SKIP — fail-silent, try next run —
// rather than block the run or risk a torn rewrite.  Appends are line-atomic (O_APPEND) so
// a concurrent append during our read/rewrite is never corruption: our rewrite replaces the
// file we snapshotted.  But the local sink does NOT take this lock, so a line it appends
// AFTER our snapshot but BEFORE our rewrite IS LOST — overwritten, not retained for re-send
// (it is not re-emitted on a later run).  This is best-effort: telemetry is aggregate data,
// not transactional, and the window only exists when TWO vouchfx processes run concurrently
// (one appending, one draining) — a normal single-process run never races itself.
//
// ATOMIC REWRITE: temp-file + File.Move(overwrite:true), the TelemetryConsentStore.Write
// pattern, so a reader never sees a half-written outbox.
//
// CAP / EVICTION: enforce max bytes / max age-days / max lines, dropping from the TOP
// (oldest first), under the same lock.  Age is computed from each line's "timestamp"
// field (the allowlisted event timestamp) against an INJECTED clock so tests are
// deterministic; a line whose timestamp cannot be parsed is treated as NOT-expired (kept)
// so a malformed line is never silently evicted by the age rule alone.

using System.Text;
using System.Text.Json;

namespace Platform.Engine.Telemetry;

/// <summary>
/// Configurable ceilings for the local outbox; eviction is oldest-first when any ceiling
/// is exceeded (S12-G-01).
/// </summary>
/// <param name="MaxBytes">The maximum total outbox size in bytes.</param>
/// <param name="MaxAgeDays">The maximum age in days of a retained line.</param>
/// <param name="MaxLines">The maximum number of retained lines.</param>
public readonly record struct OutboxCap(long MaxBytes, int MaxAgeDays, int MaxLines)
{
    /// <summary>The default ceilings (5 MB / 30 days / 10 000 lines).</summary>
    public static OutboxCap Default => new(
        TelemetryTransportOptions.DefaultMaxBytes,
        TelemetryTransportOptions.DefaultMaxAgeDays,
        TelemetryTransportOptions.DefaultMaxLines);

    /// <summary>Builds the cap from resolved transport options.</summary>
    /// <param name="options">The resolved transport options.</param>
    /// <returns>The cap carried by those options.</returns>
    public static OutboxCap FromOptions(TelemetryTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new OutboxCap(options.MaxBytes, options.MaxAgeDays, options.MaxLines);
    }
}

/// <summary>
/// Owns the exclusive locked read / atomic rewrite / oldest-first cap over the local
/// telemetry outbox (S12-G-01).
/// </summary>
/// <remarks>
/// All mutation happens under a sibling <c>.lock</c> taken with <see cref="FileShare.None"/>.
/// If the lock cannot be taken (a concurrent drainer or appender), every operation SKIPS
/// fail-silently rather than blocking the run or risking a torn rewrite.  The rewrite is
/// atomic (temp-file + <see cref="File.Move(string, string, bool)"/>), so a reader never
/// observes a half-written outbox.
/// </remarks>
public sealed class OutboxStore
{
    private const string TimestampProperty = "timestamp";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly ITelemetryPaths _paths;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Creates a store over the outbox resolved by <paramref name="paths"/>.
    /// </summary>
    /// <param name="paths">The on-disk path seam (the outbox lives at <see cref="ITelemetryPaths.OutboxPath"/>).</param>
    /// <param name="now">
    /// The clock used for age-based eviction (inject a fixed value in tests; production
    /// passes <c>() =&gt; DateTimeOffset.UtcNow</c>).  Defaults to the real UTC clock.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public OutboxStore(ITelemetryPaths paths, Func<DateTimeOffset>? now = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Runs <paramref name="drain"/> under an exclusive lock: snapshots the current outbox
    /// lines, hands them to <paramref name="drain"/> (which returns the lines that were
    /// NOT delivered), then atomically rewrites the outbox to those undelivered lines AND
    /// enforces <paramref name="cap"/> (oldest-first).  SKIPS — leaving the outbox
    /// untouched — when the lock cannot be taken or the outbox does not exist.
    /// </summary>
    /// <param name="cap">The ceilings to enforce after the drain.</param>
    /// <param name="drain">
    /// An async function given the snapshot lines (oldest first); it returns the lines
    /// that remain UNDELIVERED (to be re-persisted).  It must not throw for an ordinary
    /// transport fault — but if it does, the outbox is left exactly as snapshotted.
    /// </param>
    /// <param name="cancellationToken">Propagated to the drain.</param>
    /// <returns>
    /// The outcome: whether the lock was taken and the drain ran, and the line counts.
    /// </returns>
    public async Task<OutboxDrainOutcome> DrainUnderLockAsync(
        OutboxCap cap,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>> drain,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drain);

        var outboxPath = _paths.OutboxPath;
        FileStream? lockStream = null;
        try
        {
            lockStream = TryAcquireLock(outboxPath);
            if (lockStream is null)
            {
                // A concurrent appender/drainer holds the lock: skip this run entirely.
                return OutboxDrainOutcome.Skipped;
            }

            if (!File.Exists(outboxPath))
            {
                // Nothing queued: there is no outbox to drain or cap yet.
                return OutboxDrainOutcome.NothingToDo;
            }

            var snapshot = ReadAllLines(outboxPath);
            if (snapshot.Count == 0)
            {
                return OutboxDrainOutcome.NothingToDo;
            }

            // The drain may throw despite the fail-silent contract; if it does, the outbox
            // is left exactly as snapshotted (we have not rewritten it yet).
            var undelivered = await drain(snapshot, cancellationToken).ConfigureAwait(false);
            undelivered ??= Array.Empty<string>();

            var retained = ApplyCap(undelivered, cap);
            RewriteAtomic(outboxPath, retained);

            return new OutboxDrainOutcome(
                LockAcquired: true,
                Drained: true,
                SnapshotLineCount: snapshot.Count,
                RetainedLineCount: retained.Count);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or NotSupportedException
                or ArgumentException)
        {
            // Any file-system fault in the locked window leaves the outbox intact and is
            // swallowed — the drain is best-effort and must never disrupt the run.
            return OutboxDrainOutcome.Skipped;
        }
        finally
        {
            lockStream?.Dispose();
        }
    }

    /// <summary>
    /// Enforces <paramref name="cap"/> on the outbox in place (no drain), under the lock,
    /// rewriting only when eviction is needed.  Used when the HTTP endpoint is NOT
    /// configured so the cap still bounds local growth.  SKIPS fail-silently when the lock
    /// cannot be taken or the outbox is absent/within all ceilings.
    /// </summary>
    /// <param name="cap">The ceilings to enforce.</param>
    /// <returns>The outcome (lock taken? lines retained?).</returns>
    public OutboxDrainOutcome EnforceCap(OutboxCap cap)
    {
        var outboxPath = _paths.OutboxPath;
        FileStream? lockStream = null;
        try
        {
            lockStream = TryAcquireLock(outboxPath);
            if (lockStream is null)
            {
                return OutboxDrainOutcome.Skipped;
            }

            if (!File.Exists(outboxPath))
            {
                return OutboxDrainOutcome.NothingToDo;
            }

            var snapshot = ReadAllLines(outboxPath);
            var retained = ApplyCap(snapshot, cap);

            // Only rewrite when eviction actually changed the line set — a clean run with
            // an under-cap outbox does no I/O beyond the read.
            if (retained.Count != snapshot.Count)
            {
                RewriteAtomic(outboxPath, retained);
            }

            return new OutboxDrainOutcome(
                LockAcquired: true,
                Drained: false,
                SnapshotLineCount: snapshot.Count,
                RetainedLineCount: retained.Count);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or NotSupportedException
                or ArgumentException)
        {
            return OutboxDrainOutcome.Skipped;
        }
        finally
        {
            lockStream?.Dispose();
        }
    }

    // Takes the sibling lock with FileShare.None.  Returns null when it cannot be taken
    // (a concurrent holder) — the caller then SKIPS.  The lock file lives next to the
    // outbox so the directory must exist first.
    private static FileStream? TryAcquireLock(string outboxPath)
    {
        var dir = Path.GetDirectoryName(outboxPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var lockPath = outboxPath + ".lock";
        try
        {
            // FileShare.None: a second opener (another drainer) fails with IOException, so
            // only one process holds the drain/cap window at a time.  FileMode.OpenOrCreate
            // so the lock file persists harmlessly between runs.
            return new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // The lock is held by a concurrent drainer/appender: skip.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> ReadAllLines(string outboxPath)
    {
        // Read with FileShare.ReadWrite so a concurrent appender (the local sink) is never
        // blocked by our snapshot read; we only need a point-in-time view.
        var stream = new FileStream(
            outboxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using (stream)
        using (var reader = new StreamReader(stream, Utf8NoBom))
        {
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }
    }

    // Atomically replace the outbox with `lines` (one per line, '\n'-terminated, UTF-8 no
    // BOM — matching the local sink's exact on-disk format).  An EMPTY set truncates the
    // outbox to zero bytes (still via the atomic rename) so a fully-delivered outbox is
    // emptied without leaving a stale file readers could re-send.
    private static void RewriteAtomic(string outboxPath, IReadOnlyList<string> lines)
    {
        var dir = Path.GetDirectoryName(outboxPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = outboxPath + ".tmp-" + Guid.NewGuid().ToString("n");
        try
        {
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                builder.Append(line).Append('\n');
            }

            File.WriteAllText(temp, builder.ToString(), Utf8NoBom);
            File.Move(temp, outboxPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception cleanupEx) when (
                cleanupEx is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException
                    or NotSupportedException
                    or ArgumentException)
            {
                // A stray temp file must never escalate into a throw.
            }

            throw;
        }
    }

    // Apply the three ceilings with OLDEST-FIRST eviction (drop from the top of the list,
    // which is the oldest line because the outbox is append-ordered).  Order of checks:
    //   1. age  : drop any line older than maxAgeDays (a line with an unparseable
    //             timestamp is treated as NOT expired so it is never silently dropped by
    //             age alone — it can still be evicted by the line/byte caps below).
    //   2. lines: keep at most maxLines, dropping the oldest surplus.
    //   3. bytes: keep the newest lines whose cumulative size fits maxBytes.
    // Each rule only ever DROPS the oldest lines, so the retained set is always a
    // contiguous newest-suffix of the input — exactly oldest-first eviction.
    private List<string> ApplyCap(IReadOnlyList<string> lines, OutboxCap cap)
    {
        var now = _now();

        // 1. Age: find the first index whose line is within the age window; everything
        // before it is too old.  A line with an unparseable timestamp counts as in-window.
        var ageCutoff = now - TimeSpan.FromDays(cap.MaxAgeDays);
        var firstKept = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsExpired(lines[i], ageCutoff))
            {
                firstKept = i;
                break;
            }

            // Every line so far is expired; if we reach the end they are all expired.
            firstKept = i + 1;
        }

        var afterAge = new List<string>(lines.Count - firstKept);
        for (var i = firstKept; i < lines.Count; i++)
        {
            afterAge.Add(lines[i]);
        }

        // 2. Lines: keep at most MaxLines, dropping the oldest surplus from the front.
        var maxLines = Math.Max(cap.MaxLines, 0);
        var startByLines = afterAge.Count > maxLines ? afterAge.Count - maxLines : 0;

        // 3. Bytes: walk from the NEWEST line backwards, accumulating UTF-8 byte size
        // (line + the '\n' terminator), and keep only the newest suffix that fits.
        var maxBytes = Math.Max(cap.MaxBytes, 0);
        long running = 0;
        var startByBytes = afterAge.Count;
        for (var i = afterAge.Count - 1; i >= startByLines; i--)
        {
            var size = Utf8NoBom.GetByteCount(afterAge[i]) + 1; // +1 for '\n'
            if (running + size > maxBytes)
            {
                break;
            }

            running += size;
            startByBytes = i;
        }

        var start = Math.Max(startByLines, startByBytes);
        var retained = new List<string>(afterAge.Count - start);
        for (var i = start; i < afterAge.Count; i++)
        {
            retained.Add(afterAge[i]);
        }

        return retained;
    }

    // A line is expired when its allowlisted "timestamp" field is strictly older than the
    // cutoff.  A line whose timestamp is missing or unparseable is treated as NOT expired
    // (return false) — age must never silently drop a line we cannot date.
    private static bool IsExpired(string line, DateTimeOffset ageCutoff)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(TimestampProperty, out var tsElement)
                || tsElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return tsElement.TryGetDateTimeOffset(out var ts) && ts < ageCutoff;
        }
        catch (JsonException)
        {
            // A non-JSON line cannot be dated — keep it (it may still be byte/line-capped).
            return false;
        }
    }
}

/// <summary>
/// The outcome of an <see cref="OutboxStore"/> operation: whether the lock was taken,
/// whether a drain ran, and the snapshot/retained line counts (S12-G-01).
/// </summary>
/// <param name="LockAcquired">Whether the exclusive lock was taken (false ⇒ skipped).</param>
/// <param name="Drained">Whether a drain function ran (vs. a cap-only enforcement).</param>
/// <param name="SnapshotLineCount">The number of lines snapshotted under the lock.</param>
/// <param name="RetainedLineCount">The number of lines retained after drain + cap.</param>
public readonly record struct OutboxDrainOutcome(
    bool LockAcquired,
    bool Drained,
    int SnapshotLineCount,
    int RetainedLineCount)
{
    /// <summary>The lock could not be taken: the operation skipped entirely.</summary>
    public static OutboxDrainOutcome Skipped => new(false, false, 0, 0);

    /// <summary>The lock was taken but there was nothing to do (no/empty outbox).</summary>
    public static OutboxDrainOutcome NothingToDo => new(true, false, 0, 0);
}
