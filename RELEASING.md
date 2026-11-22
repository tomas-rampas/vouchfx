# Releasing vouchfx

This document covers how to cut a release, how consumers verify the artifacts,
the human provisioning steps (signing credentials and the NuGet Trusted
Publishing policy), and the distinction between the primary (nupkg) and
secondary (self-contained exe) distribution channels.

## How to trigger a release

The release pipeline (`release.yml`) publishes only when a GitHub Release is
published. Pushing a version tag alone does not start the pipeline. Manual
`workflow_dispatch` remains available as a non-publishing preflight.

1. In `CHANGELOG.md`, rename the `## [Unreleased]` heading to
   `## [<version>] — <date>` and add a fresh, empty `## [Unreleased]` heading
   above it, so the file keeps exactly one `## [Unreleased]` heading
   (`scripts/check_site.py` enforces this). Do this in the reviewed commit
   before tagging. Set the `VouchfxEngineVersion` fallback in
   `Directory.Build.props` to `<version>` in the same commit: the
   `SourceCapabilityStamp_MatchesTheNewestChangelogVersion` test fails when the
   newest versioned heading and the fallback differ. If a version heading is
   withdrawn before it is published, lower the fallback to the newest
   remaining versioned heading in that commit too.
2. Navigate to the GitHub Releases page for this repository.
3. Click **Draft a new release**.
4. Create a new tag in the format `v<semver>` (e.g. `v1.2.3`) at the reviewed
   commit, or select an existing reviewed version tag.
5. Fill in the release notes. Mark alpha, beta and release candidate versions
   as pre-releases.
6. Click **Publish release**. Publishing a reviewed draft or creating a published
   Release through the GitHub API emits the same event.
7. The pipeline builds all artifacts. Once the Docker integration lane has
   passed on the release commit (see below), it attests and cosign-signs them
   and attaches them to the release automatically. As its last job, the run
   moves the floating tag for the release. Expect a run to take about
   80 minutes, not the 15 or so it took before the lane joined it.

A tag push followed by Release publication starts one publishing run. A newer
run does not automatically cancel an existing run (`cancel-in-progress: false`);
manual cancellation remains possible.

### The Docker integration gate

Every release run, including the manual preflight, calls the Docker integration
lane (the `docker-integration` job, which runs
`.github/workflows/docker-integration.yml`) at the commit being released. The
lane starts with the run, beside the builds. It took about 75 minutes in CI, so
expect a release run to take about 80 minutes. The attestation and signing job
(`sign-and-attest`) and the publication job (`publish-release`) both need it, so
the Marketplace job, which needs both, waits for it too.

If the lane fails, is cancelled, times out (its limit is 120 minutes) or is
skipped, these are **withheld**: build-provenance attestations, cosign
signatures, the NuGet package signature, the release assets, the NuGet pushes,
the Marketplace extension and the move of the floating tags (`v1-alpha`,
`v1-rc`, `v1`). A floating tag is expected to stay where it was, because the job
that moves it (`move-floating-tag`, the last job of the run) needs the
publication jobs.

This holds only for a release whose tagged commit contains these workflows. For
a `release` event GitHub runs the workflow files of the tagged commit, and the
release tags made before this change, from `v1.0.0-alpha.6` on, carry the old
`move-floating-tag.yml`, which moves the floating tag as soon as the Release is
published, with no gate. So do not create or re-publish a GitHub Release for a
tag made before this change, do not tag a release on a commit that does not
contain this change (a commit from before it, or one on a branch cut before it),
and do not re-run an old **Move floating convenience tag** run. Each of these
can move the floating tag at once, and can move it backwards.

These are **not** withheld:

- The GitHub Release itself. Its tag and notes are public before the workflow
  starts, so a red lane leaves a public release with no assets and nothing on
  NuGet.org.
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

What to do about a red or timed-out lane on a release that is already public:

1. Read the lane's failure in the job expected to be shown as
   `Docker integration / Aspire topology and provider tests`. If it is a
   transient fault (a registry stall or rate limit, a runner fault), use
   **Re-run failed jobs** on the release run. That is expected to re-run the
   lane and, if it passes, `sign-and-attest`, `publish-release` and the
   Marketplace job on the same commit as the first attempt. A successful re-run
   is also expected to move the floating tag, as the last job (`move-floating-tag`).
   It has not yet been exercised on a release run, so confirm that those jobs
   ran, and check where the floating tag points. Do not move the
   tag before a re-run: the Marketplace job's checkout is expected to refuse a
   moved tag, and the other jobs would publish the original commit under a tag
   that no longer points at it.
