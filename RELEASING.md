# Releasing vouchfx

This document covers how to cut a release, how consumers verify the artifacts,
the human provisioning steps (signing credentials and the NuGet Trusted
Publishing policy), and the distinction between the primary (nupkg) and
secondary (self-contained exe) distribution channels.

## How to trigger a release

The release pipeline (`release.yml`, shown in the Actions tab as
`Release (signed, provenance)`) starts only by manual dispatch. No GitHub
Release, tag push, branch push or pull request starts it. No tag, Release,
package, attestation or cosign signature is made before the run's tests and
builds have passed. The build jobs still sign or notarise the installers where
those secrets are configured, and the run artefacts are visible as in any run.
The run creates the tag and the GitHub Release itself, near its end.

**Do not create a GitHub Release or a version tag by hand.** A tag
`v<version>` that already exists makes the first job refuse a `rehearse` or
`publish` run for that version. A Release created by hand starts nothing on new
commits. For a tag made before this change it is different; see
[The Docker integration gate](#the-docker-integration-gate).

The run takes three inputs:

| Input | Value |
|-------|-------|
| `mode` | `validate` (the default), `rehearse` or `publish`. |
| `version` | Required; there is no default. The semver to build, without a leading `v` (for example `1.5.0` or `1.5.0-rc.1`). It becomes the engine version of the packaged tool. See the `prepare` checks below for the form each mode needs. |
| `commit` | The full 40-character SHA of the commit to release. Required by `rehearse` and `publish`. It must equal the commit the run uses, which is the head of `main` when the run starts. |

The three modes:

| Mode | Where it can run | What it does |
|------|------------------|--------------|
| `validate` | Any branch. | Tests and builds everything, including the Docker integration lane. It publishes nothing, attests nothing, makes no cosign signature and creates no Release, draft or tag. It never asks for approval of the `release` environment. |
| `rehearse` | `main` only, with `commit` equal to the run's commit. | As `validate`. Then the publishing job (`publish-release`) waits for approval of the `release` environment, creates a draft Release, uploads the assets to it, exchanges the NuGet token (no package is pushed), deletes the draft and stops. It makes no tag, no published Release, no package, no attestation and no cosign signature. |
| `publish` | `main` only, with `commit` equal to the run's commit. | The real release. |

The first job, `prepare` (shown as `Check the release request`), checks the
request before anything is built. A refusal is an error line in that job's log.

- In every mode, `version` must be strict semver: `1.2.3` or `1.2.3-rc.1`. A
  `+build` suffix or a fourth dotted part (`1.2.3.4`) is refused. A numeric
  pre-release part may not have a leading zero: `1.2.3-01` is refused.
- In `rehearse` and `publish`, the run must be on `main`; `commit` must be the
  full 40-character commit of the run; the repository must be the canonical one;
  no tag `v<version>` may exist; no published Release may exist for it; and none
  of the NuGet package IDs may already have that version.
- In `publish` only, the newest version heading in `CHANGELOG.md` and the
  engine version fallback in `Directory.Build.props` (`VouchfxEngineVersion`)
  must both equal `version`. The `CHANGELOG.md` section for `version` must also
  be usable as release notes: it must exist exactly once, must not be empty and
  must be at most 100,000 characters. GitHub's generated notes are appended to
  it, and a Release body is believed, not documented, to be capped at 125,000
  characters.
- `rehearse` accepts only a version of the form `X.Y.Z-rehearsal.N` (`N` a
  number), for example `1.5.0-rehearsal.1`. `publish` refuses any version with
  a pre-release whose first identifier is `rehearsal` or `validate` (for example
  `1.5.0-rehearsal.1` or `1.5.0-validate`), and any version that starts with
  `0.0.0-`. Those versions are reserved for rehearsal and validation, so a
  rehearsal version can never be released later.
- The version becomes the engine version of the packaged tool. The
  packaged-tool smoke test runs that tool, and the engine refuses to load any
  provider whose minimum engine version is higher than its own version (by
  SemVer precedence). So for `validate` and `rehearse` use the next release
  version as `X.Y.Z` (for example `1.5.0-validate.1` and `1.5.0-rehearsal.1`).
  A pre-release sorts below its release, so `1.5.0-rehearsal.1` is lower than
  `1.5.0`. If a provider requires the release version itself, use a higher
  `X.Y.Z`, for example `1.5.1-rehearsal.1`. A version below a provider's
  minimum, such as `0.0.0-test` or `0.0.0-rehearsal.1`, passes `prepare` and
  then fails the packaged-tool smoke test, so the run fails. In `rehearse` mode
  that failure also skips the publishing job.

To release:

1. In `CHANGELOG.md`, rename the `## [Unreleased]` heading to
   `## [<version>] — <date>` and add a fresh, empty `## [Unreleased]` heading
   above it, so the file keeps exactly one `## [Unreleased]` heading
   (`scripts/check_site.py` enforces this). Set the `VouchfxEngineVersion`
   fallback in `Directory.Build.props` to `<version>` in the same commit: the
   `SourceCapabilityStamp_MatchesTheNewestChangelogVersion` test fails when the
   newest versioned heading and the fallback differ, and `prepare` refuses a
   `publish` run unless both equal `version`. If a version heading is withdrawn
   before it is released, lower the fallback to the newest remaining versioned
   heading in that commit too. Merge this release pull request into `main`.
   The Release notes are the `CHANGELOG.md` section for the version followed by
   GitHub's generated notes, so write that section as release notes.
2. Optional: run `validate` on the commit you mean to release, to find a failing
   build or Docker lane early. See [Checking a candidate](#checking-a-candidate-validate-and-rehearse).
   A `publish` run also runs everything first and makes no tag, Release or
   package if it fails, so this step saves time rather than adds safety.
3. Read the full SHA of the head of `main`:
   `git fetch origin && git rev-parse origin/main`.
4. In the Actions tab, select `Release (signed, provenance)` and click
   **Run workflow**. Choose the branch `main`. Set `mode` to `publish`, `version`
   to the version without a leading `v`, and `commit` to the SHA from step 3.
   If `main` moves between steps 3 and 4, the run is expected to be refused
   because `commit` is no longer the head of `main`; read the SHA again.
5. Wait for the tests and builds. Expect about 80 minutes (see
   [The Docker integration gate](#the-docker-integration-gate)). If the run goes
   red before the publishing job, no tag, Release or package is made; see
   [A red run before the publishing job](#a-red-run-before-the-publishing-job).
6. When the tests, builds, smoke tests, attestation and signing have passed, `publish-release`
   waits for approval of the `release` environment. The environment allows only
   `main`, has one required reviewer and allows self-approval. Open the run and
   approve it.
7. Wait for `publish-release`, the Marketplace job and the `move-floating-tag`
   job. In the log of `publish-release`, the step
   `Verify the tag and the published release` is expected to end with
   `Verified: v<version> is at <commit> and its Release is published.` Check
   that the packages are on NuGet.org.
8. Work through the [documentation checklist](#release-time-documentation-checklist).

A newer run does not cancel an existing run (`cancel-in-progress: false`);
manual cancellation remains possible. Only a `rehearse` or `publish` run on
`main` uses the shared concurrency group `release-publish`, so two of those never
run at once, and GitHub keeps at most one waiting run in a group: a third
`rehearse` or `publish` dispatch on `main` is expected to replace the one still
waiting. Every other run has a group of its own. That includes a `validate` run
and a `rehearse` or `publish` dispatch from another branch, which `prepare`
refuses, so neither can take the place of a waiting release run.

### Order of publication

In `publish` mode the order is:

1. Tests and builds, including both smoke-test jobs
   (`smoke-test-packaged-tool` and `smoke-test-distribution-platforms`).
2. The Docker gate passes.
3. Attest and sign (`sign-and-attest`). The job needs the Docker gate, the builds
   and both smoke-test jobs, so no attestation or signature is made until all of
   them have passed.
4. Approval of the `release` environment. Signing comes before this step and the
   approval does not protect it: the approval protects the draft Release and the
   NuGet push, not the signing identity.
5. `publish-release` creates a private draft Release whose target is the run's
   full commit SHA, and uploads all assets to it.
6. `publish-release` pushes the NuGet packages.
7. `publish-release` publishes the draft. That is expected to create the
   lightweight tag `v<version>` at the tested commit. A version with a `-` suffix is marked as a
   pre-release and is not marked "latest".
8. `publish-release` checks that the tag points at the tested commit.
9. The Marketplace job (`publish-marketplace`).
10. `move-floating-tag`.

NuGet goes before the publication of the Release because it is the step most
likely to fail, and a draft is not public. The Release title is the tag. Nothing
in the run pushes a tag with git: the tag appears when the draft is published.
Attestations, cosign signatures and the GPG signature of the package are made
only in `publish` mode, because each is a permanent public record.

Everything above is expected behaviour. None of it has run on GitHub yet; see
[First runs](#first-runs-what-only-github-can-show).

### The Docker integration gate

Every release run, in every mode, calls the Docker integration lane (the
`docker-integration` job, which runs `.github/workflows/docker-integration.yml`)
at the commit being released. It needs only `prepare`, so it starts beside the
TLS transport lane, before the builds.
It took about 75 minutes in CI, so expect a release run to take about 80
minutes, plus the wait for approval. The attestation and signing job
(`sign-and-attest`) and the publishing job (`publish-release`) both need it, so
the Marketplace job, which needs both, waits for it too. `move-floating-tag`
needs `publish-release` and `publish-marketplace`.

If the lane fails, is cancelled, times out (its limit is 120 minutes) or is
skipped, these are **withheld**: build-provenance attestations, cosign
signatures, the GPG signature of the package, the draft and the GitHub Release,
the tag, the NuGet pushes, the Marketplace extension and the move of the
floating tags (`v1-alpha`, `v1-rc`, `v1`). A floating tag is expected to stay
where it was.

These are **not** withheld:

- The installer builds. They still run and, where those secrets are
  configured, are signed with the project's keys (Authenticode for the MSI and
  `vouchfx.exe`, a GPG signature for the `.deb`) or, for the `.pkg`, submitted
  to Apple for notarisation. The installers stay downloadable as run
  artefacts (`msi-installer`, `deb-package`, `pkg-installer`) for 30 days by
  anyone signed in to GitHub, because this repository is public. Do not treat
  them as released, and do not point anyone at them.
- The other build outputs. The nupkg (`nuget-package`), per-RID archives
  (`publish-archives`), SBOM (`sbom`) and `.vsix` (`vsix`) are built and stay
  downloadable as run artefacts for 30 days, with no attestation or cosign
  signature. Do not treat them as released either.

**Releases made by hand for older tags.** A GitHub Release created by hand for a
tag made before this change still runs that tag's old workflows: the old release
run and the old **Move floating convenience tag** workflow. For a `release`
event GitHub runs the workflow files of the tagged commit, and nothing in the
current files can prevent that. The old floating-tag move can move a floating
tag at once, with no gate, and can move it backwards. So do not create or
re-publish a GitHub Release for a tag made before this change, and do not re-run
an old **Move floating convenience tag** run. Do not re-run a release run from
before this change either: a re-run uses that run's old workflow file.

### A red run before the publishing job

A red, cancelled or timed-out run before `publish-release` leaves no tag, no
Release, no package, no extension and no floating-tag move. Run artefacts exist
(see above). `sign-and-attest` needs the Docker gate, the builds and both
smoke-test jobs, so a red Docker lane or a red smoke test also means that no
attestation or signature has been made. The version number is not spent: fix the
cause, merge, and dispatch again with the same version.

1. Read the failure. For the lane, read the job expected to be shown as
   `Docker integration / Aspire topology and provider tests`. If it is a
   transient fault (a registry stall or rate limit, a runner fault), use
   **Re-run failed jobs** on the release run. That is expected to re-run the
   lane and, if it passes, the jobs after it on the same commit as the first
   attempt. It has not yet been exercised on a release run, so confirm that
   those jobs ran, and be ready to approve the `release` environment: whether the
   approval carries over from the first attempt has not been seen.
2. If it is a real defect, or the pre-pull reports that a pinned image no longer
   exists, do not re-run: a re-run tests the same commit and the same image
   list. Fix it, merge, and dispatch again with the same version and the new
   head of `main` as `commit`. The floating tags are not moved by a red run, so
   `v1` stays where it was and consumers who reference it (the reusable
   `vouchfx-run.yml` workflow at `@v1`, or the GitLab template with `ref: v1`)
   are expected not to get this commit. Nothing needs moving back.
3. A timeout (the lane ends at 120 minutes) is not known to be transient. Re-run
   once only if the log shows the time went to the pre-pull or to a runner
   stall; otherwise treat it as step 2.
4. There is no bypass: no input, variable or label skips the lane. Do not
   disable the job or edit the workflow to release a commit the lane did not
   pass.

A `publish` run that fails after `sign-and-attest` has succeeded (for example
when the `release` environment is not approved, or `publish-release` fails) has
still written attestations and made cosign signatures for artefacts that were not
released. They are public records. Do not point anyone at those artefacts. If
`sign-and-attest` itself fails part-way, some of its attestations and signatures
may already exist in the same way.

### Recovering a publication that stopped part-way

`publish-release` can be re-run. Each step checks what already exists, so
**Re-run failed jobs** is expected to complete a partial publication. This has
not been exercised on GitHub yet. First read the log of the step
`Classify the release state`, which reports the state of `v<version>` as `none`,
`draft` or `published`. Then pick the case by what exists.

Use **Re-run failed jobs**, not **Re-run all jobs**. **Re-run all jobs** runs
`prepare` again, which refuses the version once its tag or a package of it
exists. A re-run is expected to start from the latest attempt, so after that
refusal **Re-run failed jobs** only runs `prepare` again and is refused too: the
run can no longer finish the publication. If the Release is already published,
or every package is on NuGet.org and the draft holds every asset, finish by hand
as case 3 describes for a run past the 30-day limit. Otherwise the version is
spent (see case 2). When only a draft exists and nothing was pushed, **Re-run
all jobs** is not refused, but it builds, attests and signs everything again.

Four rules apply to every case:

- **A re-run uses the original run's commit.** It does not take the current head
  of `main`. It also uses that run's workflow file.
- **A draft is replaced, not reused.** When a draft for the version already
  exists at the run's commit (a re-run, or a later run at the same commit), the
  step `Create the draft release and upload the assets` deletes the earlier draft
  and makes a new one from this run's files. Hand edits to the draft (notes,
  title, assets) are lost, so make none before the run has published it. Just
  before the deletion the step reads the draft again. If that read fails, or the
  draft has since been published or now has another tag or target commit, the
  step fails with a message that ends `nothing was deleted`: look at the
  Releases page before you re-run. A change to the draft's notes, title or
  assets does not stop the deletion. The step then reads the new draft back
  (see the last rule below).
- **Do not delete a draft of a version you still mean to release once any package
  of that version has been pushed.** Check NuGet.org first. If the draft is
  deleted after a partial push, the run is refused (case 7). There is one
  exception, because the step `Create the draft release and upload the assets`
  deletes an earlier draft itself before it makes the new one. If that step
  failed after its log line `Deleted the earlier draft …` and before a new draft
  exists, the packages on NuGet.org are expected to come from an earlier attempt
  of this run, and the version is not spent. Confirm that the log of
  `Publish nupkg to NuGet.org — Trusted Publishing` in an earlier attempt of
  this run pushed each package of the version that NuGet.org lists. If one came
  from another run, treat the version as spent. Otherwise make a draft by hand
  at the tested commit
  (`gh release create v<version> --repo tomas-rampas/vouchfx --draft --target <full SHA of the tested commit> --title v<version> --notes "Replaced by the release run."`).
  The full SHA is the run's `commit` input. Keep `--draft`: without it the
  command publishes a Release at once and creates the tag. Check on the Releases
  page that it is the only draft for the tag, and use **Re-run failed jobs**.
  The run is expected to delete that draft and make its own.
- **A new draft is read back, and the list of releases can lag.** GitHub's list
  of releases can lag behind a write: in the first rehearsal it showed no draft
  about a second after the draft was made. So the step
  `Create the draft release and upload the assets` reads the list back after it
  makes a draft, up to 12 times, 5 seconds apart (about a minute in all), while
  the list cannot be read or shows nothing for the tag. An earlier draft that
  the step deleted itself is left out of the count. The step then asks the
  release tool for the release of the tag, again up to 12 times, until the tool
  finds it, and checks that it is the very draft that was read back. Only then
  does it upload the assets, once. Nothing has been pushed to NuGet.org when
  any of these failures happens. If the command that creates the draft fails,
  a draft may still have been stored: look at the drafts for the tag on the
  Releases page before you re-run. After the draft is created, the step fails
  in one of six ways:
  - The list shows more than one entry for the tag, or one entry that is not a
    draft or not at the tested commit. The message says that the new draft
    `did not read back as expected` and what it found. This is not lag, so the
    step fails at once, and it uploads and pushes nothing. In a rehearsal the
    clean-up is expected to delete the draft. In a `publish` run this is not
    expected to happen: it means that someone made a draft for the tag during
    the run, or that an assumption about GitHub is wrong. Look at the drafts
    for the tag on the Releases page and do not re-run until you understand
    it. Two drafts can also both come from this run: one from an earlier
    attempt whose draft the list never showed (the next failure), and one from
    the re-run. In that case delete one of the two by hand, so that exactly
    one remains, and use **Re-run failed jobs**.
  - The list never shows the draft. The message begins `The list of releases
    did not show the new draft for v<version>`. Nothing was uploaded. In
    `publish` mode the draft may exist. Wait a few minutes, check that the
    Releases page lists exactly one draft for the tag, and then use **Re-run
    failed jobs**, which replaces it. A rehearsal deletes its draft in the
    clean-up step.
  - The list cannot be read on the last attempt. The message begins `Could not
    read the releases back after creating the draft for`. Nothing was
    uploaded. A draft may exist, so look at the drafts for the tag on the
    Releases page. **Re-run failed jobs** replaces a draft that is there.
  - The release tool never finds the draft. The message begins `The tool did
    not find the draft for v<version>`. Nothing was uploaded. In `publish`
    mode the draft is left in place. Wait a few minutes, check that the
    Releases page lists exactly one draft for the tag, and then use **Re-run
    failed jobs**, which replaces it. A rehearsal deletes its draft in the
    clean-up step.
  - The release tool finds another release for the tag. The message begins
    `The release the tool finds for v<version> is not the draft this run
    created and read back`, and says what it found. Nothing was uploaded. Like
    the first failure this is not lag, so the step fails at once: look at the
    Releases page and do not re-run until you understand it.
  - The upload fails. The message begins `The upload to the draft for
    v<version>`. The upload is not tried again, because its failure may not be
    lag. In `publish` mode the draft is left in place: **Re-run failed jobs**
    replaces it. A rehearsal deletes its draft in the clean-up step.

The cases:

1. **A draft only, and no package of the version is on NuGet.org** (for example
   the NuGet login failed, or every push failed). Fix the cause and use **Re-run
   failed jobs**, or delete the draft. Before you delete a draft, confirm on
   NuGet.org that no package of that version was pushed. A new dispatch from a
   different commit is refused by the publishing job while a draft for that
   version targets another commit: delete the draft first, after the same check.
   If the log says that several releases or drafts exist for the tag, delete the
   extra drafts by hand, also after that check.
2. **Some NuGet packages pushed.** Fix the cause (for example the Trusted
   Publishing policy for a new package ID) and use **Re-run failed jobs**, not a
   new dispatch. A new dispatch of the same version is refused once NuGet.org
   lists a package of it, which can take some minutes after a push; do not try
   one in that time. There is no route when the cause needs a code change: a re-run
   tests the same commit, and a new dispatch is refused. If the cause needs a
   code change, the version is spent: release the next patch version, and delete
   the draft of the spent version. The packages already pushed stay on NuGet.org.
   For the next version, rename the `CHANGELOG.md` heading and set the
   `VouchfxEngineVersion` fallback to the new version in a new release pull
   request, and consider unlisting the packages of the spent version on
   NuGet.org.
3. **All packages pushed, the draft not published.** Use **Re-run failed jobs**.
   After GitHub's 30-day limit for re-runs, publish the draft by hand (its target
   is the tested commit) and run **Move floating convenience tag** by hand. For a
   stable version, also publish the `.vsix` from the draft to the Marketplace.
4. **The publication step refused the tag** (the log of `Publish the draft release`
   says that the tag `now resolves to` another commit and that `nothing was
   published`). Someone made the tag after the run started. The packages are
   already on NuGet.org and the draft is not published. Find out who made the tag
   and why before you delete or move it. Once the tag is gone, or points at the
   tested commit, **Re-run failed jobs** is expected to publish the draft.
5. **Verification failed because the tag is elsewhere** (the log says the tag does
   not point at the tested commit; the step fails at once, without waiting). The
   Release is already published and the packages are already on NuGet.org. The
   Marketplace job and the floating-tag move are skipped. Do not move anything
   by hand before you find out why the tag is where it is.
6. **Verification failed because the Release is not published** (the step
   `Verify the tag and the published release` says `The Release v<version> is
   not published.`, for example when the API answers 404 just after publication).
   The step reads the tag and the Release up to 12 times, 5 seconds apart, so it
   waits up to about a minute for them before it fails. The tag check comes
   first in each read, so this message means that the tag is at the tested
   commit. If the log says `is not published` and the tag is at the
   tested commit, wait a few minutes, confirm on the Releases page that the
   Release is shown as published, and then use **Re-run failed jobs**. The
   classification then finds a
   published Release, and the run is expected to continue (see case 8). If it
   fails the same way again, look at the Releases page before you re-run once
   more. After the same wait the step can also end with `Tag 'v<version>' was
   not found after 12 reads.` or with `Could not read the remote tags.` In
   both the publication step has already run. Wait a few minutes, check on
   GitHub that the tag exists and points at the tested commit, and then use
   **Re-run failed jobs**. If the tag still does not exist, the classification
   stops with `A published Release for 'v<version>' exists but its tag cannot
   be found.` Do not re-run again, and move nothing by hand, before you know
   why the Release has no tag.
7. **No draft and no Release, but a package of the version is on NuGet.org.** In
   `publish` mode the classification fails with a message that the package is
   already on NuGet.org and that no draft or Release exists for the tag. Before
   you treat the version as spent, read the log of
   `Create the draft release and upload the assets` in the most recent attempt
   in which that step ran (the attempt before the refused one). If it deleted the earlier
   draft and then failed, follow the third rule above: the version is not spent.
   Otherwise an earlier run pushed the package and its draft was deleted since.
   The version is then spent: release a new version (see case 2 for the release
   pull request). The workflow refuses because publishing now could pair another
   commit's packages with this commit's Release.
8. **The Release is already published.** If the
   published Release holds the same asset names as the files this run would
   upload, the run continues: it uploads nothing to the Release, the push step
   skips the packages already on NuGet.org and pushes any that are missing, and
   the step
   `Publish the draft release` reports that the Release is already published.
   This is the normal re-run after a failed verification. If the names differ,
   the step `Create the draft release and upload the assets` fails with a
   message that the published Release "does not
   hold this run's assets". The run uploads nothing and pushes nothing to
   NuGet.org, and the later jobs are skipped. Compare the Release's assets with
   the list in the step `List release artifacts` and find out how they came to
   differ. A re-run fails in the same way until that changes. Only the asset
   names are compared, not their contents.
9. **A re-run of an old `publish` run, or a stable patch of an older line, is
   refused.** For a stable version with no published Release yet (state `none` or
   `draft`), the classification fails with a message that begins `Refusing to
   publish` if a published stable Release with a higher version exists, because
   the version "would be marked Latest in its place". So an old failed run cannot
   be re-run after a newer stable release, and a stable patch of an older line
   cannot be released after a newer stable one. Pre-releases are not affected, and
   neither is a run whose Release is already published. If the draft of an older
   stable version must still be released, decide by hand whether it may be marked
   Latest; the workflow does not do it.
10. **A Marketplace or floating-tag failure.** See the cases below.

### Floating tag cases

Four cases concern the floating tags after the Docker lane has passed. In each,
"the manual run" means running **Move floating convenience tag**
(`move-floating-tag.yml`) by hand with a release tag. A manual run has neither
the commit check nor the no-downgrade check described below, so first confirm
that the tag and its release are the ones you mean and that, in the release run
for that tag, the Docker lane (where that run has one) and `publish-release`
succeeded. Its log reports `Moved '<floating tag>'` on success.

**A publication job fails.** `move-floating-tag` needs `publish-release` and
`publish-marketplace`. If either fails after the lane has passed, the floating
tag does not move. For `publish-release`, see the recovery cases above. If the
Marketplace job failed, the Release and the packages are already public. Fix the
cause and use **Re-run failed jobs**. That is expected to re-run the failed job
and the jobs after it, and then move the tag; it has not yet been exercised. If
only the Marketplace job failed and it cannot be fixed soon, use the manual run
with the release tag.

**Only the `move-floating-tag` job fails or is cancelled.** This job runs after
`publish-release` and `publish-marketplace` have succeeded. If it fails, the
release run shows as failed although the release and the packages are published,
and the floating tag has not moved. Tag moves run one at a time, and GitHub keeps
one waiting job per concurrency group by default, so with three tag moves at once
the middle one is expected to be cancelled, not failed. Read the job's log. What
to do depends on its message:

- *The release tag resolves to a different commit than the one the run tested.*
  The release tag was moved after the run started. Do not use the manual run
  with this tag: it would put the floating tag on a commit that no gate passed.
  Point the release tag back at the tested commit named in the log and use
  **Re-run failed jobs**, or ship a new release.
- *The floating tag is on a commit that carries no release tag of its line.* The
  job cannot tell which release the floating tag is on, so it moves nothing. Find
  out why the floating tag is there. If the move is intended, use the manual run
  with the release tag.
- *The remote tags could not be read*, or the job was cancelled. Use **Re-run
  failed jobs**, which is expected to retry only that job; this has not yet been
  exercised. The other route is the manual run with the release tag.
- *Any other message* (for example `No release found for tag`, which a transient
  GitHub API error also produces). If the tag was refused as not valid semver, no
  floating tag tracks it; do nothing. Otherwise check that the release is still
  published and that its tag still exists, then use **Re-run failed jobs**.

**A warning that the floating tag is already on a newer release.** The job can
end successfully with a warning that names the floating tag and the newer release
it is on. It moved nothing. That is expected when an older release run's
`move-floating-tag` job finishes, or is re-run, after a newer release in the same
line has moved the tag. A stable patch of an older minor line (a `v1.3.z` after
`v1.4.0`) is refused before publication; see case 9 above. No action is needed.
To put the tag on the older release anyway, use the manual run.

**A release found defective after it was published.** If a release passed its
gates, was published, moved `v1` and was later found defective, consumers who
reference `v1` (the reusable `vouchfx-run.yml` workflow at `@v1`, or the GitLab
template with `ref: v1`) get it. Until the patch ships, use the manual run with
the last good GA release tag (`v1.y.z` with no pre-release suffix; a pre-release
tag never moves `v1`, and the run still succeeds; a `v1.0.0-rc.N` tag moves
`v1-rc`, and a `v1.0.0-alpha.N` or `-beta.N` tag moves `v1-alpha`, instead), and
confirm that its log
reports `Moved 'v1'`. A manual run has no no-downgrade check, so it moves `v1`
back to the older release. After that, do not re-run the defective release's
run, and cancel any re-run of it that has started: the defective release is newer than
the one `v1` is now on, so its `move-floating-tag` job would move `v1` forward
again.

### Release-time documentation checklist

Some published text is worded to be true both before and after a release, and some can only be
finished once the tag is published. Work through this list in the release pull request or straight
after publication:

- Set `site/facts-fallback.json` (`engine_release`, `sdk_version`) to the newest **published**
  release (for example `v1.2.1` and `1.2.1`). It is used only when the live fact fetch fails, so it
  must never name an unpublished version.
- Name the first release that bundles a new Aspire patch line where the docs say "up to and including
  v1.2.1": `SECURITY.md` (the Aspire advisory section), the `CHANGELOG.md` Aspire entry, blueprint
  section 5.7 and the `NuGetAuditSuppress` comment in `Directory.Build.props`. Check that the
  `scripts/check_distribution.py` and `Vouchfx.Dcp.targets` comments still match the pinned Aspire.
- Update texts that say "once published": `SECURITY.md` and `docs/getting-started.md` (the
  `SslCa` advice for 1.2.1). Re-read the vouchfx-mcp paragraph in `docs/getting-started.md`: it
  states the exact-pin rule and names no version, so it needs an edit only if that rule changes.
- Confirm that the `move-floating-tag` job, the last job of the release run, moved the floating tag
  for this release (`v1` for a GA release), and that `docs/ci-integration.md` still describes the tags
  truthfully. If the run is red before that job, no floating tag is expected to have moved.
  See [Floating tag cases](#floating-tag-cases) if the job fails or warns.
- In `vouchfx-mcp`, bump `ENGINE_PIN` and run `scripts/sync-vendored.ps1 -Update`; `docs/recipes.md`,
  `docs/language-reference.md` and the two composed-schema goldens are vendored byte-for-byte, so
  mention any change to them in that pull request. In `vouchfx-samples`, bump its engine pin.
- Re-read the .NET 10 wording in `docs/getting-started.md`, `docs/ci-integration.md` and
  `ci/gitlab/README.md`, and measure the .NET 10 default image on GitLab SaaS if a fresh receipt is wanted
  (the current receipts used `sdk:8.0`).
- Re-check the landing page (`site/index.html`) for statements that were worded around an unpublished
  release.
- Open the Actions tab and confirm that `CI`, `.NET compatibility` and `Scheduled run alerts` show no
  "disabled" notice, and that no "Scheduled run alert" issue is open. GitHub disables scheduled
  workflows after 60 days without repository activity, and the watchdog that raises those issues is
  itself scheduled. Re-enable all three together if any is disabled.

### Checking a candidate: `validate` and `rehearse`

These two modes replace the old manual preflight (smoke-test) dispatch. Neither
creates a tag or a published Release, and neither pushes a package.

**`validate`: test and build only.** Use it on any branch.

1. In the Actions tab, select `Release (signed, provenance)` and click
   **Run workflow**.
2. Choose the branch to check. Set `mode` to `validate` and `version` to the next
   release version followed by `-validate.N`, for example `1.5.0-validate.1`
   (there is no default; see the `prepare` checks above for why `0.0.0-test`
   does not work). Leave `commit` empty.
3. The run calls the Docker integration lane (it took about 75 minutes in CI; see
   [The Docker integration gate](#the-docker-integration-gate)), builds all
   artifacts (nupkg, per-RID tarballs, SBOM, VSIX, MSI, deb, pkg) and runs the
   distribution checks.
4. `publish-release`, `publish-marketplace` and `move-floating-tag` are expected
   to show as skipped. The run makes no attestation, no cosign signature and no
   GPG signature of the package, and creates no Release, draft or tag. The build
   jobs still sign or notarise the installers where those secrets are configured,
   as in any run. The build outputs stay downloadable as run artefacts for 30
   days.

A `validate` run does not show that the public packages are available, and it
does not exercise the publishing job.

**`rehearse`: also run the publishing job, then clean up.** Use it to prove the
publishing job without releasing anything.

1. Pick a rehearsal version: the next release version followed by
   `-rehearsal.1` (for example `1.5.0-rehearsal.1`), or a higher number if an
   earlier rehearsal left something behind (see below). `rehearse` accepts no
   other form.
2. Read the full SHA of the head of `main` (`git fetch origin && git rev-parse
   origin/main`). In the Actions tab, run `Release (signed, provenance)` on
   `main` with `mode` set to `rehearse`, that `version`, and that SHA as
   `commit`. `prepare` applies the checks for `rehearse` and `publish`, except the
   `CHANGELOG.md` and `Directory.Build.props` check, which is for `publish` only.
3. After the tests and builds, `publish-release` waits for approval of the
   `release` environment. Approve it.
4. The job creates a draft Release and reads the list of releases back until it
   shows the draft, up to 12 times, 5 seconds apart (the list can lag behind
   the creation). It waits in the same way until the release tool finds the
   draft by its tag, then uploads the assets to it once. It runs the NuGet
   login (the token exchange; no package is pushed) and deletes the draft in
   the step `Rehearsal — delete the draft release`. That step is written to run
   also when an earlier step failed or the run was cancelled.
5. When the draft step reported that it started to create a draft, the clean-up
   step reads the list, up to 12 times, 5 seconds apart, until it shows a draft
   for the tag. If none shows, the step fails when the draft was created; when
   the creation itself failed it warns instead (the job is already red). After
   deleting the draft, it reads the list again in the same way until nothing
   for the tag remains. It fails if anything still remains then, or if the list
   cannot be read on the last read. A published Release for the tag fails the
   step at once.
6. A rehearsal makes no tag, no published Release, no package, no attestation and
   no cosign signature.

The clean-up step fails with one of these messages, each of which tells you to
look for the draft, or to delete it, by hand:

- `Could not read the releases; delete the rehearsal draft for '<tag>' by hand.`
  (the list before any deletion, unreadable on the last read).
- `This job created a rehearsal draft for '<tag>', but the list of releases does
  not show it. Look for the draft by hand and delete it.` (still no draft after
  the repeated reads).
- `Could not read the releases after the deletion; check by hand that no draft for
  '<tag>' remains.` (unreadable on the last read after the deletion).
- `A release or draft for '<tag>' remains after the deletion. Delete it by hand.`
  (still listed after the repeated reads).

When the creation of the draft was started and failed, and the list shows no
draft for the tag after the repeated reads, the step does not fail. It warns:
`This job started the creation of a rehearsal draft for '<tag>' and the creation
failed.` Check the Releases page by hand for a draft for the tag and delete it
if there is one.

If the step finds a published Release for the tag, it fails with `A published
Release for '<tag>' exists; a rehearsal never publishes. Investigate.` Do not
delete anything before you know why.

The job refuses a rehearsal when a tag, a draft or a Release already exists for
the version. If a rehearsal draft is left behind, delete it by hand on the
Releases page, or rehearse the next number (for example `1.5.0-rehearsal.2`).

### First runs: what only GitHub can show

Only part of this flow has run on GitHub. The first `validate` run, on `main`,
passed the request check, the TLS lanes, the Docker lane and the builds, and
failed the packaged-tool smoke test because its version, `0.0.0-test`, was below
a provider's minimum engine version. A second `validate` run on `main`, with a
version of the form `1.5.0-validate.1`, was green. The first `rehearse` run, on
`main`, reached the publishing job and failed in the read-back of the new
draft: GitHub's list of releases lags behind a write, and it showed no draft
about a second after the draft was made. The clean-up step then found the draft
and deleted it, and the list showed nothing for the tag on the first read after
the deletion. The job now waits for the list for up to about a minute. The
upload and the NuGet login have not run, and `publish` mode has not run. A
check by hand with a maintainer's token, not the job's token, showed the same
lag (nothing listed 1.4 seconds after the creation, the draft listed 4 seconds
after it), the full commit as the draft's target, the release tool finding the
draft by its tag with the same id, and an upload by tag. The contract tests
(`scripts/ci/test_release_workflow.py`) check the workflow file and run its step
scripts with fixture commands; they cannot show the points below. Work through
them in order, and correct this document where a run shows something different.

A `validate` run, on `main` and from a feature branch, must show the points
below. The first three have been shown on `main`; the fourth, a run from a
feature branch, is still to do.

- [x] The packaged-tool smoke test passes with the version given to the run.
- [x] `sign-and-attest` runs and makes no attestation and no signature.
- [x] The run is green, and `publish-release`, `publish-marketplace` and
  `move-floating-tag` show as skipped.
- [ ] The same three points hold in a run from a feature branch.

The first `rehearse` run, on `main`, must show:

- [x] The run waits for approval of the `release` environment, and
  self-approval works.
- [x] The job token can list draft releases. The state check and the clean-up
  depend on it.
- [x] Creating the draft with the commit as its target creates no tag.
- [ ] The draft is read back as the only draft for the tag, with the full
  commit as its target, once the list shows it (the step fails otherwise).
- [ ] The release tool finds the new draft by its tag, with the id that was
  read back.
- [ ] Uploading assets to the draft by tag name works, and the asset names are
  unique.
- [ ] The NuGet token exchange succeeds in the `release` environment, both
  before and after the policy on NuGet.org gains the environment (see
  [NuGet.org publish](#nugetorg-publish-trusted-publishing)).
- [x] The rehearsal draft is deleted, also after a failed step.
- [ ] How many reads each wait needed. The log of each wait says
  `on attempt N of 12`; note the highest N here.
- [ ] Whether the inputs, the approval and the earlier attempt's artefacts
  carry over on **Re-run failed jobs**.

The first real release (ideally a pre-release) must show the items below. A
pre-release needs its own version heading in `CHANGELOG.md` and its own
`VouchfxEngineVersion` fallback in the release pull request; the stable release
that follows needs another.

- [ ] All packages are pushed.
- [ ] Publishing the draft creates the tag at the tested commit.
- [ ] The Release is marked "latest" only for a stable version.
- [ ] The notes are the `CHANGELOG.md` section followed by the generated notes.
- [ ] The cosign signing identity reads `release.yml@refs/heads/main`, with the
  trigger `workflow_dispatch`.
- [ ] The stricter commands in [Verifying artifacts](#verifying-artifacts) pass
  on an artefact of the release: `gh attestation verify` with `--signer-workflow`,
  `--source-ref` and `--source-digest`, and `cosign verify-blob` with
  `--certificate-identity`, `--certificate-github-workflow-trigger` and
  `--certificate-github-workflow-sha`. They have not been run against a real
  release yet. If one of them fails, correct this document.
- [ ] No attestation exists for the artefacts of a `validate` or `rehearse` run.
- [ ] The `move-floating-tag` job runs. For a pre-release that no floating tag
  tracks (for example `1.5.0-rc.1`) it is expected to move nothing.

The first stable release must also show:

- [ ] The Marketplace job publishes the extension (where its token is
  configured), and `move-floating-tag` moves `v1`.

---

## Artifact inventory

Each release attaches the following files:

| File | Description |
|------|-------------|
| `vouchfx.<ver>.nupkg` | dotnet global tool (primary distribution) |
| `vouchfx-<ver>-linux-x64.tar.gz` | Self-contained archive, Linux x64 |
| `vouchfx-<ver>-win-x64.tar.gz` | Self-contained archive, Windows x64 |
| `vouchfx-<ver>-osx-x64.tar.gz` | Self-contained archive, macOS x64 |
| `vouchfx-<ver>-osx-arm64.tar.gz` | Self-contained archive, macOS ARM64 |
| `vouchfx-<ver>-win-x64.msi` | Windows MSI installer |
| `vouchfx-<ver>-linux-x64.deb` | Debian/Ubuntu package |
| `vouchfx-<ver>-osx-arm64.pkg` | macOS package installer |
| `vouchfx-<ver>.bom.json` | CycloneDX SBOM (spec 1.7, JSON) |
| `vouchfx-<ver>.vsix` | VSCode extension |
| `*.cosign.bundle` | cosign keyless signature bundle per artifact |
| `*.nupkg.asc` | GPG detached signature for the nupkg (if `GPG_SIGNING_KEY` provisioned) |
| `*.deb.asc` | GPG detached signature for the .deb (if `GPG_SIGNING_KEY` provisioned) |

---

## Verifying artifacts

### Verify SLSA provenance (GitHub attestation)

Every artifact is attested with SLSA build provenance via
`actions/attest-build-provenance`.  Verification requires the `gh` CLI:

```bash
gh attestation verify vouchfx.1.2.3.nupkg \
  --repo tomas-rampas/vouchfx
```

Replace the filename with the artifact you want to verify.  A successful
verification prints the workflow that built the artifact and the ref it ran on.
For a release made by the current flow, that is expected to be `release.yml` on
`refs/heads/main`, started by `workflow_dispatch`. A release made before this
change names its tag (`…@refs/tags/v<version>`) instead.

**What this command proves.** It proves that an attestation for the file exists
and was made by some workflow run in this repository, on any ref. It does not
name the workflow, the ref or the commit; read the printed workflow and ref
yourself. Before the current flow, every manual dispatch of the release workflow
that reached the signing job wrote attestations, including five runs on `main`,
so a test artefact from that time can pass this command.

Only the `publish` mode of the release run attests artifacts. An artifact taken
from the run artefacts of a `validate` or `rehearse` run has no attestation, and
this command is expected to fail for it.

**Stricter command for a release made by the current flow.** Pin the signing
workflow, the ref and the commit that the release tag points at. Replace
`<full commit SHA of the tag>` with the 40-character SHA of that commit (for
example the output of `git rev-parse 'v<version>^{commit}'` in a clone that has
the tag):

```bash
gh attestation verify vouchfx.1.2.3.nupkg --repo tomas-rampas/vouchfx \
  --signer-workflow github.com/tomas-rampas/vouchfx/.github/workflows/release.yml \
  --source-ref refs/heads/main \
  --source-digest <full commit SHA of the tag>
```

A pass is expected to show that `release.yml` ran on `main` at that commit and
attested the file. Only `publish` runs attest, so that run was a `publish` run.
It does not show that this run is the one that released the file: a `publish`
run that failed after `sign-and-attest` attested its artefacts too, and a later
run at the same commit may have released other files. This command has not been
run against a real release yet, because none exists; the first real release is
the check (see [First runs](#first-runs-what-only-github-can-show)). For a
release made before the current flow, no stricter `gh` form is documented here.

### Verify cosign keyless signature

Each artifact ships with a `.cosign.bundle` (self-contained: signature +
Fulcio certificate + Rekor inclusion proof).  Verification requires the
`cosign` CLI (install: `brew install cosign` / `winget install sigstore.cosign`):

```bash
cosign verify-blob vouchfx.1.2.3.nupkg \
  --bundle vouchfx.1.2.3.nupkg.cosign.bundle \
  --certificate-identity-regexp \
    '^https://github\.com/tomas-rampas/vouchfx/\.github/workflows/release\.yml@.*' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

The identity pattern ends in `@.*`, so it accepts `release.yml` of this
repository on any branch or tag. The ref after the `@` in the certificate's
identity is the one the run used: `refs/heads/main` (trigger `workflow_dispatch`)
for a release made by the current flow, and `refs/tags/v<version>` for an older
one. Only `publish` runs make a cosign signature, so a `validate` or `rehearse`
artefact has no bundle.

**What this command proves.** A pass proves that some run of `release.yml` in
this repository, on any ref, signed the file, and that the signature is anchored
in the Sigstore Rekor public transparency log. No long-lived private key was
involved. It does not prove which ref, trigger or commit the run used. Before
the current flow, a manual dispatch of the release workflow skipped the cosign
steps, so, unlike the attestations above, no cosign signature exists for those
test runs.

**Stricter command for a release made by the current flow.** Pin the exact
identity, the trigger and the commit that the release tag points at (the
`<full commit SHA of the tag>` is the 40-character SHA, as for `gh` above):

```bash
cosign verify-blob vouchfx.1.2.3.nupkg \
  --bundle vouchfx.1.2.3.nupkg.cosign.bundle \
  --certificate-identity https://github.com/tomas-rampas/vouchfx/.github/workflows/release.yml@refs/heads/main \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-github-workflow-trigger workflow_dispatch \
  --certificate-github-workflow-sha <full commit SHA of the tag>
```

A pass is expected to show that `release.yml` ran on `main` at that commit, by
manual dispatch, and signed the file. As for `gh`, it does not show that this run
is the one that released the file.

**Stricter command for a release made before the current flow.** The identity
names the tag instead:

```bash
cosign verify-blob vouchfx.1.2.3.nupkg \
  --bundle vouchfx.1.2.3.nupkg.cosign.bundle \
  --certificate-identity https://github.com/tomas-rampas/vouchfx/.github/workflows/release.yml@refs/tags/v<version> \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

The stricter commands are expected to work. Their flags were checked against the
`gh` manual and the cosign v3.1.4 reference, but they have not been run against a
real release yet, because none exists made by the current flow. The first real
release is the check; it is on the [first-runs checklist](#first-runs-what-only-github-can-show).

### Verify GPG signature (nupkg, when provisioned)

If `GPG_SIGNING_KEY` was configured, a `vouchfx-<ver>.nupkg.asc` is also
attached.  To verify:

```bash
# Import the project's public key (published separately at keyserver or in docs/)
gpg --import vouchfx-public.asc
gpg --verify vouchfx.1.2.3.nupkg.asc vouchfx.1.2.3.nupkg
```

### Verify the SBOM

The `vouchfx-<ver>.bom.json` is a CycloneDX 1.7 JSON SBOM listing the
NuGet packages in the full solution dependency graph.  Inspect it with:

```bash
# Pretty-print
jq . vouchfx-1.2.3.bom.json | head -100

# Count components
jq '.components | length' vouchfx-1.2.3.bom.json

# Check for a specific package
jq '.components[] | select(.name == "Npgsql")' vouchfx-1.2.3.bom.json
```

Or load it into a CycloneDX-compatible tool (Dependency-Track, OWASP
Dependency-Check, Grype, Trivy, etc.) for vulnerability correlation.

---

## Primary vs secondary distribution

### Primary: dotnet global tool (nupkg)

**Recommended for developers with a .NET 10 SDK or runtime.**

```bash
dotnet tool install --global vouchfx
vouchfx --version
```

The tool is framework-dependent and requires .NET 10 or a newer major (runtime
or SDK) on the target machine; releases built before the move to .NET 10
require .NET 8. Stable Core distributions, beginning with `1.0.0`, bundle the engine DLLs and the complete,
version-matched DCP tools trees for `win-x64`, `linux-x64`, `osx-x64` and
`osx-arm64`. A source checkout or prior Aspire restore is not an installation
prerequisite. At topology start, `DcpPathResolver` selects the executing RID's
bundle beside the host assembly before stale build-machine cache metadata.
`ASPIRE_DCP_PATH` remains an explicit override for an existing DCP installation
(the directory containing `dcp`). The bundled tree is
`dcp/<VouchfxDcpVersion>/<rid>/tools/`. That property is 13.4.6 on current source, in lockstep
with `Aspire.Hosting` and `Aspire.AppHost.Sdk`; published releases through v1.2.1 bundle 13.4.2, and
a release cut from current source bundles 13.4.6. `AspireVersionLockstepGuardTests` fails the
unit-test run when `VouchfxDcpVersion`, the `Aspire.Hosting*` package versions (except
`Aspire.Hosting.Elasticsearch`) and the `Aspire.AppHost.Sdk` versions diverge.

**Legacy published rc.6:** this older package does not bundle DCP and requires
`aspire.hosting.orchestration.<rid>` version 13.4.2 in the executing machine's
NuGet cache, or an explicit `ASPIRE_DCP_PATH`. That legacy cache prerequisite
does not describe stable Core distributions from `1.0.0` onward. Installing the retired
Aspire workload does not provide the required NuGet package.

### Secondary: self-contained executables and installers

**For machines without a .NET SDK, or for OS-native package management.**

The self-contained archives (`*.tar.gz`) and native installers (`.msi`,
`.deb`, `.pkg`) bundle the .NET 10 runtime alongside vouchfx.  Users do not
need the .NET SDK or runtime installed.

**Bundled DCP portability:** current self-contained archives and installers
include the complete version-matched DCP tools tree for their own RID. They use
the same bundle-first resolution as the global tool; no pre-existing Aspire
NuGet cache is required. The `smoke-test-distribution-platforms` job in
`release.yml` installs the exact candidate into an isolated cache and launches
the bundled native DCP and archive CLI on all four advertised RIDs. The
`smoke-test-packaged-tool` job additionally runs the installed tool against a
live Docker topology with an empty user cache, checks the JUnit results and
rejects new Docker residue or a restored Aspire orchestration package. Both
jobs gate `publish-release`. Investigate the failing gate's logs before
retrying; a retry is not evidence that a portability defect is resolved.

**Single-file executables are NOT produced** (e.g. `PublishSingleFile=true`
is not used).  The Roslyn compiler in the engine uses `Assembly.Location` to
discover provider DLLs; single-file mode returns an empty string for that API,
breaking provider loading with an `IL3000` build error.  This is a hard engine
invariant (see CLAUDE.md).

---

## Human provisioning: certificate signing and trusted publishing

The following steps require provisioned credentials or human action on external
services.  The pipeline gates on them — signing steps are SKIPPED, not failed,
when the secret is absent, so releases still complete without them; NuGet
publishing succeeds via Trusted Publishing without any GitHub secret.

**Fail-closed behaviour — read before provisioning.**
When a signing secret is absent, the step is silently skipped and the release
proceeds with an unsigned artifact.  When a secret is *present but
misconfigured* (wrong passphrase, expired certificate, missing macOS
Developer-ID codesign pre-requisite, etc.) the signing step *fails* and the
entire release is blocked.  This is intentional — a broken signing
configuration must not produce an unsigned binary while appearing to have
signed it.

**Provision each signing method completely, or not at all.**
Each signing method requires two or more secrets (listed below).  Provisioning
only one of a pair (e.g. `CODESIGN_PFX_BASE64` without `CODESIGN_PFX_PASSWORD`)
will cause the step to run and fail immediately.  The `HAS_*` gate checks only
the primary secret; add all secrets for a method in the same step.

### Authenticode signing (Windows exe and MSI)

**What:** Signs `vouchfx.exe` and `vouchfx-<ver>-win-x64.msi` with an
Authenticode signature so Windows SmartScreen trusts the installer.

**Secrets required:**
- `CODESIGN_PFX_BASE64` — base64-encoded PKCS#12 (`.pfx`) certificate
- `CODESIGN_PFX_PASSWORD` — the certificate's password

**How to obtain a certificate:**
- Purchase an Extended Validation (EV) or Organisation Validation (OV) code-
  signing certificate from a CA trusted by Microsoft (DigiCert, Sectigo,
  GlobalSign, etc.).  EV certificates suppress SmartScreen immediately; OV
  certificates accumulate reputation over time.
- Or use Microsoft's Azure Trusted Signing service (formerly ACS):
  provision an Azure Trusted Signing account and replace the PFX-based signing
  steps in `release.yml` with the `azure/trusted-signing-action` action.

**How to provision:**
```bash
# Encode the .pfx file as base64
base64 -w 0 codesigning.pfx > codesigning.pfx.b64
# Add to GitHub repository secrets:
#   CODESIGN_PFX_BASE64   <- contents of codesigning.pfx.b64
#   CODESIGN_PFX_PASSWORD <- the certificate password
```

### Apple notarisation (macOS .pkg)

**What:** Submits the `.pkg` to Apple's Notary Service so macOS Gatekeeper
trusts the installer.

**Secrets required:**
- `APPLE_ID` — Apple ID e-mail address
- `APPLE_TEAM_ID` — 10-character Apple Developer team identifier
- `APPLE_APP_SPECIFIC_PASSWORD` — app-specific password generated at
  `appleid.apple.com > Security > App-Specific Passwords`

**How to obtain:**
- Enrol in the Apple Developer Programme (USD 99/year).
- Generate an app-specific password at https://appleid.apple.com.
- Your Team ID is visible in the Apple Developer portal.

**Note:** Apple notarisation also requires the binary (or the `.pkg`)
to be code-signed with a Developer ID certificate before submission.
The current skeleton does not include a code-signing step for macOS;
add `codesign --deep --sign "Developer ID Application: ..."` before
`pkgbuild` and `productsign` before notarytool if required.

### GPG signing (nupkg)

**What:** Produces a `.asc` detached signature for the nupkg so consumers
can verify the package without relying solely on NuGet's HTTPS.

**Secrets required:**
- `GPG_SIGNING_KEY` — base64-encoded armoured GPG private key
- `GPG_PASSPHRASE` — the key's passphrase

The signature is made only in `publish` mode.

**How to provision:**
```bash
# Generate a dedicated signing key (or export an existing one)
gpg --gen-key
gpg --export-secret-keys --armor <key-id> | base64 -w 0 > signing-key.b64
# Add to GitHub repository secrets:
#   GPG_SIGNING_KEY <- contents of signing-key.b64
#   GPG_PASSPHRASE  <- the key passphrase
# Publish the public key to a keyserver or in docs/
gpg --export --armor <key-id> > docs/vouchfx-gpg-public.asc
```

### NuGet.org publish (Trusted Publishing)

**What:** Pushes the nupkg to NuGet.org so users can install via
`dotnet tool install --global vouchfx`.

**Secret required:** None — uses NuGet.org Trusted Publishing.

**How it works:**

The publish step uses NuGet.org's Trusted Publishing feature, which eliminates
the need for long-lived API keys.  The publishing job (`publish-release`)
carries `id-token: write` permission and exchanges the GitHub OIDC token
(single-use) for a short-lived (~1 hour, reusable for multiple pushes)
NuGet.org API key via the `NuGet/login` action.  The `dotnet nuget push`
command then uses that ephemeral key, in `publish` mode only. A `rehearse` run
makes the exchange and pushes nothing. The job runs in the `release` environment
(only `main`; one required reviewer; self-approval allowed).

A Trusted Publishing policy was created on NuGet.org on 2026-07-05 with the
following settings:

| Field | Value |
|-------|-------|
| Package Owner | Tomas.R |
| Repository Owner | tomas-rampas |
| Repository | vouchfx |
| Workflow File | release.yml |
| Environment | (none) |

The table shows the policy as it is today. **The policy does not name an
environment yet, and the publishing job now runs in the environment `release`.**
The owner adds the environment to the policy between two rehearsals:

1. Run a rehearsal (see [Checking a candidate](#checking-a-candidate-validate-and-rehearse))
   with the policy as it is, and note whether the step `NuGet login (OIDC → short-lived API key)`
   succeeds. Whether the exchange succeeds before the policy names the
   environment has not been seen.
2. On NuGet.org, add the Environment `release` to the policy.
3. Run a second rehearsal and confirm that the same step succeeds.

Until then, the workflow's own conditions are what keep a `validate` run from
reaching NuGet: the job `publish-release`, which holds the login and the push,
runs only when the mode is not `validate`, the ref is `main` and the repository
is `tomas-rampas/vouchfx`, and the push step runs only in `publish` mode. The
policy itself names only the repository and the workflow file, so before the
environment is added nothing in it tells a `validate` run from a `publish` run.
Forks would in any case fail the token exchange since the policy names this
repository.

Those conditions are in `release.yml`, and a modified `release.yml` on a feature
branch controls its own conditions. So until the policy names the `release`
environment, anyone with write access to the repository can reach the NuGet token
exchange from a branch. Once the policy names the environment, the environment's
`main`-only rule applies to that exchange. This is no worse than before the
change.

**Policy state:** the policy became permanently active on the first successful publish, which locked
the GitHub repository and owner IDs in place. If a publish ever fails at the token exchange, check the
policy's state on the NuGet.org Trusted Publishing management page first.

---

## Documentation surfaces

The following topics belong in README.md or product documentation (not here):

- **Getting started / installation** — `dotnet tool install --global vouchfx`,
  system requirements.
- **Verifying a downloaded binary** — a shorter consumer-facing version of
  the cosign and gh-attestation verification commands above.
- **Upgrading** — `dotnet tool update --global vouchfx`.
- **Building from source** — for the self-contained exe use case.
- **How the bundled DCP works** — how stable distributions carry their own
  DCP tools, and the `ASPIRE_DCP_PATH` override, belong in the user docs, not
  in RELEASING.md.
