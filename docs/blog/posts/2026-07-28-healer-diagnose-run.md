---
date: 2026-07-28
authors:
  - tomas-rampas
categories:
  - Announcements
tags:
  - AI
  - MCP
  - Healer
  - Tooling
---

# Why the Healer refuses to “fix” your environment

I already wrote about the broader agentic day — catalogue, scaffold, the boring foundations. This post is only about the Healer: `diagnose_run` on vouchfx-mcp, and the one rule I will not break.

<!-- more -->

## The bug that is not a bug

When an agent sees a red test, it wants to help. That is fine. What is not fine is when the red means “Docker could not pull the image” and the agent starts rewriting your assertions.

vouchfx has four outcomes for a reason:

- **Pass** — the system under test behaved as expected  
- **Fail** — the product is wrong; the test caught it  
- **Environment error** — the lab is wrong (pull, health, provision, seed)  
- **Inconclusive** — we ran out of time or evidence never arrived  

Only **Fail** is a product defect by default. Environment error does not break CI unless you ask it to. That is not pedantry. If you mix them, people stop trusting the suite, and then they stop writing suites.

So the Healer is not “make it green at any cost”. It is “tell me the truth about the run, and if it was a real Fail, suggest a review-only patch.”

## What `diagnose_run` actually does

You already had `explain_run`: it reads the JSON Lines event stream from a finished run (or the last run in the MCP session) and returns a structured diagnosis — verdict, category meaning, summary, notable steps, RETRY timelines, environment-error records. No re-run. No containers.

`diagnose_run` sits on top of that. Same events file. Same taxonomy. Plus:

- for steps that **Failed** with usable observation/diff evidence: a **proposal** — `stepId`, a short `rationale`, and a `patch` (template / unified-diff style snippet)  
- for **EnvironmentError**: **no** suite rewrite; infrastructure guidance instead  
- for **Inconclusive**: guidance, still no rewrite of the suite as if it were a product bug  
- for **Pass**: empty proposals  

Nothing writes your `.e2e.yaml` for you. Nothing calls git. The tool is read-only. If a host LLM rewrites the wording of a proposal, that is the host’s job — the MCP server still does not invent step types or “heal” a dead container by editing `expect.status`.

## Why proposals are Fail-only

This is the whole product.

An environment error is often: image pull, unhealthy service, missing history, seed failed. Editing the test “to match reality” in that situation trains the suite to accept a broken lab. Next week CI is green and production is not.

A Fail with an expected-vs-observed diff is the opposite case: the product drifted, or the assertion is wrong, or both. A proposal is a starting point for a human, not a commit.

I almost put “smart” auto-apply in the plan once. Then I remembered every time an agent “fixed” my test for me and I spent an hour undoing it. Proposals only.

## What it is not

- Not a closed-loop self-healing robot that owns your git history  
- Not an LLM inside vouchfx-mcp — the server stays deterministic; the host model is optional colour on top  
- Not formal `healer-suggestion` events in the engine wire yet — that can come later if renderers need it  
- Not a substitute for reading the events file when the response is truncated for size  

## How you use it (roughly)

```
run_suite  →  events file
explain_run / diagnose_run  →  structured truth + Fail proposals
you (or your agent, with your approval) apply a change
validate / run again
```

If you only have the events path, that is enough for v1. Suite path is optional; without the source, patches are weaker, but the taxonomy stays honest.

## Status, honestly

`diagnose_run` is on main in [vouchfx-mcp](https://github.com/tomas-rampas/vouchfx-mcp) (merged after Spec C). Install story for the MCP tool is still the usual pin/CLI dance until publishing is as boring as the engine tool. Try it, break it, open an issue.

I care more about one correct “this is EnvironmentError, do not edit the assertion” than about ten flashy self-heal demos.

## Links

- [vouchfx-mcp tools reference](https://vouchfx-mcp.vouchfx.io/docs/tools-and-resources.html)
- [Overview (Generator + Healer)](https://vouchfx-mcp.vouchfx.io/docs/overview.html)
- [Earlier post: the longer agentic day](https://vouchfx.io/blog/2026/07/28/agentic-path-m0-m2/)
- [MCP repository](https://github.com/tomas-rampas/vouchfx-mcp)
