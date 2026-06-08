// S02-C-02 — shared internal helpers for schema loading and YAML→JSON conversion.
//
// Extracted from YamlSchemaValidator so that SchemaComposer can reuse both the
// embedded resource loader and the YAML→JsonDocument bridge without duplication.
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Platform.Engine.Compilation.Schema;

/// <summary>
/// Shared internal helpers for loading embedded schema resources and converting
/// YAML documents to <see cref="JsonDocument"/> instances.
/// </summary>
/// <remarks>
/// Both <see cref="YamlSchemaValidator"/> and <see cref="SchemaComposer"/> use
/// these helpers.  Keeping them here avoids duplicating the YAML→JSON bridge and
/// the embedded-resource lookup pattern.
/// </remarks>
internal static class SchemaResources
{
    /// <summary>
    /// Reads the embedded <c>root-language-schema.json</c> resource from the
    /// <see cref="Platform.Engine.Compilation"/> assembly and returns its raw
    /// JSON text.
    /// </summary>
    /// <returns>
    /// The full JSON text of the embedded root-language schema.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the embedded resource is not found or cannot be opened.
    /// </exception>
    internal static string ReadRootLanguageSchemaJson()
    {
        var assembly = typeof(SchemaResources).Assembly;

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
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Converts a YAML document string to a <see cref="JsonDocument"/> via
    /// YamlDotNet's JSON-compatible serialisation bridge.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for disposing the returned
    /// <see cref="JsonDocument"/> to release its pooled UTF-8 buffer.
    /// Evaluation of the root element must occur inside the <c>using</c> scope.
    /// </remarks>
    /// <param name="yamlText">The raw YAML text to convert.</param>
    /// <returns>A <see cref="JsonDocument"/> representing the parsed document.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the YAML document is entirely empty.
    /// </exception>
    internal static JsonDocument ConvertYamlToJsonDocument(string yamlText)
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
        return JsonDocument.Parse(json);
    }
}
