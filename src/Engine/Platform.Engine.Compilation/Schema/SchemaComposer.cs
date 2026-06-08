// S02-C-02 — Schema composition mechanism (§8.2, §13.6).
//
// Assembles the unified JSON Schema from the root-language schema and each
// registered provider's JsonSchemaFragment.  The composition uses an if/then
// chain keyed on the step 'type' const (§13.6):
//
//   • An 'allOf' clause on the step-items schema contains one if/then pair
//     per registered provider.
//   • The 'if' matches steps whose 'type' const equals "<family>.<provider>".
//   • The 'then' applies the provider's own schema fragment.
//   • The const key is always derived from Kind — never from the fragment text —
//     so a provider cannot misdeclare its own key.
//
// Performance note (§13.6): the if/then/else discriminator pattern is
// dramatically faster than a flat 'oneOf' on large provider catalogues because
// the validator selects exactly one branch on the first comparison rather than
// trying every branch on every node.
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Platform.Sdk;

namespace Platform.Engine.Compilation.Schema;

/// <summary>
/// Assembles a unified JSON Schema from the root-language schema and the
/// <see cref="JsonSchemaFragment"/> contributed by each registered provider.
/// </summary>
/// <remarks>
/// <para>
/// The composition inserts an <c>allOf</c> block of <c>if</c>/<c>then</c>
/// clauses into the <c>step</c> definition inside the root schema.  Each clause
/// is keyed on the provider's <c>&lt;family&gt;.&lt;provider&gt;</c> string
/// (derived from <see cref="StepKindId"/>, never from the fragment) so that the
/// discriminator is authoritative and cannot be subverted by a provider's own
/// JSON text (§13.6).
/// </para>
/// <para>
/// Providers without a <see cref="JsonSchemaFragment"/> (i.e. those that do not
/// implement <c>IStepBinder&lt;TModel&gt;</c>) are silently omitted from the
/// composed schema.
/// </para>
/// </remarks>
public static class SchemaComposer
{
    private static readonly EvaluationOptions _options = new()
    {
        OutputFormat = OutputFormat.List,
    };

    /// <summary>
    /// Builds a unified <see cref="JsonSchema"/> that combines the embedded
    /// root-language schema with the <c>if</c>/<c>then</c> discriminator
    /// clauses contributed by every provider in <paramref name="registry"/>
    /// that has a non-null <see cref="JsonSchemaFragment"/>.
    /// </summary>
    /// <param name="registry">
    /// The frozen provider registry to compose.  An empty registry produces a
    /// schema identical to the raw root-language schema (no provider constraints).
    /// </param>
    /// <returns>
    /// A fully composed <see cref="JsonSchema"/> ready for evaluation.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Propagated when the embedded root-language schema cannot be found or
    /// loaded.
    /// </exception>
    public static JsonSchema ComposeSchema(StepKindRegistry registry)
    {
        var rootJson = SchemaResources.ReadRootLanguageSchemaJson();

        // Parse the root schema JSON into a mutable JsonObject so we can
        // inject the allOf discriminator block into $defs/step.
        var rootNode = JsonNode.Parse(rootJson)
            ?? throw new InvalidOperationException(
                "Root-language schema JSON parsed to null.");

        var rootObj = rootNode.AsObject();

        // Collect if/then clauses from providers that have a SchemaFragment.
        var ifThenClauses = BuildIfThenClauses(registry);

        if (ifThenClauses.Count > 0)
        {
            // Navigate to $defs/step (which already exists in the root schema).
            var defs = rootObj["$defs"]?.AsObject()
                ?? throw new InvalidOperationException(
                    "Root schema is missing the '$defs' object.");

            var stepDef = defs["step"]?.AsObject()
                ?? throw new InvalidOperationException(
                    "Root schema '$defs' is missing the 'step' definition.");

            // Inject an 'allOf' array containing one if/then pair per provider.
            // JSON-serialise each clause to a JsonNode so we stay in
            // System.Text.Json throughout (§5.7 — never Newtonsoft).
            var allOfArray = new JsonArray();
            foreach (var clause in ifThenClauses)
            {
                allOfArray.Add(clause);
            }

            stepDef["allOf"] = allOfArray;
        }

        // Remove the root schema's $id before parsing the composed schema.
        // YamlSchemaValidator has already registered the original $id URI with
        // the global JsonSchema.Net SchemaRegistry; attempting to register the
        // same URI a second time throws JsonSchemaException.  The composed
        // schema is transient — it is built per-call and does not need a stable
        // public URI, so stripping $id is safe and correct.
        rootObj.Remove("$id");

        // Re-serialise the mutated JSON object to text and parse it back
        // through JsonSchema.FromText so we get the fully validated schema
        // with all keyword handlers registered.
        var composedJson = rootObj.ToJsonString();
        return JsonSchema.FromText(composedJson);
    }

