// Vouchfx.Sdk.Testing — StepRunResult.
//
// The data result returned by ProviderTestHarness.RunSingleStepAsync.  EXPECTED
// failures (schema-validation, model-validation) are returned here as data with a
// null Verdict and a populated error list — never as exceptions — so a provider
// author can assert on them without try/catch.  An UNEXPECTED failure (a genuine
// Roslyn compile error caused by a contributor's emit bug) still throws loudly.
using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Sdk.Testing;

/// <summary>
/// The outcome of running a single step through <see cref="ProviderTestHarness"/>.
/// </summary>
/// <remarks>
/// <para>
/// This record encodes the three terminal states of the single-step pipeline as data
/// (never as exceptions), so a provider author asserts on them directly:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>Schema rejected</strong> — the YAML failed JSON-Schema validation
///       against the composed language schema.  <see cref="Verdict"/> is
///       <see langword="null"/>, <see cref="SchemaErrors"/> is non-empty, and the
///       pipeline halted before parsing.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Model-validation failed</strong> — the provider's
///       <c>IStepValidator&lt;T&gt;.Validate</c> rejected the bound model.
///       <see cref="Verdict"/> is <see langword="null"/>,
///       <see cref="ValidationErrors"/> is non-empty, and the pipeline halted before
///       emit.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Ran</strong> — the step compiled and executed; <see cref="Verdict"/>
///       carries the step's recorded outcome (Pass / Fail / EnvironmentError /
///       Inconclusive, §12.1) and <see cref="Observation"/> its diagnostic text.
///     </description>
///   </item>
/// </list>
/// <para>
/// A genuine Roslyn compile error — a bug in the provider's emitted CSX — is
/// <strong>not</strong> represented here: the harness lets it throw so the contributor
/// sees it loudly.
/// </para>
/// </remarks>
/// <param name="Verdict">
/// The step's verdict (§12.1) when the step ran, or <see langword="null"/> when the
/// pipeline halted at schema validation or model validation (an authoring error, not a
/// product outcome).
/// </param>
/// <param name="Observation">
/// The provider-supplied diagnostic observation recorded by the step, or
/// <see langword="null"/> when the step recorded none or did not run.
/// </param>
/// <param name="DurationMs">
/// Wall-clock duration the step recorded in milliseconds; <c>0</c> when the step did
/// not run.  Always non-negative.
/// </param>
/// <param name="SchemaErrors">
/// The schema-validation error messages when the YAML was schema-rejected; empty
/// otherwise.
/// </param>
/// <param name="ValidationErrors">
/// The model-validation error messages when the provider rejected the bound model;
/// empty otherwise.
/// </param>
public sealed record StepRunResult(
    Verdict? Verdict,
    string? Observation,
    long DurationMs,
    IReadOnlyList<string> SchemaErrors,
    IReadOnlyList<string> ValidationErrors)
{
    /// <summary>
    /// Gets a value indicating whether the step ran and passed
    /// (<see cref="Verdict"/> equals <see cref="Vouchfx.Engine.Abstractions.Verdict.Pass"/>).
    /// </summary>
    public bool IsPass => Verdict == Vouchfx.Engine.Abstractions.Verdict.Pass;
}
