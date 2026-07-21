# Changelog

All notable changes to vouchfx are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The v1 language schema, provider SDK surface and
event-wire contract are frozen for the whole v1.x series and enforced by golden-file CI gates; within v1.x,
evolution is additive only.

The first public pre-releases shipped beginning 2026-07-08: see the version entries below. The `Unreleased`
section remains the cumulative delivered-capability record that seeds the v1.0.0 GA release notes; the alpha
pre-releases are published previews of it. Note: GitHub Releases for v1.0.0-alpha.3 and v1.0.0-alpha.4 were
left in draft status at the time of publication (packages shipped to NuGet.org regardless) and were promoted
to published pre-releases on 2026-07-14.

## [Unreleased]

### Added

- **`vouchfx validate` subcommand** (#260) — compile-level validation without Docker: JSON-Schema validation → parse/AST → provider pipeline (bind/validate/emit) → full Roslyn compile. Discovers `.e2e.yaml` files from a file or directory path (recursive). Exit codes: 0 all valid, 2 usage error, 4 one or more invalid. `--json` flag produces a versioned machine document (schemaVersion, engineVersion, per-scenario diagnostics by stage).
- **`vouchfx list` subcommand** (#260) — list the sealed Core step-type catalogue (twenty-five dotted `family.provider` types). Exit codes: 0 success. `--json` flag produces a versioned machine document (schemaVersion, engineVersion, sorted stepTypes array).

### Changed

- **Documentation site rebuilt on Material for MkDocs** — the GitHub Pages site migrated from a custom static builder to Material for MkDocs with identical visual design, all legacy `.html` URLs redirecting to their new homes, and a new blog platform seeded with launch and alpha.9 posts. Publication boundary (confidential content detection, snippet allowlist, unresolved-fact detection) is now enforced by a hard CI gate (`scripts/check_site.py`) that runs before every Pages deployment. Development workflow unchanged: local `mkdocs build --strict` and `mkdocs serve`, fact tokens `{{fact:...}}` still work identically (now applied by MkDocs hooks instead of the old build script), offline authoring supported with `VOUCHFX_SITE_FACTS=offline`. The legacy `scripts/build_site.py` remains in-tree as the DOCS-list source of truth for the redirect table but no longer runs in the engine's CI; satellite repositories' wrappers and SHA pins are unaffected.
- **Onboarding and local-development documentation now packaged-CLI-first** — the getting-started guide leads with the published NuGet global tool (`dotnet tool install --global vouchfx --prerelease`), building from source repositioned as a contributor's path; stale pre-feature claims about secret values in `--events` corrected (verbatim occurrences of resolved secrets are redacted before terminal output, `--events` stream, and reports; transformed values remain the author's responsibility); troubleshooting guidance realigned accordingly; CI reference surfaces (the reusable GitHub Actions workflow and GitLab template documentation) now frame the build-from-source install as the deliberate pinned-ref design rather than claiming the tool remains unpackaged.
- **Project sites migrated to custom domains** — the engine site and its three satellite sites now serve from `vouchfx.io`, `samples.vouchfx.io`, `providers.vouchfx.io` and `telemetry.vouchfx.io` respectively, each carrying canonical URLs, a `robots.txt`, and a `sitemap.xml`; the engine site additionally serves an `llms.txt` index for AI/search-engine crawlers and JSON-LD structured data on its landing page. The publication gate (`scripts/check_site.py`) now blocks any deploy whose built output still references the retired GitHub Pages default domain, closing off the split link equity and crawl confusion of publishing under two hosts at once.

### Fixed

- **Schema validation error collection filters out discriminator-branch mismatches** (#259) — when an `.e2e.yaml` scenario contains an invalid step, the composed 25-provider JSON Schema validation previously reported every non-matching provider's `if`/`then` discriminator branch as a separate spurious error — up to 24 "Expected \"<other-type>\"" entries per invalid step, obscuring the genuine error. Error collection (now shared between the composed-schema and root-schema validators) filters these discriminator-branch mismatches out; an invalid step reports only its genuine errors (e.g. exactly one missing-required-properties error instead of 25 entries). The composed schema is byte-unchanged. User-visible effect: readable validation output at full provider scale, in the CLI run path and everywhere validation errors surface.

## [1.0.0-alpha.9] — 2026-07-18

A single-fix correctness release: the step `timeout` field now does what the language reference has always
said it does, for every verify mode. No language-schema shape, SDK or event-wire contract changes.

### Fixed

- **Step `timeout` is now enforced for IMMEDIATE steps** (#232) — the DSL has always documented `timeout` as
  an upper bound on the step, but the engine wired it only as the RETRY polling window. Every step's compiled
  body now runs inside a per-step cancellation scope: providers observe the step's token cooperatively (their
  client calls are cut when the budget elapses), and a body that ignores the token but completes past the
  budget has its outcome superseded. Either way the step resolves as **Inconclusive** (`step-timeout`), never
  Fail, mirroring the RETRY window semantics (§12.1). A declared timeout becomes the step's governing bound —
  it replaces the provider's built-in transport timeout (the previous hard-coded 30-second HTTP / 5-second AWS
  conventions), so `timeout: 90s` now genuinely means ninety seconds; with no timeout declared, behaviour is
  unchanged (provider transport conventions remain the de facto bound). RETRY semantics are preserved: the
  window bounds the poll, per-attempt transport conventions still bound each attempt, and an in-flight attempt
  is now also cut at the window's edge where the client supports cancellation. Language schema, SDK and
  event-wire contracts are untouched.

## [1.0.0-alpha.8] — 2026-07-18

A customer-journey hardening release: every advertised example now passes when actually run, the packaged
tool accepts a single `.e2e.yaml` file as the discovery root, and automatic state reset covers five more
stores. No language, SDK or event-wire contract changes.

### Added

- **Examples run gate in CI** — a new `vouchfx examples` workflow discovers every flat `examples/*.e2e.yaml`
  suite dynamically and CLI-runs each on its own runner with strict gating (`--fail-on-env-error
  --fail-on-inconclusive`), so example run-rot is caught on every push to `main` that touches the examples or
  the engine. The compile-only examples test proves the YAML compiles; this gate proves the suites actually
  pass — and exercises the single-file discovery-root form across every provider family as a side effect.
- **Automatic state reset between sequential scenarios** — SQL Server, MySQL, MongoDB, Redis and Elasticsearch
  dependencies now join PostgreSQL with automatic state reset between sequential scenarios sharing one
  topology. Data is cleared whilst structure (tables, indexes, mappings) is preserved. A failed reset surfaces
  as an environment error naming the dependency — never as a test failure. Brokers and DynamoDB/MinIO are not
  reset; add explicit cleanup steps for those. Language, SDK and event-wire contracts remain frozen.

### Changed

- GitHub Actions dependency upgrades via Dependabot across the CI workflows (setup-dotnet 6, setup-node 7,
  github-script 9, codeql-action 4.37.1), with the codeql-action init/analyze pair landed together to avoid
  the split-bump version-mismatch failure. The satellite repositories (providers, samples, telemetry backend)
  now carry the same weekly `github-actions` Dependabot configuration as the engine, so workflow SHA pins no
  longer rot silently anywhere in the fleet.

### Fixed

- **All fifteen per-provider examples now pass when run** — a full customer-journey audit found eight of the
  fifteen flat `examples/*.e2e.yaml` suites failing at run time despite the compile gate staying green: three
  declared placeholder SUT images that can never start (`orders-api:latest`,
  `ghcr.io/example/orders-api:latest`), and five depended on the `traefik/whoami` placeholder behaving like a
  real order service (expecting `201`/`202` responses, published events, sent email or a pre-declared queue).
  Each now follows the honest-simulate pattern the passing examples established: the placeholder SUT is
  exercised with `expect: status: 200`, and a clearly-marked `script.csharp` step simulates the SUT's own
  write over the staged connection string (Redis session shapes, Elasticsearch document, MongoDB document,
  MySQL row, RabbitMQ queue declaration, SMTP welcome email, Service Bus topic publish) so every assertion is
  genuinely observable. The `docs/recipes.md` "complete runnable example" links are now true as written.
- **`vouchfx run <file>.e2e.yaml` now works** — the advertised single-file form (including
  `vouchfx run <file> --watch`, which is single-file only) previously exited 2 with "Discovery root … does
  not exist" because discovery accepted only directories. A root naming a single `*.e2e.yaml` file now
  resolves to exactly that scenario; an existing file without the `.e2e.yaml` suffix is a precise usage
  error (exit 2) rather than a silent false green. The CI reference workflow now gates both root forms on
  every relevant push, and the reusable workflow's `scenario-path` input accepts a file (with a new
  optional `artifact-name` input so one workflow run can invoke it more than once).
