// Vouchfx.Engine.Authoring — E2eDocument (S03-B-01).
//
// Strongly-typed document model for a .e2e.yaml file (docs/02 §3).
// Only the steps section is required; the remaining three sections are optional.

namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// The root document model for a <c>.e2e.yaml</c> test file (§3).
/// </summary>
/// <remarks>
/// The parser (<see cref="YamlDocumentParser"/>) deserialises a YAML string into
/// this record.  Only <see cref="Steps"/> is mandatory; all other sections are
/// optional and will be <see langword="null"/> when absent.
/// </remarks>
/// <param name="Metadata">
/// Optional human-facing information about the test (name, owner, tags).
/// See §3.1.
/// </param>
/// <param name="Environment">
/// Optional infrastructure declaration: services under test, managed dependencies,
/// and optional seed data.  See §3.2.
/// </param>
/// <param name="Variables">
/// Optional map of constant scalar values pre-loaded into the shared variable
/// context before the first step runs.  See §3.3.
/// </param>
/// <param name="Steps">
/// Ordered list of typed steps that constitute the test.  The only mandatory
/// top-level section.  See §3.4 and §4.
/// </param>
public sealed record E2eDocument(
    MetadataSpec? Metadata,
    EnvironmentSpec? Environment,
    IReadOnlyDictionary<string, string>? Variables,
    IReadOnlyList<StepSpec> Steps);
