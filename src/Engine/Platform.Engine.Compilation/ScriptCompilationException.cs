// Platform.Engine.Compilation — ScriptCompilationException (§5).
using Microsoft.CodeAnalysis;

namespace Platform.Engine.Compilation;

/// <summary>
/// Thrown when a CSX source string fails to compile via Roslyn.
/// </summary>
/// <remarks>
/// Wraps the full <see cref="Diagnostic"/> list so callers can render structured
/// compiler errors rather than parsing a flat string.
/// </remarks>
public sealed class ScriptCompilationException : Exception
{
    /// <summary>
    /// Gets the compiler diagnostics that caused the failure.
    /// Only diagnostics with severity <see cref="DiagnosticSeverity.Error"/> trigger
    /// this exception; warnings are surfaced here for completeness.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Initialises a new instance with a formatted message and the raw diagnostics.
    /// </summary>
    /// <param name="diagnostics">
    /// All diagnostics returned by <c>Compilation.Emit</c>; must not be
    /// <see langword="null"/>.
    /// </param>
    public ScriptCompilationException(IReadOnlyList<Diagnostic> diagnostics)
        : base(BuildMessage(diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))))
    {
        Diagnostics = diagnostics;
    }

    private static string BuildMessage(IReadOnlyList<Diagnostic> diagnostics)
    {
        var errors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString());
        return $"Script compilation failed:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}";
    }
}
