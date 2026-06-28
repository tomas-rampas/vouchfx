# Sprint 11 — Tooling & hardening: stabilisation (Milestone M4)

| | |
|---|---|
| **Phase** | 4 — Tooling and hardening (MVP §8.4) |
| **Weeks** | 21 |
| **Length** | **1 week** (stabilisation sprint) |
| **Milestone** | **M4 — Tooling & hardening** closes at end of sprint |
| **Theme** | A short, focused stabilisation sprint: the reference scenario green from both editor and CLI, the secret-redaction path penetration-tested, and the release manifest (signing, SBOM, packaging) ready for the pilot. No new features. |

## Sprint goal

The full reference scenario is green from the editor and the CLI; the security review (including a
redaction penetration test) has passed; and the v1.0 release manifest — signed binaries, SBOMs,
provenance, and the three OS packages — is built and verifiable. The product is ready to put in front of
pilot teams.

> **Capacity note:** one-week sprint; the task slice is deliberately small and hardening-only. Feature
> carry-over from Sprint 10 is triaged out, not crammed in.

## Tasks

### Workstream C — Tooling & CI templates

#### S11-C-01 · GitLab CI template — live-pipeline validation
- **Owner:** TX · **Estimate:** 0.5d · **Depends on:** S10-C-02 · **Spec:** MVP §8.4 (CI templates)
- **Status:** ⏳ Deferred / #153 · **Delivery note:** Static validation complete (yamllint + JSON-schema clean); live-pipeline run scheduled as post-M4 follow-up (infra-gated).
- **Why this was deferred from S10-C-02:** validating the template on real GitLab infrastructure is
  infra-gated — it needs a real GitLab runner (Docker-in-Docker) that the engine's GitHub-Actions CI cannot
  provide. S10-C-02 completed the static validation (yamllint + JSON-schema clean) and recorded the live run
  as an explicit follow-up in `ci/gitlab/README.md`. It belongs in the stabilisation sprint, before M4 closes
  (the M4 exit criterion includes "CI templates published").
- Execute the GitLab CI template (`ci/gitlab/vouchfx-run.gitlab-ci.yml`) on a real GitLab pipeline to confirm
  the live behaviour static linting cannot: Docker-in-Docker service startup, the clone + `dotnet build`
  install path, exit-code gating, and artefact publication.
- **Acceptance:**
  - The template runs green on a real GitLab pipeline (gitlab.com shared runner or an equivalent privileged
    runner) against the reference/smoke suite.
  - Exit-code gating is verified: an induced product Fail breaks the job; an induced Environment error does
    not (without the opt-in flag).
  - Both the JUnit and HTML artefacts are published by the job and downloadable from the pipeline UI.
  - The "Validation status" section of `ci/gitlab/README.md` is updated with the live result, runner type,
    and GitLab version/date.
  - Any behavioural divergence from the GitHub Actions equivalent found during the live run is filed as a
    follow-up issue.

### Workstream D — Integration & hardening

#### S11-D-01 · Reference scenario green from editor and CLI
- **Owner:** TL · **Estimate:** 1.5d · **Depends on:** S09, S10 deliverables · **Spec:** MVP §8.4 (exit criterion), §1 (reference scenario)
- **Status:** ✅ Delivered · **Commit:** `19c3e43`
- The four-technology reference scenario (REST + Kafka + DB + webhook, with seed + secret + RETRY +
  capture) runs green from VSCode and from the CLI, reproducibly from a clean checkout.
- **Acceptance:**
  - The reference scenario passes from both surfaces on the standardised hardware; the memory gate is green.
  - Test A (engine API): `Sprint11ReferenceTests.cs` exercises the reference scenario against discovered endpoints (docker capstone green).
  - Test B (CLI): real `vouchfx run` invocation on live Docker topology verified green.
  - Non-docker compile twin: the same scenario compiles and validates without Docker, confirming schema/compilation paths are decoupled from runtime.

#### S11-D-02 · Release manifest — signing, SBOM, provenance
- **Owner:** TL · **Estimate:** 1.5d · **Depends on:** S01-D-02 · **Spec:** MVP §9.1, §10 (licence/compliance, supply-chain)
- **Status:** ✅ Delivered · **Commit:** `fac3cd4`
- Sign all binaries (Authenticode / Apple Developer ID / GPG), generate SBOMs and provenance
  attestations, and verify the supply-chain story end-to-end.
- **Acceptance:**
  - Binaries verify as signed; SBOM + provenance attach to the release; verification steps documented.
  - CycloneDX SBOM generation integrated into pack/publish pipeline.
  - SLSA v1.0 provenance attestation + keyless cosign signing in CI verified.
  - OS-installer skeleton scripts (`.msi`, `.pkg`, `.deb`, VSCode Marketplace) staged and verified.
  - Certificate provisioning secret-gated in CI configuration (Authenticode, Apple Developer ID, GPG keys).

#### S11-D-03 · Packaging for all three operating systems
- **Owner:** TL · **Estimate:** 1.5d · **Depends on:** S11-D-02 · **Spec:** MVP §9.1 (distribution channels)
- **Status:** ✅ Delivered (part 1) · **Commit:** `790add8`
- Produce the `dotnet tool` package + the secondary installers: signed `.msi` (Windows), notarised
  `.pkg` (macOS), `.deb` (Debian-family), plus the VSCode Marketplace package.
