# Sprint 09 — Tooling & hardening: editor and report surface

| | |
|---|---|
| **Phase** | 4 — Tooling and hardening (MVP §8.4) |
| **Weeks** | 17–18 |
| **Length** | 2 weeks |
| **Milestone** | Contributes to **M4** (closes Sprint 11) |
| **Theme** | Turn a working engine into something a stranger can use: the VSCode authoring experience, a CI-grade CLI, and the durable report artifacts (HTML + JUnit XML) the pilot and CI consume. |

## Sprint goal

The VSCode extension provides schema-driven YAML autocomplete/validation and embedded C# IntelliSense in
`script.csharp` steps; the CLI returns deterministic, taxonomy-aware exit codes; and the standalone HTML
report (with the reproducibility envelope) and JUnit XML output are produced from the one event stream.

## Entry assumptions

- M3 cleared: six providers, frozen schema/contract, SDK published, runner + reporting features.

## Tasks

### Workstream C — Authoring tooling

#### S09-C-01 · VSCode extension — schema-driven YAML autocomplete & validation
- **Owner:** TX · **Estimate:** 2.5d · **Depends on:** S08-F-01 · **Spec:** DSL §10; MVP §8.4 (VSCode extension), §3.1
- Bind the frozen v1 JSON Schema to `.e2e.yaml` files for autocomplete, hover, and inline validation.
- **Acceptance:**
  - Authoring a step offers schema-accurate completion and flags invalid fields against the v1 schema.

#### S09-C-02 · VSCode extension — embedded C# IntelliSense in `script.csharp`
- **Owner:** TX · **Estimate:** 3d · **Depends on:** S09-C-01 · **Spec:** DSL §10; MVP §8.4, §10 (embedded-IntelliSense risk)
- Provide embedded C# IntelliSense in `script.csharp` blocks via the .NET language server. **Risk gate:**
  if this proves harder than estimated, ship schema-only YAML for v1 and fast-follow the C# intelligence
  (MVP §10 mitigation) — decided with TL by sprint mid-point.
- **Acceptance:**
  - C# completion/diagnostics work inside a `script.csharp` block against `Vars`; **or** the documented
    fallback is invoked and recorded.

#### S09-C-03 · CLI — deterministic, taxonomy-aware exit codes
- **Owner:** TX · **Estimate:** 1.5d · **Depends on:** S07-C-01 · **Spec:** BP §12.1, §16; MVP §8.4 (CLI runner), §4.2 (CI fitness)
- Finalise CI-friendly output and deterministic exit codes that distinguish the four verdicts; **only
  `Fail` breaks the build by default** — Environment error and Inconclusive use distinct non-Fail codes.
- **Acceptance:**
  - A genuine failure returns the Fail exit code; an environment error and an inconclusive return their
    own codes and do **not** fail CI by default.

### Workstream G — Result reporting & diagnostics

#### S09-G-01 · Standalone HTML report with the reproducibility envelope
- **Owner:** PC · **Estimate:** 2.5d · **Depends on:** S08-G-01, S08-G-02 · **Spec:** BP §14, §17; MVP §8.4 (HTML report), §3.1
- Render a self-contained HTML report to disk from the event stream, embedding the reproducibility
  envelope (hashing secret references, never values), the polling timeline, and the captured-variable
  thread.
- **Acceptance:**
  - The HTML report opens standalone, shows all four verdict categories distinctly, and contains no
    secret values.

#### S09-G-02 · JUnit XML output for CI gates
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S02-G-01 · **Spec:** BP §14; MVP §8.4 (JUnit XML), §3.1
- Emit JUnit XML from the same stream so CI systems can gate and display results, mapping the taxonomy
  faithfully (Environment error / Inconclusive not silently collapsed into failure).
- **Acceptance:**
  - JUnit XML validates against common CI consumers; the four verdicts survive the mapping.

### Workstream D — Integration

#### S09-D-01 · Renderer parity check across surfaces
- **Owner:** TL · **Estimate:** 1d · **Depends on:** S09-G-01, S09-G-02 · **Spec:** BP §14 (one stream, many renderers)
- Verify terminal, HTML, and JUnit renderers are all driven by the single event stream and agree on
  verdicts (no per-audience pipeline divergence).
- **Acceptance:**
  - The same suite yields consistent verdicts across all three renderers from one stream.

## Exit criteria (sprint demo)

- Authoring a `.e2e.yaml` in VSCode shows schema completion and (or fallback) C# IntelliSense; the CLI
  runs it with taxonomy-aware exit codes; the run produces an HTML report and JUnit XML from the one
  stream.

## Risks mitigated this sprint (MVP §10)

- Embedded C# IntelliSense harder than estimated (explicit risk gate + documented fallback).
- Reporting under-polished (HTML + JUnit feature work begins early in Phase 4, not at the end).
