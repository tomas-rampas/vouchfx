# Changelog

All notable changes to vouchfx are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). From v1.0.0 GA onwards, the v1 language schema,
provider SDK surface and event-wire contract are frozen for the whole v1.x series and enforced by golden-file
CI gates; within v1.x, evolution is additive only. Pre-GA (the `Unreleased` section and every alpha/rc entry
below it) still narrows and corrects the contract in place — exactly the entries the `Breaking`/`Changed`
headings below record.

The first public pre-releases shipped beginning 2026-07-08: see the version entries below. The `Unreleased`
section remains the cumulative delivered-capability record that seeds the v1.0.0 GA release notes; the alpha
pre-releases are published previews of it. Note: GitHub Releases for v1.0.0-alpha.3 and v1.0.0-alpha.4 were
left in draft status at the time of publication (packages shipped to NuGet.org regardless) and were promoted
to published pre-releases on 2026-07-14.

## [Unreleased]

### Added

- **Per-dependency image override** — `environment.dependencies[].image` field allows explicit specification of a container image for individual managed dependencies, bypassing Aspire's provisioned defaults. An `image:` carrying no tag or digest **must** be paired with a `version:` field; if both `image:` (with tag) and `version:` are set, the combination is rejected as ambiguous. A tagless `image:` without `version:` is rejected to prevent floating on `:latest`. Any `image:` value is used exactly as written, with the provider's registry default cleared, and `imageRegistry` applied on top only if the image carries no registry hostname of its own.
- **`capture` now has a real schema shape** — each entry is either a bare scalar JSONPath expression or a single-key mapping (`{ jsonpath: "$.id" }` / `{ xpath: "//id" }`). A non-scalar/non-mapping value, both keys present, neither present, an unknown key, and a non-scalar expression value were already rejected before this change — by `ParseCaptureEntry` (`YamlDocumentParser`), with a located parse error — and remain rejected identically today; on the CLI path the parser still reports these first, since it runs before schema validation is reached for a document that fails to parse. The genuinely new value here is authoring-time: the schema now expresses the same grammar, so a `.e2e.yaml`-aware editor can flag these shapes and offer completion for the two recognised keys (`jsonpath`/`xpath`) without invoking the compiler at all. A capture variable name beginning with an engine-reserved bookkeeping prefix (`svc::`, `conn::`, `__outcome::`, `__capture_status::`, `__attempts::`) is likewise now expressed in the schema — previously only `AstBuilder` caught this, at compile time — and the identical guard now also applies to top-level `variables:` keys, which `AstBuilder` always rejected but the schema never mirrored.
- **`metadata.schemaVersion` is now a real rejection hook** — constrained to the literal `"v1"` (the only language schema version that exists); the field stays optional, so omitting it remains valid, but a document declaring anything else (e.g. `schemaVersion: v2`) now fails schema validation instead of being silently accepted and ignored.

### Changed