- **The CLI no longer prints the release build machine's path at startup** — the packaged tool logged
  `Application host directory is: /home/runner/work/…` on every run (the `apphostprojectpath` assembly
  metadata baked at build time). Aspire's `Aspire.Hosting.DistributedApplication` lifecycle banners are now
  filtered below Warning, matching the existing health-check filter; the path was never used — scenario-relative
  paths resolve against the suite's own directory. DCP diagnostics and all warnings/errors still surface.

### Security

- The CI workflow token now defaults to least privilege (`permissions: contents: read` at workflow level);
  the coverage-badge job retains its job-scoped `contents: write`.

## [1.0.0-alpha.7] — 2026-07-13

A hardening and housekeeping release: no engine, DSL or contract changes.

### Security

- Transitive security dependencies are now pinned centrally via `CentralPackageTransitivePinningEnabled`
  (MessagePack 2.5.301, SharpCompress 1.0.0), so vulnerable transitive versions cannot resolve silently.
- The VS Code extension's `undici` development dependency was bumped to 7.28.0 (Dependabot).

### Changed

- The GitHub Pages site generator was extracted into the shared `vouchfx-site-tools` package, now consumed
  by all four ecosystem repositories instead of four diverging copies.
- `pages.yml` actions are SHA-pinned and the ecosystem notify dispatch `curl` was repaired ahead of
  cross-repo docs fan-out activation.
