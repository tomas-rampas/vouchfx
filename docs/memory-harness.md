# The memory-leak harness

vouchfx's central engineering claim is its memory model: every `.e2e.yaml` suite is compiled **once** into a collectible `AssemblyLoadContext`, invoked N times, and unloaded back to baseline. If that claim were ever violated — by the engine itself, or by a static cache deep inside a provider's client library — a long-running or watch-mode session would grow without bound. The memory-leak harness is the permanent, blocking CI gate that proves the claim holds, on every push and on a weekly schedule.

This page documents the harness as a tool: what it measures, how to run it yourself, and what a passing report looks like.

## Why it exists

The collectible load context guarantees that dynamically generated assemblies are reclaimed on unload. It does **not** guarantee that references held by libraries *outside* the context are reclaimed — several mainstream .NET clients register process-wide singletons or static caches that can pin objects across the boundary (`HttpClient`'s handler pool, `Npgsql`'s connection pool, `Confluent.Kafka`'s native handles, and so on). Section 5 of the [Architecture Blueprint](01_Technical_Architecture_and_Engineering_Blueprint.md) treats this as a first-class failure mode, and rule three of §5.4 requires a regression test over the **full transitive closure of every Core provider** — not just trivial scripts — so a dependency-introduced leak is caught the week it appears, not at a pilot.

## What it measures

The harness lives at `tests/Vouchfx.Engine.Compilation.MemoryHarness` and runs the real engine pipeline — `RoslynScriptCompiler.CompileOnce` into a fresh collectible `AssemblyLoadContext`, execute, unload — in a loop, with a script body (the *closure probe*) that deliberately touches every Core provider's canonical client:

- **Relational:** `Npgsql`, `Microsoft.Data.SqlClient`, `MySqlConnector` — real connection objects built and disposed per cycle (pools cleared or bounded).
- **Document / key-value:** `MongoDB.Driver` (`MongoClient` with cluster-registry release), `StackExchange.Redis` (a real multiplexer per cycle), `AWSSDK.DynamoDBv2` (`AmazonDynamoDBClient`).
- **Brokers:** `Confluent.Kafka` (native producer/consumer handles) plus `Confluent.SchemaRegistry`/Avro serdes, `RabbitMQ.Client`, `NATS.Net`, `Azure.Messaging.ServiceBus`.
- **HTTP-based providers:** a per-cycle `HttpClient` (covers `http.rest`, `http.soap`, `mail-expect.smtp`, `cache-assert.elasticsearch`, `metrics-assert.prometheus`).
- **Object storage:** `AWSSDK.S3` (`AmazonS3Client`).
- **Host-resource accessors:** the `Webhooks` and `Traces` read paths across the collectible boundary.
- **Engine plumbing:** Polly `ResiliencePipeline` construction (stateless by design).

Nothing connects to a live service — clients target unreachable loopback endpoints and are built + disposed inside the script's `finally` blocks, exactly as provider-emitted code must. Heavier clients cycle every 50th iteration; the loop's aggregate still exercises each one hundreds of times.

Two guard tests keep the probe honest as the provider set grows: a coverage guard asserts that every registered Core provider has a probe entry with a marker string that appears verbatim in the probe script, and the count assertions fail the build if a provider is added without extending the closure.

After the loop the harness forces a full GC, compares the managed heap against the pre-loop baseline, and verifies that the number of live collectible load contexts returned to its starting value. A preamble additionally churns 64 contexts under bounded concurrency (the parallel-scenario shape) and requires all 64 to be reclaimed.

## Running it yourself

```bash
dotnet build vouchfx.sln -c Release
dotnet run -c Release --no-build \
  --project tests/Vouchfx.Engine.Compilation.MemoryHarness \
  -- --mode closure --iterations 5000 --threshold-bytes 2000000
```

| Option | Meaning |
|---|---|
| `--mode closure` | The full Core-provider client closure (the CI gate's mode). |
| `--iterations 5000` | Calibrated run length — long enough that a per-iteration leak of even a few hundred bytes trips the threshold. |
| `--threshold-bytes 2000000` | The permanent 2 MB pass ceiling for net heap growth. |

The process exits **0** only when the delta is inside the threshold **and** every collectible context was reclaimed; any other outcome is a non-zero exit, which is what makes it usable as a blocking gate.

## A passing report, with real figures

Verbatim output from a run over the complete twenty-five-provider closure (5,000 iterations, engine `main`, 2026-07-08):

```
[MemoryHarness] Starting: mode=closure, iterations=5,000, threshold=2,000,000 bytes.
[MemoryHarness] Concurrent-ALC-churn: scenarios=64, maxConcurrency=4,
                alcsCreated=64, alcsReclaimed=64, allReclaimed=True.
[MemoryHarness] Mode=closure, NetDelta=+14912 bytes (14.6 KB),
                Threshold=2,000,000 bytes, Passed=True,
                CollectibleBefore=1, CollectibleAfter=1, ContextReclaimed=True.
```

Reading it:

| Figure | Value | Interpretation |
|---|---|---|
| Baseline heap | 8,378,416 bytes | Managed heap before the loop. |
| Post-run heap | 8,393,328 bytes | After 5,000 compile/run/unload cycles and a final GC. |
| **Net delta** | **+14,912 bytes (14.6 KB)** | ≈3 bytes per iteration — about 0.75 % of the threshold (~134× headroom). Run-to-run noise straddles zero; healthy runs routinely come in *negative* (the heap ends smaller than baseline). |
| Collectible contexts | 1 before / 1 after | Nothing pinned a load context — the unload genuinely reclaims. |
| Concurrent churn | 64 created / 64 reclaimed | The parallel-scenario ALC shape leaks nothing either. |

The machine-readable JSON line the harness also emits (`netDeltaBytes`, `passed`, `contextReclaimed`, plus a `singletonsReset` inventory documenting the reset/disposal strategy for every client in the closure) is what the CI job archives.

## Where it runs in CI

The gate is the `memory-leak` job in `.github/workflows/build.yml`: **blocking** on every push and pull request, and scheduled weekly (Mondays 06:00 UTC) so a leak introduced by a dependency upgrade between pushes is still caught within days. It is a permanent CI gate from the project's first milestone because a leak discovered late costs far more than one caught the week a client library enters the closure.

## Adding a provider to the closure

A new Core provider whose emitted code uses a client library must extend the closure in two places: a coverage row naming the provider's step kind, canonical client, and probe marker; and — when the client is genuinely new to the process — a build-and-dispose block in the probe script whose text contains that marker verbatim. Clients already exercised by an existing block (for example, a provider whose runtime surface is plain `HttpClient`) declare the shared marker instead of duplicating a block. The coverage guard fails the build until both places agree with the registered provider set, which is what keeps this page's "full closure" claim true as the catalogue grows.