- **Acceptance:**
  - `dotnet tool install -g` works; each OS installer installs and runs a smoke test; the extension
    packages for the Marketplace.
  - CLI packaged as `dotnet` global tool; installation and smoke-test verified on standardised hardware.
  - OS-installer skeleton scripts (`.msi`, `.pkg`, `.deb`) staged; final signing deferred to certificate provisioning gate.

### Workstream B — Compiler & runtime

#### S11-B-01 · Security review — secret-redaction penetration test
- **Owner:** CR1 · **Estimate:** 1.5d · **Depends on:** S08-B-01, S05-G-01 · **Spec:** BP §17.1.1; MVP §8.4, §10 (secret-leak risk), §5.5 (security sign-off)
- **Status:** ✅ Delivered · **Commit:** `8bcb1ae`
- Penetrate the redaction path on realistic OAuth and AWS-signing scenarios where base64, HMAC-signed, or
  partially-logged forms could escape redaction; confirm the typed `SecretString` (not string-matching)
  holds.
- **Acceptance:**
  - No secret material escapes into the event stream, terminal, HTML, JUnit, or reproducibility envelope
    under the adversarial scenarios; security reviewer signs off.
  - New `ResolvedSecretLedger` per-scenario scrub added to eliminate diagnostic leakage at observation-text level.
  - Defence-in-depth: `SecretString` type + terminal/HTML/JUnit redaction + envelope reference-only hashing all verified independently.

#### S11-B-02 · Environment serialisation hardening (pilot-blocker fix)
- **Owner:** CR1 · **Estimate:** 0.5d · **Depends on:** S10 deliverables · **Spec:** Runtime stability
- **Status:** ✅ Delivered · **Commit:** `7846316`
- Fix a pre-existing crash in shared-environment equality and watch-mode topology-reuse checks when
  dependency extras (`schemaRegistry: true`, etc.) are present. `System.Text.Json` cannot serialize
  `YamlDotNet.RepresentationModel.YamlNode`, causing "Cannot read the Value of an empty anchor" on every
  `vouchfx run` and watch-mode invocation with Avro Kafka scenarios.
- **Acceptance:**
  - New `YamlNodeJsonConverter` (write-only) maps scalar → string, mapping → deterministic object,
    sequence → array; complex YAML keys + empty-anchor scalars handled without `InvalidCastException`.
  - `ScenarioRunner.SerialiseEnvironment` uses cached converter; extra-free envs serialize identically to before.
  - 7 non-docker unit tests: crash-fix, key-order invariance, value fidelity, real `schemaRegistry: true` YAML,
    extra-free stability, null, complex-key scenarios all pass.

### Workstream A — Orchestration

#### S11-A-01 · Image-pull / environment-error robustness pass
- **Owner:** OR · **Estimate:** 1d · **Depends on:** S02-A-02 · **Spec:** MVP §10 (90s-startup / image-pull risk)
- **Status:** ✅ Delivered · **Commit:** `08c798f`
- Confirm image-pull failures classify as Environment errors naming registry host + auth status, and the
  CI pre-warm step keeps cold-runner startup honest against the 90-second diagnostic.
- **Acceptance:**
  - An induced pull failure is an Environment error with diagnostics; pre-warm path validated.
  - Registry-unreachable errors → ImagePull classification with host + auth status details.
  - Rate-limit scenarios → distinct `rate-limited` authStatus indicator.

### Workstream D — Milestone

#### S11-D-04 · M4 phase-exit review package
- **Owner:** TL · **Estimate:** 0.5d · **Depends on:** all S11 tasks · **Spec:** MVP §5.5, §7.1, §8.4
- **Status:** ✅ Delivered · **Package:** `plan/m4-phase-exit.md`
- Assemble the M4 evidence: reference scenario green both ways, memory safety continuous, SDK dry-run
  passed, CI template publishing reports, signed/packaged release ready.
- **Acceptance:**
  - M4 exit-criteria checklist (18 criteria) with evidence traces and commit SHAs.
  - Reproducible demo script with 9 executable steps covering build, unit tests, memory gate, tool packaging, reference scenario from editor/CLI, redaction integrity, CLI exit-code gating.
  - Open gate items enumerated: certificate provisioning, GitLab live-pipeline run (#153), steering review.
  - Sign-off block for human gatekeepers.

## Exit criteria — Milestone M4 (MVP §8.4)

The reference scenario is green from the editor and the CLI; the extension is feature-complete for the
MVP; memory safety is continuously verified; an outside contributor has implemented a non-Core provider
against the SDK without platform-team help; a CI template runs the suite and publishes its report; and
the signed, packaged release with SBOM and provenance is ready for pilot.

## Risks mitigated this sprint (MVP §10)

- Secret values leak despite redaction (adversarial penetration test).
- Licence/compliance or supply-chain gaps at launch (signing + SBOM + provenance done before pilot).
- Cold-runner startup flakiness (image-pull robustness + pre-warm validated).
