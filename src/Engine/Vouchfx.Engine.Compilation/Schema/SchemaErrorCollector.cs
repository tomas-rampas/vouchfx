// Issue #259 — filter spurious "if"-discriminator errors out of schema
// validation error collection (§8.2, §13.6).
//
// Shared by SchemaComposer (composed schema: root language schema + every
// registered provider's if/then discriminator clause) and YamlSchemaValidator
// (root schema only, no provider clauses) so the flat-Details-walk logic is
// written exactly once.
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
    /// <returns>
    /// A non-empty list of <see cref="SchemaValidationError"/> entries. When
    /// every genuine error was filtered out as noise but the schema still
    /// reports failure overall (e.g. a Flag-level result or an exotic nested
    /// <c>$ref</c> chain with no detailed errors at all), a single synthetic
    /// error at the root location is returned so the caller always receives
    /// at least one actionable message.
    /// </returns>
    internal static List<SchemaValidationError> CollectErrors(EvaluationResults results)
    {
        var errors = new List<SchemaValidationError>();
        CollectErrorsRecursive(results, errors);

        if (errors.Count == 0)
        {
            errors.Add(new SchemaValidationError(
                results.InstanceLocation.ToString(),
                "Schema validation failed with no detailed error messages."));
        }

        return errors;
    }

    private static void CollectErrorsRecursive(EvaluationResults node, List<SchemaValidationError> sink)
    {
        if (node.IsValid)
            return;

        // Skip "if"-discriminator noise (issue #259): a non-matching provider
        // clause's own 'if' sub-evaluation is legitimately invalid but describes
        // no real document problem — see the class remarks.
        if (node.Errors is { Count: > 0 } && !IsIfDiscriminatorNoise(node.EvaluationPath.ToString()))
        {
            var location = node.InstanceLocation.ToString();
            foreach (var (keyword, message) in node.Errors)
            {
                sink.Add(new SchemaValidationError(location, $"[{keyword}] {message}"));
            }
        }

        if (node.Details is { Count: > 0 })
        {
            foreach (var child in node.Details)
            {
                CollectErrorsRecursive(child, sink);
            }
        }
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
