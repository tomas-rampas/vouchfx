namespace Vouchfx.Engine.Compilation;

/// <summary>A compiler subprocess could not safely produce an image.</summary>
public sealed class CompileWorkerException : Exception
{
    /// <summary>Creates a failure with a fixed, source-free diagnostic.</summary>
    /// <param name="message">Diagnostic containing no script or child output.</param>
    public CompileWorkerException(string message) : base(message) { }
}
