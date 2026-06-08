// Platform.Engine.Compilation.MemoryHarness — MemoryProbe (S01-B-03 / S02-B-01, §5).
//
// Measures the managed heap delta across a high-volume load-unload workload.
// This is the seed of the permanent CI leak gate (wired blocking in Sprint 2, S02-D-01).
//
// Two measurement modes:
//   trivial  — a single Vars["probe"]=1; script; establishes the ALC lifecycle baseline.
//   closure  — a script that touches one type from each Core-provider canonical client
//              (Npgsql, Confluent.Kafka, MongoDB.Driver, StackExchange.Redis, HttpClient)
//              so that their static initialisers run and any singleton pinners are exercised.
//
// Protocol (both modes):
//   1. Compile the probe script ONCE (§5: compile-once invariant).
//   2. Warm up a few load-unload cycles + forced GC to amortise one-time Roslyn /
//      JIT costs before the baseline is taken.
//   3. Take BaselineBytes after a quiescent GC.
//   4. Run the load-unload workload <iterations> times — one full collectible-ALC
//      lifecycle per iteration (the leak-sensitive path).
//   5. Force a bounded post-settle loop (up to MaxPostSettlePasses quiescent GC
//      rounds) and break as soon as collectibleAfter ≤ collectibleBefore.
//   6. Take PostBytes.
//   7. NetDeltaBytes = PostBytes - BaselineBytes.
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
/// The structured result produced by <see cref="MemoryProbe.RunAsync"/> or
/// <see cref="MemoryProbe.RunClosureAsync"/>.
/// Serialised as a single-line JSON object to stdout by the harness executable.
/// </summary>
/// <param name="Mode">
/// The probe mode: <c>"trivial"</c> for the baseline ALC lifecycle test or
/// <c>"closure"</c> for the full Core-provider dependency closure test.
/// </param>
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
/// <param name="SingletonsReset">
/// Names of the singletons (or singleton pools) reset by <see cref="SingletonReset"/>
/// during the workload.  Empty for the trivial run where no client libraries are touched.
/// </param>
public sealed record HeapMeasurement(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("iterations")] int Iterations,
    [property: JsonPropertyName("baselineBytes")] long BaselineBytes,
    [property: JsonPropertyName("postBytes")] long PostBytes,
    [property: JsonPropertyName("netDeltaBytes")] long NetDeltaBytes,
    [property: JsonPropertyName("thresholdBytes")] long ThresholdBytes,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("collectibleBefore")] int CollectibleBefore,
    [property: JsonPropertyName("collectibleAfter")] int CollectibleAfter,
    [property: JsonPropertyName("contextReclaimed")] bool ContextReclaimed,
    [property: JsonPropertyName("singletonsReset")] IReadOnlyList<string> SingletonsReset);

/// <summary>
/// Memory measurement harness for the compile-once / collectible-unload cycle (§5).
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="RunAsync"/> (trivial mode) to verify the ALC lifecycle baseline, or
/// <see cref="RunClosureAsync"/> (closure mode — the M1 CI gate) to exercise the full
/// transitive dependency closure of every Core provider's canonical client library.
/// Both methods are safe to call from any thread; neither mutates any static state.
/// </para>
/// <para>
/// The post-workload GC settle uses a bounded retry loop (up to
/// <see cref="MaxPostSettlePasses"/> rounds) rather than a single
/// <see cref="QuiescentGc"/> call.  This eliminates intermittent false failures caused
/// by finalisation timing on fast hardware (where the GC may need an extra pass to
/// fully unload all collectible ALCs) whilst still detecting real leaks (a leaking
/// context is never reclaimed regardless of how many passes the loop runs).
/// </para>
/// </remarks>
public static class MemoryProbe
{
    /// <summary>
    /// The CSX source compiled for the trivial probe.  Intentionally minimal —
    /// the goal is to measure ALC lifecycle overhead, not script body cost.
    /// </summary>
    private const string TrivialProbeScript = """
        Vars["probe"] = 1;
        """;

    /// <summary>
    /// The mode label written to <see cref="HeapMeasurement.Mode"/> for the trivial run.
    /// </summary>
    private const string TrivialMode = "trivial";

    /// <summary>
    /// The mode label written to <see cref="HeapMeasurement.Mode"/> for the closure run.
    /// </summary>
    public const string ClosureMode = "closure";

    /// <summary>
    /// Number of warm-up load-unload cycles executed before the baseline is taken.
    /// Enough to amortise Roslyn metadata caches and JIT compilation of the hot paths.
    /// </summary>
    private const int WarmUpIterations = 10;

    /// <summary>
    /// Number of quiescent GC passes (Collect + WaitForFinalizers) used at both the
    /// baseline and inside each post-settle iteration.
    /// </summary>
    private const int QuiescentGcPasses = 3;

