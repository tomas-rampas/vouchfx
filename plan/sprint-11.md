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
- The four-technology reference scenario (REST + Kafka + DB + webhook, with seed + secret + RETRY +
  capture) runs green from VSCode and from the CLI, reproducibly from a clean checkout.
- **Acceptance:**
  - The reference scenario passes from both surfaces on the standardised hardware; the memory gate is green.

#### S11-D-02 · Release manifest — signing, SBOM, provenance
- **Owner:** TL · **Estimate:** 1.5d · **Depends on:** S01-D-02 · **Spec:** MVP §9.1, §10 (licence/compliance, supply-chain)
- Sign all binaries (Authenticode / Apple Developer ID / GPG), generate SBOMs and provenance
  attestations, and verify the supply-chain story end-to-end.
- **Acceptance:**
  - Binaries verify as signed; SBOM + provenance attach to the release; verification steps documented.

#### S11-D-03 · Packaging for all three operating systems
- **Owner:** TL · **Estimate:** 1.5d · **Depends on:** S11-D-02 · **Spec:** MVP §9.1 (distribution channels)
- Produce the `dotnet tool` package + the secondary installers: signed `.msi` (Windows), notarised
  `.pkg` (macOS), `.deb` (Debian-family), plus the VSCode Marketplace package.
- **Acceptance:**
  - `dotnet tool install -g` works; each OS installer installs and runs a smoke test; the extension
    packages for the Marketplace.

### Workstream B — Compiler & runtime

#### S11-B-01 · Security review — secret-redaction penetration test
- **Owner:** CR1 · **Estimate:** 1.5d · **Depends on:** S08-B-01, S05-G-01 · **Spec:** BP §17.1.1; MVP §8.4, §10 (secret-leak risk), §5.5 (security sign-off)
- Penetrate the redaction path on realistic OAuth and AWS-signing scenarios where base64, HMAC-signed, or
  partially-logged forms could escape redaction; confirm the typed `SecretString` (not string-matching)
  holds.
- **Acceptance:**
  - No secret material escapes into the event stream, terminal, HTML, JUnit, or reproducibility envelope
    under the adversarial scenarios; security reviewer signs off.

### Workstream A — Orchestration

#### S11-A-01 · Image-pull / environment-error robustness pass
- **Owner:** OR · **Estimate:** 1d · **Depends on:** S02-A-02 · **Spec:** MVP §10 (90s-startup / image-pull risk)
- Confirm image-pull failures classify as Environment errors naming registry host + auth status, and the
  CI pre-warm step keeps cold-runner startup honest against the 90-second diagnostic.
- **Acceptance:**
  - An induced pull failure is an Environment error with diagnostics; pre-warm path validated.

### Workstream D — Milestone

#### S11-D-04 · M4 phase-exit review package
- **Owner:** TL · **Estimate:** 0.5d · **Depends on:** all S11 tasks · **Spec:** MVP §5.5, §7.1, §8.4
- Assemble the M4 evidence: reference scenario green both ways, memory safety continuous, SDK dry-run
  passed, CI template publishing reports, signed/packaged release ready.
- **Acceptance:**
  - Reproducible demo recorded; steering review held; pilot-readiness confirmed.

## Exit criteria — Milestone M4 (MVP §8.4)

The reference scenario is green from the editor and the CLI; the extension is feature-complete for the
MVP; memory safety is continuously verified; an outside contributor has implemented a non-Core provider
against the SDK without platform-team help; a CI template runs the suite and publishes its report; and
the signed, packaged release with SBOM and provenance is ready for pilot.

## Risks mitigated this sprint (MVP §10)

- Secret values leak despite redaction (adversarial penetration test).
- Licence/compliance or supply-chain gaps at launch (signing + SBOM + provenance done before pilot).
- Cold-runner startup flakiness (image-pull robustness + pre-warm validated).
