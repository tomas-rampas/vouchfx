// Vouchfx.Engine.Abstractions — ScriptGlobalVariables (§5, §13.3.1).
// This is the SOLE typed bridge between the vouchfx host and any emitted script delegate.
// Rule: no static members — the boundary must stay clean so the collectible AssemblyLoadContext
// has nothing rooting the emitted assembly back into the Default context.
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Abstractions.Traces;
using Vouchfx.Engine.Abstractions.Webhooks;

namespace Vouchfx.Engine.Abstractions;

/// <summary>
/// The sole bridge between the vouchfx host and an emitted script delegate.
/// Every piece of state the script may observe or mutate passes through this object.
/// No static members are permitted; doing so would root the collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> back into the Default context
/// and prevent unloading — defeating the entire memory model (§5).
/// </summary>
public sealed class ScriptGlobalVariables
{
    /// <summary>
    /// Mutable per-run state dictionary.  Steps read previously captured values and write
    /// new ones here; <c>{placeholder}</c> substitution resolves against this map at
    /// step-execution time (§6, §13.3.1).
    /// </summary>
    public IDictionary<string, object?> Vars { get; }

    /// <summary>
    /// Typed client surface provided by the orchestration layer.
    /// Steps obtain strongly-typed clients (e.g. <c>HttpClient</c>, <c>NpgsqlConnection</c>)
    /// by key.  The surface is intentionally empty in Sprint 1; the full provider SDK (§13)
    /// populates it in later sprints.
    /// </summary>
    public IReadOnlyDictionary<string, object> Services { get; }

    /// <summary>
    /// The execution-time secret accessor (§17).  Emitted step blocks resolve
    /// <c>${secret:source/path}</c> references through this member at the moment a
    /// value is fed into an injection sink — never at compile time, so no secret
    /// value is ever baked into the emitted IL.
    /// </summary>
    /// <remarks>
    /// This is an <em>instance</em> property by design: the secrets subsystem must
    /// never expose a static handle across the boundary, as a static would root the
    /// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> back into
    /// the Default context and defeat the memory model (§5).  Legacy constructors
    /// populate this with a <see cref="NullSecretAccessor"/> that throws on use.
    /// </remarks>
    public ISecretAccessor Secrets { get; }

    /// <summary>
    /// The execution-time webhook-capture accessor (§5, S07-F-01a).  A later
    /// assertion step reads the inbound HTTP requests captured by a host-owned
    /// ephemeral webhook listener through this member, keyed by the listener's
    /// logical name — never through any static handle.
    /// </summary>
    /// <remarks>
    /// This is an <em>instance</em> property by design, exactly like
    /// <see cref="Secrets"/>: the webhook listener and its capture buffer are owned
    /// by the topology/runner in the <strong>Default</strong>
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, and the emitted script
    /// observes their captured requests only by-reference through this accessor.
    /// Exposing the listener, the buffer, or this accessor as a static would root the
    /// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> back into the
    /// Default context and defeat the memory model (§5) — so it must remain an instance
    /// passed in at construction.  The accessor is read-only: the script can observe
    /// captured requests but can never start, stop, or mutate a listener.  Legacy
    /// constructors populate this with a <see cref="NullWebhookCaptureAccessor"/> that
    /// returns an empty list, so a run with no listener never pays for one and every
    /// existing call site keeps compiling unchanged.
    /// </remarks>
    public IWebhookCaptureAccessor Webhooks { get; }

    /// <summary>
    /// The execution-time OTLP span-capture accessor (Phase C, §5, <c>trace-expect.otlp</c>).
    /// A later assertion step reads the spans exported over OTLP/HTTP to a host-owned
    /// ephemeral receiver through this member, keyed by the receiver's logical name — never
    /// through any static handle.
    /// </summary>
    /// <remarks>
    /// This is an <em>instance</em> property by design, exactly like <see cref="Webhooks"/>
    /// and <see cref="Secrets"/>: the OTLP receiver and its capture buffer are owned by the
    /// topology/runner in the <strong>Default</strong>
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, and the emitted script observes
    /// their captured spans only by-reference through this accessor. Exposing the receiver,
    /// the buffer, or this accessor as a static would root the collectible
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> back into the Default context
    /// and defeat the memory model (§5) — so it must remain an instance passed in at
    /// construction. The accessor is read-only: the script can observe captured spans but can
    /// never start, stop, or mutate a receiver. Legacy constructors populate this with a
    /// <see cref="NullTraceCaptureAccessor"/> that returns an empty list, so a run with no
    /// <c>trace-expect.otlp</c> step never pays for a receiver and every existing call site
    /// keeps compiling unchanged.
    /// </remarks>
    public ITraceCaptureAccessor Traces { get; }

