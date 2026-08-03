// Vouchfx.Engine.Runtime — EnvironmentSecurityValidator (authenticated-infrastructure-mtls,
// PR A).
//
// Environment-level, pre-topology validation for the `security` block's path-valued
// fields (REQ-003 containment, REQ-004 existence, EDGE-006 — a traversal attempt that
// happens to point at a real file elsewhere on the host still fails as a containment
// error, never a "found"/"not found" one). Called from ProviderPipeline.Compile — the
// SAME pre-topology stage ScenarioValidator's "Pipeline" stage (the engine seam behind
// `vouchfx validate`) and ScenarioRunner's run path both already call before
// SuiteTopology.StartAsync — so a security artefact naming a path outside the suite
// directory, or a declared path that does not exist on the host, is caught at
// `vouchfx validate` / pre-topology `vouchfx run` time, never surfaced later as an
// opaque container-startup or TLS-handshake failure.
//
// Deliberately a separate file/class from ProviderPipeline, mirroring that file's own
// header note: each concern gets a dedicated static class so it is tested in isolation.
//
// Scope note (PR A only): this validates ONLY the host-filesystem shape of declared
// paths. It does not resolve `profile`/`endpoint` (REQ-002's requiredness is enforced by
// the JSON Schema layer alone), does not probe the endpoint (REQ-005, a later PR), and
// does not orchestrate the actual container-file copy (REQ-016, a later PR).

