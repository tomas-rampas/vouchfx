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
/// Optional explicit schema version string; used by the validator to select the
/// correct schema fragment when the document targets a specific engine release.
/// </param>
public sealed record MetadataSpec(
    string? Name,
    string? Owner,
    IReadOnlyList<string>? Tags,
    string? Description,
    string? SchemaVersion);
