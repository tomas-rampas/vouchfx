# vouchfxai — AI Companion Feasibility and Design

**Status:** Design document for phase-one delivery — MCP server development in progress.
**Date:** 2026-07-20

*This document assesses the feasibility of, and fixes the initial architecture for, **vouchfxai**: a companion product that lets an AI assistant author, run, diagnose and eventually plan vouchfx test suites on a customer's behalf. It concludes that the companion is not only feasible but already half-promised by the existing architecture, and that the correct first delivery vehicle is a Model Context Protocol (MCP) server exposing deterministic, engine-backed tools — with a hosted service wrapping the same tool surface as a later phase.*

# 1. Purpose and Scope of this Document

## 1.1 What this document decides

This document fixes four things: the phase-one delivery vehicle (an MCP server that ships no model calls of its own), the capability order (Generator, then Healer, then Planner), the coupling surface between vouchfxai and the engine (the frozen v1 contracts and the public `Vouchfx.Sdk` package, never engine internals), and the repository strategy (two repositories — a public one for the MCP server, a private one for the hosted service). Section 12 records these decisions in compact form.

## 1.2 What this document defers

The detailed design of the hosted (SaaS) phase — tenancy, account model, consumption accounting — is deliberately deferred to a later document. Section 8 records only the architectural constraint that matters now: the hosted service must wrap the same tool surface the MCP server exposes, so that nothing built in phase one is thrown away. Likewise, the Planner capability is sketched at the level of tool names and inputs, not specified.

# 2. Motivation and Fit with the Existing Architecture

## 2.1 The blueprint already promises this

Section 8 of the [Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md) defines a three-agent topology — **Planner** (requirements to structured test plan), **Generator** (plan to YAML DSL, "replacing manual authoring") and **Healer** (drift-induced failure to repaired suite) — as a post-MVP layer, with graph-based workflows and a human-in-the-loop approval gate before any machine-proposed change reaches source control. Blueprint section 8.3 goes further and names the "authoring inversion" as a deliberate strategic option: the provider contract and the event stream were designed to be machine-writable as well as machine-readable precisely so that an agent could one day draft tests and a human could review them.

vouchfxai is the concrete form of that promise. Nothing in this document invents a new direction; it chooses a delivery vehicle and an ordering for a direction the architecture has carried from the beginning.

## 2.2 The roadmap boundary already places it

