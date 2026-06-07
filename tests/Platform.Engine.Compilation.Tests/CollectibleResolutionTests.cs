// S02-B-02 — Dynamic assembly resolution for the script context.
//
// Tests written first (red) to drive the implementation of:
//   • CollectibleScriptLoadContext(name, collectibleProbingPaths) — the new overload.
//   • RoslynScriptCompiler.CompileOnce(…, additionalReferencePaths) — compile-time refs.
//   • RoslynScriptCompiler.RunIsolatedAsync(…, collectibleProbingPaths) — runtime probing.
//   • RoslynScriptCompiler.Load(…, collectibleProbingPaths) — runtime probing (Load path).
//
// Four assertions:
//   1. Satellite assembly resolves INTO the collectible context, not Default.
//   2. The collectible context + satellite are reclaimed by the GC after Unload.
//   3. Pin-avoidance: an assembly already in Default is NOT duplicated into the
//      collectible context (the map skips it).
//   4. End-to-end: compile with additionalReferencePaths + run with
//      collectibleProbingPaths produces the correct result and is fully collectible.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S02-B-02: dynamic assembly resolution for the script context (§5 memory model).
/// </summary>
public sealed class CollectibleResolutionTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    // Temp path written once per test class; cleaned up in Dispose.
    private readonly string _satPath;

    public CollectibleResolutionTests(ITestOutputHelper output)
    {
        _output = output;

        // Build a small satellite assembly ("VouchfxSatTest") and write it to a temp
        // file so subsequent tests can reference it by path.
        var image = BuildSatelliteAssembly();
        _satPath = Path.Combine(
            Path.GetTempPath(),
            $"VouchfxSat_{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(_satPath, image);

        _output.WriteLine($"Satellite written to: {_satPath}");
    }

    public void Dispose()
    {
        if (File.Exists(_satPath))
        {
            try { File.Delete(_satPath); }
            catch { /* best-effort cleanup */ }
        }
    }

    // -------------------------------------------------------------------------
    // Test 1 — satellite resolves INTO the collectible context.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a <see cref="CollectibleScriptLoadContext"/> with the satellite path in
    /// its probing list, resolves the satellite assembly by name, and asserts that:
    /// (a) the returned assembly is collectible; and
    /// (b) its load context is the collectible instance, not the Default context.
    /// </summary>
    [Fact]
    public void CollectibleLoadContext_WithProbingPath_ResolvesAssemblyIntoCollectibleContext()
    {
        // Arrange.
        var ctx = new CollectibleScriptLoadContext("res", new[] { _satPath });

        // Act — resolve by name; the context should find the path in its map.
        var asm = ctx.LoadFromAssemblyName(new AssemblyName("VouchfxSatTest"));

        // Assert (a): collectible — the assembly must carry the collectible flag.
        Assert.True(asm.IsCollectible,
            "The satellite assembly loaded from a probing path must be collectible.");

        // Assert (b): load context is THIS context, not the Default context.
        var resolvedCtx = AssemblyLoadContext.GetLoadContext(asm);
        Assert.Same(ctx, resolvedCtx);

        _output.WriteLine(
            $"Test 1: assembly '{asm.GetName().Name}' loaded into context '{resolvedCtx!.Name}' " +
            $"(collectible={asm.IsCollectible}).");

        // Cleanup — unload so the ALC does not pollute later GC assertions.
        ctx.Unload();
    }

    // -------------------------------------------------------------------------
    // Test 2 — satellite + context are reclaimed by the GC (no pin).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads the satellite assembly into a collectible context inside a
    /// <see cref="MethodImplOptions.NoInlining"/> helper, calls
    /// <see cref="AssemblyLoadContext.Unload"/>, and returns a
    /// <see cref="WeakReference"/> to the context.  The outer test then drives a
    /// bounded GC loop and asserts that the weak reference is no longer alive —
    /// proving neither the context nor the satellite is pinned.
    /// </summary>
    [Fact]
    public void CollectibleLoadContext_WithProbingPath_IsReclaimedByGcAfterUnload()
    {
        // Act — load, use, unload in a NoInlining helper so the JIT cannot keep
        // the ALC reference alive on this frame.
        var weakRef = LoadSatelliteAndUnload(_satPath);

        // Assert — drive GC until the context is collected.
        const int maxCycles = 10;
        for (var i = 0; i < maxCycles && weakRef.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weakRef.IsAlive,
            "The collectible context (with satellite probing) was NOT collected after " +
            "GC — a root is keeping the emitted assembly alive (memory leak).");
    }

    /// <summary>
    /// <see cref="MethodImplOptions.NoInlining"/> helper: creates the context with the
    /// satellite probing path, loads the satellite by name, verifies it resolves, calls
    /// <see cref="AssemblyLoadContext.Unload"/>, and returns a
    /// <see cref="WeakReference"/> to the context for the GC assertion in the caller.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadSatelliteAndUnload(string satPath)
    {
        var ctx = new CollectibleScriptLoadContext("unload-sat-test", new[] { satPath });
        try
        {
            var asm = ctx.LoadFromAssemblyName(new AssemblyName("VouchfxSatTest"));
            // Invoke the satellite type to prove it actually loaded correctly.
            var widgetType = asm.GetType("VouchfxSatTest.Widget")
                ?? throw new InvalidOperationException("VouchfxSatTest.Widget type not found.");
            var method = widgetType.GetMethod("Answer", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Widget.Answer method not found.");
            var result = (int)method.Invoke(null, null)!;
            if (result != 42)
                throw new InvalidOperationException($"Widget.Answer() returned {result}, expected 42.");
        }
        finally
        {
            ctx.Unload();
        }

        return new WeakReference(ctx);
    }

    // -------------------------------------------------------------------------
    // Test 3 — pin-avoidance: Default-loaded names are NOT duplicated.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Passes a path whose simple name ("Platform.Engine.Abstractions") is already
    /// present in <see cref="AssemblyLoadContext.Default"/> to the collectible
    /// context's probing list, then asserts that resolving that name from the
    /// collectible context returns the assembly from the Default context — NOT a
    /// collectible duplicate.
    /// </summary>
    /// <remarks>
    /// This proves the pin-avoidance rule: when the context sees that a name is
    /// already resident in Default, it silently skips that entry in its probing map
    /// and returns <see langword="null"/> from <c>Load()</c>, causing the runtime to
    /// fall back to Default.
    /// </remarks>
    [Fact]
    public void CollectibleLoadContext_PinAvoidance_DefaultAssemblyIsNotDuplicated()
    {
        // Use Platform.Engine.Abstractions — it is always loaded in Default by this point.
        var abstractionsAssembly = typeof(ScriptGlobalVariables).Assembly;
        var abstractionsName = abstractionsAssembly.GetName().Name!; // "Platform.Engine.Abstractions"
        var abstractionsPath = abstractionsAssembly.Location;

        Assert.False(string.IsNullOrEmpty(abstractionsPath),
            "Platform.Engine.Abstractions.Location is empty — cannot test pin-avoidance.");

        // Arrange — pass the abstractions path as if a caller mistakenly included it.
        var ctx = new CollectibleScriptLoadContext("skip", new[] { abstractionsPath });

        // Act — resolve the name that the context's map should have EXCLUDED.
        var resolvedAsm = ctx.LoadFromAssemblyName(new AssemblyName(abstractionsName));

        // Assert: the resolved assembly's load context must be the Default context,
        // not our collectible context — proving the map excluded the name and the
        // runtime fell back to Default.
        var resolvedCtx = AssemblyLoadContext.GetLoadContext(resolvedAsm);
        Assert.Same(AssemblyLoadContext.Default, resolvedCtx);

        // Additionally: it must NOT be collectible (Default assemblies are never collectible).
        Assert.False(resolvedAsm.IsCollectible,
            "The Default-loaded assembly must not be collectible. " +
            "If it is, the pin-avoidance rule is broken and a duplicate was loaded.");

        _output.WriteLine(
            $"Test 3: '{abstractionsName}' resolved to context '{resolvedCtx!.Name}' " +
            $"(collectible={resolvedAsm.IsCollectible}) — pin-avoidance confirmed.");

        ctx.Unload();
    }

    // -------------------------------------------------------------------------
    // Test 4 — end-to-end: compile + runtime probing via the public API.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compiles a CSX body that calls <c>VouchfxSatTest.Widget.Answer()</c> using
    /// <see cref="RoslynScriptCompiler.CompileOnce"/> with
    /// <paramref name="additionalReferencePaths"/> pointing to the satellite, then
    /// executes it with <see cref="RoslynScriptCompiler.RunIsolatedAsync"/> using
    /// <paramref name="collectibleProbingPaths"/> pointing to the same satellite.
    /// Asserts that <c>Vars["sat"]</c> equals 42 — proving compile-time reference
    /// resolution and runtime collectible resolution work end-to-end.
    /// </summary>
    [Fact]
    public async Task CompileOnce_WithAdditionalReferencePaths_AndRunIsolatedAsync_WithProbingPaths_ReturnsCorrectResult()
    {
        // The CSX body uses the fully-qualified name to avoid needing an explicit
        // import (the satellite namespace is VouchfxSatTest).
        const string csxSource = """
            Vars["sat"] = VouchfxSatTest.Widget.Answer();
            """;

        var referencePaths = new[] { _satPath };

        // Act — compile once with the satellite as an additional metadata reference.
        var compiled = RoslynScriptCompiler.CompileOnce(
            csxSource,
            additionalReferencePaths: referencePaths);

        Assert.NotNull(compiled);
        Assert.NotEmpty(compiled.Image);

        // Act — run in isolation, providing the satellite as a probing path so the
        // collectible ALC can resolve it at runtime.
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(vars);

        await RoslynScriptCompiler.RunIsolatedAsync(
            compiled,
            globals,
            collectibleProbingPaths: referencePaths);

        // Assert — Widget.Answer() returns 42; the satellite call succeeded inside
        // the collectible context.
        Assert.Equal(42, vars["sat"]);

        _output.WriteLine($"Test 4: Vars[\"sat\"] = {vars["sat"]} (expected 42).");
    }

    // -------------------------------------------------------------------------
    // Test 5 — end-to-end: Load + collectibleProbingPaths + InvokeAsync.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compiles a CSX body that calls <c>VouchfxSatTest.Widget.Answer()</c> using
    /// <see cref="RoslynScriptCompiler.CompileOnce"/> with
    /// <c>additionalReferencePaths</c> pointing to the satellite, then loads the result
    /// via <see cref="RoslynScriptCompiler.Load"/> with <c>collectibleProbingPaths</c>
    /// pointing to the same satellite.  Invokes the <see cref="LoadedScript"/> a few
    /// times and asserts that <c>Vars["sat"]</c> equals 42 on every invocation.
    /// Disposes the handle after use.
    /// </summary>
    [Fact]
    public async Task Load_WithCollectibleProbingPaths_ResolvesAndInvokesSatelliteCorrectly()
    {
        // The CSX body uses the fully-qualified name to avoid needing an explicit import.
        const string csxSource = """
            Vars["sat"] = VouchfxSatTest.Widget.Answer();
            """;

        var referencePaths = new[] { _satPath };

        // Compile once with the satellite as an additional metadata reference.
        var compiled = RoslynScriptCompiler.CompileOnce(
            csxSource,
            additionalReferencePaths: referencePaths);

        Assert.NotNull(compiled);
        Assert.NotEmpty(compiled.Image);

        // Load into a single collectible ALC using the satellite probing path.
        using var loaded = RoslynScriptCompiler.Load(compiled, collectibleProbingPaths: referencePaths);

        // Invoke several times to confirm the delegate binding is stable across calls.
        const int invocations = 3;
        for (var i = 0; i < invocations; i++)
        {
            var vars = new Dictionary<string, object?>();
            var globals = new ScriptGlobalVariables(vars);

            await loaded.InvokeAsync(globals);

            Assert.Equal(42, vars["sat"]);
            _output.WriteLine($"Test 5 invocation {i + 1}: Vars[\"sat\"] = {vars["sat"]} (expected 42).");
        }
    }

    // -------------------------------------------------------------------------
    // Private helper — build the satellite assembly image.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compiles a small satellite library assembly named <c>VouchfxSatTest</c> that
    /// exposes a single public static type:
    /// <code>
    ///   namespace VouchfxSatTest { public static class Widget { public static int Answer() => 42; } }
    /// </code>
    /// Returns the emitted PE bytes.  The compilation is strict (no warnings suppressed)
    /// to keep the artefact well-formed.
    /// </summary>
    private static byte[] BuildSatelliteAssembly()
    {
        const string satelliteSource = """
            namespace VouchfxSatTest
            {
                public static class Widget
                {
                    public static int Answer() => 42;
                }
            }
            """;

        // Gather minimal BCL references from TPA — the satellite only needs CoreLib.
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
            assemblyName: "VouchfxSatTest",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(satelliteSource) },
            references: refs,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = string.Join(", ", System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Where(result.Diagnostics,
                    d => d.Severity == DiagnosticSeverity.Error),
                d => d.ToString()));
            throw new InvalidOperationException(
                $"Satellite assembly compilation failed: {errors}");
        }

        return ms.ToArray();
    }
}
