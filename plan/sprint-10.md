# Sprint 10 — Tooling & hardening: trust and the contribution path

| | |
|---|---|
| **Phase** | 4 — Tooling and hardening (MVP §8.4) |
| **Weeks** | 19–20 |
| **Length** | 2 weeks |
| **Milestone** | Contributes to **M4** (closes Sprint 11) |
| **Theme** | Make the tool *trustworthy* and the contribution path *real*: full verdict taxonomy across every surface, Test Explorer integration, an outside-contributor SDK dry-run, turnkey CI templates, and the documentation set the 60-minute onboarding criterion depends on. |

## Sprint goal

The four-verdict taxonomy is consistent across terminal, HTML, JUnit, and editor; failures decorate the
right YAML line in VSCode Test Explorer; an outside contributor completes an SDK dry-run; GitHub Actions
and GitLab CI templates run the suite and publish artifacts; and the documentation set is published.

## Entry assumptions

- Sprint 9 delivered: VSCode core, CLI exit codes, HTML report, JUnit XML.

## Tasks

### Workstream G — Result reporting & diagnostics

#### S10-G-01 · VSCode Test Explorer integration ✓
- **Owner:** PC · **Estimate:** 2.5d · **Depends on:** S09-C-01, S09-G-01 · **Spec:** BP §14; MVP §8.4 (Test Explorer), §3.1
- Surface scenarios/steps in Test Explorer; a failure decorates the originating YAML line.
- **Acceptance:**
  - Engineering-complete on this branch (consumer built, unit-tested); live end-to-end acceptance (reflecting per-step verdicts and decorating the correct line) is pending the companion .NET PR #148 (which delivers the CLI's `--events` and `--no-decorations` options) and a manual VSCode demo after that merge.

#### S10-G-02 · Full verdict taxonomy across all surfaces (incl. Inconclusive) ✓
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S09-D-01 · **Spec:** BP §12.1; MVP §8.4 (verdict taxonomy), §12.1 invariant
- Ensure Pass / Fail / **Environment error** / **Inconclusive** render consistently and distinctly across
  terminal, HTML, JUnit, and editor — each with shape+text, not colour alone (accessibility).
- **Acceptance:**
  - All four verdicts are visually and semantically distinct on every surface; only Fail gates CI. ✓

#### S10-G-03 · Accessibility review (WCAG 2.1 AA) ✓
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S10-G-02 · **Spec:** MVP §9.5, §10 (accessibility risk)
- Commission/conduct the Phase 4 accessibility review of the HTML report and extension: colour-blind-safe
  verdicts, keyboard navigation, semantic HTML, and the `--no-decorations` screen-reader terminal mode.
- **Acceptance:**
  - The report passes a WCAG 2.1 AA check; the `--no-decorations` mode works for screen readers. ✓

#### S10-G-04 · Opt-in telemetry flow & pilot backend ✓
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S09-G-02 · **Spec:** MVP §9.1 (telemetry row — a Phase 4 release-manifest deliverable), §9.3, §8.5.3
- Ship the privacy-first opt-in telemetry **in Phase 4** so it is present in the v1.0 build the pilot uses
  to generate measurement data: first-run notice, no collection until opted in, and the limited metric set
  (run/scenario counts, verdict counts, step family/provider counts, startup time, time-to-first-test,
  anonymous install id, versions). **Never** collects test contents, captured values, secret
  references/values, SUT addresses, or image names; honours the per-file no-telemetry flag and the
  production-run exclusion.
- **Acceptance:**
  - Nothing is collected pre-opt-in; the forbidden fields are provably never sent; data flows to the pilot
    backend with 90-day retention; disabling deletes the install id within 30 days.
  - v1 scope: the opt-in client and local JSON Lines outbox sink shipped (40 unit tests). The hosted pilot
    backend (90-day retention) is infrastructure, deferred and flagged; the per-file `metadata.telemetry`
    opt-out is deferred to v2 because adding it would change the frozen v1 schema (SchemaFreezeTests).
    v1 suppression = global consent + `--no-telemetry` + `VOUCHFX_NO_TELEMETRY`.

### Workstream C / D — CI templates

