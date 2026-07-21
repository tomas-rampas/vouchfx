# vouchfx public roadmap

This is the public, capability-level roadmap for vouchfx. It is updated at every minor release. Work is
sequenced by **risk, not visibility** — the hardest problems (the dynamic-compilation memory model and
health-gated container orchestration) were solved and CI-gated first, before any surface features.
Milestones, not dates, are the measure of progress; where dates appear they are quarters, deliberately.

For what changed when, see the [changelog](changelog.md). For how project decisions are made, see
[governance](governance.md).

## Delivered

The engine is feature-complete for v1.0. Everything below is on `main`, CI-gated, and exercised by the
four-technology reference scenario (REST, Kafka, PostgreSQL, webhook):

- **The compile-once memory model** — `.e2e.yaml` compiles through Roslyn exactly once into a collectible
  `AssemblyLoadContext`; a memory-leak regression test over the full provider closure is a permanent CI gate.
- **Health-gated orchestration** — headless .NET Aspire + Testcontainers topologies, started deterministically
  and torn down cleanly.
- **Twenty-five Core providers across eleven step families** — `http.rest`, `http.soap`; `db-assert` for PostgreSQL, MySQL,
  SQL Server, MongoDB and DynamoDB; `mq-publish`/`mq-expect` for Kafka, RabbitMQ, NATS, Azure Service Bus and
  Redis Streams; `cache-assert` for Redis and Elasticsearch; `mail-expect.smtp`; `webhook-listen.http`;
  `metrics-assert.prometheus`; `storage-assert.s3`; `trace-expect.otlp`; `script.csharp` (inline `code` or an
  external `file` reference).
- **Engine-owned asynchronous verification** — `verifyMode: RETRY` with bounded exponential backoff (Polly v8);
  authors never write `Thread.Sleep`.
- **Automatic state reset between sequential scenarios** — PostgreSQL, SQL Server, MySQL, MongoDB, Redis and
  Elasticsearch dependencies are automatically reset (data cleared, structure preserved) after each scenario
  completes; broker and DynamoDB/MinIO dependencies are not reset; a failed reset surfaces as an environment
  error naming the dependency.
- **Frozen v1 contracts** — the language schema, the provider SDK surface and the event-wire contract are
  frozen byte-for-byte, each enforced by a golden-file CI gate. Evolution within v1.x is additive only.
