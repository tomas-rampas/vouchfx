// Known event-type discriminator strings for the vouchfx structured event
// stream (§14.4).  Use these constants at call sites instead of raw string
// literals to guard against typos and make grep-ability straightforward.
//
// New event types introduced by the engine after Sprint 1 should be added here
// before they appear in production code.  Sprint-1 deliverable: envelope only;
// full payload types land in Sprint 2.

namespace Platform.Engine.Abstractions.Events;

/// <summary>
/// Canonical event-type discriminator strings for the structured JSON Lines
/// event stream (§14.4).
/// </summary>
public static class EventTypes
{
    /// <summary>Emitted once when a suite run begins.</summary>
    public const string SuiteStarted = "suite-started";

    /// <summary>Emitted once when a suite run finishes.</summary>
    public const string SuiteCompleted = "suite-completed";

    /// <summary>Emitted when a scenario begins execution.</summary>
    public const string ScenarioStarted = "scenario-started";

    /// <summary>Emitted when a scenario finishes execution.</summary>
    public const string ScenarioCompleted = "scenario-completed";

    /// <summary>Emitted when a step begins its first (or only) attempt.</summary>
    public const string StepStarted = "step-started";

    /// <summary>
    /// Emitted for every individual attempt of a step.  RETRY steps emit one
    /// <c>step-attempt</c> per polling cycle, which is what makes the polling
    /// timeline renderable without re-running the suite (§14.5).
    /// </summary>
    public const string StepAttempt = "step-attempt";

    /// <summary>
    /// Emitted when a step is fully resolved — whether by success, exhausted
    /// retries, timeout, or environment error.
    /// </summary>
    public const string StepCompleted = "step-completed";

    /// <summary>
    /// Emitted when the orchestration layer encounters an infrastructure failure
    /// (image-pull error, health-gate timeout, discovery failure, or provisioning
    /// error) that prevents the scenario from running at all (§12.1 Environment
    /// error verdict).  This event is <em>never</em> emitted with a
    /// <c>Fail</c> verdict — only <c>ENV_ERROR</c>.
    /// </summary>
    public const string EnvironmentError = "environment-error";
}
