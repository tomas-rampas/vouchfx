// Vouchfx.Engine.Telemetry — TelemetryConsentStore (S10-G-04).
//
// Reads/writes the consent + install-id store, shows the one-time first-run notice,
// and owns the enable/disable lifecycle (including install-id minting/deletion and
// outbox clearing).  Every path is injected via ITelemetryPaths, so tests use a temp
// directory and never touch the real %APPDATA%.
//
// SAFETY: reading a missing OR corrupt store NEVER throws — it returns the safe
// default (Undecided, no install id).  Telemetry bookkeeping must never be able to
// break the tool.

using System.Text.Json;

namespace Vouchfx.Engine.Telemetry;

/// <summary>
/// Persists and mutates the telemetry consent + install-id state and shows the
/// one-time first-run notice (S10-G-04).
/// </summary>
/// <remarks>
/// <para>
/// All on-disk locations come from an injected <see cref="ITelemetryPaths"/>, so the
/// base directory is fully controllable and tests never read or write the real
/// per-user config.  Reading a missing or corrupt store returns a safe default
/// (<see cref="TelemetryConsent.Undecided"/>, no install id) and never throws —
/// telemetry must be invisible and must never break the tool.
/// </para>
/// </remarks>
public sealed class TelemetryConsentStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ITelemetryPaths _paths;

    /// <summary>
    /// Creates a store backed by the supplied <paramref name="paths"/>.
    /// </summary>
    /// <param name="paths">
    /// The on-disk location seam.  Production passes
    /// <see cref="DefaultTelemetryPaths"/>; tests pass a temp-directory-rooted
    /// instance so the real <c>%APPDATA%</c> is never touched.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    public TelemetryConsentStore(ITelemetryPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>
    /// Reads the persisted telemetry state, returning a safe default
    /// (<see cref="TelemetryConsent.Undecided"/>, <c>noticeShown=false</c>, no
    /// install id) when the file is absent OR unreadable OR corrupt.
    /// </summary>
    /// <returns>The persisted (or defaulted) state; never <see langword="null"/>.</returns>
    /// <remarks>
    /// This method NEVER throws.  A missing file, a permission error, a truncated or
    /// non-JSON file, or a JSON shape that does not deserialise all collapse to the
    /// same safe default — telemetry bookkeeping must never be able to break the run.
    /// </remarks>
    public TelemetryState Read()
    {
        try
        {
            var path = _paths.ConsentStorePath;
            if (!File.Exists(path))
            {
                return new TelemetryState();
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new TelemetryState();
            }

            var state = JsonSerializer.Deserialize<TelemetryState>(json, s_jsonOptions);
            return state ?? new TelemetryState();
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or NotSupportedException
                or ArgumentException
                or JsonException)
        {
            // A missing/corrupt/unreadable store must read back as the safe default
            // (Undecided), never throw — telemetry must be invisible to the tool.
            return new TelemetryState();
        }
    }

    /// <summary>
    /// Persists <paramref name="state"/> to the consent store, creating the
    /// <c>vouchfx</c> config directory if it does not yet exist.
    /// </summary>
    /// <param name="state">The state to write.</param>
    /// <returns>
    /// <see langword="true"/> when the write succeeded; <see langword="false"/> when
    /// a file-system error prevented it (swallowed — telemetry must never break the
    /// tool).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    public bool Write(TelemetryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            var path = _paths.ConsentStorePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(state, s_jsonOptions);

            // Write to a sibling temp file, then atomically replace the destination via
            // File.Move(overwrite: true).  A plain truncate-then-write (File.WriteAllText)
            // leaves a window where a crash or a concurrent writer can produce a truncated
            // file; that file reads back as Undecided (Read's safe default), silently
            // resetting an opted-in user AND re-showing the one-time first-run notice.  The
            // temp-file-then-rename keeps the on-disk store either fully old or fully new —
            // a reader never observes a half-written state.  The temp file lives in the SAME
            // directory so the move is a same-volume rename (atomic), not a cross-volume copy.
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("n");
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                // Best-effort cleanup of the temp file on any failure of the write/replace;
                // the outer catch still classifies and swallows the original fault.
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
                    // Leaving a stray temp file behind must never escalate into a throw.
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

    /// <summary>
    /// Shows the one-time first-run notice to <paramref name="stderr"/> WHEN consent
    /// is <see cref="TelemetryConsent.Undecided"/> and the notice has not been shown
    /// before, then records that it was shown.  The notice itself collects and sends
    /// NOTHING — it only informs the user.
    /// </summary>
    /// <param name="stderr">The writer the notice is written to (stderr by convention).</param>
    /// <returns>
    /// <see langword="true"/> when the notice was shown on this call;
    /// <see langword="false"/> when it was suppressed (already shown, or consent is no
    /// longer Undecided).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="stderr"/> is null.</exception>
    /// <remarks>
    /// Idempotent across runs: after the first display the <c>noticeShown</c> flag is
    /// persisted, so the notice never appears again even while consent stays Undecided.
    /// </remarks>
    public bool ShowFirstRunNoticeIfNeeded(TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        var state = Read();
        if (state.Consent != TelemetryConsent.Undecided || state.NoticeShown)
        {
            return false;
        }

        stderr.WriteLine(FirstRunNoticeText);

        // Persist that the notice was shown so it never nags again.  A failed write
        // is swallowed (Write returns false) — at worst the notice shows once more.
        Write(state with { NoticeShown = true });
        return true;
    }

    /// <summary>
    /// Enables telemetry: sets consent to <see cref="TelemetryConsent.Enabled"/> and
    /// mints a fresh install id IF one does not already exist.  An existing install id
    /// is preserved (re-enabling is idempotent on the id).
    /// </summary>
    /// <returns>The state after enabling (its <c>InstallId</c> is guaranteed non-null).</returns>
    public TelemetryState Enable()
    {
        var state = Read();
        // Mint an install id ONLY on enable, and only if absent — re-enabling keeps
        // the same id so the install's history stays linkable while consent holds.
        var installId = state.InstallId ?? Guid.NewGuid();
        var updated = state with
        {
            Consent = TelemetryConsent.Enabled,
            InstallId = installId,
        };
        Write(updated);
        return updated;
    }

    /// <summary>
    /// Disables telemetry: sets consent to <see cref="TelemetryConsent.Disabled"/>,
    /// DELETES the install id from the store, and IMMEDIATELY clears/removes the
    /// outbox file — severing every link to past activity well within the data
    /// retention requirement.
    /// </summary>
    /// <returns>The state after disabling (its <c>InstallId</c> is <see langword="null"/>).</returns>
    /// <remarks>
    /// The <c>noticeShown</c> flag is preserved (an explicit decision means the notice
    /// should never reappear).  Outbox removal is best-effort and swallows file-system
    /// errors — a failure to delete must not throw, but on any normal system the file
    /// is removed at once.
    /// </remarks>
    public TelemetryState Disable()
    {
        var state = Read();
        var updated = state with
        {
            Consent = TelemetryConsent.Disabled,
            InstallId = null,
        };
        Write(updated);

        // Clear the outbox immediately: disabling must leave no queued events behind.
        DeleteOutbox();
        return updated;
    }

    /// <summary>
    /// Best-effort deletion of the local outbox file.  Swallows file-system errors —
    /// removing queued telemetry must never throw.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the outbox no longer exists after the call
    /// (deleted, or it was already absent); <see langword="false"/> only when a
    /// file-system error left it in place.
    /// </returns>
    public bool DeleteOutbox()
    {
        try
        {
            var path = _paths.OutboxPath;
            if (File.Exists(path))
            {
                File.Delete(path);
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

    /// <summary>
    /// The exact text of the one-time first-run notice.  It states what is collected,
    /// that NOTHING is sent until the user explicitly opts in, and how to opt in / out.
    /// Held as a constant so the notice wording is reviewable in one place.
    /// </summary>
    public const string FirstRunNoticeText =
        "vouchfx can collect anonymous, aggregate usage telemetry (tool/engine/.NET\n"
        + "versions, step + scenario verdict counts, which built-in step kinds ran, and\n"
        + "startup timings) to help prioritise the engine. It NEVER collects your test\n"
        + "contents, captured values, secrets, URLs, image names, scenario names, or step\n"
        + "ids.\n"
        + "\n"
        + "Telemetry is OFF by default and NOTHING is sent unless you opt in:\n"
        + "  enable  : vouchfx telemetry enable\n"
        + "  opt out : vouchfx telemetry disable   (or set VOUCHFX_NO_TELEMETRY=1)\n"
        + "  status  : vouchfx telemetry status\n"
        + "\n"
        + "This notice is shown once.";
}
