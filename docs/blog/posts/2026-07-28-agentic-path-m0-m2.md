---
date: 2026-07-28
authors:
  - tomas-rampas
categories:
  - Announcements
tags:
  - AI
  - MCP
  - Scaffold
  - Healer
  - Tooling
---

# A long day on the agentic path for vouchfx

I spent most of today pushing vouchfx further into the “AI can actually help author tests” space. Not a big marketing launch — more a day of shipping foundations, arguing with myself about product shape, and cleaning up the mess that follows when five docs sites must stay honest.

<!-- more -->

## What I shipped

Three layers of the agentic plan, in order, because the order matters.

**M0 — the catalogue and the schema as truth.**  
The engine can now export the composed v1 JSON Schema (`vouchfx schema`) and a shape-level step catalogue (`list --json` with required/optional fields, capture support, family intent). That is not glamorous, but without it any AI helper invents step types that do not exist. VS Code and vouchfx-mcp consume the live export instead of a stale vendored list. This landed as Spec A and is on main.

**M1 — Generator scaffold.**  
`vouchfx scaffold` (and MCP `scaffold_suite`) take structured intent — step types from the catalogue, environment outline, step ids — and emit a schema-valid `.e2e.yaml` skeleton with provenance comments and secret references only. Free text stays with the host LLM. The tool does not “understand English”; it refuses unknown types and produces something validate can accept. Spec B, also merged.

**M2 — Healer diagnose.**  
MCP `diagnose_run` builds on `explain_run`: same four-outcome taxonomy, plus review-only patch proposals for **Fail**, and infrastructure guidance for **EnvironmentError**. No auto-apply. No rewriting the suite because Docker failed. Spec C is built and has a PR open on vouchfx-mcp.

Around that: PRs, DCO, Copilot comments, a mkdocs strict link that broke the site build (one wrong relative path in README when it is embedded as project-readme), and the docs fan-out so sibling sites do not stay stale when the engine docs move.

## The pain (the real one)

The hard part was not “call an LLM”. I deliberately did not put a model inside the engine or the MCP server. The hard part was **not letting the product become something stupid**.

I almost went down OpenAPI-as-the-whole-Generator. That sounds smart until you remember vouchfx is multi-seam: Kafka, Postgres, webhooks, not only REST. OpenAPI does not know your topics. Forcing everything through OpenAPI would have been a lie.

I also almost invented a human JSON “intent” format that then becomes YAML. That is just another language with worse tooling. If the user already lives in free text with an MCP host, the value is grounding — catalogue, validate, run — not a second config dialect.

And then the boring operational pain: a soft-skipped `ECOSYSTEM_DISPATCH_TOKEN` meant sibling Pages only refreshed on weekly cron. For a fleet of five sites that share “live facts”, that is not “fine for solo”. It is drift with a smile. We made fan-out required and set the secret properly.

## Lesson learnt

**Deterministic tools first, clever language models second.**  
If the tool surface invents step types or confuses Fail with EnvironmentError, the agent will amplify the mistake. Spec A was the boring prerequisite that makes Spec B and C honest.

**Do not confuse “MCP implies LLM” with “the server should run the LLM”.**  
MCP without a host model is not how people will use vouchfx-mcp for authoring — but the server still stays deterministic. The host drafts; validate and catalogue keep the draft legal.

**Product arguments beat plan wording.**  
I had “OpenAPI first” in an older plan note. After M0 shipped the catalogue, that note was wrong. Changing the plan in conversation was correct; shipping the wrong spine to defend a document would have been worse.

**Fleet ops is product.**  
If docs sites disagree about engine version, users stop trusting the tool. Soft-skip is not a feature.

## What I am planning

**Short term**

- Land Healer PR on vouchfx-mcp, keep CI and review noise under control.
- Make sure people can actually install engine + MCP paths that include scaffold and diagnose without a local pack ritual forever (publish story still matters).
- M3 is Planner: coverage gaps and “retest what changed”, but only after M1/M2 are real in the open.

**Explicitly not planning**

- Becoming a UI/browser tool or UiPath clone. That stays out.
- Unsupervised AI rewriting test suites into git. Proposals only.
- Pretending ROI percentages from vendor white papers are our metrics.

## Honest status

The agentic layer is still a path, not a finished product. Generator scaffold and Healer diagnose are usable from the right commits and PRs; the full “conversation to green multi-tech suite” story still depends on the host LLM and on humans reviewing output. That is by design.

If you try scaffold or diagnose and something is wrong, please open an issue. I would rather fix contact with reality than polish a story that only works in the demo.

## Links

- [Getting started — Generator / suite scaffold](https://vouchfx.io/getting-started/#generator--suite-scaffold)
- [vouchfx-mcp documentation](https://vouchfx-mcp.vouchfx.io/)
- [Engine PRs (recent): schema/catalogue, scaffold](https://github.com/tomas-rampas/vouchfx/pulls?q=is%3Apr+is%3Amerged)
- [MCP Healer PR](https://github.com/tomas-rampas/vouchfx-mcp/pull/38)
