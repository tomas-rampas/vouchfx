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
/// <strong>WHICH PATH A DIAGNOSTIC MAY NAME, and why the two rules in this feature are not in
/// conflict.</strong> This class's diagnostics carry the RESOLVED absolute host path;
/// <c>SecurityConfigurationAccessor.EnsureContained</c> deliberately carries the DECLARED text
/// only. The split is by AUDIENCE, not by site, and each half is forced:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <strong>Validation-time</strong> — this class, <c>EnvironmentSecurityValidator</c> and
///     <c>ServerArtifactInjection.Plan</c>. Their messages reach the author's own terminal and
///     nothing else (verified: each is written through <c>TextWriter.WriteLineAsync</c> on the
///     early-exit / environment-configuration-error path and is never placed in an event line).
///     REQ-004's acceptance criterion REQUIRES the resolved path here — "fails <c>vouchfx
///     validate</c> … with a message containing the resolved path" — because "not found" without
///     the path it looked in is the diagnostic that sends an author hunting.
///   </description></item>
///   <item><description>
///     <strong>Step-time</strong> — <c>SecurityConfigurationAccessor</c>. Its
///     <c>SecurityMaterialException</c> surfaces through a provider's general catch into
///     <c>Vars</c> and the §14 event stream, which is archived, uploaded and rendered, and where
///     no scrubber can redact a host path after the fact. Declared text only.
///   </description></item>
/// </list>
/// <para>
/// The rule to apply when adding a caller is therefore "name the resolved path if and only if the
/// message cannot reach the event stream" — not "pick one and use it everywhere", which would
/// either break REQ-004 or leak host paths into every archived run.
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
            return string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' resolves outside the suite directory (resolved to '{1}', which is not contained "
                + "within '{2}').",
                declaredPath,
                candidate,
                resolvedBaseDirectory);
        }

        resolvedPath = candidate;
        return null;
    }
}
