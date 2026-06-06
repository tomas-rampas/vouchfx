# Sprint 12 — Pilot & release (Milestone M5)

| | |
|---|---|
| **Phase** | 5 — Pilot and release (MVP §8.5) |
| **Weeks** | 22–24 |
| **Length** | **3 weeks** (pilot-heavy; mostly non-code work) |
| **Milestone** | **M5 — Pilot & v1.0 release** closes at end of sprint |
| **Theme** | Gather the evidence the whole MVP exists to produce. Release v1.0 deliberately, onboard the pilot cohort, instrument the success criteria, and write the go/no-go assessment. |

## Sprint goal

v1.0 is released across all channels under the chosen name; at least eight pilot teams are onboarded and
running their own tests; the success criteria are instrumented and being measured; the launch communications
and governance artifacts are published; and the go/no-go assessment is written for the steering review.

> **Capacity note:** this is a three-week, PD-led, non-code-dominated sprint. Engineering capacity is held
> mostly for launch support and fast doc/contract fixes (the first-thirty-days response commitments,
> MVP §5.4, §9.4). The pilot's six-week active measurement window extends *beyond* week 24; M5 requires the
> cohort onboarded and measurement under way, with the gate read at the end of the window.

## Tasks

### Workstream E — Pilot & feedback

#### S12-E-01 · Publish launch governance artifacts
- **Owner:** PD · **Estimate:** 2d · **Depends on:** S07-E-01 · **Spec:** MVP §9.6, §9.7, §9.8, §9.9
- Publish, before launch day: the open-source/commercial **feature boundary** (§9.7), the **public
  roadmap** (§9.8), the **governance document** with the Verified-tier rubric (§9.6), the **trademark
  policy** (§9.9), and `CODE_OF_CONDUCT.md` + `SECURITY.md`. DCO sign-off is wired on the repos.
- **Acceptance:**
  - All artifacts are live on the project website/repo at launch; the feature boundary and roadmap are
    public before anyone asks.

#### S12-E-02 · Onboard the pilot cohort (≥ 8 teams)
- **Owner:** PD · **Estimate:** 3d · **Depends on:** S06-E-01, S10-E-* · **Spec:** MVP §8.5.1, §8.5.4, §4.2 (adoption gate), §10
- Bring at least eight teams (Engaged + Light-touch tracks) onto the tool using the Phase 4 documentation
  and the §8.5.2 support model; confirm each meets the team profile and opts into telemetry.
- **Acceptance:**
  - ≥ 8 teams onboarded across both tracks; pilot contracts agreed; telemetry opt-in confirmed per team.

#### S12-E-03 · Run weekly working sessions & retrospectives
- **Owner:** PD · **Estimate:** 2d (recurring) · **Depends on:** S12-E-02 · **Spec:** MVP §8.5.1, §8.5.3, §8.5.4
- One hour per Engaged team per week with a structured agenda; the week-three calibration and week-six
  demand-signal retrospectives; observational notes captured.
- **Acceptance:**
  - Sessions held on cadence; week-three retrospective completed within the sprint; notes recorded toward
    the per-pilot written brief.

#### S12-E-04 · Author migration porting examples
- **Owner:** PD · **Estimate:** 2.5d · **Depends on:** S10-E-02 · **Spec:** MVP §8.5.4, §3.2, §10 (migration-path risk)
- Author worked examples porting a representative Postman collection, an xUnit integration test, and a
  SpecFlow feature onto the platform, showing `script.csharp` as the escape hatch — honest "re-author, not
  auto-convert" framing.
- **Acceptance:**
  - Three runnable porting examples published; the manual path is concrete, not daunting.

#### S12-E-05 · Instrument & measure the success criteria
- **Owner:** PD · **Estimate:** 1.5d · **Depends on:** S10-G-04 · **Spec:** MVP §4.2, §8.5.3, §8.5.4
- Collect time-to-first-test, flakiness, adoption, behavioural demand signal, and community-pathway
  viability by telemetry, retrospective, and observation; reconcile against the §4.2 targets.
