// Vouchfx.Engine.Telemetry — OutboxDrainState (S12-G-01, Phase A).
//
// The persisted, cross-RUN exponential back-off for the HTTP drain.  Because each
// `vouchfx run` is a fresh process, an in-memory back-off would be lost between runs and
// a hard-down endpoint would be hammered once per run.  This record persists
// `nextAttemptUtc` + `consecutiveFailures` at DrainStatePath (telemetry-drain.json) so a
// run can cheaply decide "is it even worth trying yet?" before opening the outbox.
//
// SAFETY (mirrors TelemetryConsentStore): a missing OR corrupt state file reads back as
// the PERMISSIVE default (attempt allowed, zero failures) and NEVER throws — drain
// bookkeeping must never be able to break the run.  The write reuses the consent store's
// temp-file + File.Move(overwrite:true) ATOMIC-rewrite pattern.
//
// DETERMINISM: the clock and the jitter source are INJECTED (Func<DateTimeOffset> /
// Func<double>), so tests compute the exact next-attempt instant.  Production binds the
// real UtcNow and a thread-safe Random; nothing here calls DateTimeOffset.UtcNow or
// Random directly in a way a test cannot control.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vouchfx.Engine.Telemetry;

/// <summary>
/// The persisted cross-run back-off state for the telemetry HTTP drain (S12-G-01).
/// </summary>
/// <remarks>
/// <para>
/// On a failed drain the next attempt is pushed to
/// <c>now + min(base * 2^consecutiveFailures, cap)</c> with bounded jitter, so a
/// hard-down endpoint backs off geometrically ACROSS separate runs instead of being
/// retried every run.  A successful drain resets the state (immediate next attempt).
/// </para>
/// <para>
/// <strong>Fail-open + never-throws:</strong> a missing or corrupt state file reads back
/// as the permissive default (attempt allowed) and the read never throws — telemetry
/// must be invisible to the run.  The write reuses the consent store's atomic
/// temp-file-then-rename so a reader never observes a half-written state.
/// </para>
/// </remarks>
public sealed record OutboxDrainState
{
    /// <summary>The base back-off interval (1 minute) before exponential growth.</summary>
    public static readonly TimeSpan DefaultBaseBackoff = TimeSpan.FromMinutes(1);

    /// <summary>The back-off ceiling (6 hours): the interval never grows past this.</summary>
    public static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The earliest UTC instant at which the next drain attempt is permitted.  Defaults
    /// to <see cref="DateTimeOffset.MinValue"/> so a fresh install attempts immediately.
    /// </summary>
    [JsonPropertyName("nextAttemptUtc")]
    public DateTimeOffset NextAttemptUtc { get; init; } = DateTimeOffset.MinValue;

    /// <summary>
    /// The number of consecutive failed drains, driving the exponential back-off.  Reset
    /// to zero on the next success.
    /// </summary>
    [JsonPropertyName("consecutiveFailures")]
    public int ConsecutiveFailures { get; init; }

    /// <summary>
    /// Whether a drain attempt is permitted at <paramref name="now"/> (i.e. the back-off
    /// window has elapsed).
    /// </summary>
    /// <param name="now">The current UTC instant.</param>
    /// <returns><see langword="true"/> when <paramref name="now"/> is at/after the next attempt.</returns>
    public bool MayAttempt(DateTimeOffset now) => now >= NextAttemptUtc;

    /// <summary>
    /// The state after a SUCCESSFUL drain: failures reset to zero and the next attempt is
    /// allowed immediately.
    /// </summary>
    /// <returns>A reset state.</returns>
    public static OutboxDrainState Reset() => new();

    /// <summary>
    /// The state after a FAILED drain: the failure count increments and the next attempt
    /// is pushed to <c>now + min(base * 2^failures, cap)</c> with bounded jitter.
    /// </summary>
    /// <param name="now">The current UTC instant.</param>
    /// <param name="jitter">
    /// A jitter fraction in <c>[0,1)</c> (inject a deterministic value in tests; production
    /// passes a thread-safe random).  Adds up to <c>±20%</c> of the computed interval so
    /// many installs do not retry in lock-step (thundering-herd avoidance).
    /// </param>
    /// <param name="baseBackoff">The base interval; defaults to <see cref="DefaultBaseBackoff"/>.</param>
    /// <param name="maxBackoff">The ceiling; defaults to <see cref="DefaultMaxBackoff"/>.</param>
    /// <returns>The backed-off state.</returns>
    public OutboxDrainState AfterFailure(
        DateTimeOffset now,
        double jitter,
        TimeSpan? baseBackoff = null,
        TimeSpan? maxBackoff = null)
    {
        var failures = ConsecutiveFailures + 1;
        var interval = ComputeInterval(
            failures,
            baseBackoff ?? DefaultBaseBackoff,
            maxBackoff ?? DefaultMaxBackoff,
            jitter);

        return this with
        {
            ConsecutiveFailures = failures,
            NextAttemptUtc = now + interval,
        };
    }

