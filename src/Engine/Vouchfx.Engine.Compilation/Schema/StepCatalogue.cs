// Engine schema and catalogue export (Spec A / engine-schema-and-catalogue-export).
//
// Public, packable document model for the shape-level step catalogue: every registered
// step type with required/optional field names, capture support, and a short family
// intent, plus (additive, #556) its governance tier, the verify modes it accepts, a link to
// its language-reference section and a minimal example suite. Serialised by the CLI
// (`list --json`) and by in-process hosts (MCP, VS Code tooling) via EngineExport — one
// model so CLI and library cannot drift.
//
// Wire shape is frozen at v1 (CatalogueJsonGoldenTests / ListJsonGoldenTests). Evolution
// within v1 is additive only.
//
// The four members #556 added (tier, supportedVerifyModes, docsUrl, example) are init
// properties in the record BODY, not positional parameters, and that placement is the
// compatibility decision rather than a style choice. The previous additive round put its two
// members in the primary constructor, which changed the arity of both that constructor and the
// compiler-generated Deconstruct: binary-breaking for any compiled consumer, and
// source-breaking for a positional pattern written against the old shape (CHANGELOG records
// the cost). A body property changes neither, so an existing consumer keeps compiling AND keeps
// running against the new assembly. JsonPropertyOrder 9-12 still appends them after
// atLeastOneOfGroups on the wire, and System.Text.Json populates init-only properties after it
// runs the (unchanged) parameterised constructor, so a document still round-trips.

