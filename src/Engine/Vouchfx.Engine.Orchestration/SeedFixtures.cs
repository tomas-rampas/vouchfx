// Vouchfx.Engine.Orchestration — SeedFixtures (S05-A-02).
//
// Pure helper for the seed pipeline: resolve a fixture file path against the seed
// base directory and compute its content hash, so that the reproducibility envelope
// can "record the content hash of every applied fixture" (docs/02 §3.2.5) through
// one routine rather than a duplicated one.  Note the envelope is its only
// production consumer: the seed applier does not call this, despite an older
// comment here saying the two "MUST agree on what a fixture's hash is".
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
/// content hash, used to record the content hash of every applied fixture in the
/// reproducibility envelope (docs/02 §3.2.5).
/// </para>
/// <para>
/// <strong>The seed applier does NOT call it.</strong>  An earlier version of this
/// remark said it did, for <c>sql</c> files; grep finds no such call site.  The
/// applier does its own existence check and raises its own
/// <c>OrchestrationException</c>, which is why the <see cref="FileNotFoundException"/>
/// this type throws EXPLICITLY reaches no production observer — see
/// <see cref="ComputeContentHash"/>'s own remarks, and do not restore the
/// shared-caller premise without a call site to point at.
/// </para>
/// <para>
/// <strong>That is a statement about ONE exception, not about this type.</strong> The
/// file read itself raises <see cref="IOException"/> /
/// <see cref="UnauthorizedAccessException"/> naming the RESOLVED path, and the sole
/// caller's catch does not cover either, so those DO escape. Tracked as issue #488;
/// see the comment at the read for what the open decision actually is. Do not read
/// this paragraph, or the fixed throw below it, as saying the type is clean.
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
    /// <see cref="OrchestrationException"/>; no such caller exists.)  One test calls it directly
    /// and asserts the message, so a change here is not silent.
    /// </exception>
    internal static string ComputeContentHash(string baseDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        var resolvedPath = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        if (!File.Exists(resolvedPath))
        {
            // NO RESOLVED PATH IN THE MESSAGE (#357's rule), and NOT because this one reaches an
            // artefact — measured, it does not: the single production caller,
            // ScenarioRunner.HashFixtureOrNull, catches FileNotFoundException and swallows it.
            //
            // #473 CHANGED IT ANYWAY, and the reason is worth stating precisely, because the
            // reasoning it replaces was not wrong so much as conditional. "Unreachable" here is a
            // fact about TODAY'S caller, not a property of this method — the comment that stood
            // here said as much, in the form of an instruction to a future reader ("if a second
            // caller ever appears … this becomes a disclosure"). A rule that holds only while
            // nobody adds a caller is one that a caller silently breaks; nothing goes red, and the
            // resolved path is simply in an artefact one day. #473 was itself a sweep for exactly
            // that shape, so leaving a fourth instance of it behind, documented, would be
            // documenting the next occurrence rather than removing it.
            //
            // THE LEDGER IS THE WRONG TOOL HERE, deliberately, and this is the one #473 site that
            // does not use it. SecurityPathDisclosureLedger substitutes the declared form back
            // into text the ENGINE DID NOT WRITE — librdkafka's, the Docker daemon's, the BCL's.
            // This string is the engine's own, so the declared form simply goes in it. Threading a
            // run-scoped ledger into a pure content-hash helper to fix a message it fully controls
            // would be plumbing bought for nothing.
            //
            // The declared text is the actionable half either way: an author reading "fixtures/
            // absent.json, relative to the suite directory" knows what to fix, and nothing about
            // the host's layout is disclosed.
            throw new FileNotFoundException(
                $"seed fixture file not found: '{relativePath}', relative to the suite directory.",
                relativePath);
        }

        // THIS METHOD IS NOT SETTLED, AND THE THROW ABOVE IS ONLY HALF OF IT — ISSUE #488.
        //
        // The line below hands the RESOLVED path to the BCL, which quotes it back in its own
        // message on the shapes File.Exists cannot pre-empt: a sharing violation
        // (`IOException: The process cannot access the file '<resolved>' …`), a permission denial
        // (`UnauthorizedAccessException: Access to the path '<resolved>' is denied.`), or the file
        // vanishing in the window between the check above and this read. So #473 removed a
        // disclosure from a message no caller observes and left one, two lines later, on a path
        // that is BOTH reachable and worse: ScenarioRunner.HashFixtureOrNull catches
        // FileNotFoundException only, so none of those escapes is swallowed — each propagates out
        // of the envelope build entirely.
        //
        // NOT FIXED HERE, deliberately, because the fix is a decision rather than an edit: whether
        // the envelope should degrade (catch IOException broadly and record a null hash, as it
        // already does for a missing file) or fail the run, and whether this helper should take
        // the run's ledger after all — which the throw above argues against for text the engine
        // writes, but this text is the BCL's. #488 owns that call. It is recorded here rather than
        // in a tracker alone so the next reader of this method does not conclude from the fixed
        // throw above that the file is done.
        var bytes = File.ReadAllBytes(resolvedPath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }
}
