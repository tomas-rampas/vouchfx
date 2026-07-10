// Verdict taxonomy for the vouchfx engine (§12.1).
//
// Four outcomes, kept separate everywhere — taxonomy, reporting, exit codes.
// Only Fail breaks CI by default.  Conflating an environment error with a
// defect destroys trust in the tool.

namespace Vouchfx.Engine.Abstractions;

/// <summary>
/// The four possible outcomes of a step or scenario execution (§12.1).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>
///     <see cref="Pass"/> — the step or scenario met all its assertions.
///   </description></item>
///   <item><description>
///     <see cref="Fail"/> — the step or scenario failed its assertions.
///     <strong>Only this outcome breaks CI by default.</strong>
///   </description></item>
///   <item><description>
///     <see cref="EnvironmentError"/> — infrastructure failure: unhealthy
///     container, image-pull failure, seed failure, tunnel error.  The system
///     under test was never exercised; the defect is in the environment, not
///     the product.
///   </description></item>
///   <item><description>
///     <see cref="Inconclusive"/> — timeout, partition outlasted the grace
///     period, or an upstream capture was unmet.  The engine cannot determine
///     correctness.
///   </description></item>
/// </list>
/// </remarks>
public enum Verdict
{
    /// <summary>All assertions were met.</summary>
    Pass,

    /// <summary>One or more assertions failed.  Breaks CI by default.</summary>
    Fail,

    /// <summary>
    /// Infrastructure failure (container unhealthy, image pull, seed, tunnel).
    /// The system under test was not exercised.
    /// </summary>
    EnvironmentError,

    /// <summary>
    /// The engine could not determine correctness (timeout, partition grace
    /// period exceeded, upstream capture unmet).
    /// </summary>
    Inconclusive,
}