    /// <summary>
    /// The host-side per-step live event sink (§14, issue #262). An emitted CSX step block
    /// reports its lifecycle (started / per-attempt / completed) through this member so a
    /// <c>--events-stream</c> tail can observe genuine per-step liveness DURING the isolated
    /// run, not only after it returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Secrets"/>, <see cref="Webhooks"/>, and <see cref="Traces"/>, this
    /// member's default is <see langword="null"/> — deliberately <strong>not</strong> a
    /// Null-object. The emitted CSX guards every call with <c>?.</c>
    /// (<c>StepEvents?.OnStepStarted(...)</c>), so a <see langword="null"/> sink is a pure
    /// no-op and the post-return reconstruction in the runner is behaviourally UNCHANGED —
    /// this is the common path (no <c>--events-stream</c> flag) and must allocate nothing.
    /// </para>
    /// <para>
    /// This is an <em>instance</em> property by design, exactly like <see cref="Secrets"/>,
    /// <see cref="Webhooks"/>, and <see cref="Traces"/>: the concrete sink is a Default-ALC
    /// object (built and owned by the runner) passed in by reference. Only Default-ALC data
    /// types (<see langword="string"/>, <see cref="StepOutcome"/>,
    /// <see cref="Retry.AttemptRecord"/>) ever cross this member's methods, so holding it
    /// never roots the collectible
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> the emitted script's submission
    /// assembly lives in (§5).
    /// </para>
    /// </remarks>
    public IStepEventSink? StepEvents { get; }

