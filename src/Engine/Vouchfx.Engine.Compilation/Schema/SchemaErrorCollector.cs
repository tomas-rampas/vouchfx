// Issue #259 — filter spurious "if"-discriminator errors out of schema
// validation error collection (§8.2, §13.6).
//
// Shared by SchemaComposer (composed schema: root language schema + every
// registered provider's if/then discriminator clause) and YamlSchemaValidator
// (root schema only, no provider clauses) so the flat-Details-walk logic is
// written exactly once.
//
// Closed-step-surface diagnostics (unevaluatedProperties): $defs/step now
// closes with "unevaluatedProperties": false instead of its old
// "additionalProperties": true (the typo-closing change). JsonSchema.Net
// evaluates that per-offending-property against the bare boolean schema
// `false`, whose own failure carries no keyword name — the node's Errors
// dictionary is keyed by an EMPTY STRING with the generic message "All
// values fail against the false schema". FormatError recognises this shape
// (via EvaluationPath's terminal segment, since the per-node keyword is
// blank) and substitutes a message that names the offending property — and,
// when the original instance is available, the step's own `type` — so an
// author sees "Unknown property 'taget' on step type 'http.rest'" instead of
// the technically-correct-but-useless raw text.
//
// Follow-up regression (closed here): unevaluatedProperties only collects a
// subschema's annotations when that subschema's application succeeds AS A
// WHOLE. When a step is already invalid for an unrelated reason (a missing
// required field, a bad enum value, a `type` that matches no if/then clause),
// the matching provider fragment's own failure means NONE of its
// `properties` annotations propagate — every one of that step's otherwise
// legitimate fields is then reported as a spurious unevaluatedProperties
// "unknown property" error alongside the genuine defect.
// SuppressUnevaluatedPropertiesCascade drops those: same class of problem as
// IsIfDiscriminatorNoise (schema-machinery artefacts that are correct by the
// evaluator's rules and wrong for a human), same treatment.
using System.Globalization;
using System.Text.Json;
using Json.Schema;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// Shared error-collection logic for <see cref="EvaluationResults"/> trees
/// produced by <see cref="OutputFormat.List"/> evaluation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Background (§13.6).</b> <see cref="SchemaComposer"/> injects one
/// unconditional <c>if</c>/<c>then</c> clause per registered provider into
/// <c>$defs/step/allOf</c>, keyed on the step's <c>type</c> const. With
/// <see cref="OutputFormat.List"/>, JsonSchema.Net reports every node it
/// evaluates as a flat sibling under the top-level result — including each
/// non-matching clause's own <c>if</c> sub-evaluation, which is legitimately
/// invalid (the <c>type</c> const does not equal that clause's key) even
/// though this has no bearing on the compound <c>if</c>/<c>then</c> clause's
/// overall validity (a clause whose <c>if</c> does not match is vacuously
/// satisfied, per the JSON Schema 2020-12 <c>if</c>/<c>then</c>/<c>else</c>
/// semantics). For a single invalid step, this surfaces one spurious
/// <c>"Expected \"&lt;other-provider-type&gt;\""</c> entry per non-matching
/// provider — up to 24 at the full 25-provider Core catalogue — none of
/// which describe an actual problem with the document.
/// </para>
/// <para>
/// <see cref="IsIfDiscriminatorNoise"/> recognises and drops these: a node
/// contributes noise if and only if its <see cref="EvaluationResults.EvaluationPath"/>
/// contains an <c>allOf/&lt;N&gt;/if</c> segment triple (verified empirically —
/// see the class remarks on the <c>SchemaErrorCollectionAtScaleTests</c> test
/// fixture). A clause's genuine <c>then</c>-branch failure (reached only once
/// its <c>if</c> genuinely matched) always has an evaluation path of the form
/// <c>allOf/&lt;N&gt;/then/...</c> and is therefore never matched by this check.
/// </para>
/// </remarks>
internal static class SchemaErrorCollector
{
    /// <summary>
    /// Walks the flat <c>Details</c> list produced by the <c>List</c> output
    /// format and collects every leaf node that is invalid, carries at least
    /// one keyword error, and is not "if"-discriminator noise (see remarks).
    /// </summary>
    /// <param name="results">The top-level evaluation results.</param>
    /// <param name="instance">
    /// The original document instance the schema was evaluated against, when
    /// available. Never consulted for filtering or validity — only to enrich
    /// an <c>unevaluatedProperties</c> violation's message with the offending
    /// step's own <c>type</c> value (see the class remarks). Passing
    /// <see langword="null"/> degrades gracefully: the property name is still
    /// reported, just without the "on step type '…'" suffix.
    /// </param>
    /// <returns>
    /// A non-empty list of <see cref="SchemaValidationError"/> entries. When
    /// every genuine error was filtered out as noise but the schema still
    /// reports failure overall (e.g. a Flag-level result or an exotic nested
    /// <c>$ref</c> chain with no detailed errors at all), a single synthetic
    /// error at the root location is returned so the caller always receives
    /// at least one actionable message.
    /// </returns>
    internal static List<SchemaValidationError> CollectErrors(EvaluationResults results, JsonElement? instance = null)
    {
        var collected = new List<CollectedError>();
        CollectErrorsRecursive(results, collected, instance);

        var survivors = SuppressUnevaluatedPropertiesCascade(collected);

        var errors = new List<SchemaValidationError>(survivors.Count);
        foreach (var error in survivors)
        {
            errors.Add(new SchemaValidationError(error.InstanceLocation, error.Message));
        }

        if (errors.Count == 0)
        {
            errors.Add(new SchemaValidationError(
                results.InstanceLocation.ToString(),
                "Schema validation failed with no detailed error messages."));
        }

        return errors;
    }

