# Contributing to vouchfx

Thank you for your interest in vouchfx. This guide explains how to build and submit a provider — a step type that vouchfx can execute — and the governance model for different tiers of provider maturity.

## Writing a vouchfx Provider

vouchfx is built on a **compile-time, source-level plugin model** — there is no runtime loader, no dynamic assembly loading, and no sandbox. If you want to add support for a new database, message broker, protocol, or assertion, you implement a provider and vouchfx discovers it automatically at startup.

### Getting Started

**Install the Provider SDK.** Reference the [`Platform.Sdk`](https://www.nuget.org/packages/Platform.Sdk) NuGet package (v1.0.0 or later, Apache-2.0) in your project. This package is the frozen v1 contract — all interfaces and types you need to implement.

```xml
<PackageReference Include="Platform.Sdk" Version="1.0.0" />
```

**Use the worked example as a template.** The repository contains [`examples/Example.Steps.Hello`](examples/Example.Steps.Hello) — a complete, minimal, non-Docker provider that emits a message and asserts it equals a constant. It is **explicitly designed as a copyable template**. Copy this project's structure and adapt it to your step type; the example is comprehensive enough that it will teach you the contract and specific enough that you will not learn useless patterns.

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

The worked example includes an integration-test fixture ([`examples/Example.Steps.Hello.Tests/HelloConsoleFixtureTests.cs`](examples/Example.Steps.Hello.Tests/HelloConsoleFixtureTests.cs)) that runs the provider end-to-end:

1. The reflective `StepKindRegistry` discovers the provider from its `[StepProvider]` attribute.
2. A `.e2e.yaml` step using the provider validates against the composed JSON Schema.
3. The provider's `Bind` → `Validate` → `Emit` pipeline produces a `CsxFragment`.
4. The fragment is assembled and compiled **once** (the engine's memory model: compile-once, isolate, unload).
5. The compiled delegate runs in an isolated `AssemblyLoadContext`.
6. The step writes its `StepOutcome` (verdict + duration + observation) into `Vars`.
7. The runner reads the outcome back from `Vars`.

Copy this fixture pattern to unit-test your own provider without Docker (if your provider has no infrastructure dependency) or with minimal Aspire setup (if it does). The fixture shows you exactly how the engine exercises a provider.

**Run tests locally:**

```bash
dotnet test examples/Example.Steps.Hello.Tests
```

**Include Docker integration tests** if your provider uses infrastructure (databases, brokers, etc.). The `HelloConsole` example has no infrastructure, so it runs without Docker. A real provider (e.g., a database assertion) would have a second test project with the `requires=docker` attribute to exercise the orchestration path.

### Governance Tiers

All vouchfx providers are governed in three tiers, all Apache-2.0. This is how the community grows the platform without the platform team implementing every database or broker.

**Core** — six providers shipped by the vouchfx team as part of the engine release:
- `http.rest`
- `db-assert.postgres`
- `script.csharp`
- `mq-publish.kafka`
- `mq-expect.kafka`
- `webhook-listen.http`

Core providers are bundled with the engine, versioned together, and fully supported by the platform team.

**Verified** — community providers that have passed a published rubric and are endorsed by the platform team:
- The provider's integration-test fixture passes on the official matrix (the engine's main branch plus the two preceding minor versions).
- The provider's README contains worked examples covering at least three realistic use cases and a known-limitations section.
- Security sign-off completed: credential handling reviewed for correctness, transitive dependency vulnerabilities scanned (zero high-severity at promotion), TLS defaults inspected, no telemetry phoning home, package signature verified.
- The licence is Apache-2.0 (or compatible) and the contributor has signed off via DCO (Developer Certificate of Origin).
- The provider declares a `MinEngineVersion` compatible with the engine's current major version.
- At least one platform-team maintainer has read the emitted CSX for the provider's representative steps and confirmed it follows the CsxFragment composition contract in the architecture blueprint's section 13.3.1.

Verified providers live in a separate repository (`verified-providers` or similar, future) with stricter review and are listed on the project website. They are **not** bundled with the engine but are discoverable and installable via NuGet, with automatic registry updates.

**Community** — all other providers, with no platform-team endorsement:
- They may be shipped by anyone, versioned independently, and installed via NuGet.
- The Verified rubric is the published feedback for what is needed to graduate to Verified.
- No governance gatekeeping — only the Apache-2.0 licence requirement and the reflective-discovery contract.

Authors whose provider does not yet meet the Verified rubric remain in Community, with no implied endorsement, and the rubric itself is the actionable feedback.

### Submitting Your Provider

1. **Develop** against the `Platform.Sdk` NuGet package and the worked example.
2. **Write tests** following the fixture pattern; ensure they pass locally.
3. **Document** the provider with a README that covers use cases, known limitations, and any configuration.
4. **Publish** your provider as a NuGet package under Apache-2.0.
5. **Announce** — open an issue on the vouchfx repository describing the provider; the maintainers will add it to the community provider index and test compatibility.
6. **(Optional) Seek Verified tier** — if your provider meets the rubric, submit a pull request to the verified-providers repository with your integration tests and security sign-off. The maintainers will review and promote it to Verified.

### Questions?

Refer to:

- **The worked example:** [`examples/Example.Steps.Hello`](examples/Example.Steps.Hello) — a complete, minimal, copyable provider.
- **The architecture blueprint:** [`docs/01_Technical_Architecture_and_Engineering_Blueprint.md`](docs/01_Technical_Architecture_and_Engineering_Blueprint.md) — section 13 covers provider architecture in detail, section 13.3.1 the CsxFragment composition rules, section 5.6 the reserved-namespace hygiene rule.
- **The MVP plan:** [`docs/03_MVP_Project_Plan.md`](docs/03_MVP_Project_Plan.md) — section 9.6 covers the Verified-tier rubric and governance model.
- **This repository's rules:** [`CLAUDE.md`](CLAUDE.md) — the hard invariants every contributor must honour, including the provider contract and memory-model rules.

## Contributing to the Platform

If you want to contribute to the vouchfx engine itself (rather than writing a provider), see the [delivery plan](plan/README.md), which sequences work by risk and phase. The plan references the architecture and specification documents; start there.

All contributions must honour the hard invariants in [`CLAUDE.md`](CLAUDE.md). Documentation prose is British English.

## Licence

All contributions are made under the Apache-2.0 licence and must be compatible with it. See [`LICENSE`](LICENSE).
