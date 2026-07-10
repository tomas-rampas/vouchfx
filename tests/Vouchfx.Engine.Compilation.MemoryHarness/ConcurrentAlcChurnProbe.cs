// Vouchfx.Engine.Compilation.MemoryHarness — ConcurrentAlcChurnProbe (S08-T1, §5).
//
// The CONCURRENT memory-leak case for scenario parallelism.  This is the first time the engine
// runs many scenarios SIMULTANEOUSLY, so the permanent leak gate must prove that the per-scenario
// collectible AssemblyLoadContexts are ALL reclaimed when they are created + unloaded concurrently
// — not just in the sequential one-ALC-at-a-time path the closure probe already covers.
//
// Mechanism (no Docker):
//   • Drive the REAL internal ParallelSuiteRunner.RunParallelCoreAsync (via InternalsVisibleTo)
//     with a FAKE ScenarioCoreFunc — so the actual concurrency gate + fixed-slot gather + the
//     Task.WhenAll lifecycle are exercised, exactly as a real parallel run would, but WITHOUT any
//     Aspire topology.
//   • Each fake "scenario" creates its OWN collectible ALC, genuinely LOADS an assembly into it
//     (so the context owns real loaded IL, the leak-sensitive thing), records a WeakReference to
//     that ALC, then Unload()s it — the exact compile-once → isolate → unload discipline (§5),
//     fanned across the bounded pool.
//   • After the gather completes and a forced GC + WaitForFullGCComplete settle, EVERY recorded
//     WeakReference must be dead.  A live ALC means a concurrent scenario pinned its collectible
//     context — the precise regression this gate guards against.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Authoring;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Runtime;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Compilation.MemoryHarness;

/// <summary>
/// The structured result of the concurrent-ALC-churn probe.
/// </summary>
/// <param name="Scenarios">Number of concurrent fake scenarios driven through the parallel runner.</param>
/// <param name="MaxConcurrency">The concurrency degree the runner was configured with.</param>
/// <param name="AlcsCreated">Number of per-scenario collectible ALCs created (one per scenario).</param>
/// <param name="AlcsReclaimed">Number of those ALCs whose <see cref="WeakReference"/> was dead after GC.</param>
/// <param name="AllReclaimed">
/// <see langword="true"/> when every per-scenario collectible ALC was reclaimed (no leak).
/// </param>
public sealed record ConcurrentChurnMeasurement(
    int Scenarios,
    int MaxConcurrency,
    int AlcsCreated,
    int AlcsReclaimed,
    bool AllReclaimed);

/// <summary>
/// Drives <see cref="ParallelSuiteRunner.RunParallelCoreAsync"/> with a fake core that creates,
/// loads, and unloads a REAL collectible <see cref="AssemblyLoadContext"/> per scenario, then
/// proves every per-scenario ALC is reclaimed after the concurrent gather + a forced GC (§5).
/// </summary>
public static class ConcurrentAlcChurnProbe
{
    // The registry is built once over the HttpRest Core provider (the harness already references
    // it).  The fake core never inspects the registry/AST — the parallel runner only uses the
    // parallel-list lengths/order — so a trivial valid AST suffices for slot bookkeeping.
    private static readonly StepKindRegistry s_registry =
        StepKindRegistry.BuildAndFreeze(new[] { typeof(Vouchfx.Steps.HttpRest.HttpRestProvider).Assembly });

    private static readonly Func<string, JsonElement, string?> s_noDiff = (_, _) => null;

