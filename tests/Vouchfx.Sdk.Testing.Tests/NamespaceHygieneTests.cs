// Vouchfx.Sdk.Testing.Tests — assembly-graph-hygiene guard for the harness package.
//
// §5.6: the engine refuses customer DLLs that declare the reserved `Vouchfx.Engine.*` /
// `Vouchfx.Steps.*` namespaces.  The harness package must itself be clean: every public
// type it ships must live under `Vouchfx.Sdk.Testing*` — neither a reserved namespace
// nor any other.  This documents and guards that invariant for the package's own surface.
using System;
using System.Linq;
using System.Reflection;
using Vouchfx.Sdk.Testing;
using Xunit;

namespace Vouchfx.Sdk.Testing.Tests;

/// <summary>
/// Guards that every public type in the <c>Vouchfx.Sdk.Testing</c> assembly lives under
/// the <c>Vouchfx.Sdk.Testing</c> namespace (graph-hygiene clean, non-reserved).
/// </summary>
public sealed class NamespaceHygieneTests
{
    private static readonly Assembly s_harnessAssembly =
        typeof(ProviderTestHarness).Assembly;

    /// <summary>
    /// Every public (or nested-public) type the harness exposes must be namespaced under
    /// <c>Vouchfx.Sdk.Testing</c>.  A stray public type in <c>Vouchfx.Engine.*</c> /
    /// <c>Vouchfx.Steps.*</c> (reserved) — or any other namespace — fails the guard.
    /// </summary>
    [Fact]
    public void AllPublicTypes_LiveUnderVouchfxSdkTestingNamespace()
    {
        var offenders = s_harnessAssembly
            .GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Where(t => t.Namespace is null
                || !t.Namespace.StartsWith("Vouchfx.Sdk.Testing", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Every public type in Vouchfx.Sdk.Testing must live under the " +
            "'Vouchfx.Sdk.Testing' namespace (§5.6 graph hygiene). Offenders: " +
            $"[{string.Join(", ", offenders)}].");
    }

    /// <summary>
    /// Belt-and-braces: no public type may declare a reserved engine/provider namespace
    /// (<c>Vouchfx.Engine.*</c> or <c>Vouchfx.Steps.*</c>), which a customer DLL is
    /// refused at startup for.
    /// </summary>
    [Fact]
    public void NoPublicType_UsesAReservedNamespace()
    {
        var reservedOffenders = s_harnessAssembly
            .GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Where(t => t.Namespace is { } ns
                && (ns.StartsWith("Vouchfx.Engine", StringComparison.Ordinal)
                    || ns.StartsWith("Vouchfx.Steps", StringComparison.Ordinal)))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.Empty(reservedOffenders);
    }
}
