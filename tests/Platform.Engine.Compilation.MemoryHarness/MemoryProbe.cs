// Platform.Engine.Compilation.MemoryHarness — MemoryProbe (S01-B-03, §5).
//
// Measures the managed heap delta across a high-volume load-unload workload.
// This is the seed of the permanent CI leak gate (wired blocking in Sprint 2, S02-D-01).
//
// Protocol:
//   1. Compile the trivial probe script ONCE (§5: compile-once invariant).
//   2. Warm up a few load-unload cycles + forced GC to amortise one-time Roslyn /
//      JIT costs before the baseline is taken.
//   3. Take BaselineBytes after a quiescent GC.
//   4. Run the load-unload workload <iterations> times — one full collectible-ALC
//      lifecycle per iteration (the leak-sensitive path).
//   5. Force another quiescent GC, take PostBytes.
//   6. NetDeltaBytes = PostBytes - BaselineBytes.
//      If the ALC lifecycle is broken, uncollectable assemblies accumulate and
//      the delta explodes; if it is correct the delta stays near zero.
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Platform.Engine.Abstractions;
using Platform.Engine.Compilation;

namespace Platform.Engine.Compilation.MemoryHarness;

/// <summary>
/// The structured result produced by <see cref="MemoryProbe.RunAsync"/>.
/// Serialised as a single-line JSON object to stdout by the harness executable.
/// </summary>
/// <param name="Iterations">Number of load-unload cycles exercised.</param>
/// <param name="BaselineBytes">
/// Managed heap size (bytes) after warm-up, before the workload.
/// </param>
/// <param name="PostBytes">
/// Managed heap size (bytes) after the workload and a quiescent GC.
/// </param>
/// <param name="NetDeltaBytes">
/// <see cref="PostBytes"/> − <see cref="BaselineBytes"/>.
/// A value close to zero confirms no assemblies were retained across iterations.
/// </param>
/// <param name="ThresholdBytes">
/// Acceptance ceiling for <see cref="NetDeltaBytes"/>.
/// </param>
/// <param name="Passed">
/// <see langword="true"/> when <see cref="NetDeltaBytes"/> is strictly below
/// <see cref="ThresholdBytes"/>.
/// </param>
/// <param name="CollectibleBefore">
/// Number of collectible assemblies in <see cref="System.AppDomain.CurrentDomain"/>
/// after warm-up, before the workload.
/// </param>
/// <param name="CollectibleAfter">
/// Number of collectible assemblies after the workload and a quiescent GC.
/// </param>
/// <param name="ContextReclaimed">
/// <see langword="true"/> when <see cref="CollectibleAfter"/> ≤
/// <see cref="CollectibleBefore"/>, confirming the ALC unload graph was fully
/// traversed by the GC.
/// </param>
public sealed record HeapMeasurement(
    [property: JsonPropertyName("iterations")] int Iterations,
    [property: JsonPropertyName("baselineBytes")] long BaselineBytes,
    [property: JsonPropertyName("postBytes")] long PostBytes,
    [property: JsonPropertyName("netDeltaBytes")] long NetDeltaBytes,
    [property: JsonPropertyName("thresholdBytes")] long ThresholdBytes,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("collectibleBefore")] int CollectibleBefore,
    [property: JsonPropertyName("collectibleAfter")] int CollectibleAfter,
    [property: JsonPropertyName("contextReclaimed")] bool ContextReclaimed);

/// <summary>
/// Memory measurement harness for the compile-once / collectible-unload cycle (§5).
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="RunAsync"/> to run the full measurement protocol.  The method is
/// safe to call from any thread; it does not mutate any static state.
/// </para>
/// <para>
/// The trivial probe script (<c>Vars["probe"] = 1;</c>) is compiled exactly once at the
/// start of <see cref="RunAsync"/> via <see cref="RoslynScriptCompiler.CompileOnce"/>.
/// Each iteration then performs a complete load-unload cycle using
/// <see cref="RoslynScriptCompiler.RunIsolatedAsync"/> (one fresh
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> per call, always unloaded in
/// <c>finally</c>).  This is the leak-sensitive path: if the ALC lifecycle is broken, an
/// uncollectable assembly accumulates per iteration and the heap delta explodes; if it is
/// correct, the delta stays near zero regardless of iteration count.
/// </para>
/// </remarks>
public static class MemoryProbe
{
    /// <summary>
    /// The CSX source compiled for the probe.  It is intentionally trivial —
    /// the goal is to measure ALC lifecycle overhead, not script body cost.
    /// </summary>
    private const string ProbeScript = """
        Vars["probe"] = 1;
        """;

