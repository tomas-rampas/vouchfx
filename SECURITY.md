# Security Policy

vouchfx compiles declarative `.e2e.yaml` integration tests into C#, runs them
through Roslyn, and orchestrates container topologies (.NET Aspire +
Testcontainers) to test distributed .NET systems end-to-end. Because it executes
generated code in privileged CI and handles references to secrets, we take
security reports seriously and aim to respond quickly and transparently.

This document describes how to report a vulnerability, what is in and out of
scope, and our coordinated-disclosure expectations.

## Reporting a vulnerability

**Please do not open a public GitHub issue, pull request, or discussion for a
suspected security vulnerability.** Public disclosure before a fix is available
puts every user at risk.

Report privately by **either** of these routes:

1. **GitHub private security advisory (preferred).** Go to the repository's
   **Security → Advisories → Report a vulnerability** page
   (`https://github.com/tomas-rampas/vouchfx/security/advisories/new`). This
   opens a private channel visible only to you and the maintainers, with a
   built-in workflow for coordinating the fix and publishing a CVE.

2. **Email.** Write to **`security@vouchfx.invalid`**.
   > ⚠️ **Placeholder address.** `security@vouchfx.invalid` is a deliberate
   > placeholder — no mailbox is provisioned yet (vouchfx is pre-1.0 adoption
   > stage). Until a real security contact and monitored inbox exist, **use the
   > GitHub private-advisory route above**, which works today. Replacing this
   > placeholder with a real, monitored address is a tracked governance item.

If you cannot use either route, open a *minimal*, non-revealing public issue that
says only "I have a security report, please provide a private contact" — never
include details, reproduction steps, or proof-of-concept in the public channel.

### What to include

A good report lets us reproduce and triage fast. Please include, where possible:

- A clear description of the issue and the **impact** (what an attacker can do).
- The **affected component** and version/commit (engine, a specific Core
  provider, the Provider SDK, or a CI template).
- **Reproduction steps** — ideally a minimal `.e2e.yaml` and/or environment that
  triggers the issue.
- Any **proof-of-concept**, logs, or stack traces (redact your own secrets
  first — see the secrets note below).
- The **configuration** in play: OS, .NET SDK version, Aspire/Docker versions,
  and whether you ran locally, in CI, or against a SaaS/on-prem fabric.
- Your assessment of **severity** and any known mitigations or workarounds.

> 🔐 **Redact secrets before sending.** vouchfx references secrets by reference
> only (`${secret:source/path}`) and resolves them at step-execution time, so
> well-formed suites should never *contain* secret values (see "Secrets posture"
> below). If your reproduction nonetheless surfaces a credential in a log or
> dump, redact it before sending us the report.

## Our commitment (coordinated disclosure)

When you report privately, we will:

| Stage | Target |
| --- | --- |
| **Acknowledge** receipt of your report | within **3 business days** |
| **Initial assessment** (validity, severity, scope) | within **7 business days** |
| **Status updates** while we work a confirmed issue | at least every **7 business days** |
| **Fix or mitigation** for confirmed High/Critical issues | targeted within **90 days** of confirmation |

We follow a **coordinated-disclosure** model:

- We will work with you on a disclosure timeline and a mutually agreed
  publication date. Our default embargo is up to **90 days**, sooner if a fix
  ships earlier, and we may extend it for complex fixes — always in
  communication with you.
- We will publish a **GitHub Security Advisory** (and request a CVE where
  warranted) when the fix is released.
- We will **credit** you in the advisory and release notes for the report unless
  you ask to remain anonymous. We do not currently operate a paid bug-bounty
  programme.
- We ask that you give us reasonable time to remediate before any public
  disclosure, and that your testing does not harm users, degrade services, or
  access data that is not yours.

## Supported versions

vouchfx follows the **v1.x engine series**. The v1 public contracts — the
Provider SDK interfaces, the `.e2e.yaml` JSON Schema, and the JSON Lines
event-wire records — are frozen for v1.x and evolve additively only, enforced by
golden-file CI gates. Security fixes land on the latest v1.x release.

