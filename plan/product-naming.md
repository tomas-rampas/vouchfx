# Product-Name Decision — Planning Artefact

> **Status:** Planning artefact — Sprint 6, task S06-E-02 (Workstream E, Pilot & feedback).
> Originated: Sprint 6, Phase 3, week 11.
>
> **Ownership notice:** This document frames the decision and provides structure. The actual
> candidate research, trademark searches, domain lookups, NuGet/GitHub availability checks,
> legal engagement, and final choice are **owned by the product/delivery lead (PD)** and must
> not be treated as completed by this document. No trademark or domain availability results
> are asserted here — only the process and the structured checklist for the team to fill in.

---

## Contents

1. [Why now — what depends on the name](#1-why-now--what-depends-on-the-name)
2. [Naming criteria and scoring rubric](#2-naming-criteria-and-scoring-rubric)
3. [Candidate shortlist](#3-candidate-shortlist)
4. [Trademark pre-screening process](#4-trademark-pre-screening-process)
5. [Decision process and timeline](#5-decision-process-and-timeline)
6. [Post-decision migration checklist](#6-post-decision-migration-checklist)

---

## 1. Why now — what depends on the name

### 1.1 The dependency tree

The chosen product name is a root input for every external-facing artefact. The diagram below
shows what blocks on it:

```
Chosen name
 ├── GitHub organisation name          ← org must be claimed before any public announcement
 │    └── Repository rename (vouchfx → <name>)
 ├── NuGet package-id family
 │    ├── <Name>.Engine
 │    ├── <Name>.Abstractions
 │    ├── <Name>.Steps.*              (one id per Core provider)
 │    ├── <Name>.Cli
 │    └── <Name>.Testing              (fixture / Provider SDK)
 ├── Documentation domain             ← docs.<name>.dev (or .io / .com)
 │    └── docs URL baked into every report, README, release note, IDE tooltip
 ├── JSON Schema \$id URIs             ← schema URI is a stable public contract once v1 freezes (M3)
 │    └── https://schemas.<name>.dev/e2e/v1/suite.json
 ├── Launch artefacts (Sprint 12)
 │    ├── NuGet.org release
 │    ├── GitHub Releases page
 │    ├── README / landing page
 │    └── Conference / blog material
 └── Vendor entity (Sprint 7)
      ├── Legal name (Ltd / LLC / GmbH)
      ├── Jurisdiction selection        ← separate question; blocks entity registration
      └── Trademark filing              ← Nice classes 9 + 42
```

### 1.2 The cost of deciding late

| Decision slips to… | Cost incurred |
|---|---|
| **End of Phase 3 (Sprint 8 / M3)** | v1 JSON Schema `$id` URI published with the wrong name; breaking change to remove it post-v1. Package ids may conflict with a squatter who observed the announcement. |
| **Sprint 9–10** | HTML report, JUnit XML, terminal renderer, CI templates — all embed the docs URL. Renaming them adds a rework sprint on top of the hardening phase. |
| **Sprint 11 hardening** | SBOM, release-signing metadata, packaging scripts all contain the product name. Re-signing after a rename is operationally expensive. |
| **Sprint 12 / M5** | Pilot cohort is already using v1 NuGet ids. A rename forces a breaking migration the pilots were not warned about. Reputation cost. |
| **Post-launch** | Trademark opposition from a prior user; forced rebrand after launch is the maximum cost scenario (MVP §10 risk register). |

The target is a confirmed name **before Sprint 8 ends** so the v1 contract freeze (M3) locks in
the correct package ids and schema URIs. The vendor entity needs the name at the start of Sprint 7
to begin jurisdiction research and legal engagement in parallel.

---

## 2. Naming criteria and scoring rubric

Use the rubric below to score each shortlist candidate. Score each criterion **1–5**; multiply by
the weight; sum for a total out of 100.

| # | Criterion | Weight | Notes |
|---|---|---|---|
| 1 | **Memorability / pronounceability** | 15 | Can a developer say it aloud, spell it from memory, and type it in a terminal? Single word preferred. Avoid gratuitous letter substitutions. |
| 2 | **.NET / developer-tool fit** | 15 | Does it feel at home next to dotnet, xUnit, Testcontainers, Aspire? Avoid names that evoke UI/browser testing (confusion with Playwright / Selenium). |
| 3 | **Trademark availability** | 20 | Knock-out search across Nice classes 9 and 42 in USPTO, EUIPO, WIPO Global Brand DB. A live conflicting registration or a pending application in those classes is a hard block. |
| 4 | **Domain availability** | 15 | Preference order: `.dev` > `.io` > `.com`. If `.com` is live and expensive it is not a hard block, but adds cost. |
| 5 | **NuGet id availability** | 15 | Search nuget.org for `<Name>.*`; also check for squatted variations. Package id must not collide with any existing live package. |
| 6 | **GitHub org availability** | 10 | The exact org name should be claimable. |
| 7 | **No negative connotations** | 5 | Check in English, German, French, Spanish, Japanese (major .NET markets). Look for slang or offensive meanings. |
| 8 | **Differentiation from existing .NET test tools** | 5 | Not easily confused with: xUnit, NUnit, SpecFlow, Reqnroll, Testcontainers, Aspire, Playwright, WireMock.Net, Fixie, Verify, Shouldly, FluentAssertions. |

**Scoring guidance:**

- **5** — clearly passes; no concerns found.
- **4** — passes with a minor caveat.
- **3** — uncertain; needs closer review.
- **2** — conflict or ambiguity exists; possible work-around.
- **1** — hard conflict; likely disqualified.

Candidates scoring **below 65 / 100** or scoring **1 on criterion 3 or 5** should be eliminated
before the decision round.

---

## 3. Candidate shortlist

Six to ten candidates are proposed across three naming strategies:

- **Strategy A — Evocative / coined:** invented or blended words that are unique and trademarkable.
- **Strategy B — Metaphor:** draws on a concept that maps to what the tool does (proof, witness, topology, flow).
- **Strategy C — Descriptive compound:** positions the tool in the .NET/distributed ecosystem.

For each candidate the rationale is given, followed by a pre-screening checklist that the team
must fill in. Availability cells are marked **TODO** — no live lookups have been performed.

---

### Candidate 1 · **Tessera** (Strategy A — coined/evocative)

**Rationale.** A tessera is a small tile used in Roman mosaics; a tessera was also the token
(password, countersign) that verified identity between parties in antiquity. Both senses map
cleanly: the tool is a small verification tile in a larger distributed mosaic, and it "vouches"
for the correctness of a transaction crossing multiple services. The word is short, unique in the
.NET tool space, and easy to pronounce in English, German, and Spanish. `Tessera.Engine`,
`Tessera.Steps.DbAssert.Postgres`, and `dotnet tessera run` all read naturally.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar (e.g. Cloudflare, Namecheap) | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Tessera.*` | nuget.org search | TODO |
| GitHub org `tessera` | github.com/tessera | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Candidate 2 · **Nexon** (Strategy A — coined)

**Rationale.** A blend of "nexus" (a connected series, a hub where multiple systems meet) and the
"-on" suffix common in developer tools (Maven, Nexus itself — but Nexus Repository is an existing
product in the developer-tool space, which is a concern worth flagging). The tool orchestrates a
nexus of containers and services. Short, typeable, CLI-friendly: `nexon run suite.e2e.yaml`.

> **Pre-screening note:** "Nexus Repository" (Sonatype) operates in adjacent tooling space. While
> "Nexon" differs, similarity risk in class 42 warrants careful knock-out screening before
> investing further in this candidate.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Nexon.*` | nuget.org search | TODO |
| GitHub org `nexon` | github.com/nexon | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Candidate 3 · **Axiom** (Strategy B — metaphor)

**Rationale.** An axiom is a statement accepted as true — a foundation for proof. The tool
generates proof that a distributed transaction behaves correctly. "Axiom" is pronounceable
universally, maps to the verification intent, and has no negative connotations.

> **Pre-screening note:** "Axiom" is a common word and is likely in use as a trade mark in
> software classes. Axiom Data (a time-series logging SaaS) already uses it in class 42. This
> candidate has a high conflict probability and should be screened first to decide quickly whether
> to eliminate it, saving effort on the others.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Axiom.*` | nuget.org search | TODO |
| GitHub org `axiom` | github.com/axiom | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Candidate 4 · **Veridian** (Strategy A — coined/evocative)

**Rationale.** Blends "verity" (truth, verification) with a unique suffix making the composite
novel and distinctive — important for trademark strength. Sounds like a product, is five syllables
but only three in casual pronunciation ("vuh-RID-ee-un"), and evokes verdancy (green/passing
tests). No obvious conflict in the .NET tool ecosystem. Package ids read well:
`Veridian.Engine`, `Veridian.Steps.*`.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Veridian.*` | nuget.org search | TODO |
| GitHub org `veridian` | github.com/veridian | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Candidate 5 · **Manifold** (Strategy B — metaphor)

**Rationale.** A manifold is an engineering component that connects and routes flows between
multiple systems — an apt metaphor for a tool that orchestrates a topology of containers,
databases, queues, and HTTP services and routes a single business transaction through all of them.
The word is evocative of distributed systems and well-understood by engineers across disciplines.
CLI invocation `manifold run` reads naturally.

> **Pre-screening note:** "Manifold" is an English common word used in several existing developer
> products (e.g. Manifold.co, defunct). Common-word marks are harder to register without acquired
> distinctiveness unless confined to a narrow class-specific meaning. Screen carefully.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Manifold.*` | nuget.org search | TODO |
| GitHub org `manifold` | github.com/manifold | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Candidate 6 · **Trellis** (Strategy B — metaphor)

**Rationale.** A trellis is a framework that gives shape and support to organic growth — matching
both the scaffolding role the tool plays (declare a topology, let it grow into a running
environment) and the open-source governance model (Core / Verified / Community tiers). Short,
unambiguous spelling, no negative connotations across major languages, and no obvious .NET test
tool conflicts. `Trellis.Engine`, `dotnet trellis run`.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Trellis.*` | nuget.org search | TODO |
| GitHub org `trellis` | github.com/trellis | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Candidate 7 · **Probix** (Strategy A — coined)

**Rationale.** Portmanteau of "probe" (to test/investigate) + "-ix" (convention in developer
tool names: Felix, Helix, Nix, Milix). The "-ix" suffix gives it a technical, developer-native
feel. "Probe" maps directly to what the tool does: it probes a distributed system by driving a
real transaction through it. `Probix.Engine`, `dotnet probix run`, `probix.dev` are all clean
and pronounceable.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Probix.*` | nuget.org search | TODO |
| GitHub org `probix` | github.com/probix | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Candidate 8 · **Synapse** (Strategy B — metaphor)

**Rationale.** A synapse is the junction across which a signal passes between two cells. The tool's
core job is to verify that signals pass correctly across the junctions between services (HTTP,
Kafka, DB, webhook). The word is used in neuroscience and AI contexts but has no notable .NET tool
incumbent. It is pronounceable in all major languages and available as a developer-tool metaphor.

> **Pre-screening note:** Microsoft Azure Synapse Analytics is a high-profile product in the Azure
> ecosystem. While that is a Microsoft Azure product name (not a NuGet package family), the
> proximity to a major .NET-adjacent platform product raises confusion risk. Evaluate carefully on
> the differentiation criterion.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Synapse.*` | nuget.org search | TODO |
| GitHub org `synapse` | github.com/synapse | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Candidate 9 · **Conduit** (Strategy C — descriptive compound)

**Rationale.** A conduit carries signals or resources between points in a system. The tool acts as
a conduit for test signals through a distributed topology. Descriptive names are harder to
trademark in isolation, but `Conduit` is specific enough as a mark in the software testing class
to be registrable with evidence of use. `Conduit.Engine`, `dotnet conduit run` are natural.

> **Pre-screening note:** "Conduit" (Zitadel Conduit) and similar products exist. Common-noun
> marks in class 42 require careful screening. This candidate is included for completeness but
> should be ranked lower on the trademark criterion until a search is completed.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Conduit.*` | nuget.org search | TODO |
| GitHub org `conduit` | github.com/conduit | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Candidate 10 · **Lattice** (Strategy B — metaphor)

**Rationale.** A lattice is an ordered structure of interconnected points — capturing both the
topology the tool orchestrates (services, queues, databases arranged in a defined graph) and the
declarative, structured nature of the `.e2e.yaml` DSL. The word is short, unambiguous, and has
a technical register without being jargon. `Lattice.Engine`, `lattice.dev`, `dotnet lattice run`
all read well.

> **Pre-screening note:** HashiCorp / Akamai have used "Lattice" in adjacent infrastructure and
> service-mesh contexts. Screen carefully for class 9/42 conflicts before investing in this name.

| Check | Registry / tool | Status |
|---|---|---|
| Trademark class 9 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Trademark class 42 | USPTO TESS · EUIPO eSearch · WIPO Global Brand DB | TODO |
| Domain `.dev` | Registrar | TODO |
| Domain `.io` | Registrar | TODO |
| Domain `.com` | Registrar | TODO |
| NuGet `Lattice.*` | nuget.org search | TODO |
| GitHub org `lattice` | github.com/lattice | TODO |
| Connotation check | Native-speaker review (EN/DE/FR/ES/JA) | TODO |

---

### Scoring worksheet (to be completed by PD after pre-screening)

| Candidate | C1 Mem (×15) | C2 .NET fit (×15) | C3 TM (×20) | C4 Domain (×15) | C5 NuGet (×15) | C6 GitHub (×10) | C7 Connotations (×5) | C8 Differentiation (×5) | **Total /100** |
|---|---|---|---|---|---|---|---|---|---|
| Tessera | | | | | | | | | |
| Nexon | | | | | | | | | |
| Axiom | | | | | | | | | |
| Veridian | | | | | | | | | |
| Manifold | | | | | | | | | |
| Trellis | | | | | | | | | |
| Probix | | | | | | | | | |
| Synapse | | | | | | | | | |
| Conduit | | | | | | | | | |
| Lattice | | | | | | | | | |

Eliminate any candidate scoring below 65 or scoring 1 on criterion 3 (trademark) or criterion 5
(NuGet) before the decision round.

---

## 4. Trademark pre-screening process

### 4.1 What pre-screening is and is not

A **knock-out search** is a rapid, self-conducted scan of public trademark registers to identify
obvious conflicts: live registrations or published applications in the target classes for the same
or confusingly similar name. It takes roughly 30–60 minutes per candidate and is the first filter.

A knock-out search does **not** replace a **full clearance opinion**. A full clearance is conducted
or supervised by a qualified trademark attorney. It covers:

- Phonetic and visual similarity analysis (not just exact-name matches)
- Common-law / unregistered use (trade name searches, domain history, forum/repository usage)
- Co-existence risk assessment
- Jurisdiction-specific advice (US, EU, UK post-Brexit, AUS if relevant)

A full clearance is required before filing and should be commissioned for the top two finalists
after the knock-out search eliminates the obvious conflicts.

### 4.2 Nice Classification

File under at minimum:

| Class | Description relevant to this product |
|---|---|
| **9** | Computer software; software development tools; testing software; downloadable software for integration testing of distributed computing systems. |
| **42** | Software as a service (SaaS); providing online non-downloadable software for testing distributed computing systems; software development services; cloud-based software platform services. |

Class 9 covers the downloadable/open-source NuGet package. Class 42 covers any hosted or
cloud-delivered runner, future SaaS offering, or online documentation services.

### 4.3 Search tools (knock-out phase)

| Register | URL | Notes |
|---|---|---|
| USPTO TESS | https://tess.uspto.gov | US federal registrations and published applications. Search "live" status only. Use the "Word Mark" search first, then phonetic variants. |
| EUIPO eSearch plus | https://euipo.europa.eu/eSearch | EU trade marks. Filter by Nice class 9 and 42. |
| WIPO Global Brand Database | https://branddb.wipo.int | Aggregates national registers. Covers IR marks extending into multiple jurisdictions. |
| UK IPO | https://trademarks.ipo.gov.uk | UK national register (separate from EUIPO post-Brexit). Relevant if UK entity or UK launch. |
| DPMA (Germany) | https://www.dpma.de/english | Relevant if GmbH jurisdiction is Germany. |

For each candidate: search the exact word; search common phonetic variations; search for the word
as a prefix + wildcard (e.g. `TESSERA*`) to catch compound marks that might assert priority over
the word alone.

### 4.4 When to involve a trademark attorney

Engage a trademark attorney when:

1. The knock-out search reveals a potentially conflicting registration but not an obvious identical
   conflict — the attorney assesses confusing similarity risk.
2. You are ready to file — attorneys handle the specification of goods/services and respond to
   office actions.
3. You are considering a jurisdiction outside the founding team's home country — different
   filing conventions apply.
4. A third party sends a cease-and-desist or opposition notice at any stage.

Budget for attorney time at two stages: (a) clearance opinion on the top two finalists
(approximately 2–4 hours per candidate, jurisdiction-dependent), and (b) filing and prosecution
(per-class per-jurisdiction flat fee from most IP firms handling software marks).

---

## 5. Decision process and timeline

### 5.1 Participants

| Role | Responsibility |
|---|---|
| **PD (product/delivery lead)** | Owns the process; drives the scoring round; commissions attorney search; makes the final call after stakeholder input. |
| **TL (technical lead/architect)** | Validates .NET ecosystem fit; confirms namespace and package-id implications; signs off on migration impact. |
| **Stakeholders** | Any investors, advisors, or co-founders with naming input; consulted in the scoring round, not the veto path. |
| **Trademark attorney** | Engaged after knock-out screening narrows to two finalists; provides clearance opinion before the decision is announced. |

### 5.2 Staged process

```
Step 1 — Knock-out screening (PD, ~1 week)
  └── For each candidate: run all TODO checks in §3 against the registries in §4.3
  └── Eliminate candidates that fail trademark, NuGet, or GitHub hard checks
  └── Score remaining candidates against the rubric in §2
  └── Produce a ranked shortlist of the top 3

Step 2 — Attorney engagement (PD + attorney, ~1–2 weeks)
  └── Commission full clearance opinion on top 2 candidates
  └── Obtain at minimum US and EU opinions; add UK/DE if entity jurisdiction requires it

Step 3 — Stakeholder scoring round (PD + TL + stakeholders, ~3 days)
  └── Present scored shortlist with attorney clearance notes
  └── PD makes the final decision

Step 4 — Name confirmed; migration begins (see §6)
  └── Claim GitHub org, domain, and NuGet prefix immediately on confirmation
  └── Hand off to legal for vendor entity registration (Sprint 7)

Step 5 — Legal engagement for vendor entity (Sprint 7)
  └── Decide jurisdiction (see §5.4)
  └── Instruct solicitor/attorney to register entity under the chosen name
  └── Begin trademark filing (can run in parallel with entity registration)
```

### 5.3 Phase 3 target decision date

The name must be confirmed **before Sprint 8 ends** (end of Phase 3, M3 gate). At that point:

- The v1 JSON Schema `$id` URI is published and becomes a stable public contract.
- The v1 NuGet package ids are published on NuGet.org.
- The Provider SDK documentation references the package id family.

The latest safe confirmation date is **the start of Sprint 8** — this allows the Sprint 8 team to
use the confirmed name when freezing the schema URI and publishing the SDK artefacts.

The knock-out screening (Step 1) should begin **immediately** in Sprint 6. Attorney engagement
(Step 2) should be commissioned in Sprint 7 to allow the 1–2 week turnaround before the Sprint 8
decision window.

### 5.4 Jurisdiction question for the vendor entity (Sprint 7)

The vendor entity registration (Sprint 7, task S07-E-xx) requires a jurisdiction decision. This
is a separate but related question. Key factors:

- **Where do the founding team members reside?** Formation in the founder's home jurisdiction
  avoids cross-border complexity for payroll and IP ownership.
- **Primary market.** .NET adoption is global; the US and EU are the largest enterprise markets.
  A UK Ltd, Delaware C-Corp, or Irish DAC are common choices for developer-tool companies
  targeting both.
- **IP holding structure.** If the trademark is filed by the entity, the entity must exist or be
  forming before the trademark application. Discuss with the attorney whether to file in the
  founder's personal name initially and assign to the entity on formation.
- **Future investment / open-source foundation.** Delaware C-Corp is standard for US venture
  investment. A foundation model (e.g. .NET Foundation membership or a separate Apache-licensed
  entity) is an alternative for the community tier.

The PD should produce a jurisdiction recommendation for review at the Sprint 7 planning session.

---

## 6. Post-decision migration checklist

Once the name is confirmed, execute these steps in order. Items marked as **immediate** must be
done before any public announcement; items marked **Sprint 8** must be done before M3 artifacts
are published.

### 6.1 Claim assets first (immediate, before any announcement)

- [ ] Claim GitHub organisation: `github.com/<name>` (org, not user)
- [ ] Register domain: `<name>.dev` (preferred) and `<name>.io` as fallback; consider `<name>.com`
      defensively even if not the primary URL
- [ ] Reserve NuGet package ids by publishing a `0.0.1-placeholder` package for each of:
      `<Name>.Engine`, `<Name>.Abstractions`, `<Name>.Steps.*` family, `<Name>.Cli`, `<Name>.Testing`
      (NuGet.org does not allow id reservation without a published package)

### 6.2 Repository rename (before Sprint 8)

- [ ] Rename GitHub repository from `vouchfx` to the new name (GitHub issues an automatic
      redirect from the old URL)
- [ ] Update `vouchfx.sln` solution name
- [ ] Update `README.md` title and all internal cross-links
- [ ] Update `.github/workflows/*.yml` — any hardcoded `vouchfx` references in workflow names
      or step labels

### 6.3 Namespace question (requires explicit decision)

The engine currently reserves `Platform.Engine.*` and `Platform.Steps.*` as its root namespaces.
These are **not** name-derived; they reflect a deliberate design choice (§5.6 of the architecture
blueprint) to keep the reserved namespace stable regardless of the product's marketing name.

Two options:

| Option | Pros | Cons |
|---|---|---|
| **Keep `Platform.*` namespaces** | No breaking change to provider contracts; provider SDK stays `Platform.Steps.*`; less migration work. | "Platform" is generic; NuGet package ids diverge from namespace roots (package `<Name>.Engine`, namespace `Platform.Engine`). |
| **Rename to `<Name>.*` namespaces** | Package id and namespace are aligned; cleaner developer experience; `<Name>.Steps.*` namespace matches package id. | Breaking change to the provider contract at the one point (pre-M3) where it is still possible to make it without external users; requires updating all existing source files and tests. |

**Recommendation:** decide this at the same time as the product name while the codebase is still
pre-public. Post-M3 the v1 provider contract is frozen and a namespace rename is a v2 breaking
change. Record the decision in this document and in `CLAUDE.md` under "Hard invariants".

### 6.4 NuGet package family (Sprint 8, before first public publish)

- [ ] Update all `.csproj` `<PackageId>` elements from `vouchfx.*` / `Platform.*` to
      `<Name>.*` (coordinated with the namespace decision above)
- [ ] Update `<AssemblyName>` elements if renamed
- [ ] Regenerate any NuGet metadata (`<Description>`, `<RepositoryUrl>`, `<PackageProjectUrl>`)
      with the confirmed name and domain

### 6.5 JSON Schema `$id` URI (Sprint 8, before v1 schema publish)

- [ ] Set the canonical `$id` to `https://schemas.<name>.dev/e2e/v1/suite.json`
- [ ] Ensure this URI is reachable (redirect or static host) before publishing v1 schema
- [ ] Update any existing test fixtures that reference a draft `$id`

### 6.6 Documentation and reporting artefacts (Sprint 8–9)

- [ ] Update docs site URL in terminal renderer, HTML report header, JUnit XML comment, and
      SARIF output
- [ ] Update README, CONTRIBUTING.md, and Provider SDK documentation
- [ ] Update `CLAUDE.md` — replace all `vouchfx` references with the confirmed name
- [ ] Update the JSON Lines event schema (`schemaVersion` metadata fields, `source` field)

### 6.7 Launch artefacts (Sprint 12)

- [ ] GitHub Release page title and tag prefix
- [ ] NuGet.org package pages (description, logo, tags)
- [ ] Blog / conference / social material
- [ ] SBOM `name` field and release-signing certificate subject (if a code-signing certificate
      is issued in the entity name)

### 6.8 Legal handoff (Sprint 7)

- [ ] Entity registration with the chosen name in the decided jurisdiction
- [ ] Trademark application filed (Nice classes 9 and 42) — filing date secures priority
- [ ] IP assignment from any personal holdings to the entity (if trademark was filed personally
      pending entity formation)
- [ ] Open-source licence headers updated to reflect the legal entity name as copyright holder

---

*End of planning artefact. Reviewed by: (PD to sign off before Sprint 7 planning).*
