# Getting Started with vouchfx

**Welcome to vouchfx** — a declarative testing platform for distributed systems.

This 60-minute guide walks you through your first end-to-end integration test: from checking that your environment is ready, through installing the `vouchfx` CLI, authoring a minimal `.e2e.yaml` test file, running it, and interpreting the verdict. By the end, you will have a PASS and a working understanding of how the platform works.

## What is vouchfx?

vouchfx tests **distributed systems end-to-end** by letting you author tests in declarative YAML. A test describes a system under test (REST services, databases, message brokers in Docker containers), then orchestrates their topology, runs ordered steps (HTTP calls, database assertions, scripts) against discovered endpoints, and reports a clear verdict: **Pass**, **Fail**, **Environment error** (infrastructure breakage), or **Inconclusive** (timeout / unmet condition).

The pipeline is: `.e2e.yaml` → validate vs JSON Schema → compile YAML→AST→C#→Roslyn (once) → orchestrate containers via Aspire/Testcontainers → execute steps → collect verdict → unload & reset.

This guide focuses on the essentials. For architecture, the provider model, secrets, and advanced features, see the [Technical Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md).

## Prerequisites

**What you need on your machine.**

### .NET 8 SDK

vouchfx targets **.NET 8 LTS**. Check your SDK version:

```bash
dotnet --version
```

You should see an `8.0.x` version (8.0.400 or later). Building from source requires an 8.0.x SDK — `global.json` pins it — and the packaged tool needs the .NET 8 *runtime* (included with the SDK); a newer major SDK alone (9.x or later) satisfies neither. If you only have a newer version, [install .NET 8.0 LTS](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) alongside it — SDKs and runtimes install side by side.

### Docker daemon

vouchfx orchestrates containerised topologies using Docker. The engine starts containers, health-gates them, and cleans them up. Verify Docker is running:

```bash
docker info
```