    /// <summary>
    /// The execution-time per-target CLIENT SECURITY CONFIGURATION accessor
    /// (authenticated-infrastructure-mtls, REQ-014).  An emitted step block resolves the
    /// configuration declared for its own <c>target</c> through this member — certificate
    /// paths for a client library that takes only paths, loaded certificate objects for one
    /// that takes only objects — and configures its transport from it at step-execution time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is an <em>instance</em> property by design, exactly like <see cref="Secrets"/>,
    /// <see cref="Webhooks"/> and <see cref="Traces"/>: the accessor and the certificate
    /// objects it owns live in the <strong>Default</strong>
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> (built and owned by the
    /// runner), and the emitted script reaches them only by-reference through this member.
    /// A static handle would root the collectible context back into the Default context and
    /// defeat the memory model (§5).
    /// </para>
    /// <para>
    /// The material this exposes is deliberately NOT reachable through <see cref="Vars"/>
    /// under any prefix (REQ-014): <see cref="Vars"/> feeds the reported and §14 event
    /// surface, and a certificate or key path written there would leak past the
    /// <c>SecretString</c> redaction model.  Legacy constructors populate this with a
    /// <see cref="NullSecurityConfigurationAccessor"/> whose every lookup returns
    /// <see langword="null"/>, so a run declaring no <c>security</c> block pays nothing and
    /// every existing call site keeps compiling unchanged.
    /// </para>
    /// </remarks>
    public ISecurityConfigurationAccessor Security { get; }

    /// <summary>
    /// Initialises a new instance with caller-supplied dictionaries, secret accessor,
    /// webhook-capture accessor, OTLP trace-capture accessor, host-side step-event sink, and
    /// per-target security-configuration accessor (the full host↔script boundary).
    /// </summary>
    /// <param name="vars">
    /// Mutable state map; must not be <see langword="null"/>.
    /// </param>
    /// <param name="services">
    /// Read-only typed-client surface; must not be <see langword="null"/>.
    /// </param>
    /// <param name="secrets">
    /// The execution-time secret accessor; must not be <see langword="null"/>.
    /// </param>
    /// <param name="webhooks">
    /// The execution-time webhook-capture accessor; must not be <see langword="null"/>.
    /// </param>
    /// <param name="traces">
    /// The execution-time OTLP trace-capture accessor; must not be <see langword="null"/>.
    /// </param>
    /// <param name="stepEvents">
    /// The host-side per-step live event sink, or <see langword="null"/> when no live
    /// <c>--events-stream</c> conduit is configured for this run.
    /// </param>
    /// <param name="security">
    /// The execution-time per-target security-configuration accessor; must not be
    /// <see langword="null"/>.  Pass <see cref="NullSecurityConfigurationAccessor.Instance"/>
    /// when the run declares no <c>security</c> block.
    /// </param>
    public ScriptGlobalVariables(
        IDictionary<string, object?> vars,
        IReadOnlyDictionary<string, object> services,
        ISecretAccessor secrets,
        IWebhookCaptureAccessor webhooks,
        ITraceCaptureAccessor traces,
        IStepEventSink? stepEvents,
        ISecurityConfigurationAccessor security)
    {
        Vars = vars ?? throw new ArgumentNullException(nameof(vars));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        Webhooks = webhooks ?? throw new ArgumentNullException(nameof(webhooks));
        Traces = traces ?? throw new ArgumentNullException(nameof(traces));
        StepEvents = stepEvents;
        Security = security ?? throw new ArgumentNullException(nameof(security));
    }

    /// <summary>
    /// Initialises a new instance with caller-supplied dictionaries, secret accessor,
    /// webhook-capture accessor, OTLP trace-capture accessor, and host-side step-event sink
    /// (the full host↔script boundary, issue #262), and no declared security configuration.
    /// <see cref="Security"/> is a <see cref="NullSecurityConfigurationAccessor"/>.
    /// </summary>
    /// <param name="vars">
    /// Mutable state map; must not be <see langword="null"/>.
    /// </param>
    /// <param name="services">
    /// Read-only typed-client surface; must not be <see langword="null"/>.
    /// </param>
    /// <param name="secrets">
    /// The execution-time secret accessor; must not be <see langword="null"/>.
    /// </param>
    /// <param name="webhooks">
    /// The execution-time webhook-capture accessor; must not be <see langword="null"/>.
    /// Pass <see cref="NullWebhookCaptureAccessor.Instance"/> when the run declares no
    /// webhook listener.
    /// </param>
    /// <param name="traces">
    /// The execution-time OTLP trace-capture accessor; must not be <see langword="null"/>.
    /// Pass <see cref="NullTraceCaptureAccessor.Instance"/> when the run declares no
    /// <c>trace-expect.otlp</c> step.
    /// </param>
    /// <param name="stepEvents">
    /// The host-side per-step live event sink, or <see langword="null"/> when no live
    /// <c>--events-stream</c> conduit is configured for this run (the common case — every
    /// call the emitted CSX makes is guarded with <c>?.</c>, so a <see langword="null"/> sink
    /// costs nothing and changes no behaviour).
    /// </param>
    public ScriptGlobalVariables(
        IDictionary<string, object?> vars,
        IReadOnlyDictionary<string, object> services,
        ISecretAccessor secrets,
        IWebhookCaptureAccessor webhooks,
        ITraceCaptureAccessor traces,
        IStepEventSink? stepEvents)
        : this(vars, services, secrets, webhooks, traces, stepEvents, NullSecurityConfigurationAccessor.Instance)
    {
    }

    /// <summary>
    /// Initialises a new instance with caller-supplied dictionaries, secret accessor,
    /// webhook-capture accessor, and OTLP trace-capture accessor (the full host↔script
    /// boundary, Phase C), and no host-side step-event sink. <see cref="StepEvents"/> is
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="vars">
    /// Mutable state map; must not be <see langword="null"/>.
    /// </param>
    /// <param name="services">
    /// Read-only typed-client surface; must not be <see langword="null"/>.
    /// </param>
    /// <param name="secrets">
    /// The execution-time secret accessor; must not be <see langword="null"/>.
    /// </param>
    /// <param name="webhooks">
    /// The execution-time webhook-capture accessor; must not be <see langword="null"/>.
    /// Pass <see cref="NullWebhookCaptureAccessor.Instance"/> when the run declares no
    /// webhook listener.
    /// </param>
    /// <param name="traces">
    /// The execution-time OTLP trace-capture accessor; must not be <see langword="null"/>.
    /// Pass <see cref="NullTraceCaptureAccessor.Instance"/> when the run declares no
    /// <c>trace-expect.otlp</c> step.
    /// </param>
    public ScriptGlobalVariables(
        IDictionary<string, object?> vars,
        IReadOnlyDictionary<string, object> services,
        ISecretAccessor secrets,
        IWebhookCaptureAccessor webhooks,
        ITraceCaptureAccessor traces)
        : this(vars, services, secrets, webhooks, traces, stepEvents: null)
    {
    }

    /// <summary>
    /// Initialises a new instance with caller-supplied dictionaries, secret accessor,
    /// and webhook-capture accessor (the full host↔script boundary, S07-F-01a), and no
    /// configured OTLP receivers. <see cref="Traces"/> is a <see cref="NullTraceCaptureAccessor"/>
    /// that returns an empty list.
    /// </summary>
    /// <param name="vars">
    /// Mutable state map; must not be <see langword="null"/>.
    /// </param>
    /// <param name="services">
    /// Read-only typed-client surface; must not be <see langword="null"/>.
    /// </param>
    /// <param name="secrets">
    /// The execution-time secret accessor; must not be <see langword="null"/>.
    /// </param>
    /// <param name="webhooks">
    /// The execution-time webhook-capture accessor; must not be <see langword="null"/>.
    /// Pass <see cref="NullWebhookCaptureAccessor.Instance"/> when the run declares no
    /// webhook listener.
    /// </param>
    public ScriptGlobalVariables(
        IDictionary<string, object?> vars,
        IReadOnlyDictionary<string, object> services,
        ISecretAccessor secrets,
        IWebhookCaptureAccessor webhooks)
        : this(vars, services, secrets, webhooks, NullTraceCaptureAccessor.Instance)
    {
    }

    /// <summary>
    /// Initialises a new instance with caller-supplied dictionaries and secret accessor,
    /// and no configured webhook listeners.  <see cref="Webhooks"/> is a
    /// <see cref="NullWebhookCaptureAccessor"/> that returns an empty list.
    /// </summary>
    /// <param name="vars">
    /// Mutable state map; must not be <see langword="null"/>.
    /// </param>
    /// <param name="services">
    /// Read-only typed-client surface; must not be <see langword="null"/>.
    /// </param>
    /// <param name="secrets">
    /// The execution-time secret accessor; must not be <see langword="null"/>.
    /// </param>
    public ScriptGlobalVariables(
        IDictionary<string, object?> vars,
        IReadOnlyDictionary<string, object> services,
        ISecretAccessor secrets)
        : this(vars, services, secrets, NullWebhookCaptureAccessor.Instance)
    {
    }

    /// <summary>
    /// Initialises a new instance with caller-supplied dictionaries and no configured
    /// secret sources.  <see cref="Secrets"/> is a <see cref="NullSecretAccessor"/>
    /// that throws on any resolution attempt.
    /// </summary>
    /// <param name="vars">
    /// Mutable state map; must not be <see langword="null"/>.
    /// </param>
    /// <param name="services">
    /// Read-only typed-client surface; must not be <see langword="null"/>.
    /// </param>
    public ScriptGlobalVariables(
        IDictionary<string, object?> vars,
        IReadOnlyDictionary<string, object> services)
        : this(vars, services, NullSecretAccessor.Instance)
    {
    }

    /// <summary>
    /// Convenience constructor for tests and simple PoC invocations that do not need
    /// a pre-populated service map or configured secret sources.
    /// </summary>
    /// <param name="vars">
    /// Mutable state map; must not be <see langword="null"/>.
    /// </param>
    public ScriptGlobalVariables(IDictionary<string, object?> vars)
        : this(vars, new Dictionary<string, object>(StringComparer.Ordinal), NullSecretAccessor.Instance)
    {
    }
}
