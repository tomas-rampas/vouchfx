# Governance

This document describes how decisions are made in the vouchfx project: who decides what enters the engine,
how providers move between governance tiers, how contributors gain commit rights, and how disputes are
resolved. It exists so the rules are known in advance, not discovered during a disagreement.

Everything here governs the open-source project. The permanent open-source feature boundary is published on
the [roadmap](docs/roadmap.md).

## The three provider tiers

Step providers are governed across three tiers. All three are Apache-2.0, so a provider can move between
tiers without intellectual-property friction.

| Tier | What it means | Who decides |
|---|---|---|
| **Core** | Ships with the engine, maintained by the platform team, covered by the engine's CI gates (including the memory-leak gate over the full provider closure). | The platform team decides what enters Core. |
| **Verified** | Community-authored, hosted in the [provider hub](https://github.com/tomas-rampas/vouchfx-providers), promoted by meeting the published rubric below. | Any maintainer may promote a provider that meets the rubric; the rubric — not judgement — is the gate. |
| **Community** | Listed in the provider hub index with no implied endorsement. | Open to everyone; announcement is a pull request to the index. |

## The Verified-tier promotion rubric

Promotion to Verified is a checklist, not a conversation. The minimum bar:

- The provider's integration-test fixture passes on the official matrix: the engine's `main` plus the two
  preceding minor versions.
- The provider's README contains worked examples covering at least three realistic use cases and a
  known-limitations section.
- The security checklist is signed off: credential handling reviewed for correctness, transitive dependency
  vulnerabilities scanned (zero high-severity at promotion), TLS defaults inspected, no telemetry phoning
  home, package signature verified.
- The licence is Apache-2.0 (or compatible) and the contributor has signed off via the Developer Certificate
  of Origin (DCO).
- The provider declares a `MinEngineVersion` compatible with the engine's current major version.
- At least one platform-team maintainer has read the emitted CSX for the provider's representative steps and
  confirmed it follows the CsxFragment composition contract (Architecture Blueprint §13.3.1).

A provider that does not yet meet the rubric remains in the Community tier, and the rubric itself is the
actionable feedback for what needs work. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full authoring
guide.

## Contributor sign-off

Contributions are accepted under the **Developer Certificate of Origin** (DCO) — sign your commits with
`git commit -s`. The DCO was chosen over a Contributor Licence Agreement deliberately: it is lighter-weight,
requires no separate signing infrastructure, and matches .NET Foundation practice.

## Commit rights

- The platform team maintains the engine repository and holds final review on engine changes; the frozen v1
  contracts (language schema, provider SDK surface, event-wire) are enforced by golden-file CI gates that no
  contribution may weaken.
- Verified-tier provider authors gain commit rights to the providers repository after their **third merged
  pull request**.

## Dispute resolution

Serious but good-faith disputes — a contested promotion decision, a rejected engine change, a conduct
concern that is not a Code of Conduct violation — are handled through a written process before anything
escalates: state the disagreement in an issue, the platform team responds in writing with reasons, and the
exchange stays on the record. Code-of-Conduct matters follow [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md);
security matters follow [`SECURITY.md`](SECURITY.md).

Decision authority deliberately rests with the platform team while the project establishes its quality bar.
Broader structures (a foundation, elected stewards) become appropriate if and when the project's scale
warrants them — moving to one would itself be a documented, public decision.
