// Vouchfx.Engine.Runtime.Tests — assembly-level xUnit configuration.
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
// topology FROM THIS ASSEMBLY starts at a time, comfortably inside DCP's window.
// (Only from this assembly: see the cross-assembly section below, which is the
// half this attribute cannot reach.)
//
// This mirrors the existing precedent in
// tests/Vouchfx.Engine.Compilation.Tests/AssemblyInfo.cs (which disables
// intra-assembly parallelism for the memory-probe flake).
//
// Note: Sprint08ParallelCapstoneTests' OWN internal concurrency is the engine's
// ParallelSuiteRunner SemaphoreSlim (maxConcurrency: 2), NOT xUnit — so this
// attribute does NOT change that test's behaviour; it only stops OTHER topology
// classes from racing it (which actually helps — it gets the runner to itself).
// Residual: that capstone deliberately starts two Postgres topologies at once and
// so shares DCP's ~20s window with itself. This comment used to add that it was
// "the ONE remaining place" that happens, on an "otherwise-idle runner (everything
// else serialised)" — both halves rested on the cross-assembly claim disproved
// below, and neither is true: other assemblies' topologies can start alongside it.
// If the `00:00:20` flake recurs in the integration job, this capstone is still a
// suspect (fix there is its startup budget / a CI-gated maxConcurrency, not
// test-parallelism config), but it is no longer the only one.
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
//   • this assembly — banner 18:22:10.137Z to summary 18:36:35.436Z = 14m25s
//     WALL CLOCK; VSTest reported 13m25s of test EXECUTION on its summary line;
//   • Vouchfx.Engine.Orchestration.Tests — banner 18:22:01.728Z to summary
//     19:05:59.833Z = 43m58s WALL CLOCK; VSTest reported 43m27s of execution.
// This assembly's window is contained in that one, so the OVERLAP IS this
// assembly's whole window: every row here ran alongside that host. 37 hosts
// announcing inside 68 seconds while two of them run for 14 and 44 minutes makes
// sequential per-project execution arithmetically impossible — that, not any
// single duration, is the decisive fact.
//
// The claim that stood here until #489 — that `dotnet test <solution>` runs one
// test host per project SEQUENTIALLY, so this DLL never runs alongside another
// test DLL — was false. FOUR copies of it were live, and what each one carried
// differed:
//   • here and in Orchestration.Tests/AssemblyInfo.cs — load-bearing for test
//     assertions, since rewritten;
//   • HeadlessTopologySelfHealTests.cs (Orchestration.Tests) — load-bearing for a
//     DESIGN DECISION, not an assertion: it justified declaring no dedicated
//     xUnit collection around a NUGET_PACKAGES/ASPIRE_DCP_PATH mutation. That
//     conclusion survives on process scoping alone; the premise is corrected there;
//   • .github/workflows/build.yml — load-bearing for nothing executable. It was
//     documentation, and wrong documentation about scheduling is how the other
//     three stayed plausible.
//
// CONSEQUENCE FOR TEST AUTHORS, and it is the whole reason this paragraph exists:
// no row in this assembly may assume exclusive access to a machine-wide or
// user-wide resource — a per-user directory, a fixed port, a certificate store, an
// environment variable another PROCESS would have to see (SetEnvironmentVariable is
// process-scoped). Assert on what the row itself owns and can name. This assembly is
// on the producing side of that in at least two ways: its secured-probe-abort rows
// write DCP captures into the per-user capture directory, and some of them spawn
// `vouchfx run` CLI subprocesses that write there from a process no in-process
// redirect reaches. #489 is what that looked like from the consuming side.
//
// The certificate-store guard below is subject to the same limit and survives it for
// its own reason: it is scoped to certificates THIS PROCESS cached, not to the store
// being empty.
//
// UNRESOLVED: the DCP ~20s startup-watchdog flake above is therefore only half
// contained. Topologies started by other assemblies can still overlap this one's,
// and nothing here prevents that; the attribute closes the in-assembly half only
// (with the S08 parallel capstone excepted even there, by design — its concurrency
// is the engine's ParallelSuiteRunner, not xUnit). That gap is being raised
// separately — do not read this note as saying it is fixed.

// WHY a collection orderer is registered here
// ───────────────────────────────────────────
// #419's non-regression guard (CertificateStoreHygieneGuardTests) asserts that this process
// leaves no certificate of its own cached in Windows' intermediate-CA stores. That question is
// only meaningful once every certificate bed in the assembly has been disposed, so the guard's
// collection has to run LAST — which is what CertificateStoreGuardLastOrderer arranges, and
// which the DisableTestParallelization above turns into a real guarantee ON A HOST WHERE THE
// GUARD DOES ANYTHING: collections run sequentially, in the order the orderer returns. The guard
// itself is Windows-only (nothing else caches intermediates this way) and every job in
// .github/workflows/build.yml runs on ubuntu-latest, so in CI the ordering is arranged for a
// check that returns immediately. The enforcement surface is a developer's Windows machine.
//
// If xUnit ever fails to load the orderer it logs a diagnostic and falls back to its default
// order. The consequence is a guard that may run too early and find an already-swept store —
// a missed leak, not a false failure — because serialised execution means no bed is alive
// while it runs.
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: TestCollectionOrderer(
    "Vouchfx.Engine.Runtime.Tests.CertificateStoreGuardLastOrderer", "Vouchfx.Engine.Runtime.Tests")]
