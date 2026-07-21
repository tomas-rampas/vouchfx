// Vouchfx.Engine.Reporting — EventStreamAppender (issue #258).
//
// --events / --json (FileReportWriter) writes the buffered JSON Lines event stream ONCE,
// at the very end of a run — byte-frozen (FileReportWriterTests byte-compares the file to
// string.Join("\n", buffer), no trailing newline) so the telemetry capture path can re-read
// it verbatim. That archive is deliberately left untouched by this type.
//
// This type is the NEW, separate, opt-in `--events-stream <file>` sink: it appends the SAME
// EventStreamJson.ToLine records INCREMENTALLY, as each scenario finishes, to a file a
// concurrent reader can already be tailing (`tail -f`, a file-watcher, …). Scope decision
// (approved): SCENARIO-granular liveness only — step / step-attempt events are reconstructed
// from Vars after the compiled delegate returns (see ScenarioRunner), so a flush lands a
// whole scenario's lines at once. Genuine per-step streaming is tracked separately (#262)
// and is deliberately NOT attempted here.
//
// Design choices (contrast FileReportWriter, which this deliberately does NOT reuse):
//   • FileShare.Read (never FileShare.None): FileReportWriter's end-of-run writer uses
//     FileShare.None because nothing needs to read the file while it is being written; a
//     TAILING reader is exactly the point of this type, so the write handle must let a
//     concurrent reader open the file for read access while we still hold it open.
//   • The file is opened ONCE (FileMode.Create — the previous run's stale content, if any,
//     is truncated on open, matching FileReportWriter's overwrite semantics) and kept open
//     for the appender's lifetime; every scenario's lines are appended to that SAME handle.
//   • Every appended line ends with '\n' (proper JSON Lines — one record per line, INCLUDING
//     the last) followed by an explicit Flush(), so a tailing reader always observes only
//     whole lines, never a partially-written one sitting in a buffer. This is the one
//     deliberate byte-level difference from the --events archive, which is '\n'-JOINED with
//     no trailing newline (a design this type must never disturb).
//   • UTF-8 WITHOUT a BOM (new UTF8Encoding(false)) — identical rationale to FileReportWriter.
//   • I/O failure discipline mirrors FileReportWriter exactly: a bad path (or a failure mid
//     -append) is caught, a single type-name-only diagnostic is written to the optional sink
//     (§17 redaction — never ex.Message, never the resolved path), and the failure is
//     swallowed. Report/stream writing must NEVER change the already-computed verdict or
//     exit code. SELF-DISABLE IS SYMMETRIC across BOTH failure points: whether the INITIAL
//     open fails, or a LATER append/flush fails mid-suite (destination deleted, disk full,
//     removable media unplugged), the writer is nulled out (under the same lock) so every
//     subsequent AppendLines call becomes a silent no-op — exactly ONE diagnostic total for a
//     dead sink, never one per remaining scenario, and no truncated partial line can ever
//     abut a later append with no separating boundary.
//   • Thread-safe: ParallelSuiteRunner appends from multiple concurrently-running scenario
//     slots as each completes (completion order, not declaration order — the parity-preserving
//     --events archive stays declaration-ordered and end-written, unaffected by this type).
//     A single lock around the write+flush serialises those calls so lines from different
//     scenarios can never interleave mid-record.

using System.Text;

namespace Vouchfx.Engine.Reporting;

