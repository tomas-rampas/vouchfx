// Vouchfx.Engine.Telemetry — ITelemetryPaths (S10-G-04).
//
// The single seam that decides WHERE telemetry state lives on disk.  It exists so
// that EVERY test injects a throwaway temp directory and NEVER touches the real
// per-user %APPDATA%/vouchfx folder — a hard requirement for the privacy tests
// (they must not read, write, or mutate a developer's actual telemetry consent).

namespace Vouchfx.Engine.Telemetry;

/// <summary>
/// Resolves the on-disk locations for telemetry state (the consent + install-id
/// store and the local outbox).
/// </summary>
/// <remarks>
/// All persistence in the telemetry subsystem flows through this abstraction so the
/// base directory is fully injectable.  Production uses
/// <see cref="DefaultTelemetryPaths"/> (per-user <c>%APPDATA%/vouchfx</c>); tests
/// inject a temp directory and therefore can never touch the real per-user config.
/// </remarks>
public interface ITelemetryPaths
{
    /// <summary>
    /// The absolute path to the consent + install-id store JSON file
    /// (<c>{baseDir}/vouchfx/telemetry.json</c>).
    /// </summary>
    string ConsentStorePath { get; }

    /// <summary>
    /// The absolute path to the local telemetry outbox — the append-only JSON Lines
    /// file the <see cref="LocalFileTelemetrySink"/> writes to
    /// (<c>{baseDir}/vouchfx/telemetry-outbox.jsonl</c>).
    /// </summary>
    string OutboxPath { get; }

    /// <summary>
    /// The absolute path to the cross-run drain back-off state file
    /// (<c>{baseDir}/vouchfx/telemetry-drain.json</c>), persisting
    /// <c>nextAttemptUtc</c> + <c>consecutiveFailures</c> so a failed HTTP drain backs
    /// off across separate <c>vouchfx run</c> invocations rather than retrying every run
    /// (S12-G-01).
    /// </summary>
    /// <remarks>
    /// Additive to the v1 seam: like the consent store and outbox, this path is injected
    /// so tests write to a temp directory and never touch the real per-user config.  A
    /// missing or corrupt state file reads back as the permissive default (attempt
    /// allowed) — drain bookkeeping must never be able to break the run.
    /// </remarks>
    string DrainStatePath { get; }
}

/// <summary>
/// The production <see cref="ITelemetryPaths"/>: state lives under a
/// <c>vouchfx</c> folder inside a per-user base directory (by default
/// <see cref="Environment.SpecialFolder.ApplicationData"/>, i.e. <c>%APPDATA%</c> on
/// Windows / <c>~/.config</c> on Linux).
/// </summary>
/// <remarks>
/// The base directory is a constructor parameter so tests inject a temp directory.
/// The parameterless constructor resolves the real per-user
/// <see cref="Environment.SpecialFolder.ApplicationData"/> for production use.
/// </remarks>
public sealed class DefaultTelemetryPaths : ITelemetryPaths
{
    private const string VouchfxFolder = "vouchfx";
    private const string ConsentFileName = "telemetry.json";
    private const string OutboxFileName = "telemetry-outbox.jsonl";
    private const string DrainStateFileName = "telemetry-drain.json";

    private readonly string _vouchfxDir;

    /// <summary>
    /// Creates the production paths under the real per-user
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> directory.
    /// </summary>
    public DefaultTelemetryPaths()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
    {
    }

    /// <summary>
    /// Creates paths rooted at an explicit <paramref name="baseDirectory"/>.  Tests
    /// pass a temp directory so they never touch the real <c>%APPDATA%</c>.
    /// </summary>
    /// <param name="baseDirectory">
    /// The base directory under which the <c>vouchfx</c> folder (and its
    /// <c>telemetry.json</c> / <c>telemetry-outbox.jsonl</c> files) live.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="baseDirectory"/> is <see langword="null"/>.
    /// </exception>
    public DefaultTelemetryPaths(string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        _vouchfxDir = Path.Combine(baseDirectory, VouchfxFolder);
    }

    /// <inheritdoc />
    public string ConsentStorePath => Path.Combine(_vouchfxDir, ConsentFileName);

    /// <inheritdoc />
    public string OutboxPath => Path.Combine(_vouchfxDir, OutboxFileName);

    /// <inheritdoc />
    public string DrainStatePath => Path.Combine(_vouchfxDir, DrainStateFileName);
}
