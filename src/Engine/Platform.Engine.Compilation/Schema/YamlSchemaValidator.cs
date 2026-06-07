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
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Platform.Engine.Compilation.Schema;

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
            doc = ConvertYamlToJsonDocument(yamlText);
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

            var errors = CollectErrors(results);
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
        var assembly = typeof(YamlSchemaValidator).Assembly;

        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("root-language-schema.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Embedded resource 'root-language-schema.json' was not found in assembly " +
                $"'{assembly.FullName}'.  Verify the <EmbeddedResource> item in the project file.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Could not open manifest resource stream for '{resourceName}'.");

        using var reader = new System.IO.StreamReader(stream);
        var schemaText = reader.ReadToEnd();
        return JsonSchema.FromText(schemaText);
    }

    /// <summary>
    /// Converts a YAML document string to a <see cref="JsonDocument"/> via
    /// YamlDotNet's JSON-compatible serialisation bridge.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for disposing the returned <see cref="JsonDocument"/>
    /// to release its pooled UTF-8 buffer.  Evaluation of the root element must
    /// occur inside the <c>using</c> scope.
    /// </remarks>
    /// <param name="yamlText">The raw YAML text to convert.</param>
    /// <returns>A <see cref="JsonDocument"/> representing the parsed document.</returns>
    private static JsonDocument ConvertYamlToJsonDocument(string yamlText)
    {
        // Step 1 — parse YAML to a plain object graph.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();

        var graph = deserializer.Deserialize<object?>(yamlText);

        if (graph is null)
        {
            // An entirely empty YAML document deserialises to null; surface this
            // as an invalid result rather than letting the schema validator
            // receive a null node.
            throw new InvalidOperationException("The YAML document is empty.");
        }

        // Step 2 — re-serialise the object graph to a JSON string using
        // YamlDotNet's JSON-compatible emitter, which correctly renders
        // Dictionary<object,object> entries as JSON object properties.
        var jsonSerializer = new SerializerBuilder()
            .JsonCompatible()
            .Build();

        var json = jsonSerializer.Serialize(graph);

        // Step 3 — parse the JSON string into a System.Text.Json JsonDocument.
        // The caller must dispose the document to release its pooled UTF-8 buffer.
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Walks the flat <c>Details</c> list produced by the <c>List</c> output
    /// format and collects every leaf node that is invalid and carries at least
    /// one keyword error.
    /// </summary>
    /// <param name="results">The top-level evaluation results.</param>
    /// <returns>
    /// A non-empty list of <see cref="SchemaValidationError"/> entries.  When the
    /// details list is empty (e.g. the root itself fails with no children), a
    /// single synthetic error at the root location is returned so the caller
    /// always receives at least one actionable message.
    /// </returns>
    private static List<SchemaValidationError> CollectErrors(EvaluationResults results)
    {
        var errors = new List<SchemaValidationError>();
        CollectErrorsRecursive(results, errors);

        if (errors.Count == 0)
        {
            // Fallback: the schema reports failure but produced no detailed errors
            // (this can happen with Flag-level results or nested $ref chains).
            errors.Add(new SchemaValidationError(
                results.InstanceLocation.ToString(),
                "Schema validation failed with no detailed error messages."));
        }

        return errors;
    }

    /// <summary>
    /// Recursively collects validation errors from an <see cref="EvaluationResults"/>
    /// tree, harvesting only the nodes that are invalid and carry keyword errors.
    /// </summary>
    private static void CollectErrorsRecursive(EvaluationResults node, List<SchemaValidationError> sink)
    {
        if (node.IsValid)
        {
            return;
        }

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
