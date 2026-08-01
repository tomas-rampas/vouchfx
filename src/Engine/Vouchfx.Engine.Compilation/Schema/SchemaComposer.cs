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
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Compilation.Schema;

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
    /// The frozen language-schema version this composer emits.  The composed
    /// schema self-identifies as this version via the
    /// <c>x-vouchfx-schema-version</c> annotation (see <see cref="ComposeSchemaJson"/>),
    /// so the snapshotted golden artifact is unambiguously the <c>v1</c> contract
    /// (S08-F-01).
    /// </summary>
    public const string SchemaVersion = "v1";

    /// <summary>
    /// The annotation key under which the composed schema records its frozen
    /// version.  Custom (<c>x-</c>-prefixed) keywords are ignored by the JSON
    /// Schema validator, so this marker is documentation only and does not affect
    /// validation behaviour.
    /// </summary>
    private const string SchemaVersionAnnotationKey = "x-vouchfx-schema-version";

    private static readonly JsonSerializerOptions s_indentedJsonOptions = new()
    {
        WriteIndented = true,
        // Preserve the schema text verbatim — JSON-escaping printable characters
        // would mutate provider regex/description strings and defeat byte-stability.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
        // Reuse the exact same composed JSON text that ComposeSchemaJson emits so
        // the validated schema and the frozen golden can never drift apart
        // (S08-F-01).  JsonSchema.FromText re-parses with all keyword handlers
        // registered; the indentation/annotation are validation-irrelevant.
        var composedJson = ComposeSchemaJson(registry);
        return JsonSchema.FromText(composedJson);
    }

    /// <summary>
    /// Composes the unified schema for <paramref name="registry"/> and returns it
    /// as canonical, indented JSON text suitable for snapshotting and diffing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output is <b>byte-stable</b> for a given set of provider fragments:
    /// the <c>if</c>/<c>then</c> discriminator clauses are sorted by their
    /// <c>&lt;family&gt;.&lt;provider&gt;</c> type key (so reflection/registration
    /// order cannot perturb the bytes) and the JSON is written with
    /// <see cref="JsonSerializerOptions.WriteIndented"/>.  This is what the
    /// S08-F-01 freeze gate snapshots into <c>composed-schema.v1.json</c>.
    /// </para>
    /// <para>
    /// <see cref="ComposeSchema"/> parses exactly this text, so the validated
    /// schema and the frozen golden are guaranteed identical.
    /// </para>
    /// </remarks>
    /// <param name="registry">
    /// The frozen provider registry whose fragments contribute to the schema.
    /// </param>
    /// <returns>The composed schema as canonical, indented JSON text.</returns>
    public static string ComposeSchemaJson(StepKindRegistry registry)
    {
        var rootObj = BuildComposedRootObject(registry);
        return rootObj.ToJsonString(s_indentedJsonOptions);
    }

    /// <summary>
    /// Parses the embedded root-language schema, injects the sorted provider
    /// discriminator clauses and the version annotation, strips <c>$id</c>, and
    /// returns the mutated <see cref="JsonObject"/>.  Shared by
    /// <see cref="ComposeSchema"/> and <see cref="ComposeSchemaJson"/> so both see
    /// identical content.
    /// </summary>
    private static JsonObject BuildComposedRootObject(StepKindRegistry registry)
    {
        var rootJson = SchemaResources.ReadRootLanguageSchemaJson();

        // Parse the root schema JSON into a mutable JsonObject so we can
        // inject the allOf discriminator block into $defs/step.
        var rootNode = JsonNode.Parse(rootJson)
            ?? throw new InvalidOperationException(
                "Root-language schema JSON parsed to null.");

        var rootObj = rootNode.AsObject();

        // Stamp the frozen version onto the composed schema so the artifact
        // self-identifies (S08-F-01).  The key is x-prefixed, so the validator
        // ignores it — it is purely a documentation/freeze marker.
        rootObj[SchemaVersionAnnotationKey] = JsonValue.Create(SchemaVersion);

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

        return rootObj;
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

        // Compose the schema JSON text once and build BOTH the evaluator
        // (JsonSchema) and, only if validation actually fails, a parsed
        // JsonElement copy of the SAME text for SchemaErrorCollector's enum
        // enrichment (see its FormatEnumError / TryReadEnumArray) — avoids a
        // second ComposeSchemaJson call (SchemaComposer.ComposeSchema would
        // otherwise recompute this same text) and avoids the extra parse
        // entirely on the common (valid) path.
        var composedSchemaJson = ComposeSchemaJson(registry);
        var composedSchema = JsonSchema.FromText(composedSchemaJson);

        using (doc)
        {
            var results = composedSchema.Evaluate(doc.RootElement, _options);

            if (results.IsValid)
                return SchemaValidationResult.Valid;

            // Pass the instance through so an unevaluatedProperties violation
            // (the typo-closing change on $defs/step) can name the offending
            // step's own 'type' in its message — see SchemaErrorCollector.
            using var schemaDoc = JsonDocument.Parse(composedSchemaJson);
            var errors = SchemaErrorCollector.CollectErrors(results, doc.RootElement, schemaDoc.RootElement);
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
        var keyed = new List<(string TypeKey, JsonObject Clause)>();

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

            keyed.Add((typeKey, clause));
        }

        // Sort the clauses by their <family>.<provider> type key so the composed
        // schema is BYTE-STABLE regardless of reflection/registration order
        // (registry.All iterates a dictionary's values).  This ordering is
        // validation-irrelevant: each clause's 'if' matches a distinct 'type'
        // const, the discriminators are mutually exclusive, and exactly one
        // branch ever applies to a given step.  A stable order is what lets the
        // S08-F-01 freeze gate snapshot the schema and diff it deterministically.
        keyed.Sort(static (a, b) => string.CompareOrdinal(a.TypeKey, b.TypeKey));

        var clauses = new List<JsonObject>(keyed.Count);
        foreach (var (_, clause) in keyed)
            clauses.Add(clause);

        return clauses;
    }

    // Error collection itself (the flat-Details walk plus "if"-discriminator
    // noise filtering, issue #259) lives in SchemaErrorCollector, shared with
    // YamlSchemaValidator so the walk is written exactly once.
}
