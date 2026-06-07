// Throwaway reference provider for S01-F-01 contract tests.
// Provides minimal null-object implementations of context interfaces.
using Platform.Sdk;

namespace Platform.Sdk.Tests.Providers;

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
/// that do not require project-stage services.
/// </summary>
public sealed class NullProjectContext : IProjectContext
{
    /// <summary>Gets the singleton instance.</summary>
    public static readonly NullProjectContext Instance = new();

    private NullProjectContext() { }
}

/// <summary>
/// Null-object implementation of <see cref="ICompileContext"/> for unit tests
/// that do not require compile-stage services.  Uses the literal string
/// <c>"default_step"</c> as the step identifier.
/// </summary>
public sealed class NullCompileContext : ICompileContext
{
    /// <summary>Gets the singleton instance.</summary>
    public static readonly NullCompileContext Instance = new();

    private NullCompileContext() { }
}
