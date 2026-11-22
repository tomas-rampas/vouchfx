# Owned Docker Compose environments

Use your own Compose project with existing vouchfx assertions. Select a registered environment driver in the suite; the adapter owns its separate configuration grammar. Omitting `source` retains inline provisioning. Core `1.0.0` introduced Compose support through the engine's binding/ready-lease boundary. That contract is format-neutral; other external formats require a registered driver implementation without changing step providers.

Install the latest stable CLI with a .NET SDK it supports and verify the native Docker Compose plugin:

```sh
dotnet tool install --global vouchfx
vouchfx --version
docker compose version
```

Already installed? Use `dotnet tool update --global vouchfx`. Execution requires Docker running Linux containers on a supported local daemon; validation alone does not require Docker. Legacy `1.0.0-rc.6` does not accept `environment.source`; upgrade before using this guide.

For contributor work on unreleased source, build and select that checkout's DLL explicitly:

```sh
dotnet build src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj -c Release -warnaserror
dotnet src/Cli/Vouchfx.Cli/bin/Release/net10.0/vouchfx.dll --version
```

```yaml
environment:
  source:
    driver: compose
    config: ./environment.compose.yaml
```

The configuration path is relative to the suite. It cannot be combined with inline services, dependencies, seed or image defaults.

## Build your backend from a Dockerfile

Declare the build in your native Compose manifest. For example, with `compose.yaml`
beside `environment.compose.yaml`, and an `app/` directory beside both:

```yaml
# compose.yaml
services:
  web:
    build:
      context: ./app
      dockerfile: Dockerfile.e2e
    ports:
      - target: 8080
        host_ip: 127.0.0.1
```

Here Compose builds `app/Dockerfile.e2e` with `app/` as its build context. The
`dockerfile` path is relative to that context; omit it to use the usual
`app/Dockerfile`. The application must listen on port 8080 and expose the health
path selected in the binding below. Vouchfx starts the owned project with native
`docker compose up --detach --build`, then checks the declared endpoints before
running assertions. The build remains subject to the startup deadline.

The suite selects the Compose configuration, rather than a Dockerfile directly.
There is no inline `dockerfile` or `build` field under `environment.services`.
For an inline environment, build the image first:

```sh
docker build -t my-backend:e2e -f ./app/Dockerfile.e2e ./app
```

```yaml
environment:
  services:
    api:
      image: my-backend:e2e
      httpPort: 8080
```

Run that suite against the same Docker daemon that holds the built image. Inline
provisioning starts the image; it does not build the Dockerfile.

## Bind logical targets

Name the HTTP, TCP and PostgreSQL targets your assertions use. Manifest/env-file paths are relative to this configuration; native Compose handles merging, interpolation and project/build/mount paths.

```yaml
version: 1
files: [./compose.yaml, ./compose.test.yaml]
projectDirectory: .
profiles: [test]
environment:
  TEST_PASSWORD: "${secret:env/TEST_PASSWORD}"
startupTimeoutSeconds: 120
cleanupTimeoutSeconds: 30
services:
  api:
    service: web
    port: 8080
    readiness: {path: /health, status: 200}
  broker:
    type: tcp
    service: kafka
    port: 9092
    readiness: {type: healthcheck}
dependencies:
  db:
    type: postgres
    service: postgres
    port: 5432
    database: app
    username: app
    password: "${secret:env/TEST_PASSWORD}"
```

`files` is an ordered, nonempty list. `projectDirectory` defaults to the first manifest's directory. Optional `envFiles` selects Compose interpolation files; service `env_file` inputs are tracked too. `environment` accepts literal values or whole environment-secret references. Its names start with an ASCII letter or underscore, followed by ASCII letters, digits, underscores or dots, preserving case; `VOUCHFX_PROBE.key_1` is valid. Malformed names are refused without printing them. `COMPOSE_` and `DOCKER_` names remain reserved for ownership and daemon selection. This configuration rule does not reinterpret native manifest `environment` fields. Passwords require `${secret:env/NAME}`. Secrets resolve only during acquisition through the run's scrub ledger. Unknown configuration keys are refused.