| Version | Supported          | Notes |
| ------- | ------------------ | ----- |
| 1.x     | :white_check_mark: | Active development; security fixes land here. |
| < 1.0   | :x:                | Pre-release/adoption builds — upgrade to the latest 1.x. |

When a `0.x`/pre-1.0 series is published, only the latest minor receives security
fixes; please upgrade rather than relying on an older pre-release.

## Scope

### In scope

Security issues in the parts of this repository the project maintains:

- **The engine** (`Platform.Engine.*`): compilation (parser → AST → CSX →
  Roslyn), orchestration (Aspire AppHost / Testcontainers), execution (the
  collectible `AssemblyLoadContext` host, Polly resilience), and reporting.
- **The Core providers** (`Platform.Steps.*` under `src/Providers/Core`):
  `http.rest`, `db-assert.postgres`, `script.csharp`, `mq-publish.kafka`,
  `mq-expect.kafka`, `webhook-listen.http`.
- **The Provider SDK** (`src/Sdk`) — the frozen v1 interface contract.
- **The CI templates and release workflow** we publish for consumers
  (`.github/workflows/`, `ci/`) — a software-supply-chain surface reviewed as
  such. Examples: template injection via untrusted input, secret/
  token exfiltration, or tampering with signed-release provenance.
- The **secrets-handling posture** and the **verdict taxonomy** insofar as a
  defect could leak a secret value or misclassify a security-relevant outcome.

Representative concerns we want to hear about: code-injection through the
YAML→CSX compilation path, escaping the collectible `AssemblyLoadContext`
sandboxing/assembly-graph hygiene, a customer DLL bridging the
`ScriptGlobalVariables` boundary, secret-value leakage into logs/the
reproducibility envelope, container-teardown flaws that orphan resources or
networks, and supply-chain weaknesses in the CI/release templates.

### Out of scope

- **Third-party providers** (Community tier, or any provider not under
  `src/Providers/Core`). These are the responsibility of their respective
  authors; please report to them. We will help coordinate if a shared SDK issue
  is implicated.
- **Vulnerabilities in upstream dependencies** themselves (Aspire, Roslyn,
  Npgsql, Confluent.Kafka, etc.) — report those to the upstream project. If
  vouchfx's *use* of a dependency is what creates the exposure, that is in scope.
- The **system under test**: vouchfx executes your services and your `.e2e.yaml`
  suites; vulnerabilities in *your* application or test fixtures are yours.
- Issues requiring a **malicious maintainer**, physical access to a developer
  machine, or an already-compromised CI runner outside vouchfx's control.
- Reports generated solely by automated scanners with no demonstrated impact,
  best-practice/"hardening" suggestions with no concrete exploit, and social
  engineering of maintainers.

## Secrets posture (context for reporters)

By design, vouchfx never embeds secret *values* in suites or compiled output:

- Suites reference secrets only as `${secret:source/path}` — **never literals**.
- Secrets resolve **at step-execution time, not compile time**, so values are
  never baked into the emitted IL or the reproducibility envelope (which hashes
  the *reference*, never the value).
- Resolution returns a typed `SecretString` with no value-returning
  `ToString()`/`IFormattable`, so accidental interpolation redacts at the source.

If you find a path that violates any of these properties — a secret value
reaching a log, a report, the event stream, or the reproducibility envelope —
that is an in-scope, high-priority report.

## Governance note

The maintainers and review bar for the surfaces above are defined in
[`.github/CODEOWNERS`](.github/CODEOWNERS). The frozen v1 contracts and the
CI/release templates are owned and require review precisely because they are the
highest-impact (every-consumer) and supply-chain surfaces.

---

_This policy is a living document. The security contact address, the team
handles in CODEOWNERS, and the disclosure SLAs will be confirmed as the project
moves from adoption stage to a stable v1 release._
