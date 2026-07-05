# Human-only launch actions

> Compiled 2026-07-05. These are the launch-critical items that **cannot be done by engineering
> or automation** — they need a human signature, account, payment, or decision. Everything
> engineering-side that could be closed has been closed (see
> [plan/go-to-market-gap-analysis.md](plan/go-to-market-gap-analysis.md) for the full register;
> GTM references below point into it). Tick items off here as they complete, and record
> decisions in the plan documents each row links to.
>
> **The schedule insight:** items 1 and 3 *are* the schedule — a 6–10-week outreach lead time
> plus the six-week measurement window put the earliest realistic go/no-go read at
> **mid-October to mid-November 2026** if item 1 starts immediately. Everything else fits
> inside their shadow.

## 1. Start this week (longest poles — all independent, run them in parallel)

| # | Action | Why now | Reference |
|---|--------|---------|-----------|
| 1 | ☐ **Send the first pilot-outreach tranche.** The message templates are ready to send in [plan/pilot-recruitment.md](plan/pilot-recruitment.md) §3.2–3.4, under the working name. Stand up the candidate tracker (§4). | 6–10-week cold-outreach lead time; the go/no-go read is T0 + 12–16 weeks. Every idle week moves the whole programme a week right. Nothing else compresses this. | GTM-35 · [#72](https://github.com/tomas-rampas/vouchfx/issues/72) · [#116](https://github.com/tomas-rampas/vouchfx/issues/116) |
| 2 | ☐ **Run the naming knock-out on "vouchfx" itself** — trademark classes 9/42 (CZ/EU/US), `vouchfx.dev`/`.io`/`.com`, the NuGet id, the GitHub org name. | The v1 artefacts are frozen under this name; a dirty result gets more expensive daily. If clean: record the decision in [plan/product-naming.md](plan/product-naming.md) §6.3 and buy the domains. | GTM-29 · [#73](https://github.com/tomas-rampas/vouchfx/issues/73) |
| 3 | ☐ **Book the legal consultation → incorporate the vendor entity** (working recommendation: CZ s.r.o., see [plan/vendor-entity.md](plan/vendor-entity.md)); record the firm date in that document. | Gates the pilot agreement (enterprise pilots), the DPA/MSA, and the bank account needed for GA Gate 2's refundable deposits. | GTM-30/31/33 · [#81](https://github.com/tomas-rampas/vouchfx/issues/81) |
| 4 | ☐ **Start certificate procurement** — Authenticode, Apple Developer ID (enrolment alone takes 1–2 weeks), GPG key — then load the CI secrets (`CODESIGN_PFX_BASE64`/`_PASSWORD`, `APPLE_ID`/`APPLE_TEAM_ID`/`APPLE_APP_SPECIFIC_PASSWORD`, `GPG_SIGNING_KEY`/`GPG_PASSPHRASE`). | M4 exit Gate 1; without them the v1.0 release ships unsigned except for cosign/SLSA. | GTM-03 · [#114](https://github.com/tomas-rampas/vouchfx/issues/114) |

## 2. Account and credential provisioning (hours of work, but only you can hold the keys)

| # | Action | Reference |
|---|--------|-----------|
| 5 | ☐ **Reserve `vouchfx` and `Platform.Sdk` package IDs on NuGet.org** and set up the Platform.Sdk publish channel. Trusted Publishing policy created 2026-07-05 — the `NUGET_API_KEY` secret is no longer needed. Residual: claim the package IDs (first push claims "vouchfx"); if >7 days pass before first publish, restart the policy activation window on NuGet.org. | GTM-02 · [#84](https://github.com/tomas-rampas/vouchfx/issues/84) |
| 6 | ☐ **Register the Visual Studio Marketplace publisher** ("vouchfx") for the VSIX. | GTM-04 |
| 7 | ☐ **Provision Azure for the telemetry backend** — subscription, OIDC federated identity, ACR, resource group; populate the three placeholder secrets (`TELEMETRY_INGEST_TOKENS`, `DB_ADMIN_PASSWORD`, `DB_CONNECTION_STRING`); run its `deploy.yml`; smoke `/healthz`. **Must be live before the six-week measurement window opens** — it measures GA Gate 1. | GTM-36 · [#152](https://github.com/tomas-rampas/vouchfx/issues/152) |

## 3. Sign-offs and ceremonies (calendar items)

| # | Action | Reference |
|---|--------|-----------|
| 8 | ☐ **Hold one combined M3+M4 steering session** — closes both milestones' review gates in a single slot; the TL signs the contract freeze in the same meeting. | GTM-32 · [#91](https://github.com/tomas-rampas/vouchfx/issues/91) · [#114](https://github.com/tomas-rampas/vouchfx/issues/114) |
| 9 | ☐ **Recruit the named outside contributor** for the S08-F-05 SDK-validation sign-off (the engineering was clean-room-validated; only the external signature is missing). | [#86](https://github.com/tomas-rampas/vouchfx/issues/86) |
| 10 | ☐ **Run the GitLab CI template on a live pipeline** — needs a real GitLab account/runner (the template is static-validated only); *or* record the explicit deferral to Sprint 12 that [plan/m4-phase-exit.md](plan/m4-phase-exit.md) §5 permits. | [#153](https://github.com/tomas-rampas/vouchfx/issues/153) |

## 4. Decisions and launch-day acts (unblocked by the sections above)

| # | Action | Reference |
|---|--------|-----------|
| 11 | ☐ **Cut the `v1.0.0` tag.** The 2026-07-05 smoke run proved the pipeline end-to-end (all build/sign jobs green); needs items 4 and 5 first — and formally item 8. **Before tagging,** verify the Trusted Publishing policy's active state on NuGet.org (RELEASING.md "pending-activation caveat"); restart the activation window if needed. | GTM-01/05 · [#121](https://github.com/tomas-rampas/vouchfx/issues/121) |
| 12 | ☐ **Approve and publish the launch governance artefacts** — trademark policy, open-source/commercial feature boundary, public roadmap. Drafts can be produced on request; the policy positions are yours. | GTM-24 · [#115](https://github.com/tomas-rampas/vouchfx/issues/115) |
| 13 | ☐ **Launch communications** — Show HN, /r/dotnet, the long-form blog post, and *naming the 30-day support-SLA rota* (a person commitment: issues < 1 business day, external PRs < 5 days). | GTM-34 · [#122](https://github.com/tomas-rampas/vouchfx/issues/122) |
| 14 | ☐ **Run the pricing pre-commitment conversations and take deposits** during the week-3 pilot retrospectives — needs the entity's bank account (item 3). | GTM-33 |
| 15 | ☐ **Submit the conference CFP**, if that channel is wanted — deadlines are calendar-fixed. | [plan/pilot-recruitment.md](plan/pilot-recruitment.md) §3.5 |

---

*Owner: PD. Review cadence: weekly until items 1–4 are all in flight, then at each Sprint 12
checkpoint. Superseded by the S12-E-06 go/no-go assessment.*