    /// <summary>
    /// One collected, already-formatted error, plus enough of its own shape
    /// (see <see cref="IsUnevaluatedPropertiesShape"/>) for
    /// <see cref="SuppressUnevaluatedPropertiesCascade"/> to judge it without
    /// re-parsing the message text it is about to hand back to the caller.
    /// </summary>
    private readonly record struct CollectedError(string InstanceLocation, string Message, bool IsUnevaluatedProperties);

    private static void CollectErrorsRecursive(EvaluationResults node, List<CollectedError> sink, JsonElement? instance)
    {
        if (node.IsValid)
            return;

        // Skip "if"-discriminator noise (issue #259): a non-matching provider
        // clause's own 'if' sub-evaluation is legitimately invalid but describes
        // no real document problem — see the class remarks.
        if (node.Errors is { Count: > 0 } && !IsIfDiscriminatorNoise(node.EvaluationPath.ToString()))
        {
            var location = node.InstanceLocation.ToString();
            var evaluationPath = node.EvaluationPath.ToString();

            foreach (var (keyword, message) in node.Errors)
            {
                sink.Add(new CollectedError(
                    location,
                    FormatError(keyword, message, evaluationPath, location, instance),
                    IsUnevaluatedPropertiesShape(keyword, evaluationPath)));
            }
        }

        if (node.Details is { Count: > 0 })
        {
            foreach (var child in node.Details)
            {
                CollectErrorsRecursive(child, sink, instance);
            }
        }
    }