Publish exactly one IPv4 TCP mapping for each selected service/port, with one replica. Random host ports are supported; fixed host ports are retained and conflicts fail startup. IPv6-only and multiple mappings are refused. A published `0.0.0.0` address is reached through host loopback.

| Binding | Readiness | Value supplied to assertions |
| --- | --- | --- |
| HTTP, the default; optional `type: http` | `readiness: {path: /health, status: 200}`; optional readiness `type: http` | `svc::<name>` holds `http://host:port` |
| `type: tcp` service | `readiness: {type: tcp}` proves host-port reachability only | `svc::<name>` holds bare `host:port` |
| `type: tcp` service | `readiness: {type: healthcheck}` requires its native healthcheck to be healthy and its host port reachable | `svc::<name>` holds bare `host:port` |
| `type: postgres` dependency | Opens the declared database and executes `SELECT 1` | `conn::<name>` holds its connection string |

TCP readiness must name its mode explicitly. Use `healthcheck` for brokers that need a protocol-level readiness command: configure an enabled native Compose `healthcheck.test` for that service. The adapter refuses missing, disabled or `NONE` checks before starting resources; an image's implicit healthcheck cannot satisfy this authoring requirement. At runtime it accepts healthy state only from the exact owned project's service container. Healthcheck command output is suppressed and is never stored in the ownership journal. A reachable TCP port alone does not prove broker readiness.

Existing Kafka publish/expect steps can target the logical `broker` service. Supply a host-reachable Kafka advertised listener matching the selected published port; the adapter does not rewrite Kafka advertisements. Application-to-broker traffic can use a separate listener on Compose DNS. Use an isolated fixed listener port when the broker's advertisement requires it; conflicts fail startup. Configuration, binding and readiness remain adapter concerns, so another registered format can supply the same normalised service value.

Kafka retained-record assertions manually assign every actual topic partition at its earliest offset; they do not wait for a fresh consumer group to rebalance. Metadata discovery and consumption share the existing one-second attempt budget. An empty partition does not end the drain while another partition remains unread. Use `verifyMode: RETRY` for messages that have not arrived yet; a step timeout does not widen an individual drain. Native broker health and HTTP success still require the separate Kafka business assertion.

## Validate, run and watch

```sh
vouchfx validate ./orders.e2e.yaml
vouchfx run ./orders.e2e.yaml --fail-on-env-error --fail-on-inconclusive --events ./events.jsonl
vouchfx run ./orders.e2e.yaml --watch
vouchfx schema --environment-driver compose --output ./compose-schema.json
```

Validation is pure: it reads authoring files and compiles normalised targets without running subprocesses, resolving credentials or creating containers. Runtime uses the same declarations. Config, manifest and env-file edits invalidate the generic watch plan and trigger another run. Every Compose scenario/watch execution acquires a fresh project; logical dependency names remain available for `conn::` staging even though reset/reseed is prohibited.

Use both failure flags in CI and require the expected passing step counts. Environment errors and runtime inconclusive results otherwise retain the existing opt-in exit policy; exit zero alone does not prove the suite passed.

The VS Code extension associates `*.compose.yaml` with the separate configuration schema. The ordinary suite schema remains for `*.e2e.yaml`. `vouchfx plan` explicitly reports external-source suites as unanalysable; it does not claim an empty inline topology was analysed. `scaffold` supports inline outlines and refuses source intents; author the source/config explicitly. Downstream schema-only tools cannot confirm external bindings without running core validation.

## Ownership and recovery

Each acquisition generates a random project identity on the selected local Docker daemon. Native Compose renders and validates the merged model before mutation. A native stdin reload must preserve its JSON configuration, including literal dollars in credentials; incompatible Compose versions are refused before startup. Startup/discovery consume that exact snapshot in memory. Rendered credentials and env-file contents are never journalled or included in subprocess diagnostics.

Cleanup uses project-labelled Docker resource inventory, rechecks each resource's ownership and removes only its exact ID/name. It does not reread edited manifests or run global prune. Cleanup has an independent deadline and is idempotent. A cleanup failure is reported while retaining the primary scenario result and its recovery journal. Built/pulled images remain cached.

