// Vouchfx.Engine.Runtime — concrete implementations of the provider context
// interfaces (IBindingContext, IProjectContext, ICompileContext) used during the
// ScenarioRunner's bind/validate/emit phase.
//
// Sprint-4 addition: RunProjectContext now carries DeclaredDependencies, derived
// from the scenario AST's environment.dependencies section and passed in by
// ProviderPipeline.Compile.
namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Runtime implementation of <see cref="Vouchfx.Sdk.IBindingContext"/> used
/// during the <c>ScenarioRunner</c>'s bind phase.
/// </summary>
/// <remarks>
/// <see cref="Vouchfx.Sdk.IBindingContext"/> is a marker interface in Sprint 1/2;
/// this class carries no additional state.
/// </remarks>
internal sealed class RunBindingContext : Vouchfx.Sdk.IBindingContext { }

/// <summary>
/// Runtime implementation of <see cref="Vouchfx.Sdk.IProjectContext"/> used
/// during the <c>ScenarioRunner</c>'s validation phase.
/// </summary>
/// <remarks>
/// <para>
/// Sprint-4 addition: carries <see cref="DeclaredDependencies"/> derived from
/// <c>environment.dependencies</c> in the scenario AST.  The map is empty when
/// the scenario omits that section.
/// </para>
/// </remarks>
internal sealed class RunProjectContext : Vouchfx.Sdk.IProjectContext
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
/// Runtime implementation of <see cref="Vouchfx.Sdk.ICompileContext"/> used
/// during the <c>ScenarioRunner</c>'s emit phase.
/// </summary>
/// <remarks>
/// <para>
/// Carries the step identifier and the suite-level namespace required by
/// <see cref="Vouchfx.Sdk.IStepCompiler{TModel}.Emit"/> implementations.
/// </para>
/// <para>
/// Sprint-4 addition (S04-B-02): <see cref="Captures"/> carries the
/// <c>capture</c> map from the YAML step node so that providers can emit
/// JSONPath-based capture logic directly into the generated CSX block.
/// </para>
/// <para>
/// Sprint-7 addition (S07-B-01a): the context is constructed from the
/// format-aware <see cref="Vouchfx.Sdk.CaptureExpr"/> map and exposes it via
/// <see cref="CaptureExprs"/>; <see cref="Captures"/> is derived from it as an
/// order-preserving expression-string projection so that JSONPath-only providers
/// keep working unchanged.
/// </para>
/// </remarks>
internal sealed class RunCompileContext : Vouchfx.Sdk.ICompileContext
{
    /// <summary>
    /// An empty format-aware captures map used as the default when a step has no
    /// <c>capture</c> section.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Vouchfx.Sdk.CaptureExpr> s_empty =
        new Dictionary<string, Vouchfx.Sdk.CaptureExpr>(StringComparer.Ordinal);

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
    /// The step's <c>capture</c> map (varName → <see cref="Vouchfx.Sdk.CaptureExpr"/>).
    /// Pass <see langword="null"/> or omit to use an empty dictionary.
    /// </param>
    public RunCompileContext(
        string stepId,
        string suiteNamespace,
        IReadOnlyDictionary<string, Vouchfx.Sdk.CaptureExpr>? captures = null)
    {
        StepId = stepId;
        SuiteNamespace = suiteNamespace;
        CaptureExprs = captures ?? s_empty;
        Captures = ProjectExpressions(CaptureExprs);
    }

    /// <inheritdoc />
    public string StepId { get; }

    /// <inheritdoc />
    public string SuiteNamespace { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Captures { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, Vouchfx.Sdk.CaptureExpr> CaptureExprs { get; }

    /// <summary>
    /// Projects a format-aware capture map to the back-compatible expression-string
    /// view (<see cref="Vouchfx.Sdk.ICompileContext.Captures"/>), preserving iteration order so
    /// the two views stay key- and value-aligned for providers that materialise
    /// parallel arrays from each.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ProjectExpressions(
        IReadOnlyDictionary<string, Vouchfx.Sdk.CaptureExpr> exprs)
    {
        if (exprs.Count == 0)
        {
            return EmptyStringMap;
        }

        var projected = new Dictionary<string, string>(exprs.Count, StringComparer.Ordinal);
        foreach (var (name, expr) in exprs)
        {
            projected[name] = expr.Expression;
        }

        return projected;
    }

    /// <summary>An empty string-valued capture view (the projection of <see cref="s_empty"/>).</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyStringMap =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