- **The Provider SDK** (`Vouchfx.Sdk`) with worked example providers, a conformance test harness, and the
  [community provider hub](https://github.com/tomas-rampas/vouchfx-providers).
- **Secrets as references** — `${secret:env/…}` and `${secret:vault/…}`, resolved at execution time, redacted
  at the source; the redaction path has passed a penetration test.
- **Reporting off one event stream** — terminal (WCAG-conscious), self-contained HTML, JUnit XML and a raw
  JSON Lines `--events` feed, all rendered from the same schema-versioned stream.
- **Live event streaming** — `vouchfx run --events-stream <path>` writes an incremental, tailable JSON Lines
  stream to a file as scenarios complete (scenario-level granularity), useful for real-time tailing, CI progress
  streaming, and downstream consumers.
- **Compile-level suite tooling** — `validate` and `list` subcommands perform Docker-free YAML validation
  and step-type discovery (machine-readable `--json` output), executed once at suite load time.
- **A headless CLI** with tag/owner/path/change-set selection, taxonomy-aware exit codes (only `Fail` breaks
  CI by default), parallel topology-per-scenario runs, and watch mode.
- **Editor tooling** — a VSCode extension with schema-driven validation and autocomplete, C# highlighting in
  `script.csharp` blocks, and Test Explorer integration.
- **CI integration** — a reusable GitHub Actions workflow and an `include`-able GitLab CI template.
- **A signed release pipeline** — keyless cosign signatures, SLSA provenance attestations, CycloneDX SBOMs,
  and NuGet.org publication via Trusted Publishing (no long-lived keys).

## Pre-releases are live; v1.0 GA targeted Q4 2026

The first public releases shipped on 2026-07-08 and the **`v1.0.0-alpha` series** (currently
`v1.0.0-alpha.10`) is published as pre-releases with signed artefacts (cosign, SLSA provenance,
CycloneDX SBOMs), and the `vouchfx` dotnet global tool is live on NuGet.org via Trusted Publishing
(packages for every alpha are on NuGet.org, and every alpha now has a published GitHub release
page):

```bash
dotnet tool install --global vouchfx --prerelease
```

The alphas exist for real-world validation. What remains for GA is validation and packaging, not construction:

- Validation of the end-to-end experience in real use — gathered in the open through the published
  [sample applications](https://samples.vouchfx.io/), the
  [migration guide](https://samples.vouchfx.io/docs/migrating.html) (worked Postman,
  xUnit and SpecFlow ports), and the [community provider hub](https://providers.vouchfx.io/),
  rather than a formal pilot cohort.
- The Provider SDK (`Vouchfx.Sdk`, its `Vouchfx.Sdk.Testing` harness and their engine dependency closure) is published to NuGet.org through the release pipeline; the GA task narrows to stabilising the SDK at 1.0.0 final.
- Some release artefacts gain further signatures (Windows Authenticode, macOS notarisation, GPG) only once
  the respective certificates are provisioned — cosign signatures and SLSA provenance are present on every
  artefact from day one, so verification is never blocked on those extras.

## The v1.x series — additive only

The three frozen contracts are a trust commitment for the whole v1.x series: nothing that works today breaks
within v1.x. Within that constraint, the near-term direction is:

- **Provider breadth through the community pathway** — the Provider SDK is the mechanism by which new
  technologies arrive (gRPC, Oracle, SQS and the long tail) as Community providers — with the Vouched badge
  marking maintainer-reviewed ones (per reviewed version) — without engine changes. This is deliberately
  community-first; the [provider hub](https://github.com/tomas-rampas/vouchfx-providers) is the front door.
  The launch catalogue (§5.7 of the DSL specification) now names the planned community catalogue and reserves one
  additional step family (realtime-expect) for future growth — `trace-expect` graduated out of that reserved
  state with `trace-expect.otlp` shipping as its Core provider, and `http.soap` joined `http.rest` as the
  `http` family's second Core provider. The first Community-tier provider (`rpc.json-rpc`, the tier's hub-hosted
  reference implementation) and a comprehensive [implementation guide](https://providers.vouchfx.io/docs/implementing-a-provider.html)
  are live on the hub.
- **Provider distribution and consumption** — the first Community provider package
  ([`Vouchfx.Community.JsonRpc`](https://www.nuget.org/packages/Vouchfx.Community.JsonRpc)) is published on
  NuGet.org and consumable today from a small custom runner built on the public engine API (the
  [`ledger-jsonrpc` sample](https://github.com/tomas-rampas/vouchfx-samples/tree/main/samples/ledger-jsonrpc)
  is the worked example); using an unpublished provider still means building from source with the provider
  referenced. The programme closing the remaining gap has additive steps.
  **Community source submissions on the hub are already open**: community-tier providers may be
  contributed as source into the [hub repository](https://github.com/tomas-rampas/vouchfx-providers)'s
  `community/` directory (each building and testing in isolation, author-owned, explicitly unendorsed —
  hosting is not endorsement) so authors need no NuGet account to participate; see the hub's contributing
  guide. Still ahead: a **provider directory loader** — the CLI learns to load provider assemblies from a
  directory (a `--providers` flag, an environment variable, and a default probe location), guarded by the
  same reserved-namespace and version-conflict checks the engine already enforces — and then **one-command
  acquisition**: `vouchfx providers install <id>` resolves a step kind through the community registry,
  downloads the package, and places it in the providers directory, with a dependency-only *Vouched
  metapackage* installing the whole badge-holding set once Vouched providers exist. The frozen v1 SDK contract
  is what makes externally-built provider binaries loadable across the whole v1.x series.
- **Additional secret sources** — the `${secret:…}` syntax is forward-compatible with cloud secret managers
  (Azure Key Vault, AWS Secrets Manager); adding them is configuration, not redesign.
- **Live GitLab validation** — the GitLab CI template is static-validated and behaviourally cross-checked
  against the GitHub workflow; a live-instance run is the outstanding confirmation.
- **Editor depth** — full in-block C# IntelliSense for `script.csharp` is a documented fast-follow.

## v2 candidates

Two items are explicitly parked for v2, and the reasons are part of the trust story:

- **Per-file telemetry opt-out** (`metadata.telemetry: false`) — deliberately *not* added in v1.x because it
  would mutate the frozen v1 schema. The v1 suppression surface (global consent, `--no-telemetry`,
  `VOUCHFX_NO_TELEMETRY`) already covers the privacy requirement; telemetry remains opt-in and off by default.
- **Richer control flow** — the DSL models a single linear sequence by design; conditional and parallel steps
  are a deliberate post-v1 language decision, taken slowly because language mistakes are forever.

## Deliberately not on the roadmap

Named here so nobody waits for them — build them via the Provider SDK or externally:

- **Automated migration tooling** from Postman, k6, xUnit or SpecFlow. Worked porting examples are the
  supported path; an automated converter is out of scope.
- **Mid-suite checkpoint-and-resume** — a crashed suite re-runs from the start.
- **Single-file executables** — the engine discovers provider assemblies via `Assembly.Location`, which
  single-file publishing breaks; multi-file self-contained archives are the supported form.
- **UI/browser testing and unit testing** — vouchfx tests the seams *between* systems; it is not a
  Playwright or xUnit replacement and will not become one.

## Open source, permanently

vouchfx publishes its feature boundary explicitly, in advance, so nobody discovers it later:

| Surface | Tier |
|---|---|
| The engine, the YAML DSL, all twenty-five Core providers, the Provider SDK, the VSCode extension, the terminal renderer, the HTML report, the JUnit XML output, the structured event stream, the CLI runner, the secret-reference resolution mechanism, the reproducibility envelope. | **Apache-2.0, free permanently.** |
| A hosted cross-run dashboard, managed Vault integration, cloud execution fabric (remote provisioning), agentic test planning/generation/healing, performance-testing tooling, run-history retention beyond the local cache, organisational analytics. | Commercial (future), layered *above* the open-source engine — never carved out of it. |
| SSO/OIDC federation, on-premises Helm deployment, audit logging, SIEM export, data-residency commitments, dedicated support SLAs. | Commercial enterprise (future). |

Nothing in the first row will ever move down the table. The open-source engine is the product, not a demo.