using System.Text.Json.Serialization;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// The top-level step-catalogue document exported by
/// <see cref="EngineExport.BuildCatalogue(Vouchfx.Sdk.StepKindRegistry, string)"/> and its
/// Core-set overload.
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
/// for a catalogue entry built by either <c>EngineExport.BuildCatalogue</c> overload (an
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
    [property: JsonPropertyOrder(8)] IReadOnlyList<IReadOnlyList<string>>? AtLeastOneOfGroups = null)
{
    /// <summary>
    /// Additive (#556). The provider's governance tier: <see cref="EngineExport.CoreTier"/>
    /// (<c>"core"</c>) when the registered provider's type is declared in one of the Core
    /// assemblies the caller passed to
    /// <see cref="EngineExport.BuildCatalogue(Vouchfx.Sdk.StepKindRegistry, string, IEnumerable{System.Reflection.Assembly})"/>,
    /// <see cref="EngineExport.CommunityTier"/> (<c>"community"</c>) when it is not, and
    /// <see langword="null"/> when the caller passed no Core set at all — "the caller did not
    /// say", never a guess from a namespace or an assembly name.
    /// </summary>
    /// <remarks>
    /// Membership is supplied by the caller because only the caller knows what it ships: the
    /// CLI passes <c>ProviderRegistryFactory.CoreProviderAssemblies()</c>, its single point of
    /// truth for the Core set, so every <c>list --json</c> entry is <c>"core"</c>. Blueprint
    /// §13.9 defines exactly two tiers, and Community covers every provider that is not Core
    /// (company-internal adapters included), so a provider outside a supplied set is
    /// <c>"community"</c>. There is deliberately no <c>vouched</c> member: §13.9 defines the
    /// Vouched badge as an endorsement of a Community provider's specific version, recorded in
    /// the provider hub's registry, which this engine never reads; Core providers are not
    /// subject to it. A consumer derives it from this tier plus the hub.
    /// </remarks>
    [JsonPropertyOrder(9)]
    public string? Tier { get; init; }

    /// <summary>
    /// Additive (#556). The <c>verifyMode</c> values a step of this type accepts, as the DSL
    /// wire tokens the <c>step-started</c> event already emits (<c>"IMMEDIATE"</c>,
    /// <c>"RETRY"</c>), in the <c>VerifyMode</c> enum's order. Never
    /// <see langword="null"/> for an entry built by either <c>EngineExport.BuildCatalogue</c>
    /// overload — the Core set is not an input to it; nullable only so a hand-built fixture
    /// predating this member still compiles.
    /// </summary>
    /// <remarks>
    /// This is an engine fact, not an advisability heuristic, and it needs nothing from the
    /// caller: the provider pipeline COMPILES <c>RETRY</c> for every step type (the
    /// engine-owned polling loop wraps the step; it is not rejected), DSL §7.1 makes the two
    /// modes language-wide, and no provider or SDK surface can opt out. Every entry therefore
    /// carries the full <c>VerifyMode</c> set. Should a per-provider opt-out ever exist, it
    /// narrows this list — an additive change to its meaning for that provider, never a
    /// renamed or retyped member.
    /// </remarks>
    [JsonPropertyOrder(10)]
    public IReadOnlyList<string>? SupportedVerifyModes { get; init; }

    /// <summary>
    /// Additive (#556). The absolute URL of this type's section in the published language
    /// reference — <c>https://vouchfx.io/language-reference/#&lt;anchor&gt;</c>, e.g.
    /// <c>#httprest</c> or <c>#db-assertpostgres</c> — or <see langword="null"/> unless
    /// <see cref="Tier"/> is <c>"core"</c>.
    /// </summary>
    /// <remarks>
    /// The reference (<c>docs/language-reference.md</c>, generated from the composed schema and
    /// golden-gated) documents exactly the Core registry, with one <c>### `&lt;type&gt;`</c>
    /// heading per type and a "Registered step types" list linking each by its anchor. The
    /// anchor is the id the published site's Markdown renderer gives that heading, reproduced
    /// by a port of Python-Markdown's default <c>toc</c> slugify; a test proves every Core
    /// anchor is both a heading id and a list link in the committed reference, and that the
    /// base URL equals <c>mkdocs.yml</c>'s <c>site_url</c>. A non-Core entry has no section
    /// there to point at, and for an entry whose tier the caller did not state the engine
    /// cannot tell whether it has one, so neither gets a URL rather than a guessed one.
    /// </remarks>
    [JsonPropertyOrder(11)]
    public string? DocsUrl { get; init; }

    /// <summary>
    /// Additive (#556). A complete, minimal <c>.e2e.yaml</c> suite exercising one step of this
    /// type, as YAML text (ASCII, LF line endings, trailing newline), or
    /// <see langword="null"/> unless <see cref="Tier"/> is <c>"core"</c> and the engine can
    /// derive the environment the step targets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Engine-authored and deterministic: it is <c>SuiteScaffolder</c>'s own output for a
    /// single step of the type, over a canonical minimal environment, with the scaffold's
    /// provenance comment lines left out — so it carries no timestamp, host path or version
    /// string. The step id is the type with its dot replaced by a hyphen
    /// (<c>db-assert-postgres</c>); a service-targeting type (<c>http</c>,
    /// <c>metrics-assert</c>) gets one service named <c>app</c>; a dependency-targeting type
    /// gets one dependency named after its kind (<c>postgres</c>, <c>kafka</c>,
    /// <c>mailpit</c>, …); a type with no <c>target</c> (<c>script.csharp</c>,
    /// <c>trace-expect.otlp</c>, <c>webhook-listen.http</c>) gets no environment at all.
    /// The Core examples therefore compose: two of the same kind share one dependency name, no
    /// two share a step id, and all 25 merge into one suite that still validates (tested).
    /// </para>
    /// <para>
    /// It is the whole suite, not the step alone, because a step alone names a
    /// <c>target</c> it does not declare: measured against the built CLI, 22 of the 25 Core
    /// step types fail <c>vouchfx validate</c> without an environment declaring their target.
    /// A test runs every Core example through the same schema, bind and provider-Validate
    /// pipeline <c>vouchfx validate</c> uses. That proof covers the Core registry only, which
    /// is why a non-Core entry gets no example: the scaffolder's placeholders are tailored to
    /// the Core field vocabulary, and for a provider the engine does not ship they would be
    /// guesses.
    /// </para>
    /// </remarks>
    [JsonPropertyOrder(12)]
    public string? Example { get; init; }
}