Hard termination cannot run `finally`. Recovery records live under `%LOCALAPPDATA%\vouchfx\environments` on Windows, or the platform's local application-data directory. Recover one interrupted project:

```sh
vouchfx environment cleanup --journal /path/to/vouchfx/environments/vouchfx-PROJECT_ID.json
```

Recovery requires access to the recorded local daemon, not original manifests or credentials. Successful cleanup removes the journal; failed or timed-out cleanup retains it.

## Current limits

This driver supports owned HTTP/TCP/PostgreSQL environments with IPv4 published endpoints on local Unix-socket or Windows named-pipe Docker daemons. Explicit `DOCKER_HOST` values must use a literal absolute Unix socket path, such as `unix:///var/run/docker.sock`, or `npipe:////./pipe/<name>` with one nonempty pipe name. Unix socket URIs cannot contain an authority, query, fragment, encoding, dot segments or empty path segments. Use isolated test overrides. External storage/networks, explicit shared resource names, fixed container names, delegated Compose provider plugins, custom resource drivers/options, reserved Compose labels, writable host mounts, host namespaces/devices/capabilities, explicit `uts`/`userns_mode`/`cgroup` settings and `use_api_socket` are refused before startup. Active `include`/`extends`, seed, borrowed resources, reuse and external-driver TLS/mTLS declarations are unsupported. Existing inline security validation remains available. These ownership checks do not sandbox user container images; review read-only host mounts and application behaviour yourself.

In this matching source checkout, follow `examples/compose-orders/README.md` for the HTTP mutation and database example, including its identical inline assertion steps.

