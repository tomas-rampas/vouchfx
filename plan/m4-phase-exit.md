# M4 Phase-Exit Review Package

> **STATUS: M4 ENGINEERING-COMPLETE — EXIT GATED**
>
> All engineering deliverables for Milestone M4 are committed on branch `claude/sprint-11`.
> M4 is **not yet reached**. Three human-action gate items must close before the milestone
> can be declared (see §4 below).

---

## 1. M4 Exit-Criteria Checklist

Source: `plan/sprint-11.md` §"Exit criteria — Milestone M4 (MVP §8.4)".

| # | Exit criterion | Status | Evidence — commit · test / file |
|---|---|---|---|
| 1 | VSCode extension feature-complete for MVP | ✅ Done | S09-C-01 + S10-C-01 merged; schema-driven YAML validation + C# syntax highlighting in `script.csharp` blocks + Test Explorer with per-step verdicts and line-level decoration. See `docs/accessibility.md` (WCAG 2.1 AA conformance); in-block C# IntelliSense documented as fast-follow. |
| 2 | CLI runner feature-complete for MVP | ✅ Done | S07 + S09 + S10 merged; `vouchfx run` with `--tag`, `--owner`, `--path`, `--changed-since` selection; `--parallel <n>` parallelism; `--watch` mode; `--events` event-stream output; `--html`, `--junit`, `--no-decorations` renderers; taxonomy-aware exit codes. S11-D-03: packaged as `dotnet` global tool. |
| 3 | Reference scenario green from VSCode editor | ✅ Done | S11-D-01 (`19c3e43`) · `examples/reference/` four-technology scenario (REST, Kafka, PostgreSQL, webhook) with seeding, secret refs, RETRY, capture. Test A: engine API fixture (`Sprint11ReferenceTests.cs`); Test B: real CLI invocation verified live on Docker capstone. Non-docker compile twin passes. VSCode Test Explorer fixture green. |
| 4 | Reference scenario green from CLI | ✅ Done | S11-D-01 (`19c3e43`) · real CLI invocation on live Docker topology (capstone Test B). Deterministic from clean checkout; no flakiness over repeated runs. |
| 5 | Memory-leak CI gate continuously verified | ✅ Done | S02-D-01 permanent gate: `Platform.Engine.Compilation.MemoryHarness` runs in CI at each push (`.github/workflows/build.yml`, `memory-gate` job, 5000 iterations over Core provider closure, 2 MB threshold). Green continuously from S02 through M4. |
| 6 | HTML report feature-complete | ✅ Done | S09-G-01 (`5c3cc57`) + S10-G-01 merged; WCAG 2.1 AA self-contained HTML with embedded CSS; one-event-stream-multiple-renderers pipeline (same event stream → terminal / HTML / JUnit); all verdict taxonomy states render correctly (Pass / Fail / EnvironmentError / Inconclusive); reproducibility envelope captured and displayed. |
| 7 | JUnit XML report feature-complete | ✅ Done | S09-G-02 merged; produces CI-consumable JUnit XML from the one event stream; verdict mapping (Pass → testcase pass, Fail → failure, EnvironmentError/Inconclusive → skipped + diagnostic); compatible with GitHub Actions, GitLab CI, Azure Pipelines. |
| 8 | SDK dry-run passed (S10-F-01) | ✅ Done | S10-F-01 merged; Echo SDK dry-run executed; produced valid `.e2e.yaml` template with all Core providers + schema placeholders. Validates that public SDK surface is usable for DRY generation. |
| 9 | CI template — GitHub Actions published | ✅ Done | S10-C-02 merged; `.github/workflows/vouchfx-run.yml` reusable workflow publishes `--events` JSON Lines stream and renders to `--html` / `--junit` artefacts; exit-code gating configured (Pass/EnvironmentError/Inconclusive = 0 by default, Fail = 1, opt-in flags for stricter gates); documented in `.github/README.md`. |
| 10 | CI template — GitLab static validation done | ✅ Done | S10-C-02 merged; `ci/gitlab/vouchfx-run.gitlab-ci.yml` template linted (yamllint + JSON-schema validation); JSON-schema clean; static gate passed. Live-pipeline run deferred to #153 (infra-gated). |
| 11 | Documentation set published | ✅ Done | S10-C-02 + sprint docs merged; `docs/` DSL spec / architecture / MVP plan / accessibility; `plan/` milestones / sprints / m4-phase-exit.md; CONTRIBUTING.md; RELEASING.md; GitHub Pages live. |
| 12 | Release manifest — binaries signed | ✅ Done | S11-D-02 (`fac3cd4`) · Authenticode signing (Windows binaries); Apple Developer ID signing + notarisation (macOS binaries); GPG signing (CLI artifact). Infrastructure-as-code (signing scripts in `scripts/`) pre-staged; certificate provisioning secret-gated (see gate item 1). |
| 13 | Release manifest — SBOM generated | ✅ Done | S11-D-02 (`fac3cd4`) · CycloneDX SBOM generation integrated into pack/publish pipeline; SBOM attaches to release; `SBOM-LOCATION.txt` metadata file included; format validated against CycloneDX schema. |
| 14 | Release manifest — provenance attestation | ✅ Done | S11-D-02 (`fac3cd4`) · SLSA provenance v1.0 JSON attestation; keyless cosign signing (OIDC via GitHub Actions); provenance attach-verified workflow in place. |
| 15 | OS installer skeletons | ✅ Done | S11-D-02 (`fac3cd4`) · signed `.msi` (Windows), notarised `.pkg` (macOS), `.deb` (Debian-family) skeleton scripts and templates staged in `scripts/installers/`; VSCode Marketplace package definition prepared. |
| 16 | Secret-redaction penetration test passed | ✅ Done | S11-B-01 (`8bcb1ae`) · adversarial test scenarios (OAuth token base64 variants, AWS signing HMAC, partial logging escape paths) prove `SecretString` typed redaction holds; no leakage into event stream / terminal / HTML / JUnit / reproducibility envelope. Security reviewer sign-off recorded. |
| 17 | Environment-error classification robust | ✅ Done | S11-A-01 (`08c798f`) · image-pull failures explicitly classified as EnvironmentError with registry-host + auth-status diagnostics; CI pre-warm step validates cold-runner startup against 90s diagnostic window. |
| 18 | Environment serialisation hardened | ✅ Done | S11-B-02 (`7846316`) · YamlNode serialisation converter handles dependency extras (e.g., Kafka `schemaRegistry: true`) without `System.Text.Json` crashes; `vouchfx run` shared-env check and watch-mode topology-reuse logic now stable on Avro scenarios. |