- **Breaking: the DSL's vocabulary terms are now matched case-sensitively** — dependency `type`, `imagePullPolicy`, `verifyMode`, and `cache-assert.redis`'s `operation`. A suite that previously wrote `type: Postgres`, `imagePullPolicy: always`, or `verifyMode: retry` validated and ran; all now fail at suite-build time. Each term has exactly one canonical spelling, matching the JSON Schema enums that constrain it — previously the schema rejected a wrong-case value at authoring time while the engine accepted it at runtime, so the two gates disagreed about what was legal. Widening the enums to accept every case variant was considered and rejected: it makes editor completion noisy (`Postgres`/`postgres`/`POSTGRES`) and stops the schema being a clean statement of the accepted forms. Update any suite using a wrong-case spelling to the lower-case dependency kind (e.g. `postgres`, `sqlserver`, `mongodb`, `kafka`, …), the capitalised pull-policy value (`Always`, `Missing`, `Never`), the upper-case verify mode (`IMMEDIATE`, `RETRY`), or the lower-case redis operation (`get`, `hget`, `llen`, …). Every `[enum]` schema-validation error — not only these four terms — now names the offending value, lists the accepted values, and, when the value is a case-insensitive match for exactly one of them, states the correct spelling directly, e.g. `Value 'Postgres' is not one of the accepted values for 'type': postgres, sqlserver, … — write 'postgres'`. This closes a gap in how that promise first shipped: schema validation runs before `EnvironmentMapper.Map()` on every production path, so a hand-written "did you mean" message living only in the mapper was unreachable — an author hit the schema's generic `[enum] Value should match one of the values specified by the enum` first, always. The fix is now in the message an author actually sees.
- **`imageRegistry` now applies to both services and dependencies** — the environment-level `imageRegistry` override previously affected only `services`; it now prefixes every un-qualified image reference in both sections. Already-qualified references (those carrying a registry hostname) are never rewritten and are pulled from their specified host as-is. When a fully-qualified `image:` is specified on a dependency, the engine clears any built-in registry default the provider might carry, preventing unintended double-prefixing.
- **`imagePullPolicy` is now enforced at topology start** — previously the field was parsed but ignored at runtime. It now governs pull behaviour for all images (Always, Missing, Never) and can be set at the environment level (applies to all containers) or per-service to override it; there is no per-dependency form.
- **`environment.seed` is now closed to its one working kind** — `seed.<dependency>.sql` (an array of file paths) is the only recognised seed entry; a dependency mapping with an unrecognised key now fails schema validation. `sql` applies to postgres, sqlserver, and mysql dependencies alike.
- **`environment.services[].env`, `httpPort`, `environment.seed.<dependency>.sql` entries, and bare-scalar `capture` values widened to match runtime behaviour** — `env` values now also accept a bare (unquoted) numeric or boolean YAML scalar, not only a quoted string; `httpPort` now also accepts a quoted string (`"8080"`), not only a bare integer; a `sql` file-path entry and a bare-scalar `capture` value (e.g. `capture: { orderId: 42 }`) now likewise accept a bare numeric or boolean YAML scalar, not only a string. All four already worked at runtime — `YamlDocumentParser` reads every one of these back as raw scalar text, regardless of how it was written — and were previously rejected only by the stricter schema. Not breaking for any previously-valid suite — this is a widening, not a narrowing.
- **`environment.dependencies[].topics[].name` no longer accepts an explicit `null`** — `topics: [{ name: ~ }]` previously validated even though it is never what an author means: with `.e2e.yaml`'s pinned YAML parser, `name: ~` is read back as the literal, one-character text `~`, not as null, so `EnvironmentMapper.ParseAsbTopics` would declare a topic literally named `~` (a *different*, narrower defect than an *absent* `name`, which the parser genuinely does drop). Rejected at schema time instead of surfacing later as an unrelated-looking Service Bus environment error.

### Fixed