The [shared-app .NET Compose orders sample](https://github.com/tomas-rampas/vouchfx-samples/tree/main/samples/orders-dotnet-compose) builds its application from the shared Dockerfile and demonstrates all five business assertions: REST creation, the PostgreSQL row, the retained Kafka event, the outbound webhook and the final REST refetch. A Windows run with Linux Docker containers on 25 October 2026 passed all five assertions and exited 0 using the official Core 1.2.1 GitHub Release package. Its README describes the pinned images, native readiness checks and standard sample runner. Compose support predates that release; 1.2.1 also corrects GitLab CI exit handling.

## Contributor acceptance coverage

The Linux Docker CI lane runs these ownership cases in the exclusive Compose acceptance collection. It retains first-attempt JSON evidence and the five cold CLI event/output files in the `owned-environment-acceptance` artefact, including partial records when a case fails. Its file-only admission gate requires all three cases and exact completed counts; a green exit without these records is insufficient.

| Capability | Native regression case |
| --- | --- |
| Requested profile starts its service; another profile stays inactive | `Compose_RequestedProfileAndOrderedInterpolationFilesAndServiceEnvFile_AffectRealContainer` |
| Later interpolation env-file wins; service `env_file` reaches its container independently | The same capability case inspects the actual hostname and container environment |
| Ordered manifest override/build, credentials and published HTTP/PostgreSQL targets | `NormalWatch_ConfigEditOutsideSuite_TwoFreshPassingRuns_CleansOwnedResources` |
| Fixed-port conflict and readiness deadline refuse a passing result | `TwoNormalScenarios_AreFresh_FixedPortConflictAndReadinessDeadlineCannotPass` |
| Assertion failure and cancellation release only owned resources | `NormalRun_AssertionFailure_AndCancellationDuringReadiness_CleanOnlyOwnedResources` |
| Repeated fresh state and topology release | `TwentyWarmAcquisitions_TenInlineAndTenCompose_ReuseOneCompiledImageAndReleaseFreshState` |
| Fresh CLI/compiler process and writable profile | `FiveColdCliProcesses_ThreeInlineAndTwoCompose_FreshWritableProfilesAndOwnedClosure` |

Warm means one test-host process and one emitted PostgreSQL assertion image across 20 fresh acquisitions, alternating ten inline and ten Compose environments. Each cycle proves an initially absent table, writes state, observes it through the compiled provider, and requires container/network/volume, journal and DCP release before continuing. Foreign resource IDs/creation times and native process birth identities must remain unchanged.

Cold means five distinct CLI process birth identities with separate writable app-data, temporary and .NET/NuGet profile directories on Linux, covering three inline and two Compose runs. Each CLI must produce two passing steps, one passing scenario and exit zero; Compose must actually journal within its isolated profile, and inline acquisition must observe its DCP host. These are bounded regression samples with existing image layers and build artefacts retained. They do not measure uncached pulls, certify every OS, or replace the separate TLS/mTLS acceptance lane. Compose reset/reseed remains unsupported; fresh state is established by recreation.

Run the focused cases on an isolated Linux Docker host:

```sh
dotnet test tests/Vouchfx.Cli.Tests -c Release --filter "requires=docker&FullyQualifiedName~EnvironmentTopologyDurabilityDockerTests"
```

The format-neutral driver boundary is unchanged. These cases exercise the built-in Compose adapter and inline provisioning; other formats still require their own registered driver.

### Reading a teardown refusal

When the gate refuses with `Case or fixture teardown did not complete`, the refusal appears where it always did. If an earlier case has missing evidence, another checkout's evidence or a first-attempt failure, that refusal is reported instead. Below the refusal line, the job log describes the case that failed, then any other case whose report can be read and whose teardown also did not complete. A report that cannot be read is left out, because it keeps its own refusal. If describing a case raises an error, its entry reads `diagnostics unavailable` with the error type. The refusal itself does not change.

For each case the log prints:

- which marker failed: `Completed` (the case did not finish) or `FixtureCleanupCompleted` (the fixture's own teardown did not finish), with both values, the cycle counters (`ExpectedCycles`, `CompletedCycles`, `AttemptedCycles`, `CurrentCycle`, `LastReleaseCompleted`) and the recorded `Failure` and `CleanupFailure` text, shortened to fit a log line;
- for containers, networks, volumes, DCP processes and journals, what the last closure check (`FixtureClosureLastInventory`) held that the last cycle's baseline (`LastBefore`) did not, and the reverse. When the last closure check comes from a `fixture-disposal` wait, the reverse lists name the fixture's own sentinel container, network and volume, which are in the cycle baseline and were removed on purpose. That wait's own `BaselineMissing` compares against the inventory taken before the fixture created anything;
- the last baseline wait (`LastClosureWait`) and each wait that did not converge (`FailedClosureWaits`), with the fields below and the last 20 of the Docker events the fixture recorded for the wait. Up to 10 earlier recorded events are printed before them if they contain the `FailedInspect` ID or an ID from a `*StillPresent` list.

Each wait record holds:

- `Purpose`: `cycle-release` (after a cycle, against that cycle's baseline) or `fixture-disposal` (after the fixture removed its sentinel, against the inventory taken before the fixture created anything).
- `Outcome`: `converged`; `bound-elapsed` (the 30-second bound ran out); `error` (something else failed inside the wait); `creation-identity-changed` (every list matched the baseline, but a resource had a different creation time); or `caller-cancelled` (the test's own deadline stopped a `cycle-release` wait).
- `StartedUtc`, `ElapsedMilliseconds` and `BoundMilliseconds`.
- `Polls`, the number of inventories that completed, and `LastInventoryUtc`, when the last one completed (`null` if none did). The `*StillPresent`, `BaselineMissing` and `BaselineContainersMissing` lists come from that last inventory, not from the moment the wait ended.
- `ContainersStillPresent`, `NetworksStillPresent`, `VolumesStillPresent`, `DcpProcessesStillPresent` and `JournalsStillPresent`: what the last inventory held that the baseline did not.
- `BaselineMissing`: what the baseline held that the last inventory did not, each entry prefixed `container:`, `network:`, `volume:`, `dcp:` or `journal:` and followed by the ID, the volume name, the process ID and start time, or the file path. `BaselineContainersMissing` repeats the container entries without the prefix.
- `FailedInspect`: the kind and ID of an inspect that failed while an inventory read creation times, otherwise `null`.
- `Error`: the exception type and message that ended a wait that did not converge.
- For a wait that did not converge, read just after it ended, all within one 10-second limit: `ContainerStates` (`name|id|state|status` for every container) and `NetworkNames` (`name|id`), or `StateReadError` if they could not be read; and `Events`, the newest 200 Docker events between `EventsSinceUtc` and `EventsUntilUtc`, or `EventsReadError` if they could not be read. The window opens 60 seconds before the wait started, so it covers up to a minute of the teardown that ran just before the wait, and closes when the events are read. The daemon replays only its most recent 256 events, so on a busy daemon the start of the window can be missing.

Evidence recorded before a field existed is still judged as before. The log says `not recorded` for a missing wait, leaves out missing fields, and calls a wait entry `malformed` if it is not an object. Text taken from the evidence is printed with the runner's workflow-command markers (`::` and `##[`) broken up, so a container name or an exception message cannot act as a command.

To classify an occurrence, work through these steps in order and stop at the first that fits.

1. **The process stopped before the evidence was finished.** Shown by: both markers `False`, with `Failure` and `CleanupFailure` both `null`. The case records any exception it raises in `Failure`, and the fixture's teardown records its exception in `CleanupFailure` (the cold case also records a failed cycle release there), so neither being set means the process ended (killed, crashed or stopped by the job timeout) after the case last saved its evidence part-way through. Look for a dump and a `Sequence*.xml` file in `integration-crash-dumps`. Does not prove: anything about teardown, because the wait records describe the last save, not the end. The single-cycle `compose-capabilities` case saves only at the end, so if its process stops the gate refuses for missing evidence instead.
2. **Find the wait that matters.** `FailedClosureWaits` lists every wait that did not converge, in order. `LastClosureWait` can predate the failure: the fixture's teardown can fail on a `container rm`, `network rm` or `volume rm` before its own wait starts, and then after a non-zero exit `CleanupFailure` names that operation; a `docker rm` that timed out gives `TaskCanceledException`, which names the call only in its stack trace. Check each wait's `Purpose` against the marker that failed. If no wait failed, the readings below do not apply; read `Failure` and `CleanupFailure`. A wait with `Outcome=caller-cancelled` matches none of the readings below unless `BaselineMissing` is also set (step 5): the case's own deadline ran out, so read `Failure`.
3. **Docker was not answering, or an inventory was too slow.** Applies to `bound-elapsed` waits only. Shown by: `Polls=0`, meaning no inventory finished within 30 seconds, so every list is empty because nothing was read; or a stale inventory, meaning `LastInventoryUtc` is earlier than `StartedUtc` plus `ElapsedMilliseconds` by several average poll durations (about `ElapsedMilliseconds` divided by `Polls`), so inventories stopped finishing part-way through. Does not prove: whether the case's resources were removed or left behind. If `StateReadError` or `EventsReadError` is set to a cancellation such as `TaskCanceledException`, the reads after the wait are missing, because they did not finish in 10 seconds; note that and continue with the next step.
4. **A Docker call failed during the wait.** Shown by: `Outcome=error`. Read `Error` first. A fixture Docker command that exits non-zero fails with `Owned topology fixture Docker operation failed (exit N) in <operation>; output withheld.`, which names the operation but not its output. The `*StillPresent` lists are not a finding here, because the wait ended on an error, not at the bound. One suspected cause, not established, is a container, network or volume removed between an inventory listing it and inspecting it for its creation time. `FailedInspect` names the kind and ID of that inspect. A `destroy` event for the same ID during the wait, in `Events` (the log prints the last 20 and up to 10 earlier events that contain that ID) or in `docker-state/docker-events-recent.txt`, would confirm it. Without one the cause stays open: the daemon can fail an inspect for other reasons, and the event can fall out of its 256-event buffer.
5. **The baseline changed underneath the wait.** Shown by: `Polls` above 0, every `*StillPresent` list empty and `BaselineMissing` not empty. The wait converges only when the baseline is present unchanged, so a baseline item that disappears keeps it from converging however quickly the case's teardown finished. Examples are a DCP process left by an earlier test that exits on its own (`dcp:`), or a foreign container that something else removed. `creation-identity-changed` is the same reading for a resource listed under the same ID or name with a different creation time. With `Polls` above 0, a `bound-elapsed` wait always has a non-empty `*StillPresent` list or a non-empty `BaselineMissing`, because its last inventory differed from the baseline. If a `*StillPresent` list is also non-empty, the wait could not have converged for either reason: judge the extra items as in steps 6 and 7. Does not prove: what removed the item.
6. **Removed late.** Shown by: a `cycle-release` wait with `Outcome=bound-elapsed`, `Polls` above 0 and `BaselineMissing` empty, whose `*StillPresent` lists name resources that were not in the baseline, normally the case's own; and a `fixture-disposal` wait after it that converged (`FixtureCleanupCompleted=True`, and `LastClosureWait` with `Purpose=fixture-disposal` and `Outcome=converged`). The disposal wait converges only when everything matches the inventory taken before the fixture created anything, so the resources were gone by the end of that wait. This proves they were removed late, not left behind. `ContainerStates` shows how far removal had got when the cycle wait ended: `removing` means removal was under way, `exited` means the container was stopped but not yet removed, `running` means it had not yet been stopped, and `dead` means Docker attempted the removal and it failed, which points to step 7. A container missing from `ContainerStates` had already gone when it was read. Does not prove: whether the answer is a longer bound or a product change. That is a separate decision, made from the recorded timings (`StartedUtc`, `ElapsedMilliseconds` and the event timestamps) and the DCP captures in `integration-dcp-captures`.
7. **Left behind.** Shown by: the same cycle wait, but the `fixture-disposal` wait also failed with the same items in its `*StillPresent` lists, and the `docker-state` snapshot taken minutes later still lists them (it covers containers, networks and volumes, not DCP processes or journals). A container or an `aspire-session-network-*` network that is still present then breaks the teardown invariant and is a product defect to investigate. Its state in `ContainerStates` and in `docker-state/container-state.txt` says where teardown stopped: `running` means the container was never stopped, `exited` means it was stopped but not removed, `removing` means removal was still under way, and `dead` means Docker attempted the removal and it failed, the clearest sign of a container left behind. When the wait that ran out is the `fixture-disposal` one, no later wait follows, and the `docker-state` snapshot alone separates late from left behind. Does not prove: which part of teardown failed. Read the events and the DCP captures for that.

The artefact and snapshot details in this section apply to the `Integration tests — Docker / Aspire` job in `.github/workflows/build.yml`. The compatibility workflow runs the same gate without the snapshot and keeps its artefact for 7 days.

When a step of the job has failed, the `owned-environment-acceptance` artefact also holds a `docker-state` directory. A step that runs after the verifier captures it, minutes after the failing case and after the job's other test projects. It shows what Docker holds at that point, so it confirms whether something persists but cannot show what was there when the case failed; the fixture's own records in the case JSON do that. The step does not run when the job is cancelled. The directory contains:

- `captured-at-utc.txt`, the time of the capture;
- `docker-ps-a.txt`, with the ID, image, creation time, status, ports and names of every container, and without the command column, because a container's command line can carry a credential;
- `docker-network-ls.txt`, `docker-volume-ls.txt`, `network-state.txt` with each network's members, and `container-state.txt` with each container's name, ID, status, exit code, the daemon's error text, start and finish times, out-of-memory flag and health status (not the health-check output);
- `docker-events-recent.txt`, the daemon's events from the last hour, at most its most recent 256, one line per event, with the action in JSON quotes and the command of `docker exec` events removed;
- `docker-events-errors.txt`, the error output of that events command, and its exit status if it was not 0.

It deliberately omits container environments, which can hold the fixture's database password.

The four artefacts of this job, `docker-integration-test-results`, `owned-environment-acceptance`, `integration-dcp-captures` and `integration-crash-dumps`, are kept for 30 days if the job has already failed or been cancelled when their upload step runs, and for 7 days otherwise, because this failure is rare and can take longer than a week to notice. Each upload step reads the job status as it runs. `docker-integration-test-results` is uploaded before the verifier, so it keeps 7 days when only the verifier or a later step fails.