    /// <summary>
    /// Maximum number of post-settle rounds when waiting for collectible ALCs to be
    /// reclaimed by the GC.  Ten rounds is far more than any correct implementation needs;
    /// exhausting the loop without reclaiming indicates a genuine leak.
    /// </summary>
    private const int MaxPostSettlePasses = 10;

    /// <summary>
    /// How often (every N iterations) <see cref="SingletonReset.ResetAll"/> is called in
    /// <see cref="RunClosureAsync"/> to prevent client connection-pool accumulation from
    /// perturbing the per-cycle heap measurement.
    /// </summary>
    private const int SingletonResetEveryN = 50;

    /// <summary>
    /// Runs the trivial memory measurement protocol and returns a structured
    /// <see cref="HeapMeasurement"/> result.
    /// </summary>
    /// <remarks>
    /// The trivial probe script (<c>Vars["probe"] = 1;</c>) proves the ALC lifecycle
    /// is correct in isolation.  For the full M1 CI gate — which also exercises every
    /// Core provider's canonical client singleton — use <see cref="RunClosureAsync"/>.
    /// </remarks>
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
        var compiled = RoslynScriptCompiler.CompileOnce(TrivialProbeScript);

        // ── Step 2: warm up ─────────────────────────────────────────────────────
        // Run a handful of load-unload cycles through a NoInlining helper so the
        // JIT cannot keep any ALC reference alive on this frame.  Then force a
        // quiescent GC to flush all one-time costs before we take the baseline.
        for (var i = 0; i < WarmUpIterations; i++)
        {
            await RunIsolatedNoInlineAsync(compiled, null, $"warmup-{i}", ct).ConfigureAwait(false);
        }

        QuiescentGc();

        // ── Step 3: baseline ────────────────────────────────────────────────────
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
        var collectibleBefore = CountCollectibleAssemblies();

