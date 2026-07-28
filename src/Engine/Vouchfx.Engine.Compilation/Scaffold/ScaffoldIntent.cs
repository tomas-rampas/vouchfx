// Vouchfx.Engine.Compilation — Suite scaffold intent model (Spec B / mcp-generator-scaffold-and-run).
//
// Structured, catalogue-grounded input for SuiteScaffolder.Generate. Free text is never a
// parameter here: an MCP host / LLM chooses step types and ids; the engine emits a
// deterministic schema-valid skeleton.

namespace Vouchfx.Engine.Compilation.Scaffold;

/// <summary>
/// Structured scaffold intent: ordered steps plus an optional environment outline.
/// </summary>
/// <param name="Steps">
/// Ordered step intents. Must be non-empty; each id must be unique within the list.
/// </param>
/// <param name="Services">
/// Optional named services for <c>environment.services</c>. Image defaults to
/// <c>traefik/whoami</c> when omitted.
/// </param>
/// <param name="Dependencies">
/// Optional named managed dependencies for <c>environment.dependencies</c>. Each
/// <see cref="ScaffoldDependencyIntent.Type"/> must be a
/// <see cref="KnownDependencyKinds"/> entry.
/// </param>
public sealed record ScaffoldIntent(
    IReadOnlyList<ScaffoldStepIntent> Steps,
    IReadOnlyList<ScaffoldServiceIntent>? Services = null,
    IReadOnlyList<ScaffoldDependencyIntent>? Dependencies = null);

/// <summary>
/// One step to emit in scaffold order.
/// </summary>
/// <param name="Id">Step id (unique within the intent).</param>
/// <param name="Type">Dotted <c>family.provider</c> key that must exist in the live registry.</param>
/// <param name="Label">
/// Optional short human label. Emitted only as a YAML comment above the step
/// (<c># label: …</c>), not as a language field.
/// </param>
public sealed record ScaffoldStepIntent(
    string Id,
    string Type,
    string? Label = null);

/// <summary>
/// A service outline entry for the scaffolded <c>environment.services</c> map.
/// </summary>
/// <param name="Name">Logical service name (map key).</param>
/// <param name="Image">OCI image reference; defaults to <c>traefik/whoami</c> when null/empty.</param>
public sealed record ScaffoldServiceIntent(
    string Name,
    string? Image = null);

/// <summary>
/// A dependency outline entry for the scaffolded <c>environment.dependencies</c> map.
/// </summary>
/// <param name="Name">Logical dependency name (map key).</param>
/// <param name="Type">
/// Dependency kind (e.g. <c>postgres</c>, <c>kafka</c>) — must match
/// <see cref="KnownDependencyKinds"/>.
/// </param>
public sealed record ScaffoldDependencyIntent(
    string Name,
    string Type);
