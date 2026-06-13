// Platform.Sdk.Testing.Tests — assembly-graph-hygiene guard for the harness package.
//
// §5.6: the engine refuses customer DLLs that declare the reserved `Platform.Engine.*` /
// `Platform.Steps.*` namespaces.  The harness package must itself be clean: every public
// type it ships must live under `Platform.Sdk.Testing*` — neither a reserved namespace
// nor any other.  This documents and guards that invariant for the package's own surface.
using System;
using System.Linq;
using System.Reflection;
using Platform.Sdk.Testing;
using Xunit;

namespace Platform.Sdk.Testing.Tests;

/// <summary>
/// Guards that every public type in the <c>Platform.Sdk.Testing</c> assembly lives under
/// the <c>Platform.Sdk.Testing</c> namespace (graph-hygiene clean, non-reserved).
/// </summary>
public sealed class NamespaceHygieneTests
{
    private static readonly Assembly s_harnessAssembly =
        typeof(ProviderTestHarness).Assembly;

    /// <summary>
    /// Every public (or nested-public) type the harness exposes must be namespaced under
    /// <c>Platform.Sdk.Testing</c>.  A stray public type in <c>Platform.Engine.*</c> /
    /// <c>Platform.Steps.*</c> (reserved) — or any other namespace — fails the guard.
    /// </summary>
    [Fact]
    public void AllPublicTypes_LiveUnderPlatformSdkTestingNamespace()
    {
        var offenders = s_harnessAssembly
            .GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Where(t => t.Namespace is null
                || !t.Namespace.StartsWith("Platform.Sdk.Testing", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Every public type in Platform.Sdk.Testing must live under the " +
            "'Platform.Sdk.Testing' namespace (§5.6 graph hygiene). Offenders: " +
            $"[{string.Join(", ", offenders)}].");
    }

    /// <summary>
    /// Belt-and-braces: no public type may declare a reserved engine/provider namespace
    /// (<c>Platform.Engine.*</c> or <c>Platform.Steps.*</c>), which a customer DLL is
    /// refused at startup for.
    /// </summary>
    [Fact]
    public void NoPublicType_UsesAReservedNamespace()
    {
        var reservedOffenders = s_harnessAssembly
            .GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Where(t => t.Namespace is { } ns
                && (ns.StartsWith("Platform.Engine", StringComparison.Ordinal)
                    || ns.StartsWith("Platform.Steps", StringComparison.Ordinal)))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.Empty(reservedOffenders);
    }
}
