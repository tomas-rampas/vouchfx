// Vouchfx.Engine.Compilation — CompiledScript (§5).
// Holds the emitted PE image that is produced exactly once and then loaded into a
// fresh CollectibleScriptLoadContext for every execution.
namespace Vouchfx.Engine.Compilation;

/// <summary>
/// An immutable value produced by <see cref="RoslynScriptCompiler.CompileOnce"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Image"/> byte array is the portable executable emitted by Roslyn.
/// It is safe to share across threads; a new
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> is created per run and
/// loads a fresh copy of the bytes each time.
/// </para>
/// <para>
/// <see cref="SubmissionTypeName"/> and <see cref="FactoryMethodName"/> record the
/// well-known Roslyn submission naming convention confirmed during the Sprint-1 PoC
/// (<c>"Submission#0"</c> / <c>"&lt;Factory&gt;"</c>).  They are exposed as constants
/// so the executor and the tests can refer to the same names without magic strings.
/// </para>
/// </remarks>
public sealed class CompiledScript
{
    /// <summary>
    /// The well-known type name Roslyn emits for the outermost submission class.
    /// Confirmed against <c>Microsoft.CodeAnalysis.CSharp.Scripting 4.14.0</c>.
    /// </summary>
    public const string SubmissionTypeName = "Submission#0";

    /// <summary>
    /// The well-known name of the public static factory method on
    /// <see cref="SubmissionTypeName"/>.
    /// Signature: <c>public static Task&lt;T&gt; &lt;Factory&gt;(object[] submissionArray)</c>.
    /// </summary>
    public const string FactoryMethodName = "<Factory>";

    /// <summary>
    /// Gets the portable executable image emitted by Roslyn.  Must not be modified.
    /// </summary>
    public byte[] Image { get; }

    /// <summary>
    /// Initialises a new instance from the supplied emitted image.
    /// </summary>
    /// <param name="image">Must not be <see langword="null"/> or empty.</param>
    public CompiledScript(byte[] image)
    {
        if (image is null || image.Length == 0)
            throw new ArgumentException("Emitted image must not be null or empty.", nameof(image));
        Image = image;
    }
}
