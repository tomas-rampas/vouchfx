// Platform.Engine.Authoring — AstBuilder (S03-B-02).
//
// Normalises a parsed E2eDocument into a fully-resolved ScenarioAst:
//   - Resolves dotted or bare-family step types to a StepKindId via the
//     StepKindRegistry, applying the db-assert guard and ambiguity rules.
//   - Parses VerifyMode and Timeout strings into typed values with defaults.
//   - Defaults Capture to an empty dict and ContinueOnFailure to false.
//   - Attaches the YamlMappingNode for downstream provider binders.

using Platform.Engine.Abstractions;
using Platform.Engine.Authoring.Ast;
using Platform.Engine.Authoring.Model;
using Platform.Sdk;

namespace Platform.Engine.Authoring;

/// <summary>
/// Transforms a parsed <see cref="E2eDocument"/> into a fully normalised
/// <see cref="ScenarioAst"/> by resolving step types, applying defaults, and
/// parsing duration and verify-mode strings.
/// </summary>
/// <remarks>
/// <para>
/// The builder is stateless; all state required for a single build pass is
/// passed as parameters.  Call <see cref="Build"/> once per document.
/// </para>
/// <para>
/// Errors are signalled by <see cref="AstBuildException"/>, which carries the
/// step identifier and 1-based source location from the step's YAML node.
/// </para>
/// </remarks>
public static class AstBuilder
{
    // The family name that is permanently forbidden from the bare-alias path,
    // regardless of how many db-assert.* providers are registered.
    private const string DbAssertFamily = "db-assert";

    /// <summary>
    /// Normalises <paramref name="doc"/> into a <see cref="ScenarioAst"/> using
    /// the provider registrations in <paramref name="registry"/>.
    /// </summary>
    /// <param name="doc">
    /// The parsed document produced by <see cref="YamlDocumentParser.Parse"/>.
    /// </param>
    /// <param name="registry">
    /// The frozen registry of known step providers.  Used to resolve
    /// bare-family aliases and to validate dotted step types.
    /// </param>
    /// <returns>
    /// A fully normalised <see cref="ScenarioAst"/> whose steps have resolved
    /// kinds, parsed timeouts, and applied defaults.
    /// </returns>
    /// <exception cref="AstBuildException">
    /// Thrown for any step that has an unknown, ambiguous, or invalid type
    /// string, an unrecognised <c>verifyMode</c> token, or an unparseable
    /// <c>timeout</c> value.
    /// </exception>
    public static ScenarioAst Build(E2eDocument doc, StepKindRegistry registry)
    {
        var variables = doc.Variables
            ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);

        // M3: Track sanitised ids to detect both raw-duplicate and post-sanitisation
        // collisions (e.g. "a-b" and "a_b" both fold to "a_b" via SanitiseId).
        // A HashSet<string> is sufficient because we also store raw→sanitised for
        // the error message.
        var seenSanitised = new Dictionary<string, string>(StringComparer.Ordinal);

        var nodes = new List<StepNode>(doc.Steps.Count);
        foreach (var step in doc.Steps)
        {
            var sanitised = CsxFragment.SanitiseId(step.Id);
            if (seenSanitised.TryGetValue(sanitised, out var collidingRaw))
            {
                var reason = string.Equals(step.Id, collidingRaw, StringComparison.Ordinal)
                    ? $"duplicate step id '{step.Id}'"
                    : $"duplicate step id '{step.Id}' (collides with '{collidingRaw}' after sanitisation)";

                throw new AstBuildException(
                    step.Id,
                    step.RawNode.Start.Line,
                    step.RawNode.Start.Column,
                    reason);
            }

            seenSanitised[sanitised] = step.Id;
            nodes.Add(BuildStep(step, registry));
        }

