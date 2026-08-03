// S03-B-03 — Pre-compilation schema validation pass with line context (§8, §13.6).
//
// This file is the pre-compile gate that an author reaches before any Roslyn
// compilation is attempted.  It delegates to SchemaComposer.Validate so that
// the unified composed schema (root language schema + every registered
// provider's if/then fragment) is evaluated, then enriches each error's
// message with a best-effort YAML line number.
//
// Line resolution is best-effort: the JSON Pointer in InstanceLocation is split
// and walked against a YamlDotNet RepresentationModel parse of the same
// document.  When a pointer segment cannot be resolved (e.g. the document is
// unparseable, the pointer targets the root, or a numeric index is out of
// range) the prefix is omitted rather than throwing.
using System.Linq;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// Pre-compilation validation gate: validates a <c>.e2e.yaml</c> document
/// against the unified composed JSON Schema (root-language schema with every
/// registered provider's fragment) and enriches each error with a resolved
/// YAML line number.
/// </summary>
/// <remarks>
/// <para>
/// This class is a thin wrapper around <see cref="SchemaComposer.Validate"/>.
/// It does not duplicate schema-composition or YAML-to-JSON-conversion logic;
/// those concerns remain in <see cref="SchemaComposer"/> and
/// <see cref="SchemaResources"/> respectively.
/// </para>
/// <para>
/// <strong>Line resolution is best-effort.</strong>  The
/// <see cref="SchemaValidationError.InstanceLocation"/> field carries a JSON
/// Pointer (RFC 6901) that was produced from the JSON-serialised representation
/// of the YAML document.  Mapping that pointer back to a line number requires
/// walking the original YAML RepresentationModel.  When any segment of the walk
/// fails — unparseable YAML, root-level pointer, out-of-range index, absent
/// mapping key — the <c>(line N)</c> prefix is omitted and the raw schema
/// message is preserved unchanged.
/// </para>
/// <para>
/// <see cref="SchemaValidationError.InstanceLocation"/> is never modified;
/// only <see cref="SchemaValidationError.Message"/> gains the prefix.
/// </para>
/// <para>
/// <b>Unknown step types (#265).</b> <c>SchemaComposer</c>'s composed schema
/// injects one unconditional <c>if</c>/<c>then</c> clause per registered
/// provider into <c>$defs/step/allOf</c> — there is no <c>else</c> and no
/// closed enumeration of known <c>type</c> values. A step whose <c>type</c>
/// matches none of the registered clauses therefore satisfies every clause
/// VACUOUSLY (per JSON Schema 2020-12 <c>if</c>/<c>then</c> semantics) and
/// passes raw schema evaluation with zero errors, even though it can never
/// bind to a provider. This class closes that gap with a POST-SCHEMA
/// cross-check against the <see cref="StepKindRegistry"/> supplied to
/// <see cref="Validate"/> — deliberately kept out of the composed schema
/// itself (which is a frozen, golden-snapshotted v1 artifact; see
/// <c>SchemaFreezeTests</c>) — mirroring how the vouchfx-mcp
/// server's <c>SuiteValidator</c>/<c>StepTypeCatalogue</c> solved the
/// identical gap against its own (vendored, read-only) copy of the schema.
/// The cross-check defers entirely to the schema for a <c>type</c> that is
/// already malformed (fails the root schema's own
/// <c>^[a-z0-9-]+\.[a-z0-9-]+$</c> pattern, e.g. a bare family name like
/// <c>http</c> or a wrongly-cased <c>HTTP.REST</c>): it never emits a second
/// error at an instance location the schema pass already flagged, so a
/// pattern-invalid value is reported exactly once, by the schema, not twice.
/// </para>
/// <para>
/// <b>Unevaluated-properties cascade on an unregistered type.</b> A step
/// whose <c>type</c> matches no registered provider never has any if/then
/// fragment run for it (the vacuous-satisfaction case above), so — same as
/// any other step failing for an unrelated reason, see
/// <c>SchemaErrorCollector</c>'s class remarks — NONE of its other fields
/// are ever claimed by a fragment's <c>properties</c> annotation, and each
/// comes back a spurious <c>unevaluatedProperties</c> "unknown property"
/// finding. <see cref="SchemaErrorCollector"/> cannot suppress this itself:
/// "the type is unregistered" is a <see cref="StepKindRegistry"/> lookup, not
/// a JSON Schema constraint, so it is invisible to a class that only ever
/// sees an <c>EvaluationResults</c> tree. This method is where both facts
/// are available at once, so it drops those raw schema errors for any step
/// the cross-check above already flags — leaving the ONE genuine,
/// actionable finding ("unknown step type") instead of that plus one spurious
/// "unknown property" per other field on the step.
/// </para>
/// </remarks>
public static class DocumentValidator
{
    /// <summary>
    /// Validates <paramref name="yamlText"/> against the unified composed JSON
    /// Schema built from <paramref name="registry"/> and enriches each error
    /// message with a <c>(line N)</c> prefix resolved from the original YAML
    /// source.
    /// </summary>
    /// <param name="yamlText">
    /// The raw contents of a <c>.e2e.yaml</c> file.
    /// </param>
    /// <param name="registry">
    /// The frozen provider registry whose JSON Schema fragments are included
    /// in the composed schema.  Passing an empty registry validates against
    /// the root-language schema only (no provider-specific constraints).
    /// </param>
    /// <param name="securityProfileRegistry">
    /// The security-profile registry <see cref="CollectUnknownSecurityProfileErrors"/> checks
    /// a declared <c>security.profile</c> against (REQ-019). Defaults to
    /// <see cref="SecurityProfileRegistry.BuiltIn"/> when omitted — the production registry.
    /// Injectable (G-MINOR-8, gatekeeper) so a caller can prove the registry cross-check's own
    /// behaviour end-to-end through this front door with a reduced/explicit registry, mirroring
    /// how <paramref name="registry"/> itself is always a parameter rather than a hardcoded
    /// <c>StepKindRegistry</c> default.
    /// </param>
    /// <returns>
    /// A <see cref="SchemaValidationResult"/> whose errors have been enriched
    /// with <c>(line N)</c> prefixes where the pointer-to-line resolution
    /// succeeded.
    /// </returns>
    public static SchemaValidationResult Validate(
        string yamlText, StepKindRegistry registry, SecurityProfileRegistry? securityProfileRegistry = null)
    {
        var resolvedSecurityProfileRegistry = securityProfileRegistry ?? SecurityProfileRegistry.BuiltIn;

        // Delegate to the composed-schema path; this is the authoritative
        // schema validation (root schema + provider fragments).
        var raw = SchemaComposer.Validate(registry, yamlText);

        // Parse the YAML once into the RepresentationModel.  This single parse
        // serves two purposes: resolving JSON-Pointer segments back to concrete
        // line numbers (below), and walking the `steps` sequence for the
        // unknown-step-type cross-check (#265, see the class remarks).  If
        // parsing fails (the document was already rejected as malformed) both
        // concerns degrade gracefully: no line prefixes, no unknown-type
        // detection — there is no YAML tree left to walk.
        YamlMappingNode? rootMapping = TryParseYamlRoot(yamlText);

        // Run the unknown-step-type cross-check UNCONDITIONALLY — not only when
        // the schema pass already failed — because a document whose ONLY defect
        // is an unregistered step type is exactly the case the composed schema
        // cannot detect on its own (it is vacuously schema-valid).
        var unknownTypeErrors = rootMapping is not null
            ? CollectUnknownStepTypeErrors(rootMapping, registry, raw.Errors)
            : Array.Empty<SchemaValidationError>();

        // REQ-019: 'security.profile' is an open string pattern in the composed schema
        // (no 'enum') — an unrecognised profile name is rejected here, at validation
        // time, against the engine's security-profile registry, mirroring the identical
        // division CollectUnknownStepTypeErrors already applies to a step's own 'type'.
        // Also run UNCONDITIONALLY, for the same reason: a document whose only defect is
        // an unregistered profile name is vacuously schema-valid (the pattern matches
        // any lower-case dotted token).
        var unknownProfileErrors = rootMapping is not null
            ? CollectUnknownSecurityProfileErrors(rootMapping, raw.Errors, resolvedSecurityProfileRegistry)
            : Array.Empty<SchemaValidationError>();

        if ((raw.IsValid || raw.Errors.Count == 0) &&
            unknownTypeErrors.Count == 0 &&
            unknownProfileErrors.Count == 0)
        {
            return raw;
        }

        // A step whose type matched no registered provider gets exactly the
        // same spurious-unevaluatedProperties cascade SchemaErrorCollector
        // suppresses for schema-detected defects (§ its class remarks) — no
        // if/then fragment ever ran for it, so NONE of its other fields were
        // ever claimed, and every one of them comes back "unevaluated". That
        // defect is a registry lookup, not a JSON Schema constraint, so it is
        // invisible to SchemaErrorCollector; this is the one place that knows
        // both which step failed the cross-check AND which raw schema errors
        // are the unevaluatedProperties shape, so it is suppressed here
        // instead — same rationale, same "one real defect per round" trade,
        // applied at the layer that actually has both facts in hand.
        var schemaErrors = raw.Errors;
        if (unknownTypeErrors.Count > 0)
        {
            var unknownTypeSteps = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unknownTypeError in unknownTypeErrors)
            {
                if (SchemaErrorCollector.TryGetStepScope(unknownTypeError.InstanceLocation, out var scope))
                    unknownTypeSteps.Add(scope);
            }

            schemaErrors = raw.Errors
                .Where(e => !(SchemaErrorCollector.IsUnevaluatedPropertiesMessage(e.Message) &&
                              SchemaErrorCollector.TryGetStepScope(e.InstanceLocation, out var scope) &&
                              unknownTypeSteps.Contains(scope)))
                .ToList();
        }