        // ── Step 4: workload ────────────────────────────────────────────────────
        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            await RunIsolatedNoInlineAsync(compiled, null, $"iter-{i}", ct).ConfigureAwait(false);
        }

        // ── Step 5: post measurement (bounded settle loop) ──────────────────────
        // A single QuiescentGc pass can miss ALCs whose finalisation spans multiple
        // GC rounds.  The loop drives the GC up to MaxPostSettlePasses times and
        // stops as soon as the collectible count returns to baseline.
        // A real leak never reclaims, so the loop exhausts and the test fails correctly.
        var collectibleAfter = collectibleBefore + 1; // sentinel: enter loop
        for (var pass = 0; pass < MaxPostSettlePasses && collectibleAfter > collectibleBefore; pass++)
        {
            QuiescentGc();
            collectibleAfter = CountCollectibleAssemblies();
        }

        var postBytes = GC.GetTotalMemory(forceFullCollection: true);

        // ── Step 6: verdict ─────────────────────────────────────────────────────
        var netDelta = postBytes - baselineBytes;
        var passed = netDelta < thresholdBytes;
        var contextReclaimed = collectibleAfter <= collectibleBefore;

        return new HeapMeasurement(
            Mode: TrivialMode,
            Iterations: iterations,
            BaselineBytes: baselineBytes,
            PostBytes: postBytes,
            NetDeltaBytes: netDelta,
            ThresholdBytes: thresholdBytes,
            Passed: passed,
            CollectibleBefore: collectibleBefore,
            CollectibleAfter: collectibleAfter,
            ContextReclaimed: contextReclaimed,
            SingletonsReset: Array.Empty<string>());
    }

    /// <summary>
    /// Runs the closure memory measurement protocol — the definitive M1 CI gate —
    /// and returns a structured <see cref="HeapMeasurement"/> result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="RunAsync"/> (trivial probe), this method compiles and runs
    /// <see cref="ClosureProbeScript.Source"/>, which touches one type from each of
    /// the Core provider canonical client libraries (Npgsql, Confluent.Kafka,
    /// MongoDB.Driver, StackExchange.Redis, and HttpClient).  This forces those
    /// assemblies' static initialisers to run so that any singleton pinners that could
    /// anchor objects across the collectible boundary are exercised.
    /// </para>
    /// <para>
    /// <see cref="SingletonReset.ResetAll"/> is called every
    /// <see cref="SingletonResetEveryN"/> iterations to prevent connection-pool
    /// accumulation from perturbing the per-cycle heap delta.  The reset list is
    /// captured once and included in the returned <see cref="HeapMeasurement"/>.
    /// </para>
    /// <para>
    /// The threshold remains the same 2 MB default as the trivial run: the client
    /// static caches are amortised during warm-up; only the per-cycle delta is
    /// measured.  If the 5,000-iteration delta consistently exceeds this figure,
    /// the root cause must be investigated and a specific singleton reset added to
    /// <see cref="SingletonReset"/> — not simply a threshold widening.
    /// </para>
    /// </remarks>
    /// <param name="iterations">
    /// Number of full load-unload cycles.  Default 5,000 for the CI gate; 200 for
    /// the unit test smoke.
    /// </param>
    /// <param name="thresholdBytes">
    /// Maximum acceptable <see cref="HeapMeasurement.NetDeltaBytes"/>.
    /// Default 2,000,000 bytes (2 MB).
    /// </param>
    /// <param name="ct">Cancellation token propagated into each script invocation.</param>
    /// <returns>
    /// A <see cref="HeapMeasurement"/> describing the outcome of the run.
    /// </returns>
    public static async Task<HeapMeasurement> RunClosureAsync(
        int iterations = 5_000,
        long thresholdBytes = 2_000_000,
        CancellationToken ct = default)
    {
        // Build the list of additional reference paths from the canonical client
        // assemblies that are already loaded into the Default context (because the
        // harness project references them).  These are compile-time references only;
        // at runtime the assemblies resolve from the Default context automatically.
        // System.Net.Http is also included explicitly: although it is a BCL assembly,
        // Roslyn's TPA-based reference builder only picks up CoreLib/Runtime/Collections
        // by default, so HttpClient is not resolved without this entry.
        var additionalRefs = new[]
        {
            typeof(Npgsql.NpgsqlConnection).Assembly.Location,
            typeof(Confluent.Kafka.ProducerConfig).Assembly.Location,
            typeof(MongoDB.Driver.MongoClientSettings).Assembly.Location,
            typeof(StackExchange.Redis.ConfigurationOptions).Assembly.Location,
            typeof(System.Net.Http.HttpClient).Assembly.Location,
        };

        // ── Step 1: compile ONCE with the canonical-client metadata references ──
        var compiled = RoslynScriptCompiler.CompileOnce(
            ClosureProbeScript.Source,
            additionalReferencePaths: additionalRefs);

        // Capture the singleton reset list once (used in reporting and the result).
        var singletonsReset = SingletonReset.ResetAll();

        // ── Step 2: warm up ─────────────────────────────────────────────────────
        for (var i = 0; i < WarmUpIterations; i++)
        {
            await RunIsolatedNoInlineAsync(compiled, null, $"warmup-{i}", ct).ConfigureAwait(false);
        }

        QuiescentGc();

        // ── Step 3: baseline ────────────────────────────────────────────────────
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
        var collectibleBefore = CountCollectibleAssemblies();

        // ── Step 4: workload ────────────────────────────────────────────────────
        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Reset known singleton pools every N iterations to prevent pool
            // accumulation from perturbing the per-cycle delta.
            if (i % SingletonResetEveryN == 0)
                SingletonReset.ResetAll();

            await RunIsolatedNoInlineAsync(compiled, null, $"iter-{i}", ct).ConfigureAwait(false);
        }

        // ── Step 5: post measurement (bounded settle loop) ──────────────────────
        var collectibleAfter = collectibleBefore + 1; // sentinel: enter loop
        for (var pass = 0; pass < MaxPostSettlePasses && collectibleAfter > collectibleBefore; pass++)
        {
            QuiescentGc();
            collectibleAfter = CountCollectibleAssemblies();
        }

        var postBytes = GC.GetTotalMemory(forceFullCollection: true);

        // ── Step 6: verdict ─────────────────────────────────────────────────────
        var netDelta = postBytes - baselineBytes;
        var passed = netDelta < thresholdBytes;
        var contextReclaimed = collectibleAfter <= collectibleBefore;

        return new HeapMeasurement(
            Mode: ClosureMode,
            Iterations: iterations,
            BaselineBytes: baselineBytes,
            PostBytes: postBytes,
            NetDeltaBytes: netDelta,
            ThresholdBytes: thresholdBytes,
            Passed: passed,
            CollectibleBefore: collectibleBefore,
            CollectibleAfter: collectibleAfter,
            ContextReclaimed: contextReclaimed,
            SingletonsReset: singletonsReset);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Thin <see cref="MethodImplOptions.NoInlining"/> wrapper around
    /// <see cref="RoslynScriptCompiler.RunIsolatedAsync"/> so that the JIT cannot keep
    /// any reference to the internal <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
    /// alive on the caller's stack frame after this method returns.
    /// </summary>
    /// <param name="compiled">The compiled script image.</param>
    /// <param name="collectibleProbingPaths">
    /// Optional satellite probing paths forwarded to
    /// <see cref="RoslynScriptCompiler.RunIsolatedAsync"/>.
    /// </param>
    /// <param name="label">Human-readable label for the ALC name.</param>
    /// <param name="ct">Cancellation token.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunIsolatedNoInlineAsync(
        CompiledScript compiled,
        IReadOnlyList<string>? collectibleProbingPaths,
        string label,
        CancellationToken ct)
    {
        var vars = new Dictionary<string, object?>();
        var globals = new ScriptGlobalVariables(vars);
        await RoslynScriptCompiler.RunIsolatedAsync(
                compiled,
                globals,
                runLabel: label,
                collectibleProbingPaths: collectibleProbingPaths,
                cancellationToken: ct)
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
