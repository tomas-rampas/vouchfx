// Platform.Engine.Runtime — concrete implementations of the provider context
// interfaces (IBindingContext, IProjectContext, ICompileContext) used during the
// ScenarioRunner's bind/validate/emit phase.
//
// These are intentionally minimal: IBindingContext and IProjectContext are marker
// interfaces in Sprint 1/2; ICompileContext carries the step identity surface
// (StepId, SuiteNamespace) introduced in Sprint 2.
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
/// <see cref="Platform.Sdk.IProjectContext"/> is a marker interface in Sprint 1/2;
/// this class carries no additional state.
/// </remarks>
internal sealed class RunProjectContext : Platform.Sdk.IProjectContext { }

/// <summary>
/// Runtime implementation of <see cref="Platform.Sdk.ICompileContext"/> used
/// during the <c>ScenarioRunner</c>'s emit phase.
/// </summary>
/// <remarks>
/// Carries the step identifier and the suite-level namespace required by
/// <see cref="Platform.Sdk.IStepCompiler{TModel}.Emit"/> implementations.
/// </remarks>
internal sealed class RunCompileContext : Platform.Sdk.ICompileContext
{
    /// <summary>
    /// Initialises a new <see cref="RunCompileContext"/> with the given step
    /// identifier and suite namespace.
    /// </summary>
    /// <param name="stepId">
    /// The identifier of the step currently being compiled.
    /// </param>
    /// <param name="suiteNamespace">
    /// The C# namespace into which the compiled suite is emitted.
    /// </param>
    public RunCompileContext(string stepId, string suiteNamespace)
    {
        StepId = stepId;
        SuiteNamespace = suiteNamespace;
    }

    /// <inheritdoc />
    public string StepId { get; }

    /// <inheritdoc />
    public string SuiteNamespace { get; }
}