The [roadmap's](roadmap.md) permanent feature-boundary table lists "agentic test planning/generation/healing" in the commercial (future) row, "layered *above* the open-source engine — never carved out of it". vouchfxai is that layer. Nothing in the Apache-2.0 permanent row — the engine, the DSL, the twenty-five Core providers, the Provider SDK, the CLI runner, the structured event stream, the renderers — moves, changes licence, or grows a vouchfxai dependency. The companion consumes the engine exactly as any third party would; the coupling runs strictly upward.

## 2.3 Why the engine is unusually machine-legible

Most test frameworks make poor targets for machine authoring because their authoring surface is a general-purpose programming language and their output is unstructured text. vouchfx is the opposite on both counts, and the four contracts below — all frozen for the v1.x engine series and enforced by golden-file CI gates — are the reason an external AI layer is feasible at all:

| Contract | What it gives an AI layer | Where it is defined |
|---|---|---|
| The composed v1 JSON Schema (`x-vouchfx-schema-version: v1`, draft 2020-12) | A complete, versioned grammar of everything a suite may contain. A generated document either validates or it does not; there is no ambiguity for a model to exploit. | [DSL specification](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md) section 8 |
| The v1 JSON Lines event stream | A schema-versioned, machine-readable record of exactly what every step attempted and observed, including per-attempt records for RETRY polling and the reproducibility envelope. | Blueprint section 14.4 |
| The four-way verdict taxonomy | Pass, Fail, Environment error and Inconclusive are kept separate everywhere, so a diagnostic agent can distinguish "the product is broken" from "the infrastructure is broken" mechanically rather than heuristically. | Blueprint section 12.1 |
| The provider registry | Machine-readable metadata for every registered step type in dotted `family.provider` form, with strongly-typed models — the vocabulary a generator is allowed to use. | Blueprint section 13 |

The blueprint's section 14.8 even reserves the interaction pattern: a `healer-suggestion` event posted back into the stream, rendered inline and clearly marked as machine-generated and reviewable.

# 3. Architecture: an MCP Server First

## 3.1 The inversion — the model lives in the client

Blueprint section 8 assumed the agentic layer would host its own agents. Phase one of vouchfxai inverts that: the large language model lives in the *client* — Claude Code, an MCP-capable IDE, or any other MCP host the customer already uses — and vouchfxai ships **zero model calls**. The server exposes only deterministic, engine-backed tools: schema retrieval, validation, scaffolding, execution, and event-stream analysis.

This inversion buys four things at once. There are no model API keys, no per-token costs and no model-version churn inside vouchfxai. Every tool is deterministic and therefore unit-testable like any other engine component. The customer chooses their own assistant and their own model governance. And the human-in-the-loop gate of blueprint section 8.2 is preserved structurally rather than by policy: an MCP server cannot apply an edit — the client applies edits, and the human reviews them in their own editor.

## 3.2 Process shape

The phase-one server is a .NET console process speaking MCP over stdio, built on the official `ModelContextProtocol` C# SDK. It takes two dependencies on vouchfx, both public:

- **In-process, via the `Vouchfx.Sdk` and engine packages** for everything that does not start containers: schema composition, document validation, the full authoring pipeline (parse, bind, validate), and the provider catalogue.
- **Out-of-process, via the `vouchfx` dotnet global tool** for everything that does start containers. The server invokes the CLI exactly the way the VSCode Test Explorer already does — `vouchfx run <file> --events <tmpEventsFile> --no-decorations` — and reads the resulting JSON Lines stream. This is not a shortcut: running a topology requires an Aspire host executable carrying the `Aspire.AppHost.Sdk` and its DCP metadata (blueprint section 4), so an arbitrary host process *cannot* run suites in-process. The CLI is the supported execution boundary, and reusing the Test Explorer's machine contract keeps vouchfxai on an interface that is already exercised daily.

## 3.3 Trust boundary

```text
┌─────────────────────┐   MCP (stdio)   ┌──────────────────────┐   frozen v1 contracts   ┌────────────────┐
│  MCP client + LLM   │ ◄─────────────► │  vouchfxai server    │ ◄─────────────────────► │  vouchfx       │
│  (Claude Code, IDE) │   tool calls    │  deterministic tools │   schema · events ·     │  engine + CLI  │
│  human reviews all  │                 │  no model calls      │   verdicts · registry   │  containers    │
│  proposed edits     │                 │  no secret values    │                         │                │
└─────────────────────┘                 └──────────────────────┘                         └────────────────┘
```

The model sees YAML, schemas, diagnostics and event streams. It never sees secret values (section 7.1), and nothing it produces reaches disk or source control without a human applying it.

# 4. The MCP Tool Surface

Each tool below names its inputs, outputs and the engine surface that backs it. Tools 4.1–4.5 constitute the Generator phase, 4.6–4.7 the Healer phase, and 4.8 sketches the Planner phase.

## 4.1 `vouchfx_schema`

Returns the composed v1 JSON Schema — the root document grammar merged with every registered provider's fragment, exactly as the engine composes it at startup (`SchemaComposer` in the compilation layer). The client model uses it as ground truth for what a valid suite looks like. Input: none (optionally a provider filter). Output: the schema document plus its `x-vouchfx-schema-version` marker.

## 4.2 `vouchfx_validate`

Takes `.e2e.yaml` content and returns structured diagnostics: schema violations from the engine's schema validator, then — when the document is schema-valid — the deeper semantic diagnostics the authoring pipeline produces (parse, AST construction, binding against typed provider models). This is the tool that turns a language model's plausible-but-wrong output into a corrected document *before* a human ever sees it: the client generates, validates, repairs and only then presents.

The engine ships a standalone `vouchfx validate [<path>] [--json]` CLI subcommand (section 9.1, delivered) that exposes the compile-phase validation pipeline without starting a topology. The MCP server can either shell out to this subcommand (loosening its version coupling to the engine) or call the validation components in-process; both are supported.

## 4.3 `vouchfx_step_catalog`

Returns the provider catalogue from the step-kind registry: every registered step type in dotted `family.provider` form, its field shapes, capture support and family intent. This is the generator's vocabulary — with the catalogue in context, the client model cannot invent step types that do not exist, and it learns which of the twenty-five Core providers (or any additionally registered provider) fits an intent. Input: none, or a family filter. Output: machine-readable catalogue, the same metadata that feeds the generated language reference.

## 4.4 `vouchfx_scaffold`

Deterministically produces a structurally valid suite skeleton from a machine-readable description of the system under test — in the first instance an OpenAPI document, later Kafka topic descriptors or a docker-compose file. The skeleton contains `metadata`, an `environment` block with the described services and dependencies, and typed step stubs; the client model fills in the *semantic* content (payloads, assertions, captures) and `vouchfx_validate` checks the result. The division of labour is deliberate: the deterministic tool guarantees structure, the model contributes meaning, and neither trusts the other.

## 4.5 `vouchfx_run`

Executes a suite by invoking the `vouchfx` CLI as described in section 3.2 and returns a verdict summary plus the parsed event stream. Pass-through inputs mirror the CLI's selection and execution flags (`--tag`, `--parallel`, and so on). The tool reports the four verdicts distinctly and never collapses them — an Environment error result is returned as such, not as a failure.

## 4.6 `vouchfx_explain_run`

Parses a JSON Lines event stream (from `vouchfx_run` or from a file a CI system produced with `--events`) and returns a structured explanation: the scenario and step timeline, each step's attempt history (the RETRY polling timeline is reconstructable because every `step-attempt` is recorded individually), captured variables, and the reproducibility envelope. Correlation is by `runId` and `stepId`, matching the event-wire contract — step events deliberately do not carry a scenario identifier, and the tool respects the same aggregation model the renderers use.

## 4.7 `vouchfx_diagnose`

The Healer's core. Takes an event stream and the suite source, and returns *proposals*, strictly separated by verdict:

- **Fail** — candidate explanations grounded in the expected-versus-observed diff carried by the failure event, and, where drift is the likely cause, a proposed patch to the YAML (or embedded CSX) presented as a structured diff.
- **Environment error** — infrastructure advice only: image pull, container health, seed script, tunnel. The tool will never propose a YAML edit for an Environment error, because per blueprint section 12.1 conflating an environment problem with a product defect destroys trust in the tool — and a machine-authored "fix" for an infrastructure problem is the fastest way to do exactly that.
- **Inconclusive** — guidance on timeouts, grace periods and unmet upstream captures.

Proposals are never applied by the tool. The client presents them, the human accepts or rejects — the section 8.2 approval gate in MCP form. Optionally the tool can emit its proposals as `healer-suggestion` events appended to the stream, in the machine-generated, reviewable form blueprint section 14.8 reserves, so the existing renderers can display them inline.

## 4.8 Planner-phase tools (sketch only)

Two tools are anticipated but not specified here: a topology-inspection tool (given a repository or compose file, describe the services, dependencies and seams worth testing) and a coverage-gap tool (given the existing suites and the event streams of recent runs, identify untested seams and propose scenario outlines). Both compose the Generator tools rather than bypassing them: a Planner proposal becomes real only by being scaffolded, filled, validated and run through the same pipeline.

# 5. Phasing: Generator, then Healer, then Planner

Blueprint section 8.1 lists the agents in pipeline order — Planner feeds Generator feeds Healer. Delivery order is different, and deliberately so: authoring is the adoption bottleneck (a customer's first hour with vouchfx is spent writing YAML, not diagnosing drift), and the Generator tools are also the foundation the other two phases compose. The Healer comes second because it needs only the event stream and the validation pipeline, both of which exist. The Planner comes last because it has the widest and least-bounded inputs.

| Phase | Tools | Capability delivered | Done means |
|---|---|---|---|
| 1 — Generator | 4.1 `vouchfx_schema`, 4.2 `vouchfx_validate`, 4.3 `vouchfx_step_catalog`, 4.4 `vouchfx_scaffold`, 4.5 `vouchfx_run` | An MCP client can take a service description and produce a validated, runnable suite with a human reviewing every file. | A suite for a reference service is generated, validated and passes, end to end, from a single client conversation. |
| 2 — Healer | 4.6 `vouchfx_explain_run`, 4.7 `vouchfx_diagnose` | The same client can explain any run and propose verdict-aware repairs, never auto-applied. | A deliberately drifted reference suite is diagnosed and the proposed patch, once human-approved, makes it pass. |
| 3 — Planner | 4.8 topology inspection, coverage-gap analysis | The client can propose *what* to test, not just write what was asked. | Scoped when phase 2 ships. |

Milestones here are capability-level by design; delivery scheduling lives outside the published documentation.

# 6. Relationship to the Microsoft Agent Framework

Blueprint section 8 builds the agentic layer on the Microsoft Agent Framework (MAF) and, in the same breath, commits to validating MAF's then-current capability set before detailed design. MCP-first is that caution made concrete, not a contradiction of it.

The reconciliation is a layering: MCP tools are the *deterministic tool layer*; MAF is the *orchestration layer* that phase two adds above it. A MAF agent graph — Planner node, Generator node, Healer node, human-approval gate — invokes tools to act on the world, and MAF supports MCP tool invocation directly. Everything phase one builds is therefore the tool substrate the eventual closed-loop, hosted agent system calls; nothing is thrown away, and the MAF adoption decision is deferred to the moment it is actually needed, with a working tool surface to evaluate it against.

# 7. Security and Trust Boundaries

## 7.1 Secrets never cross the AI boundary

The engine's secrets invariant (blueprint section 17) extends across the AI boundary unchanged. Suites contain only `${secret:source/path}` references, so the model reads and writes references, never values. The Generator is *incapable* of embedding a secret value in a suite it did not receive one for, and the redaction-at-source design (`SecretString` with no value-returning conversions) means the event stream the Healer reads never contains a value either — the reproducibility envelope hashes references, not values. The vouchfxai server adds one obligation of its own: no tool may resolve a secret reference; resolution remains exclusively a step-execution-time engine concern.

## 7.2 Every mutation is a proposal

No tool writes to the customer's files or repository. Generated suites, scaffolds and diagnostic patches are returned to the client as content; the human applies them through their own editor and their own version control. This preserves blueprint section 8.2's human-in-the-loop gate without needing a gate at all — the architecture has no path around the human.

## 7.3 Machine-generated provenance

Anything vouchfxai contributes to a shared artefact is marked as machine-generated: `healer-suggestion` events carry that marking per blueprint section 14.8, and generated suites carry a provenance comment. A reviewer must always be able to tell drafted-by-agent from written-by-human.

## 7.4 The event stream is data, not instructions

Event streams contain content that originated in the system under test — response bodies, error messages, captured values — which from the model's perspective is untrusted input and a prompt-injection surface. The client-side guidance that ships with vouchfxai treats stream content strictly as data to be analysed, never as instructions to be followed, and the deterministic tools themselves never interpret stream content at all.

# 8. Phase Two: the Hosted Service

The hosted form of vouchfxai wraps the identical tool surface behind an authenticated API, so a hosted agent (or a customer's own client, pointed at the service) uses the same schema, validate, scaffold, run and diagnose operations. Two existing pieces of the architecture do most of the work:

- **Execution** lands on the cloud execution fabric of blueprint section 6 — remote container provisioning over the tunnelled Docker control channel, with Ryuk-based cleanup — in the planned Team tier of blueprint section 7.2. Topological parity (the architecture's first design principle) is what makes this phase cheap: a suite generated in a local MCP conversation runs unchanged on the hosted fabric, byte for byte.
- **Identity and authorisation** reuse the tier model of blueprint section 7 — platform accounts for the Team tier, enterprise IdP federation where the Enterprise tier's constraints apply.

Tenancy, account model, consumption accounting and the operational design of hosted model access are acknowledged as required and deliberately unspecified here; they belong to the phase-two document. What is fixed now is only the constraint that matters to phase one: the hosted service is a wrapper around the same tools, never a fork of them — concretely, it lives in the `vouchfx-cloud` repository and consumes the `vouchfx-mcp` tool layer as a package dependency (section 10).

# 9. What Changes in vouchfx Itself

vouchfxai requires no change to vouchfx. Four small additions would nonetheless sharpen the boundary, and each is worth shipping on its own merits. All are additive: the v1 schema, the v1 event-wire contract and the v1 provider interfaces are untouched, and the existing golden-file freeze gates (`SdkContractFreezeTests`, `EventContractFreezeTests`) remain the proof.

## 9.1 A `validate` CLI subcommand

**Delivered.** `vouchfx validate [<path>] [--json]` exposes the full compile-phase validation pipeline (schema, parse, AST, provider binding, Roslyn compilation) without starting a topology or containers. Structured diagnostics include validation stage (schema/parse/pipeline/roslyn) and detailed messages. The exit code is 0 (all valid), 2 (usage error), or 4 (one or more scenarios invalid). The frozen JSON wire contract is pinned by golden-file tests (`ValidateJsonDocument`, `ValidateJsonScenario`, `ValidateJsonDiagnostic`). CI users have a fast pre-flight gate without Docker; vouchfxai can shell out to this subcommand to decouple from the engine's package version.

## 9.2 A `schema` export command

`vouchfx schema` — emit the composed v1 schema as an artefact, so any consumer (vouchfxai, editor tooling, documentation pipelines) obtains it from the installed engine version rather than embedding a copy.

## 9.3 A machine-readable provider catalogue

**Partially delivered.** `vouchfx list --step-types --json` emits the sealed Core step-type catalogue — every registered dotted `family.provider` key, sorted by type, with separate family and provider fields. The frozen JSON wire contract is pinned by golden-file tests (`ListJsonDocument`, `ListJsonStepType`). The richer metadata envisioned in section 4.3 — field shapes, capture support, and family intent — remains future work, but the command is consumable by editor tooling and can be integrated into the vouchfxai generator's vocabulary lookup.

## 9.4 Formalising the `healer-suggestion` event

Blueprint section 14.8 reserves the concept; phase two of vouchfxai is the moment to specify the record. Renderers tolerate unknown fields by contract, so introducing a new event kind is additive and breaks no consumer of the v1 stream.

# 10. Repository Strategy

**Decision: vouchfxai spans two new repositories — `vouchfx-mcp` (public, Apache-2.0) and `vouchfx-cloud` (private).**

The roadmap's boundary language — layered above, never carved out — is an instruction about repository shape as much as licensing, and the two-repository split places the phase boundary of section 5 and the OSS/commercial boundary of section 2.2 on the same seam:

- **`vouchfx-mcp`** hosts the phase-one MCP server: the deterministic tool layer of section 4, coupled only to the frozen v1 contracts and the public `Vouchfx.Sdk` package on NuGet. It is public and Apache-2.0, consistent with the rest of the ecosystem, and deliberately so: every MCP-capable client that adopts the tools becomes an on-ramp to the engine, and an open tool layer invites the same community scrutiny the engine enjoys. From its first release the repository ships the tool layer as a versioned package as well as a server executable, because the hosted service consumes it **as a package, never as copied source** — the same discipline that governs every other boundary in the ecosystem.
- **`vouchfx-cloud`** hosts the phase-two service of section 8: the hosted API wrapper, tenancy and identity integration, consumption metering and billing, the MAF agent orchestration of section 6, and any dashboard surface. It is private: it is commercial code, and the project's publication boundary keeps billing and pricing material off every public surface.

The split resolves rather than defers the licensing question a single `vouchfxai` repository would have carried: the public repository never contains commercial code, so nothing in it ever needs to change licence, and the private repository never needs a public licence at all. Each repository also gains an honest release cadence — the tool layer iterates with the MCP ecosystem, the hosted service with its own operational rhythm — while both stay decoupled from the engine, which pins Aspire per release and moves deliberately. The ecosystem has already proven the consumption model: the custom-runner sample built on the public engine API demonstrates that a third-party executable can drive the engine without private access, and both new repositories are architecturally the same kind of consumer. The acknowledged cost of the split is ceremony: the tool layer needs package versioning and a compatibility statement from day one, because `vouchfx-cloud` depends on it across a repository boundary.

The rejected alternative — a project folder inside this repository — is worth one honest paragraph. It would simplify day-one development (one solution, one CI), but it couples the companion's release cadence to the engine's, entangles the publication boundary of this repository's documentation site with a commercial product's documentation, and visually contradicts the "never carved out" promise the roadmap makes: the clearest way to show the OSS engine does not depend on the AI layer is for the engine's repository not to contain it.

# 11. Feasibility Assessment and Risks

The overall assessment is that vouchfxai is feasible with current, shipped building blocks: every phase-one tool is backed by an engine surface that exists today, and the only genuinely new code is the MCP hosting shell and the scaffold/diagnose logic. The material risks:

| Risk | Assessment | Mitigation |
|---|---|---|
| MCP C# SDK maturity | The official `ModelContextProtocol` SDK is young and evolving. | The tool layer is a thin shell over engine calls; the MCP-facing surface is small and replaceable. Pin the SDK per vouchfxai release, mirroring how the engine pins Aspire. |
| CLI-shelling overhead for runs | Each `vouchfx_run` pays CLI start-up plus topology start-up. | Accept it: the Aspire host requirement makes in-process execution from an arbitrary host non-viable by design, and topology start-up dominates the cost anyway. The Test Explorer contract proves the pattern in daily use. |
| Engine version coupling | In-process use of engine packages ties a vouchfxai release to an engine minor. | The frozen v1 contracts make this a compile-time, not behavioural, coupling; section 9.1's `validate` subcommand offers a route to full process-level decoupling if it bites. |
| MAF maturity (phase two) | Blueprint section 8 already flags MAF as still maturing. | Unchanged posture: defer the MAF commitment until phase two, and evaluate it against the then-existing tool surface. |
| Event-stream size on long runs | Large suites produce large JSON Lines files for `vouchfx_explain_run` to summarise into a bounded context. | The stream is line-delimited and filterable by `runId`/`stepId`; the tool summarises server-side and returns structure, never the raw stream. |
| Model-quality dependence | Generated suite quality depends on the client's model, which vouchfxai does not control. | Deliberate: validation and the human gate bound the damage a weak model can do to "unhelpful", never "unsafe". The deterministic tools are the floor. |

# 12. Decision Log

| # | Decision | Rationale in brief |
|---|---|---|
| 1 | vouchfxai is delivered first as an MCP server. | Cheapest path to value; the customer's own assistant supplies the model; the SaaS phase wraps, not replaces, it. |
| 2 | Phase one ships zero model calls. | Determinism, testability, no key management, and a structurally enforced human-in-the-loop. |
| 3 | Capability order is Generator, then Healer, then Planner. | Authoring is the adoption bottleneck; each later phase composes the earlier tools. |
| 4 | Coupling to the engine is only via frozen v1 contracts, the public SDK package and the CLI. | Keeps the OSS/commercial boundary honest and survives engine releases. |
| 5 | Diagnosis is verdict-aware and never proposes YAML edits for Environment errors. | Blueprint section 12.1: conflating environment problems with defects destroys trust. |
| 6 | vouchfxai spans two repositories: `vouchfx-mcp` (public, Apache-2.0) and `vouchfx-cloud` (private). | The phase boundary and the OSS/commercial boundary land on the same seam; the public repository never contains commercial code, and the engine repository visibly depends on neither. |
| 7 | This document is published on the documentation site. | The agentic direction is already public in blueprint section 8 and the roadmap boundary table; this document stays at capability level. |