    /// <summary>
    /// Number of warm-up load-unload cycles executed before the baseline is taken.
    /// Enough to amortise Roslyn metadata caches and JIT compilation of the hot paths.
    /// </summary>
    private const int WarmUpIterations = 10;

    /// <summary>
    /// Number of quiescent GC passes (Collect + WaitForFinalizers) used at both the
    /// baseline and post-workload measurement points.
    /// </summary>
    private const int QuiescentGcPasses = 3;

    /// <summary>
    /// Runs the memory measurement protocol and returns a structured
    /// <see cref="HeapMeasurement"/> result.
    /// </summary>
    /// <param name="iterations">
    /// Number of full load-unload cycles to execute in the measured workload.
    /// Default 5,000 for the CI gate run; use a smaller number (e.g. 200) in unit
    /// tests to keep the suite fast.
    /// </param>
    /// <param name="thresholdBytes">
    /// Maximum acceptable <see cref="HeapMeasurement.NetDeltaBytes"/>.
    /// Default 2,000,000 bytes (2 MB).
    /// </param>
    /// <param name="ct">Cancellation token propagated into each script invocation.</param>
    /// <returns>
    /// A <see cref="HeapMeasurement"/> describing the outcome of the run.  Inspect
    /// <see cref="HeapMeasurement.Passed"/> and <see cref="HeapMeasurement.ContextReclaimed"/>
    /// to determine overall success.
    /// </returns>
    public static async Task<HeapMeasurement> RunAsync(
        int iterations = 5_000,
        long thresholdBytes = 2_000_000,
        CancellationToken ct = default)
    {
        // ── Step 1: compile ONCE ────────────────────────────────────────────────
        var compiled = RoslynScriptCompiler.CompileOnce(ProbeScript);

        // ── Step 2: warm up ─────────────────────────────────────────────────────
        // Run a handful of load-unload cycles through a NoInlining helper so the
        // JIT cannot keep any ALC reference alive on this frame.  Then force a
        // quiescent GC to flush all one-time costs before we take the baseline.
        for (var i = 0; i < WarmUpIterations; i++)
        {
            await RunIsolatedNoInlineAsync(compiled, $"warmup-{i}", ct).ConfigureAwait(false);
        }

        QuiescentGc();

        // ── Step 3: baseline ────────────────────────────────────────────────────
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
        var collectibleBefore = CountCollectibleAssemblies();

        // ── Step 4: workload ────────────────────────────────────────────────────
        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            await RunIsolatedNoInlineAsync(compiled, $"iter-{i}", ct).ConfigureAwait(false);
        }

        // ── Step 5: post measurement ────────────────────────────────────────────
        QuiescentGc();
        var postBytes = GC.GetTotalMemory(forceFullCollection: true);
        var collectibleAfter = CountCollectibleAssemblies();

        // ── Step 6: verdict ─────────────────────────────────────────────────────
        var netDelta = postBytes - baselineBytes;
        var passed = netDelta < thresholdBytes;
        var contextReclaimed = collectibleAfter <= collectibleBefore;

        return new HeapMeasurement(
            Iterations: iterations,
            BaselineBytes: baselineBytes,
            PostBytes: postBytes,
            NetDeltaBytes: netDelta,
            ThresholdBytes: thresholdBytes,
            Passed: passed,
            CollectibleBefore: collectibleBefore,
            CollectibleAfter: collectibleAfter,
            ContextReclaimed: contextReclaimed);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Thin <see cref="MethodImplOptions.NoInlining"/> wrapper around
    /// <see cref="RoslynScriptCompiler.RunIsolatedAsync"/> so that the JIT cannot keep
    /// any reference to the internal <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
    /// alive on the caller's stack frame after this method returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunIsolatedNoInlineAsync(
        CompiledScript compiled,
        string label,
        CancellationToken ct)
    {
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(compiled, globals, runLabel: label, cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Forces a quiescent GC state: multiple passes of Collect + WaitForFinalizers
    /// so that finalisers have run and any pending ALC unload callbacks have fired.
    /// </summary>
    private static void QuiescentGc()
    {
        for (var pass = 0; pass < QuiescentGcPasses; pass++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }
    }

    /// <summary>
    /// Returns the number of collectible assemblies currently registered in
    /// <see cref="AppDomain.CurrentDomain"/>.
    /// </summary>
    private static int CountCollectibleAssemblies()
        => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.IsCollectible);
}
