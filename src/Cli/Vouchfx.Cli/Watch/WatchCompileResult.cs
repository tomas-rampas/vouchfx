// Vouchfx.Cli — WatchCompileResult (S08-C-01, watch mode).
//
// The outcome of re-reading + re-validating + re-compiling the watched .e2e.yaml file on a
// save.  It is the boundary between WatchSession's reuse/rebuild decision and the engine's
// compile pipeline: a success carries the ENVIRONMENT HASH (so the session can decide
// "unchanged → reuse the kept topology") plus an opaque, already-compiled payload the run
// seam consumes; a failure carries a human-readable message the session reports without
// tearing down the loop (an authoring slip mid-edit must not stop watching).

namespace Vouchfx.Cli.Watch;

/// <summary>
/// The result of re-reading, validating and compiling the watched scenario file (S08-C-01).
/// </summary>
/// <remarks>
/// <para>
/// Discriminates a successful compile (which yields an <see cref="EnvironmentHash"/> used to
/// decide topology reuse, plus an opaque <see cref="Compiled"/> payload) from a failure
/// (which yields only an <see cref="Error"/> message).  The session never inspects
/// <see cref="Compiled"/>; it merely threads it back into the run seam — keeping the engine's
/// internal compiled-scenario types out of the CLI session and its tests.
/// </para>
/// <para>
/// The <see cref="EnvironmentHash"/> is the SOLE input to the reuse-vs-rebuild decision: when
/// it equals the kept topology's hash, the existing topology is re-used (only the
/// — cheap — scenario re-runs); when it differs, the old topology is disposed and a new one
/// built.  The hash is computed by the compile seam from the scenario's <c>environment</c>
/// block alone, so a change confined to <c>steps</c> leaves it unchanged.
/// </para>
/// </remarks>
internal sealed class WatchCompileResult
{
    private WatchCompileResult(
        bool isSuccess,
        string? environmentHash,
        object? compiled,
        string? error)
    {
        IsSuccess = isSuccess;
        EnvironmentHash = environmentHash;
        Compiled = compiled;
        Error = error;
    }

    /// <summary><see langword="true"/> when the file compiled cleanly.</summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// A stable hash of the scenario's <c>environment</c> block, used to decide whether the
    /// kept topology can be re-used.  Non-<see langword="null"/> only on success.
    /// </summary>
    public string? EnvironmentHash { get; }

    /// <summary>
    /// The opaque compiled-scenario payload threaded back into the run seam.  The session does
    /// not inspect it.  Non-<see langword="null"/> only on success.
    /// </summary>
    public object? Compiled { get; }

    /// <summary>
    /// A human-readable compile/validation error message reported to the user.  Non-<see
    /// langword="null"/> only on failure.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// Builds a successful result carrying the environment hash and the opaque compiled payload.
    /// </summary>
    /// <param name="environmentHash">The scenario's environment-block hash (reuse key).</param>
    /// <param name="compiled">The opaque compiled-scenario payload for the run seam.</param>
    public static WatchCompileResult Success(string environmentHash, object compiled)
    {
        ArgumentNullException.ThrowIfNull(environmentHash);
        ArgumentNullException.ThrowIfNull(compiled);
        return new WatchCompileResult(
            isSuccess: true, environmentHash: environmentHash, compiled: compiled, error: null);
    }

    /// <summary>
    /// Builds a failure result carrying the compile/validation error message.
    /// </summary>
    /// <param name="error">The human-readable error to report.</param>
    public static WatchCompileResult Failure(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new WatchCompileResult(
            isSuccess: false, environmentHash: null, compiled: null, error: error);
    }
}