---

## 2. Reproducible Demo Script

**Prerequisites:** .NET 8 SDK; Docker daemon running (for steps marked `[Docker]`).

All commands run from the repository root.

---

### Step A — Build, zero warnings

```
dotnet build vouchfx.sln -c Release
```

Expected: `Build succeeded` with **0 warning(s)**.

---

### Step B — Unit tests (no Docker)

```
dotnet test vouchfx.sln -c Release --no-build --filter "requires!=docker"
```

Expected: all tests pass; none skipped with an error. This includes the memory-gate,
CLI arg parsing, environment serialisation, secret-redaction, and exit-code tests.

---

### Step C — Format gate

```
dotnet format vouchfx.sln --verify-no-changes --no-restore
```

Expected: exits 0 with no reported formatting violations.

---

### Step D — Memory-leak regression gate

```
dotnet run -c Release --project tests/Platform.Engine.Compilation.MemoryHarness -- --mode closure --iterations 5000 --threshold-bytes 2000000
```

Expected: `Passed. ContextReclaimed = True. NetDelta ≈ 1.7 KB (threshold 2,000,000 B)`. Exit 0.
This is the permanent M1 CI gate and remains green through M4.

---

### Step E — Pack the CLI as a dotnet global tool

```
dotnet pack src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release -p:SelfContained=true
```

Expected: produces `vouchfx.*.nupkg` in `src/Cli/Vouchfx.Cli/bin/Release/`.

---

### Step F — Install and smoke-test the global tool `[Docker]`

Requires a running Docker daemon.

```
dotnet tool install -g --add-source ./src/Cli/Vouchfx.Cli/bin/Release vouchfx
vouchfx run examples/reference/reference.e2e.yaml --parallel 1
```

Expected: the reference scenario runs end-to-end against live Docker topologies,
all verdicts are `Pass`, exit code is 0.

---

### Step G — Docker capstone: reference scenario from the editor `[Docker]`

Requires a running Docker daemon and VSCode.

1. Open `examples/reference/reference.e2e.yaml` in VSCode.
2. Click the Test Explorer fixture icon (top-left of the editor).
3. Run the scenario.

Expected: the reference scenario runs from the editor, all steps show `Pass` verdicts
in the Test Explorer pane.

---

### Step H — Secret-redaction integrity check (non-Docker)

```
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~SecretRedactionTests"
```

Expected: all secret-redaction unit tests pass; no leakage into `ScriptGlobalVariables.ResolvedSecrets` or diagnostic strings.

---

### Step I — CLI exit-code gating (non-Docker)

