// Platform.Engine.Abstractions — VerifyMode (§2 DSL, §13.3.1).
//
// Controls whether the engine evaluates a step's assertion once on execution
// or polls with backoff until a deadline.  The canonical DSL wire tokens are
// the member names upper-cased: Immediate ↔ "IMMEDIATE", Retry ↔ "RETRY".

namespace Platform.Engine.Abstractions;

/// <summary>
/// Controls the assertion-evaluation strategy for a step (§2 DSL, §13.3.1).
/// </summary>
/// <remarks>
/// <para>
/// The canonical DSL wire token for each member is its name upper-cased.
/// For example, <see cref="Immediate"/> maps to <c>"IMMEDIATE"</c> in the
/// <c>.e2e.yaml</c> file and in JSON event payloads, and <see cref="Retry"/>
/// maps to <c>"RETRY"</c>.
/// </para>
/// <para>
/// A <see cref="System.Text.Json.Serialization.JsonConverter{T}"/> is not
/// provided at this layer; consumers that need JSON serialisation should derive
/// the token with <c>member.ToString().ToUpperInvariant()</c> or register a
/// converter on their own <see cref="System.Text.Json.JsonSerializerOptions"/>
/// instance.
/// </para>
/// </remarks>
public enum VerifyMode
{
    /// <summary>
    /// The step's assertion is evaluated exactly once at execution time.
    /// This is the default strategy.  DSL wire token: <c>"IMMEDIATE"</c>.
    /// </summary>
    Immediate,

    /// <summary>
    /// The engine polls the assertion with configurable backoff until it passes
    /// or the step's <c>timeout</c> is exceeded.  Authors must never write
    /// <c>Thread.Sleep</c>; the polling loop is entirely engine-owned.
    /// DSL wire token: <c>"RETRY"</c>.
    /// </summary>
    Retry,
}
