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

- **`${env:NAME}` placeholders in service environment variables** — `environment.services.<name>.env` values may now reference engine-process environment variables via `${env:NAME}` syntax, resolved at topology-build time before container startup. An unset variable fails the suite before any container starts, naming the variable — never an empty-string substitution. This check is invisible to `vouchfx validate` (which never builds a topology) and, on `vouchfx run`, reports as an **Environment error** (§12.1) — exiting the process with code **0** by default, like any other Environment error, unless the caller passes `--fail-on-env-error`; without it, a mistyped `${env:DB_PASSWORD}` produces a green CI run that executed zero steps. Whether this case should exit non-zero unconditionally is left to REQ-018's own slice, which owns exit-code semantics generally. The resolved value is visible via standard `docker inspect` output (inherent to container environment storage, not a security defect); authors should treat service-visible environment variables as non-confidential and use `${secret:…}` references for credentials requiring redaction.
- **Raw TCP endpoints on services via `ports` declaration** — `environment.services.<name>.ports: [9093, ...]` declares raw TCP endpoints without an implicit HTTP health probe or HTTP endpoint, enabling non-HTTP systems under test (e.g. a customer-supplied Kafka broker, a proprietary binary service) to be declared. Each port is exposed via Aspire's generic endpoint (scheme `tcp`).
- **Explicit service health-check configuration, with per-shape defaults** — `environment.services.<name>.healthCheck:` overrides the health probe. Two forms: `{ type: tcp, port: N }` (a TCP connect probe on the specified port — no HTTP request — followed by a bounded zero-byte read to discriminate a live backend from DCP's host-published proxy accepting a connection before it has even reached one) or `{ type: http, path: "/..." }` (the explicit spelling of the default HTTP `/` health check). There is no way to disable health-checking outright — `type` is closed to `tcp`/`http`. Omitting `healthCheck` now depends on shape: an `image:`-only HTTP service, and the hybrid `ports` + `httpPort` shape, both keep the default HTTP probe on `/`; a `ports`-only service (no sibling `httpPort`) defaults to a `tcp` probe against the first declared port, rather than no health check at all. `type` values are case-sensitive.
- **`IProjectContext.DeclaredServices` (services-generalisation spec, REQ-010) — new member on the provider-authoring SDK's validation-stage context**, mapping each declared `environment.services.<name>` to the Aspire endpoint names its shape produces (mirroring the existing `DeclaredDependencies`). `IProjectContext` is provider-*consumed*, never provider-*implemented* (see its own remarks), so this is non-breaking for every Core/Community provider, which only ever reads it. It is **inside** the frozen v1 provider contract golden — `SdkContractFreezeTests` snapshots the whole `Vouchfx.Sdk` public surface, `interface Vouchfx.Sdk.IProjectContext` included — and that golden was regenerated deliberately for this change, not bypassed: the gate was engaged and its diff reviewed. Regenerating it is legitimate here because neither the new member nor its `DeclaredServiceInfo` value type has shipped in any of the fourteen published tags (measured: `DeclaredServices` appears in `src/Sdk/Vouchfx.Sdk/` in none of them), so no consumer can have compiled against either shape. It IS source-breaking for any TEST DOUBLE — in this repository or an external one — that implements `IProjectContext` directly: a hand-rolled `class X : IProjectContext` with no `DeclaredServices` member fails to compile against this SDK version. This repository's own test suites needed 29 such stand-ins updated (measured directly, not estimated); two further implementations outside the test suites — `TestProjectContext` (`Vouchfx.Sdk.Testing`) and the engine's own `RunProjectContext` (`Vouchfx.Engine.Runtime`) — needed the identical change, 31 in total. `TestProjectContext` ships inside the `Vouchfx.Sdk.Testing` package with the member already present, so any external harness built against that package is unaffected, but a hand-rolled double anywhere else is not. `DeclaredServices` underpins the health-check and target-resolution entries below. **Reshaped before this series' first release** (still pre-release, so a value-type change costs nothing external): the map's value is now a `DeclaredServiceInfo` record (`EndpointNames`, with room for a future init-only addition) rather than a bare endpoint-name list — an external target the engine does not itself start (a later capability) would otherwise have had nowhere to signal that beyond the SAME empty-list shape `[]` already means for a project-form service's auto-discovered endpoints.
- **Service-targeted Kafka publish/expect steps** — `mq-publish.kafka` and `mq-expect.kafka` now accept a `target` naming a declared service (previously kafka-dependency-only; a non-kafka dependency target is still rejected). At runtime, a service-targeted Kafka step currently fails closed as an **Environment error** (§12.1) — exiting the process with code **0** by default, like any other Environment error, unless the caller passes `--fail-on-env-error`; without it, a suite whose only defect is a service-targeted Kafka step's still-pending connection staging reports a green CI run rather than a failure. The provider-side connection staging for services arrives with a later slice of this series.
- **Every Core provider schema fragment now documents itself** — a root-level `description` was added to the 16 fragments that lacked one (`http.rest`, `http.soap`, all five `db-assert.*` providers, both `cache-assert.*` providers, `mail-expect.smtp`, `mq-expect.kafka`/`rabbitmq`, `mq-publish.kafka`, `script.csharp`, `trace-expect.otlp`, `webhook-listen.http`), plus `http.soap`'s previously-undescribed `expect.xpath[].path`/`.value` fields — every provider and field in the generated `docs/language-reference.md` now states what it does. Author-facing text only: internal engine class names stay in `$comment` (a JSON Schema 2020-12 keyword the reference generator never reads), the same treatment already applied elsewhere in the schema and now extended to `root-language-schema.json`'s `$defs/dependency` container and its `type` field, whose descriptions previously leaked `SchemaComposer.BuildIfThenClauses`/`EnvironmentMapper` class names into author-facing prose.
- **`"default"` declared on five fields where the engine already applies one** — `metrics-assert.prometheus.path` (`/metrics`), `db-assert.dynamodb.expect.exists` and `storage-assert.s3.expect.exists` (`true`), `http.soap.expect.status` (`200`), `http.soap.expect.fault` (`false`). Editor/IDE tooling reading the schema can now surface these without consulting provider source.
- **`EngineExport.BuildCatalogue` surfaces a `oneOf`/`anyOf`-nested requirement honestly, via two new typed fields — never as prose folded into `RequiredFields`.** `script.csharp`'s catalogue entry previously reported `RequiredFields: []` — a real lie: exactly one of `code`/`file` is required, but that constraint lives entirely inside a `oneOf` the field-extraction logic never read. `StepCatalogueEntry` gains two additive fields: `ExactlyOneOfGroups` (from a root `oneOf`, e.g. `script.csharp`'s `[["code","file"]]`, and — the same generic detection — `mq-publish.azureservicebus`'s `[["queue","topic"]]`) and `AtLeastOneOfGroups` (from a root `anyOf`, e.g. `mq-expect.azureservicebus`'s `[["expectPayloadContains","expectProperties"]]` — which also closes a related gap: a minimal document built from `RequiredFields` alone for that type previously under-specified a document the composed schema actually rejects). `SuiteScaffolder` (the `vouchfx scaffold` / MCP generator) consumes both fields directly, emitting each group's first member with its own scaffold value, in place of the two hardcoded per-provider special cases this replaces; a `[Theory]` scaffolds all 25 registered Core provider types and validates every result against the full composed schema, so a mismatch between the catalogue and what the scaffolder emits cannot recur unnoticed. A qualifying `oneOf`/`anyOf` branch must be EXACTLY `{"required": ["name"]}` — one field name, nothing else — enforced by the extraction code itself, not merely documented; a branch with any other content, or with more than one required name (`mq-expect.azureservicebus`'s own `queue` XOR (`topic` + `subscription`) shape has a two-name branch), degrades to no group at all rather than a fabricated or mis-cardinality one. The two consumers of this shape see different compatibility classes from the same change: the **JSON wire** (the `list --json`/`vouchfx-mcp` contract) is additive only — two new properties an existing client can ignore; a client with an older DTO simply never sees them (on the wire each is always present and always an array, `[]` at minimum). The **.NET API** is not — `StepCatalogueEntry` is a positional record, so both its constructor and its compiler-generated `Deconstruct` change arity, a binary-breaking change for any direct consumer (source-breaking too, for a positional-pattern match written against the old 7-arg shape). This is covered by this release's `feat!` marker on `Vouchfx.Engine.Compilation` (`IsPackable`, a published engine assembly — not the frozen `Vouchfx.Sdk` contract, which is untouched), not silently absorbed as a minor bump.
- **Schema-validation messages for two more forbidden-property shapes now name WHY, not just WHAT** — `storage-assert.s3`'s mutually-exclusive `expect.size`/`expect.minSize` (`Property 'minSize' cannot be combined with 'size' — exactly one of the two may be set`) and every `exists: false`-forbids-content-fields shape (`storage-assert.s3`'s six content fields, `db-assert.dynamodb`'s `expect.item`; e.g. `Property 'item' is not valid when 'exists' is false`), both derived from the live composed schema's own sibling `if` condition, never a hardcoded field list. Degrades to the previous generic `Property 'x' is not valid here` for any shape that doesn't exactly match one of these two patterns.
- **`security` block on services and kafka dependencies** — a `security` object is now recognised and validated on every `environment.services.<name>` and on a `kafka` `environment.dependencies.<name>`; on every other dependency kind it is rejected, because no security profile is wired for those kinds in this release (see the freeze-critical shape entry below for why that narrowing had to land before 1.0). The schema: `profile` (`tls`/`mtls`, case-sensitive, required — an open string pattern rather than a closed `enum`; an unrecognised name is rejected at validation time against the engine's security-profile registry, not by the schema itself, mirroring how a step's own `type` is a pattern in the schema and a registry lookup elsewhere), `endpoint` (port number or endpoint name, required whenever `security` is present — deliberately explicit so a suite cannot silently resolve to plaintext), optional `caCert` (valid to omit: trust material may already live in declared truststore/keystore or platform trust store; nothing is synthesised when absent), `clientCert`/`clientKey` (required together, `mtls` only; forbidden under `tls`), and `serverArtifacts` (list of `{source, target}` pairs; no inline `contents` — binary keystore material cannot survive YAML text). At `vouchfx validate` and pre-topology `vouchfx run` time: every **declared** path-valued field (`caCert`, `clientCert`, `clientKey`, each `serverArtifacts[].source`) is resolved relative to the suite's directory, containment-checked (checked **before** existence — a path pointing to a real file outside the directory still fails containment), and must exist on the host. An **undeclared** optional field is absent, not missing — nothing is checked or synthesised. A **declared but blank** path value (empty or whitespace-only) is rejected outright: the schema's `minLength: 1` catches a literal empty string, and `EnvironmentSecurityValidator` catches a whitespace-only value the schema's character count alone cannot, naming the offending field. (The field was originally named `mode`; renamed to `profile` before this series' first release — see the freeze-critical shape entry below.)
- **`security.profile` is a freeze-critical, registry-closed, per-kind-narrowed shape (not merely a field rename)** — three changes made together, before this series' first release, because each becomes impossible to make cleanly once the v1.0 language freezes: (1) **`mode` renamed to `profile`, total, not aliased** — a mechanism axis (which technology-specific wiring applies), not a strength axis, the same role `type: family.provider` plays for steps; the optional dotted form (`<vendor>.<name>`, e.g. `acme.custom`) reserves the SHAPE for a future out-of-tree profile, at no cost to the pattern — but, per (3) below, no such profile validates anywhere yet: it would still need to become a registered profile, and on a dependency other than kafka no profile validates at all. (2) **`$defs/security` closes with `unevaluatedProperties: false` replacing `additionalProperties: false`** (never both — a sibling `additionalProperties` in the same schema object silently voids `unevaluatedProperties`), so a composed profile fragment's own field can validate the way a provider fragment's own step field already can; a zero-behaviour change for every field this feature declares today. (3) **A `security` block is legal only where this release actually wires a client connection: on any declared service, and on a `kafka` dependency. Every other dependency kind rejects the block outright** — not the profile *value*, the block. There is no profile an author can substitute: `tls` and `mtls` are refused identically on a `postgres`, `redis`, `mongodb`, … dependency, and so is a future out-of-tree `<vendor>.<name>` profile. The message says so directly (`Dependency 'cache' (type 'redis') declares 'security', but no security profile is wired for dependency kind 'redis' in this release — only a 'kafka' dependency, or a declared service, can carry a 'security' block today`), because the alternative an author needs is a different *target kind*, not a different value. Expressed as a single allow-list clause (`security: false` for every dependency kind except kafka), not an enumerated exclusion, so a future dependency kind carries no `security` block the instant it is added. **There is no substitute declaration that restores transport security for those twelve kinds in this release, and the message deliberately offers none.** Re-declaring the technology under `environment.services` does not work: every step family targeting those kinds resolves `target` against `environment.dependencies` alone (measured — 17 of the 25 Core providers reject a `target` that is not a declared dependency of their own kind, and those 17 cover all twelve excluded kinds), so the move trades a schema rejection for a step-validation rejection. The service form is a working path only for infrastructure already reached as a service — an HTTP system under test, whose steps resolve `target` exclusively against `environment.services`. Transport security for the remaining dependency kinds is a **1.1** capability (REQ-013).

  This is deliberately **narrower than the shape first drafted for this series**, which pinned `security.profile` to the single value `tls` on those twelve kinds and so accepted `profile: tls` on, say, a postgres dependency. Nothing in this release stages a TLS client connection for such a dependency: the engine-side confirmation probe would confirm the endpoint speaks TLS while the step's own provider-emitted client connected in plaintext — exactly the false assurance the registry invariant below exists to close, arriving through the schema instead. Because both gates run at **validation** time, they decide which suites validate at all, so widening them later is safe and tightening them later is not; 1.0 therefore rejects the whole block, and server-side TLS for the remaining dependency kinds (with the corresponding widening) is a 1.1 capability. A suite that declared a `security` block on a non-kafka dependency against an earlier pre-release of this series must drop that block: unless the technology is genuinely an HTTP system under test (which belongs under `environment.services` regardless), moving the declaration there does not preserve the behaviour — it only relocates the failure, from schema validation to the step's own target reconciliation.

  A new `SecurityProfileRegistry`, internal to the engine assembly and published in no package (mirroring the provider `StepKindRegistry`'s own reflective, frozen-at-startup discovery), checks that every declared `(profile, target-kind)` pair resolves to a registered wiring at validation time — closing a false-assurance path the transport-only confirmation probe cannot: the probe can confirm an endpoint speaks TLS while a provider's own client connects unsecured, and a schema narrowing alone has no way to notice that drift. Both built-in wirings recognise exactly the two target kinds above, so the registry and the schema agree by construction rather than by discipline. No SDK extension interface for third-party profiles ships — the registry and the `mtls`/`tls` wirings are built and exercised through it, but nothing in that seam is `public`, and it stays that way until a second profile exists to prove the shape.

  One limit of (2) worth knowing before relying on it: the composed-fragment seam is proven **at the schema layer only**. `YamlDocumentParser` reads six fixed keys into `SecuritySpec`, which carries no `Extra` bucket (unlike `DependencySpec`), so a field contributed by a composed profile fragment validates and is then dropped before any consumer can read it. Closing that is purely additive (an init-only property on an unfrozen Authoring record) and waits for the second profile that would define what the bucket should carry.

- **HTTPS and mutual-TLS client connections for the HTTP step family.** `http.rest`, `http.soap` and `metrics-assert.prometheus` now configure their transport from the `security` block declared on the target they name: they present the declared client certificate (`profile: mtls`) and validate the server against the declared `caCert`. A target that declares no `security` block is untouched — no callback is installed and the platform's own trust store applies, exactly as before. Certificate paths are read at **step-execution time**, never interpolated into the compiled script, so a secured suite still compiles once and its reproducibility envelope is unaffected. Certificates are loaded lazily (only for a target some step actually resolves), once per scenario, and shared by every step that resolves the same target.

  Five author-visible consequences of declaring `security` on a service, none of which apply to a service that declares none:

  - The endpoint named by `security.endpoint` is exposed with an **`https` scheme**, and `svc::<name>` resolves to it in preference to any sibling plaintext endpoint the same service declares. What that resolution *stages* follows the protocol the suite's own steps speak to the target: an `https://host:port` URL for a service the HTTP family addresses, and a bare `host:port` bootstrap authority for one the Kafka families address (see the separate entry below) — the endpoint annotation carries the `https` scheme either way.
  - The **implicit plaintext HTTP endpoint** an `image`-form service would otherwise receive is **suppressed**, exactly as declaring `ports` suppresses it, unless `httpPort` is also declared alongside. Note that an `httpPort` equal to `security.endpoint` is not a surviving plaintext endpoint — it *is* the secured one, replaced rather than added to, so such a service ends up with no plaintext endpoint at all.
  - A secured service's **default health check becomes a TCP probe** rather than an HTTP one. A container health check cannot present a client certificate, so an HTTP probe against an mTLS listener holds a working topology unhealthy forever; declare an explicit `healthCheck` against a separate unsecured port for a stronger probe.
  - A **`project`-form service cannot be secured** in this release, and now says so at `vouchfx validate` time rather than once containers are starting. Its endpoints come from its own launch profile, so the engine has none of its own to give an `https` scheme.
  - A declared `caCert` is a **pin, not additional trust**. The declared anchor is consulted on every path: a peer certificate that chains only to the machine's own trust store is rejected even though the platform would have accepted it, and the peer must also present the `serverAuth` extended key usage (or none at all, which means unconstrained). The hostname is still checked — a declared CA says which issuer to trust, never which host. **The hostname the engine checks is a loopback address, not the logical service name**: a step reaches its target at the host-side endpoint the orchestrator allocates (`https://localhost:<allocated port>`), so the system under test's own server certificate must name `localhost` — and, for safety, `127.0.0.1` — in its subject alternative name. A certificate issued for the declared service name fails with a hostname mismatch, which is never forgiven and reports as an **Environment error**. **Two-tier certificate authorities are supported in the server direction**: declare the offline root as `caCert` and the server sends its issuing intermediate as usual; peer-supplied intermediates are used to build the path and never become trust anchors themselves. The **client** direction has no equivalent — `clientCert` is presented as a single leaf with no chain alongside it, so an intermediate-issued client certificate authenticates only if the system under test can already obtain that intermediate itself (§3.2.6b).

- **Kafka client transport security, on all four emitted client configurations.** `mq-publish.kafka` and `mq-expect.kafka` now configure their producer and consumer from the `security` block declared on the target the step names — on the plain-payload path and the Avro/schema-registry path alike, publish and expect. `profile: tls` verifies the broker only; `profile: mtls` additionally presents the declared `clientCert`/`clientKey`. A declared `caCert` becomes the client's trust anchor; an omitted one leaves the client's own default trust resolution untouched, and the property is assigned only when a path was actually declared — the Kafka client configuration is a keyed property bag in which assigning an empty string *adds* `ssl.ca.location` with an empty value (a path the library then tries to open and fails on), whereas leaving it unassigned removes the key. A target that declares no `security` block emits byte-for-byte the plaintext client it always did. Certificate paths are read at **step-execution time** through the run's own security accessor, never interpolated into the compiled script, so a secured suite still compiles once and its reproducibility envelope is unaffected. Kafka needs provider-side code where the other broker and store kinds do not, because its client configuration derives no transport decision from the bootstrap string the way an `amqps://`, `tls://`, `,ssl=true` or `tls=true` connection string does — there is no connection-string channel to carry it, so the decision has to be made in the emitted client configuration. **The profile switch is exhaustive and fails closed**: `tls` and `mtls` are mapped explicitly and any other profile throws, naming the target and the profile. That is deliberate rather than redundant with the engine's own registry check, because the profile discriminator is an open string: a later wired profile reached through a *different* security protocol (SASL/SCRAM, Kerberos, an OAuth bearer) would, under a which-fields-happen-to-be-set test, silently inherit mutual-TLS semantics nobody chose for it and connect with the wrong protocol. A profile added to the registry alone therefore turns the suite red rather than acquiring these semantics by accident; the two sets are pinned equal by a test.

- **A fail-closed confirmation probe runs before the suite does, and reports a named level rather than a boolean.** Once the topology is health-gated, and before the seed — therefore before any step — the engine connects to every declared `security` endpoint itself, presenting the same material a step will, and refuses to run the suite if it cannot confirm the declaration. Deliberately *not* an Aspire health check: a container health check cannot present a client certificate, so it can only establish that something accepted a socket. Health-gate the container, then confirm the security. Each declared target produces one declared-versus-observed line in the run's output (under `--parallel` too), carrying one of two levels:

  - **`AuthenticatedRoundTrip`** — the engine completed an application-protocol round trip over the secured connection. Today that means a Kafka `ApiVersions` exchange, reached for any `kafka` dependency and for any target the suite's own `mq-publish.kafka`/`mq-expect.kafka` steps address — including one declared as a **service**, which is the shape a customer-supplied broker actually takes, so the strong level is inferred from the suite's own steps rather than from the declaration kind. Under `profile: mtls` it additionally means a second connection presenting **no** client certificate did *not* complete the same exchange, and that differential is what makes the claim "the broker **required** an identity" true rather than merely "the broker tolerated one". A successful round trip on its own could not carry that claim: a peer that never sends a certificate request refuses nothing, and Kafka's own `ssl.client.auth` defaults to `none`, so a listener that requests a certificate without requiring one would be indistinguishable from an enforcing one. Under `profile: tls` there is no client identity to accept and none is claimed — the confirmation's own detail line says which of the two it is. The level says nothing about **authorisation**: whether that identity may publish to or consume from a given topic is the broker's own per-request decision and still surfaces as an ordinary step-level environment error.
  - **`TransportConfirmed`** — the endpoint speaks TLS, its certificate satisfied the declared `caCert` (or the platform's own trust store where none is declared), and the declared client certificate was **presented**. It does **not** confirm that the peer *accepted* that certificate, and nothing in this release claims it does: the target's application protocol is not known from its declaration, and a completed TLS 1.3 handshake carries no such signal. Certificate acceptance is first established when a step actually runs.

  The two levels exist so that a run which confirmed only the transport cannot read identically to one which confirmed an authenticated round trip. Note also that the probe and the step share the *material* but not the *judge*: the probe's peer verdict is .NET `SslStream`'s, applied to the host-published address the topology staged, while a Kafka step's is its own client library's, applied to whatever the broker's `advertised.listeners` names. The risk direction is safe (the step is never *less* strict than the probe), but they are not the same judgement.

- **`security.serverArtifacts` now copies the declared files into the target's own container** at topology-build time — the server-side keystore or certificate a broker's own entrypoint expects to find — on a service and on a `kafka` dependency alike. Previously the list was validated and then ignored. The bytes are streamed in through the container runtime's own API rather than bind-mounted, and that choice is the substance of the feature: a bind mount depends on the host filesystem and the container daemon sharing a view of one path, which under a remote daemon or Docker-in-Docker they do not, and the mount then presents an **empty directory** inside the container rather than failing — so an entrypoint that only tests for the keystore's existence comes up healthy with no secured listener and no error anywhere. Streaming carries no host/daemon co-location assumption, which is what keeps a secured suite running unchanged local, in CI, or against a remote fabric. Three author-visible rules that the schema alone does not carry, all now rejected before any container starts: `target` must name a **file**, not a directory (a trailing `/` is refused, naming the offending path and showing the intended shape); two artefacts declared on the same owner may not claim the same in-container path; and only a host file path is accepted — there is deliberately no inline `contents:` form, because binary keystore material cannot survive as YAML text. Each `source` is resolved against the suite's own directory, containment-checked and existence-checked on exactly the same terms as `caCert`/`clientCert`/`clientKey`.

- **A security-confirmation failure now breaks CI with neither `--fail-on-env-error` nor `--fail-on-inconclusive`** — the single deliberate exception to "only `Fail` breaks CI by default" (§12.1), and the entry here most likely to change a pipeline's outcome: a pipeline that passes neither gating flag can now go red where it previously could not. Either the post-health-gate **confirmation probe** fails — the declared endpoint refuses the connection, does not speak TLS, presents a certificate that does not chain to the declared `caCert`, or refuses the declared client certificate — in which case the run aborts before any step executes and exits **3**; or a pre-topology **security preflight** rejects the declaration — a certificate or artefact path that escapes the suite directory or does not exist, an artefact `target` that is not an absolute in-container file path, or a `profile` with no wiring for the target's kind — in which case no container starts at all and the run exits **4**; or the **schema** rejects it first, which covers the per-kind narrowing of `profile` (a `profile` the target's kind has no wiring for is refused by the root schema before the preflight ever sees it) and, more broadly, *any* schema error located at or inside a declared `security:` block — a mistyped field, a wrong scalar type, a list where a string belongs — likewise starting no container and exiting **4**; or a secured multi-scenario suite is refused over its **directory layout** (the scenarios resolve their declared security paths against different directories — see the separate note below), which likewise starts no container and exits **4**. Each keeps the exit code its own verdict names: no new code is introduced, and what `EnvironmentError` *means* is unchanged, so a pipeline keying on the taxonomy reads the same outcome it always did. **Every other cause of an environment error is untouched** and still exits 0 by default — an unhealthy container, an image that cannot be pulled, a seed failure unrelated to security, an unset `${env:NAME}`. The discriminator is the classified error kind on the exception, never the message text and never the verdict; the signal is read off the runner's own result rather than re-derived, because the whole point of the carve-out is that the verdict is unchanged and so cannot distinguish the case, and the carve-out's mechanism applies identically under `--parallel` (each scenario's own probe and preflight raise it there exactly as they do here; only the directory-layout arm has no counterpart, because under `--parallel` each scenario compiles, resolves its declared client material and seeds its server artefacts against one and the same directory — its own — so the two-roots-for-one-declared-path ambiguity that guard refuses cannot form). The change is therefore scoped to suites that declare a `security` block at all — measured: `security` appears in the language schema of **none** of the fourteen published tags, so no already-published suite can be affected.

  One related behavioural change, not limited to security: when **every** discovered scenario carries an early (pre-topology) verdict, the shared-topology `run` path now returns without building the topology at all, where it previously started, health-gated and then tore down containers first. That is what turns a missing `clientCert` file into a prompt exit 4 naming the file, instead of a two-minute wait ending in a health-gate timeout reported as exit 3 with the preflight message buried above it. A suite mixing one valid scenario with one that failed preflight is unaffected by construction — the condition is that *every* scenario has an early verdict — so the topology builds and the valid scenario runs exactly as before.

  One more suite-level refusal, for secured suites only: a multi-scenario suite that declares `security` and whose scenarios live in **different directories** is refused before the **shared** topology is built, with a non-zero exit. The scenarios of a suite must share a byte-identical `environment` block but not a folder, so a relative path such as `caCert: ./certs/ca.pem` in two scenarios one directory apart names two different files — the pre-run probe would then present one scenario's copy while another scenario's steps present their own, and the probe's verdict would no longer be evidence about those steps. The engine refuses rather than silently picking a directory; suites declaring no `security` are unaffected, and so is `--parallel`, where the guard deliberately does not apply — each scenario there builds its own topology and its own pre-run probe against its own directory, so there is no shared probe for a second scenario's material to diverge from.

- **A Kafka broker declared under `environment.services` is now reachable by its own steps, not merely accepted at validation.** `mq-publish.kafka`/`mq-expect.kafka` have accepted a `target` naming a declared **service** since this feature began — the shape a customer-supplied broker takes, since it runs its own entrypoint and configuration rather than being provisioned by the engine — but such a step could never actually run: the engine staged a service's endpoint at `svc::<name>` and both providers read `conn::<name>`, so a suite that confirmed green at the pre-run probe then failed on its first step with `kafka bootstrap not found`. Two halves of one rule fix it, and the rule is that **the engine stages the value in the form its own consumer uses, and a provider never rewrites it**. The engine now determines that form from the protocol the suite's own steps speak against the target — the same inference that already chooses the confirmation level, reused so the two cannot disagree: a target the HTTP family addresses is staged as an `https://host:port` URL exactly as before, and a target the Kafka families address is staged as the bare `host:port` bootstrap authority those clients expect, carrying no scheme for the provider to strip. And each provider emits the `Vars` key matching the kind its target actually is, decided at compile time from the same declared-service map its own `Validate` reconciled the target against — never guessed, and never resolved by trying one key and falling back to another. **A target addressed by both families is now rejected**, naming the target: one endpoint stages one value and the two families consume different shapes of it, so picking a winner would hand the loser a value it must transform to use. `vouchfx validate` reports it for a scenario that addresses one target with both families. A multi-scenario suite that splits the two families across its scenarios is rejected at the pre-topology stage of a **shared-topology** `vouchfx run` instead, with the same diagnostic: those scenarios share one topology, so the staged form is decided from the union of the steps across every scenario the run will actually execute, and no single-file check can see that (under `--parallel` the union never forms — each scenario owns its topology, so its own steps alone decide its staged form, and the per-scenario rejection is the whole of what applies) — `validate` deliberately treats each file independently and never decides which files form a suite. On both of those paths the rejection arrives before any container starts; under `--watch`, whose compile step builds the scenario but runs no validation stage, it arrives once the topology is already up — and those containers stay up, because the watch loop tears a topology down only when a save changes the `environment` block, which a steps-level conflict does not. Either way the rejection narrows nothing that ever worked — before this change the Kafka half of such a suite failed at run time every time — and the remedy is to declare the broker and the HTTP API as two entries under `environment.services`.

- **New public SDK type `Vouchfx.Sdk.KafkaSecurityHelper`** — the compile-time constant source of the helper class the two Kafka providers splice into their `CsxFragment.RequiredHelpers` to configure transport security on the emitted client, the Kafka counterpart of the existing helper that does the same job for the HTTP family's message handler. It is a separate type rather than a member on that one because the two are spliced by disjoint provider sets and name disjoint types: this one references the Kafka client library, which resolves only because the Kafka providers contribute that assembly, so splicing it into an HTTP-only suite would not compile. Like every helper source it is byte-identical across both providers, so the assembler deduplicates it to one copy per suite. It is **inside** the frozen v1 provider contract golden — `SdkContractFreezeTests` snapshots the whole `Vouchfx.Sdk` public surface — and that golden was regenerated deliberately for this addition, not bypassed: the gate was engaged and its three-line diff reviewed. Regenerating it is legitimate here because the type has shipped in none of the fourteen published tags (measured: `KafkaSecurityHelper.cs` is absent from `src/Sdk/Vouchfx.Sdk/` in every one), so no consumer can have compiled against it, and the change is purely additive — a new type alongside the existing surface, mutating no v1 interface and changing no existing member's shape. Nothing that has already shipped changes.

- **New public SDK type `Vouchfx.Engine.Authoring.Model.SecurityArtifactPath`**, and a new **`ICompileContext.DeclaredServices`** member — the two other additive surface changes this release makes, recorded here for the same reason the type above is: an addition nobody wrote down is indistinguishable, to a reader of the changelog, from one nobody made. `SecurityArtifactPath` is the single spelling of the containment rule for a declared `security` artefact path, hoisted so the pre-topology validator, the client-certificate accessor and the container-file copy resolve one rule rather than three copies of it; it is public because those three live in assemblies that do not reference one another, and it is not covered by the frozen v1 provider golden (that golden snapshots `Vouchfx.Sdk` only). `ICompileContext.DeclaredServices` mirrors the identically named member on `IProjectContext` so a provider's `Emit` can decide which `Vars` key its target needs; it carries a **default implementation** returning the empty map, so every existing implementation of that interface — this repository alone holds some eighty test stand-ins, and provider authors are free to hold more — compiles and behaves exactly as before.

### Changed

- **Scalar and map-valued fields across every Core provider widened to match what each provider's own `Bind` already accepts at runtime — a non-breaking widening, not a narrowing.** Every `additionalProperties: {"type":"string"}` map declared by a provider (`headers`, `parameters`, `properties`, `labels`, `expect.row`, `expect.document`, `expect.item`, `expect.metadata`, `avro.record`, `match.headers`, `match.json`, `expectProperties`, and their siblings — 24 fields across all 25 providers) now accepts `["string","integer","number","boolean"]`; the named comparison-VALUE scalars a provider reads back as raw text regardless of the YAML value's own declared type (`payload` on all five `mq-publish.*` providers, `key` on `mq-publish.kafka`/`match.key` on `mq-expect.kafka`, `expect.value` on `cache-assert.redis`, `expect.xpath[].value` on `http.soap` (bound via the same `GetScalar` raw-text read as its sibling `.path`), `payloadContains`/`expectPayloadContains`/`bodyContains`/`contentContains`/`subject-contains`/`body-contains`) widen the same way; and the `int`/`long`-parsed integer fields — `expect.status` on `http.rest` and `http.soap`, `expect.rowCount` on the three SQL `db-assert.*` providers, `expect.count` on `cache-assert.elasticsearch`/`mail-expect.smtp`/`db-assert.mongodb`, `expect.min-count` on `cache-assert.elasticsearch`, and `expect.length` on `cache-assert.redis` — ten fields in total (`expect.min-count` is one of the ten, not an eleventh field beyond them) — now accept `["integer","string"]`, with any declared numeric bound (`minimum`) kept, guarded by a new `pattern: "^[0-9]+$"` alongside the type union.

  The map/value-scalar widenings above genuinely already worked at runtime exactly as described: the affected `Bind` reads the field back via a raw `YamlScalarNode` cast, as opaque text, regardless of how the YAML value was written. The ten integer fields are not the same claim, and an earlier draft of this note overstated them identically — a quoted numeric string (`"200"`) already worked, but a non-numeric string (`"abc"`, or an unresolved `{placeholder}` token — none of these ten fields' `Bind` applies placeholder substitution before the numeric parse) did not "work": `int.TryParse`/`long.TryParse` failed silently and the assertion was never applied, a silent pass rather than an error, and the type widening alone — with no further guard — would have legalised writing exactly that at schema level too. The new `pattern` closes that gap: it still accepts every quoted numeric string the type union was widened for, and still rejects `"abc"`/`"2xx"`/an unresolved placeholder — identically to what the pre-widening, string-rejecting schema also rejected for that non-numeric text, so this is parity with the old schema's outcome on those inputs, not a regression, reached by a different mechanism (`[pattern]` instead of `[type]`).

  Fields that name a declared or addressed resource rather than carry a comparison value (`target`, `topic`, `queue`, `subject`, `stream`, `routingKey`, `table`, `bucket`, `collection`, `index`, a Redis/S3/DynamoDB `key`, `traceId`/`service`/`spanName`, `to`) are deliberately left string-only — an identifier an author would never plausibly write as a bare YAML number. The four fixtures previously pinned by `SchemaAcceptedCorpusTests` as "the engine accepts this, the schema rejects it today" now validate; that pinning theory (and its now-empty discovery method) is retired and the fixtures moved into the plain accepted corpus.
- **Breaking: eleven previously-open nested blocks across nine Core providers now reject unknown keys.** `unevaluatedProperties: false` on `$defs/step` (added in a prior release) does not recurse into nested objects, so a typo inside any of these blocks previously validated silently: `expect` on `http.rest` and `http.soap` (plus `http.soap`'s own `expect.xpath[]` array items), `match` on `mq-expect.kafka`/`rabbitmq`/`nats`/`redis`, `trace-expect.otlp`, and `webhook-listen.http`, and `avro` on `mq-publish.kafka` and `mq-expect.kafka`. Each now closes with a plain `additionalProperties: false` — a REPLACEMENT of the eight blocks that previously declared their own `additionalProperties: true` (the remaining three — `http.rest`'s `expect`, `http.soap`'s `expect`, and `http.soap`'s own `expect.xpath[]` array items — had no `additionalProperties` keyword at all before this change, a pure addition, not a replacement; eight replacements plus three additions is the eleven), never a `false` added alongside a retained `true` (the same same-object-cancellation trap the step-level closure's own regression guard documents applies one nesting level down). A document with an unrecognised key in any of these eleven positions that previously validated now fails at that exact location with an actionable `[additionalProperties]`-tagged message.
- **Breaking: constraints previously enforced only by a provider's own runtime `Validate` now also reject at schema/authoring time, across all 25 Core providers.** Every required string field gains `minLength: 1`, mirroring the empty-string rejection exactly (an empty `target: ""` etc. now fails schema validation instead of a provider's own runtime check) — a whitespace-only value (e.g. `target: "   "`) is a deliberate boundary, not an oversight: `minLength` counts characters, so it still passes schema, and is still caught at the provider's own `Validate` (`IsNullOrWhiteSpace`); the schema is looser than the provider here, never tighter — the same two-gate division of labour as everywhere else in this release, with exactly three deliberate exceptions: the genuinely NEW rejections named at the end of this entry, where the schema is tighter than a `Validate` that never covered those shapes at all. `http.rest`/`http.soap`/`metrics-assert.prometheus`'s `path` gains an SSRF-guard pattern (rooted-relative only: no absolute URL, no protocol-relative `//`, no backslash). `mq-publish.azureservicebus` now requires exactly one of `queue`/`topic`. `mq-expect.azureservicebus` now requires `queue` XOR (`topic` + `subscription` together — enforced by both a `oneOf` and a `dependentRequired` pair, the latter for a more specific message when only `topic` is set) plus at least one of `expectPayloadContains`/`expectProperties`. `cache-assert.redis` now requires `field` when `operation: hget`, and requires the `expect` member matching each of the seven operations (`value` for get/hget, `exists` for exists/ttl, `length` for hlen/llen/scard). `db-assert.postgres`/`sqlserver`/`mysql` now require `expect.rowCount` and/or `expect.row`; `db-assert.mongodb` now requires `expect.count` and/or `expect.document`. `db-assert.dynamodb`'s `expect.exists: false` now forbids `expect.item`. `storage-assert.s3`'s `expect.size` and `expect.minSize` are now mutually exclusive, and `expect.exists: false` now forbids all six content expectations (`size`/`minSize`/`sha256`/`contentContains`/`contentType`/`metadata`). `metrics-assert.prometheus.expect` now requires at least one of `value`/`min`/`max`. `mail-expect.smtp`'s `expect.match`, and — now that the block closes (see above) — `mq-expect.kafka`/`rabbitmq`/`nats`/`redis` and `webhook-listen.http`'s `match`, each now require at least one declared criterion. `mq-publish.kafka`'s `avro.record` now requires at least one field. `script.csharp`'s `code` is now capped at 64 KiB, mirroring the provider's own existing runtime bound. Nearly every constraint above was already enforced by the corresponding provider's `Validate` method; for those, a document that previously failed at compile time with the provider's own message now fails earlier, at schema validation, with a schema-native message instead (`[required]`/`[oneOf]`/`[dependentRequired]`/`[minProperties]`/`[maxLength]`/`[minLength]`/`[pattern]`/`[properties]`). Three of the constraints above are genuinely NEW rejections, not a re-timed lift — found only by re-checking each `Validate` method line-by-line against its schema counterpart, since an earlier draft of this note claimed no new rejections existed at all: `http.soap`'s `expect.xpath[].path` gains `minLength: 1` where `Validate` never inspects `expect.xpath` at all — the array, and every field inside each of its entries, was previously unvalidated at authoring time full stop, only surfacing as a runtime XPath-engine failure against a possibly-empty expression string. `db-assert.dynamodb` and `storage-assert.s3`'s `exists: false`-forbids-content checks are presence-based in the schema (the key itself may not appear, regardless of what it contains) but were count-based in `Validate` (`is { Count: > 0 }`) for one field on each provider — `db-assert.dynamodb`'s `expect.item` and `storage-assert.s3`'s `expect.metadata` alone (the other five S3 content fields were already presence-checked in `Validate`, `is not null`, so those five are exact parity, old check and new schema agreeing). Concretely: `expect: {exists: false, item: {}}` — an explicit, empty map — previously validated (`Count` is `0`, not `> 0`, so `Validate`'s condition was false) and now fails schema validation, since the key's mere presence now trips the `false` sub-schema regardless of its content being empty; `storage-assert.s3`'s `expect: {exists: false, metadata: {}}` narrows identically.

- **Breaking: closed target resolution for HTTP and metrics steps, narrowed to services only.** `http.rest`, `http.soap`, and `metrics-assert.prometheus` now reject a `target` naming anything other than a declared service at validation time (`vouchfx validate`, before any container starts). An unknown target is rejected naming the target and listing what is declared; a target naming a declared DEPENDENCY is rejected too — these three providers resolve `target` exclusively against declared services, so a dependency target would otherwise validate and then always fail at run time. Previously, an unknown or dependency target was accepted and failed only at runtime (an opaque "bootstrap not found" environment error). Host-resource-contributed names (e.g. a `webhook-listen.http` listener) count as valid declared targets — but a host resource whose name collides with a declared service, or with a dependency's own sidecar endpoint (a `mailpit` SMTP sidecar, a `kafka` schema-registry sidecar), is rejected outright, naming both surfaces; such a suite previously validated and ran, with the listener silently shadowing the real target, so a step could report a Pass having never contacted it. This rejects suites that previously validated: four pre-existing test fixtures needed an added `environment.services` block to keep passing, the branch's own evidence that this narrows rather than adds.
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
- **A dangling or empty `image:` on a dependency no longer throws at suite-build time.** Previously, `image:` with no value (or an explicit `image: ""`) threw `ArgumentException: Image reference must not be null, empty, or whitespace`; both now resolve identically to `image:` being absent altogether, matching the schema's existing description text.
- **A plain (unquoted) YAML-null `version:` or `image:` — `~`, `null`, `Null`, or `NULL` — no longer silently becomes a literal, unpullable container tag or repository.** Previously each of the four tokens reached Aspire verbatim as the container tag (e.g. `version: ~` pulled `...:~`; `version: NULL` pulled the four-character tag `...:NULL`), producing no error until Docker tried to pull the garbage reference — this was also true of a plain-null `image:` when paired with a sibling `version:` (the token reached Aspire as a literal, wrong repository name). A LONE plain-null `image:` with no sibling `version:` behaved differently, and flips the other way — failure to success: previously it correctly failed loudly at suite-build time (the pre-existing rejection of a tagless image with nothing to pin its version, so it would otherwise float on `:latest`), and now instead succeeds, correctly treated as absent. All four tokens now resolve identically to the key being absent, for both fields. This is new behaviour only for the four explicit tokens: a dangling or `""` `version:` was already correctly treated as absent before this change (unaffected). A quoted value (e.g. `version: "~"`) is unaffected either way and is still used literally — only YAML's unquoted null forms are resolved. Whitespace-only values (e.g. `image: "   "`) are also unaffected and continue to behave exactly as before this change: a loud rejection for `image:`, and the pre-existing literal-tag behaviour for `version:` (neither is part of this fix's contract).

### Removed

- **Breaking: the `publish` and `documents` `environment.seed` kinds** — both were wired-but-deferred stubs: the engine read the referenced fixture, content-hashed it, and recorded the intent through an injectable sink (`IBrokerWarmupSink` / `IDocumentSeedSink`), but never performed an actual broker publish or document-store write. Neither kind was used anywhere in this repository. A suite still writing `publish:` or `documents:` under a seed dependency now fails schema validation loudly, rather than silently doing nothing the way the removed seams did. Re-adding either kind, once genuinely implemented, is purely additive.
- **Breaking (CLR surface): `Vouchfx.Engine.Authoring`'s `PublishSeed`/`DocumentSeed` records are gone, and `DependencySeed`'s constructor arity changed to a single `Sql` parameter** — the direct consequence, at the library level, of the seed-kind removal above. Stated explicitly because it is a genuine break for any code consuming the parsed AST directly, not merely authoring `.e2e.yaml`, even though `Vouchfx.Engine.Authoring` ships with the package description "TESTING SURFACE, NOT the frozen v1 provider contract" and carries no stability contract before v1.0 GA — intentional pre-GA narrowing, not an oversight.

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
