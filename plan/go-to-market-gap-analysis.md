# Go-to-Market Gap Analysis

> **STATUS: ENGINEERING AHEAD — COMMERCIAL TRACK NOT STARTED.**
>
> Compiled 2026-07-04. This document is the consolidated gap register between the current state
> of the project (engineering-complete through Sprint 11 / M4, Sprint 12 partially delivered) and
> a defensible v1.0 launch with an honest go/no-go read. It merges a commercial/launch-track lens
> and a product/adoption lens over a repo-wide research pass, and it is the single schedule-of-record
> input for Sprint 12 tasks S12-D-01/02 and S12-E-01..06 (issues #115–#122).

---

## 1. Executive Summary

The engineering track is roughly two sprints ahead of the commercial track, and the commercial
track has never started: there are zero releases, tags or published packages (release workflow —
0 runs, both NuGet IDs unreserved, checked live 2026-07-04); the product name is formally
undecided while the v1 artefacts are already frozen under the working name; no vendor entity
exists, so no pilot agreement, DPA or deposit can be executed; no pilot outreach has ever been
performed against a 6–10 week cold-outreach lead time (plan/pilot-recruitment.md §1.2); the
telemetry backend that measures GA Gate 1 (sustained adoption) is built but undeployed; and both
the M3 and M4 milestone exit reviews remain unheld. The two longest poles — pilot recruitment and
entity/trademark formation — are independent of each other and of the release channel, so they
must start immediately and in parallel; on the arithmetic in §5.4, the earliest realistic
go/no-go read is **mid-October to mid-November 2026** even if outreach begins the week of 6 July.
Four adoption gaps (one Blocker, three Major) are being remediated in the current change-set
(§6); everything else in the register (§4) is open.

### 1.1 Top ten gaps

| Rank | ID | Gap | Severity | Owner | First action |
|---|---|---|---|---|---|
| 1 | GTM-35 | Pilot recruitment never started; 6–10-week lead time on an eight-team target | Blocker | PD | Send the first direct-outreach tranche this week (templates are ready-to-send) |
| 2 | GTM-29 | Naming decision unmade; v1 artefacts frozen under the working name | Blocker | PD | Run the knock-out search on "vouchfx" itself (§5.1) |
| 3 | GTM-30 | No vendor entity; DPA/MSA/pilot agreement/privacy policy uncommissioned | Blocker | PD + Legal | Book the legal consultation; record the firm date in plan/vendor-entity.md |
| 4 | GTM-31 | Trademark: no knock-out search, nothing filed, priority date unsecured | Blocker | Legal | File EUIPO classes 9+42 as soon as the entity exists |
| 5 | GTM-01 | Release channel never exercised: 0 tags, 0 releases, 0 pipeline runs | Blocker | RE | Run the mandated `workflow_dispatch` smoke test (needs no secrets) |
| 6 | GTM-02 | NuGet: IDs unreserved, `NUGET_API_KEY` unprovisioned, no Platform.Sdk channel | Blocker | RE | Reserve both IDs and provision the key after GTM-29 resolves |
| 7 | GTM-36 | Telemetry backend undeployed (Azure unprovisioned, deploy secrets placeholder) | Blocker | ENG + RE | Provision Azure, populate secrets, run deploy.yml, smoke `/healthz` |
| 8 | GTM-03 | Signing certificates unprovisioned (M4 Gate 1) | Blocker (formal) | RE | Start Authenticode + Apple Developer ID procurement now (1–2-week enrolment) |
| 9 | GTM-32 | M3 and M4 human exit gates all open; sign-off blocks empty | Blocker (formal) | PD + TL | Schedule one combined M3+M4 steering session |
| 10 | GTM-24 | Launch governance artefacts unpublished (S12-E-01, #115) | Blocker | PD + Legal | Extract §9.6–9.9 of docs/03 into standalone published artefacts |

---

## 2. Method and Scope

**Scope.** Everything between "engineering-complete through Sprint 11 / M4" and a defensible v1.0
launch plus go/no-go read. In scope: the engine repository, both satellite repositories
(vouchfx-providers, vouchfx-telemetry-backend), live GitHub state (issues, releases, workflow
runs), live NuGet.org state, the GitHub Pages surface, and the `plan/` artefact set. Out of
scope: post-v1 backlog items (plan/post-v1-backlog.md) except where they affect pilot perception
(GTM-21).

**Method.** Two independent analysis lenses — commercial/launch-track and product/adoption
(first-contact through first green suite) — were run over a shared research map; load-bearing
citations were spot-verified against the working tree (e.g. the hard-coded health path at
`src/Engine/Platform.Engine.Orchestration/EnvironmentMapper.cs:570`, the postgres-only seed
dispatch at `src/Engine/Platform.Engine.Orchestration/SeedApplier.cs:217`) and against live
external state (NuGet.org, GitHub API, both checked 2026-07-04). This document merges and
deduplicates the two lenses into a single register.

**Severity scale.**

| Severity | Meaning |
|---|---|
| **Blocker** | Launch or the go/no-go read is impossible, or a first-time team cannot succeed, until closed. "Blocker (formal)" marks items where the engineering is done but a milestone gate or contractual commitment blocks declaration. |
| **Major** | Most adopting teams bounce or need hand-holding; or a launch commitment is materially overstated until closed. |
| **Minor** | Friction, hygiene or credibility polish; survivable with documentation. |

**Status values.** `Open`, or `Addressed in this change-set` for the four in-flight remediations
described in §6.

**Owner types.** PD = product/delivery lead · TL = technical lead · RE = release engineer /
DevOps · ENG = engineering · DOCS = technical documentation · Legal = external counsel.

---

## 3. Current State: the Engineering/Commercial Asymmetry

The asymmetry is stark and one-sided.

**Engineering track — substantially complete.** All 18 of 18 M4 exit criteria are done
(plan/m4-phase-exit.md §1): the CLI ships as a dotnet global tool with signed-release
infrastructure, SBOM, SLSA provenance and OS-installer skeletons staged; 18 Core providers across
8 families are merged on `main` (PR #158, commit `d2c4b3d`); the four-technology reference
scenario is green from both CLI and editor; the memory-leak gate has been green continuously
since M1; the community provider hub (github.com/tomas-rampas/vouchfx-providers) is public with
green CI (S12-F-01); and the telemetry backend is engineering-complete and deploy-ready in its
own repository. Sprint 12's only two tasks with recorded progress are the two engineering ones
(S12-F-01, S12-G-01 — plan/sprint-12.md).

**Commercial track — never started.** Zero git tags, zero GitHub releases, zero runs of the
release workflow (including the smoke test RELEASING.md mandates before the first real tag);
neither `vouchfx` nor `Platform.Sdk` exists on NuGet.org; every availability cell in
plan/product-naming.md is TODO and its own decision deadline ("before Sprint 8 ends") has been
overtaken by the S08 contract freeze; the firm-date placeholder in plan/vendor-entity.md (line
348) is blank and issue #81 is open; plan/pilot-recruitment.md states explicitly that "No actual
outreach has been performed" (lines 1–8); the M3 and M4 sign-off blocks
(plan/m3-phase-exit.md §5, plan/m4-phase-exit.md §5) are both empty; and the eight pilot/release
tasks of Sprint 12 (S12-E-01..06, S12-D-01/02) have no recorded progress.

**Perception amplifiers.** The public surfaces understate or misstate readiness in both
directions: ~100 of 124 open GitHub issues are task-tracking issues for already-merged sprint
work, so an external audit of the tracker reads as chaos (GTM-28); meanwhile README.md:31 claims
the Provider SDK "is published as a NuGet package" (it is packable, not published) and the
documented install command `dotnet tool install -g vouchfx` fails today
(docs/getting-started.md:39-60 is honest about this; README is not). The landing page is two
sprints stale (site/index.html:47 hero badge, :274-281 six-provider grid) and 13 references
across 7 files point at the non-existent `vouchfx-org` organisation (9 in copyable snippets,
4 in the CODEOWNERS placeholder block) — both being fixed in this change-set (GTM-19).

---

## 4. Gap Register

### 4.1 Release & distribution

| ID | Gap | Severity | Owner | Evidence | Remediation | Status |
|---|---|---|---|---|---|---|
| GTM-01 | Release channel never exercised: 0 tags, 0 releases, 0 runs of the release workflow — including the `workflow_dispatch` smoke test RELEASING.md mandates before the first real tag. A never-run ~900-line workflow will surface defects that must not be discovered on tag day. | Blocker | RE | `git tag --list` empty; `gh release list` empty; release workflow 0 total runs via GitHub API (checked 2026-07-04); triggers confined to release/tag/dispatch (`.github/workflows/release.yml:57-72`); README.md:31 "ready and verified" reflects static verification only | Run the `0.0.0-test` smoke dispatch now (needs no secrets — keyless cosign/SLSA), fix what it surfaces, then walk RELEASING.md's first-release checklist in order | Addressed in this change-set (2026-07-05): smoke run executed; all six build/sign jobs green, one publish-job defect found and fixed in follow-up PR; gap remains Open until a real tagged release |
| GTM-02 | NuGet channel absent: `vouchfx` and `Platform.Sdk` IDs unreserved (BlobNotFound live, 2026-07-04); `NUGET_API_KEY` unprovisioned, so a tag-cut today silently **skips** the push (release.yml:795-914) and produces a release whose documented install command fails; release.yml packs only the CLI nupkg — **Platform.Sdk has no publish channel at all**, yet vouchfx-providers depends on it via a local feed with one "REMOVE AFTER SDK PUBLISHED" marker; `Platform.Sdk` is a generic ID with prefix-reservation/squatting risk | Blocker | RE | api.nuget.org BlobNotFound for both IDs; `src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj:35-58` (PackAsTool, PackageId=vouchfx, Version 1.0.0); vouchfx-providers local-feed bootstrap | After GTM-29: create the nuget.org account under the entity/name, reserve both IDs (consider prefix reservation), provision `NUGET_API_KEY`, add a Platform.Sdk pack/push job to release.yml before v1.0 | Open |
| GTM-03 | Signing certificates unprovisioned (Authenticode, Apple Developer ID, GPG) — M4 Gate 1. A release cut now ships unsigned binaries except keyless cosign; docs/03 Table 9.1 commits to signed installers. macOS notarisation additionally needs a Developer-ID codesign step the skeleton lacks (RELEASING.md) | Blocker (formal) | RE | plan/m4-phase-exit.md §4 Gate 1 (engineering staged; provisioning pending); all four secret sets absent | Start Authenticode + Apple Developer ID procurement immediately (Apple enrolment alone can take 1–2 weeks); scope the missing codesign step while waiting; configure the CI secret store | Open |
| GTM-04 | VSCode Marketplace: no publish automation (release.yml only *attaches* the .vsix), publisher-account registration status unknown, extension version 0.1.0 vs engine 1.0.0 | Major | RE | `tools/vscode-vouchfx/package.json` (publisher `vouchfx`, version 0.1.0); S12-D-01 requires Marketplace presence at v1.0 | Register the publisher (needs GTM-29), align the version, publish manually at v1.0 per S12-D-01; automation can wait | Open |
| GTM-05 | OS coverage claims vs tested reality: the release manifest commits to Windows 10/11, macOS 13+, Ubuntu/Debian (docs/03 Table 9.1), but the .msi/.deb/.pkg are self-described skeletons never built in CI and never install-tested on any OS; no macOS run is recorded anywhere in CI (build.yml jobs are ubuntu-only; the release workflow has zero runs) — a macOS pilot team may be the first human ever to run vouchfx there | Major | RE | 0 release-workflow runs; plan/m4-phase-exit.md:91-103 (local pack/install validation commands); S12-D-01 acceptance requires verified installs on all three OSes | After the GTM-01 smoke run, execute the pack/install validation on each OS including the DCP-cold-machine case; record as S12-D-01 evidence. Tool-install verification is P0; installers may follow if the nupkg path is verified first | Open |
| GTM-06 | GitLab live-pipeline run (#153) never executed; dind↔DCP networking is an acknowledged unknown (`TESTCONTAINERS_HOST_OVERRIDE=docker` untested against DCP). A Blocker for GitLab-first shops | Major | RE | `ci/gitlab/vouchfx-run.gitlab-ci.yml` static-validated only; plan/m4-phase-exit.md §4 Gate 2 | Execute #153 before onboarding any GitLab pilot; until then label the template "experimental". Formal deferral is permitted by plan/m4-phase-exit.md §5 — record the decision rather than leaving the gate ambiguous | Open |
| GTM-07 | CI templates default to building the engine from source per consumer run (slow pipelines); headers carry stale "Sprint 11" future-tense wording | Minor | RE | `.github/workflows/vouchfx-run.yml:9-18`; GitLab template header | Flip the template default to the published tool at v1.0; keep from-source as the pinned-ref escape hatch | Open |

### 4.2 Product & first-contact

| ID | Gap | Severity | Owner | Evidence | Remediation | Status |
|---|---|---|---|---|---|---|
| GTM-08 | SUT configuration surface: no `env:` on services and no container-context `${conn:dep}` references — a containerised SUT could not reach managed dependencies at all (hard adoption blocker), including `host.docker.internal` reachability | Blocker | ENG | Identified during the adoption-lens review; remediation in flight | `env:` on services + `${conn:dep}` container-context resolution + host reachability — see §6(a) | Addressed in this change-set |
| GTM-09 | Aspire/DCP prerequisite cliff: the framework-dependent nupkg deliberately omits DCP binaries; on a machine that never restored an Aspire project the very first `vouchfx run` after a clean tool install dies with an infrastructure error, not an actionable message. RELEASING.md itself says the user-facing explanation "belongs in README/docs"; README buries it in one sentence. Directly threatens the <60-minute time-to-first-test guardrail | Major | ENG + DOCS | release.yml:34-52; RELEASING.md:143-185, :300-309 | (i) Detect the missing-DCP condition at CLI startup and emit a one-line remedy (`dotnet workload install aspire`); (ii) add a `vouchfx doctor`-style preflight (Docker reachable? DCP present? SDK version?); (iii) first-class Prerequisites callout in README/getting-started including first-run expectations (image pulls dominate the first run) | Open |
| GTM-10 | Service health gate hard-coded to `HTTP GET /` — no `healthPath` knob. Any real customer image that 404s on `/` (APIs exposing only `/health`, `/ready`, `/swagger`) can never pass the 120 s gate: every run is an EnvironmentError, and nothing in the docs explains why. The sharpest residual first-contact risk after GTM-08 lands — that fix gets the SUT *connected* but not necessarily *healthy* | Major | ENG | `src/Engine/Platform.Engine.Orchestration/EnvironmentMapper.cs:566-576` (verified :570); `ServiceSpec` has no health field (`src/Engine/Platform.Engine.Authoring/Model/EnvironmentSpec.cs:68-72`) | Add optional `healthPath:` (default `/`) to ServiceSpec + schema + language-reference; document the failure signature ("HealthGate timeout → check your image answers 2xx on the health path") in troubleshooting. Ship with, or immediately behind, the in-flight SUT surface | Open |
| GTM-11 | `project:`-form services get no endpoint discovery — no `svc::<name>` is staged, so an `http.rest` `target:` pointing at a project service fails confusingly mid-run. DSL §3.2 presents `project:` as fully supported with no documented endpoint-discovery limitation, making a .NET team's most natural first move ("point it at my csproj") silently fail | Major | ENG + DOCS | `EnvironmentMapper.cs:511-512, 578-586`; docs/02 §3.2 | Minimum bar: authoring-time validation error ("http.rest cannot target a project-form service in v1") + documented limitation; proper fix: stage the project's Aspire-discovered endpoint as `svc::<name>` | Open |
| GTM-12 | Seed asymmetry vs the 18-provider surface: `sql:` seed dispatches to postgres **only**, `publish:` to kafka **only**, `documents:` to mongodb/elasticsearch/cosmos — so the `db-assert.mysql`/`db-assert.sqlserver` providers shipped in PR #158 have no declarative way to create the schema they assert against, and rabbitmq/nats/azureservicebus have no broker pre-provisioning. Authors fall back to `script.csharp` bootstrap steps exactly where a new evaluator tests the declarative premise; the samples repo (§6(b)) will collide with this immediately | Major | ENG + DOCS | `src/Engine/Platform.Engine.Orchestration/SeedApplier.cs:81-90, 209-228` (verified :217); the mq-rabbitmq example's "queue must pre-exist" note | Extend the SeedApplier `sql` dispatch to mysql/sqlserver before the samples suites need it; document the interim `script.csharp` bootstrap pattern per technology; decide and record the broker-provisioning story (seed grammar vs `extra:` fields) | Open |
| GTM-13 | `imagePullPolicy` parsed but never enforced: schema enum + parser + model exist with zero consumers (no `WithImagePullPolicy` anywhere in `src/`); docs/02 §3.2.1 promises default logic that does not exist; docs/common-patterns.md:138 documents a value (`IfNotPresent`) not even in the enum. `Never` (air-gapped) and `Always` (moving tags) both silently behave as the runtime default — silent acceptance of an ineffective knob is worse than not having it | Major | ENG | `root-language-schema.json:81-85`; `YamlDocumentParser.cs:117,494`; `EnvironmentSpec.cs:45,71`; grep over `src/` | Either implement, or reject the field at validation with "not supported in v1" and strip it from schema/docs; fix the common-patterns contradiction in the docs pass either way | Open |
| GTM-14 | Time-to-first-test guardrail (<60 min starter / <90 min reference — a formal GA criterion, docs/03 Table 4.1) has never been observed with an outside user, and the current path (build from source, Docker verification, first image pulls, the GTM-09 cliff) plausibly exceeds it before authoring begins. docs/getting-started.md:5 claims 60 minutes *including* building from source | Major | PD + DOCS | docs/03 Table 4.1; docs/getting-started.md:5, :39-60 | Once the tool-install path exists, run 2–3 timed clean-machine walkthroughs (Windows, macOS, Linux); record the timings as gate evidence; trim getting-started to the packaged path | Open |
| GTM-15 | Weak IDE guardrails: the environment-level schema sets `additionalProperties: false`, but the `services`/`dependencies`/`seed` sub-objects are untyped `type: object` — a misspelled `httpPort`, `image` or dependency `type` gets no squiggle. This is exactly where the eight broken examples (GTM-17) went wrong; the VSCode extension's headline value is blind in the section novices edit first | Major | ENG | `src/Engine/Platform.Engine.Compilation/Schema/root-language-schema.json:62-87` | Type the service sub-schema and a discriminated dependency sub-schema per kind; tightening validation is behaviourally breaking for files that relied on looseness, so run it consciously through the golden-file gate (`VOUCHFX_REGEN_SCHEMA` infrastructure exists) | Open |
| GTM-16 | Authoring error messages do not teach: the project's own examples independently invented a nested `request:` shape and ISO-8601 durations — real authors will make identical mistakes and get unhelpful parse errors | Minor | ENG | `src/Engine/Platform.Engine.Authoring/DurationParser.cs:17-23` (accepts `<n>`, `<n>ms`, `<n>s`, `<n>m`); http.rest schema `required [target, method, path]` | Make the two errors teaching errors: duration failure says "ISO-8601 (PT30S) is not supported; use 30s or an integer for seconds"; unknown-property `request:` hints "http.rest takes method/path at top level" | Open |

### 4.3 Documentation & examples

| ID | Gap | Severity | Owner | Evidence | Remediation | Status |
|---|---|---|---|---|---|---|
| GTM-17 | Eight shipped per-provider examples are unrunnable as written and not CI-validated (nested `request:` on http.rest, phantom `url:/statusCode:` fields, ISO-8601 `PT30S`/`PT10S` timeouts, mongodb-only with sequence `dependencies` declaration); only the reference scenario is validated in CI | Major | ENG | `examples/` cache-assert-redis:28, mail-expect-smtp:29, mq-nats:40, mq-rabbitmq:37, mq-azureservicebus:53, db-assert-mongodb:24, cache-assert-elasticsearch:25, db-assert-mysql:19/:22; only db-assert-sqlserver.e2e.yaml is clean | Fix all eight + add an examples-compile CI gate over every `examples/**/*.e2e.yaml` — see §6(d). Residual: the samples repo needs its own equivalent gate | Addressed in this change-set (2026-07-05): all eight examples repaired and CI-compiled; ExamplesCompileTests gate active |
| GTM-18 | No runnable real-world sample applications: beyond the reference scenario there is nothing showing vouchfx against services in multiple languages across the 18-provider matrix — the highest-leverage first-contact artefact is missing | Major | ENG + DOCS | S12-E-04 (#118) names porting examples as the natural anchor; examples/ contains reference + getting-started + ci-reference/smoke.e2e.yaml + (broken) per-provider files + Example.Steps.Hello/Echo SDK samples | Public **vouchfx-samples** repository: C#/Python/Java sample apps + suites across postgres/mysql/sqlserver/kafka/rabbitmq/nats/redis/smtp — see §6(b) | Addressed in this change-set |
| GTM-19 | Site/docs/cross-repo misalignment: landing page two sprints stale (hero badge site/index.html:47; six-provider grid :274-281; roadmap :330-356 treats S11 as future and M5 as not started); phantom `vouchfx-org` org in 13 references across 7 files (9 in copyable snippets: README.md:134,175,202; docs/getting-started.md:44; docs/recipes.md:550,597; docs/troubleshooting.md:95; docs/telemetry.md:288; vouchfx-run-reference.yml; 4 in CODEOWNERS placeholder block); engine README links neither satellite repo and README.md:32 still says community tiers are "still to come"; telemetry-backend repo has no LICENSE file despite its README claiming Apache-2.0; stale "Sprint 11" future tense across install surfaces | Major | DOCS + ENG | Citations as listed; vouchfx-telemetry-backend `licenseInfo` null via GitHub API | Full alignment pass across site, docs, README and satellite repos — engine-repo edits in this branch; the telemetry-backend LICENSE and providers-hub README refresh land as sibling-repo commits within the same programme — see §6(c) | Addressed in this change-set |
| GTM-20 | Documentation coverage debt vs the 18-provider reality: docs/recipes.md has 8 recipes over 4 of 18 providers and no Kafka recipe despite docs/getting-started.md:235 promising one; docs/common-patterns.md:430-570 covers the original 6 only; docs/troubleshooting.md's provider section is Kafka-only (:449); docs/language-reference.md says "Registered step types (16)" vs 18 shipped. A team adopting for RabbitMQ or SQL Server — the audience the batch expansion targeted — finds zero recipes, zero troubleshooting, zero patterns | Major | DOCS | Citations as listed | Run the `VOUCHFX_REGEN_SCHEMA` language-reference regen now (mechanical, minutes — the "can never drift" credibility page); then a family-level pass (one recipe + one troubleshooting entry per family, not per provider); cross-link the samples suites as executable recipes rather than duplicating | Addressed in this change-set (2026-07-05) |
| GTM-21 | PB-02 state-reset limitation undocumented: per-scenario reset (Respawn) is Postgres-only; multi-scenario suites against mysql/sqlserver/mongo/redis/elasticsearch get cross-scenario state bleed — nondeterministic failures that look like product flakiness and threaten the <1-in-500 guardrail *as perceived by pilots*. Acceptable as a documented v1 limitation; Major while silent | Major | DOCS (now) + ENG (post-v1) | plan/post-v1-backlog.md PB-02 | Prominent "state isolation per store" matrix in common-patterns + a troubleshooting entry describing the bleed symptom and the parallel-path workaround; ensure the samples suites for those stores model the workaround | Addressed in this change-set (2026-07-05) |
| GTM-22 | Migration/porting examples (S12-E-04) not started: Postman/xUnit-integration/SpecFlow ports are the highest-leverage conversion artefact ("here is your Postman collection as an .e2e.yaml") and the natural week-1 pilot exercise | Minor | PD + DOCS | Issue #118 open, zero progress; plan/sprint-12.md | One worked port per source tool, hosted in vouchfx-samples, linked from getting-started "Next steps"; complete before pilot onboarding | Open |
| GTM-23 | Authoring conventions live only in code/comments: the mailpit `svc::<name>-smtp` / `conn::<name>` key convention and the azureservicebus `extra.queues/topics` grammar appear in no authoring doc; the `Vars["conn::…"]` literal-key vs `{placeholder}` bare-identifier split is only learnable from the reference example; stale in-code claims that the bare `cache-assert` alias still resolves | Minor | DOCS | `ScenarioRunner.cs:1204-1215`; `AstBuilder.cs:173-261`; EnvironmentMapper registry comments | Short "how names resolve" section plus the alias table in language-reference; document the mailpit/ASB conventions; purge stale alias claims | Open |

### 4.4 Ecosystem & community

| ID | Gap | Severity | Owner | Evidence | Remediation | Status |
|---|---|---|---|---|---|---|
| GTM-24 | Launch governance artefacts unpublished (S12-E-01, #115) — all must be live **before** launch day: open-source/commercial feature boundary (docs/03 §9.7), public roadmap (§9.8), governance doc with the Verified rubric (§9.6), trademark policy (§9.9 — published at launch, not after the first dispute), CODE_OF_CONDUCT.md + DCO wiring on the engine repo, SECURITY.md on the providers hub, mirrored CoC/SECURITY/DCO on the telemetry backend. Partial reality: the engine repo already has SECURITY.md; the feature boundary and public roadmap exist only inside docs/03 | Blocker | PD + Legal | plan/sprint-12.md:77-84; vouchfx-providers repo hygiene vs engine repo root | Extract §9.6–9.9 into standalone published artefacts (~2 days writing + legal review of the trademark policy, which depends on GTM-29/31); add CODE_OF_CONDUCT.md + DCO wiring to engine repo; add SECURITY.md to providers hub; mirror both on telemetry backend | Partially addressed in this change-set (2026-07-05): CODE_OF_CONDUCT.md added; trademark policy/feature boundary/public roadmap remain Open |
| GTM-25 | Community hub looks dead at launch: `registry/community-providers.json` is `[]`; the providers README still lists 6 Core providers vs 18; the conformance CI engine pin (`26d4fa5`) is two merges behind main. The community-pathway GA gate (≥3 Community providers in 6 months) starts from an empty index, and provider-author onboarding requires the 5-project local-feed pack loop until Platform.Sdk publishes (GTM-02) | Minor | PD + ENG | vouchfx-providers registry, README, conformance.yml pin | Seed ≥1 first-party Verified-candidate or Community entry (the clean-room `text.reverse` provider from S08-F-05 is the natural seed); de-enumerate the Core list ("see engine README"); advance the engine SHA pin | Open |
| GTM-26 | Discoverability — repo metadata now addressed, hyperlinks remain: the providers README says "listed on the project website" twice without hyperlinking the live Pages site (tomas-rampas.github.io/vouchfx). The `homepageUrl` field on all three repos was set to the Pages URL as part of this change-set. | Minor | PD | providers README §3 | Hyperlink the Pages site from both mentions in the providers README | Addressed in this change-set (2026-07-05): homepage fields set; provider README hyperlinks remain |
| GTM-27 | Governance-surface inconsistency across satellites: vouchfx-telemetry-backend lacks CONTRIBUTING/CODE_OF_CONDUCT/SECURITY files; vouchfx-providers lacks SECURITY (its missing LICENSE is covered by GTM-19) | Minor | ENG | Repo root listings | Add SECURITY.md to providers hub; mirror the providers-hub + SECURITY file set on the telemetry backend | Open |
| GTM-28 | Issue-tracker hygiene: ~100 of 124 open issues are task-tracking issues for Sprint 01–11 work already merged to main (e.g. #15–#80 minus a few). Anyone auditing readiness from GitHub gets a wildly wrong picture — cheap pre-launch credibility work | Minor | PD + ENG | GitHub issue list (checked 2026-07-04); genuinely open ≈ 16: #152, #153, #154, #115–#123, #114, #87–#90, #92–#113, epics | Bulk-close with a "delivered in PR #N" comment; leave the genuinely open set | Open |

### 4.5 Commercial & legal

| ID | Gap | Severity | Owner | Evidence | Remediation | Status |
|---|---|---|---|---|---|---|
| GTM-29 | Naming decision unmade while the frozen artefacts already carry the working name: plan/product-naming.md lists 10 candidates with every one of ~80 availability cells TODO and an empty scoring worksheet; its own deadline per §5.3 is "The name must be confirmed before Sprint 8 ends", which has been overtaken — `Platform.Sdk 1.0.0`, the `vouchfx` CLI/ToolCommandName, `VOUCHFX_*` env vars and `x-vouchfx-schema-version` are all frozen under the working name by the S08 golden-file gates; the §6.3 namespace decision was never recorded; plan/sprint-12.md:13 still says v1.0 releases "under the chosen name". Renaming now is a v2 breaking change by the project's own freeze rules; the de-facto decision is "ship as vouchfx" but no artefact says so. Gates every ID reservation (GTM-02, GTM-04), the org/domain, and trademark filing (GTM-31) | Blocker | PD | plan/product-naming.md (all cells TODO, §3 scoring worksheet empty, §6.3 unrecorded); plan/sprint-12.md:13 | Run the knock-out search on **"vouchfx" itself** (~2 days): trademark classes 9/42 CZ/EU/US, vouchfx.dev/.io/.com, NuGet `vouchfx`, GitHub org. If clean, record "launch as vouchfx; Platform.\* namespaces retained for v1.x" in product-naming.md §6.3 and CLAUDE.md; only if dirty, run the shortlist scoring (§5.1) | Open |
| GTM-30 | No vendor entity and no legal artefacts: plan/vendor-entity.md is a planning artefact (recommendation CZ s.r.o.; firm-date placeholder blank at line 348; issue #81 open); three P1 templates are uncommissioned — DPA, MSA/EULA, Pilot/Beta Programme Agreement — plus privacy policy (P2 in vendor-entity.md §6.3). MVP §10 rates "no vendor entity blocks enterprise procurement" Med/High; the demand gate's refundable deposits require the entity's bank account (vendor-entity.md §1.2). Any enterprise-track pilot candidate stalls at procurement; the deposit half of GA Gate 2 is mechanically impossible | Blocker | PD + Legal | plan/vendor-entity.md:348, §1.2, §6.3–6.4; issue #81; docs/03 §10 | Book the legal consultation this week, record the firm incorporation date, and commission the three P1 templates + privacy policy in the same engagement. Do not serialise behind GTM-29 — the entity can be named after the product or neutrally | Open |
| GTM-31 | Trademark: no knock-out search run, nothing filed, priority date unsecured while the project is publicly visible on GitHub and Pages — the exact squatter scenario vendor-entity.md §1.4 warns about. Budget already scoped (EUR 3,500–8,000; EUIPO+ÚPV+US ITU simultaneous initial filings, WIPO IR in follow-up) | Blocker | Legal | plan/vendor-entity.md §4.4 (knock-out results marked blocker), §1.4 | After the GTM-29 knock-out: file EUIPO+ÚPV+US ITU classes 9+42 as soon as the entity exists; publish the trademark policy (feeds GTM-24) at launch | Open |
| GTM-32 | M3 and M4 formal exit gates all open, sign-off blocks empty. M3: named outside-contributor SDK sign-off (#86 — the engineering substance is already clean-room-validated; this is a signature), steering review, TL sign-off on the three frozen golden files (#91). M4: certificate provisioning (= GTM-03), GitLab live run (= GTM-06, deferrable with a recorded decision), steering review (#114). Formal M4 gates S12-D-01; an unreached M3/M4 undermines the launch narrative. Tracked via #91/#114 — the phase-exit review package tasks containing the sign-off items | Blocker (formal) | PD + TL | plan/m3-phase-exit.md §4–5; plan/m4-phase-exit.md §4–5 (both sign-off blocks empty) | Schedule one combined M3+M4 steering session (same attendees, one calendar slot); TL signs the freeze in the same meeting; PD recruits the named outside contributor | Open |
| GTM-33 | Pricing validation not begun: the docs/03 §4.4 Table 4.2 bands (Indie free; Team $25–40/seat or $50/mo + per-container-minute; Enterprise $150–250/seat) exist as hypothesis only; GA Gate 2 requires ≥4 non-binding pre-commitments at band **and** ≥2 refundable deposits — deposits blocked on GTM-30's bank account; band refinement is designed to happen inside pilot conversations, which have not started (GTM-35) | Major | PD | docs/03 §4.4 Table 4.2; docs/03:153-163 (Table 4.1 gates); plan/vendor-entity.md §1.2 | Fold the pricing conversation script into the week-3 pilot retrospective agenda; sequence the deposit ask for after incorporation | Open |
| GTM-34 | Launch communications and 30-day support SLA rota not prepared (S12-D-02, #122): website walkthrough, GitHub release notes, Show HN/Lobsters, /r/dotnet, .NET Foundation Slack/Discord, long-form blog, day-14 second post, SLAs (issues <1 business day, PRs <5 days), success test = ≥2 external provider-repo PRs by day 30. Cheap, but must not be started on launch morning | Major | PD + TL | docs/03 §9.4 Table 9.2; issue #122 | After GTM-01 and GTM-24 land: draft the launch post + day-14 post, name the SLA rota, pre-write the Show HN | Open |

### 4.6 Measurement & pilots

| ID | Gap | Severity | Owner | Evidence | Remediation | Status |
|---|---|---|---|---|---|---|
| GTM-35 | Pilot recruitment never started — the longest pole. The source line (plan/pilot-recruitment.md:1-8) is time-bounded ("by the creation of this document", a Sprint-6 artefact) and states "No actual outreach has been performed"; additionally, no outreach tracker or S06-E-01 (#72) progress is recorded anywhere since. Lead time from cold outreach to an onboarded team is 6–10 weeks (§1.2); target is 8 committed teams (30 % attrition buffer over the 6-team GA gate); the message templates, tracking schema, pipeline thresholds and 5-step fallback ladder are all ready-to-send; the conference-CFP calendar is stale. The M5 exit criterion of ≥8 onboarded teams is mechanically impossible until roughly two months after outreach starts | Blocker | PD | plan/pilot-recruitment.md:1-8, §1.2, §3–§6; plan/sprint-12.md:161-169 (M5 exit); issue #72 (S06-E-01) | Send the first tranche of direct-outreach messages **this week** under the working name (the template already calls vouchfx "a working name" — pilot-recruitment.md:105 — so GTM-29 does not block outreach); stand up the candidate tracker; prioritise SMB/OSS-friendly channels first because enterprise-track candidates are gated on GTM-30's pilot agreement | Open |
| GTM-36 | Telemetry backend undeployed: Phase B is engineering-complete and pushed (vouchfx-telemetry-backend, 2026-06-29) with an honest README ("complete and ready for deployment… operator deployment required"), but Azure is unprovisioned and the deploy.yml secrets (`TELEMETRY_INGEST_TOKENS`/`DB_ADMIN_PASSWORD`/`DB_CONNECTION_STRING`) are explicitly PLACEHOLDER; the engine Phase A transport (PR #155) is inert until endpoint + token are configured. Explicit prerequisite for S12-E-05 — i.e. for measuring GA Gate 1 *at all*. Hard constraint: the backend must be live **before** the six-week window opens, or it effectively restarts (the gate needs six consecutive measured weeks) | Blocker | ENG + RE | vouchfx-telemetry-backend deploy.yml (placeholder secrets); plan/sprint-12.md:26-73; docs/telemetry.md:284 ("officially hosted pilot instance is pending deployment") | Provision Azure (OIDC identity, ACR, resource group), populate the three secrets, run deploy.yml, smoke `/healthz` + `/v1/telemetry`, then configure the engine endpoint/token for the pilot cohort. Schedule slack exists behind GTM-35's lead time, but it must land within ~6 weeks | Open |
| GTM-37 | Go/no-go calendar never re-baselined: the four-outcome model (Go / Iterate-locally / Pivot / Stop, docs/03:558-571 Table 11.1) with conjunctive gates is well-defined and S12-E-06 (#120) owns the written assessment, but no artefact reconciles the original week-24 framing with today (engineering done, commercial track unstarted); the community-gate clocks (≥3 Community providers in 6 months, ≥1 Verified in 12) start at v1.0, not at gate-read | Major | PD | docs/03:558-571; plan/pilot-recruitment.md §5 (calendar-stale weeks 11–16) | Publish a re-baselined launch calendar in plan/ — T0 outreach, backend-live deadline, v1.0 target date, window open/close, gate-read date — and make it the single schedule of record for #115–#122. §5.4 below supplies the arithmetic | Open |

---

## 5. Critical Path to Launch

### 5.1 The naming-decision fork (call it first)

Every artefact with an identifier in it hangs off GTM-29, and the fork must be called explicitly
because **the v1 artefacts are already frozen under the working name**: `Platform.Sdk 1.0.0`, the
`vouchfx` CLI and ToolCommandName, `VOUCHFX_*` environment variables and
`x-vouchfx-schema-version` are all enforced by the S08 golden-file freeze gates, and
plan/product-naming.md's own decision deadline (before the Sprint 8 freeze) has passed.

- **Branch A — confirm "vouchfx" (expected, fast).** Run the knock-out search on the working
  name itself: trademark classes 9/42 (CZ/EU/US), vouchfx.dev/.io/.com, NuGet `vouchfx`, GitHub
  org. If clean, record "launch as vouchfx; `Platform.*` namespaces retained for v1.x" in
  plan/product-naming.md §6.3 and CLAUDE.md. No engineering impact; unblocks GTM-02, GTM-04,
  GTM-30 (entity name, if product-named) and GTM-31 within days. This branch also resolves
  whether a `vouchfx-org` organisation is created or the docs continue to point at
  `tomas-rampas` (the latter is what the in-flight alignment change, §6(c), implements).
- **Branch B — knock-out dirty (slow, disruptive).** Run the plan/product-naming.md §3 scoring
  worksheet over the shortlist. The public-facing name then diverges from the frozen artefact
  identifiers, or a rename is accepted as a v2-scale breaking change — which contradicts the
  freeze discipline the project enforces via golden-file CI gates and would reset release
  engineering. Either sub-branch adds weeks and must complete before any ID reservation,
  Marketplace publisher registration or trademark filing.

Crucially, the fork does **not** gate pilot outreach (the outreach template already presents
vouchfx as "a working name", plan/pilot-recruitment.md:105) and does not gate the legal
consultation (the entity can be named neutrally).

### 5.2 Start-this-week set (no upstream dependencies)

Seven actions have no upstream dependency and should all begin in the week commencing
2026-07-06: GTM-35 first outreach tranche; GTM-30 legal consultation; GTM-29 knock-out search;
GTM-01 release smoke run; GTM-03 certificate procurement (Apple enrolment alone can take 1–2
weeks); GTM-28 issue close-out; GTM-37 calendar re-baseline. Everything else hangs off these.

### 5.3 Ordered dependency narrative

1. **Naming resolves first among the gating decisions** (GTM-29, §5.1). Its outputs feed the
   NuGet account and ID reservations plus the Platform.Sdk publish channel (GTM-02), the
   Marketplace publisher registration (GTM-04), and — jointly with the entity (GTM-30) — the
   trademark filing (GTM-31) and the trademark-policy half of the governance set (GTM-24).
2. **The legal chain runs in parallel**: consultation → incorporation (GTM-30) → bank account
   (enables the GA Gate 2 deposit mechanics, GTM-33) and → EUIPO filing (GTM-31). Enterprise
   pilot onboarding waits on the Pilot/Beta Programme Agreement from this chain; SMB/OSS pilots
   do not — hence the channel prioritisation in GTM-35.
3. **The release chain**: GTM-01 smoke run (now) → fix what it surfaces → GTM-02 IDs/key →
   GTM-03 certs land → formal M4 via GTM-32 (steering review; GTM-06 run or formal deferral per
   plan/m4-phase-exit.md §5) → **S12-D-01 v1.0 release**, whose acceptance additionally requires
   GTM-04 (Marketplace) and GTM-05 (verified installs on all three OSes). GTM-24 governance
   artefacts and GTM-34 launch comms must be live/ready *before* launch day.
4. **The adoption chain runs alongside the release chain**: the in-flight change-set (§6) closes
   GTM-08/17/18/19; the residual first-contact set — GTM-10 (health path), GTM-11 (project-form
   validation), GTM-12 (mysql/sqlserver seed), GTM-09 (DCP message/preflight), GTM-13
   (imagePullPolicy implement-or-reject), GTM-20 (language-reference regen + family recipes),
   GTM-21 (isolation matrix) — must be triaged before pilot onboarding (S12-E-02), because these
   will otherwise surface *inside* the measurement window as perceived product flakiness.
5. **The measurement chain is the schedule driver**: GTM-35 outreach (T0, now) → screening →
   committed cohort → S12-E-02 onboarding (needs the adoption chain and, for enterprise
   candidates, the legal chain) → six-week active window (S12-E-05, which needs GTM-36 deployed
   **before** the window opens) → S12-E-06 written go/no-go read against the three conjunctive
   GA gates (docs/03:153-163), with the pricing/deposit conversations (GTM-33) folded into the
   week-3 retrospectives.

```
GTM-29 naming ──┬─► GTM-02 NuGet IDs/key ──┐
                ├─► GTM-04 Marketplace      ├─► GTM-01 smoke ─► S12-D-01 v1.0 ─► GTM-34 comms ─► LAUNCH
                └─► GTM-31 trademark ◄── GTM-30 entity                 ▲
GTM-30 entity ──┬─► pilot agreement ─► enterprise pilots     GTM-24 governance artefacts
                └─► bank account ─► deposits (GA Gate 2, GTM-33)
GTM-03 certs ─────────► M4 Gate 1 ─┐
GTM-06 #153 (or defer) ► M4 Gate 2 ─┼─► formal M4 (GTM-32) ─► S12-D-01
combined steering ────► M3+M4 Gate 3┘
GTM-35 outreach (T0, longest lead) ─► S12-E-02 onboard ─► 6-week window ─► S12-E-06 go/no-go
GTM-36 backend deploy (before window opens) ─► S12-E-05 instrumentation ────────┘
```

### 5.4 Timeline arithmetic

Outreach start T0 → +6–10 weeks recruitment (plan/pilot-recruitment.md §1.2) → S12-E-02
onboarding → six-week active measurement window (S12-E-05) → gate read (S12-E-06). **Core
arithmetic: T0 + 12–16 weeks.** With T0 = week commencing 2026-07-06, the gate read lands in
late September at the absolute optimistic edge, but **realistically mid-October to mid-November
2026** — the mid-November tail reflects onboarding/slippage beyond the core 12–16 weeks. The
v1.0 release (S12-D-01) sits comfortably inside this envelope provided the start-this-week set
(§5.2) actually starts this week; the schedule risk is concentrated entirely in GTM-35 and
GTM-30, neither of which any engineering work can compress.

---

## 6. What This Change-Set Is Addressing

Four remediations are in flight in the current change-set and carry the status "Addressed in
this change-set" in the register. They land together with this document as one programme:

| Item | Gap | What it addresses |
|---|---|---|
| (a) SUT configuration surface | GTM-08 | `env:` on services with container-context `${conn:dep}` connection-string references and `host.docker.internal` reachability — addressing a hard adoption blocker: a containerised SUT cannot reach its managed dependencies at all. |
| (b) vouchfx-samples repository | GTM-18 | A public samples repo with real-world C#/Python/Java applications and suites across postgres/mysql/sqlserver/kafka/rabbitmq/nats/redis/smtp; also the natural home for the S12-E-04 porting examples (GTM-22). |
| (c) Site/docs/cross-repo alignment | GTM-19 | Landing-page staleness (6→18 providers, Sprint-10 badge, roadmap tense), sibling-repo links from the engine surfaces, the phantom `vouchfx-org` URLs in copyable snippets, stale "Sprint 11" future-tense install wording, and the missing telemetry-backend LICENSE file. |
| (d) Examples repair + CI gate | GTM-17 | The eight broken example files (cache-assert-redis, mail-expect-smtp, mq-nats, mq-rabbitmq, mq-azureservicebus, db-assert-mongodb, cache-assert-elasticsearch, db-assert-mysql) and an examples-compile CI gate so no shipped `.e2e.yaml` can silently drift from the step contracts again. |

**Residual watch-items adjacent to this work, not covered by it** (all registered above, all
engineering-track adoption risks to triage before S12-E-02 onboarding): the hard-coded `GET /`
health gate (GTM-10 — the single sharpest residual risk: item (a) gets the SUT *connected*, but
any image that does not answer 2xx on `/` still times out at the health gate with an
EnvironmentError); `project:`-form endpoint discovery (GTM-11); the mysql/sqlserver seed gap
that the new samples suites will hit immediately (GTM-12); and the `imagePullPolicy` no-op
(GTM-13).

---

## 7. Register of Source Plan Documents

This analysis builds on, and deliberately does not duplicate, the following existing artefacts.
Where this document and a source document overlap, the source document remains authoritative for
its own content; this register only records the launch-readiness *state* of each.

| Document | Role in this analysis |
|---|---|
| [plan/product-naming.md](product-naming.md) | Naming candidates, availability matrix, scoring rubric and the §6.3 namespace decision — the substance behind GTM-29 and the §5.1 fork. |
| [plan/vendor-entity.md](vendor-entity.md) | Entity-form recommendation, trademark plan and budget, and the P1 legal-template inventory — the substance behind GTM-30/31/33. |
| [plan/pilot-recruitment.md](pilot-recruitment.md) | Recruitment channels, ready-to-send templates, pipeline schema, health thresholds and fallback ladder — the substance behind GTM-35 and the §5.4 arithmetic. |
| [plan/m3-phase-exit.md](m3-phase-exit.md) | M3 evidence package and the three open human gates folded into GTM-32. |
| [plan/m4-phase-exit.md](m4-phase-exit.md) | M4 evidence package (18/18 engineering criteria) and the three open human gates behind GTM-03/06/32; also the local pack/install validation used by GTM-05. |
| [plan/post-v1-backlog.md](post-v1-backlog.md) | PB-01/PB-02 deferred scope; PB-02 is surfaced here only as a documentation obligation (GTM-21). |
| [plan/sprint-12.md](sprint-12.md) | The M5 task set (S12-D-01/02, S12-E-01..06, S12-F-01, S12-G-01) and exit criteria that this register maps gaps onto. |

*Navigation note:* several of these (pilot-recruitment, product-naming, vendor-entity, both
phase-exit packages) currently publish on the Pages site only via the auto-render fallback and
are unreachable from the site navigation; promoting them into the `scripts/build_site.py` DOCS
list is part of the site tidy-up under GTM-19/26.

---

*Compiled 2026-07-04. Owner of this register: PD. Review cadence: update at each Sprint 12
checkpoint and at the §5.4 calendar milestones until superseded by the S12-E-06 go/no-go
assessment.*
