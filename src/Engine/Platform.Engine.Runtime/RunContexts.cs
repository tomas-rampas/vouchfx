// Platform.Engine.Runtime — concrete implementations of the provider context
// interfaces (IBindingContext, IProjectContext, ICompileContext) used during the
// ScenarioRunner's bind/validate/emit phase.
//
// Sprint-4 addition: RunProjectContext now carries DeclaredDependencies, derived
// from the scenario AST's environment.dependencies section and passed in by
// ProviderPipeline.Compile.
namespace Platform.Engine.Runtime;

/// <summary>
/// Runtime implementation of <see cref="Platform.Sdk.IBindingContext"/> used
/// during the <c>ScenarioRunner</c>'s bind phase.
/// </summary>
/// <remarks>
/// <see cref="Platform.Sdk.IBindingContext"/> is a marker interface in Sprint 1/2;
/// this class carries no additional state.
/// </remarks>
internal sealed class RunBindingContext : Platform.Sdk.IBindingContext { }

/// <summary>
/// Runtime implementation of <see cref="Platform.Sdk.IProjectContext"/> used
/// during the <c>ScenarioRunner</c>'s validation phase.
/// </summary>
/// <remarks>
/// <para>
/// Sprint-4 addition: carries <see cref="DeclaredDependencies"/> derived from
/// <c>environment.dependencies</c> in the scenario AST.  The map is empty when
/// the scenario omits that section.
/// </para>
/// </remarks>
internal sealed class RunProjectContext : Platform.Sdk.IProjectContext
{
    // ── Singleton representing an absent environment section ──────────────────

    /// <summary>
    /// A <see cref="RunProjectContext"/> with no declared dependencies, used
    /// when the scenario file omits the <c>environment.dependencies</c> section.
    /// </summary>
    internal static readonly RunProjectContext Empty =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises a <see cref="RunProjectContext"/> with the given dependency map.
    /// </summary>
    /// <param name="declaredDependencies">
    /// Map of dependency name to type (e.g. <c>"orders-db" → "postgres"</c>).
    /// Must not be <see langword="null"/>; use an empty dictionary when there
    /// are no dependencies.
    /// </param>
    internal RunProjectContext(IReadOnlyDictionary<string, string> declaredDependencies)
    {
        DeclaredDependencies = declaredDependencies;
    }

    // ── IProjectContext ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }
}

/// <summary>
/// Runtime implementation of <see cref="Platform.Sdk.ICompileContext"/> used
/// during the <c>ScenarioRunner</c>'s emit phase.
/// </summary>
/// <remarks>
/// <para>
/// Carries the step identifier and the suite-level namespace required by
/// <see cref="Platform.Sdk.IStepCompiler{TModel}.Emit"/> implementations.
/// </para>
/// <para>
/// Sprint-4 addition (S04-B-02): <see cref="Captures"/> carries the
/// <c>capture</c> map from the YAML step node so that providers can emit
/// JSONPath-based capture logic directly into the generated CSX block.
/// </para>
/// </remarks>
internal sealed class RunCompileContext : Platform.Sdk.ICompileContext
{
    /// <summary>
    /// An empty captures map used as the default when a step has no
    /// <c>capture</c> section.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> s_empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Initialises a new <see cref="RunCompileContext"/> with the given step
    /// identifier, suite namespace, and capture map.
    /// </summary>
    /// <param name="stepId">
    /// The identifier of the step currently being compiled.
    /// </param>
    /// <param name="suiteNamespace">
    /// The C# namespace into which the compiled suite is emitted.
    /// </param>
    /// <param name="captures">
    /// The step's <c>capture</c> map (varName → JSONPath expression).
    /// Pass <see langword="null"/> or omit to use an empty dictionary.
    /// </param>
    public RunCompileContext(
        string stepId,
        string suiteNamespace,
        IReadOnlyDictionary<string, string>? captures = null)
    {
        StepId = stepId;
        SuiteNamespace = suiteNamespace;
        Captures = captures ?? s_empty;
    }

    /// <inheritdoc />
    public string StepId { get; }

    /// <inheritdoc />
    public string SuiteNamespace { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Captures { get; }
}