using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Validates the path-valued fields of every declared <c>security</c> block
/// (<c>caCert</c>, <c>clientCert</c>, <c>clientKey</c>, each
/// <c>serverArtifacts[].source</c>) across <c>environment.services</c> and
/// <c>environment.dependencies</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two rules, checked in this fixed order for every DECLARED path (REQ-003, EDGE-006 —
/// containment before existence):
/// </para>
/// <list type="number">
///   <item><description>
///   <strong>Containment</strong> — the path, resolved relative to the suite
///   directory, must not escape it.
///   </description></item>
///   <item><description>
///   <strong>Existence</strong> — the resolved path must exist on the host.
///   </description></item>
/// </list>
/// <para>
/// An <strong>undeclared</strong> optional field (<c>caCert</c> above all) is
/// <em>absent, not missing</em> (REQ-004(b)): neither rule is applied to it — no check,
/// no synthesis, no message for it.
/// </para>
/// </remarks>
internal static class EnvironmentSecurityValidator
{
    /// <summary>
    /// Validates every declared <c>security</c> block's path-valued fields across
    /// <paramref name="ast"/>'s <c>environment.services</c> and
    /// <c>environment.dependencies</c>.
    /// </summary>
    /// <param name="ast">The normalised scenario AST.</param>
    /// <param name="suiteDirectory">
    /// The directory containing the suite's own <c>.e2e.yaml</c> file — the same base
    /// directory <c>IProjectContext.SuiteDirectory</c> exposes and
    /// <c>environment.seed</c> / <c>script.csharp</c>'s <c>file</c> field already
    /// resolve against. Every declared path is resolved against THIS directory and must
    /// not escape it (REQ-003).
    /// </param>
    /// <returns>
    /// The first containment or existence failure encountered (services checked before
    /// dependencies; within each, map iteration order), or <see langword="null"/> when
    /// every declared path is both contained and exists.
    /// </returns>
    internal static ValidationFailure? Validate(ScenarioAst ast, string suiteDirectory)
    {
        string resolvedSuiteDirectory;
        try
        {
            resolvedSuiteDirectory = Path.GetFullPath(suiteDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Copilot review finding (PR #345, thread 3698546487): every DECLARED
            // artefact path already fails closed via ValidatePath's own try/catch below
            // (see its Path.Combine call), but the suite directory itself — the base
            // every artefact resolves against — reached Path.GetFullPath with no guard
            // of its own, so a malformed suiteDirectory (e.g. one embedding a NUL) would
            // throw straight out through this unguarded caller: Stage 3a of
            // ScenarioValidator.ValidateScenario calls ProviderPipeline.Compile (which
            // calls this validator) with no try/catch of its own around it. No field
            // path is named here, deliberately — unlike a per-artefact ValidationFailure,
            // this failure is about the base directory itself, not any one declared field.
            return new ValidationFailure(
                $"suite directory '{suiteDirectory}' is not a valid path ({ex.Message})")
            {
                IsSecurityPreflight = true,
            };
        }

        var services = ast.Environment?.Services;
        if (services is not null)
        {
            foreach (var (name, spec) in services)
            {
                var failure = ValidateSecurity(spec.Security, "services", name, resolvedSuiteDirectory);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        var dependencies = ast.Environment?.Dependencies;
        if (dependencies is not null)
        {
            foreach (var (name, spec) in dependencies)
            {
                var failure = ValidateSecurity(spec.Security, "dependencies", name, resolvedSuiteDirectory);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validates one service's or dependency's <see cref="SecuritySpec"/> (when
    /// declared): <c>caCert</c>, <c>clientCert</c>, <c>clientKey</c>, then every
    /// <c>serverArtifacts[].source</c> in declared order.
    /// </summary>
    private static ValidationFailure? ValidateSecurity(
        SecuritySpec? security, string ownerKindPlural, string ownerName, string resolvedSuiteDirectory)
    {
        if (security is null)
        {
            return null;
        }

        var failure =
            ValidatePath(security.CaCert, "caCert", ownerKindPlural, ownerName, resolvedSuiteDirectory) ??
            ValidatePath(security.ClientCert, "clientCert", ownerKindPlural, ownerName, resolvedSuiteDirectory) ??
            ValidatePath(security.ClientKey, "clientKey", ownerKindPlural, ownerName, resolvedSuiteDirectory);
        if (failure is not null)
        {
            return failure;
        }

        if (security.ServerArtifacts is null)
        {
            return null;
        }

        for (var i = 0; i < security.ServerArtifacts.Count; i++)
        {
            var fieldName = $"serverArtifacts[{i}].source";
            var artifactFailure = ValidatePath(
                security.ServerArtifacts[i].Source, fieldName, ownerKindPlural, ownerName, resolvedSuiteDirectory);
            if (artifactFailure is not null)
            {
                return artifactFailure;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates a single declared path-valued field: containment first (REQ-003,
    /// EDGE-006), then existence (REQ-004). Returns <see langword="null"/> without
    /// performing either check when <paramref name="declaredPath"/> is <see
    /// langword="null"/> — i.e. absent (REQ-004(b) — an undeclared optional field is
    /// absent, not missing).
    /// </summary>
    private static ValidationFailure? ValidatePath(
        string? declaredPath,
        string fieldName,
        string ownerKindPlural,
        string ownerName,
        string resolvedSuiteDirectory)
    {
        if (declaredPath is null)
        {
            return null;
        }

        var fieldPath = $"environment.{ownerKindPlural}.{ownerName}.security.{fieldName}";

        // A DECLARED but blank value is a different case from an absent one (REQ-004(b)
        // above): the schema's 'minLength: 1' already rejects a literal "" outright on a
        // real `vouchfx validate` CLI run, but 'minLength' counts CHARACTERS, so a
        // whitespace-only value (e.g. "   ") satisfies it and reaches here undetected —
        // and unlike a provider step, nothing sits behind this authoring surface to catch
        // it a second time (there is no provider Validate call in this path). This check
        // is therefore the one reachable gate for a declared-but-blank value, both for
        // that whitespace case on a real CLI run and, for the empty-string case too, for
        // direct engine embedding that bypasses the schema layer entirely.
        if (string.IsNullOrWhiteSpace(declaredPath))
        {
            return new ValidationFailure($"{fieldPath}: declared value '{declaredPath}' is blank.")
            {
                IsSecurityPreflight = true,
            };
        }

        // REQ-003's own wording: every path-valued security field "MUST be resolved
        // relative to the directory containing the .e2e.yaml file". Reject a ROOTED
        // declaredPath here, BEFORE Path.Combine, rather than let it reach Path.Combine
        // and rely on containment to catch it (critic MAJOR-2). Path.Combine DISCARDS
        // its first argument outright when the second is rooted (documented .NET
        // behaviour) — so a rooted declaredPath was never actually resolved "relative to
        // the suite directory" at all, regardless of what IsContainedWithin went on to
        // decide about the result. Checking containment on that result papered over two
        // distinct defects instead of preventing them: (a) a rooted path that HAPPENED to
        // land inside the suite directory validated successfully, even though REQ-003
        // requires a relative path, not merely a contained one; and (b) a rooted path
        // that landed outside was still rejected, but via IsContainedWithin's Ordinal
        // comparison, which reports the confusing "resolves outside the suite directory"
        // message for a path whose only real defect versus the suite directory is
        // drive-letter casing (e.g. declared 'c:\suite\...' against suite directory
        // 'C:\suite\...') — the rooted path's own casing never had anything to do with
        // the suite directory's casing, since Path.Combine had already discarded it.
        // Rejecting every rooted path here closes both, by removing the cause rather than
        // patching either symptom.
        if (Path.IsPathRooted(declaredPath))
        {
            return new ValidationFailure(
                $"{fieldPath}: '{declaredPath}' must be a path relative to the suite directory, " +
                "not an absolute path.")
            {
                IsSecurityPreflight = true,
            };
        }

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(Path.Combine(resolvedSuiteDirectory, declaredPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Fail closed with a clean diagnostic instead of letting a malformed path
            // (e.g. one embedding a NUL) throw out through the unguarded caller: Stage 3a
            // of ScenarioValidator.ValidateScenario calls ProviderPipeline.Compile (which
            // calls this validator) with no try/catch of its own around it.
            return new ValidationFailure($"{fieldPath}: '{declaredPath}' is not a valid path ({ex.Message})")
            {
                IsSecurityPreflight = true,
            };
        }

        // Containment BEFORE existence (REQ-003, EDGE-006): a traversal attempt that
        // happens to point at a real file elsewhere on the host must still fail with the
        // containment error, never a "found"/"not found" one. declaredPath is guaranteed
        // relative at this point (the rooted check above already returned otherwise), so
        // this is a genuine '..'-escape, not the rooted-path case.
        if (!IsContainedWithin(resolvedPath, resolvedSuiteDirectory))
        {
            return new ValidationFailure(
                $"{fieldPath}: '{declaredPath}' resolves outside the suite directory " +
                $"(resolved to '{resolvedPath}', which is not contained within " +
                $"'{resolvedSuiteDirectory}').")
            {
                IsSecurityPreflight = true,
            };
        }

        if (!File.Exists(resolvedPath))
        {
            return new ValidationFailure(
                $"{fieldPath}: file '{declaredPath}' not found (resolved to '{resolvedPath}').")
            {
                IsSecurityPreflight = true,
            };
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="resolvedPath"/> is <paramref name="resolvedSuiteDirectory"/>
    /// itself or a descendant of it. Both arguments must already be fully resolved
    /// (<see cref="Path.GetFullPath(string)"/>) absolute paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="StringComparison.Ordinal"/> comparison, deliberately — NOT
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. The prefix compared against here
    /// is always a byte-for-byte copy of <paramref name="resolvedSuiteDirectory"/> itself
    /// — UNCONDITIONALLY true, not merely true for the paths this method happens to be
    /// called with: <see cref="ValidatePath"/> rejects a ROOTED <c>declaredPath</c>
    /// outright, before <see cref="Path.Combine(string, string)"/> is ever called (see
    /// that method's own remarks), so every <paramref name="resolvedPath"/> that reaches
    /// this comparison was necessarily built by appending a RELATIVE segment to the SAME
    /// <paramref name="resolvedSuiteDirectory"/> this method also receives as its own
    /// compare target. The two can therefore never differ from each other in casing — an
    /// ordinal comparison is exactly as permissive as a case-insensitive one for every
    /// legitimately-contained path. A case-INSENSITIVE comparison, on the other hand,
    /// would wrongly ACCEPT a '..'-escape into a sibling directory that differs from the
    /// suite directory only in case (e.g. a resolved path under '...\suite' against suite
    /// directory '...\Suite') — two DISTINCT directories on the case-sensitive
    /// filesystems CI runs on, which a case-insensitive prefix check cannot tell apart.
    /// (There is no drive-letter-casing concern to trade off against this either: a
    /// rooted, differently-cased-drive-letter path such as <c>'c:\suite\...'</c> against
    /// suite directory <c>'C:\suite\...'</c> never reaches this method at all — it is
    /// rejected by <see cref="ValidatePath"/>'s own rooted-path guard first, which is
    /// precisely the confusing-message case that guard exists to close; see its remarks.)
    /// </para>
    /// <para>
    /// <strong>Not a hardened sandbox boundary:</strong> <see cref="Path.GetFullPath(string)"/>
    /// is a purely LEXICAL normalisation — it does not resolve symlinks or junctions. A
    /// symlink placed inside the suite directory can therefore point outside it,
    /// undetected by this check. This is accepted under the current trust model (the
    /// suite author already controls the suite directory, and <c>script.csharp</c>
    /// already grants that same author arbitrary C#) rather than treated as a hardened
    /// sandbox boundary; revisit if suites are ever sourced from an author less trusted
    /// than whoever controls the suite directory.
    /// </para>
    /// </remarks>
    internal static bool IsContainedWithin(string resolvedPath, string resolvedSuiteDirectory)
    {
        if (string.Equals(resolvedPath, resolvedSuiteDirectory, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = resolvedSuiteDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedSuiteDirectory
            : resolvedSuiteDirectory + Path.DirectorySeparatorChar;

        return resolvedPath.StartsWith(prefix, StringComparison.Ordinal);
    }
}