You should see version and runtime information. On Windows, this typically means [Docker Desktop](https://www.docker.com/products/docker-desktop/) with the Linux engine running; on macOS, Docker Desktop likewise; on Linux, a running `dockerd`.

## Installing vouchfx

vouchfx is packaged as a `dotnet` global tool and is live on NuGet.org as a pre-release. Install and
upgrade it straight from NuGet.org:

```bash
# Install (pre-release channel — required while the published versions are 1.0.0-alpha.x)
dotnet tool install --global vouchfx --prerelease

# Upgrade later
dotnet tool update --global vouchfx --prerelease
```

From the v1.0 GA release onwards the plain commands (without `--prerelease`) work too.

The installer places the `vouchfx` command in `~/.dotnet/tools` (`%USERPROFILE%\.dotnet\tools` on Windows). If your shell cannot find `vouchfx` afterwards, add that directory to your `PATH` or open a fresh shell — the installer only updates `PATH` for new sessions.

**The Aspire orchestration prerequisite.** The tool drives container topologies through .NET Aspire's DCP orchestrator. At run time the engine locates the DCP binaries in your per-user NuGet package cache (`NUGET_PACKAGES` if set, otherwise `~/.nuget/packages/`), in the `aspire.hosting.orchestration.<rid>` package matching your machine's platform and the engine's pinned Aspire version (currently 13.4.2). Any machine that has restored a project carrying `Aspire.AppHost.Sdk` 13.4.2 already has it. On a completely fresh machine, populate the cache once before your first run — clone this repository and restore it:

```bash
git clone https://github.com/tomas-rampas/vouchfx.git
dotnet restore vouchfx/vouchfx.sln
```

(Restoring any other project that carries `Aspire.AppHost.Sdk` at the same version works equally well. Do **not** use the retired `dotnet workload install aspire` command — the Aspire workload was discontinued with Aspire 9, installs the wrong version to the wrong location, and cannot satisfy this prerequisite.)

Without the cached orchestration package, `vouchfx run` reports an environment error — never a test verdict — whose message names the exact missing package and this remedy. If you keep DCP somewhere non-standard, point the `ASPIRE_DCP_PATH` environment variable at the directory containing the `dcp` executable instead.

> **Known defect in 1.0.0-alpha.5 and earlier:** those pre-releases resolve DCP only through a path baked in on the release build machine, so the NuGet-installed tool fails with `The Aspire orchestration component is not installed at "/home/runner/..."` on every other machine regardless of your cache. Fixed in **1.0.0-alpha.6** — upgrade (`dotnet tool update --global vouchfx --prerelease`), or run from source (below). Full write-up: [KB: DCP orchestrator not found](kb/dcp-orchestrator-portability.md).

## Building vouchfx from source

If you want the latest unreleased engine — or you are contributing — build from source. Clone the repository if you haven't already:

```bash
git clone https://github.com/tomas-rampas/vouchfx.git
cd vouchfx
```

Build the entire solution:

```bash
dotnet build vouchfx.sln
```

This compiles the engine, providers, and the CLI runner. The build takes a minute or two and should produce zero warnings.

Alternatively, if you want only the CLI (faster), build that project:

```bash
dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release
```

## Authoring your first test

### The structure of a `.e2e.yaml` file

A test file has four top-level sections; only `steps` is mandatory:

- **`metadata`** — name, owner, tags, description. Used for reporting and filtering; no execution effect.
- **`environment`** — `services` (containers under test), `dependencies` (managed Aspire resources like databases), optional `seed` (SQL/fixtures applied after topology is healthy).
- **`variables`** — optional constants pre-loaded into the shared context, available to all steps.
- **`steps`** — the ordered list of actions and assertions (HTTP calls, database queries, scripts, listeners).

### The worked example

Here is a minimal, complete, runnable test. Copy this into a new file `hello-world.e2e.yaml` in your project:

```yaml
metadata:
  name: getting-started-hello-world
  owner: vouchfx-newcomers
  tags: [getting-started, hello-world]
  description: >-
    The first vouchfx test — a single http.rest GET against traefik/whoami.
    Asserts the response is HTTP 200 and captures the container hostname
    via JSONPath for demonstration; any later step could use the captured
    value via {hostname} placeholder substitution.

environment:
  services:
    whoami:
      image: traefik/whoami
      httpPort: 80

steps:
  - id: whoami-GET-api
    type: http.rest
    target: whoami
    method: GET
    path: /api
    expect:
      status: 200
    capture:
      hostname: "$.hostname"
```

**What each part does:**

| Field | Role |
|-------|------|
| `metadata.name` | Human-readable test name for reports. |
| `metadata.owner` | Team or person responsible; useful for filtering large suites. |
| `metadata.tags` | Labels to select subsets of tests (e.g., `--tag smoke`). |
| `environment.services.whoami` | A named service under test. The `image:` must be a valid OCI image (defaults to Docker Hub). |
| `httpPort: 80` | Tells vouchfx the container listens on port 80; the engine health-gates this port before running steps. |
| `steps[0].id` | Unique step identifier. Used in reporting and for referencing this step's captures in later steps. |
| `type: http.rest` | The step family and provider: make an HTTP request. |
| `target: whoami` | Which service to call (matches a name in `environment.services`). |
| `method: GET` | HTTP verb (GET, POST, PUT, DELETE, etc.). |
| `path: /api` | The URL path. Logically the request goes to `http://<target>:<httpPort><path>`; in practice the engine calls the endpoint Aspire discovers for the container, so reports show `localhost` and an ephemeral proxy port. |
| `expect.status: 200` | Assert the response is HTTP 200. If the status code differs, the step fails. |
| `capture.hostname: "$.hostname"` | JSONPath expression. Extracts the `hostname` field from the JSON response body and stores it in `Vars["hostname"]`. Any later step can reference this as `{hostname}`. |

The `traefik/whoami` container is a tiny utility that responds to any HTTP request with a description of itself: on the `/api` path the response is a JSON object, including a `hostname` field (other paths return a plain-text equivalent). This makes it a perfect pedagogical system under test: it is guaranteed to respond, requires no configuration, and the response contains extractable data.

**Where to save your test file:**

You can save it anywhere in your project tree. For example:

- `examples/getting-started/hello-world.e2e.yaml` (in the vouchfx repo itself, as shipped)
- `tests/e2e/hello-world.e2e.yaml` (a typical project layout)
- Any directory with a `.e2e.yaml` extension is discoverable by the `vouchfx` CLI

## Running your test

You now have the packaged `vouchfx` CLI installed on your PATH (from "Installing vouchfx"). From the repository root (scenario paths resolve relative to your current directory), run the example:

**Using the packaged global tool (recommended):**

```bash
vouchfx run examples/getting-started
```

To run a single file instead of a directory, specify it directly:

```bash
vouchfx run examples/getting-started/hello-world.e2e.yaml
```

These commands run the repository's bundled copy of the example; if you authored your own `hello-world.e2e.yaml` elsewhere (as in the previous section), point `vouchfx run` at that file or directory instead.

This command:

1. Discovers all `.e2e.yaml` files in `examples/getting-started/` (or runs the single file if given).
2. Validates each file against the JSON Schema.
3. For each scenario, starts the container topology (the `whoami` service), health-gates it, runs the steps in order, and collects verdicts.
4. Renders a terminal report with per-step verdicts.

### Building from source (contributors)

If you are building vouchfx from source (as a contributor), you can invoke the CLI directly from its build output instead of the global tool.

**Build the CLI once:**

```bash
dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release
```

**Run the example from the built binary:**

```bash
src/Cli/Vouchfx.Cli/bin/Release/net8.0/vouchfx run examples/getting-started
```

(On Windows the binary is `vouchfx.exe`, so the command is `…\net8.0\vouchfx.exe run examples\getting-started`.)

**Tip:** Add the `src/Cli/Vouchfx.Cli/bin/Release/net8.0` directory to your PATH (or create an alias `vouchfx`) so you can type just `vouchfx run <path>`. Alternatively, if you prefer to use `dotnet run`, you must pass an **absolute** scenario path (because `dotnet run` executes from the project directory):

```bash
dotnet run --project src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -- run "$(pwd)/examples/getting-started"
```

**What a successful run looks like:**

You should see output similar to:

```
Scenario 'getting-started-hello-world' started
  step 'whoami-GET-api' started
  step 'whoami-GET-api': PASS (19 ms)
    provenance:
      captured 'hostname' <- step 'whoami-GET-api' ($.hostname)
Scenario 'getting-started-hello-world': PASS  (pass=1 fail=0 envError=0 inconclusive=0)
```

(Before the scenario line, you may see Aspire/topology startup messages such as "Distributed application started.")

On an interactive terminal, the verdict is colour-coded and prefixed with a shape glyph: `[+]` for PASS, `[x]` for FAIL, `[!]` for ENV_ERROR, or `[?]` for INCONCLUSIVE. When output is piped (as in CI), glyphs and colours are omitted and you see plain text as shown above. The process exit code (0 on Pass) is the shell's `$?`, not a printed line.

**If something goes wrong:**

- **ENV_ERROR — HealthGate timeout:** the `whoami` container failed to become healthy. Check that Docker is running and can pull `traefik/whoami`. Run `docker pull traefik/whoami:latest` to warm the cache. Note that a cold image pull can trip a shorter internal DCP per-resource startup window (120 seconds is our outer gate, but DCP has internal timeouts — so pre-pulling and warming images helps on first run).
- **FAIL — assertion error:** a step assertion failed (e.g., the HTTP status was 500, not 200). You will see a diff of expected vs. actual beneath the step line. Verify the service is running and the path `/api` is correct.
- **INCONCLUSIVE — timeout or unmet condition:** the step took longer than its timeout, or a capture expression did not match the response. Unlikely on localhost; more common with slow networks or overloaded machines.

## The four verdicts

vouchfx keeps four outcomes distinct everywhere (reporting, exit codes, CI gating):

| Verdict | Meaning | Breaks CI by default? |
|---------|---------|---|
| **Pass** | The scenario and all steps succeeded. | No |
| **Fail** | A step assertion failed (e.g., status was 500, not 200). This is a product defect. | **Yes** |
| **Environment error** | Infrastructure broke (container unhealthy, image pull failed, seed failed). | No |
| **Inconclusive** | The engine could not decide (timeout, unmet capture, partition outlasted grace period). | No |

This distinction is crucial: a `Fail` means your code is broken; an `EnvironmentError` means your CI infrastructure is broken. By default, only `Fail` breaks the build — allowing you to distinguish product defects from infrastructure problems. You can opt in to gating on the others with `--fail-on-env-error` and `--fail-on-inconclusive`. Note: when every discovered scenario fails to parse (a directory of malformed files, or a single malformed file), the entire run is classified as Inconclusive and exits 4 unconditionally, independent of the `--fail-on-inconclusive` flag — matching the behaviour of `vouchfx validate`.

## Generating reports

By default, `vouchfx run` prints a plain-text terminal report. You can also emit:

### HTML report

A self-contained, interactive HTML file with per-step details, captured variables, and the reproducibility envelope (no secret values embedded):

```bash
vouchfx run examples/getting-started --html report.html
```

Open `report.html` in your browser to see a detailed timeline of each step, captured values, and any failures.

### JUnit XML

For CI integration (GitHub Actions, GitLab, Jenkins):

```bash
vouchfx run examples/getting-started --junit results.xml
```

The XML file maps vouchfx verdicts to standard JUnit elements (`<failure>` for Fail, `<error>` for EnvironmentError, `<skipped>` for Inconclusive), so your CI system can ingest and visualise results natively.

### Streaming JSON Lines events

For live tailing and downstream processing (CI progress, real-time analysis, or integration with tools like the vouchfx MCP server):

```bash
vouchfx run examples/getting-started --events-stream events.jsonl
```

The `--events-stream` flag writes the same schema-versioned JSON Lines event records as the buffered `--events` archive, but incrementally as the run proceeds to a tailable file. The engine holds the write handle and grants shared read access; external tools can tail it in real time (UTF-8 without BOM) — useful for:

- **Live CI progress tracking** — display test results to the user as they complete, rather than waiting for the full suite to finish.
- **Downstream consumers** — tools like the vouchfx MCP server or a custom dashboard can read from the stream as events arrive.
- **Structured logging** — integrate vouchfx runs into centralised log aggregation systems.

**Important caveats:**

- **Per-step real-time liveness.** Step and step-attempt events are emitted as each step/attempt completes during the run, not batched at scenario end. For a RETRY step, each polling attempt is observable live as it happens, so you can watch the polling timeline unfold in real time without waiting for the scenario to finish.
- **Parallel mode writes in arrival order.** When you run with `--parallel <n>`, step lines from concurrently-running scenarios interleave by their completion order in the stream, not by declaration order. Each event still carries `runId` and `stepId`, which consumers use to disambiguate steps across an aggregated multi-scenario stream. The `--events` archive (if written) still respects declaration order.
- **Tailing requires shared access.** The engine holds the write handle and grants shared read access; a concurrent reader must open the file with shared read/write access (on Windows, `FileShare.ReadWrite`; on Unix, the file is readable immediately).
- **`--events` and `--events-stream` are independent.** You can use both together; they write to separate files and have no interaction. The `--events` archive remains a buffered, declaration-ordered snapshot written once at the end of the run.
- **Best-effort file path.** If the path is unwritable or incorrect, the engine prints a short diagnostic and continues — a bad stream path does not change the run's verdict or exit code.
- **Cancellation and fault handling.** On a cancelled or faulted run, the live stream may contain partial per-step lines (up to and including the step that was executing) that the authoritative `--events` archive does not — the stream shows progress, the archive is the source of truth.

### All three reports together

```bash
vouchfx run examples/getting-started --html report.html --junit results.xml --events-stream events.jsonl
```

## Advanced: Graceful shutdown for programmatic usage

When vouchfx is launched by a parent process that manages its lifecycle (such as the vouchfx MCP server), use the `--shutdown-on-stdin-eof` flag to enable graceful shutdown:

```bash
vouchfx run ./tests --shutdown-on-stdin-eof
```

When enabled, the engine monitors its standard input stream and gracefully initiates shutdown (equivalent to Ctrl+C) when the stream closes. This allows the engine's own container and topology teardown to run to completion before the process exits, preventing orphaned containers. This flag is **opt-in and off by default** — normal interactive and CI runs are completely unaffected. If graceful shutdown does not complete within the teardown budget (approximately 30 seconds), the engine force-exits itself, making the flag self-safe — a host that closes stdin is guaranteed termination without needing to send a kill signal. A forced exit always yields exit code 4 (Inconclusive), independent of the `--fail-on-inconclusive` flag.

**Caveat:** do not combine `--shutdown-on-stdin-eof` with runs whose stdin is already closed or redirected-from-nothing (e.g., `< /dev/null`), which would cause the run to cancel immediately.

The engine has also improved its handling of Ctrl+C interruption: when you press Ctrl+C during a run, the engine now allocates approximately 30 seconds for clean container and topology teardown, ensuring orphaned containers and Aspire session networks are no longer left behind. For an immediate hard abort, send SIGKILL or close the terminal.

## Validating without running

The `vouchfx validate` command compiles and validates `.e2e.yaml` files to check whether they are acceptable to the engine, **without** starting any containers, orchestrating a topology, or running steps. It performs the full validation pipeline — JSON Schema validation, parsing, AST construction, provider binding, and Roslyn compilation — but stops before execution. This is ideal for the author loop: tight feedback on syntax and step correctness before paying the Docker startup cost.

```bash
# Validate a single file or all files in a directory (recursive)
vouchfx validate examples/getting-started

# Validate with machine-readable JSON output (for tooling)
vouchfx validate examples/getting-started --json
```

Exit codes for `vouchfx validate`:

| Exit code | Meaning |
|---|---|
| **0** | All scenarios are valid. |
| **2** | Usage error — an unrecognised option or flag, the path is missing, or the path is not a readable .e2e.yaml file or directory. |
| **4** | One or more scenarios are invalid (schema, parse, pipeline, or Roslyn errors). |

The `--json` output carries the schema version and a per-scenario diagnostics list (stage: schema, parse, pipeline, or roslyn), suitable for editor plugins, CI gates, or downstream analysis.

`validate` models an unfiltered pre-flight: each scenario's relative `file:` references resolve against that scenario's own directory in both `validate` and `run`, so a passing `validate` predicts path resolution success. Note: in an unfiltered sequential `run`, the shared topology's seed is applied from the first scenario's directory; in `--parallel` runs, each scenario's topology applies its seed from its own directory.

> **Security note:** `validate` compiles your test in-process using the same Roslyn compiler as `run`, with no sandboxing. This is safe for suites you author and trust, but not for actively hostile input — a malicious `script.csharp` body can exhaust resources or crash the validating process. For use cases involving untrusted input (such as the vouchfx MCP server), isolate validation in a separate worker process.

## Listing step types

The `vouchfx list` command displays the engine's sealed Core step-type catalogue — all twenty-five `<family>.<provider>` step types compiled into this build. It reflects what the engine ships in the default CLI process. When additional providers are registered in-process (custom host or library API), the same catalogue export includes those types as well — there is no silent Core-only filter on the export path.

```bash
# List all step types (default)
vouchfx list

# Get machine-readable JSON (for tooling)
vouchfx list --json
```

Exit codes for `vouchfx list`:

| Exit code | Meaning |
|---|---|
| **0** | Success. |
| **2** | Usage error — an unrecognised option or flag. |
| **3** | Catalogue export failed because a registered step type lacks required metadata (fail-closed; no partial document). |

The `--json` output is a versioned catalogue document carrying the engine version and a sorted array of step types. Each entry includes:

- dotted `type` (`family.provider`), plus separate `family` and `provider` fields
- `requiredFields` and `optionalFields` (type-specific field names from the provider schema fragment)
- `captureSupported` (boolean; capture is a common language field and is supported on every fragment-backed type)
- `familyIntent` (a short one-liner describing the family's purpose)

This shape is the contract for integration with tooling such as [vouchfx-mcp](https://vouchfx-mcp.vouchfx.io/) and the VS Code extension. Incomplete metadata fails the entire export rather than omitting fields silently.

## Exporting the composed JSON Schema

The `vouchfx schema` command emits the **composed** v1 JSON Schema that the engine uses for validation: the root language grammar merged with every registered provider fragment. Default output is stdout (pipe-friendly); use `--output` to write a file.

```bash
# Print the composed schema to stdout
vouchfx schema

# Write to a file (parent directory must already exist)
vouchfx schema --output ./composed-schema.json

# Pipe to a file
vouchfx schema > composed-schema.json
```

Exit codes for `vouchfx schema`:

| Exit code | Meaning |
|---|---|
| **0** | Success. |
| **2** | Usage error — missing parent directory for `--output`, or an unrecognised option. |
| **3** | Composition or incomplete-metadata failure (a registered provider cannot supply a schema fragment). |

The exported document is draft 2020-12 JSON Schema and includes Core types such as `http.rest` and `db-assert.postgres`. Export never resolves or embeds secrets — only structural schema.

### Library API (in-process hosts)

In-process hosts (MCP servers, custom runners) should call the public API in the **`Vouchfx.Engine.Compilation`** package rather than shelling out:

- `Vouchfx.Engine.Compilation.Schema.EngineExport.ComposeSchemaJson(StepKindRegistry)` — composed schema JSON
- `Vouchfx.Engine.Compilation.Schema.EngineExport.BuildCatalogue(StepKindRegistry, engineVersion?)` — catalogue document model
- `EngineExport.SerializeCatalogue(...)` — same wire shape as `vouchfx list --json`

Pass the same frozen `StepKindRegistry` the process uses for validation/run so the export always matches live registration. Incomplete metadata throws `CatalogueExportException` naming the offending step type.

## Coverage analysis (the Planner)

As your test suite grows, the **Planner** is a read-only analysis tool that answers the question: **what should I test next?** It intersects three sources — your declared suite set (the `.e2e.yaml` files on disk), your run history (the JSON Lines event archive from `--events-stream` or `--events`), and the available step catalogue — and emits a structured gap report.

```bash
# Analyse your declared suites against run history
vouchfx plan ./tests/e2e --events ./run-history.jsonl

# Exit with code 5 if gaps are found (for CI gating)
vouchfx plan ./tests/e2e --events ./run-history.jsonl --fail-on-gap

# Machine-readable JSON report (for tooling)
vouchfx plan ./tests/e2e --events ./run-history.jsonl --json
```

The Planner identifies:

- **Coverage gaps:** suite never run, step never exercised, dependency not asserted, dependency missing an asserting step type, service missing an HTTP call
- **History-health signals:** step stale (last run >30 days ago), flaky (both passes and failures), fragile (infrastructure errors), inconclusive (timeouts or unmet conditions)
- **Identity issues:** suite names colliding or referencing renamed files

Exit codes for `vouchfx plan`:

| Exit code | Meaning |
|---|---|
| **0** | Successful analysis (regardless of gaps found). |
| **2** | Usage error — bad suite path, empty directory, out-of-range threshold, or missing parent directory for `--output`. |
| **3** | Incomplete catalogue metadata (a provider lacks schema). |
| **5** | Gaps found (only with `--fail-on-gap`). |

Every gap carries structured hints — step types to test, dependency names, suggested step ids — that the **Generator** (`vouchfx scaffold`) consumes directly to draft a skeleton step. This closes the loop: **plan → pick a gap → scaffold → validate → run**.

Thresholds are configurable (`--stale-days N`, `--flaky-min-runs N`, `--fragile-min-env-errors N`, `--inconclusive-min N`) with sensible defaults (30 days, 2 runs, 2 errors, 2 outcomes respectively).

See the [Planner documentation](planner.md) for the full reference.

## Generator / suite scaffold

Authoring is often the adoption bottleneck. vouchfx provides a **Generator** path that turns a **structured intent** (chosen step types, environment outline, step ids) into a **schema-valid `.e2e.yaml` skeleton** grounded in the **live step catalogue** — then you (or an MCP host LLM) fill in real semantics, **validate**, and **run**.

Important boundaries:

- **Free text lives in the host LLM** (for example via [vouchfx-mcp](https://vouchfx-mcp.vouchfx.io/)), not in the engine. The scaffold tool never accepts a natural-language goal string.
- **Scaffold args are structured only**: ordered step types (`family.provider`), optional services/dependencies, and per-step ids (plus optional short labels as comments).
- **No LLM inside the engine** — scaffold, validate, and run are deterministic for a given input and registration.
- Humans are **not** expected to maintain a parallel JSON-intent product as primary UX; YAML remains the suite language. The JSON intent is a thin tool argument for automation and MCP parity.

### CLI: `vouchfx scaffold`

```bash
# Intent file → stdout
vouchfx scaffold --intent ./intent.json

# Write a suite file (parent directory must already exist)
vouchfx scaffold --intent ./intent.json --output ./suites/draft.e2e.yaml

# Pipe intent on stdin
cat intent.json | vouchfx scaffold --intent -
```

Example intent JSON:

```json
{
  "steps": [
    { "id": "get-api", "type": "http.rest", "label": "GET api" },
    { "id": "check-db", "type": "db-assert.postgres" }
  ],
  "services": [ { "name": "api", "image": "traefik/whoami" } ],
  "dependencies": [ { "name": "db", "type": "postgres" } ]
}
```

The emitted YAML:

- Begins with a **provenance comment** marking the suite as machine-drafted by `vouchfx scaffold`, and that a human must **review before trust** (no timestamps — output is stable for identical input).
- Reflects the provided services/dependencies and step ids in input order.
- Fills **required** provider fields with deterministic placeholders (paths, `SELECT 1`, minimal `expect` blocks, and so on) so the document is **schema-valid** without an LLM fill.
- Never plants secret literals; credential-shaped fields use `${secret:env/SCAFFOLD_PLACEHOLDER}` references only.

Typical host workflow (free text stays outside the tool surface):

1. User describes a goal in the MCP host / chat.
2. The host LLM chooses step types and ids from `vouchfx list --json` (catalogue).
3. Host calls **scaffold** with structured args → YAML skeleton.
4. Host LLM (or human) fills real paths, queries, and expectations.
5. **validate** (`vouchfx validate`) then **run** (`vouchfx run`).

Exit codes for `vouchfx scaffold`:

| Exit code | Meaning |
|---|---|
| **0** | Success — YAML written to stdout or `--output`. |
| **2** | Usage error — missing/unreadable intent, malformed JSON, missing parent directory for `--output`, or an unrecognised option. |
| **3** | Scaffold validation failure — unknown step type, unknown dependency kind, duplicate step ids, empty steps list, or incomplete catalogue metadata. |

### Library API

In-process hosts should call `Vouchfx.Engine.Compilation.Scaffold.SuiteScaffolder.Generate(StepKindRegistry, ScaffoldIntent, engineVersion?)` rather than shelling out when already in-process. Unknown types and dependency kinds throw `ScaffoldException` with clear diagnostics. The same Core registry used for `validate` / `run` keeps CLI and library from drifting.

Scaffold alone does **not** guarantee a green multi-tech `run` without further fill — it guarantees a legal skeleton ready for validation and authoring.

## Next steps

Now that you have a passing test, here's where to go:

### Recipes & cookbook

The `docs/recipes.md` file collects common testing patterns: capturing and reusing values across steps, waiting for eventual consistency with `verifyMode: RETRY`, seeding databases, calling webhooks, and publishing/consuming Kafka events. Also see `docs/common-patterns.md` for authoring patterns and `docs/troubleshooting.md` for failure modes and fixes.

### Full DSL specification

For step types beyond `http.rest` (database assertions, scripts, message queues, webhooks), see [`docs/02_YAML_DSL_Specification_and_VSCode_Extension_Design.md`](02_YAML_DSL_Specification_and_VSCode_Extension_Design.md). It covers:

- Every step family across twenty-five Core providers — eleven families: HTTP calls (`http`: REST, SOAP), database assertions (`db-assert`: PostgreSQL, MySQL, SQL Server, MongoDB, DynamoDB), message publishing (`mq-publish`) and message expectations (`mq-expect`) — both over Kafka, RabbitMQ, NATS, Azure Service Bus, and Redis Streams — cache assertions (`cache-assert`: Redis, Elasticsearch), email (`mail-expect`: SMTP), metrics (`metrics-assert`: Prometheus), storage (`storage-assert`: S3), distributed tracing (`trace-expect`: OTLP), webhooks (`webhook-listen`), and scripts (`script`: C#).
- Capture and placeholder syntax: threading state forward.
- Verifymode: `IMMEDIATE` (assert now) vs. `RETRY` (engine-owned polling with backoff).
- Secrets: reference-only syntax (`${secret:env/NAME}` / `${secret:vault/path}`), resolved at execution time.
- Seed: SQL fixtures and warm-up logic applied before the first step.

### Real-world sample applications

For worked end-to-end examples with real C#, Python, and Java services, see the [vouchfx-samples](https://github.com/tomas-rampas/vouchfx-samples) repository. It contains sample applications and complete test suites demonstrating common patterns.

### Architecture Blueprint

For deep understanding of how the platform is built, memory safety, orchestration, and the provider model, read [`docs/01_Technical_Architecture_and_Engineering_Blueprint.md`](01_Technical_Architecture_and_Engineering_Blueprint.md).

### CI integration

To run vouchfx tests in GitHub Actions or GitLab CI/CD, see the [CI integration reference](ci-integration.md). The reusable workflow and template handle building, running, and publishing reports.

### Writing a custom provider

Once you're comfortable with the built-in steps, you can write your own. See the [provider hub's implementation guide](https://providers.vouchfx.io/docs/implementing-a-provider.html) for the complete journey from contract to conformance. To use someone else's community provider, see the [consuming guide](https://providers.vouchfx.io/docs/consuming-a-provider.html). For platform-engine details and the frozen v1 contract, see [CONTRIBUTING.md](https://github.com/tomas-rampas/vouchfx/blob/main/CONTRIBUTING.md) and the [`examples/Example.Steps.Echo`](https://github.com/tomas-rampas/vouchfx/tree/main/examples/Example.Steps.Echo) worked example — walk through its README, including the contributor friction log it contains, to understand the author's journey.

## Summary

You now have:

1. ✓ Verified the .NET 8 SDK and Docker daemon are installed.
2. ✓ Installed the `vouchfx` CLI (or built it from source).
3. ✓ Authored a minimal `.e2e.yaml` test file.
4. ✓ Run the test and seen a **PASS** verdict.
5. ✓ Generated HTML and JUnit reports.
6. ✓ Learned the four verdicts and their CI implications.
7. ✓ Discovered where to find recipes, the full DSL, and architecture docs.

**The next hour?** Pick a real service from your own codebase, author a test against it, and explore the other step types (database assertions, scripts, message queues). Start with `http.rest` and capture as here; as you grow the test, the DSL spec will guide you through the rest.

Welcome to distributed system testing that you can read and reason about. Happy testing.
