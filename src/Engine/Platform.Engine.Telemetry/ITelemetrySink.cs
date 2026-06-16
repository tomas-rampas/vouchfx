// Platform.Engine.Telemetry — ITelemetrySink + LocalFileTelemetrySink (S10-G-04).
//
// The transport seam.  In v1 the ONLY implementation is LocalFileTelemetrySink, which
// appends one compact JSON line per event to a local outbox file.  This stands in for
// the (out-of-scope, infra) hosted pilot backend; the future HTTP transport will
// implement this SAME interface, so swapping transports never touches the allowlist or
// the builder.

using System.Text;
using System.Text.Json;

namespace Platform.Engine.Telemetry;

/// <summary>
/// The telemetry transport seam: accepts an already-built, allowlisted
/// <see cref="TelemetryEvent"/> and delivers it (S10-G-04).
/// </summary>
/// <remarks>
/// Implementations receive ONLY the privacy-allowlisted <see cref="TelemetryEvent"/>
/// — they never see the raw event stream, so no transport can widen what is sent
/// beyond the allowlist.  v1 ships <see cref="LocalFileTelemetrySink"/> (a local
/// outbox file); a future hosted backend implements this same interface.
/// </remarks>
public interface ITelemetrySink
{
    /// <summary>
    /// Delivers a single telemetry event.
    /// </summary>
    /// <param name="telemetryEvent">The allowlisted event to deliver.</param>
    /// <param name="cancellationToken">Propagated to any async transport work.</param>
    Task SendAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// An <see cref="ITelemetrySink"/> that appends one compact JSON line per event to a
/// local outbox file (<c>{configDir}/vouchfx/telemetry-outbox.jsonl</c>), UTF-8
/// without a BOM (S10-G-04).
/// </summary>
/// <remarks>
/// <para>
/// This is the v1 transport: it persists events to a local JSON Lines file rather
/// than calling out to any network endpoint.  It stands in for the hosted pilot
/// backend (out of scope here, infra) — the future HTTP transport implements the
/// same <see cref="ITelemetrySink"/> contract, so the rest of the subsystem is
/// transport-agnostic.
/// </para>
/// <para>
/// The outbox path is injected via <see cref="ITelemetryPaths"/>, so tests write to a
/// temp directory and never touch the real per-user config.  Each event is one
/// compact line; the file is appended to, so successive runs accumulate (until the
/// user disables telemetry, which clears it).
/// </para>
/// </remarks>
public sealed class LocalFileTelemetrySink : ITelemetrySink
{
    // UTF-8 without a BOM: a leading BOM would corrupt the first JSON line for a
    // line-oriented consumer, exactly as the event-stream file writer avoids it.
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        // One compact object per line — JSON Lines mandate; embedded newlines would
        // corrupt the line-oriented outbox.  Null fields are omitted for compactness.
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ITelemetryPaths _paths;

    /// <summary>
    /// Creates a sink writing to the outbox resolved by <paramref name="paths"/>.
    /// </summary>
    /// <param name="paths">
    /// The on-disk location seam.  Production passes <see cref="DefaultTelemetryPaths"/>;
    /// tests pass a temp-directory-rooted instance so the real outbox is never touched.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public LocalFileTelemetrySink(ITelemetryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>
    /// Serialises <paramref name="telemetryEvent"/> to a single compact JSON line and
    /// APPENDS it (with a trailing newline) to the outbox file, creating the
    /// <c>vouchfx</c> config directory if needed.
    /// </summary>
    /// <param name="telemetryEvent">The allowlisted event to append.</param>
    /// <param name="cancellationToken">Propagated to the async file write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="telemetryEvent"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Best-effort by contract:</strong> telemetry must never disrupt a run, so
    /// this sink swallows file-system faults (a locked outbox, a permission error) rather
    /// than propagating them — the subsystem's fail-silent posture does not depend on the
    /// caller wrapping the call in a try/catch.  Only an <see cref="ArgumentNullException"/>
    /// (a programming error) and <see cref="OperationCanceledException"/> (cooperative
    /// cancellation) surface to the caller.
    /// </para>
    /// </remarks>
    public async Task SendAsync(
        TelemetryEvent telemetryEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);

        try
        {
            var path = _paths.OutboxPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var line = JsonSerializer.Serialize(telemetryEvent, s_jsonOptions);

            // Append (one line per event) so successive runs accumulate in the outbox.
            // FileMode.Append + a fresh StreamWriter keeps each call's write atomic at the
            // line level for the single-process CLI; UTF-8 without a BOM keeps the file a
            // clean JSON Lines stream.  FileShare.ReadWrite (not just Read) lets a SECOND
            // concurrent vouchfx process open the same outbox to append without the second
            // opener failing with a sharing violation — each O_APPEND write is positioned
            // at the current end of file, so concurrent single-line appends do not interleave.
            var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream, Utf8NoBom);
                await using (writer.ConfigureAwait(false))
                {
                    await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or NotSupportedException
                or ArgumentException)
        {
            // Best-effort: a file-system fault while persisting telemetry must never
            // disrupt the run, so it is swallowed here rather than relying solely on the
            // caller's try/catch.  OperationCanceledException is intentionally NOT caught —
            // cancellation is a cooperative signal the caller asked for, not a fault.
        }
    }
}