    /// <summary>
    /// Drops a step's <c>unevaluatedProperties</c> entries when that SAME step
    /// also carries at least one error of a different kind — the cascade
    /// described in the class remarks. A step's fields can only be judged
    /// "unevaluated" relative to whichever if/then clause matched its
    /// <c>type</c>; once that clause has already failed for an unrelated
    /// reason (a missing required field, a bad enum, …), every one of its
    /// <c>properties</c> annotations is withheld too, so its otherwise
    /// legitimate fields present as false positives. Suppressing them here
    /// trades completeness for correctness: if a step carries BOTH a genuine
    /// defect and an unrelated typo, the typo is hidden this round, the
    /// author fixes the reported defect, re-runs, and sees the typo next —
    /// standard iterative convergence, and preferable to asserting a false
    /// "unknown property" alongside a true one. When the ONLY thing wrong
    /// with a step is an unevaluated property, nothing here touches it — that
    /// is the feature <c>unevaluatedProperties: false</c> exists for.
    /// </summary>
    /// <remarks>
    /// Grouping is by the step's OWN instance path (<c>/steps/&lt;N&gt;</c>),
    /// derived from each error's <see cref="EvaluationResults.InstanceLocation"/>
    /// rather than assumed from list position, so two steps in one document
    /// are judged independently — see <see cref="TryGetStepScope"/>. An error
    /// whose location does not fall under <c>/steps/&lt;N&gt;</c> at all (e.g.
    /// a document-level violation such as a missing <c>steps</c> section) has
    /// no step scope and is therefore never touched by this method — the rule
    /// is deliberately confined to the step surface.
    /// </remarks>
    private static List<CollectedError> SuppressUnevaluatedPropertiesCascade(List<CollectedError> errors)
    {
        HashSet<string>? stepsWithOtherErrors = null;

        foreach (var error in errors)
        {
            if (!error.IsUnevaluatedProperties && TryGetStepScope(error.InstanceLocation, out var scope))
            {
                stepsWithOtherErrors ??= new HashSet<string>(StringComparer.Ordinal);
                stepsWithOtherErrors.Add(scope);
            }
        }

        // Fast path: no step in this document carries a non-unevaluatedProperties
        // error at all, so there is nothing to cascade from — every collected
        // error survives unchanged, in original tree-walk order.
        if (stepsWithOtherErrors is null)
            return errors;

        var survivors = new List<CollectedError>(errors.Count);
        foreach (var error in errors)
        {
            if (error.IsUnevaluatedProperties &&
                TryGetStepScope(error.InstanceLocation, out var scope) &&
                stepsWithOtherErrors.Contains(scope))
            {
                continue;
            }

            survivors.Add(error);
        }

        return survivors;
    }

