// Vouchfx.Engine.Compilation — ScaffoldException (Spec B).

namespace Vouchfx.Engine.Compilation.Scaffold;

/// <summary>
/// Thrown when suite scaffold cannot produce a valid skeleton (unknown step type,
/// unknown dependency kind, duplicate ids, empty steps, etc.).
/// </summary>
public sealed class ScaffoldException : InvalidOperationException
{
    /// <summary>
    /// Initialises a new instance with a clear diagnostic message.
    /// </summary>
    /// <param name="message">Human-readable description of the scaffold failure.</param>
    public ScaffoldException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance with a message and inner exception.
    /// </summary>
    public ScaffoldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