/// <summary>
/// Appends the frozen v1 JSON Lines event-stream records to a tailable file, incrementally,
/// as each scenario completes (issue #258).
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>--events-stream</c> sink: a SEPARATE, opt-in file from the <c>--events</c> /
/// <c>--json</c> archive <see cref="FileReportWriter"/> still writes once, at the end of the
/// run, byte-for-byte unchanged. The two may be used together or independently.
/// </para>
/// <para>
/// The file is opened exactly once, for the lifetime of this instance, with
/// <see cref="FileShare.Read"/> so a concurrent reader (a <c>tail -f</c>, a file-watcher) can
/// open the SAME file for read access while this appender still holds the write handle open —
/// contrast <see cref="FileReportWriter"/>'s end-of-run writer, which uses
/// <see cref="FileShare.None"/> because nothing needs to read the file mid-write there.
/// </para>
/// <para>
/// <strong>Diagnose once, then silent — for BOTH failure points:</strong> a bad path at
/// construction and a write/flush failure mid-suite are handled identically: a single
/// type-name-only diagnostic is emitted and the writer is discarded, so every subsequent
/// <see cref="AppendLines"/> call is a silent no-op rather than re-diagnosing (or retrying
/// against) an already-dead sink for every remaining scenario.
/// </para>
/// </remarks>
public sealed class EventStreamAppender : IDisposable, IAsyncDisposable
{
    // UTF-8 without a byte-order mark — identical rationale to FileReportWriter: a leading
    // BOM can trip a line-oriented JSON Lines consumer.
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly TextWriter? _diagnostics;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>
    /// Opens <paramref name="path"/> for incremental append, creating any missing parent
    /// directories and truncating an existing file at the same path (matching
    /// <see cref="FileReportWriter"/>'s overwrite-on-run-start semantics).
    /// </summary>
    /// <param name="path">The destination file for the incremental JSON Lines stream.</param>
    /// <param name="diagnostics">
    /// An optional sink for a single, one-line, type-name-only diagnostic when the file
    /// cannot be opened (a caller-supplied bad path, a permission error). The failure is
    /// swallowed either way: constructing this type must never throw and must never affect
    /// the run's verdict or exit code. <see langword="null"/> discards the notice.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public EventStreamAppender(string path, TextWriter? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        _diagnostics = diagnostics;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // FileMode.Create truncates a stale file from a previous run. FileShare.Read is
            // the load-bearing difference from FileReportWriter.CreateWriter's FileShare.None:
            // it lets a concurrent reader open this SAME path for read access (e.g. `tail -f`,
            // FileShare.ReadWrite on their side) while this handle stays open.
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            // No handle to write to: every subsequent AppendLines call becomes a silent no-op
            // (see the _writer is null guard there) rather than re-diagnosing a dead sink on
            // every scenario completion.
            _writer = null;
            _diagnostics?.WriteLine(
                $"Events-stream open failed for 'events-stream' (verdict unaffected): {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Appends <paramref name="lines"/> to the open file, one per line, each terminated with a
    /// literal <c>'\n'</c> — INCLUDING the last line (proper JSON Lines) — then flushes so a
    /// tailing reader observes only whole, complete lines.
    /// </summary>
    /// <param name="lines">
    /// The event-stream lines for one completed scenario (as produced by
    /// <c>EventStreamJson.ToLine</c>), in the order they should appear in the file.
    /// </param>
    /// <remarks>
    /// A no-op when the file could not be opened (the constructor already diagnosed that), when
    /// a PRIOR call to this method already failed and self-disabled the sink (see below), or
    /// when <paramref name="lines"/> is empty. Thread-safe: callers on different threads (each
    /// scenario slot in <c>ParallelSuiteRunner</c>) may call this concurrently — a single lock
    /// serialises the write + flush so lines from different scenarios can never interleave
    /// mid-record. Any I/O failure here is caught, diagnosed ONCE (type-name only, §17
    /// redaction), and swallowed — appending must never change the run's verdict or exit code
    /// — and, symmetrically with the construction-failure path, the writer is then discarded
    /// (under the same lock) so every SUBSEQUENT call becomes a silent no-op instead of
    /// re-diagnosing (or retrying against) the same dead sink once per remaining scenario, and
    /// so a partially-written line from the failed attempt can never abut a later append with
    /// no separating boundary.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is <see langword="null"/>.</exception>
    public void AppendLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (_writer is null || lines.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                foreach (var line in lines)
                {
                    // Explicit '\n' (never writer.WriteLine, whose NewLine is "\r\n" on
                    // Windows) — JSON Lines is LF-terminated regardless of platform, and this
                    // must match the '\n' the --events archive already joins on.
                    _writer.Write(line);
                    _writer.Write('\n');
                }

                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException
                or ObjectDisposedException)
            {
                _diagnostics?.WriteLine(
                    $"Events-stream append failed for 'events-stream' (verdict unaffected): {ex.GetType().Name}");

                // Symmetric self-disable (matches the construction-failure path and Dispose):
                // once a write fails, the sink is presumed dead for the rest of the run — null
                // out _writer, under this SAME lock, so every SUBSEQUENT AppendLines call is a
                // silent no-op. This guarantees exactly ONE diagnostic total for a sink that
                // dies mid-suite (destination deleted, disk full, removable media unplugged),
                // never one per remaining scenario, and it prevents a partially-written line
                // from this failed attempt ever abutting a later append with no separating
                // boundary. Best-effort disposal releases the underlying OS handle where
                // possible; any further failure here is swallowed — the sink is already dead,
                // there is nothing more to report.
                try
                {
                    _writer?.Dispose();
                }
                catch
                {
                    // Best-effort only: the handle is already in a failed state.
                }
                finally
                {
                    _writer = null;
                }
            }
        }
    }

    /// <summary>
    /// TEST-ONLY seam (issue #258 peer-review follow-up): closes the underlying file
    /// <see cref="Stream"/> out from under this appender WITHOUT going through
    /// <see cref="Dispose"/>, so a test can deterministically simulate a mid-run write failure
    /// (destination deleted, disk removed, handle revoked) without needing a genuinely
    /// unrecoverable I/O fault. The <see cref="StreamWriter"/> itself is left otherwise intact,
    /// so the NEXT <see cref="AppendLines"/> call reaches the write, then throws
    /// <see cref="ObjectDisposedException"/> from the now-closed underlying stream — exercising
    /// EXACTLY the same self-disable path a real mid-suite I/O failure takes.
    /// </summary>
    /// <remarks>
    /// Internal — reachable only from this assembly's own test project via the
    /// <c>InternalsVisibleTo</c> already granted for <see cref="AppendLines"/>'s own testing.
    /// Production code must never call this.
    /// </remarks>
    internal void SimulateBrokenSinkForTests()
    {
        lock (_lock)
        {
            _writer?.BaseStream.Dispose();
        }
    }

    /// <summary>
    /// Closes the underlying file handle. Safe to call more than once; any failure closing
    /// the file is swallowed (never affects the run's verdict or exit code).
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _writer?.Dispose();
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ObjectDisposedException)
            {
                _diagnostics?.WriteLine(
                    $"Events-stream close failed for 'events-stream' (verdict unaffected): {ex.GetType().Name}");
            }
            finally
            {
                _writer = null;
            }
        }
    }

    /// <summary>
    /// Asynchronously closes the underlying file handle. Delegates to <see cref="Dispose"/> —
    /// <see cref="StreamWriter"/> has no genuinely async close path worth taking here, and the
    /// synchronous close is already fast and swallows every failure.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
