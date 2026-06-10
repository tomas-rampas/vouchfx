# Pilot Recruitment Plan

> **Planning artefact — Sprint 6 / S06-E-01.**
> This document defines the recruitment strategy, outreach tactics, candidate pipeline schema, and
> timeline for assembling the vouchfx pilot cohort. It is a plan for the product/delivery lead (PD)
> to act on, not a record of actions taken. All outreach, legal consent collection, contact handling,
> and data retention decisions are owned by the PD and subject to applicable law. No actual outreach
> has been performed by the creation of this document.

---

## 1. Objective and Gate

### 1.1 Target

Recruit a pipeline of **eight candidate teams**, over-recruited against the **six-team go/no-go
gate** defined in MVP §4.2 ("Sustained pilot adoption") and §8.5.1.

The six-team gate requires at least six distinct teams to run the tool against a real suite at
least three times per week for six consecutive weeks. Eight are targeted because real-world
attrition between "committed" and "actively running" is reliably non-zero: teams are re-assigned,
sprints slip, platform decisions change. Recruiting eight against a six-team requirement gives a
30 % attrition buffer before the gate is at risk.

### 1.2 Lead Time and Latest Safe Start

The lead time from cold outreach to an engaged, onboarded pilot team has been estimated at **6–10
weeks** in practice (MVP §8.3, §8.5.1). The pilot programme begins in Phase 5 (weeks 22–24,
Sprint 12). Working back from week 22 with a 10-week lead time puts the latest safe start of
outreach at **week 12** — the second week of Phase 3 and of Sprint 6.

Recruitment therefore **opens in Sprint 6** (weeks 11–12) as task S06-E-01. Delaying to Phase 4
would leave insufficient runway to correct a slow response rate before the pilot cohort is needed.

### 1.3 Risk Cross-Reference

This plan directly mitigates the **"too few pilot teams"** risk identified in MVP §10. That risk
is rated as one of the plan's primary non-technical risks; its probability and impact both increase
sharply if recruitment begins later than Phase 3. The recruitment pipeline is tracked explicitly
(§4 below) so the PD can trigger the fallback (§6.3) before the window closes.

---

## 2. Ideal Pilot Profile

### 2.1 Qualifying Criteria

A strong pilot candidate meets all of the following:

| Criterion | Description |
|---|---|
| **Distributed .NET estate** | The system under test crosses at least two of: a synchronous REST call, an asynchronous message (Kafka or similar), a database mutation (relational or document), an outbound webhook or event notification. Pure monoliths are not the target. |
| **Five or more .NET services** | Enough surface to generate meaningful integration coverage; single-service teams rarely articulate cross-service pain. |
| **Existing integration-test pain** | The team can name a specific problem: flaky test environments, absent end-to-end coverage, onerous hand-rolled harnesses, or slow feedback loops. The platform must solve a real problem, not a theoretical one. |
| **Container-capable infrastructure** | Docker is available on developer machines and in CI. The tool provisions its topology locally; teams that cannot run Docker are blocked at the first step. |
| **Named champion** | One engineer (platform engineer, staff engineer, or senior developer) who owns the evaluation and will attend the agreed cadence. A team without a champion rarely sustains engagement past onboarding. |
| **Willingness to opt into telemetry** | The success criteria in MVP §4.2 depend on opt-in usage telemetry. Teams that cannot accept even anonymised telemetry cannot contribute to the measured gate. |

### 2.2 Preferred Characteristics (not mandatory)

- Prior experience with Testcontainers, Aspire, or comparable local topology tooling — reduces onboarding friction.
- A CI pipeline that already runs integration tests — the team has both pain and context.
- A team lead or engineering manager willing to be named in release notes (engaged-track teams, per MVP §8.5.1).
- Cross-timezone flexibility: the platform team operates European business hours (08:00–18:00 CET); teams in other timezones are welcomed with the caveat stated in MVP §8.5.2.

### 2.3 Disqualifiers

The following automatically disqualify a candidate from the pilot:

| Disqualifier | Reason |
|---|---|
| **Pure UI/browser-only testing** | The platform is not a UI automation or browser-testing tool. Recruiting teams expecting Playwright/Selenium equivalence sets false expectations and produces misleading feedback. |
| **No container infrastructure** | Docker is a hard runtime dependency. Teams running on locked-down estates without Docker access cannot run the tool at all. |
| **No distributed service surface** | A single-service application has no cross-service transaction to test. The platform's core claim is untestable against it. |
| **No integration-test pain** | A team satisfied with its existing approach has no incentive to sustain engagement. The demand-signal gate (MVP §4.2) requires behavioural evidence of value; teams without pain rarely produce it. |
| **Unable to commit to minimum cadence** | Engaged-track teams: one engineer-day per week for six weeks. Light-touch teams: at least three runs per week with a week-six retrospective. Below this, the team cannot contribute to the sustained-adoption gate. |

---

## 3. Channels, Tactics, and Message Templates

Five channels are run in parallel, as specified in MVP §8.5.1 and S06-E-01. Each has a distinct
audience, a specific ask, and a recommended cadence. All outreach is honest about the product's
status: early, free, and supported.

### 3.1 Channel Summary

| Channel | Audience | Specific ask | Cadence | Owner role |
|---|---|---|---|---|
| **Direct outreach** (DM / email) | Engineers in the team's professional network, prior colleagues, known .NET platform practitioners | 20-minute introductory call, followed by an onboarding conversation if the profile fits | One personalised message per contact; follow up once after 10 days if no reply; do not chase further | PD |
| **.NET subreddit** (r/dotnet) | Broad .NET developer community | Community post describing the problem space, linking to the worked reference scenario; invite teams that recognise the pain | One post at the start of outreach; one follow-up post in week 14 if pipeline is below target | PD |
| **.NET Foundation Slack** | .NET open-source and community contributors, including platform engineers | Post in #testing or #general describing the pilot programme; invite DMs from interested parties | One post at channel open; maintain the thread actively for one week; do not repost in the same channel within 30 days | PD |
| **dotnet Discord** | Active .NET developer community, skews toward practitioner engineers | Same content as Slack post, adapted for Discord formatting; cross-link to the GitHub reference scenario | One post per relevant channel (e.g. #testing, #cloud-native); active thread management for one week | PD |
| **Conference CFP** | .NET conference attendees and reviewers | Abstract submission for a talk that demonstrates the platform against a real distributed scenario; the talk functions as deferred recruitment at scale | Submit to the target conference in week 11; expect notification by week 13–14; deliver if accepted | PD + TL |

### 3.2 Direct Email / DM Template

> **Subject:** Early access pilot — distributed .NET integration testing tool
>
> Hi [Name],
>
> I'm reaching out because you work on distributed .NET systems, and I think you might feel the same
> pain we set out to solve.
>
> We have built **vouchfx** (working name): a tool that lets you describe a business transaction —
> a REST call, a Kafka event, a database mutation, a webhook — as a short YAML file, and runs it
> against a real, locally provisioned topology (.NET Aspire + Testcontainers). No hand-rolled
> harnesses, no mocked-out infrastructure, no `Thread.Sleep`. Reports tell you which step of which
> transaction failed and why.
>
> We are opening a small pilot cohort before the v1.0 release. It is free, and the team will be
> on hand to help you author your first suite. The ask is modest: around one engineer-day per week
> for six weeks, with a structured conversation at the end about whether it solved the problem.
>
> If that sounds relevant to your team's situation, I would welcome a 20-minute call to see whether
> the fit is there. No pressure — if the timing is wrong or the profile does not match, a "not now"
> is genuinely useful feedback.
>
> The reference scenario (a four-provider end-to-end suite) is at [link]; it gives you a concrete
> sense of what authoring looks like.
>
> Best,
> [Name], vouchfx team

### 3.3 Community Post Template (.NET Subreddit / Slack / Discord)

> **Title:** We built a declarative integration-testing tool for distributed .NET systems —
> looking for pilot teams
>
> If you run a .NET microservices estate and your integration test story is "it's complicated",
> this might be relevant.
>
> **The problem we are solving:** testing a business transaction that crosses a REST call, a Kafka
> event, a database write, and an outbound webhook requires either a hand-rolled test harness (slow
> to build, slow to maintain) or a managed cloud environment (expensive and often unavailable
> locally). Most teams end up with a patchwork that nobody trusts.
>
> **What we built:** vouchfx compiles a short `.e2e.yaml` file into C# and orchestrates the
> required container topology on your machine using .NET Aspire and Testcontainers. The test engine
> runs the transaction against real infrastructure and reports Pass / Fail / Inconclusive — with a
> polling timeline for async assertions so you can see *why* a Kafka consumer never received the
> message, not just that it did not.
>
> We are opening a small pilot cohort ahead of v1.0. It is free. The team is hands-on during the
> pilot. The ask is three runs of your own suite per week and a conversation at the end.
>
> **You are a good fit if:** you have five or more .NET services, at least one cross-service
> transaction (REST + Kafka or REST + DB + webhook), Docker in your CI, and a named engineer who
> will own the evaluation.
>
> Reference scenario (four providers, full YAML → CSX → run): [link]
> DM me or comment here if you want to explore it.

### 3.4 Conference CFP Abstract Sketch

> **Proposed title:** From YAML to a running distributed test in under 90 seconds — live demo
>
> **Abstract (300 words):**
>
> Integration testing a distributed .NET system — a business transaction that crosses a REST API,
> a Kafka event, a Postgres mutation, and an outbound webhook — is a solved problem in theory.
> In practice it means choosing between a hand-rolled test harness with fragile environment setup
> or an expensive managed cloud environment that is unavailable on a developer's laptop at 3 pm on
> a Friday.
>
> This talk walks through vouchfx, a tool that replaces both approaches with a short declarative
> YAML file. Live on stage, a `.e2e.yaml` suite is authored, compiled to C# via Roslyn, and run
> against a real topology provisioned by .NET Aspire and Testcontainers — containers started,
> health-gated, seeded, tested, and torn down — while the report renders step by step in the
> terminal.
>
> The talk covers: the authoring model (the DSL, capture, substitution, and the RETRY verification
> mode that replaces `Thread.Sleep` for async assertions); the compilation pipeline (YAML to AST
> to CSX to a collectible AssemblyLoadContext — and why that last detail is not optional); the
> orchestration model (.NET Aspire headless, Testcontainers, health gates, Respawn reset between
> runs); and the verdict taxonomy (why Inconclusive is not Fail, and why conflating them destroys
> trust in a testing tool).
>
> The talk ends with a live pilot recruitment call: teams running .NET microservices who feel this
> pain are invited to join the early access cohort before v1.0.
>
> **Audience:** .NET platform engineers, staff engineers, and engineering leads maintaining
> microservices estates.
> **Format:** 40-minute session with live demo. Slides available open-source after the event.
> **Target conferences:** .NET Conf (CFP typically opens August/September); NDC Oslo / London;
> DotNext; BuildStuff.

---

## 4. Candidate-Tracking Sheet Schema

Track every candidate team in a shared spreadsheet or issue tracker. The following columns
define the minimum viable pipeline record.

### 4.1 Column Definitions

| Column | Type | Values / Format | Notes |
|---|---|---|---|
| **Team / Org** | Text | Free text (e.g. "Payments team @ Acme Ltd") | Use team-level granularity per MVP §4.2 — multiple teams within one company count separately only when they own different services. |
| **Primary contact** | Text | Name + email / handle | The named champion (§2.1). |
| **Channel / Source** | Enum | Direct · Subreddit · Slack · Discord · CFP · Referral · Other | Where the lead originated. |
| **Distributed-surface fit** | Enum | Strong · Partial · Unknown · Disqualified | Strong = crosses at least two of REST / Kafka / DB / webhook. Partial = one-service or partial Docker. Assessed during screening call. |
| **Stage** | Enum | See §4.2 | Current funnel position. |
| **Champion confirmed** | Boolean | Yes / No | Has a named engineer accepted ownership of the evaluation? |
| **Track** | Enum | Engaged · Light-touch | Self-selected per MVP §8.5.1. |
| **Next action** | Text | Free text (e.g. "Send reference scenario link; await reply") | Single next step, owned by PD. |
| **Next action due** | Date | ISO 8601 | Date by which PD acts if no response received. |
| **Last contact** | Date | ISO 8601 | Date of most recent meaningful exchange. |
| **Notes** | Text | Free text | Relevant context: blockers, constraints, referral source detail, follow-up flags. |

### 4.2 Funnel Stages

| Stage | Definition | Entry trigger | Exit trigger |
|---|---|---|---|
| **Contacted** | Outreach has been sent; no substantive reply yet. | PD sends first message or post goes live and a team member responds. | Candidate replies (→ Screening) or is silent after one follow-up (→ Disqualified / Archived). |
| **Screening** | A conversation (call or async exchange) is under way to assess fit against §2.1. | First substantive reply from candidate. | Screening call complete: fit confirmed (→ Committed) or disqualifier identified (→ Disqualified / Archived). |
| **Committed** | The team has agreed in writing (email or chat message) to the minimum cadence for their chosen track, a named champion is confirmed, and a start-week is agreed. Telemetry consent is obtained. | Screening confirms fit and candidate explicitly agrees to the pilot terms. | Onboarding begins in Sprint 12 (→ Onboarded) or candidate withdraws (→ Withdrawn). |
| **Onboarded** | The team has completed the getting-started guide and has run at least one suite against their own system. | Sprint 12 onboarding session completed. | Pilot concludes (→ Completed) or team exits early (→ Withdrawn). |
| **Completed** | The team has run their suite for six weeks, attended the week-three and week-six retrospectives (engaged track) or the week-six retrospective (light-touch), and the demand-signal conversation is recorded. | Week-six retrospective complete. | — |
| **Withdrawn** | Team exited the pilot voluntarily at any stage. | Written confirmation of exit. | A withdrawal is a data point about the tool (MVP §8.5.1); the PD records the reason. |
| **Archived** | No response after one follow-up, or profile disqualified. | No reply to follow-up, or disqualifier confirmed in screening. | — |

**Definition of "Committed":** a team is committed when (a) a named champion has explicitly agreed
to the pilot terms in writing, (b) the minimum weekly cadence for their chosen track is accepted,
(c) a start-week is fixed, and (d) telemetry consent is recorded. A verbal expression of interest
does not constitute commitment; a written agreement does.

### 4.3 Pipeline Health Targets

| Metric | Target | Warning threshold | Action |
|---|---|---|---|
| Teams at Contacted or later | 8 by end of week 13 | Fewer than 5 by end of week 12 | Activate fallback (§6.3): widen channels, increase direct outreach volume |
| Teams at Screening or later | 6 by end of week 14 | Fewer than 4 by end of week 13 | Review messaging and profile criteria; consider referral incentive |
| Teams at Committed | 8 by end of week 16 (M3) | Fewer than 6 by end of week 15 | Escalate to executive sponsor; review whether pilot can proceed on six |

---

## 5. Timeline: Phase 3 Outreach Cadence (Weeks 11–16)

Phase 3 spans Sprints 6–8 (weeks 11–16). The outreach cadence below is designed to build a
committed pipeline of eight teams by the M3 gate at the end of week 16 — in time for Phase 4
preparation and Sprint 12 onboarding.

| Week | Sprint | Key outreach actions | Pipeline target | CFP milestone |
|---|---|---|---|---|
| **11** | S06 | Open direct outreach: send first wave of personalised DMs / emails to 15–20 network contacts. Prepare community post copy and reference-scenario link. | Contacted: 8+ | **Submit CFP** to primary target conference (see §3.4). Deadline is typically week 11–12 for .NET Conf; verify exact date and submit early. |
| **12** | S06 | Publish community posts: .NET subreddit, .NET Foundation Slack, dotnet Discord. Begin responding to replies and routing to screening. | Contacted: 12+; Screening: 3+ | Confirm CFP submission received. |
| **13** | S07 | First-wave screening calls / async exchanges complete. Send second wave of direct outreach to any remaining network contacts. Follow up on non-responding first-wave contacts once. | Screening: 6+; Committed: 2+ | Expect CFP acceptance / rejection notification. If accepted: confirm date, begin slide outline. |
| **14** | S07 | Complete screening for all active candidates. Begin committing teams that have confirmed fit. Retire disqualified / unresponsive candidates to Archived. | Committed: 5+; Pipeline (Contacted+) : 8+ | If CFP accepted: share talk abstract with TL for technical review. |
| **15** | S08 | Close remaining screening conversations. Chase uncommitted candidates once. If pipeline below warning threshold (§4.3), activate fallback immediately. | Committed: 7+ | Finalise talk slide structure if CFP accepted. |
| **16** | S08 (M3) | **Pipeline gate:** eight teams at Committed. Handoff list to Sprint 12 onboarding plan. Archive all Archived / Withdrawn candidates with reasons recorded. | **Committed: 8** (gate) | — |

> **CFP submission deadline.** The conference CFP submission in week 11 is time-critical. .NET Conf
> CFPs typically open in August and close six to eight weeks before the event; NDC and BuildStuff
> open three to six months ahead. The PD must confirm the exact deadline for the chosen conference
> in week 10 (before Sprint 6 starts) and treat the week-11 submission as a hard commitment. A
> missed CFP window cannot be recovered within the Phase 3 window.

---

## 6. Success Metrics and Risk

### 6.1 Primary Targets

| Metric | Target | Source |
|---|---|---|
| Teams in pipeline (Contacted or later) at end of week 13 | 8 | MVP §8.5.1 |
| Teams at Committed at end of week 16 (M3 gate) | 8 | MVP §8.5.1 (over-recruitment target) |
| Teams at Committed that proceed to Onboarded in Sprint 12 | 6 (minimum gate) | MVP §4.2 "Sustained pilot adoption" |
| Teams completing six-week engagement (Completed) | 6 | MVP §4.2 |
| Teams at Engaged track | At least 3 of the 6 committed | MVP §8.5.1 (ensures structured retrospective data) |

### 6.2 Response-Rate Assumptions

The following assumptions underpin the pipeline targets. If observed rates deviate materially,
the PD should escalate and trigger the fallback before the gap becomes unrecoverable.

| Channel | Assumed response rate | Expected leads | Basis |
|---|---|---|---|
| Direct outreach (20 contacts) | 40–50 % | 8–10 | Professional network; personalised message; audience already working in the target space |
| .NET subreddit post | 5–10 % of engaged readers | 2–4 | Community posts on r/dotnet reach 5,000–20,000 impressions for relevant topics; 2–4 qualified DMs is realistic |
| .NET Foundation Slack | 10–20 % of channel readers | 1–3 | Smaller, higher-quality audience; relevant channel members are pre-filtered |
| dotnet Discord | 5–15 % of thread participants | 1–3 | Active developer community; quality varies by channel |
| CFP / conference talk | 0 in Phase 3; 3–8 post-talk | Deferred to Phase 5 | Talk recruits at the event, after M3; not counted toward the Phase 3 pipeline gate |

The combined expected pipeline from direct, Slack, Discord, and subreddit is approximately
12–20 contacts, of which 40–60 % are expected to pass screening, yielding 5–12 committed teams.
The lower bound (5) is below target; the upper bound (12) is comfortably above. This spread
justifies the fallback plan below.

### 6.3 Fallback if Response Is Low

If the pipeline is below warning thresholds (§4.3) at any checkpoint, the following actions are
triggered in order:

1. **Widen direct outreach.** Extend to second-degree network contacts: colleagues of colleagues,
   conference speaker alumni, known contributors to Aspire, Testcontainers, or Confluent .NET
   client projects on GitHub.

2. **Show HN / Lobsters post.** Publish a "Show HN" post and a Lobsters submission linking to
   the reference scenario. These audiences are more sceptical than a .NET-specific community but
   produce high-quality leads when the technical substance is credible.

3. **Direct partnerships.** Approach .NET consultancies (known to the team) and ask whether any
   of their clients are experiencing the target pain. A consultancy-mediated introduction
   short-circuits cold outreach lead time.

4. **Incentive for engaged-track teams.** Offer engaged-track teams a named acknowledgement in
   the v1.0 release notes and, if the vendor entity is incorporated in time (S06-E-02 / S07),
   priority access to early cloud-tier features. These are low-cost signals of commitment that
   increase the perceived value of participation.

5. **Reassess the six-team gate.** If, despite the above, the committed pipeline reaches only
   four or five teams by the end of week 16, the PD escalates to the executive sponsor with a
   written assessment: proceed on a reduced cohort (accepting lower statistical confidence in
   the success criteria) or extend Phase 3 by one sprint to allow more recruitment time. This
   decision is the executive sponsor's, not the PD's alone.

> **Cross-reference:** This fallback directly addresses the "too few pilot teams" risk in MVP §10.
> That risk is rated as one of the plan's highest-impact non-technical risks. Early detection
> (via the §4.3 pipeline health targets) is the primary mitigation; this fallback is the
> secondary mitigation if early detection reveals a gap.

### 6.4 Non-Goals

This plan does not cover:

- Onboarding mechanics (covered in Sprint 12 / MVP §8.5.1–8.5.4).
- The demand-signal conversation script (covered in MVP §8.5.3).
- Legal consent forms, data processing agreements, or privacy notices (PD + legal, outside scope
  of this planning artefact).
- Pilot success-criteria measurement (covered in MVP §4.2 and the Sprint 12 plan).