        // Aggregate every (post-suppression) schema error, every unknown-step-type
        // error, AND every unknown-security-profile error into one result: neither
        // cross-check may mask or replace a genuine schema violation (on the same
        // step/security block or a different one), and a real schema violation must
        // never suppress either cross-check's own finding. Schema errors are listed
        // first, unknown-type errors next, unknown-profile errors last, so existing
        // consumers that pick the FIRST matching error for a given instance location
        // keep seeing the schema violation there.
        var combined = new SchemaValidationError[
            schemaErrors.Count + unknownTypeErrors.Count + unknownProfileErrors.Count];
        for (var i = 0; i < schemaErrors.Count; i++)
            combined[i] = schemaErrors[i];
        for (var i = 0; i < unknownTypeErrors.Count; i++)
            combined[schemaErrors.Count + i] = unknownTypeErrors[i];
        for (var i = 0; i < unknownProfileErrors.Count; i++)
            combined[schemaErrors.Count + unknownTypeErrors.Count + i] = unknownProfileErrors[i];

        var enriched = new SchemaValidationError[combined.Length];
        for (var i = 0; i < combined.Length; i++)
        {
            var error = combined[i];
            var line = rootMapping is not null
                ? ResolveLineFromPointer(rootMapping, error.InstanceLocation)
                : null;

            var message = line.HasValue
                ? $"(line {line.Value}) {error.Message}"
                : error.Message;

            enriched[i] = new SchemaValidationError(error.InstanceLocation, message);
        }

