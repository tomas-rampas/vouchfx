// Vouchfx.Engine.Compilation — CsxAssemblyException (§13.3.1).
// Raised by CsxAssembler when a fragment violates the composition rules that
// prevent identifier and helper-class collisions across providers.
namespace Vouchfx.Engine.Compilation;

/// <summary>
/// Thrown by <see cref="CsxAssembler"/> when one or more
/// <see cref="Vouchfx.Sdk.CsxFragment"/> instances violate the composition
/// rules mandated by §13.3.1.
/// </summary>
/// <remarks>
/// <para>
/// Two conditions cause this exception:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       A <see cref="Vouchfx.Sdk.CsxFragment.RequiredUsings"/> entry contains
///       whitespace, a leading <c>using </c> keyword, or a trailing semicolon —
///       i.e. it is not a bare namespace string.  The engine emits the
///       <c>using</c> keyword and semicolon; providers must supply only the
///       namespace (e.g. <c>System.Text.Json</c>).
///     </description>
///   </item>
///   <item>
///     <description>
///       Two <see cref="Vouchfx.Sdk.CsxFragment.RequiredHelpers"/> entries
///       declare a helper class with the same name but differing source text.
///       Providers must emit byte-identical source for a given helper class
///       across all step instances within one suite; two steps of the same
///       kind must not produce two differing definitions of the same class.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class CsxAssemblyException : Exception
{
    /// <summary>
    /// Initialises a new instance with the supplied diagnostic message.
    /// </summary>
    /// <param name="message">Human-readable description of the violation.</param>
    public CsxAssemblyException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises a new instance with the supplied diagnostic message and
    /// an inner exception that caused the violation.
    /// </summary>
    /// <param name="message">Human-readable description of the violation.</param>
    /// <param name="innerException">The underlying cause.</param>
    public CsxAssemblyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