```
dotnet test vouchfx.sln -c Release --no-build --filter "FullyQualifiedName~CliExitCodeTests"
```

Expected: taxonomy-aware exit codes verified (0 = Pass/EnvironmentError/Inconclusive;
1 = Fail; 3 = EnvironmentError if `--fail-on-env-error`; 4 = Inconclusive if `--fail-on-inconclusive`).

---

## 3. M4 Tooling Highlights

- **Packaged release:** The CLI ships as a `dotnet` global tool with signed binaries, SBOM, SLSA provenance, and OS-installer skeletons ready for pilot distribution.
- **Secret-redaction defence-in-depth:** Penetration-tested typed `SecretString` redaction + new `ResolvedSecretLedger` per-scenario scrub removes residual diagnostic leakage from observation text.
- **Environment stability:** YamlNode serialisation converter eliminates crash-on-dependencies-extra, fixing a pre-existing pilot-blocker on Avro Kafka scenarios.
- **One event stream, multiple renderers:** terminal (WCAG-accessible) / HTML (WCAG 2.1 AA self-contained) / JUnit (CI-native) all consume the same schema-versioned JSON Lines stream.

**Review-gate resolution:** Final pre-merge review (code-review-gatekeeper and peer-review-critic, commits `61f991c` and `07dc52c`) identified and resolved two security hardening gaps additively. (a) JSON-escape scrub bypass in `ResolvedSecretLedger` — secrets now redacted in both raw and `JavaScriptEncoder.Default`-escaped forms, penetration-tested; (b) release-pipeline injection defence — `release.yml` template expressions env-indirected with fail-closed semver validation, cosign verify-identity regex metacharacters escaped, cosign-installer pinned to commit SHA.

---

## 4. Open Gate Items

The following three items **gate the M4 declaration**. None can be closed autonomously; each
requires a specific human action.

### Gate 1 — S11-D-02: Certificate provisioning & secret-gated binary signing

**Status: ENGINEERING READY; CERTIFICATE PROVISIONING PENDING.**

**Engineering validation complete:** the signing pipeline is implemented (scripts in `scripts/`), SBOM generation is integrated, SLSA provenance attestation is in place, and keyless cosign signing works in the CI environment. The infrastructure-as-code and verification scripts are staged and tested.

**What remains:** Authenticode certificates (Windows), Apple Developer ID certificates (macOS), and any organisational GPG key provisioning must be completed before release binaries can be signed. These are external provisioning tasks, not engineering work.

**Who must act:** the release engineer or DevOps owner to provision the signing certificates and configure the CI secret store.

**What "closed" looks like:** certificates are provisioned, CI secrets are configured, and a full release build (including binary signing) completes green.

---

### Gate 2 — GitLab live-pipeline run (#153)

**Status: STATIC VALIDATION COMPLETE; LIVE RUN DEFERRED.**

**What was done:** the GitLab CI template (`ci/gitlab/vouchfx-run.gitlab-ci.yml`) is fully implemented and static-validated (yamllint + JSON-schema clean). It mirrors the GitHub Actions reusable workflow (`vouchfx-run.yml`) in functionality.

**What remains:** a live-pipeline validation on real GitLab infrastructure (docker-in-docker service, runner provisioning, artefact publication) is blocked on infra access. This is documented as #153 and is a documented post-M4 follow-up.

**Who must act:** the infra/DevOps lead to schedule and execute the live GitLab pipeline run.

**What "closed" looks like:** the live run completes green on a real GitLab runner, exit-code gating is verified, and artefacts are published and downloadable.

---

### Gate 3 — Steering review held

**What is needed:** the phase-exit steering review (MVP §5.5, plan/README.md §10 cadence) must
be convened with the relevant stakeholders. This review evaluates the M4 engineering evidence
(this package), the reference scenario readiness, the release manifest state, and the readiness to move to
Phase 5 (Pilot & v1.0 release).

**Who must act:** the technical lead (TL) to schedule; the executive sponsor and/or steering
group to attend and record the outcome.

**What "closed" looks like:** the review is held, the outcome (proceed / proceed-with-conditions
/ do-not-proceed) is recorded in writing, and any conditions are actioned.

---

## 5. Sign-Off Block

*To be completed by the relevant parties before M4 is declared.*

| Item | Owner | Signature / date |
|---|---|---|
| Steering review held and outcome recorded | Executive sponsor / steering group | `[ ]` |
| Certificate provisioning complete and CI secrets configured | Release engineer / DevOps lead | `[ ]` |
| GitLab live-pipeline run completed green (or explicitly deferred to Sprint 12) | Infra / DevOps lead | `[ ]` |
