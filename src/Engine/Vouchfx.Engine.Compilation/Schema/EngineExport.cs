// Engine schema and catalogue export (Spec A / engine-schema-and-catalogue-export).
//
// Public library API: the running engine is the single source of truth for the composed
// v1 JSON Schema and the shape-level step catalogue. CLI (`vouchfx schema`,
// `vouchfx list --json`), MCP hosts, the VS Code extension, and third parties consume
// this surface — they must not vendor or invent step types / fields.
//
// Fail-closed: any registered step type without a usable schema fragment aborts the
// entire export (no partial success document). Secrets are never resolved or embedded;
// only structural field names and metadata are emitted.

using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// Public export entry points for the composed v1 JSON Schema and the shape-level
/// step catalogue, over any frozen <see cref="StepKindRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Package: <c>Vouchfx.Engine.Compilation</c>. Prefer these methods over reflecting
/// private CLI types or shelling out when running in-process (MCP, custom hosts).
/// </para>
/// <para>
/// Both exports cover <strong>every registered</strong> provider in the supplied
/// registry (Core and any additional providers already loaded in-process). There is
/// no silent Core-only filter.
/// </para>
/// <para>
/// Incomplete catalogue metadata fails the entire export with
/// <see cref="CatalogueExportException"/> naming the offending step type.
/// </para>
/// </remarks>
public static class EngineExport
{
    /// <summary>
    /// The catalogue document wire-schema version stamped on every
    /// <see cref="StepCatalogueDocument"/>.
    /// </summary>
    public const int CatalogueSchemaVersion = 1;

    /// <summary>
    /// Shared <see cref="JsonSerializerOptions"/> for catalogue documents: camelCase
    /// property names, indented output. Matches the CLI's <c>list --json</c> options
    /// so library and CLI serialisation cannot drift on property naming.
    /// </summary>
    public static JsonSerializerOptions CatalogueJsonOptions { get; } = CreateCatalogueJsonOptions();

    private static JsonSerializerOptions CreateCatalogueJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// One-line family intents for known Core families. Unknown families still receive a
    /// non-empty fallback so export never fails solely because a community family is new.
    /// </summary>
    private static readonly Dictionary<string, string> FamilyIntents =
        new(StringComparer.Ordinal)
        {
            ["http"] =
                "Call HTTP endpoints (REST or SOAP) on services under test and assert responses.",
            ["db-assert"] =
                "Query a data store and assert properties of the result set or document.",
            ["mq-publish"] =
                "Publish a message onto a broker to drive the system under test.",
            ["mq-expect"] =
                "Consume from a broker and assert that an expected message arrived.",
            ["cache-assert"] =
                "Assert keys, values, or documents in a cache or search index.",
            ["mail-expect"] =
                "Capture and assert on messages delivered over SMTP.",
            ["webhook-listen"] =
                "Listen for an inbound HTTP webhook and assert on the received payload.",
            ["metrics-assert"] =
                "Scrape or query metrics (e.g. Prometheus) and assert on series values.",
            ["storage-assert"] =
                "Assert object presence, content, or metadata in object storage (e.g. S3).",
            ["trace-expect"] =
                "Expect distributed-trace spans (e.g. OTLP) matching declared criteria.",
            ["script"] =
                "Run inline or file-backed C# for cases the declarative step types cannot express.",
        };

    /// <summary>
    /// Composes the unified v1 JSON Schema for <paramref name="registry"/> (root language
    /// grammar merged with every registered provider fragment) as canonical indented JSON.
    /// </summary>
    /// <param name="registry">
    /// The frozen provider registry. Every registered step type must have a non-null
    /// <see cref="RegisteredProvider.SchemaFragment"/> or the call fails closed.
    /// </param>
    /// <returns>The composed schema as indented JSON text (draft 2020-12).</returns>
    /// <exception cref="CatalogueExportException">
    /// Thrown when any registered step type lacks a schema fragment (incomplete metadata).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Propagated when the embedded root-language schema cannot be loaded.
    /// </exception>
    public static string ComposeSchemaJson(StepKindRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        EnsureAllProvidersHaveSchemaFragments(registry);
        return SchemaComposer.ComposeSchemaJson(registry);
    }

    /// <summary>
    /// Builds the shape-level step catalogue for every provider in
    /// <paramref name="registry"/>, sorted by dotted type key (ordinal).
    /// </summary>
    /// <param name="registry">The frozen provider registry to catalogue.</param>
    /// <param name="engineVersion">
    /// Optional host / engine version stamp written into the document; may be
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A complete <see cref="StepCatalogueDocument"/>. Never a partial document:
    /// incomplete metadata throws before the return.
    /// </returns>
    /// <exception cref="CatalogueExportException">
    /// Thrown when any registered step type lacks a schema fragment or its fragment
    /// cannot be parsed for field names. The exception message names the step type.
    /// </exception>
    public static StepCatalogueDocument BuildCatalogue(
        StepKindRegistry registry,
        string? engineVersion = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var entries = new List<StepCatalogueEntry>(capacity: registry.All.Count);

        // Sort first so failure order is deterministic when multiple types are broken.
        foreach (var registered in registry.All
                     .OrderBy(rp => $"{rp.Kind.Family}.{rp.Kind.Provider}", StringComparer.Ordinal))
        {
            entries.Add(BuildEntry(registered));
        }

        return new StepCatalogueDocument(
            SchemaVersion: CatalogueSchemaVersion,
            EngineVersion: engineVersion,
            StepTypes: entries);
    }

