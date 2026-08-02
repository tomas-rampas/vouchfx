// Vouchfx.Engine.Runtime — concrete implementations of the provider context
// interfaces (IBindingContext, IProjectContext, ICompileContext) used during the
// ScenarioRunner's bind/validate/emit phase.
//
// Sprint-4 addition: RunProjectContext now carries DeclaredDependencies, derived
// from the scenario AST's environment.dependencies section and passed in by
// ProviderPipeline.Compile.
//
// Later addition: RunProjectContext and RunCompileContext both carry
// SuiteDirectory — the same base directory environment.seed already resolves
// relative fixture paths against — so a provider field that references an
// external file (e.g. script.csharp's `file`) can be validated and emitted
// consistently.
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
/// <para>
/// Later addition: carries <see cref="SuiteDirectory"/>, the base directory
/// relative file-path fields (e.g. <c>script.csharp</c>'s <c>file</c>) are
/// resolved against.
/// </para>
/// </remarks>
internal sealed class RunProjectContext : Vouchfx.Sdk.IProjectContext
{
    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises a <see cref="RunProjectContext"/> with the given dependency map
    /// and suite directory.
    /// </summary>
    /// <param name="declaredDependencies">
    /// Map of dependency name to type (e.g. <c>"orders-db" → "postgres"</c>).
    /// Must not be <see langword="null"/>; use an empty dictionary when there
    /// are no dependencies.
    /// </param>
    /// <param name="suiteDirectory">
    /// The base directory relative file-path fields are resolved against.
    /// </param>
    /// <param name="declaredServices">
    /// Map of service name to the endpoint/port names it exposes (services-generalisation
    /// spec, REQ-010). Defaults to an empty map (the pre-REQ-010 shape) when omitted, so
    /// this constructor's pre-existing 2-argument call sites keep compiling unchanged.
    /// </param>
    internal RunProjectContext(
        IReadOnlyDictionary<string, string> declaredDependencies,
        string suiteDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? declaredServices = null)
    {
        DeclaredDependencies = declaredDependencies;
        SuiteDirectory = suiteDirectory;
        DeclaredServices = declaredServices
            ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates a <see cref="RunProjectContext"/> with no declared dependencies or services,
    /// used when the scenario file omits both the <c>environment.dependencies</c> and
    /// <c>environment.services</c> sections.
    /// </summary>
    /// <param name="suiteDirectory">
    /// The base directory relative file-path fields are resolved against.
    /// </param>
    internal static RunProjectContext Empty(string suiteDirectory) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal), suiteDirectory);

    // ── IProjectContext ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeclaredServices { get; }

    /// <inheritdoc />
    public string SuiteDirectory { get; }
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
/// <para>
/// Later addition: carries <see cref="SuiteDirectory"/>, the base directory
/// relative file-path fields (e.g. <c>script.csharp</c>'s <c>file</c>) are
/// resolved against.
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
    /// identifier, suite namespace, capture map, and suite directory.
    /// </summary>
    /// <param name="stepId">
    /// The identifier of the step currently being compiled.
    /// </param>
    /// <param name="suiteNamespace">
    /// The C# namespace into which the compiled suite is emitted.
    /// </param>
    /// <param name="suiteDirectory">
    /// The base directory relative file-path fields are resolved against.
    /// </param>
    /// <param name="captures">
    /// The step's <c>capture</c> map (varName → <see cref="Vouchfx.Sdk.CaptureExpr"/>).
    /// Pass <see langword="null"/> or omit to use an empty dictionary.
    /// </param>
    public RunCompileContext(
        string stepId,
        string suiteNamespace,
        string suiteDirectory,
        IReadOnlyDictionary<string, Vouchfx.Sdk.CaptureExpr>? captures = null)
    {
        StepId = stepId;
        SuiteNamespace = suiteNamespace;
        SuiteDirectory = suiteDirectory;
        CaptureExprs = captures ?? s_empty;
        Captures = ProjectExpressions(CaptureExprs);
    }

    /// <inheritdoc />
    public string StepId { get; }

    /// <inheritdoc />
    public string SuiteNamespace { get; }

    /// <inheritdoc />
    public string SuiteDirectory { get; }

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
