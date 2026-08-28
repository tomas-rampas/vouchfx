// ---------------------------------------------------------------------------
// TopologyAuthoringException
//
// The narrow, taxonomy-safe channel out of EnvironmentMapper.Map's Configure
// CLOSURE (§12.1).
//
// Map validates everything it can EAGERLY — before the closure, before any
// builder mutation — precisely so an authoring fault surfaces as an
// ArgumentException that ScenarioRunner classifies as Inconclusive.  A handful
// of facts are not knowable that early: they exist only once Aspire itself has
// built the resource inside the closure.  That — not any particular fault — is
// the membership rule for this type: the author can fix it, and nothing outside
// Aspire can decide it without reimplementing the discovery.  The standing
// example is what endpoints a `project:`-form service's launch profile
// produces, which is the question behind every throw site so far.
//
// FOR THE CURRENT SET, GREP `throw new TopologyAuthoringException`.  A roster
// written out here goes stale the first time one is added, which is exactly
// what happened to the roster this comment used to carry; the property above
// does not.  Note in particular that membership implies nothing about step
// TARGETING: some of these throws are gated on it and some deliberately are
// not, because a false statement the author wrote is a fault whether or not
// anything reads the service.
//
// Every OTHER throw from inside that closure is an engine defect or an infra
// fault, and SuiteTopology.StartAsync's `catch (Exception ex)` correctly wraps
// those as OrchestrationException → EnvironmentError.  This type is how an
// AUTHORING fault discovered in the closure opts out of that wrap without
// widening it for anything else.
// ---------------------------------------------------------------------------

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Thrown from <c>EnvironmentMapper.Map</c>'s <c>Configure</c> closure when a fault that
/// could only be discovered after Aspire built the resource is nevertheless the AUTHOR's
/// to fix — not an infrastructure failure (§12.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The base class is load-bearing, not decoration.</strong> Deriving from
/// <see cref="ArgumentException"/> is what routes this to <c>ScenarioRunner</c>'s existing
/// <c>catch (ArgumentException)</c>, which returns <c>Verdict.Inconclusive</c> through
/// <c>CompleteWithoutTopologyAsync</c> — and therefore, via that path's
/// <c>ExecutedAnyScenario = false</c>, to a non-zero exit code (#369). Re-base this on
/// <see cref="System.Exception"/> and the fault silently becomes an
/// <c>EnvironmentError</c> that exits 0: green CI over a suite that never ran. The
/// classification is pinned by <c>ProjectServiceEndpointStagingTests</c>.
/// </para>
/// <para>
/// <strong>It is not a general-purpose escape hatch.</strong> <c>SuiteTopology.StartAsync</c>
/// re-throws this type unwrapped, ahead of the blanket wrap that turns everything else into
/// an <see cref="OrchestrationException"/>. Throw it only where the fault is deterministic,
/// reproducible, and fixable by editing the suite (or the project the suite names) — never
/// for a container, image, network, or engine-internal failure, all of which the taxonomy
/// reserves for <c>EnvironmentError</c>.
/// </para>
/// </remarks>
public sealed class TopologyAuthoringException : ArgumentException
{
    /// <summary>Initialises a new instance with no message.</summary>
    public TopologyAuthoringException()
    {
    }

    /// <summary>Initialises a new instance with an author-facing message.</summary>
    /// <param name="message">The diagnostic shown to the suite author.</param>
    public TopologyAuthoringException(string? message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with an author-facing message and an inner exception.</summary>
    /// <param name="message">The diagnostic shown to the suite author.</param>
    /// <param name="innerException">The underlying exception, if any.</param>
    public TopologyAuthoringException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initialises a new instance with an author-facing message and the name of the offending
    /// parameter.
    /// </summary>
    /// <param name="message">The diagnostic shown to the suite author.</param>
    /// <param name="paramName">The name of the parameter that caused the failure.</param>
    public TopologyAuthoringException(string? message, string? paramName)
        : base(message, paramName)
    {
    }
}
