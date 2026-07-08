# Changelog

All notable changes to vouchfx are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The v1 language schema, provider SDK surface and
event-wire contract are frozen for the whole v1.x series and enforced by golden-file CI gates; within v1.x,
evolution is additive only.

vouchfx is pre-release: no version has been published yet. The `Unreleased` section below is the
delivered-capability record that will seed the v1.0.0 release notes.

## [Unreleased]

### Added

**Engine and compiler**

- Compile-once execution model: `.e2e.yaml` → AST → CSX → one Roslyn compilation into a collectible
  `AssemblyLoadContext`, invoked N times and unloaded to baseline. A memory-leak regression test over the
  full transitive closure of every Core provider is a permanent CI gate.
- Validation of every scenario against a single composed JSON Schema before any container starts.
- Cross-step state threading via `capture` (JSONPath and XPath) and `{placeholder}` substitution.
- Engine-owned asynchronous verification: `verifyMode: RETRY` with bounded exponential backoff (Polly v8)
  and Inconclusive-on-timeout semantics.
- Secrets as references (`${secret:env/…}`, `${secret:vault/…}`), resolved at step-execution time, redacted
  at the source; defence-in-depth scrubbing of secret values from step observations; the redaction path is
  penetration-tested.
- Declarative environment seeding (SQL, fixtures, warm-up) applied after the topology is healthy.
- Automatic per-scenario database reset for PostgreSQL dependencies between sequential scenarios (Respawn).

**Orchestration**

- Headless .NET Aspire AppHost + Testcontainers topology orchestration with health-gated startup and
  clean, race-free teardown.
- `services` (system under test, any container or csproj) and `dependencies` (managed resources) with
  `${conn:<dependency>}` connection references resolved in the consumer's network context.

**Providers**

- Twenty-one Core providers across nine step families: `http.rest`; `db-assert.postgres`, `db-assert.mysql`,
  `db-assert.sqlserver`, `db-assert.mongodb`; `mq-publish.kafka`, `mq-publish.rabbitmq`, `mq-publish.nats`,
  `mq-publish.azureservicebus`, `mq-publish.redis`; `mq-expect.kafka`, `mq-expect.rabbitmq`, `mq-expect.nats`,
  `mq-expect.azureservicebus`, `mq-expect.redis`; `cache-assert.redis`, `cache-assert.elasticsearch`;
  `mail-expect.smtp`; `webhook-listen.http`; `metrics-assert.prometheus`; `script.csharp`. Kafka steps support
  Avro with Confluent Schema Registry. `mq-publish.redis`/`mq-expect.redis` use Redis Streams (`XADD`/`XRANGE`).
  `metrics-assert.prometheus` is the first member of the new `metrics-assert` family: it scrapes a Prometheus
  text-exposition endpoint (typically the SUT's own `/metrics`) and asserts on one metric's numeric value,
  optionally scoped by a label subset, with `capture:` support for the matched value.
- The Provider SDK (`Platform.Sdk`): the frozen v1 contract (`IStepProvider`, `IStepBinder<T>`,
  `IStepValidator<T>`, `IStepCompiler<T>`, `IResourceContributor<T>`), optional extension interfaces
  (`IStepDiffRenderer`, `IHostResourceContributor`), a conformance test harness, worked example providers,
  and an SDK dry-run validation path.
- Provider-catalogue expansion: the DSL specification now names planned Verified and Community tier providers
  in the launch catalogue (§5.7, Table 5.1), and reserves four additional step families (realtime-expect,
  storage-assert, trace-expect, metrics-assert) with their intent fixed ahead of their first providers.
- The community provider hub (`vouchfx-providers`) ships the first Community-tier provider — `rpc.json-rpc`,
  hosted in the hub under `community/` and listed in the provider registry — a complete JSON-RPC 2.0 protocol
  implementation over HTTP with substitution, capture, negative testing and the four-verdict mapping, plus a
  Docker-free conformance test harness pattern (21 tests, no infrastructure dependencies); it doubles as the
  reference implementation for the hub's provider-implementation guide, with its own conformance CI lane.

**Verdicts and reporting**

- Four-outcome verdict taxonomy — Pass, Fail, Environment error, Inconclusive — kept distinct across
  taxonomy, reporting and exit codes; only `Fail` breaks CI by default.
- One schema-versioned JSON Lines event stream feeding every renderer: the terminal renderer (with
  `--no-decorations` plain-text mode), a self-contained WCAG 2.1 AA HTML report (`--html`), JUnit XML
  (`--junit`), and the raw stream itself (`--events`).
- Per-attempt recording of RETRY polling, rendered as a polling timeline; captured-variable provenance
  rendering.

**CLI and CI**

- The `vouchfx` CLI (dotnet global tool): scenario discovery and selection by tag, owner, path glob, or git
  change-set; parallel runs with topology-per-scenario isolation (`--parallel`); watch mode (`--watch`);
  taxonomy-aware exit codes with `--fail-on-env-error` / `--fail-on-inconclusive` opt-in gates.
- A reusable GitHub Actions workflow and an `include`-able GitLab CI template (static-validated), both
  publishing JUnit and HTML artefacts with identical gating semantics.

**Editor**

- A VSCode extension: schema-driven YAML autocomplete/validation bound to `*.e2e.yaml` (byte-for-byte
  schema-sync CI gate), C# syntax highlighting inside `script.csharp` blocks, and Test Explorer integration
  with per-step verdicts and failing-line decoration.

**Telemetry (opt-in)**

- Anonymous, aggregate, allowlist-only usage telemetry — off by default, controlled by
  `vouchfx telemetry enable|disable|status`, `--no-telemetry`, and `VOUCHFX_NO_TELEMETRY`; local JSON Lines
  outbox with optional backend drain. Privacy allowlist enforced by permanent CI gates.

**Distribution and supply chain**

- A release pipeline producing the nupkg, per-RID self-contained archives, MSI/deb/pkg installers, a
  CycloneDX SBOM and the VSCode extension — each artefact keyless-cosign-signed and SLSA-provenance-attested,
  with NuGet.org publication via Trusted Publishing (OIDC; no long-lived keys).
