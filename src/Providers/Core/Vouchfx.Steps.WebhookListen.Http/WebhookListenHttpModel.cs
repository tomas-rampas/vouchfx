// Vouchfx.Steps.WebhookListen.Http — webhook-listen.http step model (DSL §5, §13).
// Strongly-typed records; Dictionary<string,object> is explicitly prohibited (§13).
//
// This is the SIXTH and final Core provider (S07-F-01b) and the asserting half of the
// host-listener foundation (C1, S07-F-01a):
//   • The C1 wiring stands up an ephemeral host-owned HTTP listener (Default ALC, owned
//     by the runner) for each declared `webhook-listener` HostResourceRequirement, stages
//     its bound URL at svc::<Listener> (and at the plain Vars[<Listener>] key so authors
//     can interpolate {<Listener>}), and exposes the inbound requests it captures through
//     ScriptGlobalVariables.Webhooks.GetCaptured(<Listener>).
//   • THIS provider declares the listener (via IHostResourceContributor) and, at a later
//     assertion step, reads those captures and asserts a request matching the declared
//     criteria has arrived.  The provider owns NO disposable resource — the listener and
//     its capture buffer are host-owned; the provider merely snapshots them.
using Vouchfx.Sdk;

namespace Vouchfx.Steps.WebhookListen.Http;

/// <summary>
/// Strongly-typed model for the <c>webhook-listen.http</c> step kind (DSL §5).
/// </summary>
/// <remarks>
/// <para>
/// The step asserts that a host-owned ephemeral webhook listener (named by
/// <see cref="Listener"/>) has captured at least one inbound HTTP request satisfying the
/// declared <see cref="Match"/> criteria.  The listener is itself a <em>host resource</em>
/// this provider contributes (via <see cref="IStepModel"/>-bound
/// <c>IHostResourceContributor&lt;WebhookListenHttpModel&gt;</c>); the engine stands it up
/// before any step runs and stages its bound URL at <c>svc::&lt;Listener&gt;</c> and at the
/// plain <c>Vars[&lt;Listener&gt;]</c> key (S07-F-01a/b).
/// </para>
/// <para>
/// This provider is a <c>verifyMode: RETRY</c> consumer (DSL §7), mirroring
/// <c>mq-expect.kafka</c>: the engine wraps a RETRY step in a polling loop, so the emitted
/// assertion is an IDEMPOTENT single scan of the captured-request buffer.  It writes
/// <see cref="Vouchfx.Engine.Abstractions.Verdict.Fail"/> when no captured request matches
/// THIS attempt and contains NO retry logic itself — the engine-owned RetryRunner
/// re-invokes the delegate and converts a sustained Fail to
/// <see cref="Vouchfx.Engine.Abstractions.Verdict.Inconclusive"/> on timeout (§12.1).
/// </para>
/// <para>
/// Each declared criterion value in <see cref="Match"/> (method / path / header values /
/// bodyContains) supports <c>{placeholder}</c> substitution and
/// <c>${secret:source/path}</c> references; both are resolved at step-execution time inside
/// the emitted helper's guarded region (§17), never at compile time.
/// </para>
/// </remarks>
/// <param name="Listener">
/// The logical listener name whose URL is staged at <c>svc::&lt;Listener&gt;</c> and whose
/// captured inbound requests this step asserts against (read via
/// <c>ScriptGlobalVariables.Webhooks.GetCaptured(Listener)</c>).  Must not be empty.
/// </param>
/// <param name="Match">
/// The criteria a captured request must satisfy.  At least one criterion (method, path,
/// a non-empty headers map, or bodyContains) must be declared; an empty match is rejected
/// by validation.
/// </param>
public sealed record WebhookListenHttpModel(string Listener, WebhookMatch Match) : IStepModel;

/// <summary>
/// The criteria a captured inbound HTTP request must satisfy for the
/// <c>webhook-listen.http</c> assertion to pass (DSL §5).
/// </summary>
/// <remarks>
/// <para>
/// All criteria are conjunctive (logical AND): a captured request matches only when every
/// declared criterion is satisfied.  Absent (<see langword="null"/> or empty) criteria
/// impose no constraint, and a match with no declared criteria is rejected by validation.
/// </para>
/// <para>
/// The captured <c>Path</c> is already token-stripped/SUT-facing (the host listener strips
/// the unguessable token segment it prepends to the staged URL), so an author writes
/// <see cref="Path"/> exactly as the system under test calls back (e.g.
/// <c>/callbacks/order</c>) without knowing the token (S07-F-01a).
/// </para>
/// </remarks>
/// <param name="Method">
/// Optional expected HTTP method (e.g. <c>"POST"</c>).  When set, a matching request's
/// method must equal this value (case-insensitive).  May contain <c>{placeholder}</c> and
/// <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Path">
/// Optional expected request path (the token-stripped, SUT-facing path).  When set, a
/// matching request's captured path must equal this value (ordinal).  May contain
/// <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
/// <param name="Headers">
/// Optional map of expected header names to their expected string values.  When non-empty,
/// every named header must be present on the captured request and its value must equal the
/// expected value (case-insensitive header-name lookup, ordinal value comparison).  Each
/// expected value may contain <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens
/// resolved at runtime; header names are used verbatim.
/// </param>
/// <param name="BodyContains">
/// Optional substring the captured request body must contain (ordinal).  May contain
/// <c>{placeholder}</c> and <c>${secret:source/path}</c> tokens resolved at runtime.
/// </param>
public sealed record WebhookMatch(
    string? Method,
    string? Path,
    IReadOnlyDictionary<string, string>? Headers,
    string? BodyContains);
