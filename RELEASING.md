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
- In every mode, the pre-release may not hold an upper-case letter:
  `1.5.0-RC.1` is refused, and the message names `1.5.0-rc.1` instead. The
  .NET tool store keeps the version in lower case. A probe on Linux with the
  .NET 10 SDK installed a tool packed as `1.0.0-RC.1` under
  `.store/<tool>/1.0.0-rc.1/`, so the smoke tests, which build that path from
  the version as given, are expected to fail after the build on Linux. NuGet
  compares versions without regard to case, so nothing is lost.
- In every mode, a version that starts with `0.0.0-` is refused. It is below
  every provider's minimum engine version, so the packaged-tool smoke test
  would fail it after the build. The message names the form to use instead.
- In `rehearse` and `publish`, the run must be on `main`; `commit` must be the
  full 40-character commit of the run; the repository must be the canonical one;
  no tag `v<version>` may exist; no published Release may exist for it; and none
  of the NuGet package IDs may already have that version.
- In `publish` only, a stable version may not be below a published stable
  Release: `1.4.1` is refused once `v1.5.0` is published, because it would be
  marked "latest" in its place (see case 9 in
  [Recovering a publication that stopped part-way](#recovering-a-publication-that-stopped-part-way)).
  A pre-release is not affected. The publishing job applies the same rule again
  for a late re-run of an old run.
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
  `1.5.0-rehearsal.1` or `1.5.0-validate`). Those versions are reserved for
  rehearsal and validation, so a rehearsal version can never be released
  later; `0.0.0-` versions are refused in every mode. The step of the publishing
  job that pushes to NuGet.org also refuses them, and any mode but `publish`,
  before it pushes anything.
- The version becomes the engine version of the packaged tool. The
  packaged-tool smoke test runs that tool, and the engine refuses to load any
  provider whose minimum engine version is higher than its own version (by
  SemVer precedence). Apart from the `0.0.0-` versions, `prepare` does not
  compare a version with the providers' minimums: a lower one, such as
  `0.0.1-test` or `0.0.0` in `validate`, passes `prepare` and then fails the
  packaged-tool smoke test. So for `validate` and `rehearse` use the next release
  version as `X.Y.Z` (for example `1.5.0-validate.1` and `1.5.0-rehearsal.1`).
  A pre-release sorts below its release, so `1.5.0-rehearsal.1` and
  `1.5.0-rc.1` are lower than `1.5.0`. If a release raises a provider's
  minimum engine version to the release version itself, its rehearsals and its
  release candidates fail the packaged-tool smoke test, and so does a
  `validate` run of the form above. For a rehearsal or a `validate` run, use a
  higher `X.Y.Z`, for example `1.5.1-rehearsal.1` or `1.5.1-validate.1`. A
  release candidate cannot do that, because its version is the one it
  releases: give the provider a minimum that the candidate meets, or release
  the stable version without a candidate. `prepare` does not check
  this; the smoke test fails after the build, and in `rehearse` and `publish`
  mode that failure also skips the publishing job.

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
   `Verify the tag and the published release` ends with
   `Verified: v<version> is at <commit> and its Release is published.` Check
   that the packages are on NuGet.org; after the first pre-release it listed
   the last of them, the tool package, about 17 minutes after its push. After
   each release, also read the `Full Changelog` link at the end of the Release
   notes. It should start at the newest earlier stable release. For the first
   pre-release GitHub started it at an older stable release, and GitHub is
   expected to do the same for the next pre-release (see
   [First runs](#first-runs-what-only-github-can-show)). If the link starts at
   another release, correct the generated part of the notes on the Release
   page by hand. A request to GitHub's endpoint for generated notes that names
   the starting tag (`previous_tag_name`) returns the right text.
8. Work through the [documentation checklist](#release-time-documentation-checklist).

A newer run does not cancel an existing run (`cancel-in-progress: false`);
manual cancellation remains possible. Only a `rehearse` or `publish` run on
`main` uses the shared concurrency group `release-publish`, so two of those never
run at once, and GitHub keeps at most one pending run in a group: a third
`rehearse` or `publish` dispatch on `main` is expected to replace the one still
pending. Every other run has a group of its own. That includes a `validate` run
and a `rehearse` or `publish` dispatch from another branch, which `prepare`
refuses, so neither can take the place of a pending release run.

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
   full commit SHA, and uploads all assets to it. It then reads the draft by
   its id: it must be the draft for the tag, at the tested commit, with the
   asset name of each uploaded file and no other. If not, the job stops before
   anything is pushed to NuGet.org (see the last rule before the cases below).
   A `rehearse` run makes the same check.
6. `publish-release` pushes the NuGet packages.
7. `publish-release` reads the tag again, and then the draft again by its id,
   with the same check as in step 5. The tag must be absent or at the tested
   commit. If either is wrong, the job stops and publishes nothing to GitHub
   Releases (see case 4 below).
8. `publish-release` publishes the draft. That creates the lightweight tag
   `v<version>` at the tested commit. A version with a `-` suffix is marked as
   a pre-release and is not marked "latest".
9. `publish-release` checks that the tag points at the tested commit.
10. The Marketplace job (`publish-marketplace`).
11. `move-floating-tag`.

NuGet goes before the publication of the Release because it is the step most
likely to fail, and a draft is not public. The Release title is the tag. Nothing
in the run pushes a tag with git: the tag appears when the draft is published.
Attestations, cosign signatures and the GPG signature of the package are made
only in `publish` mode, because each is a permanent public record.

The draft is published by its tag (`gh release edit v<version> --draft=false
--target <commit>`), not by its id. Publishing by id was considered and left
as it is: the call by tag is the one the first release used, and a call by id
could not be tried before the next real release. The read of the draft by its
id just before that call narrows the time in which the tag could name another
release; it does not close it.

Each read of the draft by its id is made up to 12 times in all, 5 seconds
apart, while the read fails or the draft lists only some of the uploaded asset
names: GitHub may lag behind the upload. A draft that is no longer a draft, has
another tag or target commit, or lists an asset name that this run did not
upload is not lag, and the job stops at once.

`publish-release` has a limit of 30 minutes, against GitHub's default of 360,
so that a call that hangs ends the job. The wait for approval of the `release`
environment comes before the job starts and is not counted. The limit is many
times the longest run of the job so far. If the job stops at its limit, find
the last step that started in its log and follow the case for a failure of
that step (upload: case 1; NuGet pushes: case 2 or 3; publication or
verification: cases 4 to 6). How GitHub reports a job that reached its limit,
and whether **Re-run failed jobs** picks it up, has not been seen: a timed-out
job is expected to show as failed and to be re-run like any other failed job.

The first pre-release made by this flow went through these steps in this order
on GitHub, except the two reads of the draft by its id in steps 5 and 7, which
were added after it. For what that run showed and what is still to show, see
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
  does it upload the assets, once. After the upload it reads the draft by its
  id, again up to 12 times, until the draft lists every uploaded asset name;
  a `rehearse` run does this too. Nothing has been pushed to NuGet.org when
  any of these failures happens. If the command that creates the draft fails,
  a draft may still have been stored: look at the drafts for the tag on the
  Releases page before you re-run. After the draft is created, the step fails
  in one of seven ways:
  - The list shows more than one entry for the tag, or one entry that is not a
    draft or not at the tested commit. The message says that the new draft
    `did not read back as expected` and what it found. This is not lag, so the
    step fails at once, and it uploads and pushes nothing. In a rehearsal the
    clean-up is expected to delete the draft. In a `publish` run this is not
    expected to happen: it means that someone made a draft for the tag during
    the run, or that an assumption about GitHub is wrong. Look at the drafts
    for the tag on the Releases page and do not re-run until you understand
    it. Two drafts can also both come from this run: one from an earlier
    attempt whose draft the job did not see, and one from the re-run. The
    unseen draft can come from an attempt in which the list never showed the
    draft (the next failure), in which the list could not be read on the last
    attempt (the third failure), or in which the command that creates the
    draft failed after GitHub had stored the draft. In that case delete one of
    the two by hand, so that exactly one remains, and use **Re-run failed
    jobs**.
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
  - The draft, read by its id after the upload, is not as uploaded. Each
    message says `Nothing was pushed to NuGet.org` and says that a rehearsal
    deletes its draft in the clean-up step. In `publish` mode the draft is left
    in place; look at it on the Releases page before you re-run:
    - `Could not read the draft` (after 12 attempts): GitHub may not have
      answered. If the Releases page shows the draft with all the assets, use
      **Re-run failed jobs**, which replaces it.
    - `still lacks assets this run uploaded` (after 12 attempts): the upload
      ended, but the draft does not list every file. The upload may have gone
      to another release for the tag; look for one on the Releases page and do
      not re-run until you understand it.
    - `is no longer the draft for v<version> at this run's commit`, or `holds
      asset names that this run did not upload`: someone published, moved or
      changed the draft during the run. This is not lag, so the step fails at
      once. Do not re-run until you understand it. If the draft now targets
      another commit, a re-run stops in the classification; delete the draft
      by hand (case 1). In `publish` mode, if the Release is now published, a
      re-run is expected not to replace it: it compares only the asset names
      (case 8), then pushes to NuGet.org, moves the floating tag where one
      tracks the version and, for a stable version, publishes to the
      Marketplace. First compare each asset's `digest` with the run's
      artefacts, as in case 4, and re-run only if they match.
    - `has no usable id`: a defect in the workflow. Do not re-run before it is
      understood.

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
4. **The publication step refused the tag or the draft.** The packages are
   already on NuGet.org and nothing was published to GitHub Releases. Read the
   log of `Publish the draft release`:
   - It says that the tag `now resolves to` another commit and that `nothing was
     published`. Someone made the tag after the run started. Find out who made
     the tag and why before you delete or move it. Once the tag is gone, or
     points at the tested commit, **Re-run failed jobs** is expected to publish
     the draft.
   - It says something about the draft. The step read the draft by its id
     just before the publishing call, with the same check as after the upload
     (see the last rule above), making a failed read or a read with missing
     assets up to 12 times in all, 5 seconds apart. Each message says
     `Nothing was published to GitHub Releases` and points here. Look at the
     Releases page before you re-run:
     - `Could not read the draft` (after 12 attempts): GitHub may not have
       answered. If the Releases page shows the draft unchanged, use **Re-run
       failed jobs**.
     - `has no usable id`: a defect in the workflow, not a change to the
       draft. Do not re-run before it is understood.
     - `is no longer the draft for v<version> at this run's commit`, `holds
       asset names that this run did not upload`, or `still lacks assets this
       run uploaded`: the draft changed after the upload. Someone published
       it, moved it to another tag or commit, or changed its assets. Find out
       what changed before you re-run.

     If the draft is still a draft at the tested commit, **Re-run failed
     jobs** is expected to replace it, upload the assets again, skip the
     packages already on NuGet.org and check the draft again. If the draft now
     targets another commit, the re-run stops in the classification; delete
     the draft by hand and continue as for a draft that is gone. If the draft
     is gone, the re-run is refused as in case 7; the packages came from this
     run, so make a draft by hand as the third rule above describes, and then
     use **Re-run failed jobs**. If the Release is now published, the re-run
     is expected to compare only its asset names (case 8), and then to move
     the floating tag and, for a stable version, to publish to the
     Marketplace. So if someone else published it, first check its assets
     against this run's: compare each asset's `digest`
     (`gh api repos/tomas-rampas/vouchfx/releases/tags/v<version> --jq '.assets[] | [.name,.digest] | @tsv'`)
     with the run's artefacts, or verify them as described in
     [Verifying artifacts](#verifying-artifacts). Re-run only if they match.
     This check has not run on GitHub yet.
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
   why the Release has no tag. If the tag points at another commit, follow
   case 5.
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
   refused.** A stable version is refused if a published stable Release with a
   higher version exists, because the version "would be marked Latest in its
   place". The message begins `Refusing to publish` and ends by naming the fix:
   release a stable version above the newest one. `prepare` applies this rule
   to a new `publish` dispatch, before anything is built, so a stable patch of
   an older line cannot be released after a newer stable one. The
   classification in `publish-release` applies the same rule again when no
   Release is published yet (state `none` or `draft`), so an old failed run
   cannot be re-run after a newer stable release. Pre-releases are not
   affected, and neither is a run whose Release is already published. If the
   draft of an older stable version must still be released, decide by hand
   whether it may be marked Latest; the workflow does not do it. The check in
   `prepare` has not run on GitHub yet.
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
`v1.4.0`) is refused by `prepare`, before the build; see case 9 above. No action
is needed.
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
   (there is no default; `prepare` refuses a `0.0.0-` version such as
   `0.0.0-test`, see its checks above). Leave `commit` empty.
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
   draft by its tag, then uploads the assets to it once. It then reads the
   draft by its id, in the same way, until the draft lists every uploaded
   asset name, as a `publish` run does before its NuGet pushes. It runs the NuGet
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

This flow has run on GitHub in all three modes, but not every path of it has.
The first `validate` run, on `main`, passed the request check, the TLS lanes,
the Docker lane and the builds, and failed the packaged-tool smoke test because
its version, `0.0.0-test`, was below a provider's minimum engine version.
`prepare` now refuses such a version before the build. A
second `validate` run on `main`, with a version of the form
`1.5.0-validate.1`, was green. The first `rehearse` run, on
`main`, reached the publishing job and failed in the read-back of the new
draft: GitHub's list of releases lags behind a write, and it showed no draft
about a second after the draft was made. The clean-up step then found the draft
and deleted it, and the list showed nothing for the tag on the first read after
the deletion. The job now waits for the list for up to about a minute. A second
`rehearse` run, on `main`, was green. The list showed the new draft on the
second read, and the release tool found it on the first. The upload of 20 files
and the NuGet login passed. The clean-up deleted the draft, the list showed
nothing for the tag on the first read after the deletion, and no tag was left.
The run cannot show the state of the policy on NuGet.org. The table in
[NuGet.org publish](#nugetorg-publish-trusted-publishing) recorded no
environment in the policy at the time of that run. Before the second rehearsal,
a check by hand with a maintainer's token, not the job's token, showed the same
lag (nothing listed 1.4 seconds after the creation, the draft listed 4 seconds
after it), the full commit as the draft's target, the release tool finding the
draft by its tag with the same id, and an upload by tag.

The first `publish` run, on `main`, was for a pre-release and was green. The
list showed the new draft on the second read, and the release tool found it on
the first. The run uploaded 40 files (the 20 release files and a cosign bundle
for each), pushed six packages and five symbol packages to NuGet.org, and
published the draft, which created the tag at the tested commit. The check after
publication read the tag and the Release on the first attempt. The Release was
marked as a pre-release, and the newest stable release stayed "latest". The
Marketplace job checked the `.vsix` and published nothing, because the
Marketplace takes no version with a pre-release suffix, and `move-floating-tag`
moved nothing. The run also showed that no Marketplace token was configured at
the time, so a stable release is expected to publish nothing to the Marketplace
until one is.

One thing was wrong in that run: the generated part of the notes started at an
older stable release, not at the newest one, so it also listed pull requests
that the newest stable release had already shipped. The run does not name the
starting release; GitHub chooses it, and for the tag of a pre-release it chose
wrongly. Requests by hand to GitHub's endpoint for generated notes, at the same
commit, started at the same older release for other pre-release tag names, and
at the newest stable release for stable tag names. So the notes of a pre-release
are expected to start at the wrong release again, and the notes of a stable
release at the right one.

The contract tests (`scripts/ci/test_release_workflow.py`) check the workflow
file and run its step scripts with fixture commands; they cannot show the points
below. Work through them in order, and correct this document where a run shows
something different.

A `validate` run, on `main` and from a feature branch, must show the points
below. The first three have been shown on `main`; the fourth, a run from a
feature branch, is still to do.

- [x] The packaged-tool smoke test passes with the version given to the run.
- [x] `sign-and-attest` runs and makes no attestation and no signature.
- [x] The run is green, and `publish-release`, `publish-marketplace` and
  `move-floating-tag` show as skipped.
- [ ] The same three points hold in a run from a feature branch.

A `rehearse` run, on `main`, must show the points below. The two runs so far
have shown the ticked ones.

- [x] The run waits for approval of the `release` environment, and
  self-approval works.
- [x] The job token can list draft releases. The state check and the clean-up
  depend on it.
- [x] Creating the draft with the commit as its target creates no tag.
- [x] The draft is read back as the only draft for the tag, with the full
  commit as its target, once the list shows it (the step fails otherwise).
- [x] The release tool finds the new draft by its tag, with the id that was
  read back.
- [x] Uploading assets to the draft by tag name works, and the asset names are
  unique.
- [x] The NuGet token exchange succeeds in the `release` environment before the
  policy on NuGet.org names the environment. The run shows the exchange; that
  the policy named no environment rests on the table in this document.
- [ ] The NuGet token exchange succeeds in the `release` environment after the
  policy on NuGet.org names the environment (see
  [NuGet.org publish](#nugetorg-publish-trusted-publishing)).
- [x] The rehearsal draft is deleted, also after a failed step.
- [x] How many reads each wait needed. The log of each wait says
  `on attempt N of 12`. In the second rehearsal the highest N was 2, for the
  read-back of the new draft; its other waits succeeded on the first read.
  Correct this figure if a later run shows a higher one.
- [ ] Whether the inputs, the approval and the earlier attempt's artefacts
  carry over on **Re-run failed jobs**.

The first real release must show the items below. It was a pre-release, and it
has shown the ticked ones. A pre-release needs its own version heading in
`CHANGELOG.md` and its own `VouchfxEngineVersion` fallback in the release pull
request; the stable release that follows needs another.

- [x] All packages are pushed.
- [x] Publishing the draft creates the tag at the tested commit.
- [x] How many reads the check after publication needed. Its log says
  `on attempt N of 12`. No rehearsal runs this check. In the first pre-release N
  was 1. Correct this figure if a later run shows a higher one.
- [x] A pre-release is not marked "latest".
- [x] The notes are the `CHANGELOG.md` section followed by the generated notes.
  The generated notes started at the wrong release, as described above.
- [x] The cosign signing identity reads `release.yml@refs/heads/main`, with the
  trigger `workflow_dispatch`.
- [x] The stricter commands in [Verifying artifacts](#verifying-artifacts) pass
  on an artefact of the release: `gh attestation verify` with `--signer-workflow`,
  `--source-ref` and `--source-digest`, and `cosign verify-blob` with
  `--certificate-identity`, `--certificate-github-workflow-trigger` and
  `--certificate-github-workflow-sha`.
- [x] No attestation exists for the artefacts of a `validate` or `rehearse` run.
  This was checked for the SBOM and the `.vsix` of one run of each mode.
- [x] The `move-floating-tag` job runs. For a pre-release that no floating tag
  tracks (for example `1.5.0-rc.1`) it moves nothing.

The first stable release must also show:

- [ ] The Release is marked "latest".
- [ ] The generated notes start at the newest earlier stable release, not at a
  pre-release or an older stable release.
- [ ] The Marketplace job publishes the extension, if its token has been
  configured by then.
- [ ] `move-floating-tag` moves `v1`.

The checks below were added after the first pre-release. The contract tests run
them with fixture commands; none has run on GitHub yet. The next runs are
expected to show:

- [ ] `prepare` refuses, in any mode, a version with an upper-case letter in its
  pre-release (for example `1.5.0-RC.1`) and a version that starts with
  `0.0.0-`, with a message that names the form to use, and no other job runs.
- [ ] `prepare` refuses a `publish` dispatch of a stable version below a
  published stable Release (for example `1.4.1` once `v1.5.0` is published),
  with a message that names the fix, and no other job runs. It accepts the
  versions it accepted before: `1.5.0`, `1.5.0-rc.1`, `1.5.1-rehearsal.1` in
  `rehearse`, and `1.5.0-validate.1` in `validate`.
- [ ] In a `publish` run, the step that pushes to NuGet.org passes its new
  refusal (mode `publish`, a version that is not reserved) and pushes as before.
- [ ] In a `rehearse` or `publish` run, the read of the draft by its id after
  the upload finds the draft, at the tested commit, with the asset names this
  run uploaded. Its log says `is the draft for v<version> at this run's commit,
  with the <N> uploaded assets, on attempt <A> of 12` (40 assets for a release
  with cosign bundles). The next rehearsal can show this, and how many
  attempts GitHub needs after an upload; correct this document if `A` is
  higher than 1.
- [ ] In a `publish` run, the read of the draft by its id just before the
  publishing call logs the same line, and the publication follows.
- [ ] `publish-release` ends well within its limit of 30 minutes.

The installers of a pre-release changed after the first pre-release (see
[Secondary: self-contained executables and installers](#secondary-self-contained-executables-and-installers)).
The contract tests run the `.deb` build step with a fixture `dpkg-deb`, and a
local container check installed packages built by that step. A `validate` run
from the branch of that change, with the version `1.5.0-validate.1`, has shown
the two ticked items; the last one is still to show:

- [x] In a `validate` run, `build-msi` builds the MSI with
  `AllowSameVersionUpgrades`, with no warning in the `wix build` log, and its
  install check passes. The run used WiX 4.0.6; the install log names
  `ProductVersion = 1.5.0`, and the MSI was installed on the runner and
  removed again.
- [x] In a `validate` run, the `.deb` of the version given carries
  `Version: 1.5.0~validate.1`, and its install check passes; its file name
  keeps the full version (`vouchfx-1.5.0-validate.1-linux-x64.deb`).
- [ ] The first stable release after the change: the stable MSI replaces an
  installed pre-release MSI of the same number, leaving one entry in
  **Installed apps** and one PATH entry. No job of the release workflow
  installs two MSIs, so this needs a manual check on Windows: a person's
  machine, or a one-off job on a hosted Windows runner outside the release
  workflow.

---

## Artifact inventory

Each release attaches the following files:

| File | Description |
|------|-------------|
| `vouchfx.<ver>.nupkg` | dotnet global tool (primary distribution) |
| `Vouchfx.Sdk.<ver>.nupkg` | Provider SDK library (the frozen v1 provider contract) |
| `Vouchfx.Sdk.Testing.<ver>.nupkg` | Test harness library for providers built outside this repository |
| `Vouchfx.Engine.Abstractions.<ver>.nupkg` | Engine abstractions library |
| `Vouchfx.Engine.Authoring.<ver>.nupkg` | Engine authoring library (YAML parser and AST builder) |
| `Vouchfx.Engine.Compilation.<ver>.nupkg` | Engine compilation library |
| `<id>.<ver>.snupkg` | Symbol package for each of the five libraries above (the tool package has none) |
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

Without the GPG signatures that makes 20 files: six packages, five symbol
packages, four archives, three installers, the SBOM and the `.vsix`. In a
`publish` run each of them has a cosign bundle, so 40 assets are uploaded. The
first pre-release made by the current flow had exactly these 40 assets and no
GPG signature.

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
For a release made by the current flow, that is `release.yml` on
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
this command fails for it.

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
run at the same commit may have released other files. This command passes for a
package of the first pre-release made by the current flow, and fails for it when
another commit is given. For a release made before the current flow, no stricter
`gh` form is documented here.

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

The stricter command for the current flow passes, with cosign 3.0.2, for a
package of the first pre-release made by that flow. It fails for it when another
commit or another trigger is given, and so does the command with the tag
identity. The command for a release made before the current flow has not been
run against such a release.

The bundles are in the new Sigstore bundle format. cosign 3 reads that format by
default. cosign 2.4.1 needs the extra option `--new-bundle-format`; without it,
it stops with `bundle does not contain cert for verification`.

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

**Installer versions of a pre-release:** the file names always carry the full
release version (`vouchfx-1.5.0-rc.1-linux-x64.deb`,
`vouchfx-1.5.0-rc.1-win-x64.msi`). The version inside the package differs:

- *Debian package.* The `Version:` field of a stable release is `X.Y.Z`; that
  of a pre-release is `X.Y.Z~<pre-release>`, for example `1.5.0~rc.1`. A
  hyphen inside the pre-release is written as `.` (`1.5.0-rc-1` gives
  `1.5.0~rc.1`), because a hyphen in that field starts a Debian revision; so
  two pre-releases that differ only in `-` and `.` give the same Debian version,
  and should not both be released. Likewise keep to the dotted pre-release
  forms (`alpha.N`, `rc.N`): mixed forms such as `rc1` and `rc.1` can order
  differently in Debian than in SemVer. In Debian's ordering `~` sorts before the
  end of the string. Checked with `dpkg --compare-versions` in Ubuntu 24.04 and
  Debian 12 containers, not on GitHub: `1.5.0` is above `1.5.0~rc.1`,
  `1.5.0~rc.2` above `1.5.0~rc.1`, and `1.5.0~rc.10` above `1.5.0~rc.9`. In
  the same containers, packages built by the workflow's own step with a
  stand-in payload installed in the order `rc.1`, `rc.2`, stable as upgrades
  with `dpkg -i`, and `apt-get install ./<file>` reported the stable package as
  an upgrade of `rc.1`. A pre-release installed after the stable release of the
  same number is a downgrade: `apt` asks for confirmation, and with `-y`
  refuses it without `--allow-downgrades`.
  `1.5.0-rc.1` was published before this change, and its package says
  `1.5.0`, the same as the stable package; see the entry in `CHANGELOG.md`.
- *Windows MSI.* Windows Installer takes only a numeric version and compares
  only its first three fields, so the MSI of a pre-release is built with the
  number of its stable release (`1.5.0-rc.1` gives `1.5.0`). `vouchfx.wxs`
  sets `AllowSameVersionUpgrades="yes"` on `MajorUpgrade`, so an MSI with the
  same number (each build has a new product code) is expected to replace the
  installed product, not to install beside it. What this is expected to mean,
  from the WiX and Windows Installer documentation and not yet seen on a
  Windows machine: the stable MSI replaces an installed pre-release MSI of the
  same number; a second pre-release MSI replaces the first; the same MSI file
  run twice finds its own product installed and does not install a second
  copy; and **a pre-release MSI run after the stable MSI of the same number
  replaces the stable release**. That last point is the price of the
  attribute: Windows Installer cannot tell `1.5.0-rc.1` from `1.5.0`, so do not
  install a pre-release MSI over the stable release of the same number unless
  you mean to. The MSI of a `validate` or `rehearse` run carries that number
  too. An MSI of a lower number is still refused with the downgrade
  message. The WiX build (the `wix` .NET tool, 4.0.x in this workflow) does
  not run the ICE validation, so ICE61's warning about removing an equal
  version is not expected in the build log; the WiX 4.0.6 compiler source has
  no warning for the attribute. The `build-msi` job of a `validate` run has
  shown the build, with no warning, and the install check of one MSI (see
  First runs); it does not install one MSI over another.

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
A rehearsal has passed the step `NuGet login (OIDC → short-lived API key)`. The
run cannot show the state of the policy; this document recorded it as the table
shows. Two steps remain for the owner:

1. On NuGet.org, check that the policy is as the table shows, and add the
   Environment `release` to it. If the policy already names an environment,
   first correct the table and the rehearse check-list in
   [First runs](#first-runs-what-only-github-can-show).
2. Run another rehearsal (see [Checking a candidate](#checking-a-candidate-validate-and-rehearse))
   and confirm that the same step succeeds. Whether the exchange succeeds once
   the policy names the environment has not been seen.

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
