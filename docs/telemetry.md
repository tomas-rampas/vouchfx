# Telemetry

## Privacy-first, opt-in design

vouchfx can collect **anonymous, aggregate usage telemetry** to help prioritise the engine and understand which features are most valuable to the community. **Telemetry is OFF by default — nothing is collected or sent unless you explicitly opt in.**

This document explains what data is collected, how to enable or disable telemetry, where your data is stored, and the technical guarantees that protect your privacy.

## What is collected

The telemetry allowlist is small and intentionally aggregate-only. When telemetry is enabled, vouchfx collects:

- **Tool, engine, and .NET runtime versions** — to understand which versions are in use.
- **Run counts and scenario counts** — how many runs and scenarios executed.
- **Per-verdict step and scenario counts** — how many steps and scenarios passed, failed, hit environment errors, or were inconclusive (counts only, never which step or scenario).
- **Step family counts** — only the eleven built-in Core families (`http`, `db-assert`, `mq-publish`, `mq-expect`, `cache-assert`, `mail-expect`, `webhook-listen`, `metrics-assert`, `storage-assert`, `trace-expect`, `script`) are emitted as count keys. Any custom or non-Core provider's family is bucketed under the constant key `"custom"`, so an author-chosen family id is never written into the telemetry event.
- **Step provider counts** — only the twenty-five built-in Core provider ids (the frozen v1 Core catalogue, `http.rest` through `script.csharp` — the [project README's provider list](project-readme.md) is the authoritative enumeration) are emitted as count keys. Any custom or non-Core provider's full id — including Community providers such as `rpc.json-rpc` — is bucketed under `"custom"`, so an author-chosen provider id never leaves the machine. *(Engines released up to `v1.0.0-alpha.5` counted only the original six provider ids — later Core ids appear under `"custom"`, and steps in the five newer families under `"custom"` for their family count too.)*
- **Startup and time-to-first-test durations** — wall-clock milliseconds from run start to first scenario start, and to first step completion.
- **Anonymous install identifier** — a random GUID minted only when you opt in; it identifies this installation only, never the user, machine, or any test content.
- **Telemetry schema version** — to allow future backends to evolve the data shape.
- **UTC timestamp** — when the event was recorded.

## What is NEVER collected

The telemetry record has **no place to store**:

- Test file contents or step definitions.
- Captured variable values or placeholders.
- Secret references or values (e.g. `${secret:vault/db-password}`).
- System-under-test addresses, URLs, or hostnames.
- Container image names.
- Scenario names or step IDs.
- Provider observations or step output.
- Any customer data whatsoever.

This "provably never sent" guarantee is **enforced by two structural CI gates**:

1. **Allowlist-reflection test** — the `TelemetryEvent` record's public properties are a closed, hand-curated allowlist. Adding any field fails the build and forces a privacy review. No field can be added without explicit scrutiny.
2. **Denylist-serialisation scan** — a synthetic telemetry event is seeded with a SUT URL, container image name, secret reference, captured value, scenario name, and raw step text, serialised to JSON, and asserts that none of those sensitive substrings appear anywhere in the output. This test runs on every build and proves that the allowlist structure itself prevents sensitive data from leaking.

These gates are permanent parts of the CI pipeline. If you trust the codebase, you can trust the allowlist — the privacy contract is structural, not just policy.

## Enabling and disabling telemetry

Telemetry is managed via three simple commands:

### Enable telemetry

```bash
vouchfx telemetry enable
```

This opts you in and mints a unique, anonymous install identifier (a GUID) if one does not already exist. You will see:

```
Telemetry ENABLED. Anonymous, aggregate usage data (versions, verdict counts,
which built-in step kinds ran, startup timings) will be collected on each run.
Your test contents, captured values, secrets, URLs, image names, scenario
names and step ids are NEVER collected.
Install id: 12345678… (anonymous; identifies this install only).
Opt out any time with: vouchfx telemetry disable
```

### Disable telemetry

```bash
vouchfx telemetry disable
```

This opts you out immediately. The install identifier is **deleted from disk** and the **local outbox is cleared** at once, severing every link to past activity well within data retention requirements. You will see:

```
Telemetry DISABLED. The install id has been deleted and the local outbox
cleared. Nothing will be collected or sent.
```

### Check telemetry status

```bash
vouchfx telemetry status
```

This shows your current consent state and whether an install identifier exists. For privacy, the command prints only a short, non-reversible prefix of the install ID (never the full GUID), so the `status` command itself does not leak a stable identifier to a log:

```
Telemetry consent : enabled (opted in)
Install id        : present (12345678…)
Outbox path       : /home/user/.config/vouchfx/telemetry-outbox.jsonl
Opt in            : vouchfx telemetry enable
Opt out           : vouchfx telemetry disable  (or set VOUCHFX_NO_TELEMETRY=1)
```

## Suppressing telemetry per-run

Even when you have opted in globally, you can suppress telemetry for a single run using either the `--no-telemetry` flag or the `VOUCHFX_NO_TELEMETRY` environment variable.

### Using the flag

```bash
vouchfx run ./tests --no-telemetry
```

This run will not emit any telemetry event.

### Using the environment variable

```bash
export VOUCHFX_NO_TELEMETRY=1
vouchfx run ./tests
```

Set `VOUCHFX_NO_TELEMETRY` to any non-empty value (e.g. `1`, `true`, `yes`) to opt out. An empty, unset, or whitespace-only value means telemetry is not suppressed.

The environment variable doubles as the **production-run exclusion** — CI/automation environments that cannot easily pass a CLI flag can set `VOUCHFX_NO_TELEMETRY=1` to guarantee no telemetry is emitted from automated test suites.

## Where data is stored (v1)

In v1, telemetry events are **persisted locally only** by default — they are appended to a JSON Lines file on your machine. There is no maintainer-hosted backend, and none is planned; the [reference backend](https://github.com/tomas-rampas/vouchfx-telemetry-backend) exists for self-hosting.

However, you can optionally configure vouchfx to drain the local outbox to a backend telemetry endpoint. See [Sending telemetry to a backend (opt-in transport)](#sending-telemetry-to-a-backend-opt-in-transport) below.

### Storage location

Telemetry data is stored in the per-user application-data directory:

- **Windows:** `%APPDATA%\vouchfx\` (typically `C:\Users\<you>\AppData\Roaming\vouchfx\`)
- **Linux/macOS:** `~/.config/vouchfx/`
- **macOS (alternative):** `~/Library/Preferences/vouchfx/`

Two files live there:

- **`telemetry.json`** — your consent state (enabled/disabled/undecided) and install identifier.
- **`telemetry-outbox.jsonl`** — the accumulated telemetry events (one JSON object per line).

When you run `vouchfx telemetry disable`, both files are cleaned up — the install ID is deleted and the outbox is removed immediately.

### Local file format

The outbox is a **JSON Lines file** (one JSON object per line, UTF-8 without a byte-order mark). Each line is a compact, allowlisted telemetry event recording the fields described above. The file is **append-only** — successive opted-in runs accumulate one event per line. It is cleared only when you run `vouchfx telemetry disable`. Automatic log rotation or capping is deferred to future hosted-backend work.

The file is safe for consumption by local analysis tools (for instance, you could parse the outbox and analyse your own patterns). No remote transmission happens in v1.

## Install-identifier lifecycle

The install identifier is a random GUID that:

- **Is minted only when you run `vouchfx telemetry enable`** — not before, not on first run, not until you explicitly opt in.
- **Is preserved across runs while consent holds** — so your installation's history can be linked while telemetry remains enabled.
- **Is deleted immediately when you run `vouchfx telemetry disable`** — severing the link to all past activity.
- **Is never associated with machine identifiers, user names, IP addresses, or test content** — it identifies this particular installation only.

If you re-enable telemetry after disabling it, a new install identifier is minted. Old and new identities are unlinked.

**One exception:** [environment-configured telemetry (CI)](#environment-configured-telemetry-ci) bypasses the local store entirely — when `VOUCHFX_TELEMETRY_INSTALL_ID` is set alongside the endpoint and token variables, the run emits under that supplied identifier and nothing above applies to it. That mode deliberately keeps one stable identity across ephemeral CI runners.

## First-run notice

When consent is undecided (i.e. you have not yet run `vouchfx telemetry enable` or `disable`), the first time you run `vouchfx`, a one-time notice is printed to stderr informing you of the telemetry feature, what is collected, and how to opt in or out:

```
vouchfx can collect anonymous, aggregate usage telemetry (tool/engine/.NET
versions, step + scenario verdict counts, which built-in step kinds ran, and
startup timings) to help prioritise the engine. It NEVER collects your test
contents, captured values, secrets, URLs, image names, scenario names, or step
ids.

Telemetry is OFF by default and NOTHING is sent unless you opt in:
  enable  : vouchfx telemetry enable
  opt out : vouchfx telemetry disable   (or set VOUCHFX_NO_TELEMETRY=1)
  status  : vouchfx telemetry status

This notice is shown once.
```

This notice is shown **exactly once** per machine, even while consent stays undecided. Once you have made an explicit decision (enabling or disabling), the notice never appears again.

## Suppression rules (v1)

Telemetry emission requires **all** of the following to be true:

1. Consent is granted — either **Enabled** locally (you have run `vouchfx telemetry enable`), **or** the run is [environment-configured](#environment-configured-telemetry-ci) (all three `VOUCHFX_TELEMETRY_*` variables set — an equally explicit, per-environment opt-in).
2. The `--no-telemetry` flag was **not passed** on the current `vouchfx run` invocation.
3. The `VOUCHFX_NO_TELEMETRY` environment variable is **not set** (or is empty/whitespace).

If any one of these is false, telemetry is suppressed for that run. This conjunction-of-consent model ensures that a single opt-out signal at any level (consent, run flag, or environment variable) prevents emission — the opt-out signals in points 2 and 3 override **both** consent channels.

## Future work: per-file opt-out (v2)

In v1, telemetry suppression is global — you control it via the `telemetry` command, the `--no-telemetry` flag, or the environment variable. A per-file opt-out (a `metadata.telemetry: off` field in the `.e2e.yaml` file) was intentionally deferred because it would require a new field on the frozen YAML metadata schema, which is prohibited by the v1 schema-freeze gates (`SchemaFreezeTests`). This constraint is enforced by the CI pipeline and cannot be bypassed.

In v2, after the schema-freeze gates are relaxed, a per-file opt-out will become possible. For now, use global consent or the per-run flags.

## Troubleshooting

### "How do I know telemetry is really off?"

If you have not run `vouchfx telemetry enable`, telemetry is OFF. Check the status with `vouchfx telemetry status` — if consent is "undecided", no data has been collected. The outbox file does not exist until you enable telemetry.

### "How do I delete all telemetry data?"

Run `vouchfx telemetry disable`. This deletes your install identifier and clears the local outbox file immediately. If you want to be extra cautious, you can manually delete the entire `vouchfx` folder in your config directory (see "Where data is stored" above), but `telemetry disable` does this for you.

### "Can I inspect the outbox file?"

Yes. The outbox is a readable JSON Lines file. You can view it with any text editor or parse it with standard JSON tools:

```bash
cat ~/.config/vouchfx/telemetry-outbox.jsonl | jq .
```

Each line is a complete telemetry event.

### "Why is telemetry written to disk, not sent immediately?"

In v1, the local outbox is the only default transport. This is intentional — telemetry is fully under your control and visible on your machine. A backend you self-host (see the [reference implementation](https://github.com/tomas-rampas/vouchfx-telemetry-backend)) consumes this outbox format; sending is optional and respects the same opt-in / opt-out controls.

### "What happens if I disable telemetry mid-run?"

If you disable telemetry while a `vouchfx run` is in progress, the current run may still emit an event to the outbox (because the decision to emit is made at run start). Future runs will not emit until you re-enable.

### "Does `--no-telemetry` appear in my shell history?"

Yes, like any other CLI flag. If you want to avoid recording the flag itself, use the environment variable instead: `export VOUCHFX_NO_TELEMETRY=1` in your `.bashrc` or equivalent, or set it inline per-run.

## Sending telemetry to a backend (opt-in transport)

vouchfx can optionally drain telemetry events from the local outbox to a remote HTTP endpoint. This feature is **OFF by default** — no network calls occur unless you explicitly configure both an endpoint and an authentication token. The transport is **best-effort and fail-silent** — a network outage, slow endpoint, or misconfiguration never affects your test runs.

### Transport configuration

Configure the HTTP transport by setting two environment variables:

- **`VOUCHFX_TELEMETRY_ENDPOINT`** — the absolute base URL of your telemetry ingest endpoint (required). Must be `http://` or `https://` (relative URLs and other schemes are rejected silently, keeping the transport inert). An example: `https://telemetry.example.com` or `https://telemetry.example.com/api` (an operator's base path is preserved when resolving relative resource paths). **HTTPS is strongly recommended for production** — the Bearer token and aggregate payload would otherwise traverse cleartext.

- **`VOUCHFX_TELEMETRY_TOKEN`** — a bearer token for authentication (required). Carried only on the HTTP `Authorization: Bearer` header and never logged, printed, or echoed in diagnostics or error messages (§17 secret handling).

The transport is **inert until BOTH variables are set** and the endpoint is a valid absolute HTTP/HTTPS URI. A missing, empty, relative, or malformed endpoint leaves telemetry in local-outbox-only mode — no network call is ever attempted.

A third, optional variable — `VOUCHFX_TELEMETRY_INSTALL_ID` — turns the pair into fully [environment-configured telemetry](#environment-configured-telemetry-ci), designed for CI.

### Environment-configured telemetry (CI)

On an ephemeral CI runner the consent store does not survive between jobs, so opting in with `vouchfx telemetry enable` would mint a fresh install identifier on every run — each CI job would appear in the backend as a brand-new install with exactly one run, inflating install counts and destroying per-repository signal.

Environment-configured telemetry fixes this with one additional variable:

- **`VOUCHFX_TELEMETRY_INSTALL_ID`** — a GUID you choose **once per repository/project** (e.g. from `uuidgen`) and store in your CI settings alongside the endpoint and token.

When **all three** `VOUCHFX_TELEMETRY_*` variables are present and valid, setting them *is* the opt-in for that environment: no `telemetry enable` step is needed, the local consent store is bypassed entirely (nothing is written to `telemetry.json`, and the first-run notice is suppressed), and every run emits under the supplied identifier — so all of a repository's CI runs aggregate under one stable install identity.

Rules:

- **All three or nothing.** The install id without a configured endpoint/token — or vice versa — leaves the ordinary store-based behaviour in force; nothing new happens.
- **An invalid (non-GUID, or all-zero) install id is ignored** with a single diagnostic naming the variable, falling back to store-based behaviour. Telemetry never breaks a run.
- **Opt-outs always win.** `--no-telemetry` and `VOUCHFX_NO_TELEMETRY=1` suppress emission in this mode exactly as they do locally.
- **Choose a random GUID.** The identifier is pseudonymous exactly like a locally minted one — it identifies the CI environment you configured it for, and nothing else.

The GitHub Actions reusable workflow (`.github/workflows/vouchfx-run.yml`) exposes matching `telemetry-*` inputs, and the GitLab template (`ci/gitlab/vouchfx-run.gitlab-ci.yml`) documents the equivalent project variables — see each file's header for the worked example.

### How it works

After each test run, if telemetry is enabled and the HTTP transport is configured:

1. Events in the local outbox are appended first (unchanged — this guarantees the local outbox always receives every event).
2. The client then attempts to drain the outbox by POSTing batches to `{endpoint}/v1/telemetry` with `Authorization: Bearer <token>` and `Content-Type: application/x-ndjson`. Batches are limited to 500 lines and ~1 MB per POST to keep requests bounded.
3. Only batches that receive a 2xx HTTP response are removed from the outbox. A non-2xx response, network error, or timeout leaves the batch and all subsequent batches intact for the next run.
4. **All faults are silent** — a network outage, timeout, or endpoint misconfiguration never affects the test run or its verdict.

### Outbox retention and draining

The local outbox is capped to prevent unbounded growth, particularly in long-running or high-frequency environments. You can override the defaults with three optional environment variables:

- **`VOUCHFX_TELEMETRY_OUTBOX_MAX_BYTES`** — maximum outbox size in bytes (default: 5 MB). Oldest events are dropped first when this ceiling is exceeded.

- **`VOUCHFX_TELEMETRY_OUTBOX_MAX_AGE_DAYS`** — discard events older than this many days (default: 30 days).

- **`VOUCHFX_TELEMETRY_OUTBOX_MAX_LINES`** — maximum outbox line count (default: 10,000 lines). Oldest events are dropped first.

All three caps are enforced independently and use oldest-first eviction. Even when the HTTP transport is unconfigured, the local outbox is still capped so it never grows unbounded.

### Cross-run back-off

If the remote endpoint is unreachable or returns an error, the client applies exponential back-off with jitter across runs. The back-off state is persisted to disk, so a hard-down endpoint does not trigger a drain attempt on every run — the back-off window grows geometrically instead. A healthy endpoint resets the back-off immediately.

### Drain budget

Each run has an overall drain budget of approximately **15 seconds**. If the endpoint is slow, the drain stops cleanly once the budget elapses, persists progress (undelivered batches carry to the next run), and does not advance the back-off (some batches DID deliver successfully). This prevents a slow endpoint from adding tens of seconds of post-run latency.

### Data sent

The payload remains the same allowlist (see [What is collected](#what-is-collected) above). The transport **never re-serialises telemetry events** — it sends the raw JSON Lines bytes that the local sink wrote, byte-for-byte, so the transport physically cannot widen what is sent beyond the privacy allowlist.

### Disabling telemetry with a backend configured

When you run `vouchfx telemetry disable`, the CLI:

1. Deletes your local install identifier.
2. Clears the local outbox.
3. **Best-effort**: sends a POST to `{endpoint}/v1/telemetry/forget` with your install ID (best-effort and fail-silent), asking the backend to disassociate any records with that ID.

If the backend is unreachable, the forget request is silently skipped — the local data is deleted regardless, and no further events will be sent from this installation.

### Backend availability

This is the **client-side transport layer**. When you point `VOUCHFX_TELEMETRY_ENDPOINT` and `VOUCHFX_TELEMETRY_TOKEN` at a compatible ingest endpoint, the transport **is fully live and will drain your outbox to it** over HTTP.

An official vouchfx telemetry backend implementing the frozen `/v1/telemetry` and `/v1/telemetry/forget` contract is publicly available at [https://tomas-rampas.github.io/vouchfx-telemetry-backend/](https://tomas-rampas.github.io/vouchfx-telemetry-backend/) ([source](https://github.com/tomas-rampas/vouchfx-telemetry-backend)) — source-complete and CI-tested for self-hosting. Before sending any data, you can [verify exactly what would be sent using the local outbox](https://tomas-rampas.github.io/vouchfx-telemetry-backend/docs/why-telemetry.html#verify-exactly-what-would-be-sent-the-local-outbox). No maintainer-hosted instance is running or planned, so telemetry remains local-only by default unless you self-host the backend. Any HTTP service implementing the frozen contract works as a backend. **Until you configure both environment variables, telemetry remains local-only.** When you are ready to send telemetry to a backend, obtain the endpoint URL and authentication token from the backend operator (or from your [self-hosted deployment](https://tomas-rampas.github.io/vouchfx-telemetry-backend/docs/self-hosting.html)), then set the environment variables above. **HTTPS is strongly recommended for production** — the Bearer token and aggregate payload would otherwise traverse cleartext.

## Questions and feedback

If you have questions about telemetry, privacy, or data handling, please open an issue on [GitHub](https://github.com/tomas-rampas/vouchfx/issues) or refer to [SECURITY.md](https://github.com/tomas-rampas/vouchfx/blob/main/SECURITY.md) for responsible disclosure.
