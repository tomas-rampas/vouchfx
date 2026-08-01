// S02-C-01 — YAML→JSON schema validation (§8).
//
// Converts a raw .e2e.yaml document to a System.Text.Json node, evaluates it
// against the embedded root-language JSON Schema (draft 2020-12), and maps the
// results to a typed SchemaValidationResult.
//
// Library invariants (CLAUDE.md §5.7):
//   • JsonSchema.Net (JsonEverything) — draft 2020-12 validation.
//   • YamlDotNet — YAML deserialisation and JSON-compatible serialisation.
//   • System.Text.Json only — never Newtonsoft.
using System.Text.Json;
using Json.Schema;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// Validates a raw <c>.e2e.yaml</c> document against the embedded root-language
/// JSON Schema (draft 2020-12).
/// </summary>
/// <remarks>
/// <para>
/// The schema is loaded once from the assembly's embedded resources and cached
/// for the lifetime of the process.  Subsequent calls to <see cref="Validate"/>
/// re-use the cached <c>JsonSchema</c> instance.
/// </para>
/// <para>
/// The YAML→JSON bridge uses YamlDotNet's JSON-compatible serialisation path
/// (<c>SerializerBuilder().JsonCompatible()</c>), which avoids the
/// <c>Dictionary&lt;object,object&gt;</c> round-trip problem that arises when
/// deserialising YAML to a plain <c>object</c> graph and then re-serialising
/// with <c>System.Text.Json</c>.
/// </para>
/// </remarks>
public static class YamlSchemaValidator
{
    private static readonly JsonSchema _schema = LoadSchema();

    // A parsed copy of the SAME root-language-schema.json text _schema was
    // built from, kept alive for the process lifetime alongside it (never
    // disposed — mirrors _schema's own "load once, live forever" caching).
    // SchemaErrorCollector's enum enrichment (FormatEnumError) needs a
    // JsonElement view of the schema to resolve a failing node's live
    // accepted-values list; only this class's root-only schema ever needs
    // this specific copy, so it lives here rather than in SchemaResources.
    private static readonly JsonDocument _schemaDocument = JsonDocument.Parse(SchemaResources.ReadRootLanguageSchemaJson());

    private static readonly EvaluationOptions _options = new()
    {
        OutputFormat = OutputFormat.List,
    };

    /// <summary>
    /// Validates the supplied YAML text against the root-language JSON Schema.
    /// </summary>
    /// <param name="yamlText">
    /// The raw contents of a <c>.e2e.yaml</c> file.  Empty or whitespace-only
    /// input is treated as invalid rather than throwing.
    /// </param>
    /// <returns>
    /// A <see cref="SchemaValidationResult"/> that is <c>IsValid = true</c> when
    /// the document satisfies all schema constraints, or <c>false</c> with one or
    /// more located <see cref="SchemaValidationError"/> entries otherwise.
    /// </returns>
    public static SchemaValidationResult Validate(string yamlText)
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

        using (doc)
        {
            var results = _schema.Evaluate(doc.RootElement, _options);

            if (results.IsValid)
            {
                return SchemaValidationResult.Valid;
            }

            // Pass the instance through so an unevaluatedProperties violation
            // can still name the offending step's own 'type' even though this
            // root-only schema carries no provider allOf/if/then clauses —
            // see SchemaErrorCollector. Also pass the schema itself so an
            // enum violation (e.g. dependency 'type', 'imagePullPolicy') can
            // be enriched with its live accepted-values list.
            var errors = SchemaErrorCollector.CollectErrors(results, doc.RootElement, _schemaDocument.RootElement);
            return new SchemaValidationResult(false, errors);
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads the embedded <c>root-language-schema.json</c> resource and parses
    /// it into a <see cref="JsonSchema"/> instance.
    /// </summary>
    private static JsonSchema LoadSchema()
    {
        var schemaText = SchemaResources.ReadRootLanguageSchemaJson();
        return JsonSchema.FromText(schemaText);
    }

    // Error collection itself (the flat-Details walk plus "if"-discriminator
    // noise filtering, issue #259) lives in SchemaErrorCollector, shared with
    // SchemaComposer. This class's root-only schema never carries a provider
    // 'allOf' clause, so the noise filter is a no-op here — sharing the walk
    // is purely deduplication, not a behaviour change for this validator.
}
