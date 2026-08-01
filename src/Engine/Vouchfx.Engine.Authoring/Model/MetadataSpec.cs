// Vouchfx.Engine.Authoring — MetadataSpec (S03-B-01).
//
// Strongly-typed record for the optional `metadata` top-level section of a
// .e2e.yaml file (docs/02 §3.1).

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// Human-facing description of the test (§3.1).
/// </summary>
/// <remarks>
/// All fields are optional.  Values here have no execution effect; they feed
/// reporting dashboards and the runner's test-selection language (§16).
/// </remarks>
/// <param name="Name">
/// Human-readable name for the test scenario.
/// </param>
/// <param name="Owner">
/// Team or individual responsible for this test.  The runner's selection
/// language filters on this value.
/// </param>
/// <param name="Tags">
/// List of labels used for suite filtering (e.g. <c>smoke</c>, <c>billing</c>).
/// </param>
/// <param name="Description">
/// Longer free-text explanation shown in test output.
/// </param>
/// <param name="SchemaVersion">
/// Optional explicit language schema version string. Bound here verbatim, but
/// read nowhere else in the engine — it is a future rejection hook, not a live
/// version-selection mechanism. The root JSON Schema constrains it to the
/// literal <c>"v1"</c> (the only language schema version that exists); a
/// document declaring anything else fails schema validation before this
/// binding is ever consulted, and an absent value remains valid (the field
/// stays optional). There is exactly one schema fragment today, so there is
/// nothing for this value to select between.
/// </param>
public sealed record MetadataSpec(
    string? Name,
    string? Owner,
    IReadOnlyList<string>? Tags,
    string? Description,
    string? SchemaVersion);
