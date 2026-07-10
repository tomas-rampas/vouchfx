// S02-B-02 — AssemblyClosure / GuardAtSuiteStart tests.
//
// Covers:
//   • ResolveEngineClosure() returns a non-empty list that includes known engine assemblies.
//   • GuardAtSuiteStart(ResolveEngineClosure()) does NOT throw (engine graph is conflict-free).
//   • GuardAtSuiteStart(null!) throws ArgumentNullException.
//
// The conflict-throwing path is covered by AssemblyGraphGuardTests.B1; see that class for
// the version-conflict assertions.  This file focuses on the AssemblyClosure façade.
using System;
using System.Linq;
using Vouchfx.Engine.Compilation;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S02-B-02: <see cref="AssemblyClosure"/> suite-start hook tests (§5.6).
/// </summary>
public sealed class AssemblyClosureGuardTests
{
    private readonly ITestOutputHelper _output;

    public AssemblyClosureGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // -------------------------------------------------------------------------
    // Test 1 — ResolveEngineClosure returns a non-empty, well-formed list.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asserts that <see cref="AssemblyClosure.ResolveEngineClosure"/> returns a
    /// non-empty list and that the list includes at least the engine compilation
    /// assembly itself.
    /// </summary>
    [Fact]
    public void ResolveEngineClosure_ReturnsNonEmptyListContainingEngineAssemblies()
    {
        // Act.
        var closure = AssemblyClosure.ResolveEngineClosure();

        // Assert — list is non-empty (the process has at least BCL + engine assemblies loaded).
        Assert.NotNull(closure);
        Assert.NotEmpty(closure);

        // The compilation assembly must be present by the time this test runs.
        var compilationAssembly = typeof(RoslynScriptCompiler).Assembly;
        var compilationName = compilationAssembly.GetName().Name; // "Vouchfx.Engine.Compilation"

        Assert.Contains(closure,
            a => string.Equals(a.GetName().Name, compilationName, StringComparison.OrdinalIgnoreCase));

        _output.WriteLine(
            $"ResolveEngineClosure returned {closure.Count} assemblies; " +
            $"contains '{compilationName}': true.");

        // Log a few names for diagnostic clarity.
        foreach (var a in closure.Take(5))
            _output.WriteLine($"  {a.GetName().Name} {a.GetName().Version}");
        if (closure.Count > 5)
            _output.WriteLine($"  … and {closure.Count - 5} more.");
    }

    // -------------------------------------------------------------------------
    // Test 2 — GuardAtSuiteStart does NOT throw for the real engine closure.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asserts that <see cref="AssemblyClosure.GuardAtSuiteStart"/> does not throw
    /// when passed the result of <see cref="AssemblyClosure.ResolveEngineClosure"/>,
    /// proving that the engine's own assembly graph is conflict-free at suite start.
    /// </summary>
    [Fact]
    public void GuardAtSuiteStart_RealEngineClosure_DoesNotThrow()
    {
        var closure = AssemblyClosure.ResolveEngineClosure();

        // Must not throw — CPM pins exactly one version of every engine dependency.
        var ex = Record.Exception(() => AssemblyClosure.GuardAtSuiteStart(closure));

        Assert.Null(ex);

        _output.WriteLine(
            $"GuardAtSuiteStart: scanned {closure.Count} assemblies — no conflicts.");
    }

    // -------------------------------------------------------------------------
    // Test 3 — GuardAtSuiteStart(null!) throws ArgumentNullException.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Asserts that <see cref="AssemblyClosure.GuardAtSuiteStart"/> throws
    /// <see cref="ArgumentNullException"/> when passed <see langword="null"/>.
    /// </summary>
    [Fact]
    public void GuardAtSuiteStart_NullArgument_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => AssemblyClosure.GuardAtSuiteStart(null!));

        // The param name must be "closure" to match the production signature.
        Assert.Equal("closure", ex.ParamName);
    }
}