    /// <summary>
    /// Reads the persisted state from <paramref name="drainStatePath"/>, returning the
    /// permissive default (attempt allowed, zero failures) when the file is absent OR
    /// unreadable OR corrupt.  Never throws.
    /// </summary>
    /// <param name="drainStatePath">The state file path (from <see cref="ITelemetryPaths.DrainStatePath"/>).</param>
    /// <returns>The persisted (or defaulted) state; never <see langword="null"/>.</returns>
    public static OutboxDrainState Read(string drainStatePath)
    {
        ArgumentNullException.ThrowIfNull(drainStatePath);
        try
        {
            if (!File.Exists(drainStatePath))
            {
                return new OutboxDrainState();
            }

            var json = File.ReadAllText(drainStatePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new OutboxDrainState();
            }

            return JsonSerializer.Deserialize<OutboxDrainState>(json, s_jsonOptions)
                ?? new OutboxDrainState();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or NotSupportedException
                or ArgumentException
                or JsonException)
        {
            // A missing/corrupt/unreadable back-off file must read back as "attempt
            // allowed", never throw — exactly the consent store's safe-default posture.
            return new OutboxDrainState();
        }
    }

    /// <summary>
    /// Atomically persists this state to <paramref name="drainStatePath"/> (temp-file +
    /// <see cref="File.Move(string, string, bool)"/>, the consent store's pattern),
    /// creating the directory if needed.  Best-effort: a file-system fault is swallowed.
    /// </summary>
    /// <param name="drainStatePath">The destination path.</param>
    /// <returns>
    /// <see langword="true"/> on success; <see langword="false"/> when a file-system error
    /// prevented the write (swallowed — drain bookkeeping must never break the tool).
    /// </returns>
    public bool Write(string drainStatePath)
    {
        ArgumentNullException.ThrowIfNull(drainStatePath);
        try
        {
            var dir = Path.GetDirectoryName(drainStatePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(this, s_jsonOptions);

            // Atomic rewrite (same pattern as TelemetryConsentStore.Write): write a sibling
            // temp file, then File.Move(overwrite:true) it over the destination.  A reader
            // (a concurrent run) therefore observes the file as either fully old or fully
            // new — never a truncated state that would mis-read as "attempt allowed".
            var temp = drainStatePath + ".tmp-" + Guid.NewGuid().ToString("n");
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, drainStatePath, overwrite: true);
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

            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or NotSupportedException
                or ArgumentException)
        {
            return false;
        }
    }

    // interval = min(base * 2^(failures-1), cap), then add jitter in [0, 0.2*interval).
    // The shift is computed in ticks with an overflow-safe cap so a large failure count
    // can never overflow the multiplication (it saturates at the cap long before then).
    private static TimeSpan ComputeInterval(
        int failures,
        TimeSpan baseBackoff,
        TimeSpan maxBackoff,
        double jitter)
    {
        // Clamp the exponent so 2^exp stays within a long; once base*2^exp exceeds the cap
        // the result is the cap anyway, so a modest clamp (62) is more than sufficient.
        var exponent = Math.Min(Math.Max(failures - 1, 0), 62);

        var baseTicks = baseBackoff.Ticks;
        var capTicks = maxBackoff.Ticks;

        // Saturating multiply: if shifting would overflow or exceed the cap, use the cap.
        long grownTicks;
        if (baseTicks <= 0)
        {
            grownTicks = 0;
        }
        else if (exponent >= 62 || baseTicks > capTicks >> exponent)
        {
            grownTicks = capTicks;
        }
        else
        {
            grownTicks = Math.Min(baseTicks << exponent, capTicks);
        }

        // Bounded jitter: 0..20% of the interval, derived ONLY from the injected fraction
        // so the result is deterministic under a fixed jitter input.
        var clampedJitter = Math.Min(Math.Max(jitter, 0.0), 1.0);
        var jitterTicks = (long)(grownTicks * 0.2 * clampedJitter);

        return TimeSpan.FromTicks(grownTicks + jitterTicks);
    }
}