        return new SchemaValidationResult(false, enriched);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Walks the document's <c>steps</c> sequence and flags any step whose
    /// <c>type</c> is a well-formed YAML scalar that does not match a
    /// registered provider key in <paramref name="registry"/> — UNLESS the
    /// schema pass already reported an error at that same
    /// <c>/steps/&lt;i&gt;/type</c> instance location, in which case this
    /// method defers to it and reports nothing there (#265).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A step missing <c>type</c> entirely, or whose <c>type</c> is not a
    /// scalar (a mapping or sequence), is already caught by the root schema's
    /// own <c>required</c>/<c>type</c> constraints on <c>$defs/step</c> — this
    /// method only inspects steps with a genuinely well-formed scalar
    /// <c>type</c> value, so it never double-reports those cases.
    /// </para>
    /// <para>
    /// A well-formed scalar <c>type</c> can still violate the root schema's
    /// own <c>^[a-z0-9-]+\.[a-z0-9-]+$</c> pattern — e.g. a bare family name
    /// like <c>http</c> (a realistic authoring mistake: bare family aliases
    /// were retired pre-v1.0 and migrating authors still write them), or a
    /// wrongly-cased <c>HTTP.REST</c>. The schema pass already reports a
    /// <c>[pattern]</c> error for that value at this exact instance location.
    /// Rather than re-deriving that same regex here, this method checks
    /// <paramref name="schemaErrors"/> for an entry at the pointer it is about
    /// to use and, if one exists, skips emitting an unknown-type error there —
    /// the two checks partition cleanly instead of double-reporting the same
    /// defect.
    /// </para>
    /// <para>
    /// When the document has no <c>steps</c> key, <c>steps</c> is not a
    /// sequence, or a given step is not a mapping, this method silently skips
    /// it: those shapes are themselves root-schema violations, not this
    /// cross-check's concern.
    /// </para>
    /// </remarks>
    /// <param name="root">The root mapping node of the YAML document.</param>
    /// <param name="registry">
    /// The frozen provider registry to check each step's <c>type</c> against.
    /// </param>
    /// <param name="schemaErrors">
    /// The raw (pre-enrichment) schema-validation errors already produced for
    /// this document by <see cref="SchemaComposer.Validate"/>. Consulted only
    /// to suppress an unknown-type finding at an instance location the schema
    /// pass already flagged (see remarks); never otherwise inspected.
    /// </param>
    /// <returns>
    /// One <see cref="SchemaValidationError"/> per step with an unrecognised
    /// <c>type</c> that the schema pass did NOT already flag at the same
    /// pointer, in document order, pointing at <c>/steps/&lt;i&gt;/type</c>;
    /// empty when every step's <c>type</c> is either registered, not a
    /// well-formed scalar, or already reported by the schema.
    /// </returns>
    private static IReadOnlyList<SchemaValidationError> CollectUnknownStepTypeErrors(
        YamlMappingNode root,
        StepKindRegistry registry,
        IReadOnlyList<SchemaValidationError> schemaErrors)
    {
        var stepsKey = new YamlScalarNode("steps");
        if (!root.Children.TryGetValue(stepsKey, out var stepsNode) ||
            stepsNode is not YamlSequenceNode stepsSequence)
        {
            return Array.Empty<SchemaValidationError>();
        }

        // Instance locations the schema pass already flagged, so this
        // cross-check never emits a second error at a pointer the schema
        // itself covers (see remarks — e.g. a pattern-invalid `type`).
        var schemaErrorLocations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schemaError in schemaErrors)
            schemaErrorLocations.Add(schemaError.InstanceLocation);

