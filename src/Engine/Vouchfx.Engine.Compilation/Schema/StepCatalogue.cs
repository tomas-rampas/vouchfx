// Engine schema and catalogue export (Spec A / engine-schema-and-catalogue-export).
//
// Public, packable document model for the shape-level step catalogue: every registered
// step type with required/optional field names, capture support, and a short family
// intent. Serialised by the CLI (`list --json`) and by in-process hosts (MCP, VS Code
// tooling) via EngineExport — one model so CLI and library cannot drift.
//
// Wire shape is frozen at v1 (CatalogueJsonGoldenTests / ListJsonGoldenTests). Evolution
// within v1 is additive only.

using System.Text.Json.Serialization;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// The top-level step-catalogue document exported by
/// <see cref="EngineExport.BuildCatalogue"/>.
/// </summary>
/// <param name="SchemaVersion">
/// The catalogue wire-schema version (currently <see cref="EngineExport.CatalogueSchemaVersion"/>).
/// </param>
/// <param name="EngineVersion">
/// Optional informational engine / host version stamp (e.g. the CLI's assembly
/// informational version). May be <see langword="null"/> when the host does not
/// supply one; consumers must tolerate a missing value.
/// </param>
/// <param name="StepTypes">
/// Every registered step type, sorted by dotted <c>type</c> key (ordinal) for
/// deterministic diffs and golden files.
/// </param>
public sealed record StepCatalogueDocument(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] string? EngineVersion,
    [property: JsonPropertyOrder(2)] IReadOnlyList<StepCatalogueEntry> StepTypes);

/// <summary>
/// A single step-type entry in the exported catalogue (bar-B richness).
/// </summary>
/// <param name="Type">Dotted <c>family.provider</c> key (e.g. <c>http.rest</c>).</param>
/// <param name="Family">The step family (e.g. <c>http</c>).</param>
/// <param name="Provider">The provider id within the family (e.g. <c>rest</c>).</param>
/// <param name="RequiredFields">
/// Type-specific required field names from the provider's JSON Schema fragment,
/// sorted ordinal. Does not include common root-step fields (<c>id</c>, <c>type</c>, …).
/// </param>
/// <param name="OptionalFields">
/// Type-specific optional field names (declared <c>properties</c> not listed in
/// <c>required</c>), sorted ordinal.
/// </param>
/// <param name="CaptureSupported">
/// Whether the language allows a <c>capture</c> block on steps of this type.
/// Capture is a common step field; for catalogue bar B this is
/// <see langword="true"/> for every type that has a schema fragment.
/// </param>
/// <param name="FamilyIntent">
/// A short human-readable one-liner describing the family's purpose, sufficient for
/// an agent or editor to choose a family.
/// </param>
public sealed record StepCatalogueEntry(
    [property: JsonPropertyOrder(0)] string Type,
    [property: JsonPropertyOrder(1)] string Family,
    [property: JsonPropertyOrder(2)] string Provider,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> RequiredFields,
    [property: JsonPropertyOrder(4)] IReadOnlyList<string> OptionalFields,
    [property: JsonPropertyOrder(5)] bool CaptureSupported,
    [property: JsonPropertyOrder(6)] string FamilyIntent);