- **Acceptance:**
  - Each §4.2 criterion has a measurement source wired; an interim dashboard reflects live pilot data.

#### S12-E-06 · Go/no-go assessment & gate read
- **Owner:** PD · **Estimate:** 1.5d · **Depends on:** S12-E-05 · **Spec:** MVP §4.5, §8.5.4, §11.3
- Produce the written assessment that feeds the cloud-tier go/no-go, addressing each conjunctive gate
  (sustained adoption, behavioural demand signal, community pathway) and recommending Go / Iterate-locally
  / Pivot / Stop.
- **Acceptance:**
  - The assessment addresses all three gates explicitly with evidence; a clear recommendation is ready for
    the steering review.

### Workstream D — Release

#### S12-D-01 · Release v1.0 across all channels
- **Owner:** TL · **Estimate:** 1.5d · **Depends on:** S11-D-03 · **Spec:** MVP §9.1, §9.4, §8.5.4
- Publish v1.0: `dotnet` global tool, VSCode Marketplace extension, and GitHub Release with binaries,
  SBOM, and provenance; tag the docs and schema to the release.
- **Acceptance:**
  - All channels carry v1.0; install paths verified on all three operating systems; "good first issue"
    labels open.

#### S12-D-02 · Launch communications
- **Owner:** TL + PD · **Estimate:** 1.5d · **Depends on:** S12-D-01, S12-E-01 · **Spec:** MVP §9.4, §5.4 (first 30 days)
- Execute the launch: website walkthrough of the reference scenario, Show HN / Lobsters, /r/dotnet,
  .NET Foundation Slack + Discord, and the launch blog post. Commit to the first-thirty-days response SLAs
  (issues within one business day, external PRs within five).
- **Acceptance:**
  - Launch artifacts live on every channel; the response-SLA rota is staffed (PC primary, TL backup).

### Workstream F — Community

#### S12-F-01 · Open the community provider repository
- **Owner:** PC · **Estimate:** 1.5d · **Depends on:** S08-F-04 · **Spec:** MVP §8.5.4, §6.6, §9.6, §4.2 (community gate)
- Open the providers repository for contributions with issue templates that politely auto-close
  off-topic submissions, the Verified-tier rubric as a checklist, and the integration-test fixture as the
  gate. Ringfence the PC triage budget (one half-day/week, MVP §5.4).
- **Acceptance:**
  - The repo accepts external PRs; templates, rubric, and fixture are in place; triage budget scheduled.

> **Telemetry note:** the opt-in telemetry flow and pilot backend are built in **Phase 4** (task
> `S10-G-04`) so they ship inside the v1.0 build the cohort runs. This sprint *uses* that telemetry to
> measure (`S12-E-05`); it does not implement it.

## Exit criteria — Milestone M5 (MVP §8.5.4)

v1.0 is released; at least eight pilot teams are onboarded and into their active window; the §4.2 success
criteria are instrumented and being measured against their targets with each gate addressed; the launch and
governance artifacts are published; the community provider repository is open; and a clear, evidence-backed
go/no-go recommendation on the cloud tier is documented and ready for the steering review.

> The six-week pilot measurement window completes after week 24; the final gate read and the steering
> decision (Go / Iterate-locally / Pivot / Stop, MVP §11.3) occur at the end of that window.

## Risks mitigated this sprint (MVP §10)

- Quiet launch with no DevRel (first-thirty-days SLAs + launch rota) · Migration disengagement (porting
  examples) · Telemetry too thin (retrospectives are the primary evidence; telemetry confirms) ·
  Hostile-fork before trademark grants (policy published at launch) · Pricing mis-calibration (bands
  refined in pilot conversations before the demand-signal ask).

## What happens after M5

The plan's instrument has done its job. The go/no-go decision (MVP §11.3) reads the three conjunctive
gates and routes to one of four outcomes — **Go** (fund the cloud fabric), **Iterate-locally** (deepen the
Indie tier on a time-boxed cycle), **Pivot** (reshape around what landed), or **Stop** (reassess the
premise). The architecture blueprint sequences the eighteen-month roadmap that a Go unlocks.