        List<SchemaValidationError>? errors = null;
        var typeKey = new YamlScalarNode("type");

        for (var i = 0; i < stepsSequence.Children.Count; i++)
        {
            if (stepsSequence.Children[i] is not YamlMappingNode stepMapping)
                continue;

            if (!stepMapping.Children.TryGetValue(typeKey, out var typeNode) ||
                typeNode is not YamlScalarNode typeScalar)
            {
                // Missing or non-scalar `type` is already a root-schema
                // violation (required/type constraints) — do not double-report.
                continue;
            }

            var typeValue = typeScalar.Value;
            if (string.IsNullOrEmpty(typeValue) || registry.TryGet(typeValue, out _))
                continue;

            var pointer = $"/steps/{i}/type";

            // Defer to the schema: a `type` that violates the root schema's
            // own pattern already has a [pattern] error at this exact
            // pointer — do not duplicate it with an unknown-type finding.
            if (schemaErrorLocations.Contains(pointer))
                continue;

            errors ??= new List<SchemaValidationError>();
            errors.Add(new SchemaValidationError(
                pointer,
                $"unknown step type '{typeValue}' — not a registered provider " +
                "(expected <family>.<provider>, e.g. 'db-assert.postgres')."));
        }

        return errors is null ? Array.Empty<SchemaValidationError>() : errors;
    }

    /// <summary>
    /// Walks <c>environment.services</c> and <c>environment.dependencies</c> for a declared
    /// <c>security.profile</c> scalar that is a well-formed value but not registered in
    /// <see cref="SecurityProfileRegistry.BuiltIn"/> — REQ-019: <c>profile</c> is an open
    /// string pattern in the composed schema (no <c>enum</c>), and an unrecognised name is
    /// rejected here, at validation time, against the frozen registry, mirroring exactly the
    /// division <see cref="CollectUnknownStepTypeErrors"/> already applies to a step's own
    /// <c>type</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>profile</c> value that already violates the root schema's own
    /// <c>^[a-z0-9-]+(\.[a-z0-9-]+)?$</c> pattern (e.g. a wrong-cased <c>TLS</c>) already has
    /// a <c>[pattern]</c> error at this exact pointer — this method defers to it and reports
    /// nothing there, mirroring <see cref="CollectUnknownStepTypeErrors"/>'s identical
    /// deferral for a pattern-invalid step <c>type</c>, so the two checks partition cleanly
    /// instead of double-reporting the same defect (the mirror image of issue #265, which
    /// moved a check the OTHER way — schema to registry lookup — to avoid exactly that).
    /// </para>
    /// <para>
    /// No unevaluated-properties cascade suppression is needed here, unlike
    /// <see cref="CollectUnknownStepTypeErrors"/>'s own step-scoped one: an unregistered
    /// <c>profile</c> VALUE does not stop <c>$defs/security</c>'s own <c>properties.profile</c>
    /// declaration from claiming the field as evaluated — only an unregistered step
    /// <c>type</c> leaves an ENTIRE step's fields unclaimed by any provider fragment.
    /// </para>
    /// </remarks>
    /// <param name="root">The root mapping node of the YAML document.</param>
    /// <param name="schemaErrors">
    /// The raw (pre-enrichment) schema-validation errors already produced for this document.
    /// Consulted only to suppress a finding at an instance location the schema pass already
    /// flagged (see remarks); never otherwise inspected.
    /// </param>
    /// <param name="registry">
    /// The security-profile registry to check each declared <c>profile</c> against —
    /// <see cref="SecurityProfileRegistry.BuiltIn"/> in production; a caller-supplied registry
    /// in a test proving this cross-check's own behaviour explicitly (G-MINOR-8).
    /// </param>
    /// <returns>
    /// One <see cref="SchemaValidationError"/> per declared <c>security.profile</c> with an
    /// unrecognised value that the schema pass did NOT already flag at the same pointer, in
    /// document order (services before dependencies, then map-iteration order within each);
    /// empty when every declared profile is either registered, not a well-formed scalar, or
    /// already reported by the schema.
    /// </returns>
    private static IReadOnlyList<SchemaValidationError> CollectUnknownSecurityProfileErrors(
        YamlMappingNode root, IReadOnlyList<SchemaValidationError> schemaErrors, SecurityProfileRegistry registry)
    {
        var environmentKey = new YamlScalarNode("environment");
        if (!root.Children.TryGetValue(environmentKey, out var environmentNode) ||
            environmentNode is not YamlMappingNode environmentMapping)
        {
            return Array.Empty<SchemaValidationError>();
        }

        var schemaErrorLocations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schemaError in schemaErrors)
            schemaErrorLocations.Add(schemaError.InstanceLocation);

        List<SchemaValidationError>? errors = null;

        foreach (var ownerSectionKey in new[] { "services", "dependencies" })
        {
            var ownerSectionNode = new YamlScalarNode(ownerSectionKey);
            if (!environmentMapping.Children.TryGetValue(ownerSectionNode, out var sectionNode) ||
                sectionNode is not YamlMappingNode sectionMapping)
            {
                continue;
            }

            foreach (var (ownerKeyNode, ownerValueNode) in sectionMapping.Children)
            {
                if (ownerKeyNode is not YamlScalarNode ownerKeyScalar ||
                    ownerValueNode is not YamlMappingNode ownerMapping)
                {
                    // Not a well-formed mapping entry — the root schema's own
                    // constraints already cover this shape; nothing for this
                    // cross-check to add.
                    continue;
                }

                if (!ownerMapping.Children.TryGetValue(new YamlScalarNode("security"), out var securityNode) ||
                    securityNode is not YamlMappingNode securityMapping)
                {
                    continue;
                }

                if (!securityMapping.Children.TryGetValue(new YamlScalarNode("profile"), out var profileNode) ||
                    profileNode is not YamlScalarNode profileScalar)
                {
                    // Missing or non-scalar `profile` is already a root-schema
                    // violation (required/type constraints) — do not double-report.
                    continue;
                }

                var profileValue = profileScalar.Value;
                if (string.IsNullOrEmpty(profileValue) ||
                    registry.TryGet(profileValue, out _))
                {
                    continue;
                }

                var pointer = $"/environment/{ownerSectionKey}/{ownerKeyScalar.Value}/security/profile";

                // Defer to the schema: a `profile` that violates the root schema's
                // own pattern already has a [pattern] error at this exact pointer —
                // do not duplicate it with an unknown-profile finding.
                if (schemaErrorLocations.Contains(pointer))
                {
                    continue;
                }

                errors ??= new List<SchemaValidationError>();
                errors.Add(new SchemaValidationError(pointer, registry.DescribeUnknownProfile(profileValue)));
            }
        }

        return errors is null ? Array.Empty<SchemaValidationError>() : errors;
    }

    /// <summary>
    /// Attempts to parse <paramref name="yamlText"/> into a
    /// <see cref="YamlMappingNode"/> using the YamlDotNet
    /// <see cref="YamlStream"/> RepresentationModel.
    /// Returns <see langword="null"/> when the YAML cannot be parsed or the
    /// root node is not a mapping (e.g. a bare scalar or sequence).
    /// </summary>
    private static YamlMappingNode? TryParseYamlRoot(string yamlText)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new System.IO.StringReader(yamlText);
            stream.Load(reader);

            if (stream.Documents.Count == 0)
                return null;

            return stream.Documents[0].RootNode as YamlMappingNode;
        }
        catch
        {
            // Any YamlDotNet parse exception means we cannot resolve lines;
            // return null so the caller skips enrichment gracefully.
            return null;
        }
    }

    /// <summary>
    /// Resolves a JSON Pointer (RFC 6901) to a YAML source line number by
    /// walking the <see cref="YamlMappingNode"/> RepresentationModel tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pointer is split on <c>/</c>; the leading empty segment (before the
    /// first slash) is discarded.  Each remaining segment is interpreted as
    /// either a mapping key (string) or a sequence index (integer).  The walk
    /// stops at the deepest node that can be reached; if any segment fails to
    /// match, <see langword="null"/> is returned.
    /// </para>
    /// <para>
    /// Per RFC 6901, <c>~1</c> decodes to <c>/</c> and <c>~0</c> decodes to
    /// <c>~</c>.  These are decoded before key lookup.
    /// </para>
    /// <para>
    /// An empty pointer (<c>""</c>) or a pointer consisting solely of <c>/</c>
    /// references the root node; no prefix is emitted for root-level errors
    /// because there is no single meaningful line for the whole document.
    /// </para>
    /// </remarks>
    /// <param name="root">The root mapping node of the YAML document.</param>
    /// <param name="pointer">A JSON Pointer string such as <c>/steps/0/method</c>.</param>
    /// <returns>
    /// The 1-based source line number of the resolved node, or
    /// <see langword="null"/> when the pointer cannot be resolved.
    /// </returns>
    private static long? ResolveLineFromPointer(YamlMappingNode root, string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
            return null;

        // Split the pointer, discarding the empty leading segment produced by
        // the leading slash.
        var segments = pointer.Split('/');

        // segments[0] is always "" (from the leading slash); walk from index 1.
        YamlNode current = root;
        for (var i = 1; i < segments.Length; i++)
        {
            var seg = DecodePointerSegment(segments[i]);

            if (current is YamlMappingNode mapping)
            {
                var key = new YamlScalarNode(seg);
                if (!mapping.Children.TryGetValue(key, out var child))
                    return null;

                current = child;
            }
            else if (current is YamlSequenceNode sequence)
            {
                if (!int.TryParse(seg, out var index) ||
                    index < 0 ||
                    index >= sequence.Children.Count)
                {
                    return null;
                }

                current = sequence.Children[index];
            }
            else
            {
                // We hit a scalar or alias before exhausting all segments.
                return null;
            }
        }

        // YamlDotNet uses 1-based line numbers in Mark.Line.
        return current.Start.Line;
    }

    /// <summary>
    /// Decodes a single JSON Pointer segment per RFC 6901:
    /// <c>~1</c> → <c>/</c>, <c>~0</c> → <c>~</c>.
    /// The replacements must be applied in this order (tilde-one before
    /// tilde-zero) to avoid double-decoding.
    /// </summary>
    private static string DecodePointerSegment(string segment) =>
        segment.Replace("~1", "/", System.StringComparison.Ordinal)
               .Replace("~0", "~", System.StringComparison.Ordinal);
}
