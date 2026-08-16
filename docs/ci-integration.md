# CI integration reference

vouchfx ships two first-party CI integrations that run a `.e2e.yaml` suite end-to-end against an
orchestrated container topology and publish JUnit and HTML artefacts:

- a **reusable GitHub Actions workflow** (`.github/workflows/vouchfx-run.yml`), and
- an **`include`-able GitLab CI/CD template** (`ci/gitlab/vouchfx-run.gitlab-ci.yml`).

They are behavioural equivalents: the same inputs (as workflow inputs or CI/CD variables), the same
build-from-source install, the same exit-code gating, and the same artefact-upload behaviour.

For a minimal copy-paste starting point, see the CI recipes in
[Recipes](recipes.md#ci-integration-with-github-actions). This page is the complete reference.

---

## Shared behaviour

### Exit-code gating (verdict taxonomy §12.1)

Both integrations respect the four-verdict taxonomy so CI can tell infrastructure breakage apart from
a product defect:

| Exit code | Verdict | Breaks CI? | When |
|---|---|---|---|
| **0** | Success — Pass, or EnvironmentError/Inconclusive when not opted in | – | `run` always; `plan` when no gaps or `--fail-on-gap` not set |
| **1** | **Fail** — one or more scenarios failed (a genuine defect) | **Always** | `run` only |
| **2** | UsageError — unrecognised option, bad arguments, missing path | Always | All commands |
| **3** | EnvironmentError (run) or catalogue error (tools) — unhealthy container, image-pull/seed failure, or incomplete provider metadata | Only when opted in (`run`) — **except** an unconfirmable `security:` declaration, which always breaks CI (see below) | `run` with `--fail-on-env-error`; `list`, `schema`, `validate`, `scaffold`, `plan` on metadata failure |
| **4** | Inconclusive — timeout, partition outlasted grace, unmet capture | Only when opted in — **except** an unconfirmable `security:` declaration, which always breaks CI (see below) | `run` with `--fail-on-inconclusive`; `run` unconditionally if every scenario fails to parse |
| **5** | Gaps found — the Planner detected at least one coverage or vocabulary gap AND the caller opted in | Only when opted in | `plan` with `--fail-on-gap` |

The distinction lets CI systems handle each outcome independently: fail the build on a product `Fail`,
page on-call for `EnvironmentError`, and escalate `Inconclusive` to reliability engineering.

**Unconditional exceptions.** Two outcomes break CI whatever the opt-in flags say.

A `run` in which *every* discovered scenario fails to parse (malformed YAML, unknown step types across
the board) is classified Inconclusive and exits 4 regardless of the opt-in flag, matching the behaviour
of `vouchfx validate`.

A suite that declares a `security:` block the engine **cannot confirm** exits non-zero with no
`--fail-on-env-error` and no `--fail-on-inconclusive`.

#### What "cannot confirm" means

A run vouches for a declared `security:` block only when it confirmed **every** target that block
declares — each secured endpoint reached, over the transport the declaration names, with the material
the declaration names. Two shapes raise:

- the post-health-gate **confirmation probe ran and measured the declaration not to hold**. The probe
  connects to the declared endpoint with exactly the material a step would use, so anything that stops
  it completing that connection lands here: the endpoint refuses the connection or does not speak TLS,
  its certificate does not chain to the declared `caCert`, it refuses the declared client certificate,
  or the declared client identity cannot be **loaded** at all — a well-formed `clientKeyPassword` that
  cannot be resolved, that resolves to an empty value, or that does not decrypt the key. The run
  aborts before any step executes.
- **something refused the document** before it could be confirmed **and left some declared target
  unconfirmed**. Nothing downstream of a refusal runs, so the declaration is never validated, never
  probed and therefore never confirmed. Both halves carry weight: a refusal beside siblings whose
  probe went on to confirm every declared target leaves nothing unconfirmed and does not raise (see
  *What does not break CI* below).

Ending without that confirmation is not by itself the rule, and one shape sits outside both bullets: a
topology that came **up** and then failed its health gate leaves the declaration unprobed, and a run
whose **only** fault is that gate still exits 0. That is the deliberate exception recorded under
*What does not break CI* below.

**What the refusal was about is not consulted, and that is the whole of the rule.** The engine does
not ask whether the fault was a security fault; it asks whether the refusal left a declared target
unconfirmed. So a schema error on an unrelated step, an unresolvable `script.csharp` `file:`, a
`${conn:…}` naming no dependency, a step-level `${secret:…}` the engine cannot resolve, a target
addressed by two protocol families, a certificate path that escapes the suite directory or does not
exist, a `clientKeyPassword` that is not one whole `${secret:<source>/<path>}` reference — every one
of these is an **instance** of the property, and none of them is a definition of it. The rule holds
for causes not named here, and this page does not maintain a list of them. The practical consequence
is the one to plan for: **in a suite that declares `security:`, an authoring fault the engine can
locate in a document it parsed reddens it when it stops that declaration being confirmed — whatever
that fault was about — where the same fault in an unsecured suite still exits 0.**

The qualification "in a document it parsed" is the one shape outside that sentence, and it is a
measured hole rather than a nuance of the rule: a file whose **YAML** the engine cannot read or parse
binds no declaration it can see, so it raises nothing of its own. Read "parsed" strictly — a document
whose YAML parses and which is then refused for its *contents* (an unknown step type, say) has bound
its `environment` block, so it is inside the sentence and does raise. See the parse bullet under
*What does not break CI*.

A rejection located **at or inside** the declaration counts even when the declaration is malformed
enough that no block binds at all: `security: mtls` — the profile name written where the block
belongs — reddens the run rather than passing as a document that declared nothing.

#### The exit code is the run's own verdict code

The rule decides whether a verdict breaks CI. It never changes the verdict, and it introduces no new
code — a pipeline keying on the taxonomy reads the same outcome it always did:

| The run's verdict | Exit | The usual shape behind it for a secured suite |
|---|---|---|
| Inconclusive | **4** | the document was refused on an authoring fault — the common case for anything caught before the topology exists |
| EnvironmentError | **3** | the confirmation probe measured the declaration not to hold; or a suite-level guard refused the suite with an environment error |
| Fail | **1** | unconditional already; the rule changes nothing here |

A refusal is therefore not automatically a 4 — the fault decides the verdict and the verdict decides
the code. Measured on the built CLI with neither gating flag, both rows on the default path: a secured
suite whose scenarios declare **different `environment` blocks** exits **3**, while one whose
scenarios **resolve their declared security paths against different directories** exits **4**. Both
guards exist because the scenarios of one suite share a single topology, so neither applies under
`--parallel`, where each scenario builds its own topology and resolves its declared paths against its
own directory.

#### The exit code is never the only evidence

Every raising path except a failed probe prints, after the fault it did report:

> This suite declares a 'security' block that this run could not confirm, so it exits non-zero
> whatever the fault reported above was: a run that cannot confirm a declared security assertion
> cannot vouch for it. Each door reports only the faults it reached, so what is reported above need
> not be the last fix before a run can confirm this suite's security block.

A failed probe is the exception because it already reports a measured security failure in its own
words. Note the reach: this line goes to **stdout only** — it is in neither `--junit` nor `--events`,
so a job that reads only the machine-readable artefacts sees a bare non-zero exit and nothing else.

#### What does *not* break CI

- **A topology that came up and then failed its health gate, when that is the run's *only* fault.**
  Such a suite exits 0 by default, with or without a `security:` block, exactly as any other
  environment error does. The qualification is load-bearing rather than cautious: a health-gate
  failure never *clears* a refusal the same run already recorded, so a secured suite in which one
  scenario was refused on an authoring fault before the topology failed its gate exits non-zero on
  that refusal. Measured on the built CLI with neither gating flag: a two-scenario secured suite
  whose first scenario omits a required `method:` and whose topology then fails to provision exits
  **3**, where the identical pair with no `security:` block exits **0**. Narrowing the only-fault
  case is [issue #390](https://github.com/tomas-rampas/vouchfx/issues/390); until it lands, an
  unhealthy container in a secured suite that is otherwise clean can leave the declaration unprobed
  and the pipeline green.
- **Every other cause of an environment error** — an unhealthy container, an unpullable image, a seed
  failure unrelated to security — raises nothing of its own, and a run whose only fault is one of
  them still exits 0 by default.
- **The same document with no `security:` block.** Measured on pairs differing in nothing else, both
  run paths: the secured document exits 4 where the unsecured one exits 0.
- **A scenario whose *YAML itself* cannot be read or parsed** — a malformed document, a file the
  runner cannot read, a file over the 1 MiB document cap. Nothing binds for such a file, so it cannot
  be shown to declare anything: it never reaches this rule and never prints the security line. Where
  *every* scenario fails that way the run still exits 4, through the parse rule above rather than
  this one; beside a parseable sibling it contributes nothing and the suite's answer comes from the
  siblings alone — measured, a directory pairing a **malformed-YAML** secured file with an unsecured
  one carrying a step-secret fault exits **0** with no security line, on both run paths. That
  residual is [issue #411](https://github.com/tomas-rampas/vouchfx/issues/411)'s amended acceptance
  and is pinned by a test asserting today's behaviour, so closing it turns that test red. Closing it
  would take a raw-YAML scan for a `security:` key — a second way of asking "does this document
  declare security" that can disagree with the parsed one — and failing closed instead would redden
  every unsecured suite that merely contains an unreadable file.

  **A document that parses and is then refused for its *contents* now does raise**, and that is the
  half of #411 that closed: an unknown step type, a duplicate step id, anything `AstBuilder` rejects.
  Such a document binds its `environment` block before it is refused, so what it declared is known,
  and a `security:` block in it is accounted for even though the document never runs. Measured on the
  built CLI with neither gating flag, both run paths: a directory pairing a secured file carrying an
  unknown step type with an unsecured sibling carrying a step-secret fault exits **4** and prints the
  security line, where the same pair with no `security:` block exits **0**.

  **Including when the `security:` node is malformed enough to bind nothing** — `security: mtls`, or a
  bare `security:` whose children are commented out. Those bind no block for the walk above to find,
  so they are caught the same way they are in a document that *did* become a scenario: by the schema,
  which reports the error at the `security` node itself. Measured, both run paths: either spelling in
  the unbuildable file of that same pair exits **4** with the security line, where the control — the
  identical file with no `security:` node at all, whose schema error sits on the step — exits **0**.
  Before this, either spelling exited **4** *alone* (through the parse rule) and **0** beside a
  parsing sibling, so adding an unrelated broken file to the suite made the pipeline greener.

  **`--tag` and `--owner` reach these documents now, and that is a behaviour change for a filtered
  job.** Selection runs before the split that hands them to the rule above and matched on the *built*
  AST's `metadata`, which such a document does not have — so every metadata filter excluded it, and it
  contributed no declaration. Measured: the pair above with a `smoke`-tagged sibling exited **4** under
  a bare `run` and **0**, silently, under `run --tag smoke`. The `metadata` block binds alongside the
  `environment` block and is now recovered with it, so a filtered job that used to skip an unbuildable
  file now reports it as Inconclusive and reddens if it declares a `security:` block the run cannot
  confirm. A document whose recovered tags genuinely do not match is still excluded.
- **A refusal in a suite the run confirmed anyway.** The rule asks what was *confirmed*, not merely
  what refused, so a scenario refused in a shared-topology suite whose probe went on to confirm every
  declared target is not by itself unconfirmable — what remains is an ordinary authoring fault, gated
  by `--fail-on-inconclusive` like any other. Two limits on that. A refusal located **at or inside the
  declaration itself** raises on its own and no later confirmation forgives it — `security: mtls`
  reddens the run whatever the probe went on to confirm. And confirming *some* of what was declared is
  not confirming it: a suite declaring two secured targets, one confirmed and one not, still breaks CI.

The reasoning behind the carve-out: an unconfirmable security assertion is not an infrastructure flake
the way a failed image pull is. It is an assertion the author explicitly wrote into the suite, and
treating it as opt-in-only would hand a team that forgot a flag a green pipeline on a security suite
that verified nothing.

### Artefacts

Both integrations **always publish reports** — via `if: always()` on GitHub Actions and `when: always`
on GitLab — so artefacts are available precisely when a suite does not pass:

- **`results.xml`** — JUnit XML for CI ingestion. The four verdicts map to distinct JUnit primitives
  (Fail → `<failure>`, EnvironmentError → `<error>`, Inconclusive → `<skipped>`). On GitLab this is
  surfaced natively in the pipeline and merge-request test-report UI.
- **`report.html`** — a self-contained HTML report with polling timelines, captured-variable
  provenance, failed-step diffs and the reproducibility envelope, with no secret values embedded.

### Installation model

The vouchfx CLI is packaged as a `dotnet` global tool (`ToolCommandName: vouchfx`), live on NuGet.org:

```bash
dotnet tool install --global vouchfx --prerelease
```

That is the recommended path for local use. **Both CI integrations deliberately build from source** at
the pinned ref instead, keeping the runner's engine in lock-step with the workflow/template definition
you pinned — pin a release tag or SHA to track a released engine.

The tool depends on the Aspire orchestration packages being present in the per-user NuGet cache; any
developer or CI environment that has built an Aspire app or resolved the engine's dependencies has
them. For machines with only the OS and no .NET SDK, use the self-contained per-OS archives and
installers attached to each [GitHub release](https://github.com/tomas-rampas/vouchfx/releases).

### The floating convenience tags

Three maintainer-moved tags exist as a convenience tier, maintained by
[`.github/workflows/move-floating-tag.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/.github/workflows/move-floating-tag.yml),
which force-moves them to each published release's commit:

| Tag | Tracks | State |
|---|---|---|
| `v1-alpha` | `v1.0.0-alpha.N` and `v1.0.0-beta.N` | Retired at `v1.0.0-alpha.10` — never deleted, simply no longer moved. |
| `v1-rc` | `v1.0.0-rc.N` | Current pre-GA tag. Tracks the latest release candidate; currently points at `v1.0.0-rc.3`. Moving to v1 at GA is an explicit edit. |
| `v1` | `v1.y.z` GA releases only | Starts moving once v1.0.0 ships. |

**Each pre-GA line gets its own tag on purpose.** A consumer who pinned `v1-alpha` chose the alpha
line; force-moving them onto a release candidate would change the engine under them without a ref
change on their side, and leave the tag name describing something it no longer points at. Moving
between lines is therefore an opt-in edit: switch `v1-alpha` → `v1-rc` when you are ready, and
`v1-rc` → `v1` at GA.

None of the three is a production-grade pin — see [Supply-chain hygiene](#supply-chain-hygiene).

---

## GitHub Actions

### Quick start

```yaml
jobs:
  vouchfx-e2e:
    # Convenience tier — good for a first try or a low-stakes repo.
    # See "Supply-chain hygiene" for the SHA-pinned production tier.
    uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@v1-rc
    with:
      scenario-path: ./tests/e2e
      fail-on-env-error: false
```

### Workflow inputs

| Input | Type | Default | Purpose |
|---|---|---|---|
| `scenario-path` | string | `.` | Directory (relative to the caller's checkout) to search recursively for `.e2e.yaml` scenarios. |
| `vouchfx-repo` | string | `${{ github.repository }}` | The `owner/repo` of the vouchfx repository to build from source. Override to track a fork, or to pin a released version. |
| `vouchfx-ref` | string | `${{ github.sha }}` | The git ref (commit SHA, tag or branch) of `vouchfx-repo` to build. Recommended: a full commit SHA for supply-chain repeatability. |
| `dotnet-version` | string | `8.0.x` | The .NET SDK version to install. vouchfx targets .NET 8 LTS. |
| `fail-on-env-error` | boolean | `false` | When `true`, an environment-error verdict fails the job with exit code 3. |
| `fail-on-inconclusive` | boolean | `false` | When `true`, an inconclusive verdict fails the job with exit code 4. |
| `prewarm-images` | string | (empty) | Optional newline-separated list of container images (one per line) to `docker pull` before the run, warming the Docker cache and mitigating Aspire/DCP's ~20-second per-resource cold-start watchdog. Each pull is best-effort and non-fatal. |
| `setup-script` | string | (empty) | Optional path (relative to the checkout) of a shell script run on the same runner before the suite, to produce fixture files the suite declares but does not ship — most often the certificates and key or trust stores a `security:` block names. It runs as `bash <path>`, so no executable bit is needed; a named-but-missing script fails the job. See [the mutual-TLS example](https://github.com/tomas-rampas/vouchfx/blob/main/examples/security-mtls.e2e.yaml). |
| `runs-on` | string | `ubuntu-latest` | The runner label to use. Must provide Docker; `ubuntu-latest` does. |

### Worked example

[`.github/workflows/vouchfx-run-reference.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/.github/workflows/vouchfx-run-reference.yml)
calls the reusable workflow against this repository's own minimal reference suite
(`examples/ci-reference/smoke.e2e.yaml`), proving the workflow runs a real suite green and publishes
artefacts end-to-end.

---

## GitLab CI

### Quick start

```yaml
include:
  - project: tomas-rampas/vouchfx
    # Convenience tier — see "Supply-chain hygiene" for the SHA-pinned tier.
    ref: v1-rc
    file: /ci/gitlab/vouchfx-run.gitlab-ci.yml

vouchfx-run:
  variables:
    VOUCHFX_SCENARIO_PATH: ./tests/e2e
    VOUCHFX_FAIL_ON_ENV_ERROR: "false"
```

### Configuration variables

| Variable | Type | Default | Purpose |
|---|---|---|---|
| `VOUCHFX_SCENARIO_PATH` | string | `.` | Directory (relative to the project root) to search recursively for `.e2e.yaml` scenarios. |
| `VOUCHFX_REPO_URL` | string | `$CI_REPOSITORY_URL` | Git URL of the vouchfx repository to build from source. Defaults to the calling project; override to track a fork. |
| `VOUCHFX_REF` | string | `$CI_COMMIT_SHA` | Git ref (commit SHA, tag or branch) of `VOUCHFX_REPO_URL` to build. |
| `VOUCHFX_DOTNET_IMAGE` | string | `mcr.microsoft.com/dotnet/sdk:8.0` | .NET 8 SDK container image the job runs in. |
| `VOUCHFX_FAIL_ON_ENV_ERROR` | string | `"false"` | When truthy, an environment-error verdict fails the job with exit code 3. |
| `VOUCHFX_FAIL_ON_INCONCLUSIVE` | string | `"false"` | When truthy, an inconclusive verdict fails the job with exit code 4. |
| `VOUCHFX_PREWARM_IMAGES` | string | (empty) | Optional whitespace/newline-separated list of container images to pre-warm. Pin each entry to an immutable digest. |
| `VOUCHFX_SETUP_SCRIPT` | string | (empty) | Optional path (relative to the project root) of a shell script run before the suite, to produce fixture files the suite declares but does not ship — the GitLab counterpart of the `setup-script` input above. A named-but-missing script fails the job. |

### Docker-in-Docker and the privileged-runner requirement

vouchfx stands up an Aspire/Testcontainers container topology, so the job needs a Docker daemon. The
template uses the standard GitLab **Docker-in-Docker (dind)** pattern with a `docker:dind` service.

**Caveat:** dind requires a **privileged runner**. The `docker:dind` service only starts on a GitLab
Runner configured with `privileged = true` (Docker executor) or an equivalently-privileged Kubernetes
executor. gitlab.com's shared SaaS Linux runners provide this; a self-managed runner must be
explicitly configured for it.

**Alternative for a non-privileged runner.** Use a **socket-bind runner**: mount the host daemon socket
into the build (`volumes = ["/var/run/docker.sock:/var/run/docker.sock", …]` in the runner config),
then drop the `services:` block and the dind/TLS variables, and set
`DOCKER_HOST: "unix:///var/run/docker.sock"`. Socket-bind trades isolation for not needing privileged
mode — choose per your security posture.

### Verification status (important)

The GitLab template is **static-validated only** — yamllint, the GitLab CI JSON schema, and a
behavioural-equivalence cross-check against the GitHub workflow. It has **not been run on a live
GitLab instance**; a live pipeline / `ci/lint` run is an infrastructure-gated follow-up.

The one substantive risk to verify when running live is whether vouchfx's **Aspire/DCP-managed
containers are reachable under sibling Docker-in-Docker**. The template sets
`TESTCONTAINERS_HOST_OVERRIDE=docker`, but DCP may resolve endpoints differently than raw
Testcontainers — that dind-to-DCP networking is the primary unknown.

See [`ci/gitlab/vouchfx-run.gitlab-ci.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/ci/gitlab/vouchfx-run.gitlab-ci.yml)
and [`ci/gitlab/README.md`](https://github.com/tomas-rampas/vouchfx/blob/main/ci/gitlab/README.md)
for implementation details.

---

## Supply-chain hygiene

For production use, pin everything to something immutable.

**GitHub Actions:**

1. **Pin the `uses:` reference to a full commit SHA**, not a moving branch or tag:
   ```yaml
   uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0 # v1.0.0-rc.3
   ```
   A branch or tag ref — including the `v1-alpha`/`v1-rc`/`v1` convenience tags — lets the workflow definition
   change underneath you; a SHA is immutable. The trailing `# vX.Y.Z` comment is not decorative: it is
   what lets Dependabot track and bump the pin automatically.
2. **Pin `vouchfx-ref` to a commit SHA or release tag**, never a branch.
3. **Pin each `prewarm-images` entry to an immutable image digest**:
   ```yaml
   prewarm-images: |
     traefik/whoami@sha256:abc123...
     postgres@sha256:def456...
   ```

**GitLab CI:** the same four rules — pin the `include:` `ref:` to a full commit SHA, pin `VOUCHFX_REF`,
pin each `VOUCHFX_PREWARM_IMAGES` entry to a digest, and pin `VOUCHFX_DOTNET_IMAGE` to a digest
(`mcr.microsoft.com/dotnet/sdk:8.0@sha256:…`) rather than the floating tag.

### Keeping the pin current

**On GitHub**, add a `github-actions` entry to your own `.github/dependabot.yml`:

```yaml
version: 2
updates:
  - package-ecosystem: "github-actions"
    directory: "/"
    schedule:
      interval: "weekly"
```

Dependabot resolves the trailing `# vX.Y.Z` comment on the SHA pin, watches this repository's tags, and
opens a PR bumping both the SHA and the comment whenever vouchfx cuts a release — nobody runs a lookup
by hand, and the pin never drifts silently.

**On GitLab**, there is no built-in equivalent for `include: ref:` entries. The closest automation is a
[Renovate](https://docs.renovatebot.com/) custom regex manager watching this repository's tags and
opening an MR to bump the pinned SHA and its trailing comment.

**Resolving a SHA by hand** (the first pin, or without Dependabot/Renovate):

```bash
git ls-remote --tags https://github.com/tomas-rampas/vouchfx v1.0.0-rc.3
```

Depending on how the release tag was created (both kinds are documented in
[RELEASING.md](https://github.com/tomas-rampas/vouchfx/blob/main/RELEASING.md)), this prints either
*two* lines — `refs/tags/v1.0.0-rc.3` (an annotated tag object's own SHA) and
`refs/tags/v1.0.0-rc.3^{}` (the commit it points at, "peeled") — or a *single* line for a lightweight
tag, whose SHA already **is** the commit. **If a `^{}` line is present, take that one**; otherwise the
single line's SHA is the commit SHA to pin.

---

## See also

- **[Recipes → CI integration](recipes.md#ci-integration-with-github-actions)** — the minimal copy-paste starting point.
- **[Getting started](getting-started.md)** — your first test in 60 minutes.
- **[Troubleshooting](troubleshooting.md)** — Docker not running, the Aspire 20-second cold-start gotcha, and other real failure modes.
