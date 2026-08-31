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
// Scope note: this validates the host-filesystem shape of declared paths, plus (slice D fix
// round one) the one SERVICE SHAPE that can never be secured — a `project`-form service, which
// the schema accepts and EnvironmentMapper then rejects at topology-build time. It does not
// resolve `profile`/`endpoint` (REQ-002's requiredness is enforced by the JSON Schema layer
// alone), does not probe the endpoint (REQ-005, a later PR), and does not orchestrate the
// actual container-file copy (REQ-016, a later PR).

using Vouchfx.Engine.Abstractions.Secrets;
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
/// Three rules, checked in this fixed order for every DECLARED path (REQ-011 first, then
/// REQ-003/EDGE-006 containment before REQ-004 existence):
/// </para>
/// <list type="number">
///   <item><description>
///   <strong>No secret-reference syntax</strong> — a value containing
///   <see cref="SecretReference.Sigil"/> is refused outright (REQ-011, #387), whether the
///   reference is the whole value or embedded in a longer path. These fields name
///   a host FILE for the engine to read and copy, and the secrets subsystem yields a VALUE,
///   which is not a file and has no path — and resolving no path for such a value is also
///   what stops the refusal echoing a garbled host path back at the author.
///   </description></item>
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
    /// True when any declared service or dependency carries a <c>security</c> block.
    /// </summary>
    /// <param name="environment">The suite's environment declaration, or <see langword="null"/>.</param>
    /// <remarks>
    /// The cheap question "does this suite claim any security at all", asked by callers that must
    /// apply a security-only rule without paying for the full validation walk. A one-line forward
    /// to <see cref="SecuredTargets.Any"/> (m5, fix round three), which is the single spelling of
    /// this walk — kept as a name rather than deleted so this class's own callers and tests keep
    /// reading in this class's idiom.
    /// </remarks>
    internal static bool DeclaresSecurity(EnvironmentSpec? environment) =>
        SecuredTargets.Any(environment);

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
                var shapeFailure = ValidateSecurableShape(spec, name);
                if (shapeFailure is not null)
                {
                    return shapeFailure;
                }

                var failure = ValidateSecurity(
                    spec.Security, SecuredTargets.ServicesFieldSegment, name, resolvedSuiteDirectory);
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
                var failure = ValidateSecurity(
                    spec.Security, SecuredTargets.DependenciesFieldSegment, name, resolvedSuiteDirectory);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Rejects, at VALIDATION time, a <c>security</c> block declared on a service shape this
    /// release cannot secure at all (REQ-023).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>project</c>-form service's endpoints are discovered from the project's own launch
    /// profile, which this engine neither models nor names AT AUTHORING TIME, so there is no
    /// endpoint for REQ-023 to construct with an <c>https</c> scheme. (A <c>svc::&lt;name&gt;</c>
    /// value IS staged for such a service since #348 — read off the resource Aspire builds, inside
    /// the Configure closure — but that can only report which endpoints were discovered, never
    /// make one <c>https</c>.) The JSON Schema accepts the combination: its project-form clause
    /// forbids several image-form-only fields but not <c>security</c> — grep that clause's
    /// <c>then</c> for the current roster rather than trusting a copy here — and
    /// <c>EnvironmentMapper.Map</c> then throws at TOPOLOGY-BUILD time.
    /// </para>
    /// <para>
    /// That is the "validates but can never work" shape, and it is caught here instead: a
    /// suite that passes <c>vouchfx validate</c> and then dies once containers are starting
    /// costs the author a full topology cycle to learn something knowable from the document
    /// alone. <c>EnvironmentMapper</c>'s own throw is deliberately KEPT as a fail-closed
    /// backstop for direct engine embedding that bypasses this stage — the two must agree, and
    /// a suite reaching the mapper with this shape now means validation was skipped, not that
    /// it passed.
    /// </para>
    /// </remarks>
    private static ValidationFailure? ValidateSecurableShape(ServiceSpec spec, string ownerName)
    {
        if (spec.Security is null || spec.Project is null)
        {
            return null;
        }

        return new ValidationFailure(
            $"environment.services.{ownerName}.security: a 'project'-form service cannot be secured in " +
            "this release. A project-form service's endpoints are discovered from its own launch " +
            "profile, so the engine has no endpoint of its own to expose with an 'https' scheme. " +
            $"Declare '{ownerName}' as an 'image'-form service to use 'security', or remove the " +
            "'security' block.")
        {
            IsSecurityPreflight = true,
        };
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

        var claimedTargets = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < security.ServerArtifacts.Count; i++)
        {
            var fieldName = $"serverArtifacts[{i}].source";
            var artifactFailure = ValidatePath(
                security.ServerArtifacts[i].Source, fieldName, ownerKindPlural, ownerName, resolvedSuiteDirectory);
            if (artifactFailure is not null)
            {
                return artifactFailure;
            }

            var targetFailure = ValidateArtifactTarget(
                security.ServerArtifacts[i].Target, i, ownerKindPlural, ownerName, claimedTargets);
            if (targetFailure is not null)
            {
                return targetFailure;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates one <c>serverArtifacts[].target</c>: an absolute in-container path naming a FILE,
    /// declared at most once on this owner (REQ-016).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EnvironmentMapper</c> enforces the same three rules when it builds the copy, and keeps
    /// doing so as a fail-closed backstop for direct engine embedding. The reason they are checked
    /// HERE as well is the exit code, not the diagnostic: a fault raised from the mapper surfaces
    /// as an ordinary <see cref="ArgumentException"/> environment-configuration error, which exits
    /// 0 by default — while these are faults in a declared <c>security</c> block, which REQ-018
    /// requires to exit non-zero with no flag. Reaching them at this stage is what attaches
    /// <see cref="ValidationFailure.IsSecurityPreflight"/> to them. It also means an author learns
    /// at <c>vouchfx validate</c> time rather than after paying for a topology build.
    /// </para>
    /// <para>
    /// Not fully covered by the schema, deliberately checked rather than assumed: the schema's
    /// <c>^/</c> pattern rejects a relative target, but accepts <c>/etc/kafka/secrets/</c> — a
    /// directory, with no file name for the engine to create — and says nothing about two artefacts
    /// claiming one path.
    /// </para>
    /// </remarks>
    private static ValidationFailure? ValidateArtifactTarget(
        string? target, int index, string ownerKindPlural, string ownerName, HashSet<string> claimedTargets)
    {
        var fieldPath = $"environment.{ownerKindPlural}.{ownerName}.security.serverArtifacts[{index}].target";

        if (string.IsNullOrWhiteSpace(target) || target[0] != '/')
        {
            return new ValidationFailure(
                $"{fieldPath}: '{target}' must be an absolute path inside the container, beginning with '/'.")
            {
                IsSecurityPreflight = true,
            };
        }

        // A container path is POSIX; split on '/' by hand rather than via Path.GetDirectoryName,
        // which on Windows would reason about '\' and reach a different answer than the mapper.
        if (target.LastIndexOf('/') == target.Length - 1)
        {
            return new ValidationFailure(
                $"{fieldPath}: '{target}' names a directory, not a file. Give the full in-container "
                + "path of the file to create, e.g. '/etc/kafka/secrets/kafka.keystore.jks'.")
            {
                IsSecurityPreflight = true,
            };
        }

        // The shapes a POSIX container path cannot mean. Checked HERE as well as in the mapper's
        // own ServerArtifactInjection.Plan, and for this stage's own reason: the author learns at
        // `vouchfx validate` time, with the field named, instead of at topology-build time as an
        // opaque daemon failure. No boundary is crossed either way — the destination is inside the
        // author's own container — but measured, '/etc/kafka/..' produces a container file literally
        // NAMED '..' and '/etc/kafka\secrets\ks.jks' one named 'kafka\secrets\ks.jks', and neither
        // diagnoses itself. The two stages must agree, which is why both carry the same three rules.
        if (target.Contains('\\', StringComparison.Ordinal))
        {
            return new ValidationFailure(
                $"{fieldPath}: '{target}' contains a backslash. A container path is POSIX: separate "
                + "its segments with '/', or the whole run of backslashes becomes part of one file "
                + "NAME rather than a directory path.")
            {
                IsSecurityPreflight = true,
            };
        }

        if (HasDotSegment(target))
        {
            return new ValidationFailure(
                $"{fieldPath}: '{target}' contains a '.' or '..' segment. Give the already-resolved "
                + "in-container path of the file to create, e.g. "
                + "'/etc/kafka/secrets/kafka.keystore.jks' - this engine does not normalise a "
                + "container path, so a '..' segment would be copied through as a literal file name.")
            {
                IsSecurityPreflight = true,
            };
        }

        if (target.Contains("//", StringComparison.Ordinal))
        {
            return new ValidationFailure(
                $"{fieldPath}: '{target}' contains an empty path segment ('//').")
            {
                IsSecurityPreflight = true,
            };
        }

        if (!claimedTargets.Add(target))
        {
            return new ValidationFailure(
                $"{fieldPath}: '{target}' is declared more than once on '{ownerName}'. Two artefacts "
                + "cannot land on one in-container path - which one wins is not something this engine "
                + "will decide silently.")
            {
                IsSecurityPreflight = true,
            };
        }

        return null;
    }

    /// <summary>
    /// True when any '/'-delimited segment of a container path is <c>.</c> or <c>..</c>.
    /// </summary>
    /// <remarks>
    /// Per SEGMENT, never a plain substring test: <c>Contains("..")</c> would also reject the
    /// perfectly ordinary <c>/etc/kafka/secrets/keystore..jks</c>. Mirrors
    /// <c>ServerArtifactInjection.HasDotSegment</c> exactly — the two stages must reach the same
    /// answer, and this rule is four lines, so a shared home would cost more than it saved.
    /// </remarks>
    private static bool HasDotSegment(string target)
    {
        foreach (var segment in target.Split('/'))
        {
            if (segment is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates a single declared path-valued field: the secret-reference refusal first
    /// (REQ-011, #387), then containment (REQ-003, EDGE-006), then existence (REQ-004).
    /// Returns <see langword="null"/> without performing any check when
    /// <paramref name="declaredPath"/> is <see langword="null"/> — i.e. absent (REQ-004(b) —
    /// an undeclared optional field is absent, not missing).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This one method is the chokepoint every HOST-path-valued security field flows through —
    /// exactly the four REQ-011 names: <c>caCert</c>, <c>clientCert</c>, <c>clientKey</c> and
    /// every <c>serverArtifacts[].source</c> — which is why REQ-011's refusal is written here
    /// once rather than four times at the call sites. <c>SecuredTargets</c>' own header records
    /// what the alternative costs: one security predicate had grown three spellings in three
    /// assemblies, each asserting in prose that it agreed with the others.
    /// </para>
    /// <para>
    /// <strong>Not every path-valued field in the block.</strong>
    /// <c>serverArtifacts[].target</c> is also path-valued but is a CONTAINER path, validated by
    /// <see cref="ValidateArtifactTarget"/> against POSIX rules and never resolved against the
    /// host filesystem — so it does not reach here and carries no sigil check. REQ-011 scopes
    /// itself to the four host paths, so that is conformant rather than an omission; the gap is
    /// filed as issue #397, not closed here.
    /// </para>
    /// </remarks>
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

        // REQ-011 (#387): a `${secret:}` reference in a PATH-valued field is refused outright,
        // FIRST — before the blank, rooted, containment and existence checks below.
        //
        // Measured behaviour before this rule, with CLIENT_KEY genuinely set in the environment
        // so a working resolver would have succeeded:
        //
        //   environment.dependencies.broker.security.clientKey: file '${secret:env/CLIENT_KEY}'
        //     not found (resolved to '...\probe\${secret:env\CLIENT_KEY}').
        //
        // That message blamed the FILESYSTEM for a misuse of reference syntax, and this check
        // going first is what removes the wrong diagnosis: no path is resolved for such a value
        // at all. (The echoed resolved path in the quotation above is history — the containment
        // and existence messages below no longer carry one; see their own note.)
        //
        // The ordering against the ROOTED check below is OBSERVABLE, not merely nominal, and the
        // colliding input is reachable from YAML: `clientKey: "/etc/${secret:env/CLIENT_KEY}"` is
        // both rooted and sigil-bearing, and this check is what makes the author see the sigil
        // diagnosis rather than "must be a path relative to the suite directory" — which would
        // send them to fix the wrong thing. (An earlier form of this comment claimed no collision
        // could exist because Path.IsPathRooted is false for a reference token. That is true of a
        // BARE token and false of the mixed value above, so it stated a property of the wrong
        // input class. Pinned by
        // EnvironmentSecurityValidatorTests.Validate_RootedPathContainingASecretReference_….)
        //
        // SecretReference.Sigil, never a respelt "${secret:" literal: the sigil is the secrets
        // subsystem's own constant and this is a second CONSUMER of it, not a second definition.
        //
        // The declared value is echoed verbatim, and the licence for that is §17 plus a MEASURED
        // property of this surface, not consistency with the four messages below. The property,
        // stated rather than counted, and NOT illustrated by a partial list either — an earlier
        // form said "two" of a set that is four, and its replacement then named two of the four as
        // examples inside this very sentence, which is the same habit wearing a different hat:
        // EVERY write of a ValidationFailure.Message to a terminal wraps it in
        // DisplaySanitiser.SanitiseForDisplay, and `--json` is System.Text.Json-escaped rather
        // than sanitised. Grep `Failure.Message` for the writes — that is the enumeration, and it
        // is always current (grepping the sanitiser instead returns 30+ unrelated call sites and
        // answers a different question).
        // Do not
        // re-derive the licence from "the siblings already do it": ANSI/ESC does NOT in fact
        // reach those messages (DocumentValidator's YAML→JSON bridge emits \e/\xNN, which STJ
        // rejects, so such a document dies at the SCHEMA stage — only BS and CR get this far), so
        // that argument was right about the conclusion and wrong about the reason, and it would
        // license a sixth raw concatenation here the day someone removes the sanitiser.
        if (declaredPath.Contains(SecretReference.Sigil, StringComparison.Ordinal))
        {
            // The clientKey clause is field-specific, deliberately: an author reaching for
            // `${secret:}` on THIS field is almost certainly trying to avoid a plaintext private
            // key at rest, and `clientKeyPassword` is the supported way to achieve exactly that.
            // Offering it on caCert or clientCert would be noise — neither is secret material.
            var remedy = fieldName == "clientKey"
                ? " To keep the key from sitting in plaintext at rest, encrypt the key FILE and "
                  + "declare its passphrase in 'clientKeyPassword', which does take a "
                  + "'${secret:<source>/<path>}' reference."
                : string.Empty;

            // The reason is SCOPING, not timing. An earlier form of this message said a reference
            // "cannot be resolved here: this material is loaded before any step runs, which is
            // when secret references resolve" — and then recommended `clientKeyPassword`, which is
            // resolved at that SAME pre-step moment (SecurityConfigurationAccessor's Lazy is first
            // touched by the REQ-005 probe) and works. A message that refuses X on a ground its own
            // remedy also stands on teaches the author nothing. #387's own "Fix" section and
            // REQ-011 both inherited that imprecision; the true reason is that these fields name a
            // host FILE for the engine to read and copy, and the secrets subsystem yields VALUES,
            // not files — there is no file for a resolved secret to be.
            //
            // THE OPENING CLAUSE IS A CONTAINMENT CLAIM BECAUSE THE GUARD IS A CONTAINMENT TEST.
            // It used to open "'{declaredPath}' is a secret reference", which asserts more than
            // `Contains` established: it is true of a value that IS one whole reference and FALSE
            // of `/etc/${secret:env/CLIENT_KEY}` or `./certs/${secret:env/NAME}.pem` — paths that
            // merely CARRY one, are reachable from ordinary YAML, and are caught by this very
            // line (the first is already an input above, in the rooted-ordering test). "Uses
            // secret-reference syntax" holds for every value the guard catches, including the
            // bare token, since a value that is a reference also uses the syntax; and the sigil
            // is NAMED so the author can see exactly which characters were found, which is what
            // makes the claim checkable against their own text rather than a verdict about it.
            // Interpolated from SecretReference.Sigil for the same reason the guard reads it
            // there — one definition of the grammar, not a second spelling in a message.
            //
            // The "whole or embedded" clause is not padding: it is the half the retracted wording
            // got wrong, and it tells the mixed-value author that trimming the path around the
            // token will not help.
            return new ValidationFailure(
                $"{fieldPath}: '{declaredPath}' uses secret-reference syntax "
                + $"('{SecretReference.Sigil}'), but this field takes a PATH - a file that must "
                + "exist inside the suite directory. The syntax is refused wherever it appears in "
                + "the value, whole or embedded in a longer path, because a secret reference "
                + "cannot name a file: the engine reads and copies the FILE this field points at, "
                + "while the secrets subsystem yields a VALUE, which is not a file and has no path."
                + remedy)
            {
                IsSecurityPreflight = true,
            };
        }

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
        // NO RESOLVED PATH, AND NO RESOLVED SUITE DIRECTORY, IN EITHER MESSAGE BELOW (#357's
        // rule, extended here). Both are absolute host paths. A ValidationFailure.Message from
        // this validator becomes ProviderPipeline's Failure.Message, then ScenarioRunner's
        // EarlyMessage, then ScenarioCompletedEvent.message — the §14 stream, the JUnit `message`
        // attribute and the HTML report, all of which are archived and uploaded. Nothing can
        // redact them there: ScenarioRunner.ScrubDiagnostic is ResolvedSecrets.Scrub, a targeted
        // net over values the run's SecretAccessor actually revealed, and a filesystem path is
        // never one of those.
        //
        // The declared text is the author's own input and stays. Naming the CONCEPT the path
        // resolves against — "the suite directory" — keeps a relative path diagnosable without
        // disclosing where the suite sits on the host, which is the same shape ScriptCsharpProvider
        // uses for `script.csharp: file '…' not found, relative to the suite directory.`
        if (!IsContainedWithin(resolvedPath, resolvedSuiteDirectory))
        {
            return new ValidationFailure(
                $"{fieldPath}: '{declaredPath}' resolves outside the suite directory.")
            {
                IsSecurityPreflight = true,
            };
        }

        if (!File.Exists(resolvedPath))
        {
            return new ValidationFailure(
                $"{fieldPath}: file '{declaredPath}' not found, relative to the suite directory.")
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
    /// <strong>Slice E note.</strong> The rule itself now lives in
    /// <see cref="SecurityArtifactPath.IsContainedWithin"/> (<c>Vouchfx.Engine.Authoring</c>) and
    /// this member forwards to it. A third consumer arrived — <c>EnvironmentMapper</c> resolves
    /// every <c>serverArtifacts[].source</c> for REQ-016 — and <c>Vouchfx.Engine.Orchestration</c>
    /// cannot reference <c>Vouchfx.Engine.Runtime</c>, so the choice was a second copy of this
    /// predicate or one shared home. This name is kept because
    /// <c>SecurityConfigurationAccessor</c> and this validator's own tests both reach it, and
    /// because the reasoning below is what a reader looking for the rule will search for.
    /// </para>
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
    internal static bool IsContainedWithin(string resolvedPath, string resolvedSuiteDirectory) =>
        SecurityArtifactPath.IsContainedWithin(resolvedPath, resolvedSuiteDirectory);
}
