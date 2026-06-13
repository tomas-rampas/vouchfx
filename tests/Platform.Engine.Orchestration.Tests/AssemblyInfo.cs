// Platform.Engine.Orchestration.Tests — assembly-level xUnit configuration.
//
// WHY intra-assembly parallelism is disabled here
// ──────────────────────────────────────────────
// Several docker-gated tests in this assembly each stand up a REAL Aspire
// topology via DistributedApplication.StartAsync:
//
//   • DbAssertPostgresDockerTests / SeedApplierDockerTests / RespawnResetProofTests
//                          — Postgres-backed topologies.
//   • VaultSecretSourceDockerTests — a dev-mode Vault container (§17).
//   • HeadlessTopologyTests / SuiteTopologyTests / TopologyTeardownLeakTests /
//     Sprint04CapstoneTests — topology lifecycle / teardown proofs.
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
// Cross-assembly parallelism (VSTest running this DLL alongside other test DLLs)
// is unaffected by this attribute; the integration CI job caps that separately
// via .github/integration.runsettings (MaxCpuCount=1).

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
