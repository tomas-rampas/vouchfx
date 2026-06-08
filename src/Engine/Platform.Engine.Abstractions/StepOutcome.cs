// Platform.Engine.Abstractions — StepOutcome (§5, §12.1, §13.3.1, §14).
//
// Plain data record written by an emitted CSX step block into ScriptGlobalVariables.Vars
// under the key produced by VarKeys.Outcome(stepId), and read back by the engine runner
// after RunIsolatedAsync to emit step-completed events.
//
// Memory-model note: this record must remain a pure data type — no static members,
// no references to engine internals.  When the collectible AssemblyLoadContext unloads,
// the instance is an ordinary managed object rooted only through the host's Vars
// dictionary and is collected by the next GC cycle.

namespace Platform.Engine.Abstractions;

/// <summary>
/// Immutable data record that captures the outcome of a single emitted step block.
/// </summary>
/// <remarks>
/// <para>
/// An emitted CSX step block constructs a <see cref="StepOutcome"/> and writes it
/// into <c>ScriptGlobalVariables.Vars</c> under the key
/// <c>VarKeys.Outcome(sanitisedStepId)</c>.  After <c>RunIsolatedAsync</c> returns,
/// the engine runner reads this key to emit a <c>step-completed</c> event into the
/// reporting stream (§14).
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
/// <param name="Verdict">
/// The step's outcome verdict as defined in §12.1.
/// </param>
/// <param name="DurationMs">
/// Wall-clock duration of the step execution in milliseconds.
/// </param>
/// <param name="Observation">
/// Optional small JSON string providing human-readable diagnostic context,
/// for example <c>{"status":200,"expected":200}</c>.  May be
/// <see langword="null"/> when no additional detail is available.
/// </param>
public sealed record StepOutcome(Verdict Verdict, long DurationMs, string? Observation);