- Documentation truth-up after the pilot-programme discontinuation, with migration-guide cross-links, and a
  new knowledge-base article on DCP orchestrator portability backed by regression tests over the self-heal
  glue.

## [1.0.0-alpha.6] — 2026-07-12

The packaged-tool portability release: the NuGet-installed tool now works on machines other than the
release build runner.

### Fixed

- **The NuGet-installed `vouchfx` tool failed its first run on every machine other than the CI runner
  that packed it** (all earlier pre-releases). The engine now self-heals the Aspire DCP path at topology
  start — when the build-time baked path does not exist, it re-resolves the platform- and version-exact
  `aspire.hosting.orchestration.<rid>` package from the executing machine's NuGet cache, honours a
  user-set `ASPIRE_DCP_PATH`, and otherwise fails with an actionable environment error. A cross-machine
  smoke test now gates every release publish. See the knowledge-base article
  `docs/kb/dcp-orchestrator-portability.md` for the full write-up.

## [1.0.0-alpha.5] — 2026-07-11

The alpha series continues with minor feature additions and dependency updates.

### Added

- `script.csharp` steps now accept an optional `file` field: a path to an external `.csx` file, resolved
  relative to the `.e2e.yaml` file's directory, read once at compile time and spliced verbatim into the
  generated code. Provides an alternative to inline `code` for larger scripts. `code` and `file` are
  mutually exclusive.

### Changed

- Dependency version upgrades via Dependabot (cosign-installer, actions/checkout).

## [1.0.0-alpha.4] — 2026-07-10

The .NET identifier space is rebranded to `Vouchfx.*` ahead of v1.0 GA, replacing the generic `Platform.*`
naming used in earlier alpha releases.

### Changed

- **The .NET identifier space is rebranded pre-GA**: package IDs, assembly names, and namespaces move from
  `Platform.*` to `Vouchfx.*` across the engine and all Core providers. The `.e2e.yaml` language and JSON
  wire contracts remain unchanged (frozen at v1). Provider SDK packages published as `Vouchfx.Sdk`,
  `Vouchfx.Sdk.Testing`, `Vouchfx.Engine.Abstractions`, `Vouchfx.Engine.Authoring`, and
  `Vouchfx.Engine.Compilation`.

## [1.0.0-alpha.3] — 2026-07-09

Governance structure is simplified to two tiers (Core / Community) and the Provider SDK closure is published.

### Changed

- Provider governance simplified from three tiers (Core / Verified / Community) to two (Core / Community).
  The former Verified tier endorsement is replaced by the **Vouched badge** — a maintainer-awarded registry
  metadata entry awarded after conformance review on the community provider hub.

### Added

- The release pipeline now packs and publishes the five-package Provider SDK closure (published under the
  `Platform.*` IDs at the time — `Platform.Sdk`, `Platform.Sdk.Testing` and the `Platform.Engine.*` closure —
  renamed to `Vouchfx.*` in alpha.4) alongside the CLI, enabling provider authors to consume the published
  NuGet packages.

