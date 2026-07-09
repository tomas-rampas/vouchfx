# Changelog

All notable changes to vouchfx are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The v1 language schema, provider SDK surface and
event-wire contract are frozen for the whole v1.x series and enforced by golden-file CI gates; within v1.x,
evolution is additive only.

The first public pre-releases (`1.0.0-alpha.1`, `1.0.0-alpha.2`) shipped on 2026-07-08 — see their entries
below. The `Unreleased` section remains the cumulative delivered-capability record that seeds the v1.0.0 GA
release notes; the alpha pre-releases are published previews of it.

## [1.0.0-alpha.2] — 2026-07-08

The same engine as alpha.1, with release-quality fixes found by cutting alpha.1 for real:

### Added

- The NuGet package carries a package README and a current description, so the nuget.org page describes
  the framework properly.

### Fixed

- The release pipeline's publish job works on real tag pushes — its first-ever execution surfaced a missing
  repository context (and a masked failure in release creation) that the smoke-test runs could not reach.
- The Docker integration suite repaired against current dependency images: RabbitMQ 4.x forbids transient
  non-exclusive queue declarations (test queues are now durable, matching the documented author guidance);
  Elasticsearch 8.17 rejects request bodies on `_refresh`; and bad-image startup failures classify as
  `ImagePull` rather than `HealthGate` even under Aspire's generic health-gate wrapper message, using
  structural container-creation evidence — keeping the four-verdict taxonomy trustworthy.

## [1.0.0-alpha.1] — 2026-07-08

**The first public release.** Everything recorded under `Unreleased` below, published as a pre-release for
pilot validation ahead of v1.0 GA: the `vouchfx` dotnet global tool on NuGet.org (published via Trusted
Publishing — no long-lived keys), per-OS self-contained archives, MSI/deb/pkg installers and the VSCode
extension attached to the GitHub release, all cosign-signed with SLSA provenance attestations and CycloneDX
SBOMs.

```bash
dotnet tool install --global vouchfx --prerelease
```

The Provider SDK (`Platform.Sdk`) is not part of the alpha package set; it ships to NuGet.org with v1.0 GA.

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
- Two additional managed-dependency types: `dynamodb` (`amazon/dynamodb-local`, health-gated on its
  documented liveness signal — a `400` response on `/`, not `200`) and `minio` (`minio/minio`, health-gated on
  its documented `/minio/health/cluster` readiness path), both plain containers whose connection string is synthesised
  post-startup (`ServiceURL=…;AccessKey=…;SecretKey=…`), mirroring the `azureservicebus` pattern.

**Providers**

- Twenty-five Core providers across eleven step families: `http.rest`, `http.soap`; `db-assert.postgres`, `db-assert.mysql`,
  `db-assert.sqlserver`, `db-assert.mongodb`, `db-assert.dynamodb`; `mq-publish.kafka`, `mq-publish.rabbitmq`,
  `mq-publish.nats`, `mq-publish.azureservicebus`, `mq-publish.redis`; `mq-expect.kafka`, `mq-expect.rabbitmq`,
  `mq-expect.nats`, `mq-expect.azureservicebus`, `mq-expect.redis`; `cache-assert.redis`,
  `cache-assert.elasticsearch`; `mail-expect.smtp`; `webhook-listen.http`; `metrics-assert.prometheus`;
  `storage-assert.s3`; `trace-expect.otlp`; `script.csharp`. Kafka steps support Avro with Confluent Schema Registry.
  `mq-publish.redis`/`mq-expect.redis` use Redis Streams (`XADD`/`XRANGE`). `metrics-assert.prometheus` is the
  first member of the `metrics-assert` family: it scrapes a Prometheus text-exposition endpoint (typically the
  SUT's own `/metrics`) and asserts on one metric's numeric value, optionally scoped by a label subset, with
  `capture:` support for the matched value. `db-assert.dynamodb` asserts against a DynamoDB item via `GetItem`
  (a real `dynamodb-local` container per suite). `storage-assert.s3` is the first member of the new
  `storage-assert` family: it HEADs (and, only when a body digest/substring is declared, bounded-GETs) an
  object in an S3-compatible store (a real MinIO container per suite) and asserts on existence, size, content
  type, metadata, SHA-256 digest, or a body substring, with `capture:` support for `etag`/`versionId`/`size`.
  `http.soap` is the second `http`-family provider: a raw-envelope SOAP 1.1 client with fault detection
  (fault-expectation checked ahead of status), XPath assertions and captures over the response envelope, and
  the same hardened-XML-reader / SSRF-guarded-path discipline `http.rest` established. `trace-expect.otlp` is
  the first member of the new `trace-expect` family and the platform's flagship distributed assertion: an
  engine-hosted OTLP/HTTP JSON receiver (mirroring `webhook-listen.http`'s host-resource model) captures the
  spans a real, unmodified OpenTelemetry SDK exports for the transaction under test. A trace id is REQUIRED
  (accepting either a bare id or a full W3C `traceparent`, with automatic extraction) — ties the assertion to
  the specific transaction under test and is what makes the no-forged-match security posture an enforced
  guarantee rather than an authoring convention; service name, span name, and attributes are optional
  refinements layered on top of it, never a substitute for it — proving the causal chain a single-service
  assertion cannot. The receiver's ring buffer surfaces an `evicted` count on a Fail so a saturated-buffer
  flood is distinguishable from a genuinely absent export.
- The Provider SDK (`Platform.Sdk`): the frozen v1 contract (`IStepProvider`, `IStepBinder<T>`,
  `IStepValidator<T>`, `IStepCompiler<T>`, `IResourceContributor<T>`), optional extension interfaces
  (`IStepDiffRenderer`, `IHostResourceContributor`), a conformance test harness, worked example providers,
  and an SDK dry-run validation path.
- Provider-catalogue expansion: the DSL specification now names the planned community catalogue (§5.7, Table 5.1).
  `trace-expect` has graduated out of its reserved-family state now that `trace-expect.otlp` ships as its Core
  provider; `realtime-expect` remains the sole reserved family with its intent fixed ahead of its first provider.
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

- A release pipeline producing the CLI nupkg, per-RID self-contained archives, MSI/deb/pkg installers, a
  CycloneDX SBOM and the VSCode extension — each artefact keyless-cosign-signed and SLSA-provenance-attested,
  with NuGet.org publication via Trusted Publishing (OIDC; no long-lived keys).
- The release pipeline now packs and publishes the five-package Provider SDK closure (`Platform.Sdk`,
  `Platform.Sdk.Testing`, `Platform.Engine.Abstractions`, `Platform.Engine.Authoring`, `Platform.Engine.Compilation`)
  alongside the CLI. Symbol packages (snupkg) are carried through attestation and signing; every workflow action
  is SHA-pinned with dependabot keeping the pins current. Bare local packs self-identify as `1.0.0-0.local`.

### Changed

- Provider governance simplified from three tiers (Core / Verified / Community) to two (Core / Community). The former
  Verified tier endorsement is replaced by the **Vouched badge** — a maintainer-awarded registry metadata entry
  (`vouched: true` + `vouchedVersion` = exact reviewed version) awarded after conformance review; one hygiene-gated
  contribution flow on the hub; no engine code or contract change.