#### S10-C-01 · GitHub Actions reusable workflow ✓
- **Owner:** TX · **Estimate:** 1.5d · **Depends on:** S09-C-03, S09-G-02 · **Spec:** MVP §8.4 (CI templates), §10 (CI-pull-time, supply-chain risks)
- Ship a reusable workflow that installs the tool, pre-warms image pulls, runs the suite, gates on the
  exit code, and attaches the HTML report + JUnit XML as artifacts.
- **Acceptance:**
  - The workflow runs the reference suite green, gates correctly on Fail, and publishes both artifacts;
    docs recommend commit-SHA pinning and image-digest pinning.

#### S10-C-02 · GitLab CI template ✓
- **Owner:** TX · **Estimate:** 1d · **Depends on:** S10-C-01 · **Spec:** MVP §8.4
- The equivalent GitLab CI template with the same gating and artifact behaviour.
- **Acceptance:**
  - The GitLab template runs, gates, and publishes artifacts equivalently.

#### S10-D-01 · CI-templates repository hardening ✓
- **Owner:** TL · **Estimate:** 0.5d · **Depends on:** S10-C-01 · **Spec:** MVP §10 (CI-templates supply-chain risk)
- The templates repo ships with CODEOWNERS, signed releases, and a published security-disclosure process,
  reviewed at the same bar as Core providers.
- **Acceptance:**
  - CODEOWNERS, signing, and SECURITY disclosure are present and reviewed.

### Workstream F — Provider SDK

#### S10-F-01 · Provider SDK dry-run with an outside contributor ✓
- **Owner:** PC · **Estimate:** 2d · **Depends on:** S08-F-05 · **Spec:** MVP §8.4 (Provider SDK dry-run), §10 (community-pathway risk), §4.2 (community gate)
- Walk an outside contributor through implementing a non-Core provider end-to-end against the **v1** SDK,
  treating any friction as a documentation or contract bug to be fixed this sprint.
- **Acceptance:**
  - The contributor ships a working non-Core provider against v1 without platform-team help; friction
    items are filed and the cheap ones fixed.

### Workstream E — Documentation

#### S10-E-01 · Getting-started guide (60-minute path) ✓
- **Owner:** PD · **Estimate:** 1.5d · **Depends on:** S09-C-01 · **Spec:** MVP §9.2, §4.2 (time-to-first-test), §8.4 (documentation set)
- The single-page guided tour that takes a first-time user from install to a first passing test
  (single `http.rest` step against one dependency) unaided.
- **Acceptance:**
  - A fresh user reaches a first passing test using only the guide (validated in-team before pilot).

#### S10-E-02 · Recipes, common-patterns, and troubleshooting cookbook ✓
- **Owner:** PD · **Estimate:** 2d · **Depends on:** S10-E-01 · **Spec:** MVP §9.2, §8.4
- Recipes (seeding patterns, test doubles with WireMock/Mountebank, secret sources, CI integration), a
  common-patterns guide, and a troubleshooting cookbook from failure modes seen in development.
- **Acceptance:**
  - Each recipe is runnable; the language reference is auto-generated from the unified schema so it cannot
    drift (MVP §9.2).

### Workstream D — Integration

#### S10-D-02 · Memory-leak gate confirmed permanent across the full surface ✓
- **Owner:** TL · **Estimate:** 0.5d · **Depends on:** S02-D-01 · **Spec:** MVP §8.4 (memory-leak test in CI), §4.2
- Confirm the Phase 1 leak gate runs against the full six-provider engine as a permanent regression guard.
- **Acceptance:**
  - The leak gate exercises all six Core providers' closure and stays green; weekly run scheduled. ✓

## Exit criteria (sprint demo)

- The four verdicts render consistently everywhere; Test Explorer decorates failing YAML lines; an
  outside contributor's SDK dry-run is complete; a CI template runs the suite and publishes artifacts;
  the documentation set is published and a fresh user reaches first-pass using only the docs; and opt-in
  telemetry is in the build and verified, ready for pilot measurement.

## Risks mitigated this sprint (MVP §10)

- Community pathway opacity (SDK dry-run) · CI templates as a supply-chain surface (hardening) ·
  Accessibility issues at launch (Phase 4 review) · Onboarding > 60 min (docs set shipped pre-pilot).
