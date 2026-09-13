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
// topology FROM THIS ASSEMBLY starts at a time, comfortably inside DCP's window.
// (Only from this assembly: see the cross-assembly section below, which is the
// half this attribute cannot reach.)
//
// This mirrors the existing precedent in
// tests/Vouchfx.Engine.Compilation.Tests/AssemblyInfo.cs (which disables
// intra-assembly parallelism for the memory-probe flake).
//
// WHAT THIS ATTRIBUTE DOES NOT BUY: cross-assembly isolation
// ──────────────────────────────────────────────────────────
// It serialises rows WITHIN this DLL. Test hosts for OTHER assemblies DO run at
// the same time as this one.
//
// MEASURED, CI run 33904509538 (2026-09-04, integration job — the run behind
// issue #489): 37 test hosts announced "Test run for ..." between 18:22:01.481Z
// and 18:23:09.437Z, a 68-second span. Each duration below carries its KIND,
// because the two kinds differ by a minute and mixing them makes the block
// contradict itself:
//   • this assembly — banner 18:22:01.728Z to summary 19:05:59.833Z = 43m58s
//     WALL CLOCK; VSTest reported 43m27s of test EXECUTION on its summary line;
//   • Vouchfx.Engine.Runtime.Tests — banner 18:22:10.137Z to summary
//     18:36:35.436Z = 14m25s WALL CLOCK; VSTest reported 13m25s of execution.
// Runtime's window is contained in this one, so the OVERLAP IS Runtime's window:
// 14m25s. 37 hosts announcing inside 68 seconds while two of them run for 14 and
// 44 minutes makes sequential per-project execution arithmetically impossible —
// that, not any single duration, is the decisive fact.
//
// The claim that stood here until #489 — that `dotnet test <solution>` runs one
// test host per project SEQUENTIALLY, so this DLL never runs alongside another
// test DLL — was false. FOUR copies of it were live, and what each one carried
// differed:
//   • here and in Runtime.Tests/AssemblyInfo.cs — load-bearing for test
//     assertions, since rewritten;
//   • HeadlessTopologySelfHealTests.cs (this assembly) — load-bearing for a
//     DESIGN DECISION, not an assertion: it justified declaring no dedicated
//     xUnit collection around the NUGET_PACKAGES/ASPIRE_DCP_PATH mutation. That
//     conclusion survives on process scoping alone; the premise is corrected there;
//   • .github/workflows/build.yml — load-bearing for nothing executable. It was
//     documentation, and wrong documentation about scheduling is how the other
//     three stayed plausible.
//
// CONSEQUENCE FOR TEST AUTHORS, and it is the whole reason this paragraph exists:
// no row in this assembly may assume exclusive access to a machine-wide or
// user-wide resource — a per-user directory, a fixed port, a certificate store,
// an environment variable another PROCESS would have to see (SetEnvironmentVariable
// is process-scoped). Assert on what the row itself owns and can name.
// DcpFlightRecorderDockerTests is the worked example: two of its rows key their
// capture-directory claims on the resource name each splices into its own
// topology, and the third — which declares no resource, so it can name nothing —
// proves the same property in-process instead of asserting on the directory.
//
// UNRESOLVED: the DCP ~20s startup-watchdog flake above is therefore only half
// contained. Topologies started by other assemblies can still overlap this one's,
// and nothing here prevents that; the attribute closes the in-assembly half only.
// That gap is being raised separately — do not read this note as saying it is fixed.

using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
