// Vouchfx.Cli — WatchCompileResult (S08-C-01, watch mode).
//
// The outcome of re-reading + re-planning the watched .e2e.yaml file on a save.  It is the
// boundary between WatchSession's reuse/rebuild decision and the engine's pre-topology stage.
// THREE outcomes, not two:
//
//   • SUCCESS  — the save passed every pre-topology gate.  Carries the TOPOLOGY FINGERPRINT (so
//     the session can decide "unchanged → reuse the kept topology") plus an opaque, already-planned
//     payload the run seam consumes.
//   • REFUSED  — a pre-topology gate rejected the save.  The seam has ALREADY rendered the
//     diagnostic and the refusal's event pair, so this carries no Error: reporting it again would
//     print the fault twice.
//   • FAILURE  — the content did not parse into an AST at all, so there is nothing to render an
//     event pair for.  Carries the message, which the session's report sink writes.
//
// The Refused arm exists because of #370: the authoring doors moved AHEAD of the build seam, and
// their output is a rendered report (events + diagnostic), not a bare line.  The run path uses the
// same "printed here, once; the completion path is told so it prints no duplicate" split.

namespace Vouchfx.Cli.Watch;

/// <summary>
/// The result of re-reading, parsing and planning the watched scenario file (S08-C-01).
/// </summary>
/// <remarks>
/// <para>
/// Discriminates a successful plan (which yields a <see cref="TopologyFingerprint"/> used to decide
/// topology reuse, plus an opaque <see cref="Compiled"/> payload) from a refusal already rendered by
/// the compile seam, and from a bare failure (which yields only an <see cref="Error"/> message).
/// The session never inspects <see cref="Compiled"/>; it merely threads it back into the run seam —
/// keeping the engine's internal planned-scenario types out of the CLI session and its tests.
/// </para>
/// <para>
/// <strong>The <see cref="TopologyFingerprint"/> is the SOLE input to the reuse-vs-rebuild
/// decision, and it is a digest of the WHOLE topology request rather than of the
/// <c>environment</c> block alone.</strong> When it equals the kept topology's, the existing
/// topology is re-used (only the — cheap — scenario re-runs); when it differs, the old topology is
/// disposed and a new one built. The environment-hash-only form it replaces had two measured
/// residuals, both steps-derived: a save that added a step targeting a previously-untargeted
/// <c>project:</c> worker never re-ran #348's refusal, and a save adding the first Kafka step
/// against a service left that service staged as a URL where the step expects a bare
/// <c>host:port</c> authority. See <c>ScenarioRunner.ComputeTopologyFingerprint</c> for what the
/// widening costs — a steps-only edit that changes no TARGET NAME still reuses.
/// </para>
/// </remarks>
internal sealed class WatchCompileResult
{
    private WatchCompileResult(
        bool isSuccess,
        string? topologyFingerprint,
        object? compiled,
        string? error)
    {
        IsSuccess = isSuccess;
        TopologyFingerprint = topologyFingerprint;
        Compiled = compiled;
        Error = error;
    }

    /// <summary><see langword="true"/> when the file planned cleanly.</summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// A stable digest of every input the topology would be built from, used to decide whether the
    /// kept topology can be re-used.  Non-<see langword="null"/> only on success.
    /// </summary>
    public string? TopologyFingerprint { get; }

    /// <summary>
    /// The opaque planned-scenario payload threaded back into the run seam.  The session does
    /// not inspect it.  Non-<see langword="null"/> only on success.
    /// </summary>
    public object? Compiled { get; }

    /// <summary>
    /// A human-readable error message for the session to report, or <see langword="null"/> when
    /// there is nothing left to report — either because the save succeeded, or because the seam
    /// already rendered the refusal itself.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// Builds a successful result carrying the topology fingerprint and the opaque planned payload.
    /// </summary>
    /// <param name="topologyFingerprint">The reuse key (see the class remarks).</param>
    /// <param name="compiled">The opaque planned-scenario payload for the run seam.</param>
    public static WatchCompileResult Success(string topologyFingerprint, object compiled)
    {
        ArgumentNullException.ThrowIfNull(topologyFingerprint);
        ArgumentNullException.ThrowIfNull(compiled);
        return new WatchCompileResult(
            isSuccess: true,
            topologyFingerprint: topologyFingerprint,
            compiled: compiled,
            error: null);
    }

    /// <summary>
    /// Builds a non-success result for a save a pre-topology gate REFUSED and whose diagnostic and
    /// event pair the compile seam has already rendered (#370).
    /// </summary>
    /// <remarks>
    /// <see cref="Error"/> is deliberately <see langword="null"/>: the session's report sink writes
    /// whatever it is given, so a message here would print the same fault a second time as a bare
    /// line under the rendered report. The kept topology, if any, is untouched — the "report and
    /// keep watching" contract is unchanged by where the refusal now happens.
    /// </remarks>
    public static WatchCompileResult Refused() => new(
        isSuccess: false, topologyFingerprint: null, compiled: null, error: null);

    /// <summary>
    /// Builds a failure result carrying a compile/validation error message the session reports.
    /// </summary>
    /// <param name="error">The human-readable error to report.</param>
    public static WatchCompileResult Failure(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new WatchCompileResult(
            isSuccess: false, topologyFingerprint: null, compiled: null, error: error);
    }
}
