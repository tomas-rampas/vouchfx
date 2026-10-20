namespace Vouchfx.Engine.Compilation;

/// <summary>Overrides compilation for one asynchronous host invocation and its child tasks.</summary>
public sealed class ScriptCompilerScope : IDisposable
{
    private static readonly AsyncLocal<Func<string, IReadOnlyList<string>?, CancellationToken, CompiledScript>?> Current = new();
    private readonly Func<string, IReadOnlyList<string>?, CancellationToken, CompiledScript>? previous;
    private bool disposed;

    /// <summary>Installs a compiler in the current execution context.</summary>
    /// <param name="compiler">Compiler returning emitted bytes only.</param>
    public ScriptCompilerScope(Func<string, IReadOnlyList<string>?, CancellationToken, CompiledScript> compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        previous = Current.Value;
        Current.Value = compiler;
    }

    /// <summary>Restores the enclosing invocation's compiler.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        Current.Value = previous;
        disposed = true;
    }

    /// <summary>Compiles using the invocation override or the in-process default.</summary>
    /// <param name="source">Assembled script.</param>
    /// <param name="referencePaths">Additional references.</param>
    /// <param name="cancellationToken">Compilation cancellation.</param>
    /// <returns>Emitted image for execution in the caller.</returns>
    public static CompiledScript Compile(string source, IReadOnlyList<string>? referencePaths, CancellationToken cancellationToken) =>
        Current.Value is { } compiler
            ? compiler(source, referencePaths, cancellationToken)
            : RoslynScriptCompiler.CompileOnce(source, additionalReferencePaths: referencePaths, cancellationToken: cancellationToken);
}