    /// <summary>
    /// Runs <paramref name="scenarios"/> concurrent fake scenarios through the parallel runner; each
    /// creates + loads + unloads its own collectible ALC.  Returns a measurement asserting whether
    /// every per-scenario ALC was reclaimed after a forced GC.
    /// </summary>
    /// <param name="scenarios">Number of concurrent fake scenarios (declaration-order slots).</param>
    /// <param name="maxConcurrency">The concurrency degree to drive the gate with.</param>
    /// <param name="ct">Cancellation token propagated into the runner.</param>
    public static async Task<ConcurrentChurnMeasurement> RunAsync(
        int scenarios = 64,
        int maxConcurrency = 4,
        CancellationToken ct = default)
    {
        var asts = new ScenarioAst[scenarios];
        var names = new string[scenarios];
        var yamls = new string[scenarios];
        var trivialAst = BuildTrivialAst();
        for (var i = 0; i < scenarios; i++)
        {
            asts[i] = trivialAst;
            names[i] = "churn-" + i.ToString(CultureInfo.InvariantCulture);
            yamls[i] = "steps: []";
        }

        // Thread-safe collection of weak-refs to each per-scenario collectible ALC.
        var weakRefs = new System.Collections.Concurrent.ConcurrentBag<WeakReference>();

        ParallelSuiteRunner.ScenarioCoreFunc fakeCore =
            (registry, yamlText, scenarioName, appHost, output, seedBaseDir, token) =>
                ChurnOneAlcAsync(weakRefs, scenarioName, token);

        var sw = new StringWriter();
        await ParallelSuiteRunner.RunParallelCoreAsync(
            s_registry,
            asts,
            names,
            yamls,
            appHostAssemblyName: null,
            output: sw,
            diffLookup: s_noDiff,
            maxConcurrency: maxConcurrency,
            runScenario: fakeCore,
            seedBaseDirectory: null,
            ct: ct).ConfigureAwait(false);

        // Force the GC to traverse the unload graph; a bounded settle loop tolerates multi-pass
        // finalisation on fast hardware while still failing on a genuine leak (which never dies).
        var alive = CountAlive(weakRefs);
        for (var pass = 0; pass < 10 && alive > 0; pass++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.WaitForFullGCComplete();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            alive = CountAlive(weakRefs);
        }

        var created = weakRefs.Count;
        var reclaimed = created - alive;
        return new ConcurrentChurnMeasurement(
            Scenarios: scenarios,
            MaxConcurrency: maxConcurrency,
            AlcsCreated: created,
            AlcsReclaimed: reclaimed,
            AllReclaimed: alive == 0);
    }

    /// <summary>
    /// One scenario's collectible-ALC lifecycle: create a fresh collectible
    /// <see cref="AssemblyLoadContext"/>, genuinely load an assembly into it (so the context owns
    /// real loaded IL — the leak-sensitive resource), record a <see cref="WeakReference"/> to the
    /// context, then <see cref="AssemblyLoadContext.Unload"/> it.  Returns a Pass verdict + a
    /// minimal buffer so the parallel runner's gather/render path runs exactly as in a real run.
    /// </summary>
    /// <remarks>
    /// Wrapped in <see cref="MethodImplOptions.NoInlining"/> so the JIT cannot keep the ALC or its
    /// loaded assembly alive on the caller's frame after the method returns — mirroring the
    /// discipline in <c>MemoryProbe.RunIsolatedNoInlineAsync</c>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(Verdict Verdict, List<string> Buffer)> ChurnOneAlcAsync(
        System.Collections.Concurrent.ConcurrentBag<WeakReference> weakRefs,
        string scenarioName,
        CancellationToken ct)
    {
        // A small, real delay so multiple scenarios genuinely overlap inside the gate (the whole
        // point of the CONCURRENT case — sequential ALC churn is already covered by the closure probe).
        await Task.Delay(2, ct).ConfigureAwait(false);

        LoadAndUnloadCollectibleAlc(weakRefs, scenarioName);

        // A minimal valid event buffer so the runner's RenderAndAggregate tail has something to fold.
        return (Verdict.Pass, new List<string>());
    }

    /// <summary>
    /// Creates a collectible ALC, loads this harness assembly's own on-disk image into it (so the
    /// context owns a genuinely-loaded assembly), records a weak-ref, and unloads the context.
    /// Isolated in its own non-inlined frame so no local keeps the ALC rooted after it returns.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LoadAndUnloadCollectibleAlc(
        System.Collections.Concurrent.ConcurrentBag<WeakReference> weakRefs,
        string contextName)
    {
        var alc = new AssemblyLoadContext(name: "churn-" + contextName, isCollectible: true);
        try
        {
            // Genuinely load an assembly INTO the collectible context from its on-disk path, so the
            // context owns real loaded IL (LoadFromAssemblyPath loads into THIS context, not the
            // default one) — this is what makes the reclaim assertion meaningful.  We pick the
            // small Abstractions assembly; any assembly with a stable file path works.
            var path = typeof(Verdict).Assembly.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var loaded = alc.LoadFromAssemblyPath(path);
                // Touch a type so the load is not optimised away.
                _ = loaded.GetName().Name;
            }

            weakRefs.Add(new WeakReference(alc));
        }
        finally
        {
            alc.Unload();
        }
    }

    private static int CountAlive(IEnumerable<WeakReference> weakRefs)
        => weakRefs.Count(w => w.IsAlive);

    /// <summary>Builds a trivially-valid AST reused for slot bookkeeping (never inspected by the fake core).</summary>
    private static ScenarioAst BuildTrivialAst()
    {
        var doc = YamlDocumentParser.Parse(
            "steps:\n  - id: s1\n    type: http.rest\n    target: x\n    method: GET\n    path: /\n    expect:\n      status: 200\n");
        return AstBuilder.Build(doc, s_registry);
    }
}
