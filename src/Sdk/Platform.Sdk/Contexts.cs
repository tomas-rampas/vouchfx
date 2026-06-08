// Platform.Sdk — provider-authoring contract surface (§13).
// This file declares the provider context interfaces passed to each provider stage.
// IBindingContext and IProjectContext remain marker interfaces this sprint;
// ICompileContext carries the step-identity surface (StepId + SuiteNamespace)
// introduced in Sprint 2.
namespace Platform.Sdk;

/// <summary>
/// Provides contextual services available to a provider's binding stage,
/// such as resolution of shared configuration or diagnostic sinks.
/// </summary>
/// <remarks>
/// <para>
/// This context is <strong>engine-supplied and provider-consumed</strong>:
/// providers receive an instance and read its members but never implement the
/// interface. Adding a member is therefore non-breaking for providers — only the
/// engine (the single in-tree implementor) must satisfy it. The <em>frozen v1
/// contract</em> (CLAUDE.md §13) governs the provider-<em>implemented</em> surface
/// (<c>IStepProvider</c>, <c>IStepBinder&lt;T&gt;</c>, …), which evolves solely via
/// new optional interfaces (e.g. <see cref="ICompileReferenceContributor"/>) and
/// freezes at the M1.5 milestone (end of Phase 2).
/// </para>
/// <para>Sprint-1 surface: marker only.</para>
/// </remarks>
public interface IBindingContext { }

/// <summary>
/// Provides contextual services available to a provider's validation stage,
/// such as access to the project-level configuration, path resolution, and
/// cross-step dependency information.
/// </summary>
/// <remarks>
/// <para>
/// This context is <strong>engine-supplied and provider-consumed</strong>:
/// providers receive an instance and read its members but never implement the
/// interface. Adding a member is therefore non-breaking for providers — only the
/// engine (the single in-tree implementor) must satisfy it. The <em>frozen v1
/// contract</em> (CLAUDE.md §13) governs the provider-<em>implemented</em> surface
/// (<c>IStepProvider</c>, <c>IStepBinder&lt;T&gt;</c>, …), which evolves solely via
/// new optional interfaces (e.g. <see cref="ICompileReferenceContributor"/>) and
/// freezes at the M1.5 milestone (end of Phase 2).
/// </para>
/// <para>
/// Sprint-4 addition: <see cref="DeclaredDependencies"/>.
/// </para>
/// </remarks>
public interface IProjectContext
{
    /// <summary>
    /// Gets the map of dependency names to their type identifiers as declared
    /// under <c>environment.dependencies</c> in the scenario file.
    /// </summary>
    /// <remarks>
    /// Keys are the logical dependency names (e.g. <c>"orders-db"</c>);
    /// values are the type identifiers (e.g. <c>"postgres"</c>, <c>"kafka"</c>).
    /// The map is empty when the scenario file omits the
    /// <c>environment.dependencies</c> section.
    /// Providers use this map to reconcile step targets against declared
    /// infrastructure (dependency reconciliation, §13).
    /// </remarks>
    IReadOnlyDictionary<string, string> DeclaredDependencies { get; }
}

/// <summary>
/// Provides contextual services available to a provider's compilation stage,
/// such as the step identifier, the suite-level namespace, and access to
/// shared helper registrations.
/// </summary>
/// <remarks>
/// <para>
/// This context is <strong>engine-supplied and provider-consumed</strong>:
/// providers receive an instance and read its members but never implement the
/// interface. Adding a member is therefore non-breaking for providers — only the
/// engine (the single in-tree implementor) must satisfy it. The <em>frozen v1
/// contract</em> (CLAUDE.md §13) governs the provider-<em>implemented</em> surface
/// (<c>IStepProvider</c>, <c>IStepBinder&lt;T&gt;</c>, …), which evolves solely via
/// new optional interfaces (e.g. <see cref="ICompileReferenceContributor"/>) and
/// freezes at the M1.5 milestone (end of Phase 2).
/// </para>
/// <para>
/// Sprint-2 additions: <see cref="StepId"/> and <see cref="SuiteNamespace"/>.
/// Sprint-4 addition: <see cref="Captures"/> (S04-B-02).
/// </para>
/// </remarks>
public interface ICompileContext
{
    /// <summary>
    /// Gets the identifier of the step currently being compiled.
    /// Providers must sanitise this value via <see cref="CsxFragment.SanitiseId"/>
    /// before embedding it in emitted variable names.
    /// </summary>
    string StepId { get; }

    /// <summary>
    /// Gets the C# namespace into which the compiled suite is emitted.
    /// Providers use this to qualify helper-class names when name collisions
    /// across suites must be avoided.
    /// </summary>
    string SuiteNamespace { get; }

    /// <summary>
    /// Gets the map of variable names to JSONPath expressions declared in the
    /// step's <c>capture</c> block (DSL §3, S04-B-02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keys are the author-supplied variable names (e.g. <c>"orderId"</c>);
    /// values are JSONPath expressions (e.g. <c>"$.id"</c>) that are evaluated
    /// against the step's response body at execution time.
    /// </para>
    /// <para>
    /// The map is never <see langword="null"/>; an empty dictionary is used when
    /// the YAML step omits the <c>capture</c> section.
    /// </para>
    /// <para>
    /// When a JSONPath expression yields no match the step outcome is set to
    /// <c>Verdict.Inconclusive</c> with reason
    /// <c>upstream-capture-unmet</c> (§12.1).
    /// </para>
    /// </remarks>
    IReadOnlyDictionary<string, string> Captures { get; }
}
