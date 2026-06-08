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
        var log = new List<string>(4);

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

        // ── Confluent.Kafka producer cache ────────────────────────────────────
        // The closure probe creates only a ProducerConfig (a plain POCO) — no
        // producer or broker connection is built.  librdkafka's internal handle is
        // never allocated, so there is no pool to reset.
        log.Add("Confluent.Kafka: ProducerConfig is a plain POCO; no handle allocated, no reset needed");

        // ── MongoDB.Driver cluster registry ───────────────────────────────────
        // The closure probe creates a MongoClientSettings object but never builds a
        // MongoClient, so the driver's internal ClusterRegistry is never populated.
        // No reset is possible or necessary.
        log.Add("MongoDB.Driver: MongoClientSettings created; no MongoClient built, no cluster registered");

        // ── StackExchange.Redis multiplexer pool ──────────────────────────────
        // ConfigurationOptions.Parse is a pure in-memory parse; no multiplexer or
        // socket is opened.  There is no connection pool to reset.
        log.Add("StackExchange.Redis: ConfigurationOptions.Parse is in-memory; no multiplexer opened, no reset needed");

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
