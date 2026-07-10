// Vouchfx.Engine.Abstractions — AttemptRecord (§7 verifyMode RETRY, §12.1, §14).
//
// Plain data record produced once per attempt by the engine-owned RETRY runner
// (RetryRunner.PollAsync) and written, as a List<AttemptRecord>, into
// ScriptGlobalVariables.Vars under the key produced by VarKeys.Attempts(stepId).
// The engine runner reads that list back to emit one step-attempt event per
// record, which is what makes a RETRY polling timeline renderable without
// re-running the suite (§14).
//
// Memory-model note: this record must remain a pure data type — no static members,
// no references to engine internals.  When the collectible AssemblyLoadContext
// unloads, the instance is an ordinary managed object rooted only through the
// host's Vars dictionary and is collected by the next GC cycle.

using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Engine.Abstractions.Retry;

/// <summary>
/// Immutable data record that captures a single attempt made by the engine-owned
/// RETRY runner while polling a step under <c>verifyMode: RETRY</c> (§7).
/// </summary>
/// <remarks>
/// <para>
/// The RETRY runner records exactly one <see cref="AttemptRecord"/> per invocation
/// of the step delegate and accumulates them in a <see cref="System.Collections.Generic.List{T}"/>
/// written into <c>ScriptGlobalVariables.Vars</c> under the key
/// <c>VarKeys.Attempts(sanitisedStepId)</c>.  After the runner returns, the engine
/// reads that list to emit one <c>step-attempt</c> event per record into the
/// reporting stream, so the RETRY backoff timeline can be rendered offline (§14).
/// </para>
/// <para>
/// <strong>Memory-model invariant (§5):</strong> this record must remain a pure data
/// type with no static members and no references to engine types that live outside
/// the collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.  When
/// the context is unloaded the instance survives only as an ordinary managed object
/// held by the host's <c>Vars</c> dictionary, allowing the GC to collect the
/// emitted assembly normally.
/// </para>
/// </remarks>
/// <param name="Attempt">
/// The 1-based ordinal of this attempt within the polling loop (the first attempt
/// is <c>1</c>).
/// </param>
/// <param name="TMs">
/// Wall-clock duration of <em>this attempt alone</em>, in milliseconds, as measured
/// by the runner's per-attempt stopwatch.  Distinct from the overall polling window.
/// </param>
/// <param name="Verdict">
/// The verdict produced by this attempt (§12.1).  A thrown attempt is recorded as
/// <see cref="Abstractions.Verdict.EnvironmentError"/>.
/// </param>
/// <param name="Observation">
/// Optional small JSON string carrying human-readable diagnostic context for this
/// attempt, for example <c>{"status":503}</c> or, for a thrown attempt,
/// <c>{"error":"connection refused"}</c>.  May be <see langword="null"/>.
/// </param>
public sealed record AttemptRecord(int Attempt, long TMs, Verdict Verdict, string? Observation);
