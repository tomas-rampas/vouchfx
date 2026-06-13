# Platform.Sdk.Testing

An **out-of-repo provider test harness** for [vouchfx](https://github.com/tomas-rampas/vouchfx)
step providers. It lets a provider author run a single-step `.e2e.yaml` scenario
**end to end** — using only published packages and **without Docker** — to prove a
custom provider works against the real engine.

vouchfx compiles declarative `.e2e.yaml` integration tests into C# and orchestrates a
container topology to test distributed .NET systems end to end. A provider plugs a new
step kind (`<family>.<provider>`) into that pipeline. This harness exercises the exact
compile-and-run path the engine takes for a dependency-free step, so you can unit-test
your provider in CI.

**Scope:** the harness supports dependency-free single steps only. Providers requiring
infrastructure via `IResourceContributor` or `IHostResourceContributor`, or extra
compile references via `ICompileReferenceContributor`, must be exercised with a
Docker integration test against the real engine and CLI. The harness also does **not**
run the engine's startup-time reserved-namespace (`Platform.*`) or forbidden-scripting-API
guards — those apply at `vouchfx` CLI suite-load time, not in this single-step path.

**`verifyMode: RETRY` is honoured.** A step with `verifyMode: RETRY` is wrapped in the
engine-owned polling loop, exactly as the engine does: a satisfied assertion returns
`Verdict.Pass`, and a never-satisfied assertion polls until the step's `timeout` elapses
and then resolves to `Verdict.Inconclusive` — never `Verdict.Fail`. Keep the step's
`timeout` small so the test stays fast. The harness does **not** silently downgrade RETRY
to IMMEDIATE.

**Missing-outcome divergence (deliberate).** If your emitted block writes no `StepOutcome`
under `Vars[VarKeys.Outcome(...)]`, the harness **throws** an `InvalidOperationException`,
whereas the production engine treats an absent outcome as `Verdict.Inconclusive`. This is
on purpose: a correct provider always writes an outcome, so a missing one is a bug the loud
throw surfaces immediately rather than masking as a soft Inconclusive verdict.

## What it does

`ProviderTestHarness.RunSingleStepAsync` reproduces the engine pipeline for one step:

1. schema-validate the YAML against the composed language schema;
2. parse → build the AST;
3. resolve your provider from the reflective `StepKindRegistry` and drive it
   **type-erased** — `Bind` → `Validate` → `Emit` — exactly as the engine does, so it
   works for any provider model type;
4. assemble the emitted `CsxFragment` and **compile once** through Roslyn;
5. run the compiled step in an **isolated, collectible** `AssemblyLoadContext`;
6. read the step's `StepOutcome` back and return it as data.

Expected authoring failures are returned as **data, not exceptions**: a schema-rejected
document yields `Verdict == null` with `SchemaErrors` populated; a model-validation
failure yields `Verdict == null` with `ValidationErrors` populated. A genuine Roslyn
compile error in your emitted CSX (a bug in your provider) still **throws loudly**.

**Execution timeout.** The caller owns the execution timeout: pass a time-bounded
cancellation token (e.g. `new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token`)
to `RunSingleStepAsync`. Note that a CPU-bound infinite loop inside a provider's
emitted code cannot be cooperatively cancelled; a buggy provider can still hang and
must be bounded at the caller.

## Quick start

```csharp
using Platform.Engine.Abstractions;
using Platform.Sdk.Testing;

const string yaml = """
    steps:
      - id: say-hello
        type: hello.console
        message: "hello, vouchfx"
        expect: "hello, vouchfx"
    """;

// The assembly that contains your [StepProvider]-decorated class.
var providerAssembly = typeof(MyProvider).Assembly;

// Pass a time-bounded cancellation token to guard against provider hangs.
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

StepRunResult result =
    await ProviderTestHarness.RunSingleStepAsync(yaml, providerAssembly, stepId: "say-hello", cancellationToken: cts.Token);

Assert.True(result.IsPass);                  // Verdict == Verdict.Pass
Assert.Contains("hello, vouchfx", result.Observation);
```

The `Platform.Sdk.Testing.Contexts` namespace also ships public `TestBindingContext`,
`TestProjectContext`, and `TestCompileContext` — reusable implementations of the frozen
provider context interfaces — which the harness uses internally and which you can use to
unit-test an individual provider stage in isolation.

Construct contexts as follows:

```csharp
using Platform.Sdk;
using Platform.Sdk.Testing.Contexts;

// For Bind stage (no-arg)
var bindingCtx = new TestBindingContext();

// For Validate stage (optional declared-dependencies map, defaults to empty)
var projectCtx = new TestProjectContext(
    declaredDependencies: new Dictionary<string, string> { { "orders-db", "postgres" } });

// For Emit stage (step id, suite namespace, optional capture expressions)
var compileCtx = new TestCompileContext(
    stepId: "assert-order",
    suiteNamespace: "MyTests",
    captureExprs: new Dictionary<string, CaptureExpr>
    {
        { "orderId", new CaptureExpr(CaptureFormat.JsonPath, "$.orderId") }
    });
```

If you omit the optional arguments:
- `TestCompileContext` defaults `suiteNamespace` to `"VouchfxGenerated"` and `captureExprs` to empty.
- `TestProjectContext` defaults `declaredDependencies` to empty (correct for single-step, dependency-free scenarios).

## A note on versioning — testing surface, not the frozen contract

This package depends on three **engine** assemblies — `Platform.Engine.Abstractions`,
`Platform.Engine.Authoring`, and `Platform.Engine.Compilation` — in addition to
`Platform.Sdk`. Those engine assemblies are a **testing surface**: they are
**versioned, not frozen**, and evolve at the engine's release cadence.

The **frozen v1 provider contract** — the interfaces you implement on your provider
(`IStepProvider`, `IStepBinder<T>`, `IStepValidator<T>`, `IStepCompiler<T>`,
`IResourceContributor<T>`, plus `CsxFragment` and `StepKindId`) — lives in
**`Platform.Sdk`**, and only that contract is frozen for the v1.x engine series. Pin
your provider against `Platform.Sdk`; use `Platform.Sdk.Testing` only in your test
project.

Apache-2.0.
