// S02-F-03 — Reserved-namespace startup guard tests.
//
// Covers:
//   • A customer DLL that declares types under Platform.Engine.* is flagged.
//   • A customer DLL that declares types under Platform.Steps.* is flagged.
//   • A customer DLL with a clean namespace produces no violations.
//   • A legitimately trusted assembly is NOT flagged when present in the trusted set,
//     but IS flagged when the trusted set is empty (proving it is the exclusion that
//     protects legitimate assemblies, not membership in a hard-coded allow-list).
//   • AssemblyClosure.GuardAtSuiteStart(ResolveEngineClosure()) does not throw
//     (the real engine assemblies are trusted by the prefix rule).
//
// BDD order: this file was written BEFORE ReservedNamespaceGuard existed (red),
// then the production types were added to make it green.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Platform.Engine.Compilation;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S02-F-03: <see cref="ReservedNamespaceGuard"/> suite-start reserved-namespace
/// check tests (§5.6).
/// </summary>
public sealed class ReservedNamespaceGuardTests
{
    private readonly ITestOutputHelper _output;

    public ReservedNamespaceGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // -------------------------------------------------------------------------
    // Test 1 — customer assembly declaring Platform.Engine.* is a violation.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A synthetic assembly whose simple name is <c>AcmeCustomer</c> but which declares
    /// a type in the <c>Platform.Engine.Injected</c> namespace must produce a
    /// <see cref="ReservedNamespaceViolation"/> whose <c>TypeFullName</c> contains the
    /// offending type and whose <c>ReservedPrefix</c> is <c>"Platform.Engine."</c>.
    /// <see cref="ReservedNamespaceGuard.ThrowIfSquatting"/> must throw
    /// <see cref="ReservedNamespaceSquatException"/> whose message names the type.
    /// </summary>
    [Fact]
    public void Find_CustomerDeclaringReservedNamespace_IsViolation()
    {
        // Arrange — compile a synthetic DLL that squats on Platform.Engine.*.
        const string source = """
            namespace Platform.Engine.Injected
            {
                public class Hack {}
            }
            """;

        var asm = LoadSyntheticAssembly(source, "AcmeCustomer");
        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Act.
        var violations = ReservedNamespaceGuard.Find(new[] { asm }, trusted);

        // Assert — exactly one violation.
        Assert.Single(violations);
        var v = violations[0];
        Assert.Equal("AcmeCustomer", v.AssemblyName);
        Assert.Contains("Platform.Engine.Injected.Hack", v.TypeFullName, StringComparison.Ordinal);
        Assert.Equal("Platform.Engine.", v.ReservedPrefix);

        _output.WriteLine($"Violation: {v.AssemblyName} → {v.TypeFullName} (prefix: {v.ReservedPrefix})");

        // ThrowIfSquatting must throw with the type named in the message.
        var ex = Assert.Throws<ReservedNamespaceSquatException>(
            () => ReservedNamespaceGuard.ThrowIfSquatting(new[] { asm }, trusted));

        Assert.Contains("Platform.Engine.Injected.Hack", ex.Message, StringComparison.Ordinal);
        Assert.Single(ex.Violations);
    }

    // -------------------------------------------------------------------------
    // Test 2 — customer assembly declaring Platform.Steps.* is a violation.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A synthetic assembly declaring a type in the <c>Platform.Steps.Evil</c>
    /// namespace must be flagged with <c>ReservedPrefix == "Platform.Steps."</c>.
    /// </summary>
    [Fact]
    public void Find_CustomerDeclaringPlatformSteps_IsViolation()
    {
        // Arrange.
        const string source = """
            namespace Platform.Steps.Evil
            {
                public class X {}
            }
            """;

        var asm = LoadSyntheticAssembly(source, "EvilVendor");
        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Act.
        var violations = ReservedNamespaceGuard.Find(new[] { asm }, trusted);

        // Assert.
        Assert.Single(violations);
        var v = violations[0];
        Assert.Equal("EvilVendor", v.AssemblyName);
        Assert.Contains("Platform.Steps.Evil.X", v.TypeFullName, StringComparison.Ordinal);
        Assert.Equal("Platform.Steps.", v.ReservedPrefix);

        _output.WriteLine($"Violation: {v.AssemblyName} → {v.TypeFullName} (prefix: {v.ReservedPrefix})");
    }

    // -------------------------------------------------------------------------
    // Test 3 — clean customer assembly produces no violation.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A synthetic assembly that only declares types in its own namespace
    /// (<c>AcmeCorp.Widgets</c>) must produce zero violations and must not cause
    /// <see cref="ReservedNamespaceGuard.ThrowIfSquatting"/> to throw.
    /// </summary>
    [Fact]
    public void Find_CleanCustomerAssembly_NoViolation()
    {
        // Arrange.
        const string source = """
            namespace AcmeCorp.Widgets
            {
                public class Thing {}
            }
            """;

        var asm = LoadSyntheticAssembly(source, "AcmeClean");
        var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Act.
        var violations = ReservedNamespaceGuard.Find(new[] { asm }, trusted);

        // Assert — no violations.
        Assert.Empty(violations);

        // ThrowIfSquatting must not throw.
        var ex = Record.Exception(
            () => ReservedNamespaceGuard.ThrowIfSquatting(new[] { asm }, trusted));

        Assert.Null(ex);

        _output.WriteLine("Clean assembly: no violations detected.");
    }

