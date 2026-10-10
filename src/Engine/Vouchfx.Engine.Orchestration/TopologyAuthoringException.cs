// Typed refusal channel from explicit pre-mutation authoring/policy guards. Eager Map
// validation performs no infrastructure I/O; SuiteTopology wraps only that boundary's
// ArgumentExceptions. Configure may raise it before Build; Compose raises it at named
// input/ownership/local-daemon guards before journalling/up, even though StartAsync runs.
// Native output/process errors and post-up failures retain their ordinary taxonomy.

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// Identifies a deterministic authoring/policy refusal at an explicit mapper or adapter
/// guard before resource mutation (§12.1).
/// </summary>
/// <remarks>
/// <para>
/// Deriving from <see cref="ArgumentException"/> preserves the existing Inconclusive
/// classification. The distinct type additionally proves authoring provenance, so a passing
/// sibling cannot hide the refusal and a generic driver exception is not assigned the same
/// no-start security evidence. Invocation of an acquisition method is not proof that a
/// resource was started. <c>ProjectServiceEndpointStagingTests</c> pins the
/// existing closure classification.
/// </para>
/// <para>
/// <strong>It is not a general-purpose escape hatch.</strong> <c>SuiteTopology.StartAsync</c>
/// re-throws this type unwrapped, ahead of the blanket wrap that turns everything else into
/// an <see cref="OrchestrationException"/>. Throw it only where the fault is deterministic,
/// reproducible, and fixable by editing authored inputs or selecting a supported local
/// Docker context — never
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
