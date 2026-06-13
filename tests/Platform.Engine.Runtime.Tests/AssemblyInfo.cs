// Platform.Engine.Runtime.Tests — assembly-level xUnit configuration.
//
// WHY intra-assembly parallelism is disabled here
// ──────────────────────────────────────────────
// Several docker-gated capstones in this assembly each stand up a REAL Aspire
// topology via DistributedApplication.StartAsync:
//
//   • Sprint06CapstoneTests / Sprint07CapstoneTests — Kafka/http.rest topologies.
//   • Sprint08ParallelCapstoneTests — stands up TWO Postgres topologies at once
//                          (ParallelSuiteRunner, maxConcurrency: 2).
//   • M2EndToEndTests / ProviderPipelineTests — Postgres-backed end-to-end runs.
//
// xUnit's default behaviour is to run all test classes within a single test DLL
// in parallel on multiple threads.  When several topology-starting classes run
// concurrently, a pile of containers start at once and none reaches "ready"
// within DCP's internal ~20s per-resource startup watchdog on a loaded CI
// runner — surfacing as an intermittent `EnvironmentError` on 'startup'
// ([HealthGate] … timeout of '00:00:20'), a CI flake unrelated to any defect.
// Disabling intra-assembly parallelism serialises those classes so only ONE
// topology starts at a time, comfortably inside DCP's window.
//
// This mirrors the existing precedent in
// tests/Platform.Engine.Compilation.Tests/AssemblyInfo.cs (which disables
// intra-assembly parallelism for the memory-probe flake).
//
// Note: Sprint08ParallelCapstoneTests' OWN internal concurrency is the engine's
// ParallelSuiteRunner SemaphoreSlim (maxConcurrency: 2), NOT xUnit — so this
// attribute does NOT change that test's behaviour; it only stops OTHER topology
// classes from racing it (which actually helps — it gets the runner to itself).
//
// Cross-assembly parallelism (VSTest running this DLL alongside other test DLLs)
// is unaffected by this attribute; the integration CI job caps that separately
// via .github/integration.runsettings (MaxCpuCount=1).

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
