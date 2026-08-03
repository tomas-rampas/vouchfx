// Throwaway reference provider for S01-F-01 contract tests.
// Provides minimal null-object implementations of context interfaces.
using Vouchfx.Sdk;

namespace Vouchfx.Sdk.Tests.Providers;

/// <summary>
/// Null-object implementation of <see cref="IBindingContext"/> for unit tests
/// that do not require binding-stage services.
/// </summary>
public sealed class NullBindingContext : IBindingContext
{
    /// <summary>Gets the singleton instance.</summary>
    public static readonly NullBindingContext Instance = new();

    private NullBindingContext() { }
}

/// <summary>
/// Null-object implementation of <see cref="IProjectContext"/> for unit tests
/// that do not require project-stage services.  Returns an empty
/// <see cref="DeclaredDependencies"/> map.
/// </summary>
public sealed class NullProjectContext : IProjectContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    /// <summary>Gets the singleton instance.</summary>
    public static readonly NullProjectContext Instance = new();

    private NullProjectContext() { }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeclaredServices { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}

/// <summary>
/// Null-object implementation of <see cref="ICompileContext"/> for unit tests
/// that do not require compile-stage services.  Returns <c>"default_step"</c>
/// as the step identifier and <c>"Generated"</c> as the suite namespace,
/// preserving the pre-Sprint-2 default-step fallback behaviour.
/// </summary>
public sealed class NullCompileContext : ICompileContext
{
    /// <inheritdoc />
    public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

    /// <summary>Gets the singleton instance.</summary>
    public static readonly NullCompileContext Instance = new();

    private NullCompileContext() { }

    /// <inheritdoc />
    public string StepId => "default_step";

    /// <inheritdoc />
    public string SuiteNamespace => "Generated";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Captures { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
        new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
}
