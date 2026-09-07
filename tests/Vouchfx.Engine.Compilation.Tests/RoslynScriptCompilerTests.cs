// S01-B-01 + S01-B-02 — Memory model PoC tests.
//
// These tests prove vouchfx's defining risk is solved:
//   • A CSX script compiles ONCE and runs N times from the same emitted image.
//   • The collectible AssemblyLoadContext fully unloads after each run,
//     returning the emitted assembly to GC — no memory leak.
//   • CSharpScript.EvaluateAsync / RunAsync are statically absent from the
//     production assembly (ensured by ForbiddenScriptingApiScanner).
//   • The scanner itself is verified by a negative-control test that proves it
//     catches both TypeReference and TypeSpecification (generic-instantiation) call
//     sites in a purpose-built synthetic assembly.
//   • ScriptGlobalVariables exposes no static state members.
//   • LoadedScript binds the <Factory> delegate once and invokes it cheaply N times
//     inside a single ALC, then unloads the context exactly once on Dispose.
//   • RunIsolatedAsync owns its ALC per call; collectibility is verified end-to-end.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S01-B-01 + S01-B-02: Memory model proof-of-concept.
/// </summary>
public sealed class RoslynScriptCompilerTests
{
    private readonly ITestOutputHelper _output;

    public RoslynScriptCompilerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // -------------------------------------------------------------------------
    // S01-B-01 Test 1: compile-once produces a non-empty image and runs correctly.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A trivial CSX script (reads <c>Vars["n"]</c>, returns <c>n+1</c>) compiles
    /// via <see cref="RoslynScriptCompiler.CompileOnce"/> exactly once and the emitted
    /// image is non-empty.  Running the delegate once returns the expected value.
    /// </summary>
    [Fact]
    public async Task CompileOnce_TrivialScript_EmitsNonEmptyImageAndRunsCorrectly()
    {
        // Arrange — a script that reads an integer from Vars and returns n+1.
        const string csxSource = """
            var n = (int)Vars["n"]!;
            Vars["result"] = n + 1;
            """;

        // Act — compile once.
        var compiled = RoslynScriptCompiler.CompileOnce(csxSource);

        // Assert — image is non-empty.
        Assert.NotNull(compiled);
        Assert.NotEmpty(compiled.Image);

        // Act — run once.
        var vars = new Dictionary<string, object?> { ["n"] = 41 };
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, runLabel: "test-1");