### Fixed

- The mailpit SMTP docker test repaired: correct CRLF line endings in the SMTP conversation, assertions on
  the server's response codes, and clearer CI diagnostics on failure.

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
*(Superseded: the SDK closure shipped early, at v1.0.0-alpha.3, and was renamed to `Vouchfx.*` in alpha.4 — see those entries.)*

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
- The Provider SDK (`Vouchfx.Sdk`): the frozen v1 contract (`IStepProvider`, `IStepBinder<T>`,
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
- `script.csharp` accepts a `file` field as an alternative to inline `code`: a path (resolved relative to the
  `.e2e.yaml` file's own directory) to an external `.csx` file, read once at compile time and spliced verbatim
  — identical trust boundary and lack of placeholder/secret substitution as `code`. `code` and `file` are
  mutually exclusive; a missing `file` is a clean Inconclusive validation failure, not a runtime crash.

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
- `v1-alpha`/`v1` floating convenience tags for consumers of the reusable workflow/template, maintained by
  `.github/workflows/move-floating-tag.yml` (force-moved to each published release's commit) — a zero-SHA-hunting
  quick start alongside the still-recommended SHA-pinned production tier. README documents the Dependabot
  `github-actions` (GitHub) / Renovate (GitLab) automation that keeps a SHA pin current without manual lookups.
- Environment-configured telemetry for CI: setting `VOUCHFX_TELEMETRY_INSTALL_ID` alongside the endpoint and
  token variables emits runs under one stable, repository-chosen install identifier, so ephemeral CI runners
  no longer mint a fresh install id per job (see `docs/telemetry.md`).

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
- The release pipeline now packs and publishes the five-package Provider SDK closure (`Vouchfx.Sdk`,
  `Vouchfx.Sdk.Testing`, `Vouchfx.Engine.Abstractions`, `Vouchfx.Engine.Authoring`, `Vouchfx.Engine.Compilation`)
  alongside the CLI. Symbol packages (snupkg) are carried through attestation and signing; every action in the release workflow
  is SHA-pinned with dependabot keeping the pins current. Bare local packs self-identify as `1.0.0-0.local`.

### Changed

- **The .NET identifier space is rebranded pre-GA**: package IDs, assembly names, and namespaces move from the generic
  `Platform.*` (engine and Core providers) to `Vouchfx.*`; the hub's community providers adopt `Vouchfx.Community.*` (hub repository change).
  The `.e2e.yaml` language and JSON wire contracts remain unchanged (frozen at v1); schema goldens and provider/event contracts
  are regenerated as pure renames; the `Platform.*` SDK packages (published at v1.0.0-alpha.3 under `Platform.Sdk`,
  `Platform.Sdk.Testing` and the `Platform.Engine.*` IDs) are to be unlisted and deprecated with migration pointers (NuGet alternate-package
  set to the `Vouchfx.*` successor) now that v1.0.0-alpha.4 has published the new IDs.
- Provider governance simplified from three tiers (Core / Verified / Community) to two (Core / Community). The former
  Verified tier endorsement is replaced by the **Vouched badge** — a maintainer-awarded registry metadata entry
  (`vouched: true` + `vouchedVersion` = exact reviewed version) awarded after conformance review; one hygiene-gated
  contribution flow on the hub; no engine code or contract change.

### Fixed

- **The NuGet-installed `vouchfx` tool now works on machines other than the release build runner.** Packages up
  to and including 1.0.0-alpha.5 located Aspire's DCP orchestrator only through the absolute path the
  `Aspire.AppHost.Sdk` baked into assembly metadata at pack time (`/home/runner/.nuget/packages/…linux-x64…` for
  NuGet.org packages), so every cross-machine install failed its first `vouchfx run` with an infrastructure
  error. The engine now self-heals at topology start: when the baked path does not exist, it re-resolves the
  platform- and version-exact `aspire.hosting.orchestration.<rid>` package from the executing machine's NuGet
  cache (`NUGET_PACKAGES`, else `~/.nuget/packages`) via the `DcpPublisher:CliPath` configuration override, and
  otherwise fails with an actionable environment error naming the missing package and remedy. A
  cross-machine smoke test in the release pipeline now gates publishing.