2. If it is a real defect, or the pre-pull reports that a pinned image no longer
   exists, do not re-run: a re-run tests the same commit and the same image
   list. Fix it, and ship a new patch version from a new commit. The public
   release with no assets stays in place; you may edit its notes to say that it
   was not published to NuGet.org. The floating tags are not moved by a red
   lane, so `v1` stays where it was and consumers who reference it
   (the reusable `vouchfx-run.yml` workflow at `@v1`, or the GitLab template with
   `ref: v1`) are expected not to get this commit. Nothing needs moving back.
3. A timeout (the job ends at 120 minutes) is not known to be transient. Re-run
   once only if the log shows the time went to the pre-pull or to a runner
   stall; otherwise treat it as step 2.
4. There is no bypass: no input, variable or label skips the lane. Do not
   disable the job or edit the workflow to publish a release the lane did not
   pass.

Four cases concern the floating tags after the Docker lane has passed. In each,
"the manual run" means running **Move floating convenience tag**
(`move-floating-tag.yml`) by hand with a release tag. A manual run has neither
the commit check nor the no-downgrade check described below, so first confirm
that the tag and its release are the ones you mean and that, in the release run
for that tag, the Docker lane and `publish-release` succeeded. Its log reports
`Moved '<floating tag>'` on success.

**A publication job fails.** `move-floating-tag` needs `publish-release` and
`publish-marketplace`. If either fails after the lane has passed, the floating
tag does not move. Fix the cause and use **Re-run failed jobs**. That is expected
to re-run the failed job and the jobs after it, and then move the tag; it has not
yet been exercised. If only the Marketplace job failed and it cannot be fixed
soon, use the manual run with the release tag.

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
  GitHub API error also produces). Check that the release is still published and
  that its tag still exists, then use **Re-run failed jobs**. If the tag was
  refused as not valid semver, no floating tag tracks it; do nothing.

**A warning that the floating tag is already on a newer release.** The job can
end successfully with a warning that names the floating tag and the newer release
it is on. It moved nothing. That is expected when an older release run finishes,
or is re-run, after a newer release in the same line has moved the tag, or when
the release is a patch of an older minor line (a `v1.3.z` after `v1.4.0`). No
action is needed. To put the tag on the older release anyway, use the manual run.

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

The manual preflight (`workflow_dispatch`) runs the lane as well, so use it to
find a failing lane before you publish the Release.

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
  See [The Docker integration gate](#the-docker-integration-gate) if the job fails or warns.
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

### Smoke-test without cutting a tag (`workflow_dispatch`)

- In the GitHub Actions UI, select `Release (signed, provenance)` and
  click **Run workflow**.
- Select the reviewed candidate ref and enter the version to validate.
- The pipeline runs the Docker integration lane (it took about 75 minutes in CI; see
  [The Docker integration gate](#the-docker-integration-gate)), builds all
  artifacts (nupkg, per-RID tarballs, SBOM, VSIX, MSI, deb, pkg), creates
  provenance attestations once the lane has passed and runs distribution checks.
- **NuGet, GitHub Release and Marketplace publication are skipped**, and so is
  the `move-floating-tag` job, so a preflight moves no floating tag. The
  cosign steps are also skipped; optional certificate-based signing remains
  dependent on configured secrets.
- **Attestations are public records**, including on preflight runs; skipping
  package publication does not make the run private.
- Use this before publishing the next stable Release to validate the pipeline and native
  installer builds. It does not establish that the public packages are available.

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
verification prints the workflow that built the artifact and its git ref.

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

A successful verification confirms:
- The file was produced by this repository's release workflow.
- The signature is anchored in the Sigstore Rekor public transparency log.
- No long-lived private key was involved.

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
the need for long-lived API keys.  The workflow carries `id-token: write`
permission and exchanges the GitHub OIDC token (single-use) for a short-lived
(~1 hour, reusable for multiple pushes) NuGet.org API key via the `NuGet/login`
action.  The `dotnet nuget push` command then uses that ephemeral key.

A Trusted Publishing policy was created on NuGet.org on 2026-07-05 with the
following settings:

| Field | Value |
|-------|-------|
| Package Owner | Tomas.R |
| Repository Owner | tomas-rampas |
| Repository | vouchfx |
| Workflow File | release.yml |
| Environment | (none) |

The workflow gates the login and push steps to the canonical repository
(`github.repository == 'tomas-rampas/vouchfx'`) and skips them on `workflow_dispatch`
smoke runs; the policy itself would accept any matching run of release.yml in this
repository regardless of trigger, so this workflow-side guard is what keeps smoke runs
from publishing. Forks would in any case fail the token exchange since the policy
names this repository.

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
