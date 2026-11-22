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
| **4** | Inconclusive or refusal — timeout, unmet capture or refused test input | Only when opted in for runtime inconclusive; the unconditional checks in [§16.4](01_Technical_Architecture_and_Engineering_Blueprint.md#164-exit-codes) prevent exit 0 | `run` with `--fail-on-inconclusive`, or an unconditional refusal check |
| **5** | Gaps found — the Planner detected at least one coverage or vocabulary gap AND the caller opted in | Only when opted in | `plan` with `--fail-on-gap` |

The distinction lets CI systems handle each outcome independently: fail the build on a product `Fail`,
page on-call for `EnvironmentError`, and escalate `Inconclusive` to reliability engineering.

**Unconditional checks.** The authoritative list is in [§16.4](01_Technical_Architecture_and_Engineering_Blueprint.md#164-exit-codes).
None replaces a code already selected: a failing scenario still exits 1 and a gated environment
error still exits 3. The sections below explain how the checks behave in CI.

**Any parse failure** — a malformed document, a file the runner cannot read, one over the 1 MiB cap.
This does not require that *every* scenario failed: one unreadable file beside a suite that otherwise
passes still exits 4, because the engine cannot say what that file would have asserted. It matches
the behaviour of `vouchfx validate`.

**Typed authoring, schema or compilation refusal** — an invalid document, missing script source,
unresolvable source configuration, incompatible shared declarations or invalid generated CSX prevents
success, even beside a passing sibling. Refusals before acquisition start no topology for that
scenario. Roslyn compilation still happens after acquisition: its refusal retains completion events
and disposal, without claiming that an assertion ran or that no resources were acquired.

Compose's explicit local-daemon, unchanged-input and native ownership policies also prevent success
when refused before journal creation or `up`. Invoking the driver alone does not mean resources were
started. Malformed native output, process failure, snapshot-reload incompatibility and post-startup
readiness/discovery errors retain their existing runtime or infrastructure policy.

The compilation signal comes only from `CompileOnce`. An identically typed exception raised during
script execution, a compile-budget cancellation and caller cancellation are not inferred to be
invalid authored code. Genuine runtime inconclusive results still require `--fail-on-inconclusive`.

**An Inconclusive suite with no executed scenario** retains its existing nonzero check independently.
An unrelated or unattributed topology startup failure carrying `EnvironmentError` is outside that
check and retains `--fail-on-env-error` when it is the only fault. A typed failure of a declared
secured resource's own readiness, discovery or server-material access that prevents confirmation
produces a non-zero default exit instead.
Verdict aggregation is unchanged: `EnvironmentError` ranks above `Fail`. A completed failed
assertion beside that broker's own unconfirmed readiness failure therefore aggregates to
`EnvironmentError` and exits 3. An aggregate `Fail` still exits 1.
A suite carrying both an authoring/provider refusal and an ungated environment error exits 4;
`--fail-on-env-error` selects 3 first. Divergent shared declarations are authoring refusals even when
the existing aggregate verdict is EnvironmentError. Unconfirmable security keeps its separate rule.

**A scenario refused at a provider- or engine-surface guard** — a provider's `Bind`, `Validate`,
`Resources`, `HostResources`, `Emit` or `CompileReferenceAssemblies` threw, or the assembler refused
the CSX fragments a provider emitted. This is a fault in the **testing machinery** and not in your
system under test; a defect the suite observed in the system under test is a `Fail` and exits 1.

**The rule identifies the fault by where it arose, not by whose it is.** Most of what it catches is
a provider defect, and the diagnostic says so — including the assembler guard, which states plainly
that a refused `CsxFragment` is a defect in a provider, while declining to name *which* step, because
the exception it catches does not identify the fragment. But the same guards also catch a
reflective-dispatch failure *before* the provider's own body ran, and a filesystem or out-of-memory
condition that may well be the host's — and for those the engine deliberately declines to blame the
provider. Read the diagnostic for attribution; the exit code only tells you the run cannot be
trusted.

The rule above already covered such a scenario when it was alone in a directory: nothing executed,
so the run exited 4. What it did not cover was the same scenario beside one that runs. Measured, on
both run paths: a document whose provider throws at bind time exited **4** on its own and **0** the
moment a passing scenario joined it in the directory. The exit code was deciding on the sibling
rather than on the defect. It now exits 4 in both arrangements.

The rule keys on **where the failure was raised**, never on the verdict, and that is what keeps the
rest of the taxonomy intact: a scenario that ran and could not conclude — a timeout, a partition
that outlasted its grace, an unmet upstream capture — is still gated behind
`--fail-on-inconclusive`, and a genuine infrastructure fault still keeps `--fail-on-env-error`.
Keying on the verdict instead would have reddened both by default.

The provider/engine rule keeps its own narrower provenance marker. Authoring refusals are covered
independently; an unexpected exception escaping to `--parallel`'s per-slot catch-all is not classified
as authoring merely from its type or message ([issue #486](https://github.com/tomas-rampas/vouchfx/issues/486)).

A suite that declares a `security:` block the engine **cannot confirm** exits non-zero with no
`--fail-on-env-error` and no `--fail-on-inconclusive`.

#### What "cannot confirm" means

A run vouches for a declared `security:` block only when it confirmed **every** target that block
declares — each secured endpoint reached, over the transport the declaration names, with the material
the declaration names. Three shapes raise:

- the post-health-gate **confirmation probe ran and measured the declaration not to hold**. The probe
  connects to the declared endpoint with exactly the material a step would use, so anything that stops
  it completing that connection lands here: the endpoint refuses the connection or does not speak TLS,
  its certificate does not chain to the declared `caCert`, it refuses the declared client certificate,
  or the declared client identity cannot be **loaded** at all — a well-formed `clientKeyPassword` that
  cannot be resolved, that resolves to an empty value, or that does not decrypt the key. The run
  aborts before any step executes.
- a **typed acquisition failure of the declared secured resource itself** prevented confirmation.
  This covers its named readiness gate, endpoint discovery and server-material access. The
  declaration must match that exact resource; an unrelated dependency or ambiguous global
  startup exception is not attributed to it from diagnostic text.
- **something refused the document** before it could be confirmed **and left some declared target
  unconfirmed**. Nothing downstream of a refusal runs, so the declaration is never validated, never
  probed and therefore never confirmed. Both halves carry weight, and the word carrying the second
  half is **scenario**: a *scenario* refused beside siblings whose probe went on to confirm every
  declared target leaves nothing unconfirmed and does not raise, because a suite's scenarios must
  declare a byte-identical `environment` block, so the declaration that probe confirmed is the
  refused scenario's own.

  **A document that never *became* a scenario is the exception, and it is the one that most often
  reddens a real pipeline.** A file that parsed and was then refused for its *contents* — an unknown
  step type, a duplicate step id — is confirmed by nothing: nothing downstream of it ran, and nothing
  ever established that its `environment` block is the one the topology started from. Such a document
  raises whatever its siblings went on to confirm. Read "document" and "scenario" as different words
  wherever this page uses them (see *What does not break CI* below, and in particular the bullet on
  an unbuildable document beside a confirming sibling).

Ending without confirmation is not by itself the rule. Unrelated or unattributed infrastructure
failures remain outside these shapes and retain the opt-in policy described below.

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
that fault was about.**

What that no longer implies is a difference in exit code. An unsecured suite carrying the same fault
also reddens, through the separate no-verdict rule above (#369), so both arms exit 4 and the code
cannot tell them apart. **The discriminator is the security notice**, which prints only for the
secured arm. `SecurityAssuranceMatrixTests` rows 01 and 05–08b use matched controls:
the secured methods assert notice presence, while the unsecured methods assert notice absence;
both assert exit 4. These are presence/absence checks, not exact notice-count assertions. The
unsecured methods were renamed from `…_Unsecured_ExitsSuccess` to
`…_ExitsInconclusiveWithNoSecurityNotice` when the distinction moved.

The qualification "in a document it parsed" is the one shape outside that sentence, and it is a
measured hole rather than a nuance of the rule: a file whose **YAML** the engine cannot read or parse
binds no declaration it can see, so it raises nothing of its own. Read "parsed" strictly — a document
whose YAML parses and which is then refused for its *contents* (an unknown step type, say) has bound
its `environment` block, so it is inside the sentence.

Inside the sentence is not the same as raising, and for this class the difference is deliberate. Such
a document raises on **its own** declaration — the `security:` block it bound, or a `security:` node
the schema rejected — and contributes nothing at all when it declared neither. That is what stops an
unsecured broken file reddening a secured suite it has nothing to do with. See the parse bullet under
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
so the explanatory summary itself is absent from machine-readable artefacts. Separately, the current source adds typed successful `security-confirmation` records to JSONL, HTML and JUnit; those records are available in Core 1.0.7. A successful record cannot stand in for a failed or unperformed probe. See [the event contract](01_Technical_Architecture_and_Engineering_Blueprint.md#1444-security-confirmation-events) for its fields and replay limits.

**The provider- or engine-surface guard rule carries the same kind of line.** It needs one more than
the parse-failure and nothing-executed rules beside it do: those two fire on a run that is *visibly*
broken — nothing executed, or a file the engine could not read — while this rule can fire on a run
whose terminal shows a *passing* scenario, with the refused sibling's own diagnostic one line among
many. Printed exactly when this rule, and no earlier rule, is why the run is non-zero:

> This run exits non-zero because a scenario was refused at a provider- or engine-surface guard
> before it could run, not because of any verdict reported above: that alone makes a run non-zero
> whatever its siblings did, including a scenario that ran and passed. The diagnostic naming what
> failed is printed above; this line exists only to connect it to the exit code.

It follows the security line's own reach — **stdout only**, absent from `--junit` and `--events` — and
the two lines never print together: the security-confirmation carve-out is decided inside `ExitCodes.FromVerdict`
before this rule is ever consulted, so whenever the security line is why the run is non-zero, this one
stays silent rather than printing a second, redundant explanation. It names no step, deliberately: the
diagnostic the guard itself printed above already names the offending step and its provider type where
one exists, and the suite-level CSX-assembly-failure arm has none to give (the exception it catches
does not identify the fragment), so one wording serves both arms truthfully.

#### What does not raise the security-confirmation exception

- **Unrelated or unattributed infrastructure failures, when they are the run's only fault.**
  An unrelated unhealthy dependency, an ambiguous global startup failure or an ordinary seed
  failure still exits 0 without `--fail-on-env-error`. Typed failure of the declared secured
  resource's own readiness, discovery or server-material access is excluded from this exception.
  Infrastructure failure never clears an authoring/provider refusal already recorded.
- **The same document with no `security:` block.** Both arms now exit 4 — the unsecured one through
  the no-verdict rule (#369) — so what separates them is the security notice, not the code. Pinned by
  `SecurityAssuranceMatrixTests` rows 01 and 05–08b, on both run paths.
- **A scenario whose *YAML itself* cannot be read or parsed** — a malformed document, a file the
  runner cannot read, a file over the 1 MiB document cap. Nothing binds for such a file, so it cannot
  be shown to declare anything: it never reaches **this** rule and never prints the security line.
  It reddens the run anyway, through the parse-failure rule above — whether it is alone, or beside a
  parseable sibling, and whether or not anything in the suite declares `security:`. A directory
  pairing a malformed-YAML secured file with an unsecured one carrying a step-secret fault exits
  **4** on both run paths, with no security line, and it is the parse rule that put it there.

  That is a change. This bullet used to record the pair exiting **0**, as
  [issue #411](https://github.com/tomas-rampas/vouchfx/issues/411)'s amended acceptance, with the
  reasoning that failing closed "would redden every unsecured suite that merely contains an
  unreadable file". That cost was subsequently taken deliberately, under #425: an unreadable file is
  one the engine could not read, so it cannot report the run as clean regardless of what it might
  have asserted. `Row09c_SecuredMalformedYamlBesideAParseableSibling_ExitsInconclusiveOnTheUnreadFile`
  pins the current behaviour.

  **A document that parses and is then refused for its *contents* now raises on its own
  declaration**, and that is the half of #411 that closed: an unknown step type, a duplicate step id,
  anything `AstBuilder` rejects. Such a document binds its `environment` block before it is refused,
  so what it declared is known, and a `security:` block in it is accounted for even though the
  document never runs. The qualification is load-bearing: **it raises only for what it itself
  declared**, so an unsecured file refused this way contributes nothing and never reddens a secured
  sibling. Measured on the built CLI with neither gating flag, both run paths: a directory pairing a
  secured file carrying an unknown step type with an unsecured sibling carrying a step-secret fault
  exits **4** and prints the security line. The same pair with no `security:` block also exits
  **4** now, through the no-verdict rule, and prints no security line —
  `Row09b_UnsecuredUnbuildableBesideAParseableSibling_ExitsInconclusiveOnTheUnreadFile` pins it.

  **Including when the `security:` node is malformed enough to bind nothing** — `security: mtls`, or a
  bare `security:` whose children are commented out. Those bind no block for the walk above to find,
  so they are caught the same way they are in a document that *did* become a scenario: by the schema,
  which reports the error at the `security` node itself. Measured, both run paths: either spelling in
  the unbuildable file of that same pair exits **4** with the security line. The control — the
  identical file with no `security:` node at all, whose schema error sits on the step — exits **4**
  too, without that line (`Row09d_SchemaErrorOutsideAnyDeclaration_ExitsInconclusiveOnTheUnreadFile`).
  Before #369 and #425, either spelling exited **4** *alone* (through the parse rule) and **0**
  beside a parsing sibling, so adding an unrelated broken file to the suite made the pipeline
  greener. That asymmetry is gone: an unread file reddens wherever it sits.

  **A metadata-filtered selection now SEES these documents, and that is a behaviour change wider
  than the security rule this page is about.** `--tag`/`--owner` matched on the *built* AST's
  `metadata`, which such a document does not have, so every metadata filter excluded it — silently,
  without even printing its parse error. The `metadata` block binds alongside the `environment` block
  and is now recovered with it, so the filter is answered from what the document actually says. State
  the change as that property, not as its security instance: **a `--tag`/`--owner` job that used to
  skip an unbuildable file now reports it**, and everything that follows from reporting it follows
  here too.

  Three consequences, in the order a pipeline will meet them. **A filter matching only unbuildable
  files now exits 4** through the parse-failure rule, with no `security:` block anywhere in
  the picture — measured on the built CLI: a directory of one `nightly`-tagged unbuildable file with
  no `security:` block and one untagged sibling exits **4** under `run <dir> --tag nightly`, where the
  same command previously selected nothing and returned **0**. **In a mixed selection the file folds
  into the suite verdict as Inconclusive**, so a job passing `--fail-on-inconclusive` reddens on it
  like any other Inconclusive scenario. And **it reddens the security rule above when it declares a
  `security:` block the run cannot confirm** — measured, both run paths: a secured unbuildable file
  **carrying the tag itself**, beside a **likewise-tagged** sibling refused at a compile-time door,
  exits **4** with the security line under `run <dir> --tag smoke`, matching the bare `run`. Both
  files must carry the tag: the security line is printed only when at least one document parsed, so
  tagging the unbuildable file alone still exits 4 — but through the parse-failure rule above rather
  than the security one, a different rule reaching the same code.

  Note where the tag has to be. The change only bites when the **unbuildable file itself** carries
  the filtered tag or owner; a document whose recovered metadata genuinely does not match is still
  excluded, which is the instruction the user gave. With the tag on the *sibling* only,
  `run <dir> --tag smoke` never selects the unbuildable file, so this rule does not reach it and no
  security line prints. **The run's exit code is decided by the selected sibling alone** — and where
  that sibling is itself refused before anything executes, as in the pairing above, that is 4 through
  the no-verdict rule, not 0. No test currently pins the tag-on-sibling-only arrangement; the claim
  here is derived from the selection behaviour plus the sibling's own row, not measured end to end.
- **An unbuildable document whose declared target name a *sibling's* probe confirmed** — the shape a
  working pipeline actually has, and the one a customer is most likely to meet. **A run vouches for a
  declaration only when the probe confirmed *that* declaration**, not merely when something confirmed
  a target of the same name. So an unbuildable secured document is never confirmed by a sibling's
  probe: nothing downstream of it ran, and nothing ever established that its `environment` block is
  the one the topology started from — the shared-`environment` divergence guard walks the *scenarios*
  only, and such a document is by construction not one of them. The broken secured file reddens the
  run whether its sibling carried an ordinary authoring fault or came up and confirmed the same
  target name; **`run` and `run --parallel N` give the same answer**, so no flag decides it. That was
  [issue #415](https://github.com/tomas-rampas/vouchfx/issues/415), and it is **closed** — see the
  behaviour-change note in `CHANGELOG.md`, because the second half of it is a default CI colour that
  moves.

  The mechanism, since it is the reason the rule can be stated that plainly: the engine matches a
  declaration to a confirmation on the declaration's whole **identity** — the target's name and kind
  together with a one-way digest of **everything the `security` block itself declares** — derived by one function
  that both the declaration walk and the probe go through. This page deliberately does not enumerate
  the fields that go into that digest: the enumeration is the function, and a copy of it here is a
  second spelling of it, free to go stale the next time a field joins it. Two documents declaring
  `api`, one asserting `mtls` on 9093 and one asserting `tls` on 8443, therefore no longer satisfy
  each other. No declared security **value** enters that record: the digest is compared, never
  rendered, and never reaches a report, an event or a log.

  One field is worth stating on its own, because its handling is the one a reader would guess wrong.
  A declared `clientKeyPassword` contributes its **reference text** whenever the whole value is one
  `${secret:…}` token — so `${secret:vault/prod-key}` and `${secret:env/DEV_KEY}` are *different*
  identities and do not cross-satisfy each other, which is exactly the collapse this design closes.
  A literal passphrase contributes its presence and nothing more: the one thing that must not be
  hashed is not hashed.
- **A refusal in a suite the run confirmed anyway.** The rule asks what was *confirmed*, not merely
  what refused, so a scenario refused in a shared-topology suite whose probe went on to confirm every
  declared target is not by itself unconfirmable. The independent authoring-refusal check still
  prevents exit 0; confirmation does not make an invalid document runnable. Two security limits remain. A refusal located **at or inside the
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
  surfaced natively in the pipeline and merge-request test-report UI. Each `<testcase>` `time` is
  the scenario's wall-clock duration from its `scenario-started` to its `scenario-completed` event,
  script compilation included and topology startup excluded, and the suite `time` is their sum, so
  it is not the run's elapsed time. When the JUnit renderer could not read a line of the event
  stream, the `<testsuite>` carries a `vouchfx.skippedEventLines` property with the number of such
  lines.
- **`report.html`** — a self-contained HTML report with polling timelines, captured-variable
  provenance, failed-step diffs and the reproducibility envelope, with no secret values embedded.
  When the HTML renderer could not read a line, the report states how many after its run summary
  and warns that it may be incomplete.

### Build your test backend

An inline service's `image:` must be available locally or pullable from a registry
under the suite's image-pull policy. Build your custom Dockerfile in an earlier
CI step, or publish the image to a registry the runner can access:

```sh
docker build -t my-backend:e2e -f ./app/Dockerfile.e2e ./app
vouchfx run ./tests/e2e --fail-on-env-error --fail-on-inconclusive
```

That command assumes the suite references `image: my-backend:e2e` and both commands
use the same daemon. For owned Compose environments, declare `build.context` and
optional `build.dockerfile` in the native manifest; acquisition runs Compose with
`--build`. Install the Compose v2 plugin and use a supported local Docker daemon.
The built-in adapter does not support remote TCP/SSH Docker contexts, so do not
assume a Docker-in-Docker template also establishes Compose compatibility. See
[Compose setup and limits](compose-environments.md) for bindings, readiness and
cleanup ownership.

### Installation model

The vouchfx CLI is packaged as a `dotnet` global tool (`ToolCommandName: vouchfx`), live on NuGet.org:

```bash
dotnet tool install --global vouchfx
```

That is the recommended path for local use. **Both CI integrations deliberately build from source** at
the pinned ref instead, keeping the runner's engine in lock-step with the workflow/template definition
you pinned — pin a release tag or SHA to track a released engine.

Published `1.0.0-rc.6` requires matching Aspire **13.4.2** native DCP tools provisioned separately for
the host platform, usually through an AppHost restore; set `ASPIRE_DCP_PATH` to their existing tools
directory. Core `1.0.0` introduced bundled matching native tools for installed distributions on
supported platforms; these need no warmed NuGet cache. Source AppHosts retain their restore/metadata
fallback, and the version they probe is the `Aspire.Hosting` version that pinned ref restored: a
workflow whose `vouchfx-ref` is a commit of current source looks for `aspire.hosting.orchestration.<rid>`
13.4.6 when the embedded path is missing, while `vouchfx-ref: v1.2.1` still looks for 13.4.2. See [version-specific installation prerequisites](getting-started.md#installing-vouchfx).
Current source targets `net10.0`. `global.json` selects SDK 10.0.100 or a later 10.0 feature
band (`rollForward: latestFeature`) and executables set `RollForward=Major`, so building that
source needs the .NET 10 SDK and running it needs a .NET 10 or newer runtime. A published global
tool follows the runtime policy of the release that packed it; releases built before the move to
.NET 10 still run on the .NET 8 runtime. Self-contained archives and
installers on the [latest stable GitHub release](https://github.com/tomas-rampas/vouchfx/releases/latest)
bundle a runtime and do not need a machine-wide SDK, subject to that version's DCP prerequisites.
See [Getting Started](getting-started.md#net-compatibility).

### The floating convenience tags

Three maintainer-moved tags exist as a convenience tier, maintained by
[`.github/workflows/move-floating-tag.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/.github/workflows/move-floating-tag.yml),
which force-moves the matching tag to a release's commit. The tag is moved by the last job of the release
run, which is expected to start only after the release's checks have passed and its packages have been
published, so a release that fails its checks is expected to leave the tag where it was. This applies to
releases tagged on a commit that contains this behaviour. A GitHub Release created or published again for
a tag made before this behaviour was added still moves the tag as soon as it is published, with no checks.

The three tags:

| Tag | Tracks | State |
|---|---|---|
| `v1-alpha` | `v1.0.0-alpha.N` and `v1.0.0-beta.N` | Retired at `v1.0.0-alpha.10` — never deleted, simply no longer moved. |
| `v1-rc` | `v1.0.0-rc.N` | Historical release-candidate line; stable releases do not move this tag. Switch to `v1` explicitly for the stable line. |
| `v1` | `v1.y.z` stable releases only | Stable convenience tag, advanced by published stable Core releases. |

**Each release line gets its own tag on purpose.** A consumer who pinned `v1-alpha` chose the alpha
line; force-moving them onto a release candidate would change the engine under them without a ref
change on their side, and leave the tag name describing something it no longer points at. Moving
between lines is therefore an opt-in edit: switch `v1-alpha` or `v1-rc` → `v1` to select the stable line.

None of the three is a production-grade pin — see [Supply-chain hygiene](#supply-chain-hygiene).

---

## GitHub Actions

### Quick start

```yaml
jobs:
  vouchfx-e2e:
    # Convenience tier — good for a first try or a low-stakes repo.
    # See "Supply-chain hygiene" for the SHA-pinned production tier.
    uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@v1
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
| `dotnet-version` | string | `10.0.x` | SDK used to build and run the suite. Current source needs .NET 10; the workflow also accepts the `8.0` and `9.0` forms, and a `vouchfx-ref` of 1.2.1 or earlier needs the `8.0` form. The reusable workflow and the GitLab template now default to the .NET 10 SDK, and releases that target .NET 10 need it. Keep the workflow or include ref and `vouchfx-ref` / `VOUCHFX_REF` on the same release, or set the SDK explicitly: `10.0.x` / `sdk:10.0` for releases that target .NET 10, and `8.0.x` / `sdk:8.0` for releases up to 1.2.1. Releases up to 1.2.1 need a .NET 8 SDK; 1.4.0 and later need a .NET 10 SDK. A commit pinned by SHA needs the SDK its own `global.json` names. `global.json` refuses an older SDK. Accepted forms include `10.0`, `10.0.x` and a patch such as `10.0.401`. See [Isolated SDK on GitHub Actions](#isolated-sdk-on-github-actions). |
| `fail-on-env-error` | boolean | `false` | When `true`, an environment-error verdict fails the job with exit code 3. |
| `fail-on-inconclusive` | boolean | `false` | When `true`, an inconclusive verdict fails the job with exit code 4. |
| `prewarm-images` | string | (empty) | Optional newline-separated list of container images (one per line) to `docker pull` before the run, warming the Docker cache and mitigating Aspire/DCP's ~20-second per-resource cold-start watchdog. Each pull is best-effort and non-fatal. |
| `setup-script` | string | (empty) | Optional path (relative to the checkout) of a shell script run on the same runner before the suite, to produce fixture files the suite declares but does not ship — most often the certificates and key or trust stores a `security:` block names. It runs as `bash <path>`, so no executable bit is needed; a named-but-missing script fails the job. See [the mutual-TLS example](https://github.com/tomas-rampas/vouchfx/blob/main/examples/security-mtls.e2e.yaml). |
| `runs-on` | string | `ubuntu-latest` | The runner label to use. Must provide Docker; `ubuntu-latest` does. |
| `artifact-name` | string | `vouchfx-reports` | Name of the uploaded reports artefact. Override it when one workflow run calls the reusable workflow more than once, because artefact names are immutable within a run. |
| `telemetry-endpoint` | string | (empty) | Optional telemetry ingest base URL. Telemetry from CI activates only when this, `telemetry-install-id` and the `telemetry-token` secret are all provided (see [Telemetry](telemetry.md)). |
| `telemetry-install-id` | string | (empty) | Optional stable install id: any GUID chosen once per caller repository. An invalid value is ignored with a warning. |

The workflow also takes one optional secret, `telemetry-token`, the bearer token for telemetry uploads. Telemetry stays off without it.

### Isolated SDK on GitHub Actions

`actions/setup-dotnet` v6 first installs the latest LTS runtime (`--channel LTS`); that major can
change without a workflow update. On a hosted runner the extra runtime would let a `net10.0`
executable start even when the job asked for a different major, so a roll-forward failure would stay
hidden. The reusable workflow therefore:

1. Installs `dotnet-version` into `$RUNNER_TEMP/vouchfx-suite-dotnet`, not the runner's default SDK directory.
2. `actions/setup-dotnet` runs before the selector and can fail first, with its own error. If that step is reached and the install succeeded, the selector refuses a value that does not begin with `8.0`, `9.0` or `10.0`, followed by the end of the string or a further version suffix, and emits `The isolated workflow supports .NET 8, 9 and 10 SDKs.` before any SDK directory is removed.
3. Runs the selector with PowerShell 7 (`pwsh`). A self-hosted runner without `pwsh` fails this step.
4. Deletes every other SDK and shared-framework line from that owned tree, after checking the tree is a physical directory directly under `RUNNER_TEMP` and contains no links or reparse points. `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` for the selected line must both be present; an unexpected shared-framework directory aborts before deletion.
5. Runs `dotnet --version` and `dotnet --list-runtimes` from the vouchfx checkout. SDK selection uses vouchfx's own `global.json`, so a `global.json` in the calling repository does not change this check. `dotnet` on `PATH` must be the isolated install, and every listed runtime must belong to the requested major.

The selector still accepts `8.0` and `9.0` because the workflow can build an older `vouchfx-ref`; current source needs `10.0`, which is the default.

The GitLab template does not run this selector. Its job image is the boundary: `mcr.microsoft.com/dotnet/sdk:10.0` is the default and is what current source needs. The template builds from source, so the image must contain a .NET 10 SDK: `global.json` (10.0.100, `rollForward: latestFeature`) refuses an image that has only a newer-major SDK. `RollForward=Major` applies only to running the built CLI: it lets the `net10.0` CLI run on a newer major runtime when .NET 10 is absent. In this template the image always contains the .NET 10 SDK, so the built CLI always runs on the image's .NET 10 runtime.

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
    ref: v1
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
| `VOUCHFX_DOTNET_IMAGE` | string | `mcr.microsoft.com/dotnet/sdk:10.0` | SDK image the job runs in. The default is .NET 10, which current source needs. The reusable workflow and the GitLab template now default to the .NET 10 SDK, and releases that target .NET 10 need it. Keep the workflow or include ref and `vouchfx-ref` / `VOUCHFX_REF` on the same release, or set the SDK explicitly: `10.0.x` / `sdk:10.0` for releases that target .NET 10, and `8.0.x` / `sdk:8.0` for releases up to 1.2.1. Releases up to 1.2.1 need a .NET 8 SDK; 1.4.0 and later need a .NET 10 SDK. `global.json` refuses an older SDK. Pin the image to a digest in production. The image is the isolation boundary; this template does not strip extra runtimes the way the GitHub workflow does. |
| `VOUCHFX_FAIL_ON_ENV_ERROR` | string | `"false"` | When truthy, an environment-error verdict fails the job with exit code 3. |
| `VOUCHFX_FAIL_ON_INCONCLUSIVE` | string | `"false"` | When truthy, an inconclusive verdict fails the job with exit code 4. |
| `VOUCHFX_PREWARM_IMAGES` | string | (empty) | Optional whitespace/newline-separated list of container images to pre-warm. Pin each entry to an immutable digest. |
| `VOUCHFX_SETUP_SCRIPT` | string | (empty) | Optional path (relative to the project root) of a shell script run before the suite, to produce fixture files the suite declares but does not ship — the GitLab counterpart of the `setup-script` input above. A named-but-missing script fails the job. |
| `VOUCHFX_TELEMETRY_ENDPOINT` | string | (unset) | Optional telemetry ingest base URL. Not sensitive. Telemetry from CI is environment-configured: it activates only when this, `VOUCHFX_TELEMETRY_INSTALL_ID` and `VOUCHFX_TELEMETRY_TOKEN` are all set (see [Telemetry](telemetry.md)). |
| `VOUCHFX_TELEMETRY_INSTALL_ID` | string | (unset) | Optional stable install id: any GUID chosen once per project. An invalid value is ignored with a warning. |
| `VOUCHFX_TELEMETRY_TOKEN` | string | (unset) | Optional bearer token for telemetry uploads. Set it as a masked variable. |

### Docker-in-Docker and the privileged-runner requirement

vouchfx stands up an Aspire container topology, so the job needs a Docker daemon. The
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

**Overriding `before_script`.** GitLab replaces a job's `before_script` list rather than merging
it, and the template's own steps install the Docker client the default SDK image lacks. A job that
defines its own `before_script` without them fails with exit 127. Keep them first:

```yaml
vouchfx-run:
  before_script:
    - !reference [.vouchfx-run, before_script]
    - ./scripts/my-extra-setup.sh
```

### Verification status (important)

The template has run on gitlab.com's privileged SaaS Linux Docker-in-Docker runners, with the
consumer's `ci/lint` reporting no errors or warnings. A positive pipeline produced a genuine smoke
PASS with exit 0. A negative-control pipeline produced a product FAIL with exit 1 and an
environment error with exit 0 under default gating. Every job kept downloadable JUnit and HTML
reports. These runs covered a reviewed candidate first and then official Core 1.1.0 source, and the
template that ran them shipped unchanged in Core 1.2.0
([issue #639](https://github.com/tomas-rampas/vouchfx/issues/639); [evidence](https://github.com/tomas-rampas/vouchfx/blob/main/ci/gitlab/README.md#validation-status)).

Live acceptance on 23 October 2026 also exercised the subsequent template changes at source
`93881722b5ccba2631814394d76f10e7dd36568e`: a no-scenarios run kept exit 0 with missing-report
warnings, and genuine NuGet restore and C# compile failures kept build exit 1 without a run verdict.
Smoke and host-override compatibility controls passed; product failure exited 1 and environment
errors exited 0 by default or 3 under strict policy. All eight jobs returned to their observed daemon
baselines. These changes shipped in Core 1.2.1; see the
[dated receipt](https://github.com/tomas-rampas/vouchfx/blob/main/ci/gitlab/README.md#live-verification-23-october-2026-utc)
for pipeline/job links, report counts, exact revisions and observation limits.

Core 1.2.0 runs the engine in an owned SDK container that shares the Docker-in-Docker daemon's
network namespace and Unix socket, because Aspire/DCP publishes loopback proxy endpoints that a
sibling job container cannot reach. A `TESTCONTAINERS_HOST_OVERRIDE` setting, familiar from
Testcontainers-based setups, is not needed and has no effect on vouchfx. Self-managed Kubernetes and socket-bind runners remain unmeasured.

See [`ci/gitlab/vouchfx-run.gitlab-ci.yml`](https://github.com/tomas-rampas/vouchfx/blob/main/ci/gitlab/vouchfx-run.gitlab-ci.yml)
and [`ci/gitlab/README.md`](https://github.com/tomas-rampas/vouchfx/blob/main/ci/gitlab/README.md)
for implementation details. The live measurements above predate the .NET 10 default image, which has not been measured on GitLab SaaS.

---

## Supply-chain hygiene

For production use, pin everything to something immutable.

**GitHub Actions:**

1. **Pin the `uses:` reference to a full commit SHA**, not a moving branch or tag:
   ```yaml
   uses: tomas-rampas/vouchfx/.github/workflows/vouchfx-run.yml@a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0 # v1.2.1
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
(`mcr.microsoft.com/dotnet/sdk:10.0@sha256:…`) rather than the floating tag. A digest of an `sdk:8.0` image, pinned for an older `VOUCHFX_REF`, must be replaced by an `sdk:10.0` digest when `VOUCHFX_REF` moves to a release that targets .NET 10. The reusable workflow and the GitLab template now default to the .NET 10 SDK, and releases that target .NET 10 need it. Keep the workflow or include ref and `vouchfx-ref` / `VOUCHFX_REF` on the same release, or set the SDK explicitly: `10.0.x` / `sdk:10.0` for releases that target .NET 10, and `8.0.x` / `sdk:8.0` for releases up to 1.2.1. Releases up to 1.2.1 need a .NET 8 SDK; 1.4.0 and later need a .NET 10 SDK. This applies to both platforms when the `uses:` or `include: ref:` pin and the `vouchfx-ref` / `VOUCHFX_REF` pin are bumped separately: a newer workflow with an older ref fails, and so does an older workflow with a newer ref.

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
git ls-remote --tags https://github.com/tomas-rampas/vouchfx v1.2.1
```

Depending on how the release tag was created (both kinds are documented in
[RELEASING.md](https://github.com/tomas-rampas/vouchfx/blob/main/RELEASING.md)), this prints either
*two* lines — `refs/tags/v1.2.1` (an annotated tag object's own SHA) and
`refs/tags/v1.2.1^{}` (the commit it points at, "peeled") — or a *single* line for a lightweight
tag, whose SHA already **is** the commit. **If a `^{}` line is present, take that one**; otherwise the
single line's SHA is the commit SHA to pin.

---

## Documentation publication

Before publishing documentation, build and validate the final output:

```bash
mkdocs build --strict
python scripts/check_site.py _site
```

The publication guard checks XML character data, including literal CDATA contiguous with ordinary text. XML script/style elements have no HTML source exemption. In JSON DATA, CDATA, IGNORE, INCLUDE, RCDATA and TEMP wrappers require valid headers and preserve adjacency between their bodies and surrounding text. The if, else and endif markers form boundaries; their condition text remains DATA and receives inline projection. Valid unfinished bodies are still scanned at end of input; unsupported headers and unfinished DATA tag syntax are refused safely. Independent JSON fields remain separate. Declaration, bogus-comment and processing-instruction openers form boundaries in JSON DATA; their bodies receive inline projection, including unfinished input. Malformed closing tags in DATA are refused safely, and each fragment is decoded only once. Actual HTML source comments and script/style bodies keep their exemptions. The guard does not execute CSS/JavaScript, fetch external frames or reproduce full browser layout.

## This repository's own CI layout

This section describes the vouchfx repository's own pipeline in `.github/workflows/build.yml` and
`.github/workflows/dotnet-compatibility.yml`, not the reusable workflow and GitLab template that
consumers call.

Each pull request to `main` runs the build and unit tests (Linux and Windows) on the .NET 10 SDK, the
memory-leak gate, the native TLS transport acceptance on Windows and Linux, the documentation build
and the workflow contract, all from `build.yml`. `dotnet-compatibility.yml` adds two lanes that
`build.yml` does not have: the Docker-free .NET 10 compatibility lane, which builds and tests with
only a .NET 10 SDK and runtime installed, and the .NET 11 roll-forward lane (the packed tool started
with only a .NET 11 runtime present). The compatibility workflow holds no Docker, memory or TLS lane,
because `build.yml` already runs those on the same .NET 10 runtime. It also runs daily on `main`,
scheduled for 06:47 UTC, so that a new .NET 10 or .NET 11 runtime build that breaks either lane is
noticed on a day with no pull request.

The Docker integration suite does not run on pull requests or pushes. Its steps live in the reusable
workflow `.github/workflows/docker-integration.yml`, which the `integration` job in `build.yml` calls
at the commit under test; the job's `if:` keeps it to the daily run on `main`, scheduled for
06:17 UTC, and to manual dispatch from any branch. A Docker-test failure therefore surfaces on the
next daily run, not before merge. The lane's job has a 120-minute timeout. The release workflow
(`release.yml`) calls the same lane on every release run, including its manual preflight, as a
gate: `sign-and-attest` and `publish-release` need it, so for a commit on which it has not passed
no attestation, cosign signature or NuGet package signature is made, nothing is attached to the
release, and nothing is published to NuGet.org or the Marketplace. The build jobs still run: the
installers are signed or notarised where those secrets are configured, and the packages, archives,
SBOM, extension and installers stay downloadable as run artefacts. `RELEASING.md` says what a red
lane leaves behind. The
`integration-gate` job in `build.yml` reports the lane's result
under the required check name **Integration tests — Docker / Aspire**: on a pull request that check
is reported as skipped, which is expected, and on a run where the lane ran it fails unless the lane
succeeded. On a cancelled run, including a pull-request or push run that a newer one superseded, the
check reports failure (it used to report cancelled), like the TLS gate; its error message names the
lane's result, and `cancelled` there means the run was cancelled, not that a test failed.
The lane's logs are in the job `Docker integration lane / Aspire topology and provider tests`; in a
release run the job is expected to be shown as `Docker integration / Aspire topology and provider tests`.

Failures no longer depend on someone watching the Actions tab for those daily runs.
`scheduled-run-alerts.yml` opens one issue per workflow, titled "Scheduled run alert", when a
scheduled run of `CI` or `.NET compatibility` fails, is cancelled or times out. The issue lists the
jobs that did not succeed and the jobs that were skipped (the Docker suite is skipped when the build
job fails, so it gave no result that day; the lane and its gate are then both listed as skipped).
Each further occurrence adds a comment, and a later scheduled run that succeeds and is the newest one
closes the issue. A break within 30 days of a close reopens the same issue, so an open issue means
the workflow is currently broken. The repository owner is added to the issue's assignees (whoever
else is assigned stays) so that they are notified whatever their watch settings.

Closing the issue by hand has consequences. If the workflow is still failing and you close the issue
as completed, the next failed scheduled run reopens it. If you close it as "not planned", it is
never reopened, and the next failure opens a new issue instead. To quieten a known break, leave the
issue open and unsubscribe from it.

A daily watchdog in the same file opens or updates that issue when either workflow is not active, or
when no scheduled run was created on `main` in the last 12 hours, whatever its status. While such a
condition lasts, the watchdog adds a comment to the open issue only when the newest finding it
recorded there was of a different kind, so a workflow disabled on purpose gets one comment, not one
a day. A run that is still in progress, or one that fails, is the first job's responsibility; the
watchdog also opens the issue if the newest completed scheduled run was unsuccessful, is also the
newest scheduled run and no issue is open. It does not do so when someone closed an alert issue for
that workflow after that run last changed, so a lost event or a failed API call does not hide a
failure, but a recovery or a manual close is not undone.

A manual run of the watchdog (**Run workflow** on the default branch) is a health report: it reads
the same data and prints its finding, and the write it would have made, in the job log, but it
creates and changes no issue. The "no scheduled run in 12 hours" finding is meaningful only around
the scheduled time (09:23 UTC), because a run created at 06:30 is more than 12 hours old by the
evening.

The state check catches a workflow that was disabled by hand. GitHub disables scheduled workflows
after 60 days without repository activity for the whole repository at once, so `CI`,
`.NET compatibility` and the watchdog stop together and nothing in these workflows can report it.
Check the Actions tab for disabled workflows (state `disabled_inactivity`) before a release, as the
checklist in `RELEASING.md` says, and re-enable all three together.

To run the Docker suite on a branch before merging, open the Actions tab, choose the `CI` workflow,
select **Run workflow** and pick the branch. Manual dispatch needs write access to this repository: a
fork contributor runs the workflow from their own fork's Actions tab (once Actions is enabled there)
or runs the `requires=docker` tests locally.

The advisory **Analyser forward (.NET 11, non-blocking)** job in `build.yml` is `continue-on-error`.
It rebuilds the solution in Release with `-warnaserror` on the .NET 11 SDK, one major ahead of the
supported SDK, so the newer SDK's recommended analysers can report problems before the repository
moves. The job installs the .NET 11 SDK and then runs from a scratch directory outside the checkout,
where it writes its own `global.json` (`11.0.100`, `rollForward: latestFeature`). The muxer resolves
`global.json` by walking up from the working directory, not from the solution, so that pin governs
the version check and the build while the repository's .NET 10 `global.json` stays out of the way.
Without it, a hosted image that also carries a newer major would select that SDK, and the job's
`11.*` version guard would fail before the rebuild. `latestFeature` rolls only within 11.0, so the
job fails closed when no 11.0 SDK is installed rather than drifting to another major.

## See also

- **[Recipes → CI integration](recipes.md#ci-integration-with-github-actions)** — the minimal copy-paste starting point.
- **[Getting started](getting-started.md)** — your first test in 60 minutes.
- **[Troubleshooting](troubleshooting.md)** — Docker not running, the Aspire 20-second cold-start gotcha, and other real failure modes.