    /// <summary>
    /// Extracts the owning step's own instance path (<c>/steps/&lt;N&gt;</c>)
    /// from an error's <c>InstanceLocation</c>, e.g. <c>/steps/0/target</c> or
    /// <c>/steps/0</c> itself both yield <c>/steps/0</c>. Returns
    /// <see langword="false"/> for any location that does not sit under a
    /// numbered <c>steps</c> element at all — the document-missing-`steps`
    /// case, or any future non-step top-level section — so callers never
    /// accidentally scope a suppression decision to something outside the
    /// step surface.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c>: <see cref="DocumentValidator"/>
    /// reuses this to scope its OWN unknown-step-type cross-check (#265) the
    /// same way — a step whose <c>type</c> matches no registered provider at
    /// all can never have any if/then fragment claim its properties either, so
    /// the identical cascade (an unrelated, step-level defect masking every
    /// other field as spuriously "unevaluated") recurs there. That defect
    /// lives outside the composed schema (a registry lookup, not a JSON Schema
    /// constraint) so <see cref="SuppressUnevaluatedPropertiesCascade"/> alone
    /// cannot see it; sharing the pointer-parsing logic keeps both call sites
    /// agreeing on what "the owning step" means without duplicating it.
    /// </remarks>
    internal static bool TryGetStepScope(string instanceLocation, out string stepScope)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            segments[0] == "steps" &&
            int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            stepScope = $"/steps/{segments[1]}";
            return true;
        }

        stepScope = string.Empty;
        return false;
    }

    /// <summary>
    /// Formats a single keyword/message pair into the text an author sees,
    /// substituting a named, actionable message for the otherwise-blank
    /// <c>unevaluatedProperties</c> shape (see the class remarks) and falling
    /// back to the original <c>[{keyword}] {message}</c> format for every
    /// other keyword.
    /// </summary>
    private static string FormatError(
        string keyword, string message, string evaluationPath, string instanceLocation, JsonElement? instance)
    {
        if (IsUnevaluatedPropertiesShape(keyword, evaluationPath))
        {
            return FormatUnevaluatedPropertiesError(instanceLocation, instance);
        }

        return $"[{keyword}] {message}";
    }

    /// <summary>
    /// True for the blank-keyword <c>unevaluatedProperties: false</c> shape
    /// (see the class remarks) — shared by <see cref="FormatError"/> (which
    /// keyword decides how to render the message) and
    /// <see cref="CollectErrorsRecursive"/> (which tags each collected error
    /// so <see cref="SuppressUnevaluatedPropertiesCascade"/> can group on it
    /// without re-parsing rendered text).
    /// </summary>
    private static bool IsUnevaluatedPropertiesShape(string keyword, string evaluationPath) =>
        keyword.Length == 0 && EndsWithSegment(evaluationPath, "unevaluatedProperties");

    /// <summary>
    /// True when <paramref name="message"/> is one this class produced via
    /// <see cref="FormatUnevaluatedPropertiesError"/> — recognised by its
    /// fixed <c>"[unevaluatedProperties] "</c> prefix, the only place that
    /// literal is ever written. Exposed for <see cref="DocumentValidator"/>,
    /// which only ever sees the final formatted <see cref="SchemaValidationError.Message"/>
    /// (never the raw keyword/evaluation-path pair <see cref="IsUnevaluatedPropertiesShape"/>
    /// needs) — see <see cref="TryGetStepScope"/>'s remarks for why it needs
    /// this at all.
    /// </summary>
    internal static bool IsUnevaluatedPropertiesMessage(string message) =>
        message.StartsWith("[unevaluatedProperties] ", StringComparison.Ordinal);

    /// <summary>
    /// Builds the actionable message for an <c>unevaluatedProperties: false</c>
    /// rejection: names the offending property (the last segment of
    /// <paramref name="instanceLocation"/>) and, when <paramref name="instance"/>
    /// is supplied and the property's containing object carries a string
    /// <c>type</c> field (true of every step — the schema requires it), the
    /// step's own type — e.g. <c>Unknown property 'taget' on step type
    /// 'http.rest'</c>. The "on step type '…'" suffix is omitted, not
    /// fabricated, when the type cannot be resolved (no instance supplied, or
    /// the containing object/its 'type' is missing or non-string).
    /// </summary>
    private static string FormatUnevaluatedPropertiesError(string instanceLocation, JsonElement? instance)
    {
        var propertyName = LastPointerSegment(instanceLocation);
        var stepType = instance is { } root ? TryResolveContainerType(instanceLocation, root) : null;

        return stepType is null
            ? $"[unevaluatedProperties] Unknown property '{propertyName}'"
            : $"[unevaluatedProperties] Unknown property '{propertyName}' on step type '{stepType}'";
    }

    /// <summary>
    /// Resolves the <c>type</c> string property of the object that directly
    /// contains the pointer's final segment — i.e. walks every segment except
    /// the last (the offending property itself) and reads <c>type</c> off
    /// whatever object that reaches. Returns <see langword="null"/> at the
    /// first sign the walk cannot proceed (an out-of-range index, a missing
    /// key, a non-container node) rather than throwing — this is a
    /// best-effort diagnostic enrichment, never a validation concern.
    /// </summary>
    private static string? TryResolveContainerType(string instanceLocation, JsonElement root)
    {
        var segments = instanceLocation.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var current = root;

        // Walk every segment except the last: that final segment names the
        // unevaluated property itself, not a step down into it.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = DecodePointerSegment(segments[i]);

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out var next))
                    return null;

                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                // NumberStyles.None + InvariantCulture, matching TryGetStepScope: a JSON
                // Pointer array index is a bare run of digits, so a leading sign, embedded
                // whitespace or a culture-specific group separator is malformed, not a
                // number to be helpfully coerced. Bare int.TryParse accepts all three and
                // would silently resolve " 1", "+1" or "-1" to an element — different
                // behaviour from the sibling helper walking the same pointers.
                if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= current.GetArrayLength())
                    return null;

                current = current[index];
            }
            else
            {
                return null;
            }
        }

        if (current.ValueKind != JsonValueKind.Object)
            return null;

        return current.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
    }

    /// <summary>
    /// The final <c>/</c>-separated segment of a JSON Pointer, RFC 6901-decoded
    /// (<c>~1</c> → <c>/</c>, <c>~0</c> → <c>~</c>, tilde-one first to avoid
    /// double-decoding). Returns the pointer unchanged if it carries no
    /// segments at all (defensive; every real <c>unevaluatedProperties</c>
    /// InstanceLocation names a property, so this never fires in practice).
    /// </summary>
    private static string LastPointerSegment(string pointer)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? pointer : DecodePointerSegment(segments[^1]);
    }

    private static string DecodePointerSegment(string segment) =>
        segment.Replace("~1", "/", StringComparison.Ordinal)
               .Replace("~0", "~", StringComparison.Ordinal);

    /// <summary>
    /// True when the last <c>/</c>-separated segment of an evaluation path
    /// equals <paramref name="keyword"/> exactly — used to recognise the
    /// <c>unevaluatedProperties</c> shape from <see cref="EvaluationResults.EvaluationPath"/>
    /// even though the node's own (per-property) keyword is blank.
    /// </summary>
    private static bool EndsWithSegment(string evaluationPath, string keyword)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1] == keyword;
    }

    /// <summary>
    /// Recognises an evaluation node that is only invalid because one of the
    /// composed schema's discriminator clauses' own <c>if</c> keyword did not
    /// match this step's <c>type</c> — see the class remarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Such a node's <see cref="EvaluationResults.EvaluationPath"/> contains an
    /// <c>allOf/&lt;N&gt;/if</c> segment sequence (confirmed against real
    /// JsonSchema.Net 9.2.1 output — e.g.
    /// <c>.../allOf/3/if/properties/type</c>); a clause's genuine <c>then</c>
    /// failure always has <c>allOf/&lt;N&gt;/then/...</c> instead, and is never
    /// matched here. <see cref="YamlSchemaValidator"/>'s root-only schema (no
    /// provider <c>allOf</c> clauses) can never produce a path shaped like this,
    /// so sharing this check with it is a no-op there, not a behaviour change.
    /// </para>
    /// <para>
    /// The match is deliberately <b>depth-independent</b>: the loop scans every
    /// <c>allOf/&lt;N&gt;/if</c> triple anywhere in the path, not only at the
    /// top level. This is correct, not merely convenient, because of the JSON
    /// Schema 2020-12 semantics of <c>if</c>/<c>then</c>/<c>else</c>: an
    /// <c>if</c> subschema's own pass/fail is <em>never</em> diagnostic of a
    /// document defect at any nesting depth — it only gates whether the
    /// sibling <c>then</c> (or <c>else</c>) applies. Consequently, a node that
    /// carries a genuine error can only ever sit on a <c>then</c>-side (or
    /// <c>else</c>-side) path; a path segment sequence ending directly under an
    /// <c>if</c> is categorically noise, however deeply it is nested (e.g. a
    /// provider fragment that nests its own conditional <c>allOf</c> inside its
    /// <c>then</c> branch — see <c>SchemaErrorCollectorTests</c>'s end-to-end
    /// nested-provider proof).
    /// </para>
    /// <para>
    /// This is also why the check is safe against untrusted document content:
    /// <see cref="EvaluationResults.EvaluationPath"/> is derived entirely from
    /// the <em>schema's</em> own structure — which providers, and this
    /// composer, control — never from the instance being validated. Nothing an
    /// author writes in a <c>.e2e.yaml</c> document can steer a genuine error
    /// onto a path this filter treats as noise; the instance only ever
    /// influences <see cref="EvaluationResults.InstanceLocation"/>, a wholly
    /// separate pointer this method never inspects.
    /// </para>
    /// </remarks>
    /// <param name="evaluationPath">
    /// The evaluation node's <see cref="EvaluationResults.EvaluationPath"/>,
    /// rendered as its JSON Pointer string (e.g. <c>/allOf/3/if/properties/type</c>).
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the path names an <c>if</c> keyword directly
    /// under an <c>allOf</c> array index — i.e. this node is discriminator
    /// noise, not a real document defect.
    /// </returns>
    internal static bool IsIfDiscriminatorNoise(string evaluationPath)
    {
        var segments = evaluationPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i + 2 < segments.Length; i++)
        {
            if (segments[i] == "allOf" && segments[i + 2] == "if")
            {
                return true;
            }
        }

        return false;
    }
}
