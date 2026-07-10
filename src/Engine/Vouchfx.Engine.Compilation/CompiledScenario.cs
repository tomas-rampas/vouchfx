// Vouchfx.Engine.Compilation — CompiledScenario (§5, §13).
// Pairs the emitted PE image with the step-id list from the originating
// AssembledScript so callers can correlate compiled artefacts to YAML steps.
namespace Vouchfx.Engine.Compilation;

/// <summary>
/// An immutable value that pairs a <see cref="CompiledScript"/> with the ordered
/// list of step identifiers whose <see cref="Vouchfx.Sdk.CsxFragment"/>s were
/// merged to produce it.
/// </summary>
/// <remarks>
/// <para>
/// Produced by <see cref="RoslynScriptCompiler.CompileScenario"/>, which is thin
/// sugar over <see cref="RoslynScriptCompiler.CompileOnce"/>.  The
/// <see cref="Compiled"/> image is safe to share across threads and may be passed
/// to <see cref="RoslynScriptCompiler.RunIsolatedAsync"/> any number of times.
/// </para>
/// <para>
/// If you already hold a <see cref="CompiledScript"/> from a direct
/// <see cref="RoslynScriptCompiler.CompileOnce"/> call, you can construct this
/// record directly rather than recompiling.
/// </para>
/// </remarks>
/// <param name="Compiled">
/// The emitted portable-executable image produced by
/// <see cref="RoslynScriptCompiler.CompileOnce"/>.
/// </param>
/// <param name="StepIds">
/// The step identifiers in the order they were passed to
/// <see cref="CsxAssembler.Assemble(IReadOnlyList{StepCompilePlan})"/>, preserved so callers can correlate
/// execution results back to originating YAML steps.
/// </param>
public sealed record CompiledScenario(CompiledScript Compiled, IReadOnlyList<string> StepIds);
