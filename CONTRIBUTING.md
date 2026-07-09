# Contributing to vouchfx

Thank you for your interest in vouchfx. This guide explains how to build and submit a provider — a step type that vouchfx can execute — and the governance model for different tiers of provider maturity.

## Writing a vouchfx Provider

vouchfx is built on a **compile-time, source-level plugin model** — there is no runtime loader, no dynamic assembly loading, and no sandbox. If you want to add support for a new database, message broker, protocol, or assertion, you implement a provider and vouchfx discovers it automatically at startup.

### Getting Started

**Install the Provider SDK.** Reference the [`Platform.Sdk`](https://www.nuget.org/packages/Platform.Sdk) NuGet package in your project. This package is the frozen v1 contract — all interfaces and types you need to implement. The SDK's first published version is a pre-release (the 1.0.0-alpha series — substitute the newest published version; the examples below use 1.0.0-alpha.3 as the anticipated first release to include the SDK packages); 1.0.0 final arrives at v1.0 GA.

```xml
<PackageReference Include="Platform.Sdk" Version="1.0.0-alpha.3" />
```

**Use the worked example as a template.** The repository contains [`examples/Example.Steps.Echo`](examples/Example.Steps.Echo) — a complete worked example that walks you through implementing a provider end-to-end, with a friction log and authoring journey documented in its README. [`Example.Steps.Hello`](examples/Example.Steps.Hello) is an even more minimal template: a non-Docker provider that emits a message and asserts it equals a constant, explicitly designed as a copyable skeleton. Start with Echo to see the full journey; copy Hello if you want to build from an ultra-minimal scaffold.

### The Step Type Model

Every step you author has a **step type** of the form `<family>.<provider>`:

- **Family** is the intent or capability — what the step *does* (`http`, `db-assert`, `mq-publish`, `webhook-listen`, `script`).
- **Provider** is the technology — which specific implementation (`rest`, `postgres`, `kafka`, `http`, `csharp`).

Example step types shipped in vouchfx Core: `http.rest`, `db-assert.postgres`, `script.csharp`, `mq-publish.kafka`, `mq-expect.kafka`, `webhook-listen.http`.

If you are building a new assertion on Postgres, your step type is `db-assert.postgres` (already exists — you would contribute to the existing provider). If you are building a new family entirely — say, a load-generation step — your type might be `load-gen.k6` (hypothetical).

### The Provider Contract

A provider is a single class marked with the `[StepProvider]` attribute and implementing four mandatory interfaces from `Platform.Sdk`:

| Interface | Purpose |
|-----------|---------|
| `IStepProvider` | Your provider's identity: the `Kind` (family + provider) and `Metadata` (version, min engine version, licence, authors). |
| `IStepBinder<TModel>` | Deserialise a step's YAML into a strongly-typed model record + supply its JSON Schema fragment. |
| `IStepValidator<TModel>` | Validate a bound model with author-friendly diagnostic messages. |
| `IStepCompiler<TModel>` | Emit the `CsxFragment` — the C# code — that runs inside the compiled delegate. |

Three optional interfaces exist for providers that manage infrastructure:

| Interface | Purpose |
|-----------|---------|
| `IResourceContributor<TModel>` | Declare Aspire resources (databases, message brokers, services) your step requires. |
| `IHostResourceContributor<TModel>` | Declare host-level resources (e.g. a listening port) your step provides to other steps. |
| `IStepDiffRenderer` | Optional: contribute to the rendered output when a step's `capture` has changed. |

**Your model is a strongly-typed record**, never a `Dictionary<string,object>`. This is what gives the binder, validator, and compiler a compile-time-checked surface to work against. See [`examples/Example.Steps.Hello/HelloConsoleModel.cs`](examples/Example.Steps.Hello/HelloConsoleModel.cs) for the pattern.

```csharp
public sealed record HelloConsoleModel(string Message, string Expected) : IStepModel;
```

The model record must implement `IStepModel`, which is a marker interface from `Platform.Sdk`.

The reflective `StepKindRegistry` discovers your provider at suite startup by scanning the assembly for classes marked `[StepProvider]`. You do not register it by hand; the registry finds it automatically.

### The Hard Rules: CsxFragment Composition (§13.3.1 of the Blueprint)

When your `IStepCompiler<TModel>` emits a `CsxFragment`, three strict rules prevent collisions between providers and keep the memory model sound:

**1. Three fields only.**

- **`RequiredUsings`** — a `IReadOnlyList<string>` of bare namespace strings (e.g., `"System"`, `"System.Text.Json"`). Never inline `using` lines. The engine collects all `RequiredUsings` across all providers, de-duplicates them, and emits them once at the top of the compiled script.
- **`RequiredHelpers`** — a `IReadOnlyList<string>` of nested `static class` definitions. Each class **must be prefixed with your provider id** to avoid collisions — e.g., `DbAssertPostgres_Helpers`, `HelloConsole_Helpers`. Every instance of a given helper must emit byte-identical source (helpers are de-duplicated by name); all per-step data must be passed as arguments, never captured.
- **`StatementBlock`** — exactly one brace-enclosed C# block (e.g., `{ var x = 1; return x; }`). This is where your step's logic lives.

**2. No `using var` in the Roslyn script body.**

The `using var` statement is illegal in a Roslyn script — it causes a parse error regardless of language version. If you need to dispose of a resource, use plain `var` + explicit `.Dispose()` in a `finally`:

```csharp
var resource = AcquireResource();
try
{
    // use resource
}
finally
{
    resource.Dispose();
}
```

**3. Emit bodies as C# 11 double-dollar raw strings (`$$"""…"""`).**

With the double-dollar form, a single `{` or `}` is a **literal brace** (the CSX block's own braces pass through verbatim), and `{{hole}}` is an interpolation hole the engine fills:

```csharp
var block = $$"""
{
    var message = {{messageLiteral}};
    // The above braces are literal; {{messageLiteral}} is a hole.
}
""";
```

Do **not** use the single-dollar form (`$"""…"""`), where `{id}` interpolates and `{{` is the literal brace — it fails as soon as your body contains a CSX code block (any `{` or `}`).

**4. Sanitise step ids before splicing.**

Step ids in YAML can contain hyphens, which are illegal in C# identifiers. Call `CsxFragment.SanitiseId(stepId)` before using the id in emitted variable names:

```csharp
var safeId = CsxFragment.SanitiseId(ctx.StepId); // "my-step-id" → "my_step_id"
var variableName = $"__value_{safeId}";
```

**5. Cross-step state passes only through `Vars`.**

The emitted CSX runs in a collectible `AssemblyLoadContext` and touches the host environment **only** through the engine-injected `Vars` global (a `ScriptGlobalVariables` dictionary). Your step must:

- **Read** earlier steps' captured state from `Vars` using keys defined by those steps.
- **Write** your step's outcome and any captured values into `Vars` under keys the engine provides (e.g., `VarKeys.Outcome(safeId)`).

Never assume variables declared by another provider will be in scope. Never use static handles to reach back to the host engine.

### Reserved Namespaces (§5.6 of the Blueprint)

Two namespace prefixes are **reserved** and will be **refused at suite startup** if a customer DLL declares them:

| Prefix | Owner |
|--------|-------|
| `Platform.Engine.*` | The vouchfx engine (compilation, orchestration, verdict taxonomy, reporting). |
| `Platform.Steps.*` | Core providers delivered by the vouchfx team. |

**Your provider must use its own namespace.** The worked example uses `Example.Steps.Hello`. A real provider you contribute might use `MyOrg.Steps.Kafka` or `Community.Steps.Snowflake` — anything that is not `Platform.Engine.*` or `Platform.Steps.*`.

### Testing Your Provider

You have two complementary paths for testing:

#### (a) Unit-test the provider's contract pipeline

Reference the `Platform.Sdk` NuGet package plus `Platform.Sdk.Testing` in your test project:

```xml
<PackageReference Include="Platform.Sdk" Version="1.0.0-alpha.3" />
<PackageReference Include="Platform.Sdk.Testing" Version="1.0.0-alpha.3" />
```

You can then exercise your provider's `Bind`, `Validate`, and `Emit` stages directly using the public `Platform.Sdk.Testing.Contexts` implementations:

```csharp
using Platform.Sdk;
using Platform.Sdk.Testing.Contexts;

// Test just the Emit stage with a TestCompileContext
var ctx = new TestCompileContext(
    stepId: "my-step",
    suiteNamespace: "MyProviderTests",
    captureExprs: null); // Omit to use an empty capture map

var fragment = myProvider.Emit(model, ctx);

// Assert on the fragment's required usings, helpers, and code generation
Assert.Contains("System.Text.Json", fragment.RequiredUsings);
```

The `TestCompileContext` constructor takes:
- `stepId` (string, required)
- `suiteNamespace` (string, optional, defaults to `"VouchfxGenerated"`)
- `captureExprs` (`IReadOnlyDictionary<string, CaptureExpr>?`, optional, defaults to empty)

#### (b) Run end-to-end without Docker using `ProviderTestHarness`

For providers with no infrastructure dependency, use the `ProviderTestHarness` to run a complete single-step scenario — schema validation, binding, validation, emission, compilation, and execution — all in one call:

```csharp
using Platform.Sdk.Testing;

const string yaml = """
    steps:
      - id: my-step
        type: myprovider.kind
        field1: value1
    """;

var result = await ProviderTestHarness.RunSingleStepAsync(
    yaml,
    typeof(MyProvider).Assembly,
    stepId: "my-step");

Assert.True(result.IsPass);
Assert.Empty(result.SchemaErrors);
Assert.Empty(result.ValidationErrors);
```

Expected failures (schema or model validation) return `Verdict == null` with the error list populated; a genuine Roslyn compile error throws. This end-to-end path is dependency-free — no Docker needed.

**For Docker integration tests:** If your provider manages infrastructure (databases, message brokers), the worked example ([`examples/Example.Steps.Hello.Tests/HelloConsoleFixtureTests.cs`](examples/Example.Steps.Hello.Tests/HelloConsoleFixtureTests.cs)) shows the pattern you would use within the vouchfx repository for full orchestration testing. That fixture runs the engine's own compile-and-run pipeline end-to-end with a live Aspire topology. The `Platform.Sdk.Testing.ProviderTestHarness` is the published, out-of-repo equivalent for dependency-free steps; a provider with infrastructure still needs a Docker integration test against the real engine to validate orchestration.

**Run your dependency-free tests locally:**

```bash
dotnet test YourProvider.Tests -c Release --filter "requires!=docker"
```

**Include Docker integration tests** if your provider uses infrastructure. You would author those tests against the engine's topology in the repository's own test suite, or ship your provider as a separate repository with its own Docker-based integration fixture.

### Governance Tiers

All vouchfx providers are governed in two tiers, all Apache-2.0. This is how the community grows the platform without the platform team implementing every database or broker.

**Core** — twenty-five providers shipped by the vouchfx team as part of the engine release, organised across eleven families:
- HTTP: `http.rest`, `http.soap`
- Database assertion: `db-assert.postgres`, `db-assert.sqlserver`, `db-assert.mysql`, `db-assert.mongodb`, `db-assert.dynamodb`
- Cache and search assertion: `cache-assert.redis`, `cache-assert.elasticsearch`
- Message publishing: `mq-publish.kafka`, `mq-publish.rabbitmq`, `mq-publish.nats`, `mq-publish.azureservicebus`, `mq-publish.redis`
- Message expectation: `mq-expect.kafka`, `mq-expect.rabbitmq`, `mq-expect.nats`, `mq-expect.azureservicebus`, `mq-expect.redis`
- Webhook listening: `webhook-listen.http`
- Email assertion: `mail-expect.smtp`
- Metrics assertion: `metrics-assert.prometheus`
- Storage assertion: `storage-assert.s3`
- Distributed tracing assertion: `trace-expect.otlp`
- Scripting: `script.csharp`

Core providers are bundled with the engine, versioned together, and fully supported by the platform team. The authoritative list is always the composed JSON Schema generated by the engine (see `docs/02 §8.2`).

**Community** — providers listed in the [`vouchfx-providers` repository hub](https://github.com/tomas-rampas/vouchfx-providers), either in external repositories or as hub-hosted source. Community providers are **not** bundled with the engine but are discoverable in the registry. The optional **Vouched badge** recognises providers that have passed a published rubric and were reviewed by a maintainer:

- The provider's integration-test fixture passes on the official matrix (the engine's main branch plus the two preceding minor versions).
- The provider's README contains worked examples covering at least three realistic use cases and a known-limitations section.
- Security sign-off completed: credential handling reviewed for correctness, transitive dependency vulnerabilities scanned (zero high-severity at review), TLS defaults inspected, no telemetry phoning home, package signature verified.
- The licence is Apache-2.0 (or compatible) and the contributor has signed off via DCO (Developer Certificate of Origin).
- The provider declares a `MinEngineVersion` compatible with the engine's current major version.
- At least one platform-team maintainer has read the emitted CSX for the provider's representative steps and confirmed it follows the CsxFragment composition contract in the architecture blueprint's section 13.3.1.

Hub-hosted Community providers will be published as individual NuGet packages from the hub's packaging pipeline (pack gate + tag-driven publish workflow). Provider packages publish once the SDK is restorable from NuGet.org. Authors whose provider does not yet meet the rubric remain listed but unbadged, and the rubric itself is the actionable feedback for earning the Vouched badge.

### Submitting Your Provider

1. **Develop** against the `Platform.Sdk` NuGet package and the worked example.
2. **Write tests** following the fixture pattern; ensure they pass locally.
3. **Document** the provider with a README that covers use cases, known limitations, and any configuration.
4. **Publish** your provider as a NuGet package under Apache-2.0 (or submit as hub-hosted source).
5. **Announce** — open an issue on the [`vouchfx-providers` repository](https://github.com/tomas-rampas/vouchfx-providers) describing the provider; the maintainers will add it to the registry index and test compatibility.
6. **(Optional) Request the Vouched badge** — once listed, open a vouched-request issue on the [`vouchfx-providers` repository](https://github.com/tomas-rampas/vouchfx-providers) with your integration tests and security sign-off evidence. The maintainers will review and award the badge if the rubric is met.

### Questions?

Refer to:

- **Worked examples:** [`examples/Example.Steps.Echo`](examples/Example.Steps.Echo) — a complete, fully-documented provider with its authoring journey; [`Example.Steps.Hello`](examples/Example.Steps.Hello) is an even more minimal copyable template. The hub's first Community-tier provider, [`community/Community.Steps.JsonRpc`](https://github.com/tomas-rampas/vouchfx-providers/tree/main/community/Community.Steps.JsonRpc), is the canonical real-world reference, demonstrating a complete protocol provider with substitution, capture, negative testing and the four-verdict mapping; the hub's [implementation guide](https://tomas-rampas.github.io/vouchfx-providers/docs/implementing-a-provider.html) walks the end-to-end provider workflow.
- **The architecture blueprint:** [`docs/01_Technical_Architecture_and_Engineering_Blueprint.md`](docs/01_Technical_Architecture_and_Engineering_Blueprint.md) — section 13 covers provider architecture in detail, section 13.3.1 the CsxFragment composition rules, section 5.6 the reserved-namespace hygiene rule.
- **Governance:** [`GOVERNANCE.md`](GOVERNANCE.md) — who decides what enters Core, the Vouched badge rubric, commit rights, and dispute resolution.
- **This repository's rules:** [`CLAUDE.md`](CLAUDE.md) — the hard invariants every contributor must honour, including the provider contract and memory-model rules.

## Contributing to the Platform

If you want to contribute to the vouchfx engine itself (rather than writing a provider), start with the [public roadmap](docs/roadmap.md) for where the project is heading, and the architecture and specification documents in [`docs/`](docs/) for how the system is built. [`GOVERNANCE.md`](GOVERNANCE.md) describes how decisions are made.

All contributions must honour the hard invariants in [`CLAUDE.md`](CLAUDE.md). Documentation prose is British English.

## Licence

All contributions are made under the Apache-2.0 licence and must be compatible with it. See [`LICENSE`](LICENSE).
