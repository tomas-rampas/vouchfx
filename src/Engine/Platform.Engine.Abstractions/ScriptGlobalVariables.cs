// Platform.Engine.Abstractions — ScriptGlobalVariables (§5, §13.3.1).
// This is the SOLE typed bridge between the vouchfx host and any emitted script delegate.
// Rule: no static members — the boundary must stay clean so the collectible AssemblyLoadContext
// has nothing rooting the emitted assembly back into the Default context.
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Abstractions.Webhooks;

namespace Platform.Engine.Abstractions;

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
    /// Initialises a new instance with caller-supplied dictionaries, secret accessor,
    /// and webhook-capture accessor (the full host↔script boundary, S07-F-01a).
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
    {
        Vars = vars ?? throw new ArgumentNullException(nameof(vars));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        Webhooks = webhooks ?? throw new ArgumentNullException(nameof(webhooks));
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
