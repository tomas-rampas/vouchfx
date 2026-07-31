# Contributing to vouchfx

Thank you for your interest in vouchfx. This guide explains how to build and submit a provider — a step type that vouchfx can execute — and the governance model for different tiers of provider maturity.

## Writing a vouchfx Provider

vouchfx is built on a **compile-time, source-level plugin model** — there is no runtime loader, no dynamic assembly loading, and no sandbox. If you want to add support for a new database, message broker, protocol, or assertion, you implement a provider and vouchfx discovers it automatically at startup.

### Getting Started

**Install the Provider SDK.** Reference the [`Vouchfx.Sdk`](https://www.nuget.org/packages/Vouchfx.Sdk) NuGet package in your project. This package is the frozen v1 contract — all interfaces and types you need to implement. NuGet consumption is live on NuGet.org; the snippets below use `1.0.0-alpha.9` as an example. `1.0.0` final arrives at v1.0 GA.

```xml
<PackageReference Include="Vouchfx.Sdk" Version="1.0.0-alpha.9" />
```

**Use the worked example as a template.** The repository contains [`examples/Example.Steps.Echo`](examples/Example.Steps.Echo) — a complete worked example that walks you through implementing a provider end-to-end, with a friction log and authoring journey documented in its README. [`Example.Steps.Hello`](examples/Example.Steps.Hello) is an even more minimal template: a non-Docker provider that emits a message and asserts it equals a constant, explicitly designed as a copyable skeleton. Start with Echo to see the full journey; copy Hello if you want to build from an ultra-minimal scaffold.

### The Step Type Model

Every step you author has a **step type** of the form `<family>.<provider>`:

- **Family** is the intent or capability — what the step *does* (`http`, `db-assert`, `mq-publish`, `webhook-listen`, `script`).
- **Provider** is the technology — which specific implementation (`rest`, `postgres`, `kafka`, `http`, `csharp`).

Example step types shipped in vouchfx Core: `http.rest`, `db-assert.postgres`, `script.csharp`, `mq-publish.kafka`, `mq-expect.kafka`, `webhook-listen.http`.

If you are building a new assertion on Postgres, your step type is `db-assert.postgres` (already exists — you would contribute to the existing provider). If you are building a new family entirely — say, a load-generation step — your type might be `load-gen.k6` (hypothetical).

### The Provider Contract

A provider is a single class marked with the `[StepProvider]` attribute and implementing four mandatory interfaces from `Vouchfx.Sdk`:

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

The model record must implement `IStepModel`, which is a marker interface from `Vouchfx.Sdk`.

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
| `Vouchfx.Engine.*` | The vouchfx engine (compilation, orchestration, verdict taxonomy, reporting). |
| `Vouchfx.Steps.*` | Core providers delivered by the vouchfx team. |

**Your provider must use its own namespace.** The worked example uses `Example.Steps.Hello`. A real provider you contribute might use `MyOrg.Steps.Kafka` or `Vouchfx.Community.Snowflake` — anything that is not `Vouchfx.Engine.*` or `Vouchfx.Steps.*`.

### Testing Your Provider

You have two complementary paths for testing:

#### (a) Unit-test the provider's contract pipeline

Reference the `Vouchfx.Sdk` NuGet package plus `Vouchfx.Sdk.Testing` in your test project:

```xml
<PackageReference Include="Vouchfx.Sdk" Version="1.0.0-alpha.9" />
<PackageReference Include="Vouchfx.Sdk.Testing" Version="1.0.0-alpha.9" />
```

You can then exercise your provider's `Bind`, `Validate`, and `Emit` stages directly using the public `Vouchfx.Sdk.Testing.Contexts` implementations:

```csharp
using Vouchfx.Sdk;
using Vouchfx.Sdk.Testing.Contexts;

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
using Vouchfx.Sdk.Testing;

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

**For Docker integration tests:** If your provider manages infrastructure (databases, message brokers), the worked example ([`examples/Example.Steps.Hello.Tests/HelloConsoleFixtureTests.cs`](examples/Example.Steps.Hello.Tests/HelloConsoleFixtureTests.cs)) shows the pattern you would use within the vouchfx repository for full orchestration testing. That fixture runs the engine's own compile-and-run pipeline end-to-end with a live Aspire topology. The `Vouchfx.Sdk.Testing.ProviderTestHarness` is the published, out-of-repo equivalent for dependency-free steps; a provider with infrastructure still needs a Docker integration test against the real engine to validate orchestration.

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

Hub-hosted Community providers are published as individual NuGet packages from the hub's packaging pipeline (pack gate + tag-driven publish workflow); the SDK is restorable from NuGet.org and the first community package (`Vouchfx.Community.JsonRpc`) is published, with the rest following as maintainers cut release tags. Authors whose provider does not yet meet the rubric remain listed but unbadged, and the rubric itself is the actionable feedback for earning the Vouched badge.

### Submitting Your Provider

1. **Develop** against the `Vouchfx.Sdk` NuGet package and the worked example.
2. **Write tests** following the fixture pattern; ensure they pass locally.
3. **Document** the provider with a README that covers use cases, known limitations, and any configuration.
4. **Publish** your provider as a NuGet package under Apache-2.0 (or submit as hub-hosted source).
5. **Announce** — open an issue on the [`vouchfx-providers` repository](https://github.com/tomas-rampas/vouchfx-providers) describing the provider; the maintainers will add it to the registry index and test compatibility.
6. **(Optional) Request the Vouched badge** — once listed, open a vouched-request issue on the [`vouchfx-providers` repository](https://github.com/tomas-rampas/vouchfx-providers) with your integration tests and security sign-off evidence. The maintainers will review and award the badge if the rubric is met.

### Questions?

Refer to:

- **Worked examples:** [`examples/Example.Steps.Echo`](examples/Example.Steps.Echo) — a complete, fully-documented provider with its authoring journey; [`Example.Steps.Hello`](examples/Example.Steps.Hello) is an even more minimal copyable template. The hub's first Community-tier provider, [`community/Vouchfx.Community.JsonRpc`](https://github.com/tomas-rampas/vouchfx-providers/tree/main/community/Vouchfx.Community.JsonRpc), is the canonical real-world reference, demonstrating a complete protocol provider with substitution, capture, negative testing and the four-verdict mapping; the hub's [implementation guide](https://providers.vouchfx.io/docs/implementing-a-provider.html) walks the end-to-end provider workflow.
- **The architecture blueprint:** [`docs/01_Technical_Architecture_and_Engineering_Blueprint.md`](docs/01_Technical_Architecture_and_Engineering_Blueprint.md) — section 13 covers provider architecture in detail, section 13.3.1 the CsxFragment composition rules, section 5.6 the reserved-namespace hygiene rule.
- **Governance:** [`GOVERNANCE.md`](GOVERNANCE.md) — who decides what enters Core, the Vouched badge rubric, commit rights, and dispute resolution.
- **This repository's rules:** [`CLAUDE.md`](CLAUDE.md) — the hard invariants every contributor must honour, including the provider contract and memory-model rules.

## Contributing to the Platform

If you want to contribute to the vouchfx engine itself (rather than writing a provider), start with the [public roadmap](docs/roadmap.md) for where the project is heading, and the architecture and specification documents in [`docs/`](docs/) for how the system is built. [`GOVERNANCE.md`](GOVERNANCE.md) describes how decisions are made.

All contributions must honour the hard invariants in [`CLAUDE.md`](CLAUDE.md). Documentation prose is British English.

## Licence

All contributions are made under the Apache-2.0 licence and must be compatible with it. See [`LICENSE`](LICENSE).

## Building the documentation site locally

The documentation site is built with **Material for MkDocs**, live at https://vouchfx.io/. Run all of the following from the repository root (the config's snippet paths resolve against the working directory):

```bash
# Install dependencies
py -3.12 -m pip install -r requirements-docs.txt

# Build the static site
mkdocs build --strict

# Serve locally with live reload
mkdocs serve
```

Pass `VOUCHFX_SITE_FACTS=offline` to skip live fact resolution when authoring offline (uses the fallback cache). Version numbers and registry counts are resolved at build time via `{{fact:...}}` tokens — the same tokens as before, now applied by MkDocs hooks rather than a standalone script. When writing documentation prose, do not hard-code version numbers — use a fact token or reference the mechanism ("the current release") so pages cannot silently rot.

The publication gate runs `python scripts/check_site.py _site` (on Windows, `py -3.12` works in place of `python`) to enforce the confidentiality boundary, detect unresolved facts, validate redirects, and require robots.txt and sitemap.xml. It also blocks any built-output reference to the retired GitHub Pages default host, and validates the landing page's og:image/twitter:image social-share card end to end (`check_og_image_asset` — see GitHub issues #297/#298 for the incident it closes): tag presence and domain-absolute URLs, the referenced asset's existence, a genuine PNG signature with an IHDR reporting exactly 1200×630 at ≤300 KB, and that `og:image:width`/`og:image:height`/`og:image:alt`/`twitter:card` are all present and agree with the real asset. `scripts/og-card/` is the committed source (a parameterised HTML template plus a render script) for regenerating that card — see its README before hand-editing `site/og-image.png`. It also confirms the AI Companion design doc's mermaid diagram actually rendered client-side, and that Material's mermaid CDN reference was rewritten to the pinned URL and nothing else slipped through (`check_mermaid_diagram_rendered`, `check_no_unpkg_mermaid_reference`, `check_pinned_mermaid_url_present`). This gate is required in CI (`.github/workflows/pages.yml`) and should be run before submitting documentation changes.

Diagrams use a fenced code block tagged with the `mermaid` language (see `docs/04_AI_Companion_Feasibility_and_Design.md` section 3.3 for a worked example). `mkdocs.yml`'s `pymdownx.superfences` `custom_fences` maps that fence to a `<pre class="mermaid">` container, which Material's theme bundle renders client-side — lazily, only on a page that actually has one. That bundle's built-in loader is hard-coded to fetch mermaid from an unpinned `unpkg.com` major-version URL, so `scripts/site_hooks/pin_mermaid.py` — the last of the five build hooks listed in `mkdocs.yml`'s `hooks:` — rewrites that one URL post-build to a pinned jsdelivr version (currently mermaid 11.16.0), without disturbing the lazy, per-page fetch itself. The three publication-gate checks named above exist because none of this is visible to `mkdocs build --strict`: mermaid is parsed entirely client-side, so a broken fence, an unpinned CDN reference, or a mis-pinned replacement URL would otherwise all build clean and only fail silently in a visitor's browser.

Sibling repositories trigger a rebuild here through the `repository_dispatch` trigger in `.github/workflows/pages.yml` (the workflow's `notify` job is the outbound half — it tells the siblings when this repo's own docs change). `scripts/check_docs_drift.py` (run weekly and on demand by `.github/workflows/docs-drift.yml`) crawls all five project sites for broken links, leaked internal-planning terminology, and facts that have drifted out of sync with their live source; findings are filed to a single tracking issue labelled `docs-drift`.
