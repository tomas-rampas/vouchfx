# Vendor-Entity Incorporation Plan — Planning Artefact

> **Status:** Planning artefact — Sprint 7, task S07-E-01 (Workstream E, Pilot & feedback).
> Originated: Sprint 7, Phase 3, week 13.
>
> **Ownership notice:** This document scopes the decision and provides the team with a
> structured incorporation checklist and jurisdiction analysis. The actual incorporation,
> legal engagement, and trademark filing are **owned by the product/delivery lead (PD)**.
> Jurisdiction, entity structure, IP assignment, and tax treatment are legal-and-tax
> decisions; consult a qualified solicitor and tax adviser before committing to any
> jurisdiction or structure. Nothing in this document constitutes legal or tax advice.
>
> **Cross-references:**
> - `plan/sprint-07.md` S07-E-01 (acceptance criteria this document satisfies)
> - `plan/product-naming.md` (Sprint 6 naming artefact — the chosen name feeds the entity
>   name and the trademark application)
> - `docs/03_MVP_Project_Plan.md` §1.2 (vendor-entity rationale), §9.6 (licence/governance/
>   trademark), §9.9 (trademark policy), §10 (risk register — "Enterprise procurement teams
>   cannot adopt the platform because no vendor entity is named", Med/High)

---

## Contents

1. [Why a vendor entity now — what it unblocks](#1-why-a-vendor-entity-now--what-it-unblocks)
2. [Jurisdiction analysis](#2-jurisdiction-analysis)
3. [Entity structure](#3-entity-structure)
4. [Trademark application prep](#4-trademark-application-prep)
5. [Incorporation checklist and timeline](#5-incorporation-checklist-and-timeline)
6. [Legal-engagement checklist](#6-legal-engagement-checklist)

---

## 1. Why a vendor entity now — what it unblocks

### 1.1 The procurement problem

Enterprise procurement teams routinely ask — from the first conversation, before any
commercial commitment — who they are legally taking software from. This applies even to
free-tier software: the team's third-party-software request form, software-asset inventory,
and GDPR Data Processing Agreement all require a named rights-holder. "Open-source project
on GitHub" does not satisfy a corporate legal team. A named entity does.

The MVP §10 risk register assigns this "Med / High" probability and impact. Without the
entity the platform is blocked from the pilot programme at the procurement gate, regardless
of how good the tool is technically.

### 1.2 What the entity specifically enables

| Artefact or action | Requires the entity |
|---|---|
| Vendor of record on procurement paperwork | Yes — a natural person is insufficient for enterprise policies at most firms |
| Data Processing Agreement (DPA) | Yes — must be signed by a legal entity |
| Master Software Agreement (MSA) / EULA | Yes |
| Security questionnaire / SOC 2 roadmap | Yes — auditors require an entity |
| Trademark registration (Nice classes 9 + 42) | Yes — filed in the entity's name (see §4) |
| GitHub organisation and domain ownership | Strongly preferred — avoids personal-name complications on departure |
| Bank account for future revenue | Yes |
| Signing of contracts with cloud providers (hosting, CDN, DNS) | Yes for commercial accounts |
| Future investment (if pursued) | Yes — investors wire to an entity, not a person |

### 1.3 Dependency on the Sprint 6 product-name decision

The entity's trading name and company name derive from the chosen product name. The
Sprint 6 artefact (`plan/product-naming.md`) defines the naming process and candidate
shortlist. The following incorporation steps are **blocked** until the name is confirmed:

- Company name reservation at the relevant registry
- Trademark application (the mark to be applied for is the product name)
- Domain registration in the entity name
- GitHub organisation claim

The following steps can proceed **in parallel**, before the name is confirmed:

- Jurisdiction decision and legal-advice engagement
- Registered-office address arrangement
- Director appointment and share-capital planning
- Bank account application (most banks allow company-name changes post-formation)

The target is a confirmed product name **before the end of Sprint 8** (MVP §9.6,
`plan/product-naming.md` §5.3). The entity should be filed, or filing initiated with a
firm date, by the end of Sprint 7 (the acceptance criterion for S07-E-01).

### 1.4 The cost of deciding late

| Entity decision slips to... | Consequence |
|---|---|
| **End of Sprint 8 / M3** | v1 schema `$id` URI and NuGet package-id family published without a legal owner named in SPDX/SBOM metadata; correcting this after first public publish is operationally expensive. Pilot conversations stall if enterprise pilots cannot sign a DPA. |
| **Sprint 9–10** | HTML report, JUnit XML, and CI templates embed a docs URL; the registrar-level domain cannot be controlled until the entity exists. Trademark application delayed — priority date is lost; a squatter could observe the Phase 3 announcement and pre-file. |
| **Sprint 11 hardening** | Release-signing certificate, SBOM name field, and NuGet package ownership all require an entity. Correcting them after release is operationally expensive and damages credibility. |
| **Post-launch** | A trademark opposition or forced rebrand is the maximum-cost scenario (MVP §10). |

---

## 2. Jurisdiction analysis

The working assumption in `docs/03_MVP_Project_Plan.md` §1.2 is **CZ s.r.o. (Czech
Republic, EU)**. This section evaluates that assumption against four common alternatives
and provides a recommendation. The final jurisdiction choice is a legal-and-tax decision
and must be made with qualified professional advice.

### 2.1 Jurisdictions evaluated

| # | Jurisdiction | Vehicle |
|---|---|---|
| 1 | **Czech Republic** | s.r.o. (Společnost s ručením omezeným — Czech private limited company) |
| 2 | **United Kingdom** | Ltd (private company limited by shares) |
| 3 | **Ireland** | DAC (Designated Activity Company) or private company limited by shares |
| 4 | **United States (Delaware)** | C-Corporation |
| 5 | **United States (Wyoming/Delaware)** | LLC |

### 2.2 Comparison matrix

| Criterion | CZ s.r.o. | UK Ltd | Irish company | US Delaware C-Corp | US LLC |
|---|---|---|---|---|---|
| **Enterprise procurement credibility** | Good — EU entity, VAT-registered, familiar to EU procurement | Good — well understood globally | Good — EU, well-known to tech companies | Excellent — US enterprise teams expect this for US vendors | Moderate — less familiar outside the US |
| **EU data-residency / GDPR posture** | Native — GDPR compliance is domestic; DPAs straightforward; no SCCs required for EU customers | Post-Brexit: SCCs or UK IDTA required for EU data transfers; dual-regime compliance overhead | Native — GDPR compliance is domestic; DPAs straightforward | Requires SCCs for EU data transfers; adds compliance overhead for regulated EU pilots | Same as C-Corp |
| **Formation cost** | Low — CZK 1 minimum share capital; notary fee ~CZK 5–10k; registry fee ~CZK 2k | Low — GBP 50 online, GBP 12 share capital minimum; formation same day | Moderate — EUR 25k minimum share capital for some vehicles (plc); private limited ~EUR 1 at formation; solicitor fees moderate | Moderate — USD 90 Delaware filing; registered agent ~USD 100–200/yr | Low — USD 90 Delaware/Wyoming filing; registered agent required |
| **Ongoing admin burden** | Moderate — annual accounts, Czech tax filing, Czech trade licence; Czech-language statutory requirements | Low to moderate — Companies House filing, HMRC; UK-language requirements; straightforward for English speakers | Moderate — CRO annual returns, Irish Revenue; English-language requirements; solicitor typically retained | High — Delaware franchise tax (minimum ~USD 450/yr), board resolutions, shareholder formalities, US accounting required | Lower than C-Corp but US accounting, state reporting, and registered agent required |
| **IP holding and trademark filing** | Solid — entity can hold IP and file EUIPO marks directly as EU applicant; WIPO IR available | Solid — entity can hold IP; EUIPO access via the new Comparable TM process (post-Brexit transition); UK IPO separately | Solid — EUIPO filing as EU applicant; same WIPO IR access as CZ | Solid — USPTO filing natural fit for US marks; EUIPO via WIPO IR; recommended if primary market is US enterprise | Same as C-Corp for IP; pass-through tax treatment |
| **Future investment / exit optionality** | Moderate — EU venture landscape growing; some EU VCs prefer Irish or Delaware holding; convertible instruments less standardised than US | Moderate — EIS/SEIS tax relief attractive to UK angels; less liquid for US VC | Moderate to good — large US tech and VC ecosystem uses Irish entities for EU holding; recognised by US investors | Excellent — standard for US VC investment; SAFEs, convertible notes all template-available; easiest for acqui-hire or M&A | Good for bootstrapped or angel-funded; converts to C-Corp easily but conversion has tax implications |
| **Contracting with US customers** | Requires W-8BEN-E for US withholding; no US entity needed for service contracts | Same — W-8BEN-E; familiar to US procurement | Same | Natural fit — US entity, W-9 supplied, no withholding complexity | Natural fit |
| **Contracting with EU customers** | Natural — domestic; VAT OSS for B2B EU invoicing | Requires EU VAT registration or fiscal representative post-Brexit for some EU B2B transactions | Natural — domestic EU; VAT straightforward | Requires EU VAT registration; slightly more friction for EU B2B | Same as C-Corp |
| **Founder base** | Matches working assumption (CZ/EU resident founder) — no cross-border complexity for payroll, NI, personal tax | Cross-border admin if founders are CZ-resident; HMRC vs Czech tax authority requires specialist advice | Cross-border admin for CZ-resident founders | Significant cross-border complexity; US tax filing for foreign founders is expensive and burdensome | Same as C-Corp |
| **Open-source / community optics** | Neutral — no particular open-source community preference | Neutral | Neutral | Neutral — Linux Foundation / Apache Foundation projects often use Delaware entities | Neutral |

### 2.3 Recommendation

**Working recommendation: CZ s.r.o.**

The CZ s.r.o. is the lowest-friction starting point given the apparent CZ/EU resident
founder base. It is GDPR-native, avoids cross-border personal-tax complexity, has low
formation cost, is a well-understood entity type for EU enterprise procurement, and allows
direct EUIPO trademark filing. It is a sound choice for the MVP and pilot phase.

**The primary open question is whether the primary enterprise market is predominantly EU
or US.** If a significant fraction of the target pilot cohort is US enterprise, consider
whether to form a lightweight US operating entity (LLC or C-Corp) as a subsidiary once
the pilot evidence materialises — this is a post-MVP decision, not a Sprint 7 one.

**Open questions for the lawyer:**

1. Does the founding structure (sole founder vs future co-founders) affect the s.r.o.
   share-capital and articles choice?
2. Is a Czech trade licence (živnostenský list) sufficient, or is a specific regulated
   activity licence required?
3. What VAT threshold applies at incorporation vs once revenue is earned? When should VAT
   registration be filed?
4. Should the trademark be initially filed in the founder's personal name and then assigned
   to the entity, or should the entity be formed first and file directly?
5. If a US pilot customer requires a US-entity counterparty on an MSA, what is the
   simplest bridge (branch, LLC, or reseller agreement)?
6. Are there any Czech corporate tax incentives applicable to an open-source software
   vendor in Phase 1?

---

## 3. Entity structure

### 3.1 s.r.o. vs alternatives

For the MVP phase a plain CZ s.r.o. is the recommended vehicle. A holding structure
(e.g. a Dutch Stichting or Irish holding company above a CZ operating subsidiary) is
appropriate only if investment rounds or IP-licensing arrangements are being actively
designed. For the pilot phase this adds complexity without benefit.

A branch of a foreign entity (e.g. a UK Ltd branch in CZ) is less suitable because it
does not create a separate legal personality in the Czech Republic, complicating
independent IP ownership.

### 3.2 Founder and cap structure

| Consideration | Notes |
|---|---|
| **Sole founder at incorporation** | Permissible for a CZ s.r.o.; the sole shareholder is also typically the sole director (jednatel). Straightforward for the MVP phase. |
| **Future co-founders** | Add via share transfer or new-share issuance; the articles should explicitly permit this without requiring a notarial act for small transfers. Raise with the lawyer at formation. |
| **Share capital** | CZK 1 minimum; CZK 200,000 is customary for credibility with banks and larger customers. Actual paid-in capital is the lawyer/accountant's advice. |
| **Vesting schedule** | If additional founders or key employees are added later, a contractual vesting schedule (cliff + monthly) is advisable even in a private company. Consult the lawyer before any equity is granted. |

### 3.3 Open-source IP relationship

The platform is Apache 2.0 licensed. This has specific IP implications:

| Item | Position |
|---|---|
| **Copyright on code** | Under Apache 2.0, contributors retain copyright in their contributions. The licence grants downstream users all rights they need. The entity does **not** acquire copyright in third-party contributions by virtue of the licence alone. |
| **Trademark** | The entity holds and files the trademark (the product name and logo, Nice classes 9 + 42). The trademark is separate from the Apache 2.0 copyright licence. Apache 2.0 does not grant trademark rights — the project's trademark policy (MVP §9.9) governs use. |
| **Commercial offerings** | Any proprietary add-ons (the future cloud tier, enterprise-only features) are owned by the entity and may carry a separate commercial licence. The open-source core remains Apache 2.0 regardless. |
| **Entity owns repository/domain/org** | GitHub organisation, domains, and NuGet package ownership should be transferred to or initially registered under the entity, not personal accounts. |

### 3.4 CLA vs DCO — decision pointer

MVP §9.6 commits to the **Developer Certificate of Origin (DCO)** rather than a
Contributor Licence Agreement (CLA). This decision is appropriate for the Apache 2.0
open-source model: contributors retain copyright, the DCO signoff (`Signed-off-by:`) is
a lightweight assertion of the right to contribute under the project's licence, and it
requires no separate infrastructure.

A CLA would be required only if:

- The project later dual-licences (e.g. open core with a commercial licence) and needs
  to relicense existing contributions; or
- A downstream enterprise customer contractually requires the vendor to warrant that it
  holds all necessary rights in the codebase (rare for Apache 2.0 projects).

**Flag for the lawyer:** confirm whether the DCO is sufficient given the intended
commercial model, particularly in the context of any future SaaS or cloud-tier offering.
If the commercial model may involve exclusive licensing of any part of the codebase,
revisit the CLA question before the project has a large contributor base — retrofitting a
CLA after many contributors is significantly harder.

---

## 4. Trademark application prep

Target: application prepared and ready to file in early Sprint 9. The trademark filing date
secures priority; earlier is better.

### 4.1 Dependency on the product-name decision

The trademark application cannot be filed until the product name is confirmed. Per
`plan/product-naming.md` §5.2, the name should be confirmed by the start of Sprint 8.
The trademark application preparation (class specification, filing strategy, knock-out
search completion) should run in parallel during Sprint 7–8 so that the application
can be submitted within days of the name being confirmed, not weeks.

### 4.2 Nice classes

File under at minimum two Nice classes:

| Class | Description relevant to this product |
|---|---|
| **9** | Computer software; software development tools; testing software; downloadable software for integration testing of distributed computing systems; software for orchestrating containerised environments for automated testing. |
| **42** | Software as a service (SaaS); providing online non-downloadable software for integration testing of distributed computing systems; software development services; cloud-based software platform services; technical support services relating to software for automated testing. |

Class 9 covers the downloadable/open-source NuGet package. Class 42 covers any hosted or
cloud-delivered runner and future SaaS offering. Both classes are required; filing only
one leaves the other open to a squatter.

### 4.3 Filing jurisdictions

| Registry | Rationale | Vehicle |
|---|---|---|
| **EUIPO (EU trade mark)** | Covers all 27 EU member states with a single filing; natural fit for a CZ entity; directly enforceable across the EU. Priority: file here first. | EUIPO eSearch; file via EUIPO online portal or via a Czech patent attorney |
| **Czech Industrial Property Office (ÚPV)** | National CZ mark; provides additional domestic protection and a clear record in the home jurisdiction. Relatively low cost and fast grant. | File in parallel with EUIPO or shortly after |
| **USPTO (US)** | Required to protect against US squatters and to support future US enterprise contracts that reference the trademark. File an intent-to-use (ITU) application; actual use in the US is not required at filing. | Via a US trademark attorney; ITU basis is appropriate pre-launch |
| **WIPO International Registration (IR)** | Designate additional territories (UK, AU, JP, CA) through a single WIPO application based on the EUIPO or ÚPV base mark. Cheaper than filing separately in each jurisdiction once a base registration exists. | After the base EUIPO or ÚPV mark is granted |

**Priority recommendation:** File EUIPO + ÚPV concurrently in Sprint 9 (immediately after
name confirmation). Commission a US ITU application simultaneously. WIPO IR follows once
the base mark is granted (typically 6–18 months post-application).

### 4.4 Knock-out search status

Per `plan/product-naming.md` §4, a knock-out search across USPTO TESS, EUIPO eSearch, and
WIPO Global Brand Database should be completed for each shortlisted candidate in Sprint 6
before the Sprint 7 entity work begins. The scoring worksheet in `plan/product-naming.md`
§3 records the outcome. This document **does not repeat that search** — it depends on the
knock-out results being in the worksheet before the trademark application is prepared.

**Confirm with PD:** knock-out search results entered in `plan/product-naming.md` §3 for
all candidates? If not, this is a blocker for trademark preparation.

### 4.5 Budget ballpark

These are indicative figures for planning purposes only; actual quotes should be obtained
from a Czech patent attorney and a US trademark attorney.

| Item | Indicative cost |
|---|---|
| EUIPO filing, classes 9 + 42 | EUR 850–1,100 (official fees: EUR 850 for two classes online) |
| ÚPV (Czech) filing, classes 9 + 42 | CZK 5,000–8,000 (official fees; attorney fee additional ~CZK 5,000) |
| US ITU filing, classes 9 + 42 | USD 700–900 in official filing fees; attorney fee for ITU preparation ~USD 500–1,500 |
| Attorney clearance opinion (top 2 candidates, US + EU) | USD/EUR 1,500–4,000 depending on firm |
| WIPO IR (after base mark granted, 3–4 territories) | CHF 1,000–2,000 in WIPO fees + attorney handling |
| **Total estimated range (Sprint 9 filings)** | **EUR 3,500–8,000 equivalent** (excluding WIPO IR which comes later) |

---

## 5. Incorporation checklist and timeline

The steps below assume a CZ s.r.o. as the chosen vehicle. Steps that are blocked on the
confirmed product name are marked. Steps that can proceed in parallel are marked.

### 5.1 Pre-formation steps (can start immediately, Sprint 7)

| # | Step | Blocked on name? | Owner | Notes |
|---|---|---|---|---|
| P1 | Confirm jurisdiction decision with lawyer | No | PD | Book initial legal consultation; bring the jurisdiction analysis in §2 of this document. |
| P2 | Confirm entity vehicle (s.r.o.) and structure with lawyer | No | PD | One sole founder, CZK 200k share capital (or lawyer's recommendation), articles permitting future share transfer. |
| P3 | Identify registered-office address | No | PD | Options: founder's residential address; a virtual office / registered-office service (~CZK 3–6k/yr); a physical office if the team has one. The address is publicly visible on the Czech Business Register. |
| P4 | Obtain a Czech data-box (datová schránka) for the director | No | PD | Required for electronic communication with Czech authorities post-incorporation. Applied for after entity formation. Note here as a pre-formation awareness item. |
| P5 | Open a formation bank account (or confirm bank) | Partially — name helps but is not required | PD | Several Czech banks (Fio, ČSOB, Raiffeisenbank) allow company-account opening in parallel with formation. Funds for share capital are deposited pre-registration. |

### 5.2 Name-dependent steps (unblock when product name is confirmed)

| # | Step | Notes |
|---|---|---|
| N1 | **Reserve company name** at the Czech Business Register (Obchodní rejstřík) | Check the name is not already registered at `or.justice.cz`. The trade name need not be identical to the product name but should match or contain it. |
| N2 | **Draft articles of association** (společenská smlouva / zakladatelská listina) | Prepared by a notary or a lawyer with notarial powers; includes company name, registered office, business activity (subject předmětu podnikání), share capital, and director details. |
| N3 | **Notarial execution** | A notary must execute the founding document; cost typically CZK 3,000–7,000. |
| N4 | **Deposit share capital** | Deposit the agreed share capital into the formation bank account; the bank issues a confirmation letter. |
| N5 | **Apply for a Czech trade licence** (živnostenský list) | Filed with the Trade Licensing Office (živnostenský úřad); "Free trade" (volná živnost) category covers software development; cost CZK 1,000; processing ~5 business days. |
| N6 | **File for registration** at the Business Register court | Submit the notarial deed, trade licence, registered-office confirmation, bank capital confirmation, and director identity documents. Processing: 5–15 business days (expedited service available for a fee). |
| N7 | **Receive IČO** (Company Registration Number) | The company legally exists from this point. |
| N8 | **Apply for DIČ** (Tax Identification Number / VAT) | Filed with the Czech Financial Administration; mandatory registration as a taxpayer; voluntary VAT registration if below the CZK 2M threshold (advise with accountant on timing). |
| N9 | **Open operating bank account** in the entity's name | May be the same institution as the formation account; add the IČO and DIČ once issued. |
| N10 | **Engage an accountant / accounting software** | Czech double-entry bookkeeping (podvojné účetnictví) is required. Monthly payroll reporting if the director draws a salary. |
| N11 | **Register for social security and health insurance** (OSSZ / health insurer) | If the director draws any remuneration, registration with the Czech Social Security Administration (ČSSZ) and a health insurance company is required. |
| N12 | **Claim digital assets in entity name** | Transfer GitHub org, domains, and NuGet namespace ownership from personal to entity account (or register new ones under the entity directly). |

### 5.3 Timeline

The following is a realistic timeline assuming the product name is confirmed at the
start of Sprint 8 (the latest safe point per `plan/product-naming.md` §5.3). Steps P1–P5
can begin immediately in Sprint 7.

```
Sprint 7 (weeks 13–14) — parallel preparation
  P1  Legal consultation booked and conducted
  P2  Entity structure confirmed
  P3  Registered office arranged
  P5  Bank relationship initiated

Sprint 8 (weeks 15–16) — name confirmed; file
  N1  Company name check and reservation
  N2  Articles drafted with notary
  N3  Notarial execution
  N4  Share capital deposited
  N5  Trade licence application filed
  N6  Business Register filing submitted
      → Target: filing submitted by end of Sprint 8

Sprint 9 (weeks 17–18) — entity active; trademark filed
  N7  IČO received (if not before Sprint 9)
  N8  DIČ / VAT registration filed
  N9  Operating bank account open
  N10 Accounting set up
  N11 Social security / health insurance registration
  N12 Digital asset transfer to entity
      Trademark applications filed (EUIPO + ÚPV + US ITU)
```

**Acceptance criterion for S07-E-01:** entity incorporated, or filing in progress with a
firm submission date recorded here. The firm date should be no later than the end of
Sprint 8 based on the timeline above.

_Firm date: [PD to record confirmed date after legal consultation]_

---

## 6. Legal-engagement checklist

### 6.1 What to bring to the lawyer at the initial consultation

The following items should be prepared or decided before the first legal consultation to
make the engagement efficient:

| Item | Notes |
|---|---|
| **Jurisdiction preference and rationale** | Bring this document's §2 analysis. The lawyer confirms or challenges. |
| **Confirmed or shortlisted product names** | For company name availability check and trademark strategy advice. |
| **Intended business activities** | Software development tools (downloadable and SaaS); open-source Apache 2.0 distribution; future cloud-hosted service. The lawyer maps these to the correct trade-licence category and VAT codes. |
| **Founder(s) identity and residency details** | Passport, proof of address; required for the notarial deed. |
| **Registered-office address** | Confirmed address or options. |
| **Share capital amount** | Proposed amount (CZK 200k working assumption). |
| **IP ownership position** | Confirm: the entity will hold the trademark; existing code was authored by the founder (or contributors under Apache 2.0 DCO); no prior assignments or employer IP claims. |
| **Open-source model summary** | Apache 2.0 engine and SDK; DCO contributors; three governance tiers (Core/Verified/Community); no current CLA. |
| **Commercial model overview** | Free Indie tier (open-source); future Professional/Enterprise tiers (SaaS, likely subscription); no current revenue. |

### 6.2 Questions only the lawyer can answer

The following questions are explicitly noted as outside the scope of this planning artefact
and must be answered by qualified legal and tax professionals:

| # | Question |
|---|---|
| L1 | Is the CZ s.r.o. the right vehicle, or does the anticipated commercial model (open-core SaaS, possible future EU/US investment) warrant a different structure from the outset? |
| L2 | Should the trademark be filed in the founder's personal name initially (to preserve the filing date) and assigned to the entity post-formation, or is it worth delaying the filing until the entity exists? What is the IP-assignment cost and complexity? |
| L3 | What is the correct VAT treatment at formation vs at the point revenue is earned? When should voluntary VAT registration be made? |
| L4 | Is a CLA required in addition to the DCO given the intended dual-commercialisation model (open-source + future SaaS)? |
| L5 | Are there any Czech Republic incentives (R&D tax credits, Antivirus programme successors, CzechInvest schemes) applicable to a software-development startup in Phase 1? |
| L6 | If a US enterprise customer requires a W-9 equivalent or insists on a US-entity counterparty, what is the lightest-weight solution (a US LLC subsidiary, a US virtual office, or a contractual structure that keeps the CZ entity as the contracting party)? |
| L7 | For the Apache 2.0 codebase, are existing licence headers sufficient, or should a formal copyright assignment from the sole-founder period be recorded in the entity's founding documents? |
| L8 | What insurance cover is appropriate at incorporation for a software-vendor entity (professional indemnity, product liability, cyber)? At what ARR threshold does this become material for enterprise customer due diligence? |

### 6.3 Contract templates to commission

The following templates should be produced or procured through the legal engagement.
They are the artefacts enterprises will ask for in pilot procurement.

| Template | Priority | Notes |
|---|---|---|
| **Data Processing Agreement (DPA)** | P1 — needed for first enterprise pilot | GDPR Art. 28 compliant; covers the engine running in a customer's CI pipeline (processor role); addresses EU/EEA data location for the SaaS tier. |
| **Master Software Agreement (MSA)** / End-User Licence Agreement (EULA) | P1 — needed for paid tiers | Covers licence grant (Apache 2.0 for the open-source components), support obligations, IP ownership, limitation of liability, and governing law. |
| **Pilot / Beta Programme Agreement** | P1 — needed for Sprint 10–12 pilot cohort | Short-form agreement for the structured pilot programme; includes data-handling terms, feedback rights, and no-warranty language. |
| **Contributor DCO confirmation** | P2 | A short CONTRIBUTING.md notice and `Signed-off-by:` commit-message convention; the lawyer confirms the DCO text is adequate for CZ law purposes. |
| **Trademark licence / policy** | P2 — publish at v1.0 launch (MVP §9.9) | Following the .NET Foundation / Apache Software Foundation template; covers factual use, derivative naming, forks. |
| **Privacy Policy** | P2 — needed before any website with telemetry | Covers opt-in telemetry, contact forms, cookie policy; GDPR-compliant. |

### 6.4 Enterprise procurement artefacts

The following are the documents enterprise procurement and infosec teams commonly request.
They are not legal contracts but depend on the entity existing and on legal sign-off.

| Artefact | Sprint readiness | Notes |
|---|---|---|
| **W-8BEN-E** (US withholding certificate) | Entity must exist | Completed by the entity; certifies foreign status for US customers withholding. |
| **VAT invoice template** | Entity must have DIČ | Czech VAT-compliant invoice format; include IČO, DIČ, IBAN, and CZ bank details. |
| **Security questionnaire** (e.g. SIG Lite, CAIQ, VSAQ) | Can draft in Sprint 9–10 | Answers to standard vendor security questionnaires; references the SOC 2 roadmap and OWASP-aligned controls already in the architecture blueprint (§11). |
| **SOC 2 Type 1 roadmap** | Phase 4–5 target | Documents the path to SOC 2 Type 1 (typically 6–12 months of controls evidence). Enterprise pilots will ask for this roadmap even if the audit has not been completed. |
| **SBOM** (Software Bill of Materials) | Sprint 11 deliverable | Per MVP §9.6; released with each signed NuGet package; lists all transitive dependencies and their licences. |
| **Open Source Licence Compliance statement** | Sprint 11 | Confirms all dependencies are Apache 2.0-compatible; produced from the SBOM and the dependency-licence scan. |

---

*End of planning artefact. Reviewed by: (PD to sign off before Sprint 7 planning session concludes).*
*Firm incorporation date to be recorded in §5.3 after legal consultation.*
