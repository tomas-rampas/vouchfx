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
/// supply one or the version is unknown. Catalogue serialisation uses
/// <c>JsonIgnoreCondition.Never</c>, so a null value is emitted on the wire as
/// <c>"engineVersion": null</c> (the property is not omitted). Consumers must
/// treat a JSON <c>null</c> and a missing property equivalently.
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
/// <param name="ExactlyOneOfGroups">
/// Additive (T1 follow-up, feat/fragment-completeness — gatekeeper B1). Each entry
/// is a group of field names of which EXACTLY ONE must be set — a root-level
/// <c>oneOf</c> whose every branch names exactly one required field (e.g.
/// <c>script.csharp</c>'s <c>[["code","file"]]</c>). Never <see langword="null"/>
/// for a catalogue entry built by <see cref="EngineExport.BuildCatalogue"/> (an
/// empty list when the type has no such shape); nullable only so a hand-built
/// fixture predating this field still compiles. Deliberately NOT folded into
/// <see cref="RequiredFields"/> as prose: that vehicle is consumed by
/// <c>SuiteScaffolder</c> as bare field NAMES emitted verbatim as YAML keys — a
/// prose string there is not a field name and breaks generated YAML. A field named
/// here is excluded from both <see cref="RequiredFields"/> and
/// <see cref="OptionalFields"/> (would otherwise silently contradict this group's
/// "exactly one" constraint). Degrades to an empty list — never a fabricated group
/// — when a <c>oneOf</c> branch's own <c>required</c> does not name EXACTLY one
/// field and nothing else (e.g. <c>mq-expect.azureservicebus</c>'s <c>queue</c> OR
/// (<c>topic</c> + <c>subscription</c>) shape has a two-name branch).
/// </param>
/// <param name="AtLeastOneOfGroups">
/// Additive (same follow-up as <see cref="ExactlyOneOfGroups"/>). Each entry is a
/// group of field names of which AT LEAST ONE must be set — the identical
/// single-required-per-branch detection applied to a root-level <c>anyOf</c>
/// instead of <c>oneOf</c> (e.g. <c>mq-expect.azureservicebus</c>'s
/// <c>[["expectPayloadContains","expectProperties"]]</c>). Same exclusion from
/// <see cref="RequiredFields"/>/<see cref="OptionalFields"/>, same
/// degrade-don't-fabricate guard, same nullable-for-old-fixtures-only contract.
/// </param>
public sealed record StepCatalogueEntry(
    [property: JsonPropertyOrder(0)] string Type,
    [property: JsonPropertyOrder(1)] string Family,
    [property: JsonPropertyOrder(2)] string Provider,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> RequiredFields,
    [property: JsonPropertyOrder(4)] IReadOnlyList<string> OptionalFields,
    [property: JsonPropertyOrder(5)] bool CaptureSupported,
    [property: JsonPropertyOrder(6)] string FamilyIntent,
    [property: JsonPropertyOrder(7)] IReadOnlyList<IReadOnlyList<string>>? ExactlyOneOfGroups = null,
    [property: JsonPropertyOrder(8)] IReadOnlyList<IReadOnlyList<string>>? AtLeastOneOfGroups = null);
