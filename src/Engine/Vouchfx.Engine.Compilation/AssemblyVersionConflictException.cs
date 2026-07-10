// Vouchfx.Engine.Compilation — AssemblyVersionConflictException (§5.6).
//
// Thrown by AssemblyGraphGuard when the same simple assembly name appears at two
// or more distinct versions in the graph presented at suite start.  Detection at
// startup (not at script runtime) is a hard invariant: a FileLoadException at
// runtime surfaces deep inside a step and destroys the verdict.
//
// Sprint-2 note: this exception maps to the Environment-error verdict bucket (§12.1).
// The verdict taxonomy itself is a later sprint; for now the exception is a distinct,
// clearly-typed signal rather than a runtime FileLoadException surprise.
using System.Globalization;
using System.Reflection;

namespace Vouchfx.Engine.Compilation;

/// <summary>
/// Carries the details of a single assembly whose simple name resolves to two or
/// more distinct <see cref="Version"/>s in the graph.
/// </summary>
/// <param name="SimpleName">
/// The <see cref="AssemblyName.Name"/> that is ambiguous (e.g. <c>"Npgsql"</c>).
/// </param>
/// <param name="Versions">
/// The distinct <see cref="Version"/>s found for this simple name, in the order
/// they were first encountered during the scan.
/// </param>
public sealed record AssemblyConflict(string SimpleName, IReadOnlyList<Version> Versions);

/// <summary>
/// Thrown when <see cref="AssemblyGraphGuard"/> detects that one or more assembly
/// simple names resolve to multiple distinct versions in the closure presented at
/// suite start (§5.6).
/// </summary>
/// <remarks>
/// <para>
/// This exception indicates an <em>Environment error</em>, not a test defect.
/// In Sprint 2 it will map to the <c>Environment-error</c> verdict bucket (§12.1).
/// Until that taxonomy is wired, callers should treat it as a fatal infrastructure
/// failure that aborts the entire suite run before any step executes.
/// </para>
/// <para>
/// The recommended fix is stated in the exception message: align all conflicting
/// versions in <c>Directory.Packages.props</c> (or rebuild the customer DLL
/// against the engine's published binding contract).
/// </para>
/// </remarks>
public sealed class AssemblyVersionConflictException : Exception
{
    /// <summary>
    /// The set of assemblies whose simple names resolved to conflicting versions.
    /// </summary>
    public IReadOnlyList<AssemblyConflict> Conflicts { get; }

    /// <summary>
    /// Initialises a new instance of <see cref="AssemblyVersionConflictException"/>
    /// with the given <paramref name="conflicts"/> and a human-readable diagnostic
    /// message that names each conflicting assembly, lists its versions, and states
    /// the recommended corrective action.
    /// </summary>
    /// <param name="conflicts">
    /// The conflicts detected by
    /// <see cref="AssemblyGraphGuard.FindConflicts(IEnumerable{AssemblyName})"/>;
    /// must be non-empty.
    /// </param>
    public AssemblyVersionConflictException(IReadOnlyList<AssemblyConflict> conflicts)
        : base(BuildMessage(conflicts))
    {
        Conflicts = conflicts;
    }

    private static string BuildMessage(IReadOnlyList<AssemblyConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);

        var lines = new System.Text.StringBuilder();
        lines.AppendLine(
            "Assembly version conflict(s) detected at suite start. " +
            "The engine requires exactly one version of each assembly in the closure.");
        lines.AppendLine();

        foreach (var conflict in conflicts)
        {
            var versionList = string.Join(", ", conflict.Versions);
            lines.AppendLine(
                CultureInfo.InvariantCulture,
                $"  '{conflict.SimpleName}' found at versions: {versionList}");
        }

        lines.AppendLine();
        lines.Append(
            "Recommended action: align versions in Directory.Packages.props, or rebuild " +
            "the customer DLL against the engine's published binding contract.");

        return lines.ToString();
    }
}
