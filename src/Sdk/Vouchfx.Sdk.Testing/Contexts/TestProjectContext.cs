// Vouchfx.Sdk.Testing — TestProjectContext.
//
// A public, ready-to-use implementation of the frozen Vouchfx.Sdk IProjectContext,
// supplied to a provider's Validate stage when driving it through the harness.  It
// mirrors the engine's RunProjectContext: an optional declared-dependencies map,
// defaulting to empty (the common single-step, dependency-free case).
using System.IO;
using Vouchfx.Sdk;

namespace Vouchfx.Sdk.Testing.Contexts;

/// <summary>
/// A public, reusable implementation of <see cref="IProjectContext"/> for driving a
/// provider's validation stage in a test harness.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IProjectContext"/> is an engine-supplied, provider-consumed interface
/// (CLAUDE.md §13).  In production the engine derives
/// <see cref="DeclaredDependencies"/> from the scenario's
/// <c>environment.dependencies</c> section and passes it to each provider's
/// <c>Validate</c>; the harness supplies this stand-in so a provider's
/// dependency-reconciliation logic can be exercised in isolation.
/// </para>
/// <para>
/// The declared-dependencies map defaults to empty, which is correct for the common
/// single-step, dependency-free case the harness targets.
/// </para>
/// </remarks>
public sealed class TestProjectContext : IProjectContext
{
    /// <summary>
    /// Initialises a new <see cref="TestProjectContext"/> with the given
    /// declared-dependencies map.
    /// </summary>
    /// <param name="declaredDependencies">
    /// Map of dependency name to type identifier (e.g. <c>"orders-db" → "postgres"</c>),
    /// as would be declared under <c>environment.dependencies</c> in a scenario file.
    /// Pass <see langword="null"/> (or omit) to use an empty map — the default for a
    /// single-step, dependency-free scenario.
    /// </param>
    /// <param name="suiteDirectory">
    /// The base directory relative file-path fields (e.g. <c>script.csharp</c>'s
    /// <c>file</c>) are resolved against.  Defaults to the process's current
    /// directory when omitted.
    /// </param>
    public TestProjectContext(
        IReadOnlyDictionary<string, string>? declaredDependencies = null,
        string? suiteDirectory = null)
    {
        DeclaredDependencies = declaredDependencies
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        SuiteDirectory = suiteDirectory ?? Directory.GetCurrentDirectory();
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

    /// <inheritdoc />
    public string SuiteDirectory { get; }
}