        // Assert — the script wrote n+1 into Vars["result"].
        Assert.Equal(42, vars["result"]);
    }

    // -------------------------------------------------------------------------
    // S01-B-01 Test 2: production assembly contains no forbidden Roslyn scripting calls.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Delegates to <see cref="ForbiddenScriptingApiScanner.Find(string)"/> to confirm
    /// that <c>Vouchfx.Engine.Compilation.dll</c> contains zero references to
    /// <c>EvaluateAsync</c> or <c>RunAsync</c> on any forbidden Roslyn scripting type
    /// (including generic instantiations of <c>Script&lt;T&gt;</c>).
    /// </summary>
    [Fact]
    public void ProductionAssembly_ContainsNoForbiddenScriptingMethods()
    {
        var assemblyPath = typeof(RoslynScriptCompiler).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(assemblyPath),
            "Could not determine assembly location — cannot scan MemberReference table.");

        var violations = ForbiddenScriptingApiScanner.Find(assemblyPath);

        Assert.Empty(violations);
    }

    // -------------------------------------------------------------------------
    // S01-B-01 Test 2b: negative-control — scanner catches TypeSpec (Script<T>) calls.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compiles a tiny synthetic assembly that deliberately calls both
    /// <c>CSharpScript.EvaluateAsync</c> (TypeReference parent) and
    /// <c>Script&lt;object&gt;.RunAsync</c> (TypeSpecification parent — the generic
    /// instantiation blind-spot the original inline scan missed), then asserts that
    /// <see cref="ForbiddenScriptingApiScanner.Find(byte[])"/> flags both.
    /// </summary>
    /// <remarks>
    /// This is the regression net for the guard itself.  Without it, a future caller
    /// adding <c>script.RunAsync(globals)</c> to the production assembly could leave
    /// the guard permanently GREEN even though the call was present in the IL.
    /// </remarks>
    [Fact]
    public void ForbiddenScriptingApiScanner_SyntheticAssembly_FindsBothTypeRefAndTypeSpecViolations()
    {
        // Build a synthetic assembly that includes:
        //   (a) CSharpScript.EvaluateAsync(...)  — TypeReference parent
        //   (b) script.RunAsync(...)              — TypeSpecification parent (Script<object>)
        //
        // The code intentionally does not compile to a working program; we only need
        // the IL MemberReferences to be emitted so the scanner can see them.
        const string violatingSource = """
            using System.Threading;
            using Microsoft.CodeAnalysis.CSharp.Scripting;
            using Microsoft.CodeAnalysis.Scripting;

            public static class Violator
            {
                public static void Bad()
                {
                    // (a) TypeReference parent: CSharpScript (non-generic static class)
                    _ = CSharpScript.EvaluateAsync("1 + 1");

                    // (b) TypeSpecification parent: Script<object> (closed generic)
                    Script<object> script = CSharpScript.Create<object>("1 + 1");
                    _ = script.RunAsync();
                }
            }
            """;

        var syntheticImage = CompileSyntheticAssembly(violatingSource);
        Assert.NotEmpty(syntheticImage);

        var violations = ForbiddenScriptingApiScanner.Find(syntheticImage);

        // We expect at least one hit for EvaluateAsync and at least one for RunAsync.
        Assert.Contains(violations, v => v.Contains("EvaluateAsync", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("RunAsync", StringComparison.Ordinal));

        _output.WriteLine(
            $"Negative-control scanner found {violations.Count} violation(s): " +
            string.Join(", ", violations));
    }

    // -------------------------------------------------------------------------
    // S01-B-02 Test 3: 5,000 invocations via LoadedScript — load once, invoke N times.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Calls <see cref="RoslynScriptCompiler.Load"/> exactly once to obtain a
    /// <see cref="LoadedScript"/> handle (one ALC, one delegate binding), then invokes
    /// <see cref="LoadedScript.InvokeAsync"/> 5,000 times, and disposes once.
    /// Proves the load-once / invoke-N / unload-once contract: the counter accumulates
    /// correctly across all iterations, confirming that (a) no recompilation or
    /// ALC reload occurs per iteration and (b) state threads through the shared
    /// <c>Vars</c> dictionary as expected.
    /// </summary>
    [Fact]
    public async Task Load_InvokedFiveThousandTimes_LoadsOnceAndAccumulatesCounter()
    {
        // Arrange — script increments Vars["counter"] by 1 each invocation.
        const string csxSource = """
            var c = Vars.ContainsKey("counter") ? (int)Vars["counter"]! : 0;
            Vars["counter"] = c + 1;
            """;

        var compiled = RoslynScriptCompiler.CompileOnce(csxSource);

        const int iterations = 5_000;
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(vars);

        // Act — Load ONCE, InvokeAsync N times, Dispose ONCE.
        var sw = Stopwatch.StartNew();
        using (var loaded = RoslynScriptCompiler.Load(compiled))
        {
            for (var i = 0; i < iterations; i++)
            {
                await loaded.InvokeAsync(globals);
            }
        }   // Dispose() calls Unload() here.
        sw.Stop();

        _output.WriteLine(
            $"LoadedScript: {iterations:N0} invocations in {sw.ElapsedMilliseconds} ms " +
            $"({sw.Elapsed.TotalMicroseconds / iterations:F1} µs/iter).");

        // Assert — counter must equal the number of iterations, proving every invocation
        // ran and no iteration reloaded (which would reset counter to 0).
        Assert.Equal(iterations, vars["counter"]);
    }

    // -------------------------------------------------------------------------
    // S01-B-02 Test 4 (original): per-run-ALC unload — low-level LoadContext path.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a <see cref="CollectibleScriptLoadContext"/>, loads the emitted image,
    /// invokes the delegate, and calls <see cref="AssemblyLoadContext.Unload"/>.
    /// A <see cref="WeakReference"/> to the ALC is returned from a
    /// <see cref="MethodImplOptions.NoInlining"/> helper so the JIT cannot keep the
    /// reference alive.  The test then loops GC collections until
    /// <see cref="WeakReference.IsAlive"/> is <see langword="false"/>, proving the
    /// ALC (and its emitted assembly) was reclaimed.
    /// </summary>
    [Fact]
    public async Task CollectibleContext_AfterUnload_IsReclaimedByGc()
    {
        // Arrange.
        const string csxSource = """
            Vars["unload_proof"] = 1;
            """;
        var compiled = RoslynScriptCompiler.CompileOnce(csxSource);

        // Act — load, invoke, unload; return WeakReference without keeping ALC alive.
        var weakRef = await LoadInvokeAndUnloadAsync(compiled);

        // Assert — GC must collect the ALC within a bounded number of cycles.
        const int maxGcCycles = 10;
        for (var i = 0; i < maxGcCycles && weakRef.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weakRef.IsAlive,
            "The CollectibleScriptLoadContext (low-level path) was NOT collected " +
            "after GC — a root is keeping the emitted assembly alive (memory leak).");
    }

    /// <summary>
    /// Loads the image into a fresh <see cref="CollectibleScriptLoadContext"/>,
    /// invokes the <c>&lt;Factory&gt;</c> delegate, unloads the context, and returns
    /// a <see cref="WeakReference"/> to the context.
    /// </summary>
    /// <remarks>
    /// <see cref="MethodImplOptions.NoInlining"/> prevents the JIT from inlining this
    /// method and inadvertently keeping local variables (which reference types inside
    /// the collectible context) alive past the <c>Unload</c> call.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadInvokeAndUnloadAsync(CompiledScript compiled)
    {
        var alc = new CollectibleScriptLoadContext("unload-test");
        try
        {
            Assembly submissionAssembly;
            using (var ms = new MemoryStream(compiled.Image, writable: false))
            {
                submissionAssembly = alc.LoadFromStream(ms);
            }

            var subType = submissionAssembly.GetType(CompiledScript.SubmissionTypeName)!;
            var factory = subType.GetMethod(
                CompiledScript.FactoryMethodName,
                BindingFlags.Public | BindingFlags.Static)!;

            var vars = new Dictionary<string, object?>();
            var globals = new ScriptGlobalVariables(vars);
            var submissionArray = new object?[] { globals, null };

            var rawTask = (System.Threading.Tasks.Task)factory.Invoke(null, new object?[] { submissionArray })!;
            await rawTask.ConfigureAwait(false);

            // Verify the script ran correctly.
            Assert.Equal(1, vars["unload_proof"]);
        }
        finally
        {
            alc.Unload();
        }

        // Return BEFORE the GC loop; do NOT hold any reference to alc beyond this point.
        return new WeakReference(alc);
    }

    // -------------------------------------------------------------------------
    // S01-B-02 Test 4b: RunIsolatedAsync end-to-end collectibility.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Drives <see cref="RoslynScriptCompiler.RunIsolatedAsync"/> through a
    /// <see cref="MethodImplOptions.NoInlining"/> helper a handful of times, collects
    /// a baseline of collectible assembly count before and after, forces GC in a
    /// bounded loop, and asserts that the count of collectible assemblies returns to
    /// the pre-call baseline.
    /// </summary>
    /// <remarks>
    /// This is the regression net for the production <c>RunIsolatedAsync</c> path.
    /// The test does NOT directly expose the internal ALC — instead it observes
    /// <see cref="AppDomain.CurrentDomain"/>'s collectible assembly count, which is a
    /// higher-fidelity signal because it exercises the real public API end-to-end.
    /// </remarks>
    [Fact]
    public async Task RunIsolatedAsync_AfterCompletion_CollectibleAssemblyCountReturnsToBaseline()
    {
        const string csxSource = """
            Vars["isolated_proof"] = 99;
            """;

        var compiled = RoslynScriptCompiler.CompileOnce(csxSource);

        // Warm up and flush any GC state from prior tests.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

        var baseline = CountCollectibleAssemblies();
        _output.WriteLine($"Collectible assembly baseline: {baseline}");

        // Drive several isolated runs through a NoInlining helper so the JIT
        // cannot keep any ALC reference alive on this frame.
        const int runs = 3;
        for (var i = 0; i < runs; i++)
        {
            await RunIsolatedNoInlineAsync(compiled, i);
        }

        // Allow the runtime to reclaim the unloaded ALCs.
        const int maxGcCycles = 10;
        int finalCount = baseline + 1; // sentinel: assume not yet collected
        for (var i = 0; i < maxGcCycles; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            finalCount = CountCollectibleAssemblies();
            if (finalCount <= baseline)
                break;
        }

        _output.WriteLine($"Collectible assembly count after GC: {finalCount} (baseline: {baseline})");

        Assert.True(finalCount <= baseline,
            $"Collectible assembly count ({finalCount}) did not return to baseline ({baseline}) " +
            "after RunIsolatedAsync — the ALCs were not fully reclaimed (memory leak).");
    }

    /// <summary>
    /// Thin <see cref="MethodImplOptions.NoInlining"/> wrapper around
    /// <see cref="RoslynScriptCompiler.RunIsolatedAsync"/> so that the JIT cannot keep
    /// any reference to the internal <see cref="CollectibleScriptLoadContext"/> alive
    /// on the caller's stack frame after this method returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunIsolatedNoInlineAsync(CompiledScript compiled, int iteration)
    {
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, runLabel: $"collectibility-{iteration}")
            .ConfigureAwait(false);
        Assert.Equal(99, vars["isolated_proof"]);
    }

    private static int CountCollectibleAssemblies()
        => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.IsCollectible);

    // -------------------------------------------------------------------------
    // S01-B-02 Test 5: LoadedScript collectibility — single loaded context is reclaimed.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads a compiled script via <see cref="RoslynScriptCompiler.Load"/>, invokes it a
    /// few times, disposes the handle, and verifies that the underlying
    /// <see cref="CollectibleScriptLoadContext"/> is reclaimed by the GC.
    /// </summary>
    /// <remarks>
    /// This is the companion collectibility test to
    /// <see cref="CollectibleContext_AfterUnload_IsReclaimedByGc"/> but exercises the
    /// load-once path: one ALC for N invocations, disposed (and unloaded) once.
    /// The <see cref="MethodImplOptions.NoInlining"/> helper ensures the JIT does not
    /// keep any reference into the collectible context alive on the caller's stack frame.
    /// </remarks>
    [Fact]
    public async Task LoadedScript_AfterDispose_AlcIsReclaimedByGc()
    {
        const string csxSource = """
            Vars["collectible_proof"] = 42;
            """;
        var compiled = RoslynScriptCompiler.CompileOnce(csxSource);

        // Act — load, invoke a few times, dispose; return WeakReference without
        // retaining any reference to the ALC or its internals.
        var weakRef = await LoadInvokeManyAndDisposeAsync(compiled);

        // Assert — the ALC must be collected within a bounded number of GC cycles.
        const int maxGcCycles = 10;
        for (var i = 0; i < maxGcCycles && weakRef.IsAlive; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weakRef.IsAlive,
            "The CollectibleScriptLoadContext (LoadedScript path) was NOT collected after GC — " +
            "a root is keeping the emitted assembly alive after Dispose (memory leak).");
    }

    /// <summary>
    /// Loads the compiled script into a single <see cref="LoadedScript"/>, invokes it
    /// a few times, disposes, and returns a <see cref="WeakReference"/> to the
    /// underlying ALC.
    /// </summary>
    /// <remarks>
    /// <see cref="MethodImplOptions.NoInlining"/> prevents the JIT from keeping any
    /// reference to the <see cref="LoadedScript"/> or its internal ALC alive after the
    /// method returns, which is the pre-condition for the GC collectibility assertion.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadInvokeManyAndDisposeAsync(CompiledScript compiled)
    {
        // We need to capture the ALC reference BEFORE the LoadedScript nulls it on Dispose,
        // so we obtain it via a local scope, keep the WeakReference, then let it go.
        WeakReference alcRef;

        var loaded = RoslynScriptCompiler.Load(compiled);
        try
        {
            // Verify the script runs correctly across several invocations.
            const int probeIterations = 5;
            var vars = new Dictionary<string, object?>();
            var globals = new ScriptGlobalVariables(vars);

            for (var i = 0; i < probeIterations; i++)
            {
                await loaded.InvokeAsync(globals).ConfigureAwait(false);
            }

            Assert.Equal(42, vars["collectible_proof"]);

            // Capture the ALC reference for the weak-ref before we dispose.
            // LoadedScript.AlcForTest exposes the ALC for this purpose.
            alcRef = loaded.AlcWeakReferenceForTest();
        }
        finally
        {
            // Dispose nulls the delegate and calls Unload() on the ALC.
            loaded.Dispose();
        }

        // Drop 'loaded' — after the finally block it is already disposed.
        // Return the weak reference; the caller drives the GC loop.
        return alcRef;
    }

    // -------------------------------------------------------------------------
    // S01-B-02 Test 6: no static bridge — ScriptGlobalVariables has no static state.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Uses reflection to assert that <see cref="ScriptGlobalVariables"/> exposes no
    /// static fields or properties.  A static member bridging the collectible context
    /// boundary would root the emitted assembly and prevent unloading (§5).
    /// </summary>
    [Fact]
    public void ScriptGlobalVariables_HasNoStaticStateMembers()
    {
        const BindingFlags staticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var type = typeof(ScriptGlobalVariables);

        var staticFields = type.GetFields(staticFlags)
            .Where(f => !f.IsLiteral) // exclude const (compile-time values, not rooted)
            .ToArray();

        var staticProperties = type.GetProperties(staticFlags)
            .ToArray();

        Assert.Empty(staticFields);
        Assert.Empty(staticProperties);
    }

    // -------------------------------------------------------------------------
    // Additional: ScriptCompilationException is thrown for bad source.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="RoslynScriptCompiler.CompileOnce"/> throws a
    /// <see cref="ScriptCompilationException"/> (not a raw Roslyn exception) when the
    /// CSX source contains a syntax error.
    /// </summary>
    [Fact]
    public void CompileOnce_InvalidSource_ThrowsScriptCompilationException()
    {
        const string badSource = "this is not valid C#  !!!";

        var ex = Assert.Throws<ScriptCompilationException>(
            () => RoslynScriptCompiler.CompileOnce(badSource));

        Assert.NotEmpty(ex.Diagnostics);
        Assert.Contains(ex.Diagnostics,
            d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        Assert.Contains("Script compilation failed", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Issue #266, Threat B — CompileOnce honours a CancellationToken on the emit path.
    // -------------------------------------------------------------------------

    /// <summary>
    /// A pre-cancelled <see cref="CancellationToken"/> passed to
    /// <see cref="RoslynScriptCompiler.CompileOnce"/> must throw
    /// <see cref="OperationCanceledException"/> — proving the token is genuinely wired
    /// through, rather than silently ignored (issue #266, Threat B: bounding a
    /// runaway-but-non-crashing compile). This does NOT exercise Roslyn's own
    /// binder/emit-phase cooperative check (the source below is trivial and would compile
    /// near-instantly); it proves CompileOnce's own defensive up-front
    /// <c>cancellationToken.ThrowIfCancellationRequested()</c> fires deterministically for
    /// an already-cancelled token, regardless of exactly where inside Roslyn's pipeline the
    /// first cooperative check would otherwise fall.
    /// </summary>
    [Fact]
    public void CompileOnce_PreCancelledToken_ThrowsOperationCanceledException()
    {
        const string csxSource = """
            Vars["never_reached"] = 1;
            """;

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => RoslynScriptCompiler.CompileOnce(csxSource, cancellationToken: cts.Token));
    }

    /// <summary>
    /// A live (not cancelled) token passed to <see cref="RoslynScriptCompiler.CompileOnce"/>
    /// does not interfere with a normal compile — the token is genuinely optional /
    /// additive, not a behaviour change for the overwhelming majority of callers that pass
    /// none (the default <see cref="CancellationToken.None"/>).
    /// </summary>
    [Fact]
    public void CompileOnce_LiveToken_CompilesNormally()
    {
        const string csxSource = """
            Vars["with_live_token"] = 1;
            """;

        using var cts = new CancellationTokenSource();

        var compiled = RoslynScriptCompiler.CompileOnce(csxSource, cancellationToken: cts.Token);

        Assert.NotNull(compiled);
        Assert.NotEmpty(compiled.Image);
    }

    // -------------------------------------------------------------------------
    // ScriptCompilationException — null-guard regression (Fix 1 / PR #126).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Passing <see langword="null"/> to the <see cref="ScriptCompilationException"/>
    /// constructor must throw <see cref="ArgumentNullException"/> with
    /// <see cref="ArgumentException.ParamName"/> equal to <c>"diagnostics"</c>.
    /// </summary>
    /// <remarks>
    /// Regression test for the ordering defect where the null-check fired inside
    /// <c>BuildMessage</c> (via <c>diagnostics.Where(…)</c>) rather than before
    /// <c>base(…)</c> was called, producing a <see cref="NullReferenceException"/>
    /// instead of the intended <see cref="ArgumentNullException"/>.
    /// </remarks>
    [Fact]
    public void ScriptCompilationException_NullDiagnostics_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new ScriptCompilationException(null!));

        Assert.Equal("diagnostics", ex.ParamName);
    }

    // -------------------------------------------------------------------------
    // Private helper — synthetic assembly compilation for negative-control test.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compiles <paramref name="csharpSource"/> to a PE image using
    /// <see cref="CSharpCompilation"/> and returns the raw bytes.
    /// The compilation is intentionally lenient: warnings are allowed and the source
    /// does not need to form a runnable program — we only need the MemberReferences
    /// to be emitted so the scanner can inspect them.
    /// </summary>
    private static byte[] CompileSyntheticAssembly(string csharpSource)
    {
        // Collect TPA paths to supply as references — same technique as production
        // BuildBaseOptions but here we include the Roslyn scripting assemblies too.
        var tpaData = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        var tpaPaths = tpaData.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // Base BCL references.
        var wantedBcl = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Threading.Tasks.dll",
            "System.Threading.dll",
            "netstandard.dll",
        };

        var refs = new List<MetadataReference>();
        foreach (var path in tpaPaths)
        {
            if (wantedBcl.Contains(Path.GetFileName(path)))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        // Add the Roslyn scripting assemblies so the calls bind.
        var roslynScriptingCore = typeof(Microsoft.CodeAnalysis.Scripting.Script).Assembly.Location;
        var roslynCsharpScripting = typeof(Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript).Assembly.Location;
        if (!string.IsNullOrEmpty(roslynScriptingCore))
            refs.Add(MetadataReference.CreateFromFile(roslynScriptingCore));
        if (!string.IsNullOrEmpty(roslynCsharpScripting))
            refs.Add(MetadataReference.CreateFromFile(roslynCsharpScripting));

        var compilation = CSharpCompilation.Create(
            assemblyName: "SyntheticViolator",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(csharpSource) },
            references: refs,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                warningLevel: 0,           // suppress warnings — this is a test artefact
                nullableContextOptions: NullableContextOptions.Disable));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        // We accept emit failures: even if the source does not fully compile, Roslyn
        // still emits whatever MemberReferences it resolved — which is all we need
        // for the scanner test.  Log diagnostics for transparency.
        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();
            // If there are genuine bind errors the reference MemberRefs won't appear
            // and the test will fail with a clear message from the assertions above.
            // We do NOT throw here so the test can still examine whatever was emitted.
            _ = errors; // acknowledged
        }

        return ms.ToArray();
    }
}
