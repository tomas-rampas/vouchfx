// Example.Steps.Hello — HelloConsoleModel (worked example, S08 T5a).
//
// The strongly-typed model for the `hello.console` step.  Models are RECORDS that
// implement IStepModel — never Dictionary<string,object> (§13 invariant).  An
// external contributor copies this shape: one immutable record per step kind, one
// property per author-supplied field.
using Vouchfx.Sdk;

namespace Example.Steps.Hello;

/// <summary>
/// Strongly-typed model for the worked-example <c>hello.console</c> step kind.
/// </summary>
/// <remarks>
/// This is the canonical shape for a provider model (§13): an immutable
/// <see langword="record"/> implementing <see cref="IStepModel"/>, with one
/// property per field the test author writes in the <c>.e2e.yaml</c> step.  Using
/// a record (rather than a loosely-typed dictionary) is what gives the binder,
/// validator and compiler a compile-time-checked contract to work against.
/// </remarks>
/// <param name="Message">
/// The greeting the step emits at execution time.  Bound from the step's
/// <c>message</c> field; the validator rejects an empty value.
/// </param>
/// <param name="Expected">
/// The constant the step asserts the emitted <see cref="Message"/> equals.  Bound
/// from the optional <c>expect</c> field; when omitted it defaults to
/// <see cref="Message"/>, so a bare emit (no assertion) always passes.
/// </param>
public sealed record HelloConsoleModel(string Message, string Expected) : IStepModel;
