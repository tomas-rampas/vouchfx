// Vouchfx.Engine.Authoring — SecurityArtifactPath (authenticated-infrastructure-mtls,
// slice E — REQ-003's containment rule, hoisted so REQ-016 can share it).
//
// Why this class exists at all, given the rule already had a home. REQ-003's containment
// predicate was written for `Vouchfx.Engine.Runtime.EnvironmentSecurityValidator` and reached
// from there by `SecurityConfigurationAccessor`, whose own remarks state the reason plainly:
// "two spellings of one security rule is how the two drift". Slice E adds a THIRD consumer,
// `Vouchfx.Engine.Orchestration.EnvironmentMapper` (REQ-016 resolves every
// `serverArtifacts[].source` to an absolute host path before handing it to Aspire), and
// Orchestration cannot see Runtime — the project reference runs the other way. The choice was
// therefore between a second copy of the predicate and one shared home both can reach.
// `Vouchfx.Engine.Authoring` is that home: both Orchestration and Runtime already reference it,
// and it is where `SecuritySpec`/`SecurityServerArtifactSpec` — the records these paths come
// out of — already live.
//
// `EnvironmentSecurityValidator.IsContainedWithin` is kept as a one-line forward to this class
// rather than deleted, so every existing caller and test keeps compiling and the validator's
// own extensive remarks stay attached to the name they were written for.
using System.Globalization;

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// The single spelling of REQ-003's containment rule for a declared <c>security</c> artefact
/// path, shared by every stage that resolves one.
/// </summary>
/// <remarks>
/// <para>
/// Three stages resolve these paths and all three must agree: <c>EnvironmentSecurityValidator</c>
/// (pre-topology validation, REQ-003/REQ-004), <c>SecurityConfigurationAccessor</c> (the
/// client-side certificate views, REQ-014) and <c>EnvironmentMapper</c> (the server-side
/// artefact copy, REQ-016).
/// </para>
/// <para>
/// <strong>Not a hardened sandbox boundary.</strong> <see cref="Path.GetFullPath(string)"/> is a
/// purely LEXICAL normalisation: it does not resolve symlinks or junctions, so a symlink placed
/// inside the suite directory can point outside it undetected. Accepted under the current trust
/// model — the suite author already controls the suite directory, and <c>script.csharp</c>
/// already grants that same author arbitrary C#.
/// </para>
/// <para>
/// <strong>WHICH PATH A DIAGNOSTIC MAY NAME.</strong> The DECLARED text only — the author's own
/// input — never the resolved absolute path and never the resolved suite directory. That holds at
/// every stage, validation-time and step-time alike, and it holds for callers not yet written.
/// </para>
/// <para>
/// The reason is that a diagnostic's audience is not knowable from its site. A validation-time
/// message from this class reaches <c>ScenarioCompletedEvent.message</c> by way of
/// <c>ProviderPipeline</c>'s failure and <c>ScenarioRunner</c>'s early message, and from there the
/// §14 event stream, the JUnit <c>message</c> attribute and the HTML report — archived, uploaded,
/// and beyond the reach of any scrubber (<c>ResolvedSecrets.Scrub</c> covers revealed secret
/// VALUES; a filesystem path is never one). An earlier revision of this comment asserted the
/// opposite of each such message — that it "is never placed in an event line" — and licensed the
/// resolved path on that basis. Naming the CONCEPT the path resolves against ("relative to the
/// suite directory") keeps a relative path diagnosable without disclosing the host layout.
/// </para>
/// </remarks>
public static class SecurityArtifactPath
{
    /// <summary>
    /// True when <paramref name="resolvedPath"/> is <paramref name="resolvedBaseDirectory"/>
    /// itself or a descendant of it. Both arguments must already be fully resolved
    /// (<see cref="Path.GetFullPath(string)"/>) absolute paths.
    /// </summary>
    /// <remarks>
    /// <see cref="StringComparison.Ordinal"/>, deliberately — never
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. Every caller rejects a ROOTED declared
    /// path before combining, so the prefix compared against here is always a byte-for-byte copy
    /// of <paramref name="resolvedBaseDirectory"/> and an ordinal comparison is exactly as
    /// permissive as a case-insensitive one for every legitimately-contained path. A
    /// case-insensitive comparison would additionally, and wrongly, ACCEPT a <c>..</c>-escape into
    /// a sibling directory differing from the base only in case — two distinct directories on the
    /// case-sensitive filesystems CI runs on.
    /// </remarks>
    public static bool IsContainedWithin(string resolvedPath, string resolvedBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(resolvedPath);
        ArgumentNullException.ThrowIfNull(resolvedBaseDirectory);

        if (string.Equals(resolvedPath, resolvedBaseDirectory, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = resolvedBaseDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedBaseDirectory
            : resolvedBaseDirectory + Path.DirectorySeparatorChar;

        return resolvedPath.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves one declared, relative artefact path against
    /// <paramref name="resolvedBaseDirectory"/> and checks containment, returning
    /// <see langword="null"/> on success or a ready-to-use diagnostic tail describing the first
    /// rule the path breaks.
    /// </summary>
    /// <param name="declaredPath">The author's own text, as written in the suite.</param>
    /// <param name="resolvedBaseDirectory">
    /// The fully resolved suite directory. Must be the SAME base every other stage used — a path
    /// resolved against a different base is still contained within THAT base, so containment
    /// cannot detect the divergence and the run would silently read a file the suite never named.
    /// </param>
    /// <param name="resolvedPath">
    /// The absolute host path on success; <see langword="null"/> when a diagnostic is returned.
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the path is blank-free, relative and contained; otherwise a
    /// sentence fragment naming the fault, with no field prefix — each caller prepends its own
    /// field path so the message reads in that caller's own idiom.
    /// </returns>
    public static string? TryResolveContained(
        string? declaredPath, string resolvedBaseDirectory, out string? resolvedPath)
    {
        ArgumentNullException.ThrowIfNull(resolvedBaseDirectory);

        resolvedPath = null;

        if (string.IsNullOrWhiteSpace(declaredPath))
        {
            return FormattableString.Invariant($"declared value '{declaredPath}' is blank.");
        }

        // REQ-003 requires a path RELATIVE to the suite directory, not merely one that happens to
        // land inside it. Rejected before Path.Combine, which DISCARDS its first argument outright
        // when the second is rooted — so a rooted path was never resolved "relative to the suite
        // directory" at all, whatever containment then concluded about the result.
        if (Path.IsPathRooted(declaredPath))
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' must be a path relative to the suite directory, not an absolute path.",
                declaredPath);
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(resolvedBaseDirectory, declaredPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Format(
                CultureInfo.InvariantCulture, "'{0}' is not a valid path ({1})", declaredPath, ex.Message);
        }

        if (!IsContainedWithin(candidate, resolvedBaseDirectory))
        {
            // Neither the resolved candidate nor the base directory is named: both are absolute
            // host paths, and every diagnostic this method returns is prefixed by a caller and
            // carried into the written artefacts. See this class's remarks.
            return string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' resolves outside the suite directory.",
                declaredPath);
        }

        resolvedPath = candidate;
        return null;
    }
}
