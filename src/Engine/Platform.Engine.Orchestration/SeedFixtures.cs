// Platform.Engine.Orchestration — SeedFixtures (S05-A-02).
//
// Pure helper for the seed pipeline: resolve a fixture file path against the seed
// base directory and compute its content hash.  Shared so that S05-B-03 (the
// reproducibility envelope) can reuse the SAME hashing routine to "record the
// content hash of every applied fixture" (docs/02 §3.2.2, line 149) — the seed
// applier and the envelope MUST agree on what a fixture's hash is, so the routine
// lives in one place rather than being duplicated.
//
// Placement rationale (so B-03 can reuse it): the runner project
// (Platform.Engine.Runtime) already references Platform.Engine.Orchestration, so a
// helper here is reachable from the runner without a new project dependency.  The
// method is kept `internal` (not part of the engine's public API surface) and
// exposed to the runner via InternalsVisibleTo on the Orchestration csproj.
//
// Determinism: the hash is the lower-case hex SHA-256 of the file's raw bytes, so
// it is stable across machines and OSes (no line-ending or encoding normalisation —
// the bytes on disk ARE the fixture).

using System.Globalization;
using System.Security.Cryptography;

namespace Platform.Engine.Orchestration;

/// <summary>
/// Pure helpers for resolving and content-hashing <c>environment.seed</c> fixture
/// files (S05-A-02).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ComputeContentHash"/> is the single source of truth for a fixture's
/// content hash.  The seed applier calls it for broker-publish payloads and
/// document fixtures (A-02), and S05-B-03 reuses it to record the content hash of
/// every applied fixture in the reproducibility envelope (docs/02 §3.2.2) — both
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
    /// Thrown when the resolved fixture file does not exist.  Callers in the seed
    /// applier map this to an <see cref="OrchestrationException"/>
    /// (<see cref="OrchestrationErrorKind.Provision"/>) so it surfaces as an
    /// Environment error (§12.1).
    /// </exception>
    internal static string ComputeContentHash(string baseDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        var resolvedPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(
                $"seed fixture file not found: '{resolvedPath}'.",
                resolvedPath);
        }

        var bytes = File.ReadAllBytes(resolvedPath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }
}
