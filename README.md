# vouchfx

[![CI](https://github.com/tomas-rampas/vouchfx/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/tomas-rampas/vouchfx/actions/workflows/build.yml?query=branch%3Amain)
[![CodeQL](https://github.com/tomas-rampas/vouchfx/actions/workflows/codeql.yml/badge.svg?branch=main)](https://github.com/tomas-rampas/vouchfx/actions/workflows/codeql.yml?query=branch%3Amain)
[![Coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Ftomas-rampas%2Fvouchfx%2Fbadges%2Fcoverage-endpoint.json)](https://github.com/tomas-rampas/vouchfx/actions/workflows/build.yml?query=branch%3Amain)
[![NuGet (prerelease)](https://img.shields.io/nuget/vpre/vouchfx)](https://www.nuget.org/packages/vouchfx)
[![Core providers](https://img.shields.io/badge/providers-25_core-blue)](https://github.com/tomas-rampas/vouchfx-providers)
[![Docs](https://img.shields.io/badge/docs-GitHub_Pages-blue)](https://vouchfx.io/)
[![License](https://img.shields.io/github/license/tomas-rampas/vouchfx)](https://github.com/tomas-rampas/vouchfx/blob/main/LICENSE)

**End-to-end integration testing for distributed systems, authored in YAML.**

vouchfx compiles declarative `.e2e.yaml` tests into Turing-complete C# (CSX), runs them memory-safely
through Roslyn, and orchestrates the container topology they need with **.NET Aspire + Testcontainers**.
It tests one business transaction as it crosses a REST call, a Kafka event, a database mutation and an
outbound webhook — the seams where distributed systems actually break.

It is **not** a unit-test framework and **not** a UI/browser tool.

> **Status: `v1.0.0-rc.5`, published on [NuGet.org](https://www.nuget.org/packages/vouchfx/) and
> [GitHub Releases](https://github.com/tomas-rampas/vouchfx/releases).** The engine is feature-complete
> for v1.0; the language schema, provider SDK surface and event-wire contract are frozen and CI-gated.
> What remains before GA is real-world validation and stabilising the Provider SDK at 1.0.0 final —
> see the [roadmap](https://vouchfx.io/roadmap/).

## Install

```bash
dotnet tool install --global vouchfx --prerelease
```

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Running a suite also
needs a Docker daemon, since vouchfx starts real containers — but `vouchfx validate` and `vouchfx list`
are Docker-free, so authoring and checking suites works on a machine without one. Machines without a
.NET SDK can use the self-contained per-OS archives and MSI/deb/pkg installers attached to each
[release](https://github.com/tomas-rampas/vouchfx/releases).

## A test, in full

```yaml
metadata:
  name: getting-started-hello-world
  owner: vouchfx-newcomers
  tags: [getting-started, hello-world]

environment:
  services:
    whoami:                    # the system under test
      image: traefik/whoami
      httpPort: 80             # health-gated before any step runs

steps:
  - id: whoami-GET-api
    type: http.rest
    target: whoami
    method: GET
    path: /api
    expect:
      status: 200
    capture:
      hostname: "$.hostname"   # available to later steps as {hostname}
```

```bash
vouchfx run ./tests/e2e
```

That is the complete file — vouchfx pulls the image, starts the topology, waits for health, runs the
step, reports a verdict and tears everything down. The annotated original is
[`examples/getting-started/hello-world.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/getting-started/hello-world.e2e.yaml);
the [Getting Started guide](https://vouchfx.io/getting-started/) walks through it in 60 minutes.

**The document shape.** Four top-level sections, of which only `steps` is mandatory:

| Section | Purpose |
|---|---|
| `metadata` | Name, owner, tags, description — drives runner selection and reporting. No execution effect. |
| `environment` | `services` (the system under test), `dependencies` (managed Aspire resources), an optional `seed`, and registry/pull overrides. |
| `variables` | Constants pre-loaded into the shared context. |
| `steps` | Ordered; each has an `id`, `type`, and optional `capture`, `verifyMode`, `timeout`, `continueOnFailure`. |

State threads forward between steps through `capture` and `{placeholder}` substitution. Asynchronous
verification is engine-owned: set `verifyMode: RETRY` and the engine polls with bounded exponential
backoff (Polly v8) — authors never write `Thread.Sleep`.

## Why it is built this way

- **Compile once, isolate, unload.** `CSharpScript.EvaluateAsync` leaks an uncollectable assembly per
  call. vouchfx compiles each suite exactly once into a collectible `AssemblyLoadContext` and unloads
  it — verified at 5,000 load-unload cycles with ~1.3 KB net heap delta, and guarded permanently by a
  [CI memory-leak job](https://vouchfx.io/memory-harness/).
- **Four verdicts, never three.** **Pass**, **Fail**, **Environment error** (unhealthy container,
  image-pull or seed failure) and **Inconclusive** (timeout, unmet capture) stay distinct through the
  taxonomy, the reports and the exit codes. **Only `Fail` breaks CI by default** — conflating an
  environment error with a defect destroys trust in the tool. Two deliberate exceptions serve that
  same argument rather than retracting it. A suite that declares a `security:` block the engine cannot
  confirm exits non-zero whatever the flags say, because that is an assertion the author wrote, not an
  infrastructure flake, and treating it as opt-in-only would hand a team who forgot a flag a green
  pipeline on a security suite that verified nothing. A run that produced no verdict at all — any parse
  failure, or a suite refused before anything executed — does the same, for the same reason: nothing
  ran, so there is nothing to report as clean.
- **One event stream, many renderers.** A schema-versioned JSON Lines stream is the single substrate;
  the terminal, HTML, JUnit XML and `--events` outputs are all renderings of it, so they can never
  disagree. Each retry attempt is recorded individually, making a polling timeline renderable without
  re-running.
- **Frozen v1 contracts.** The language schema, the `Vouchfx.Sdk` provider surface and the event wire
  format are frozen byte-for-byte for the whole v1.x series, each enforced by a golden-file CI gate.
  Evolution within v1.x is additive only — what you build against today keeps working.
- **Topological parity.** The compiled delegate depends *only* on a typed `ScriptGlobalVariables`, and
  orchestration is its sole producer. That one contract is what lets a suite run unchanged on a laptop,
  in CI, or against a remote fabric.
- **Secrets as references.** `${secret:env/…}` and `${secret:vault/…}` resolve at run time,
  never at compile time, and return a typed `SecretString` with no value-returning `ToString()`. The
  reproducibility envelope hashes the reference, never the value. The redaction path has passed a
  penetration test.

## Providers

Steps are typed `<family>.<provider>` — *family* is intent, *provider* is technology
(`db-assert.postgres`, `mq-publish.kafka`). Providers are **compile-time, source-level plugins**: add a
project, implement the contract, and a reflective registry discovers it at startup — no runtime loader,
no sandbox. **Twenty-five Core providers ship across eleven families:**

| Family | Providers |
|---|---|
| `http` | `rest`, `soap` |
| `db-assert` | `postgres`, `mysql`, `sqlserver`, `mongodb`, `dynamodb` |
| `mq-publish` | `kafka`, `rabbitmq`, `nats`, `azureservicebus`, `redis` |
| `mq-expect` | `kafka`, `rabbitmq`, `nats`, `azureservicebus`, `redis` |
| `cache-assert` | `redis`, `elasticsearch` |
| `metrics-assert` | `prometheus` |
| `storage-assert` | `s3` |
| `trace-expect` | `otlp` |
| `mail-expect` | `smtp` |
| `webhook-listen` | `http` |
| `script` | `csharp` |

Everything is Apache-2.0 across both governance tiers (Core / Community), so providers move between
tiers without IP friction. Community providers live in the
[provider hub](https://providers.vouchfx.io/); the maintainer-awarded **Vouched** badge marks those
that have passed the published rubric. Writing your own is documented in the hub's
[implementation guide](https://providers.vouchfx.io/docs/implementing-a-provider.html).

## Running suites

```bash
vouchfx run                                   # everything at or below the current directory
vouchfx run ./tests/e2e                       # a specific directory
vouchfx run --tag smoke --owner team-a        # select by metadata (AND across dimensions)
vouchfx run --changed-since main              # only scenarios touched since a git ref
vouchfx run --parallel 4                      # each scenario owns its own topology
vouchfx run ./tests/users.e2e.yaml --watch    # re-run on save
vouchfx run ./tests --html ./report.html --junit ./results.xml
```

Five Docker-free subcommands round out the loop: `vouchfx validate` runs the full compile-time pipeline
(schema, parse, AST, provider binding, Roslyn) without starting anything; `vouchfx list` prints the
sealed step-type catalogue (with shape-level fields on `--json`); `vouchfx schema` emits the
composed v1 JSON Schema; `vouchfx scaffold` generates machine-drafted `.e2e.yaml` skeletons from a structured JSON intent; and `vouchfx plan` performs coverage-and-gap analysis over your declared suites,
run history, and available providers, emitting findings for coverage gaps (suite never run, step never
exercised, dependency not asserted, vocabulary missing, service missing HTTP step), history-health signals
(stale, flaky, fragile, inconclusive-prone), and identity ambiguity. Most take `--json` for tooling;
in-process hosts can use the public library APIs (`EngineExport`, `PlanExport`, `SuiteScaffolder`)
in `Vouchfx.Engine.Compilation` and `Vouchfx.Engine.Planning` instead of shelling out.

**Exit codes follow the verdict taxonomy:**

| Code | Meaning | Breaks CI? |
|---|---|---|
| `0` | Pass — or EnvironmentError/Inconclusive when not opted in | – |
| `1` | **Fail** — a genuine defect | **Always** |
| `2` | UsageError — bad option, missing path | Always |
| `3` | EnvironmentError | Only with `--fail-on-env-error` — except an unconfirmable `security:` declaration |
| `4` | Inconclusive | Only with `--fail-on-inconclusive` — except an unconfirmable `security:` declaration |
| `5` | Gaps found | Only with `vouchfx plan --fail-on-gap` |

Two exceptions are unconditional. A run in which *every* discovered scenario fails to parse exits 4.
And a suite declaring a `security:` block the engine cannot confirm exits non-zero with neither gating
flag set, at whichever code the run's own verdict names — 3 for an EnvironmentError, 4 for an
Inconclusive. Every other environment error still exits 0 by default; see
[CI integration](https://vouchfx.io/ci-integration/) for the full breakdown.

Full CLI coverage — every flag, the report formats, graceful shutdown for programmatic hosts — is in
[Getting Started](https://vouchfx.io/getting-started/).

## CI integration

vouchfx ships a reusable **GitHub Actions workflow** and an `include`-able **GitLab CI/CD template**,
both of which build the engine, run the suite and publish JUnit + HTML artefacts even when the run
fails.

```yaml
jobs:
  vouchfx-e2e:
    uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@v1-rc
    with:
      scenario-path: ./tests/e2e
```

See the **[CI integration reference](https://vouchfx.io/ci-integration/)** for every input, the GitLab
template's privileged-runner caveat, the floating-tag contract and the supply-chain pinning rules.

## Editor support

The [VSCode extension](https://github.com/tomas-rampas/vouchfx/tree/main/tools/vscode-vouchfx) binds
the frozen v1 JSON Schema to `*.e2e.yaml` files, giving step-type-aware **autocomplete, hover and
inline validation** as you author. A CI gate keeps the editor's schema byte-for-byte in step with what
the compiler accepts, so the editor can never suggest a construct the engine would reject. It also
provides **C# syntax highlighting** inside `script.csharp` blocks and **Test Explorer integration**
with per-step verdicts and failing-line decoration. Full in-block C# IntelliSense is a
[documented fast-follow](https://github.com/tomas-rampas/vouchfx/blob/main/tools/vscode-vouchfx/docs/csharp-intellisense.md).

## Accessibility

Every verdict is **always** rendered with a distinct text token (`PASS`, `FAIL`, `ENV_ERROR`,
`INCONCLUSIVE`) — a WCAG 1.4.1 guarantee that verdicts are never distinguished by colour alone. On an
interactive terminal, each also gets a colour-independent ASCII shape glyph (`[+]`, `[x]`, `[!]`,
`[?]`) plus ANSI colour as a redundant, sighted-only convenience. Piped, redirected, CI and test output
is plain text by default; `--no-decorations` or `NO_COLOR=1` forces plain text anywhere. The complete
WCAG 2.1 AA conformance record for both the terminal and HTML renderers is at
[vouchfx.io/accessibility](https://vouchfx.io/accessibility/).

### Scaffolding a suite skeleton

```bash
# Structured JSON intent → schema-valid .e2e.yaml skeleton (stdout or --output)
vouchfx scaffold --intent ./intent.json
vouchfx scaffold --intent ./intent.json --output ./draft.e2e.yaml
```

Intent is structured only (step types, ids, optional services/dependencies) — not free text. Free-text goals belong in an MCP host LLM; the engine stays deterministic. See [Getting started — Generator / suite scaffold](https://vouchfx.io/getting-started/#generator--suite-scaffold). Library equivalent: `SuiteScaffolder.Generate` in `Vouchfx.Engine.Compilation`.

## Telemetry

**Off by default — nothing is collected or sent unless you explicitly opt in** via
`vouchfx telemetry enable`. When enabled, it covers anonymous aggregates only: tool/engine/.NET
versions, verdict counts, which built-in Core step kinds ran, and startup timings. Test contents,
captured values, secrets, URLs, image names, scenario names and step IDs are **never** collected, and
custom-provider step kinds are bucketed under a constant `"custom"` key. Permanent CI gates prevent
sensitive fields from being added to the allowlist. Suppress per run with `--no-telemetry` or
`VOUCHFX_NO_TELEMETRY=1`; see [telemetry](https://vouchfx.io/telemetry/) for the exact allowlist and
storage locations.

## Documentation

| Start here | |
|---|---|
| [Getting Started](https://vouchfx.io/getting-started/) | Your first test in 60 minutes. |
| [vouchfx-samples](https://samples.vouchfx.io/) | Production-grade sample apps (C#, Python, Node.js, Java) with complete suites. |
| [Recipes](https://vouchfx.io/recipes/) | Task-oriented: SQL seeding, WireMock doubles, secrets, Kafka, CI. |
| [Common Patterns](https://vouchfx.io/common-patterns/) | File structure, state threading, scenario selection, multi-step workflows. |
| [Troubleshooting](https://vouchfx.io/troubleshooting/) | Real failure modes — Docker, the Aspire 20-second cold-start gotcha, captures, verdicts. |

| Reference | |
|---|---|
| [Language Reference](https://vouchfx.io/language-reference/) | Per-step-type fields. Generated from the schema, frozen by a gate — it cannot drift. |
| [CI Integration](https://vouchfx.io/ci-integration/) | The GitHub Actions workflow and GitLab template in full. |
| [Architecture Blueprint](https://vouchfx.io/01_Technical_Architecture_and_Engineering_Blueprint/) | How the system is built: layers, Aspire, the Roslyn memory model, security, providers. |
| [YAML DSL Specification](https://vouchfx.io/02_YAML_DSL_Specification_and_VSCode_Extension_Design/) | The complete grammar and JSON Schema. |
| [Roadmap](https://vouchfx.io/roadmap/) · [Changelog](https://vouchfx.io/changelog/) · [Governance](https://github.com/tomas-rampas/vouchfx/blob/main/GOVERNANCE.md) | Where it is going, what shipped, how decisions are made. |

### Related repositories

- **[vouchfx-providers](https://providers.vouchfx.io/)** — the community provider hub: PR-gated conformance testing and the Vouched rubric. ([source](https://github.com/tomas-rampas/vouchfx-providers))
- **[vouchfx-samples](https://samples.vouchfx.io/)** — four sample applications and complete end-to-end suites. ([source](https://github.com/tomas-rampas/vouchfx-samples))
- **[vouchfx-mcp](https://vouchfx-mcp.vouchfx.io/)** — an MCP server for AI-assisted test authoring (not yet published as a tool). ([source](https://github.com/tomas-rampas/vouchfx-mcp))
- **[vouchfx-telemetry-backend](https://telemetry.vouchfx.io/)** — the optional, self-hostable telemetry backend. ([source](https://github.com/tomas-rampas/vouchfx-telemetry-backend))

## Building from source

**Prerequisites:** the .NET 8 SDK (pinned in `global.json`) and, for the integration tests only, a
running Docker daemon. The unit tests need neither.

```bash
dotnet build vouchfx.sln                              # C# 11, nullable, warnings-as-errors
dotnet test vouchfx.sln --filter "requires!=docker"   # unit tests — fast, no Docker
dotnet test vouchfx.sln --filter "requires=docker"    # integration — Aspire topology
dotnet format --verify-no-changes                     # formatting gate
```

CI (`.github/workflows/build.yml`) runs a blocking **build** job (build + format + unit tests), a
blocking **memory-leak** job over 5,000 load-unload cycles, and a forward-looking **integration**
(Docker) job.

<details>
<summary><strong>Repository layout</strong></summary>

```
src/
  Engine/
    Vouchfx.Engine.Abstractions      ScriptGlobalVariables, the JSON Lines event envelope
    Vouchfx.Engine.Compilation       compile-once Roslyn path, collectible context, leak guards
    Vouchfx.Engine.Orchestration     headless Aspire AppHost, health-gated topology
  Sdk/
    Vouchfx.Sdk                      the frozen v1.x provider contract
  Providers/Core/                    twenty-five providers across eleven families
    Vouchfx.Steps.Http.*             HTTP (REST, SOAP)
    Vouchfx.Steps.DbAssert.*         database assertions
    Vouchfx.Steps.MqPublish.*        message publishing
    Vouchfx.Steps.MqExpect.*         message consumption and assertions
    Vouchfx.Steps.CacheAssert.*      cache/search assertions
    Vouchfx.Steps.MetricsAssert.*    metrics assertions (Prometheus)
    Vouchfx.Steps.StorageAssert.*    object-storage assertions (S3)
    Vouchfx.Steps.TraceExpect.*      distributed-trace assertions (OTLP)
    Vouchfx.Steps.MailExpect.*       email assertions (SMTP)
    Vouchfx.Steps.WebhookListen.*    webhook listening (HTTP)
    Vouchfx.Steps.Script.*           embedded code (C#)
tests/                               per-component and per-provider xUnit projects + the memory harness
docs/                                the authoritative design — single source of truth
examples/                            worked scenarios and two example providers
CLAUDE.md                            operating rules and hard invariants for this repository
```

**Reserved namespaces.** `Vouchfx.Engine.*` (engine internals) and `Vouchfx.Steps.*` (step providers)
are reserved; customer assemblies declaring them are refused at suite start-up, and version conflicts
fail fast at suite start rather than at runtime. `Vouchfx.Sdk` is the public provider-authoring
contract — consumed by providers, not part of the engine internals.

</details>

### Example scenarios

- **[`examples/reference/reference.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/reference/reference.e2e.yaml)** — the canonical four-technology scenario: one business transaction spanning a REST call with `capture`, a database mutation asserted with `db-assert.postgres`, a Kafka publish-and-consume under `verifyMode: RETRY`, and an outbound webhook. It also threads a `${secret:env/…}` bearer token. ([walkthrough](https://github.com/tomas-rampas/vouchfx/blob/main/examples/reference/README.md))
- **[`examples/ci-reference/smoke.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/ci-reference/smoke.e2e.yaml)** — the minimal happy path used by CI integration tests.
- **[`examples/getting-started/hello-world.e2e.yaml`](https://github.com/tomas-rampas/vouchfx/blob/main/examples/getting-started/hello-world.e2e.yaml)** — the annotated first scenario shown above.

## Contributing

**Writing a provider?** Start with the [Contributing guide](https://github.com/tomas-rampas/vouchfx/blob/main/CONTRIBUTING.md)
for the step-type model, the frozen v1 contract in `Vouchfx.Sdk`, and the composition rules.
[`Example.Steps.Hello`](https://github.com/tomas-rampas/vouchfx/tree/main/examples/Example.Steps.Hello)
is a minimal template; [`Example.Steps.Echo`](https://github.com/tomas-rampas/vouchfx/tree/main/examples/Example.Steps.Echo)
demonstrates all four mandatory interfaces. The hub's
[`Vouchfx.Community.JsonRpc`](https://github.com/tomas-rampas/vouchfx-providers/tree/main/community/Vouchfx.Community.JsonRpc)
is the canonical full reference implementation.

**Contributing to the engine?** See [Contributing](https://github.com/tomas-rampas/vouchfx/blob/main/CONTRIBUTING.md),
[Governance](https://github.com/tomas-rampas/vouchfx/blob/main/GOVERNANCE.md) and the
[roadmap](https://vouchfx.io/roadmap/). Anyone working in this repository — human or agent — must
honour the **hard invariants** in [CLAUDE.md](https://github.com/tomas-rampas/vouchfx/blob/main/CLAUDE.md).
Documentation prose is British English.

## Security

Report vulnerabilities via the private coordinated-disclosure process in
[SECURITY.md](https://github.com/tomas-rampas/vouchfx/blob/main/SECURITY.md).

Every release artefact carries a **keyless [Sigstore](https://sigstore.dev/) cosign signature**, an
**SLSA build-provenance attestation** and a **CycloneDX SBOM**; the nupkg is published to NuGet.org via
Trusted Publishing (OIDC). No long-lived signing or publishing keys are managed anywhere in the
pipeline. Verify a downloaded artefact with:

```bash
gh attestation verify vouchfx.1.0.0-rc.3.nupkg --repo tomas-rampas/vouchfx

cosign verify-blob vouchfx.1.0.0-rc.3.nupkg \
  --bundle vouchfx.1.0.0-rc.3.nupkg.cosign.bundle \
  --certificate-identity-regexp '^https://github\.com/tomas-rampas/vouchfx/\.github/workflows/release\.yml@.*' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

Certificate-based signing (Windows Authenticode, macOS notarisation, GPG) is secret-gated until those
certificates are provisioned; cosign signatures and SLSA provenance are present on every artefact from
day one, so verification is never blocked on them. Full procedures are in
[RELEASING.md](https://github.com/tomas-rampas/vouchfx/blob/main/RELEASING.md).

**Distribution note:** vouchfx ships as a `dotnet` global tool (nupkg, primary) plus multi-file
self-contained per-OS executables (`.tar.gz`, `.msi`, `.deb`, `.pkg`). Single-file builds are not
produced — the compiler discovers provider assemblies via `Assembly.Location`, which returns an empty
string in single-file mode.

> **A note on `validate`.** It compiles your test in-process using the same Roslyn compiler as `run`,
> with no sandboxing. That is safe for suites you author and trust, but not for actively hostile input:
> a determined hostile author can still crash or hang the in-process compiler (a stack overflow is
> uncatchable by design). The engine bounds `script.csharp` bodies at 64 KiB and documents at 1 MiB as
> resource limits, not as a defence. For untrusted input, isolate validation in a separate worker
> process — which is exactly what the vouchfx MCP server does.

## AI assistance

Portions of vouchfx were written with AI assistance (Claude, via Claude Code), used in the manner of a
junior engineer working under close review — never as an unsupervised author. The architecture, the
hard invariants in [CLAUDE.md](https://github.com/tomas-rampas/vouchfx/blob/main/CLAUDE.md), the
provider contract and every non-trivial design decision are the maintainer's; AI-drafted code and docs
were reviewed, tested against real spikes, and frequently corrected or rejected before merge. The
[Architecture Blueprint](https://vouchfx.io/01_Technical_Architecture_and_Engineering_Blueprint/)
records several such corrections — cases where a plausible-looking snippet (Aspire APIs, Roslyn script
constraints) turned out to be wrong against the pinned library versions. That scepticism toward AI
output is deliberate, ongoing policy, not a one-off caveat.

## Licence

Apache-2.0 — see [`LICENSE`](https://github.com/tomas-rampas/vouchfx/blob/main/LICENSE).
