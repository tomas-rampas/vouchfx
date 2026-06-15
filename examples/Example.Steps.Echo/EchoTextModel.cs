// Example.Steps.Echo — EchoTextModel (worked example, S10-F-01).
//
// The strongly-typed model for the `echo.text` step.  Models are RECORDS that implement
// IStepModel — never Dictionary<string,object> (§13 invariant).  An external contributor
// copies this shape: one immutable record per step kind, one property per author-supplied
// field.
using Platform.Sdk;

namespace Example.Steps.Echo;

/// <summary>
/// Strongly-typed model for the worked-example <c>echo.text</c> step kind.
/// </summary>
/// <remarks>
/// This is the canonical shape for a provider model (§13): an immutable
/// <see langword="record"/> implementing <see cref="IStepModel"/>, with one property per
/// field the test author writes in the <c>.e2e.yaml</c> step.  Using a record (rather than
/// a loosely-typed dictionary) is what gives the binder, validator and compiler a
/// compile-time-checked contract to work against.
/// </remarks>
/// <param name="Text">
/// The text the step echoes at execution time.  Bound from the step's <c>text</c> field.
/// May contain <c>{placeholder}</c> tokens, which are resolved against the <c>Vars</c>
/// global at step-execution time (cross-step state threads forward via <c>Vars</c>).
/// </param>
/// <param name="Expect">
/// The constant the step asserts the (placeholder-resolved) <see cref="Text"/> equals.
/// Bound from the step's <c>expect</c> field, which is required: <c>echo.text</c> always
/// makes a real assertion, so there is no "bare echo" mode.
/// </param>
public sealed record EchoTextModel(string Text, string Expect) : IStepModel;
