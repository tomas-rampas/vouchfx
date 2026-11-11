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
> only (`${secret:source/path}`) and resolves them at run time, never at compile
> time, so well-formed suites should never *contain* secret values (see "Secrets posture"
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

## Published 1.2.1 tool and GHSA-473q-m89c-ghf8

The published NuGet tool `vouchfx` 1.2.1 (repository commit
`204267fdfef8eb139a4cd88e08ea917e97bf7668`) bundles
`tools/net8.0/any/MySqlConnector.dll` with FileVersion 2.4.0 and ProductVersion
`2.4.0+76f8c519f746d65b1847a5bf5f168a2744589958`. The nuspec has no
`<dependencies>` because the package type is `DotnetTool`; the bundled DLL is
the shipped library. FileVersion 2.4.0 is inside
[GHSA-473q-m89c-ghf8](https://github.com/advisories/GHSA-473q-m89c-ghf8)
(NuGet `MySqlConnector`, vulnerable range `< 2.6.1`, first patched version
2.6.1, severity medium).

The advisory states that with `SslMode` set to `VerifyCA` or `VerifyFull` and
no configured CA certificate, an unvalidated TLS connection is treated as
secure and a clear-text password can be sent before the certificate is
checked. Its impact is a man-in-the-middle proxy that presents any
self-signed certificate and captures the password of a client whose
connection string uses `VerifyCA` or `VerifyFull`, omits `SslCa`, and uses a
non-empty password. A configured `SslCa`, CA-validated TLS, local connections
(Unix socket or loopback) and empty passwords are not affected. `SslMode`
`Preferred` or `Required` is out of scope. Users are affected only when their
connection strings meet those preconditions. No `SslMode` or `VerifyFull`
setting was found under `Vouchfx.Steps.DbAssert.Mysql`; connection strings may
come from the suite or the system under test.

On 1.2.1, avoid `VerifyCA` or `VerifyFull` without a configured CA (set
`SslCa`), or upgrade to 1.3.0 or later once published (those releases include
MySqlConnector >= 2.6.1).

This notice records the library version inside that published package and the
advisory's own conditions. Report newly discovered issues through the private
channels above.

## Published v1.x tools and GHSA-f7vh-h5g3-7jrh

Every published `vouchfx` tool in the v1.x series bundles
`Aspire.Hosting` and `Aspire.Hosting.AppHost` 13.4.2 (the DLLs), and later
v1.x releases will bundle a 13.4.x release of both, all of which are inside
the advisory's range. The source pin is 13.4.2 at every v1.x release tag; the
published packages for v1.0.4, v1.1.0, v1.2.0 and v1.2.1 were inspected. The
tool bundles no Aspire dashboard binaries. 13.4.2 is inside
[GHSA-f7vh-h5g3-7jrh](https://github.com/advisories/GHSA-f7vh-h5g3-7jrh)
(NuGet `Aspire.Hosting` and `Aspire.Hosting.AppHost`, vulnerable range
`< 13.6.5`, first and only patched version 13.6.5, severity high). The
advisory describes a macOS Aspire dashboard cookie leak via a YARP link.

The fix is not available in v1.x. Aspire 13.6.0 and 13.6.5 require
YamlDotNet 18.1.0 or later, and YamlDotNet is held at 16.x for the whole v1.x
series because `IStepBinder<T>.Bind(YamlNode, ...)` is part of the frozen
provider contract. Aspire 13.5.0 to 13.5.4 work with YamlDotNet 16.3.0 but are
not patched, so v1.x stays on the 13.4.x patch line. The decision was made on
11 November 2026. Moving to 13.6.5 is a v2 engine change.

The affected path is judged unreachable. This is an inference from the code,
not a tested result. The reasons:

- The only production construction site of the Aspire host
  (`HeadlessTopology`) sets `DisableDashboard = true`. That flag can only be
  set in code: no configuration key or environment variable maps to it, and
  the host passes empty arguments.
- The tool ships no dashboard binaries. The CLI project sets
  `SkipAddAspireDefaultReferences=true`, and its build targets set a
  placeholder dashboard path rather than shipping the dashboard.
- vouchfx adds no YARP resource of its own. A suite can declare a YARP image
  under `services:`, but vouchfx still starts no dashboard alongside it.

Scanners that read the tool's `deps.json` or inspect the bundled DLLs will
flag the bundled `Aspire.Hosting` or `Aspire.Hosting.AppHost` 13.4.x against
this advisory. That finding is expected for every v1.x tool; it does not mean
the dashboard is running.

The repository builds with a single NuGet audit suppression for this advisory
in the root `Directory.Build.props`, approved by the maintainer on
11 November 2026. A guard test fails CI if any other audit suppression
or audit-level override is added to the repository's build files or
workflows, if a production Aspire host is constructed
outside `HeadlessTopology` or the dashboard is not disabled, if a YARP
package is referenced, or if `AddYarp` is called in engine source. The
suppression is removed when Aspire reaches 13.6.5 or later. Dependabot will
still attempt a security update to 13.6.5 (a pull request that cannot pass, or
an update error on the alert); it cannot succeed while YamlDotNet is held, and
the repository's Dependabot alert stays open.

What to do:

- If you run your own Aspire dashboard (your own AppHost or the Aspire CLI),
  upgrade it to 13.6.5 or later; vouchfx does not constrain its version.
- vouchfx passes no Aspire dependency to consumers of its library packages.
- Treat a scanner finding for the bundled `Aspire.Hosting` or
  `Aspire.Hosting.AppHost` 13.4.x as known, with the basis above.

This notice records the library version inside the published packages and the
basis for the judgement. Report newly discovered issues through the private
channels above.

## Scope

### In scope

Security issues in the parts of this repository the project maintains:

- **The engine** (`Vouchfx.Engine.*`): compilation (parser → AST → CSX →
  Roslyn), orchestration (Aspire AppHost / Testcontainers), execution (the
  collectible `AssemblyLoadContext` host, Polly resilience), and reporting.
- **The Core providers** (`Vouchfx.Steps.*` under `src/Providers/Core`):
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

- **Third-party providers** (any provider not shipped with the engine,
  whether or not it carries the Vouched badge). These are the responsibility of their respective
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
- Secrets resolve **at run time, never at compile time**, so values are
  never baked into the emitted IL or the reproducibility envelope (which hashes
  the *reference*, never the value). The exact run-time moment belongs to the
  field: a step's own field resolves as that step executes, while
  `security.clientKeyPassword` resolves at first use of the certificate material
  it unlocks — after the topology is up and before any step runs. *Never at
  compile time* is the property in scope for a report; *at step execution* is
  true of a step's fields only and is not the guarantee being claimed here.
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