        return new ScenarioAst(
            doc.Metadata,
            doc.Environment,
            variables,
            nodes);
    }

    // -------------------------------------------------------------------------
    // Per-step normalisation
    // -------------------------------------------------------------------------

    private static StepNode BuildStep(StepSpec step, StepKindRegistry registry)
    {
        var kind = ResolveKind(step, registry);
        var canonicalType = $"{kind.Family}.{kind.Provider}";
        var verifyMode = ResolveVerifyMode(step);
        var timeout = ResolveTimeout(step);
        var capture = step.Capture
            ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);
        var continueOnFailure = step.ContinueOnFailure ?? false;

        return new StepNode(
            step.Id,
            kind,
            canonicalType,
            step.RawNode,
            capture,
            verifyMode,
            timeout,
            continueOnFailure);
    }

    // -------------------------------------------------------------------------
    // Step-type resolution
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves the raw <c>type</c> string from a <see cref="StepSpec"/> to a
    /// validated <see cref="StepKindId"/>.
    /// </summary>
    /// <remarks>
    /// Resolution algorithm (in order):
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///       If <c>type</c> contains a dot, split on the first dot into
    ///       (family, provider); require that the composite key is registered.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Otherwise the value is a bare family alias.  The
    ///       <c>db-assert</c> family is rejected unconditionally before the
    ///       count-based rules so the error message is deterministic.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Zero matching providers → unknown family error.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       Exactly one matching provider → resolved alias.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       More than one matching provider → ambiguous alias error.
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    private static StepKindId ResolveKind(StepSpec step, StepKindRegistry registry)
    {
        var raw = step.Type;
        var line = step.RawNode.Start.Line;
        var col = step.RawNode.Start.Column;

        // ── Dotted form: family.provider ─────────────────────────────────────
        var dotIndex = raw.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex >= 0)
        {
            var family = raw[..dotIndex];
            var provider = raw[(dotIndex + 1)..];

            if (!registry.TryGet(raw, out _))
            {
                throw new AstBuildException(
                    step.Id,
                    line,
                    col,
                    $"unknown step type '{raw}' — no registered provider");
            }

            return new StepKindId(family, provider);
        }

        // ── Bare-family form ─────────────────────────────────────────────────

        // db-assert is permanently excluded from bare-alias resolution.
        if (string.Equals(raw, DbAssertFamily, StringComparison.Ordinal))
        {
            throw new AstBuildException(
                step.Id,
                line,
                col,
                "'db-assert' requires an explicit provider, e.g. db-assert.postgres; it has no default.");
        }

        var matches = registry.All
            .Where(p => string.Equals(p.Kind.Family, raw, StringComparison.Ordinal))
            .ToList();

        return matches.Count switch
        {
            0 => throw new AstBuildException(
                    step.Id,
                    line,
                    col,
                    $"unknown step family '{raw}' — no registered provider"),

            1 => matches[0].Kind,

            _ => throw new AstBuildException(
                    step.Id,
                    line,
                    col,
                    $"ambiguous step family '{raw}'; specify a provider, e.g. {raw}.{matches[0].Kind.Provider}"),
        };
    }

    // -------------------------------------------------------------------------
    // VerifyMode normalisation
    // -------------------------------------------------------------------------

    private static VerifyMode ResolveVerifyMode(StepSpec step)
    {
        if (step.VerifyMode is null)
        {
            return VerifyMode.Immediate;
        }

        // The wire token is the enum member name upper-cased (§2 DSL).
        if (string.Equals(step.VerifyMode, "IMMEDIATE", StringComparison.OrdinalIgnoreCase))
        {
            return VerifyMode.Immediate;
        }

        if (string.Equals(step.VerifyMode, "RETRY", StringComparison.OrdinalIgnoreCase))
        {
            return VerifyMode.Retry;
        }

        throw new AstBuildException(
            step.Id,
            step.RawNode.Start.Line,
            step.RawNode.Start.Column,
            $"unknown verifyMode '{step.VerifyMode}'; valid values are IMMEDIATE and RETRY");
    }

    // -------------------------------------------------------------------------
    // Timeout normalisation
    // -------------------------------------------------------------------------

    private static TimeSpan? ResolveTimeout(StepSpec step)
    {
        if (step.Timeout is null)
        {
            return null;
        }

        if (DurationParser.TryParse(step.Timeout, out var duration))
        {
            return duration;
        }

        throw new AstBuildException(
            step.Id,
            step.RawNode.Start.Line,
            step.RawNode.Start.Column,
            $"unparseable timeout '{step.Timeout}'; accepted formats: <n>ms, <n>s, <n>m, or a bare integer (seconds)");
    }
}
