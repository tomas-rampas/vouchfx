---
date: 2026-07-18
authors:
  - tomas-rampas
categories:
  - Releases
tags:
  - .NET Aspire
  - Testcontainers
---

# vouchfx v1.0.0-alpha.9: Step Timeout Enforcement

v1.0.0-alpha.9 is released. This is a single-fix correctness release addressing step `timeout` field enforcement. The engine now enforces declared timeouts as an upper bound on every step, regardless of verify mode, matching the documented behaviour.

<!-- more -->

## What changed

**Step `timeout` is now enforced for IMMEDIATE steps** ([#232](https://github.com/tomas-rampas/vouchfx/issues/232)). The `.e2e.yaml` DSL has always documented `timeout` as an upper bound on the step, but the engine previously wired it only for the RETRY polling window. Every step's compiled body now runs inside a per-step cancellation scope.

When a step timeout elapses:

- Providers that observe the cancellation token have their client calls cut when the budget elapses.
- A step body that ignores the token but completes past the budget has its outcome superseded.
- Either way, the step resolves as **Inconclusive** (`step-timeout`), never Fail, mirroring RETRY window semantics.

A declared `timeout` becomes the step's governing bound — it replaces the provider's built-in transport timeout (previously hard-coded as 30-second HTTP / 5-second AWS conventions). So `timeout: 90s` now genuinely means ninety seconds. With no timeout declared, behaviour is unchanged — provider transport conventions remain the de facto bound.

RETRY semantics are preserved: the window bounds the poll, per-attempt transport conventions still bound each attempt, and an in-flight attempt is now cut at the window's edge where the client supports cancellation.

## Contracts

No changes to language schema, SDK interface, or event-wire contract.

## Links

- [GitHub Release](https://github.com/tomas-rampas/vouchfx/releases/tag/v1.0.0-alpha.9)
- [Full Changelog](../../changelog.md#100-alpha9-2026-07-18)
