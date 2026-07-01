// Platform.Engine.Compilation.MemoryHarness — SingletonReset (S02-B-01, §5).
//
// Per-cycle reset of known singletons that could pin objects across the collectible
// AssemblyLoadContext boundary.
//
// Rationale (§5, CLAUDE.md "Memory model"):
//   Static state in library singletons (Npgsql connection pools, HttpClient handler
//   pools, OpenTelemetry tracers) can hold references into the collectible context that
//   prevent GC reclamation of unloaded ALCs.  Resetting them every N iterations keeps
//   the per-cycle heap delta near zero so the measurement reflects ALC lifecycle
//   correctness rather than client-pool accumulation.
//
//   Each pinner entry is documented — "cannot reset" cases record the constant
//   (non-growing) overhead so future maintainers understand why the item is present.

using System.Collections.Generic;

namespace Platform.Engine.Compilation.MemoryHarness;

/// <summary>
/// Resets known singletons that could pin objects across the collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> boundary.
/// </summary>
/// <remarks>
/// Called by <see cref="MemoryProbe.RunClosureAsync"/> every
/// <c>SingletonResetEveryN</c> iterations.  The returned list of names is included in
/// the <see cref="HeapMeasurement.SingletonsReset"/> field for reporting and audit.
/// </remarks>
public static class SingletonReset
{
    /// <summary>
    /// Resets all known pinners and returns a list describing what was reset (or why
    /// a given pinner was left in place).
    /// </summary>
    /// <returns>
    /// A read-only list of human-readable reset entries, one per tracked singleton.
    /// Each entry is either the name of the pinner that was reset, or a note of the
    /// form <c>"&lt;name&gt;: &lt;reason why not reset&gt;"</c>.
    /// </returns>
    public static IReadOnlyList<string> ResetAll()
    {
        var log = new List<string>(8);

        // ── Npgsql connection pool ────────────────────────────────────────────
        // NpgsqlConnection.ClearAllPools() is synchronous void (verified against
        // Npgsql 8.0.7 public API surface via reflection).  It drains the global
        // pool so that per-cycle connections do not accumulate across iterations.
        Npgsql.NpgsqlConnection.ClearAllPools();
        log.Add("Npgsql: NpgsqlConnection.ClearAllPools()");

        // ── HttpClient / SocketsHttpHandler pool ──────────────────────────────
        // The HttpClient instance created by ClosureProbeScript is disposed inside
        // the script body's finally block on every iteration, so no pool entry
        // accumulates across iterations.  The handler pool itself (managed by
        // SocketsHttpHandler) is per-handler and is cleaned up when the HttpClient
        // is disposed.  No additional harness-level reset is required.
        log.Add("HttpClient: disposed per cycle in ClosureProbeScript finally block");

        // ── Confluent.Kafka native producer/consumer handles ──────────────────
        // Sprint 6: the closure probe now builds REAL native librdkafka handles — a
        // ProducerBuilder<>.Build() producer and a ConsumerBuilder<>.Build() consumer —
        // on a bounded cadence (HandleBuildEveryN), and Dispose()s EACH inside the
        // probe's own finally on every cycle it builds them.  Disposal releases the
        // native handle (and joins its background threads) per cycle, so no handle
        // accumulates across iterations and there is nothing for the harness to reset
        // here.  librdkafka's native library itself loads ONCE into the process (the
        // Default ALC) and is shared — that is a constant, non-growing, intentional
        // overhead, NOT a per-cycle pin of the collectible context.  No global
        // Confluent.Kafka reset API exists or is needed.
        log.Add("Confluent.Kafka: native producer/consumer handles Built+Disposed per cycle in the probe finally; native lib loads once into the Default ALC (constant overhead), no global reset needed");

        // ── MongoDB.Driver cluster registry ───────────────────────────────────
        // Phase 1b: the closure probe builds a REAL MongoClient every 50 iterations
        // (inside the if(iter%50==0) block) and Disposes it in finally.  MongoDB.Driver
        // 3.x registers the cluster in a static ClusterRegistry keyed by ClusterId;
        // Dispose removes the entry, so the registry stays bounded.  No global reset
        // is possible or necessary — the per-instance Dispose is sufficient.
        log.Add("MongoDB.Driver: real MongoClient Built+Disposed per 50-iter cycle; ClusterRegistry entry removed on Dispose — no global reset needed");

        // ── StackExchange.Redis multiplexer pool ──────────────────────────────
        // ConfigurationOptions.Parse is a pure in-memory parse; no multiplexer or
        // socket is opened.  There is no connection pool to reset.
        log.Add("StackExchange.Redis: ConfigurationOptions.Parse is in-memory; no multiplexer opened, no reset needed");

        // ── Confluent.SchemaRegistry / Apache.Avro ────────────────────────────
        // Sprint 6: the closure probe constructs a CachedSchemaRegistryClient every
        // iteration and Dispose()s it inside the probe's own finally.  Its schema cache
        // is an in-memory, PER-INSTANCE cache released on Dispose() — no process-wide
        // singleton accumulates.  The Avro serdes (AvroSerializer/AvroDeserializer) and
        // GenericRecord are ordinary objects collected with the per-cycle graph; their
        // assembly static initialisers run once into the Default ALC (constant overhead).
        // No global reset exists or is needed.
        log.Add("Confluent.SchemaRegistry/Avro: CachedSchemaRegistryClient Disposed per cycle (per-instance schema cache); serdes/GenericRecord are plain per-cycle objects, no global reset needed");

        // ── Polly v8 RetryRunner ──────────────────────────────────────────────
        // Sprint 6: the closure probe drives Platform.Engine.Abstractions.Retry.RetryRunner,
        // which is STATELESS — it builds a FRESH ResiliencePipeline per PollAsync call and
        // holds no mutable static state (§5).  Nothing roots the collectible ALC, so there
        // is nothing to reset.  This entry is documentation-only.
        log.Add("Polly/RetryRunner: stateless — a fresh ResiliencePipeline is built per call, no static state, no reset needed");

        // ── Webhook capture accessor (Sprint 7) ───────────────────────────────
        // Sprint 7: the closure probe reads ScriptGlobalVariables.Webhooks (an
        // IWebhookCaptureAccessor) inside the collectible ALC.  The accessor handed in by
        // the harness is a by-reference Default-ALC STUB (ProbeWebhookAccessor) carrying an
        // immutable, pre-seeded snapshot of CapturedWebhookRequest records and NO mutable or
        // static state — exactly like the real host-owned accessor is a long-lived Default-ALC
        // instance.  The CSX only READS it (GetCaptured → iterate); it can never mutate or pin
        // it.  Nothing accumulates across iterations, so there is nothing to reset.  This entry
        // is documentation-only (mirroring the Polly/SchemaRegistry "stateless, no reset" notes).
        log.Add("Webhooks/IWebhookCaptureAccessor: by-reference Default-ALC stub with an immutable pre-seeded snapshot, read-only from the CSX, no global state — nothing to reset");

        // ── Microsoft.Data.SqlClient connection pool (Phase 1b) ──────────────
        // The closure probe builds a REAL SqlConnection every 50 iterations and
        // Disposes it in finally.  SqlClient's connection pool is process-wide;
        // SqlConnection.Dispose() returns the connection to the pool (or closes it
        // if the pool is full), so no net accumulation occurs.  No explicit
        // SqlConnection.ClearAllPools() is called because:
        //   (a) the pool is bounded and shared with the Default ALC (§5);
        //   (b) calling ClearAllPools() in the probe would interfer with any other
        //       SqlClient usage in the process.
        // The per-cycle Dispose() is sufficient; the pool is constant overhead.
        log.Add("Microsoft.Data.SqlClient: real SqlConnection Built+Disposed per 50-iter cycle; connection returned to bounded pool on Dispose — no global reset needed");

        // ── OpenTelemetry TracerProvider ──────────────────────────────────────
        // OpenTelemetry is NOT part of the proven closure — no OTel package is
        // referenced by the harness or the probe script, and no TracerProvider is
        // ever built.  This entry is documentation-only, recorded here so future
        // maintainers understand why OTel is absent from the reset list and that
        // its absence is intentional (constant zero overhead, not an oversight).
        log.Add("OpenTelemetry: not in closure — no TracerProvider built (documented for completeness)");

        return log;
    }
}