- **Spurious composite-branch schema-validation noise** — a fully valid step or service could still surface a misleading error, either because the document was invalid elsewhere in a way unrelated to it, or because JSON Schema's own `oneOf`/`anyOf` branch exploration leaked a non-matching branch's failure alongside a genuine one. Concretely, before this fix: a valid `script.csharp` step with a typo'd field reported only `[required] Required properties ["file"] are not present` (the unexercised `code`/`file` alternative) instead of naming the typo, and the advice was actively wrong (adding `file:` produced a different error); a valid `image:`-only service, anywhere in an otherwise-failing document, could report a spurious `[required] Required properties ["project"] are not present`; a valid `httpPort` or `timeout` value could report a spurious `[type]` mismatch from the branch it was never going to match, including advice to un-quote a `httpPort` this very release legalised quoting for. `httpPort`, `timeout`, and the mapping form of `capture` entries are now expressed as single merged schemas with no second branch left to leak from (identical accept/reject behaviour — `minimum`/`maximum`/`pattern`/`required`/`properties`/`additionalProperties` are all no-ops against a non-matching JSON type, so nothing was ever depending on the branch split). `SchemaErrorCollector` additionally drops a `oneOf`/`anyOf` branch's error whenever a SIBLING branch of the same composite application already satisfied it, for the two composites that cannot be merged this way (a service's "at least one of `image`/`project`", and a provider's own alternative-field `oneOf`, e.g. `script.csharp`'s `code`/`file` — a frozen provider fragment, untouched). A composite that genuinely fails — e.g. a service with NEITHER `image` nor `project` set — still reports every failing branch, unchanged; every registered Core provider type is now covered by a regression test pinning exactly one error for a single typo.
- **`[enum]` schema-validation errors are now actionable** — see the case-sensitivity entry above.
- **MySQL seed DDL/DML transactional caveat documented** (docs/02 §3.2.5) — belated documentation catch-up for the MySQL "implicit commit" behaviour already shipped; no engine behaviour changed.

### Removed

- **Breaking: the `publish` and `documents` `environment.seed` kinds** — both were wired-but-deferred stubs: the engine read the referenced fixture, content-hashed it, and recorded the intent through an injectable sink (`IBrokerWarmupSink` / `IDocumentSeedSink`), but never performed an actual broker publish or document-store write. Neither kind was used anywhere in this repository. A suite still writing `publish:` or `documents:` under a seed dependency now fails schema validation loudly, rather than silently doing nothing the way the removed seams did. Re-adding either kind, once genuinely implemented, is purely additive.

### Fixed

- **A dangling or empty `image:` on a dependency no longer throws at suite-build time.** Previously, `image:` with no value (or an explicit `image: ""`) threw `ArgumentException: Image reference must not be null, empty, or whitespace`; both now resolve identically to `image:` being absent altogether, matching the schema's existing description text.
- **A plain (unquoted) YAML-null `version:` or `image:` — `~`, `null`, `Null`, or `NULL` — no longer silently becomes a literal, unpullable container tag or repository.** Previously each of the four tokens reached Aspire verbatim as the container tag (e.g. `version: ~` pulled `...:~`; `version: NULL` pulled the four-character tag `...:NULL`), producing no error until Docker tried to pull the garbage reference — this was also true of a plain-null `image:` when paired with a sibling `version:` (the token reached Aspire as a literal, wrong repository name). A LONE plain-null `image:` with no sibling `version:` behaved differently, and flips the other way — failure to success: previously it correctly failed loudly at suite-build time (the pre-existing rejection of a tagless image with nothing to pin its version, so it would otherwise float on `:latest`), and now instead succeeds, correctly treated as absent. All four tokens now resolve identically to the key being absent, for both fields. This is new behaviour only for the four explicit tokens: a dangling or `""` `version:` was already correctly treated as absent before this change (unaffected). A quoted value (e.g. `version: "~"`) is unaffected either way and is still used literally — only YAML's unquoted null forms are resolved. Whitespace-only values (e.g. `image: "   "`) are also unaffected and continue to behave exactly as before this change: a loud rejection for `image:`, and the pre-existing literal-tag behaviour for `version:` (neither is part of this fix's contract).

## [1.0.0-rc.3] — 2026-07-30

Completion of the AI-facing tooling surface: the Generator (`vouchfx scaffold`) and Planner
(`vouchfx plan`), both with public library APIs so MCP and other in-process hosts need not shell out.
1.0.0-rc.2 was prepared but never published (no tag, no package); rc.3 ships its contents in addition to
the changes below. The language schema, provider SDK surface and event-wire contract are unchanged
(additive only).

### Added

- **`v1-rc` floating convenience tag** - `.github/workflows/move-floating-tag.yml` now routes `v1.0.0-rc.N`
  releases to a `v1-rc` tag, alongside the existing `v1-alpha` (alpha/beta) and `v1` (GA) tags. Each pre-GA
  line keeps its own tag deliberately: a consumer pinned to `v1-alpha` is never force-moved onto a release
  candidate, so switching lines stays an opt-in ref edit. `v1-alpha` is retired in place at `v1.0.0-alpha.10`
  never deleted, simply no longer moved.
- **Manual dispatch for the floating-tag workflow** - a `workflow_dispatch` trigger taking a release tag,
  for routing a tag introduced after its release was already published, and as the recovery path for a run
  GitHub superseded. It refuses any tag whose release is still a draft, preserving the same
  maintainer-confirmed-publish guarantee the `release: published` trigger provides.
