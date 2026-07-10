// Vouchfx.Steps.Script.Csharp — ScriptCsharpModel (DSL §13).
// Strongly-typed record for the script.csharp step kind.
// Models are records, never Dictionary<string,object> (§13 invariant).
using Vouchfx.Sdk;

namespace Vouchfx.Steps.Script.Csharp;

/// <summary>
/// Strongly-typed model for the <c>script.csharp</c> step kind.
/// </summary>
/// <param name="Code">
/// The inline C# body supplied by the test author.
/// Executed inside the compiled CSX submission with access to the shared
/// <c>Vars</c> dictionary.  May be empty only at the binding stage; the
/// validator rejects empty values.
/// </param>
public sealed record ScriptCsharpModel(string Code) : IStepModel;
