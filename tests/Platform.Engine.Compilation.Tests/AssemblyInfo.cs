// Platform.Engine.Compilation.Tests — assembly-level xUnit configuration.
//
// WHY intra-assembly parallelism is disabled here
// ──────────────────────────────────────────────
// Several test classes in this assembly measure process-global state:
//
//   • MemoryProbeTests   — calls GC.GetTotalMemory(true) and counts collectible
//                          assemblies via AppDomain.CurrentDomain.GetAssemblies().
//   • ClosureMemoryProbeTests — same global counters.
//   • CollectibleResolutionTests — creates and unloads collectible ALCs whose
//                          GC reclamation is asserted via a WeakReference.
//   • RoslynScriptCompilerTests — compiles throwaway scripts that affect Roslyn's
//                          metadata cache, perturbing heap measurements.
//   • RootSchemaTests / SchemaCompositionTests — heavy concurrent Roslyn compiles
//                          that substantially alter the managed heap baseline.
//
// xUnit's default behaviour is to run all test classes within a single test DLL
// in parallel on multiple threads.  When the memory-probe tests run concurrently
// with any of the above, GC.GetTotalMemory and CountCollectibleAssemblies return
// values perturbed by the other tests' allocations and ALC creations, causing
// intermittent false failures (flakes) that are unrelated to the memory model
// being validated.
//
// Cross-assembly parallelism is unaffected: each test DLL runs as a separate
// dotnet process by the test runner, so disabling intra-assembly parallelism here
// has no impact on other test projects in the solution.

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