- **`vouchfx scaffold` subcommand** - emits a machine-drafted, catalogue-grounded, schema-valid `.e2e.yaml` skeleton from a structured JSON intent (`--intent <file|->`, optional `--output <path>`). Steps, services, and dependencies are validated against the live Core registration and known dependency kinds; unknown types, duplicate ids, empty steps, and unknown dependency kinds fail closed (exit 3). Provenance comment header (no timestamps); credential-shaped fields use `${secret:}` references only. Docker-free.
- **Public library scaffolder** (`Vouchfx.Engine.Compilation.Scaffold.SuiteScaffolder`) - `Generate(StepKindRegistry, ScaffoldIntent, engineVersion?)` is the shared implementation for CLI and future MCP hosts so they cannot drift. `KnownDependencyKinds` mirrors the topology mapper's supported dependency set.
- **`vouchfx plan` subcommand** — a deterministic, read-only coverage-and-gap analysis that intersects the declared suite set, run history, and available step catalogue, emitting findings for coverage gaps (suite never run, step never exercised, dependency not asserted, vocabulary missing, service missing HTTP step), history-health signals (step stale, flaky, fragile, inconclusive-prone), and identity ambiguity. Every gap carries structured hints the `scaffold` tool consumes; the Planner never writes a suite file or calls a model. Human-readable summary on stdout by default; machine-readable JSON via `--json`; configurable thresholds for history-health classification; exit codes: 0 success (regardless of gaps), 2 usage error, 3 incomplete catalogue metadata, 5 gaps found (only with `--fail-on-gap`). Docker-free.
- **Public library Planner API** (`Vouchfx.Engine.Planning.PlanExport`) — `BuildPlan(PlanRequest, StepKindRegistry, engineVersion?)` and `SerializePlan(PlanReportDocument)` expose the same analysis the CLI uses, so MCP and other in-process hosts need not shell out. The report is a frozen v1 wire shape (`PlanReportDocument` with schema version 1, inventory, and ten finding kinds); evolution within v1 is additive only.

### Changed

- **README restructured as a landing page** (626 → 364 lines) — the status section is a short callout rather
  than a single ~400-word sentence, a complete runnable `.e2e.yaml` example is shown rather than only
  described, and providers are presented as a family table. The full GitHub Actions and GitLab CI reference
  moved to a new `docs/ci-integration.md` page (`recipes.md` and `getting-started.md` previously linked back
  into README anchors for it, and now point at the new page).
- **Getting-started documentation** - documents the Generator / scaffold workflow (structured intent, free-text host LLM boundary, validate/run path, provenance, secrets-as-refs, catalogue grounding).

## [1.0.0-rc.2] — 2026-07-28

*(Never published: no `v1.0.0-rc.2` tag or NuGet package exists; these changes shipped in rc.3.)*

Schema and catalogue export for AI and tooling consumers. The language schema, provider SDK surface, and event-wire contract are unchanged (additive catalogue fields only).

### Added

- **`vouchfx schema` subcommand** — emits the composed v1 JSON Schema (root language grammar merged with every registered provider fragment) to stdout by default, or to a file via `--output <path>`. Exit codes: 0 success, 2 usage error (e.g. missing parent directory for `--output`), 3 incomplete-metadata / composition failure. Docker-free.
- **Public library export API** (`Vouchfx.Engine.Compilation.Schema.EngineExport`) — `ComposeSchemaJson` and `BuildCatalogue` expose the same schema and shape-level catalogue the CLI uses, so MCP and other in-process hosts need not shell out. Incomplete provider metadata fails closed with `CatalogueExportException` naming the step type.
- **Rich step catalogue on `list --json`** (additive) — each step type entry now includes `requiredFields`, `optionalFields`, `captureSupported`, and `familyIntent` in addition to `type` / `family` / `provider`. Wire shape frozen by golden-file CI gates; evolution within v1 is additive only.
- **VS Code extension live schema source** — prefers `vouchfx list --json` (bar-B gate) and `vouchfx schema` from the configured CLI, with a version-checked bundled fallback.

### Changed

- **Getting-started documentation** — documents `vouchfx schema`, the enriched catalogue, fail-closed export behaviour, and the `EngineExport` library entry points for MCP / VS Code / third-party consumers.

## [1.0.0-rc.1] — 2026-07-24

A release-candidate consolidating developer-tooling, run-lifecycle, and validation improvements. The language
schema, provider SDK surface and event-wire contract are unchanged (all frozen-contract-safe: per-step
event-stream liveness and scenario-rooted file resolution are runtime enhancements with no wire-format impact;
validation improvements and exit-code corrections align the verdict taxonomy).

### Changed