    /// <summary>
    /// Serialises <paramref name="document"/> with <see cref="CatalogueJsonOptions"/>.
    /// </summary>
    public static string SerializeCatalogue(StepCatalogueDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, CatalogueJsonOptions);
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private static void EnsureAllProvidersHaveSchemaFragments(StepKindRegistry registry)
    {
        foreach (var registered in registry.All
                     .OrderBy(rp => $"{rp.Kind.Family}.{rp.Kind.Provider}", StringComparer.Ordinal))
        {
            var typeKey = $"{registered.Kind.Family}.{registered.Kind.Provider}";
            if (registered.SchemaFragment is null)
            {
                throw new CatalogueExportException(
                    typeKey,
                    $"Step type '{typeKey}' has no JSON Schema fragment; catalogue/schema export "
                    + "requires every registered provider to implement IStepBinder<T> and supply "
                    + "a SchemaFragment.");
            }
        }
    }

    private static StepCatalogueEntry BuildEntry(RegisteredProvider registered)
    {
        var family = registered.Kind.Family;
        var provider = registered.Kind.Provider;
        var typeKey = $"{family}.{provider}";

        if (registered.SchemaFragment is null)
        {
            throw new CatalogueExportException(
                typeKey,
                $"Step type '{typeKey}' has no JSON Schema fragment; catalogue export requires "
                + "every registered provider to implement IStepBinder<T> and supply a "
                + "SchemaFragment.");
        }

        var (required, optional) = ExtractFields(typeKey, registered.SchemaFragment.Json);
        var familyIntent = ResolveFamilyIntent(family);

        // Capture is a common language-level step field; bar B marks every fragment-backed
        // type as capture-capable (the root schema accepts capture on every step).
        return new StepCatalogueEntry(
            Type: typeKey,
            Family: family,
            Provider: provider,
            RequiredFields: required,
            OptionalFields: optional,
            CaptureSupported: true,
            FamilyIntent: familyIntent);
    }

    /// <summary>
    /// Parses a provider's schema-fragment JSON for top-level <c>properties</c> and
    /// <c>required</c>. Field lists are type-specific only (fragments do not restate
    /// common root-step fields).
    /// </summary>
    private static (IReadOnlyList<string> Required, IReadOnlyList<string> Optional) ExtractFields(
        string typeKey,
        string fragmentJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(fragmentJson);
        }
        catch (JsonException ex)
        {
            throw new CatalogueExportException(
                typeKey,
                $"Step type '{typeKey}' SchemaFragment is not valid JSON: {ex.Message}",
                ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CatalogueExportException(
                    typeKey,
                    $"Step type '{typeKey}' SchemaFragment root must be a JSON object.");
            }

            var requiredSet = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("required", out var requiredNode) &&
                requiredNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in requiredNode.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var name = item.GetString();
                        if (!string.IsNullOrEmpty(name))
                            requiredSet.Add(name!);
                    }
                }
            }

            var allProperties = new List<string>();
            if (root.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in properties.EnumerateObject())
                    allProperties.Add(prop.Name);
            }

            // Stable ordinal sort for goldens / diffs.
            allProperties.Sort(StringComparer.Ordinal);

            var required = allProperties
                .Where(requiredSet.Contains)
                .ToList();

            var optional = allProperties
                .Where(name => !requiredSet.Contains(name))
                .ToList();

            // required[] entries that are not also in properties are still listed as
            // required (fragment authors sometimes put required without a properties map
            // for nested allOf-only shapes). Keep them, sorted.
            foreach (var name in requiredSet.OrderBy(n => n, StringComparer.Ordinal))
            {
                if (!required.Contains(name, StringComparer.Ordinal))
                    required.Add(name);
            }

            required.Sort(StringComparer.Ordinal);

            return (required, optional);
        }
    }

    private static string ResolveFamilyIntent(string family)
    {
        if (FamilyIntents.TryGetValue(family, out var intent))
            return intent;

        return $"Steps in the {family} family.";
    }
}

/// <summary>
/// Thrown when schema or catalogue export cannot complete because a registered
/// step type lacks the metadata required for a complete export (fail-closed).
/// </summary>
public sealed class CatalogueExportException : InvalidOperationException
{
    /// <summary>
    /// Initialises a new instance naming the offending step type.
    /// </summary>
    /// <param name="stepType">
    /// The dotted <c>family.provider</c> key of the incomplete provider, or
    /// <see cref="string.Empty"/> when the failure is not type-specific.
    /// </param>
    /// <param name="message">A clear description of the gap.</param>
    /// <param name="innerException">Optional inner cause (e.g. JSON parse failure).</param>
    public CatalogueExportException(string stepType, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StepType = stepType ?? string.Empty;
    }

    /// <summary>
    /// The dotted step type that lacked exportable metadata, when known.
    /// </summary>
    public string StepType { get; }
}
