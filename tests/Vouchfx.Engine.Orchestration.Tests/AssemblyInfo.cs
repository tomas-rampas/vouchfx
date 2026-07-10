// Vouchfx.Engine.Orchestration.Tests — assembly-level xUnit configuration.
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
// tests/Vouchfx.Engine.Compilation.Tests/AssemblyInfo.cs (which disables
// intra-assembly parallelism for the memory-probe flake).
//
// Cross-assembly overlap is not a concern under the pinned .NET 8 SDK
// (global.json 8.0.400) with single-TFM test projects: `dotnet test <solution>`
// runs ONE test host per project SEQUENTIALLY, so this DLL never runs alongside
// another test DLL. Together with the in-assembly serialisation above, at most
// one Aspire topology starts at a time.

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
