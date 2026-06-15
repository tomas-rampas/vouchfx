# Telemetry

## Privacy-first, opt-in design

vouchfx can collect **anonymous, aggregate usage telemetry** to help prioritise the engine and understand which features are most valuable to the community. **Telemetry is OFF by default — nothing is collected or sent unless you explicitly opt in.**

This document explains what data is collected, how to enable or disable telemetry, where your data is stored, and the technical guarantees that protect your privacy.

## What is collected

The telemetry allowlist is small and intentionally aggregate-only. When telemetry is enabled, vouchfx collects:

- **Tool, engine, and .NET runtime versions** — to understand which versions are in use.
- **Run counts and scenario counts** — how many runs and scenarios executed.
- **Per-verdict step and scenario counts** — how many steps and scenarios passed, failed, hit environment errors, or were inconclusive (counts only, never which step or scenario).
- **Step family counts** — only the six built-in Core families (`http`, `db-assert`, `script`, `mq-publish`, `mq-expect`, `webhook-listen`) are emitted as count keys. Any custom or non-Core provider's family is bucketed under the constant key `"custom"`, so an author-chosen family id is never written into the telemetry event.
- **Step provider counts** — only the six built-in Core provider ids (`http.rest`, `db-assert.postgres`, `script.csharp`, `mq-publish.kafka`, `mq-expect.kafka`, `webhook-listen.http`) are emitted as count keys. Any custom or non-Core provider's full id is bucketed under `"custom"`, so an author-chosen provider id never leaves the machine.
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

In v1, telemetry events are **persisted locally only** — they are appended to a JSON Lines file on your machine and never sent to any remote endpoint. This local outbox stands in for a future hosted pilot backend (out of scope in this release).

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

1. Consent is **Enabled** (you have run `vouchfx telemetry enable`).
2. The `--no-telemetry` flag was **not passed** on the current `vouchfx run` invocation.
3. The `VOUCHFX_NO_TELEMETRY` environment variable is **not set** (or is empty/whitespace).

If any one of these is false, telemetry is suppressed for that run. This conjunction-of-consent model ensures that a single opt-out signal at any level (global consent, run flag, or environment variable) prevents emission.

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

In v1, the local outbox is the only transport. This is intentional — telemetry is fully under your control and visible on your machine. A future hosted backend (out of scope for v1) will consume this outbox format, but will be optional and will respect the same opt-in / opt-out controls.

### "What happens if I disable telemetry mid-run?"

If you disable telemetry while a `vouchfx run` is in progress, the current run may still emit an event to the outbox (because the decision to emit is made at run start). Future runs will not emit until you re-enable.

### "Does `--no-telemetry` appear in my shell history?"

Yes, like any other CLI flag. If you want to avoid recording the flag itself, use the environment variable instead: `export VOUCHFX_NO_TELEMETRY=1` in your `.bashrc` or equivalent, or set it inline per-run.

## Questions and feedback

If you have questions about telemetry, privacy, or data handling, please open an issue on [GitHub](https://github.com/vouchfx-org/vouchfx/issues) or refer to [`SECURITY.md`](../SECURITY.md) for responsible disclosure.
