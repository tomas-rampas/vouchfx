// Known event-type discriminator strings for the vouchfx structured event
// stream (§14.4).  Use these constants at call sites instead of raw string
// literals to guard against typos and make grep-ability straightforward.
//
// New event types introduced by the engine after Sprint 1 should be added here
// before they appear in production code.  Sprint-1 deliverable: envelope only;
// full payload types land in Sprint 2.

namespace Vouchfx.Engine.Abstractions.Events;

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

    /// <summary>
    /// Emitted when the engine has a transport advisory to make about the endpoint
    /// a targeted service is addressed on — the engine selected a plaintext listener
    /// while an https one was available, or the run addresses an https listener the
    /// engine configures no client trust for.  Carries no verdict: it is advisory,
    /// and the run's outcome is decided elsewhere (§12.1).
    /// </summary>
    /// <remarks>
    /// The advisories these events carry are printed to the terminal by their own route;
    /// the event exists so that a CI job consuming <c>--events</c> can see it too,
    /// which matters most when a failed handshake surfaces as an environment error
    /// and exits 0 by default.  The two advisories share one event type and are
    /// discriminated by <see cref="TransportNoticeEvent.Kind"/> — see
    /// <see cref="TransportNoticeKinds"/> — so that they cannot drift apart in the
    /// artefacts the way they did between the terminal and the stream.
    /// </remarks>
    public const string TransportNotice = "transport-notice";

    /// <summary>
    /// Emitted once per scenario carrying the reproducibility envelope (§17,
    /// docs/02 §3.2.2): a hash of every distinct secret <em>reference</em> and the
    /// content hash of every applied seed fixture.  By construction it carries no
    /// resolved secret value (the secret resolver is never invoked to build it).
    /// </summary>
    public const string ReproducibilityEnvelope = "reproducibility-envelope";
}