    /// <summary>
    /// Composes the unified schema from <paramref name="registry"/> and
    /// validates the supplied <paramref name="yamlText"/> against it.
    /// </summary>
    /// <param name="registry">
    /// The frozen provider registry whose fragments contribute to the
    /// composed schema.
    /// </param>
    /// <param name="yamlText">
    /// The raw contents of a <c>.e2e.yaml</c> file.
    /// </param>
    /// <returns>
    /// A <see cref="SchemaValidationResult"/> carrying the outcome of the
    /// composed-schema evaluation.
    /// </returns>
    public static SchemaValidationResult Validate(StepKindRegistry registry, string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return SchemaValidationResult.Invalid(
                new SchemaValidationError(string.Empty, "The document is empty or contains only whitespace."));
        }

        JsonDocument doc;
        try
        {
            doc = SchemaResources.ConvertYamlToJsonDocument(yamlText);
        }
        catch (Exception ex)
        {
            return SchemaValidationResult.Invalid(
                new SchemaValidationError(string.Empty, $"Failed to parse YAML: {ex.Message}"));
        }

        var composedSchema = ComposeSchema(registry);

        using (doc)
        {
            var results = composedSchema.Evaluate(doc.RootElement, _options);

            if (results.IsValid)
                return SchemaValidationResult.Valid;

            var errors = CollectErrors(results);
            return new SchemaValidationResult(false, errors);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds an ordered list of <c>if</c>/<c>then</c> JSON objects — one per
    /// provider fragment — to be placed into the step-level <c>allOf</c>.
    /// </summary>
    /// <remarks>
    /// The <c>if</c> clause matches steps whose <c>type</c> property value is a
    /// const equal to <c>"&lt;family&gt;.&lt;provider&gt;"</c>.  The
    /// <c>then</c> clause applies the provider's own <see cref="JsonSchemaFragment"/>
    /// JSON, parsed as-is.  The const key is always derived from
    /// <see cref="StepKindId"/>; the fragment text never influences the key.
    /// </remarks>
    private static List<JsonObject> BuildIfThenClauses(StepKindRegistry registry)
    {
        var clauses = new List<JsonObject>();

        foreach (var registered in registry.All)
        {
            if (registered.SchemaFragment is null)
                continue;

            var typeKey = $"{registered.Kind.Family}.{registered.Kind.Provider}";

            // Parse the provider's fragment JSON into a JsonNode.
            // If the fragment is malformed JSON we propagate the parse exception
            // so the composer fails fast at startup rather than silently
            // producing a broken schema.
            var fragmentNode = JsonNode.Parse(registered.SchemaFragment.Json)
                ?? throw new InvalidOperationException(
                    $"Provider '{typeKey}' SchemaFragment JSON parsed to null.");

            // Build: { "if": { "properties": { "type": { "const": "<key>" } }, "required": ["type"] },
            //          "then": <fragmentNode> }
            var ifClause = new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["type"] = new JsonObject
                    {
                        ["const"] = JsonValue.Create(typeKey),
                    },
                },
                ["required"] = new JsonArray { JsonValue.Create("type") },
            };

            var clause = new JsonObject
            {
                ["if"] = ifClause,
                ["then"] = fragmentNode,
            };

            clauses.Add(clause);
        }

        return clauses;
    }

    /// <summary>
    /// Walks the flat <c>Details</c> list produced by the <c>List</c> output
    /// format and collects every leaf node that is invalid and carries at least
    /// one keyword error.
    /// </summary>
    private static List<SchemaValidationError> CollectErrors(EvaluationResults results)
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

        if (node.Errors is { Count: > 0 })
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
}
