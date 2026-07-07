// Platform.Engine.Authoring — ScenarioAst record (S03-B-02).
//
// The root normalised AST produced by AstBuilder from an E2eDocument.  All
// steps have been fully resolved and defaulted; downstream stages consume
// ScenarioAst, not E2eDocument.

using Platform.Engine.Authoring.Model;

namespace Platform.Engine.Authoring.Ast;

/// <summary>
/// The root normalised AST for a <c>.e2e.yaml</c> scenario, produced by
/// <see cref="AstBuilder.Build"/>.
/// </summary>
/// <remarks>
/// <para>
/// Normalisation parses duration strings, applies per-field defaults, and
/// rejects unknown step types and bare (non-dotted) type names.  Once a
/// <see cref="ScenarioAst"/> is in hand, all step-level
/// fields are fully typed and require no further null-coalescing or parsing.
/// </para>
/// <para>
/// <see cref="Environment"/> and <see cref="Metadata"/> are carried through
/// unchanged from the <see cref="E2eDocument"/>; their internal structure is
/// not examined by the AST builder.
/// </para>
/// </remarks>
/// <param name="Metadata">
/// Optional human-facing information about the scenario (name, owner, tags).
/// <see langword="null"/> when the YAML file omits the <c>metadata</c>
/// section.
/// </param>
/// <param name="Environment">
/// Optional infrastructure declaration.  Carried through unchanged from the
/// parsed document.  <see langword="null"/> when the YAML file omits the
/// <c>environment</c> section.
/// </param>
/// <param name="Variables">
/// Constant scalar values pre-loaded into the shared variable context before
/// the first step runs.  Never <see langword="null"/>; the empty dictionary
/// is used when the YAML file omits the <c>variables</c> section.
/// </param>
/// <param name="Steps">
/// Ordered, fully-normalised step nodes.  The engine executes them in list
/// order.
/// </param>
public sealed record ScenarioAst(
    MetadataSpec? Metadata,
    EnvironmentSpec? Environment,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<StepNode> Steps);
