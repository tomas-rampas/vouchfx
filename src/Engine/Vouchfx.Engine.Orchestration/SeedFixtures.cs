// Vouchfx.Engine.Orchestration — SeedFixtures (S05-A-02).
//
// Pure helper for the seed pipeline: resolve a fixture file path against the seed
// base directory and compute its content hash.  Shared so that S05-B-03 (the
// reproducibility envelope) can reuse the SAME hashing routine to "record the
// content hash of every applied fixture" (docs/02 §3.2.5) — the seed
// applier and the envelope MUST agree on what a fixture's hash is, so the routine
// lives in one place rather than being duplicated.
//
// Placement rationale (so B-03 can reuse it): the runner project
// (Vouchfx.Engine.Runtime) already references Vouchfx.Engine.Orchestration, so a
// helper here is reachable from the runner without a new project dependency.  The
// method is kept `internal` (not part of the engine's public API surface) and
// exposed to the runner via InternalsVisibleTo on the Orchestration csproj.
//
// Determinism: the hash is the lower-case hex SHA-256 of the file's raw bytes, so
// it is stable across machines and OSes (no line-ending or encoding normalisation —
// the bytes on disk ARE the fixture).

using System.Globalization;
using System.Security.Cryptography;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Pure helpers for resolving and content-hashing <c>environment.seed</c> fixture
/// files (S05-A-02).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ComputeContentHash"/> is the single source of truth for a fixture's
/// content hash.  The seed applier calls it for <c>sql</c> files — the only seed
/// kind in the v1 language — and S05-B-03 reuses it to record the content hash of
/// every applied fixture in the reproducibility envelope (docs/02 §3.2.5) — both
/// must produce identical hashes, hence one shared routine.
/// </para>
/// </remarks>
internal static class SeedFixtures
{
    /// <summary>
    /// Computes the lower-case hexadecimal SHA-256 of the bytes of the fixture file
    /// at <paramref name="relativePath"/>, resolved against
    /// <paramref name="baseDirectory"/>.
    /// </summary>
    /// <param name="baseDirectory">
    /// The base directory against which <paramref name="relativePath"/> is resolved.
    /// </param>
    /// <param name="relativePath">
    /// The fixture file path, relative to <paramref name="baseDirectory"/> (an
    /// absolute path is also accepted and used as-is by <see cref="Path.Combine"/>).
    /// </param>
    /// <returns>
    /// The 64-character lower-case hex SHA-256 digest of the file's raw bytes.
    /// </returns>
    /// <exception cref="FileNotFoundException">
    /// Thrown when the resolved fixture file does not exist.  <strong>No production caller
    /// observes it.</strong>  This method has exactly one call site in <c>src/</c> —
    /// <c>ScenarioRunner.HashFixtureOrNull</c> — which catches this exception and swallows it, so
    /// a missing fixture is reported by the seed applier's own existence check rather than by this
    /// throw.  (An earlier version of this remark claimed callers in the seed applier map it to an
    /// <see cref="OrchestrationException"/>; no such caller exists, and that claim is why the
    /// message below was allowed to keep a resolved absolute path when every sibling diagnostic
    /// lost one.)  One test calls it directly and asserts the message, so a change here is not
    /// silent.
    /// </exception>
    internal static string ComputeContentHash(string baseDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        var resolvedPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        if (!File.Exists(resolvedPath))
        {
            // The resolved path in this message does NOT reach a written artefact, and that is
            // the only reason it is allowed to stand where every sibling diagnostic had its
            // resolved half removed (#357's rule). This throw has exactly one caller,
            // ScenarioRunner.HashFixtureOrNull, which catches FileNotFoundException and swallows
            // it — so the string is unreachable rather than merely terminal-only. If a second
            // caller ever appears, or that catch stops swallowing, this becomes a disclosure on
            // the same channel as the rest and must lose its resolved half too.
            throw new FileNotFoundException(
                $"seed fixture file not found: '{resolvedPath}'.",
                resolvedPath);
        }

        var bytes = File.ReadAllBytes(resolvedPath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }
}