- **Scenario file resolution roots per scenario** (#268) — relative `script.csharp file:` references now resolve against each scenario's own directory in both `run` and `validate`, rather than the first discovered scenario's directory. A scenario in a subdirectory now correctly finds helper scripts beside it. Sequential unfiltered `run` topology seeding remains rooted at the first scenario's directory; parallel runs seed from each scenario's own directory.
- **`--events-stream` now emits step and step-attempt events in real time** (#262) — previously, events were flushed at scenario completion (scenario-level granularity). Step and step-attempt events now appear in the stream immediately as they complete during a run, enabling live per-step progress tracking. For RETRY steps, each polling attempt is observable as it happens. In parallel runs, step lines from concurrently-running scenarios interleave by arrival order but remain disambiguated by `(runId, stepId)` pairs; the authoritative, declaration-ordered `--events` archive is unchanged.

### Fixed

- **Unknown step type now reported as validation error with line context** (#265) — `vouchfx validate` and the pre-compilation validation pass now report an unknown `type` field (e.g. `type: db-assert.oracle`) as a validation error with a line number and an authoring-friendly message ("unknown step type '…' — not a registered provider (expected `<family>.<provider>`, e.g. 'db-assert.postgres')."), rather than accepting it vacuously and surfacing the error later at provider binding without context. The composed JSON Schema is unchanged; validation is a post-schema cross-check against the registered provider keys.
- **Script body and document size limits** (#266) — the engine now enforces a 64 KiB maximum per `script.csharp` step body and 1 MiB maximum per `.e2e.yaml` document as resource-limit bounds before compilation. These are sanity limits an author might reasonably hit, not a defence against deliberate compiler crashes (stack overflow exceptions, parse-phase interpolation hangs) which remain uncatchable and not fully preventable in-process. Out-of-process isolation (as the vouchfx MCP server does) remains the correct answer for untrusted input. Terminal diagnostic output is now scrubbed of control characters and ANSI escape sequences at every known diagnostic output path, so a crafted error string or captured value cannot inject terminal escapes through those paths into a human-viewed report or terminal output (`--json` output was already safe). Fuller in-process→out-of-process isolation of the engine's compile step is planned as a future enhancement.
- **Unrecognised option or flag now exits 2 (UsageError) instead of 1** (#269) — any vouchfx subcommand now exits with code 2 when given an unrecognised or unknown option or flag, making it possible for CI to distinguish a CLI-misuse from a genuine test failure. Exit code 1 remains reserved for the Fail verdict (one or more test scenarios failed); exit codes 0, 3, and 4 (`--help`, `--version`, and conditional verdicts) are unchanged.
- **`run` where every discovered scenario fails to parse now exits 4 (Inconclusive) instead of 0** (#278) — when every scenario fails to parse (malformed YAML, unknown step types across the board — whether a single file or a directory), the run is now classified as Inconclusive and exits 4 unconditionally, independent of the `--fail-on-inconclusive` flag, matching the behaviour of `vouchfx validate` and the verdict taxonomy.

## [1.0.0-alpha.10] — 2026-07-21

A developer-tooling and run-lifecycle release: incremental JSON Lines event streaming to a tailable file,
Docker-free compile-level `validate` and `list` subcommands, opt-in stdin-driven graceful shutdown for
programmatic hosts, a wider Ctrl+C/SIGTERM teardown budget preventing container leaks, and cleaner schema-validation
errors at full provider scale. The language schema, provider SDK surface and event-wire contract are unchanged (all
frozen-contract-safe: new `--json` outputs and the new `--events-stream` file are additive contracts; the schema-noise
fix leaves the composed schema byte-unchanged).

### Added

- **`vouchfx run --events-stream <file>` flag** (#258) — writes the schema-versioned JSON Lines event stream incrementally to a tailable file, independent of the buffered `--events` archive. The engine holds the write handle and grants shared read access; a tailing reader must open the file with shared read/write access (on Windows, `FileShare.ReadWrite`; on Unix, the file is readable immediately). UTF-8 without BOM. Enables live tailing by downstream consumers such as the vouchfx MCP server and CI progress tracking. Events are flushed at scenario completion (scenario-level granularity); in parallel mode the stream reflects completion order, not declaration order. Best-effort on unwritable paths: prints a diagnostic and does not affect the run's verdict or exit code.
- **`vouchfx validate` subcommand** (#260) — compile-level validation without Docker: JSON-Schema validation → parse/AST → provider pipeline (bind/validate/emit) → full Roslyn compile. Discovers `.e2e.yaml` files from a file or directory path (recursive). Exit codes: 0 all valid, 2 usage error, 4 one or more invalid. `--json` flag produces a versioned machine document (schemaVersion, engineVersion, per-scenario diagnostics by stage).
- **`vouchfx list` subcommand** (#260) — list the sealed Core step-type catalogue (twenty-five dotted `family.provider` types). Exit codes: 0 success. `--json` flag produces a versioned machine document (schemaVersion, engineVersion, sorted stepTypes array).
- **`vouchfx run --shutdown-on-stdin-eof` flag** — an opt-in graceful-shutdown option for programmatic usage (for example, the vouchfx MCP server). When enabled, the engine monitors its standard input and gracefully initiates shutdown (as if Ctrl+C was pressed) when the input stream closes, allowing full container and topology teardown to complete before the process exits. If graceful shutdown does not complete within the teardown budget (approximately 30 seconds), the engine force-exits itself (exit code 4, Inconclusive, unconditionally), guaranteeing termination without the caller needing to send a separate kill signal. Default off; normal interactive and CI runs are unaffected. Requires the caller to hold stdin open; combining with stdin already closed (`< /dev/null`) causes immediate cancellation.

### Changed

- **Documentation site rebuilt on Material for MkDocs** — the GitHub Pages site migrated from a custom static builder to Material for MkDocs with identical visual design, all legacy `.html` URLs redirecting to their new homes, and a new blog platform seeded with launch and alpha.9 posts. Publication boundary (confidential content detection, snippet allowlist, unresolved-fact detection) is now enforced by a hard CI gate (`scripts/check_site.py`) that runs before every Pages deployment. Development workflow unchanged: local `mkdocs build --strict` and `mkdocs serve`, fact tokens `{{fact:...}}` still work identically (now applied by MkDocs hooks instead of the old build script), offline authoring supported with `VOUCHFX_SITE_FACTS=offline`. The legacy `scripts/build_site.py` remains in-tree as the DOCS-list source of truth for the redirect table but no longer runs in the engine's CI; satellite repositories' wrappers and SHA pins are unaffected.
- **Onboarding and local-development documentation now packaged-CLI-first** — the getting-started guide leads with the published NuGet global tool (`dotnet tool install --global vouchfx --prerelease`), building from source repositioned as a contributor's path; stale pre-feature claims about secret values in `--events` corrected (verbatim occurrences of resolved secrets are redacted before terminal output, `--events` stream, and reports; transformed values remain the author's responsibility); troubleshooting guidance realigned accordingly; CI reference surfaces (the reusable GitHub Actions workflow and GitLab template documentation) now frame the build-from-source install as the deliberate pinned-ref design rather than claiming the tool remains unpackaged.
- **Project sites migrated to custom domains** — the engine site and its three satellite sites now serve from `vouchfx.io`, `samples.vouchfx.io`, `providers.vouchfx.io` and `telemetry.vouchfx.io` respectively, each carrying canonical URLs, a `robots.txt`, and a `sitemap.xml`; the engine site additionally serves an `llms.txt` index for AI/search-engine crawlers and JSON-LD structured data on its landing page. The publication gate (`scripts/check_site.py`) now blocks any deploy whose built output still references the retired GitHub Pages default domain, closing off the split link equity and crawl confusion of publishing under two hosts at once.

### Fixed

- **Improved Ctrl+C and SIGTERM teardown budget** — the engine now allocates approximately 30 seconds for clean container and topology teardown after receiving a SIGINT (Ctrl+C) or SIGTERM signal, preventing orphaned containers and Aspire session networks when a run is interrupted. Previously the process could be force-killed mid-teardown, leaving infrastructure behind.
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

- **The MCP companion's documentation site is live** — `vouchfx-mcp` now serves from `vouchfx-mcp.vouchfx.io`, the fifth
  fleet site, covering installation and registration, the six-tool and two-resource reference, an overview and
  troubleshooting. The engine surfaces that described it as having no documentation site (README, `docs/ecosystem.md`)
  are corrected, and `docs/getting-started.md` and the landing footer now link it too; the drift sentinel crawls it
  alongside the other four, and the docs-deploy fan-out notifies it. The `Vouchfx.Mcp` dotnet tool itself remains
  unpublished on NuGet.org.
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