    // -------------------------------------------------------------------------
    // Test 4 — trusted assembly is not flagged; untrusted identical assembly IS.
    // -------------------------------------------------------------------------

    /// <summary>
    /// The real <c>Platform.Engine.Compilation</c> assembly legitimately declares types
    /// under <c>Platform.Engine.Compilation.*</c>.  When its simple name is included in
    /// the trusted set it must produce zero violations.  When the trusted set is empty
    /// it must be flagged — proving that the trusted-set exclusion is what protects
    /// legitimate engine assemblies, not any hard-coded allow-list.
    /// </summary>
    [Fact]
    public void Find_TrustedAssemblyDeclaringReservedNamespace_IsNotFlagged()
    {
        var engineAsm = typeof(AssemblyClosure).Assembly;
        var simpleName = engineAsm.GetName().Name!; // "Platform.Engine.Compilation"

        // Act (a): trusted set CONTAINS the simple name → no violations.
        var trustedContains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { simpleName };
        var violationsWhenTrusted = ReservedNamespaceGuard.Find(new[] { engineAsm }, trustedContains);

        Assert.Empty(violationsWhenTrusted);
        _output.WriteLine($"With trusted set containing '{simpleName}': 0 violations (correct).");

        // Act (b): trusted set is EMPTY → the real engine assembly IS flagged.
        var emptyTrusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var violationsWhenUntrusted = ReservedNamespaceGuard.Find(new[] { engineAsm }, emptyTrusted);

        Assert.NotEmpty(violationsWhenUntrusted);
        _output.WriteLine(
            $"With empty trusted set: {violationsWhenUntrusted.Count} violation(s) detected (correct).");
    }

    // -------------------------------------------------------------------------
    // Test 5 — GuardAtSuiteStart with real engine closure does not throw.
    // -------------------------------------------------------------------------

    /// <summary>
    /// <see cref="AssemblyClosure.GuardAtSuiteStart"/> over the result of
    /// <see cref="AssemblyClosure.ResolveEngineClosure"/> must not throw.
    /// The real engine assemblies are trusted by the prefix rule built inside
    /// <see cref="AssemblyClosure"/> — their simple names start with
    /// <c>Platform.Engine.</c> or <c>Platform.Steps.</c>, so they are excluded from
    /// the customer scan.  This also confirms the version-conflict happy-path still holds.
    /// </summary>
    [Fact]
    public void GuardAtSuiteStart_RealEngineClosure_DoesNotThrow()
    {
        var closure = AssemblyClosure.ResolveEngineClosure();

        var ex = Record.Exception(() => AssemblyClosure.GuardAtSuiteStart(closure));

        Assert.Null(ex);

        _output.WriteLine(
            $"GuardAtSuiteStart: scanned {closure.Count} assemblies — no namespace squats, no version conflicts.");
    }

    // -------------------------------------------------------------------------
    // Private helper — compile and load a synthetic assembly by simple name.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compiles <paramref name="csharpSource"/> into a PE image and loads it via
    /// <see cref="Assembly.Load(byte[])"/> into the Default context.
    /// The simple name of the emitted assembly is set to <paramref name="assemblySimpleName"/>
    /// so that the <see cref="ReservedNamespaceGuard"/> sees it as a customer DLL
    /// with that name.
    /// </summary>
    /// <remarks>
    /// Assemblies loaded this way are non-collectible but are tiny (a handful of types)
    /// and are only loaded once per test run.  Using the Default context keeps the
    /// implementation simple — no custom ALC lifecycle management needed for these guard
    /// tests, and the assemblies are synthetic test artefacts with no real resources.
    /// </remarks>
    private Assembly LoadSyntheticAssembly(string csharpSource, string assemblySimpleName)
    {
        var image = CompileSyntheticAssemblyImage(csharpSource, assemblySimpleName);
        Assert.NotEmpty(image);
        var asm = Assembly.Load(image);
        _output.WriteLine(
            $"Loaded synthetic assembly '{asm.GetName().Name}' ({image.Length} bytes) " +
            $"with {asm.GetTypes().Length} type(s).");
        return asm;
    }

    /// <summary>
    /// Compiles <paramref name="csharpSource"/> to a PE image using
    /// <see cref="CSharpCompilation"/> with BCL references and returns the raw bytes.
    /// The emitted assembly carries <paramref name="assemblySimpleName"/> as its simple name.
    /// </summary>
    private static byte[] CompileSyntheticAssemblyImage(string csharpSource, string assemblySimpleName)
    {
        // Collect TPA paths for BCL references — same technique as RoslynScriptCompilerTests.
        var tpaData = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        var tpaPaths = tpaData.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var wantedBcl = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
        };

        var refs = new List<MetadataReference>();
        foreach (var path in tpaPaths)
        {
            if (wantedBcl.Contains(Path.GetFileName(path)))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: assemblySimpleName,
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(csharpSource) },
            references: refs,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                warningLevel: 0,
                nullableContextOptions: NullableContextOptions.Disable));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        // For these guard tests we require successful compilation — the source is
        // deliberately simple (plain namespace + empty class).
        if (!result.Success)
        {
            var errors = string.Join(Environment.NewLine,
                result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));

            throw new InvalidOperationException(
                $"Synthetic assembly '{assemblySimpleName}' did not compile:{Environment.NewLine}{errors}");
        }

        return ms.ToArray();
    }
}
