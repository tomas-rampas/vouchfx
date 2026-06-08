// Platform.Engine.Compilation.MemoryHarness — ClosureProbeScript (S02-B-01, §5).
//
// The CSX body executed by MemoryProbe.RunClosureAsync.
//
// Purpose: force the static initialisers of every Core-provider canonical client to run
// inside a collectible AssemblyLoadContext so that any singleton pinners (connection pools,
// handler caches, tracer registries) are exercised by the memory leak gate.  If any such
// singleton anchors a reference across the collectible boundary, the per-cycle heap delta
// will grow and the gate will catch it.
//
// Design constraints (CLAUDE.md §5 / §13.3.1):
//   • No top-level 'using' directives — fully-qualified type names throughout.
//   • No 'using var' — plain 'var' + explicit .Dispose() in a 'finally'.
//   • No network I/O — all operations are purely in-memory.
//   • Results written ONLY to Vars (the single mutable crossing point).
//   • Written as a C# 11 double-dollar raw string ($$"""…""") so literal braces in the
//     CSX block pass through verbatim and {{…}} is the interpolation hole.

namespace Platform.Engine.Compilation.MemoryHarness;

/// <summary>
/// Provides the CSX source body used by <see cref="MemoryProbe.RunClosureAsync"/> to
/// exercise the Core-provider canonical client closure inside each collectible
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> iteration.
/// </summary>
public static class ClosureProbeScript
{
    /// <summary>
    /// The CSX script body.  Touches one type from each canonical client library so
    /// that their assemblies are genuinely loaded and their static initialisers run:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Npgsql</b> (<c>NpgsqlConnectionStringBuilder</c>) — creates a builder with
    ///     host and database set, writes the connection-string length to <c>Vars</c>.
    ///     No actual connection is opened.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Confluent.Kafka</b> (<c>ProducerConfig</c>) — creates a config with
    ///     <c>BootstrapServers</c> set, writes the value length to <c>Vars</c>.
    ///     No producer is built; no socket is opened.
    ///   </description></item>
    ///   <item><description>
    ///     <b>MongoDB.Driver</b> (<c>MongoClientSettings</c>) — creates a settings object
    ///     with the parameterless constructor (no DNS lookup), writes the application-name
    ///     length to <c>Vars</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b>StackExchange.Redis</b> (<c>ConfigurationOptions.Parse</c>) — parses a
    ///     localhost endpoint string in-memory, writes <c>ToString()</c> length to
    ///     <c>Vars</c>.  No connection attempt is made.
    ///   </description></item>
    ///   <item><description>
    ///     <b>HttpClient / SocketsHttpHandler</b> (BCL) — creates an
    ///     <c>HttpClient</c> instance to trigger the <c>SocketsHttpHandler</c> pool, reads
    ///     a trivial property, then disposes it explicitly in a <c>finally</c> block (no
    ///     <c>using var</c> per CSX rules).  No request is sent.
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The body obeys all CSX fragment rules (CLAUDE.md §13.3.1):
    /// no <c>using var</c>; results via <c>Vars</c> only; fully-qualified names.
    /// </remarks>
    public const string Source = """
        // ── Npgsql touch ────────────────────────────────────────────────────────
        var npgsqlBuilder = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = "probe_db"
        };
        Vars["npgsql_cs_len"] = npgsqlBuilder.ConnectionString.Length;

        // ── Confluent.Kafka touch ─────────────────────────────────────────────
        var kafkaConfig = new Confluent.Kafka.ProducerConfig
        {
            BootstrapServers = "localhost:9092"
        };
        Vars["kafka_bs_len"] = kafkaConfig.BootstrapServers.Length;

        // ── MongoDB.Driver touch ─────────────────────────────────────────────
        var mongoSettings = new MongoDB.Driver.MongoClientSettings();
        Vars["mongo_app_len"] = (mongoSettings.ApplicationName ?? string.Empty).Length;

        // ── StackExchange.Redis touch ────────────────────────────────────────
        var redisOpts = StackExchange.Redis.ConfigurationOptions.Parse("localhost");
        Vars["redis_str_len"] = redisOpts.ToString().Length;

        // ── HttpClient / SocketsHttpHandler touch ────────────────────────────
        // No 'using var' — CSX disallows it.  Dispose in finally.
        var http = new System.Net.Http.HttpClient();
        try
        {
            Vars["http_timeout_ms"] = (long)http.Timeout.TotalMilliseconds;
        }
        finally
        {
            http.Dispose();
        }
        """;
}
