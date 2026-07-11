// Vouchfx.Steps.Script.Csharp — ScriptCsharpModel (DSL §13).
// Strongly-typed record for the script.csharp step kind.
// Models are records, never Dictionary<string,object> (§13 invariant).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.Script.Csharp;

/// <summary>
/// Strongly-typed model for the <c>script.csharp</c> step kind.
/// </summary>
/// <param name="Code">
/// The inline C# body supplied by the test author, or <see langword="null"/>
/// when the step uses <paramref name="File"/> instead.
/// Executed inside the compiled CSX submission with access to the shared
/// <c>Vars</c> dictionary.  Exactly one of <see cref="Code"/> or
/// <see cref="File"/> must be set; the validator rejects both-set,
/// neither-set, and blank values.
/// </param>
/// <param name="File">
/// The path (relative to the scenario's own directory) of an external
/// <c>.csx</c> file supplied instead of <paramref name="Code"/>. Read once at
/// compile time and spliced verbatim into the compiled CSX submission,
/// identically to <see cref="Code"/> — it is not re-read per execution and
/// participates in no runtime substitution.
/// </param>
public sealed record ScriptCsharpModel(string? Code, string? File) : IStepModel;
