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
    ///   <item><description>
    ///     <b>Confluent.Kafka native handles (Sprint 6)</b> — builds a REAL
    ///     <c>ProducerBuilder&lt;string,string&gt;().Build()</c> producer and a
    ///     <c>ConsumerBuilder&lt;string,string&gt;().Build()</c> consumer (each allocates
    ///     a native librdkafka handle — the exact discipline mq-publish/mq-expect rely on),
    ///     reads <c>Name.Length</c>, then <c>Dispose()</c>s each in a <c>finally</c>.  No
    ///     <c>ProduceAsync</c> / <c>Subscribe</c> / <c>Consume</c> — there is no broker.
    ///     The handle build is gated to <c>Vars["__iter"] % 50 == 0</c> because each
    ///     <c>Build()</c> churns native threads; a per-iteration pin would still surface
    ///     across the few-hundred real builds over the run.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Confluent.SchemaRegistry Avro serdes (Sprint 6)</b> — constructs a
    ///     <c>CachedSchemaRegistryClient</c> (lazy; no network), an
    ///     <c>AvroSerializer&lt;GenericRecord&gt;</c> + <c>AvroDeserializer&lt;GenericRecord&gt;</c>,
    ///     parses a tiny Avro schema and builds a <c>GenericRecord</c>, writes a length to
    ///     <c>Vars</c>, then <c>Dispose()</c>s the registry client in a <c>finally</c>.
    ///     Runs every iteration (cheap — no native handle).  No serialize (needs a registry).
    ///   </description></item>
    ///   <item><description>
    ///     <b>Polly RETRY runner (Sprint 6)</b> — invokes
    ///     <c>RetryRunner.PollAsync</c> with an immediately-passing attempt (one attempt),
    ///     exercising the Polly <c>ResiliencePipeline</c> construction and the cross-boundary
    ///     lambda pass.  Writes the outcome + attempt timeline into <c>Vars</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Webhooks read path (Sprint 7)</b> — reads the host-owned webhook capture
    ///     snapshot through the NEW <c>ScriptGlobalVariables.Webhooks</c> accessor
    ///     (<c>Webhooks.GetCaptured("probe")</c>, paralleling <c>Secrets</c>) and iterates
    ///     the returned <c>CapturedWebhookRequest</c> records, summing their
    ///     <c>Method</c>/<c>Path</c>/<c>Body</c> lengths into <c>Vars</c>.  Proves the
    ///     accessor + record graph read path does not pin the collectible ALC (S07-E1).
    ///     Under the closure run the harness stub seeds two requests; under the trivial
    ///     probe the null accessor returns an empty list and the loop is a no-op.
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

        // ── Confluent.Kafka config touch ──────────────────────────────────────
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

        // ── Sprint-6: REAL native Confluent producer + consumer handles ──────
        // librdkafka allocates a native handle (and background threads) on Build().
        // This is the EXACT create-and-dispose discipline mq-publish/mq-expect rely on:
        // Build → read a trivial property → Dispose() in finally.  No ProduceAsync /
        // Subscribe / Consume — there is no broker; we only allocate and free the handle.
        // The handle build is GATED to Vars["__iter"] % 50 == 0 (HandleBuildEveryN cadence):
        // each Build() churns native threads, so building one EVERY iteration would dominate
        // the wall-clock.  A pin accumulates, so a few-hundred real builds across the run is
        // ample to catch a per-iteration ALC pin.  Warm-up passes __iter == 0, so it builds.
        var iter = Vars.TryGetValue("__iter", out var iterObj) && iterObj is long il ? il : 0L;
        if (iter % 50 == 0)
        {
            Confluent.Kafka.IProducer<string, string> producer =
                new Confluent.Kafka.ProducerBuilder<string, string>(
                    new Confluent.Kafka.ProducerConfig { BootstrapServers = "localhost:9092" }).Build();
            try
            {
                Vars["kafka_producer_name_len"] = producer.Name.Length;
            }
            finally
            {
                // Explicit Dispose() in finally (CSX disallows 'using var').  No Flush —
                // nothing was produced.  Dispose releases the native librdkafka handle.
                producer.Dispose();
            }

            Confluent.Kafka.IConsumer<string, string> consumer =
                new Confluent.Kafka.ConsumerBuilder<string, string>(
                    new Confluent.Kafka.ConsumerConfig
                    {
                        BootstrapServers = "localhost:9092",
                        GroupId = "probe"
                    }).Build();
            try
            {
                Vars["kafka_consumer_name_len"] = consumer.Name.Length;
            }
            finally
            {
                // No Close() — Close() requires a subscription/group join (a network op).
                // Dispose() alone releases the native handle, which is all the leak gate needs.
                consumer.Dispose();
            }

            // ── MongoDB.Driver real MongoClient build + Dispose ────────────────
            // Build a REAL MongoClient (exercises static initialisers, connection pool
            // creation). Read a trivial property (Settings.Server), then Dispose() in
            // finally.  No Connect() / query — the address does not exist; only the handle
            // is allocated and released.  Exercises the same discipline the db-assert.mongodb
            // provider's emitted helper uses (MongoDB.Driver 3.x: IMongoClient : IDisposable).
            // 'using var' is prohibited in CSX bodies (§13.3.1); explicit Dispose() in finally.
            MongoDB.Driver.MongoClient? mongoClient = null;
            try
            {
                mongoClient = new MongoDB.Driver.MongoClient("mongodb://localhost:27017");
                Vars["mongo_client_svr_len"] = mongoClient.Settings.Server.ToString().Length;
            }
            finally
            {
                mongoClient?.Dispose();
            }
        }

        // ── Sprint-6: Avro serdes + schema-registry client (cheap — every iter) ─
        // Construction is lazy (no network): CachedSchemaRegistryClient does not contact
        // the registry until a serialize/lookup, and AvroSerializer/AvroDeserializer just
        // capture the client + config.  This exercises the serdes static initialisers and
        // the IDisposable registry client (its in-memory schema cache is per-instance and
        // released on Dispose).  We do NOT serialize — that would need the live registry.
        var sr = new Confluent.SchemaRegistry.CachedSchemaRegistryClient(
            new Confluent.SchemaRegistry.SchemaRegistryConfig { Url = "http://localhost:8081" });
        try
        {
            var avroSerializer =
                new Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>(
                    sr, new Confluent.SchemaRegistry.Serdes.AvroSerializerConfig());
            var avroDeserializer =
                new Confluent.SchemaRegistry.Serdes.AvroDeserializer<Avro.Generic.GenericRecord>(sr);

            // Parse a tiny schema and build a GenericRecord (exercises Apache.Avro init).
            var avroSchema = (Avro.RecordSchema)Avro.Schema.Parse(
                "{\"type\":\"record\",\"name\":\"P\",\"fields\":[{\"name\":\"id\",\"type\":\"string\"}]}");
            var record = new Avro.Generic.GenericRecord(avroSchema);
            record.Add("id", "probe-id");
            object idValue;
            record.TryGetValue("id", out idValue);
            Vars["avro_id_len"] = ((idValue as string) ?? string.Empty).Length;
            Vars["avro_serdes_built"] =
                (avroSerializer is not null && avroDeserializer is not null) ? 1L : 0L;
        }
        finally
        {
            // Explicit Dispose() in finally (CSX disallows 'using var').  Releases the
            // registry client's per-instance in-memory schema cache.
            sr.Dispose();
        }

        // ── Sprint-6: Polly-backed RETRY runner (cheap — every iter) ─────────
        // An immediately-passing attempt → exactly one attempt.  This exercises the Polly
        // ResiliencePipeline construction (a fresh pipeline per call — stateless RetryRunner)
        // and the cross-ALC-boundary lambda pass.  The result + per-attempt timeline land in
        // Vars under the keys below; the lambda returns a Pass StepOutcome on first try.
        await Platform.Engine.Abstractions.Retry.RetryRunner.PollAsync(
            Vars,
            "rr_outcome",
            "rr_attempts",
            1000L,
            1L,
            async (System.Threading.CancellationToken __ct) =>
                new Platform.Engine.Abstractions.StepOutcome(
                    Platform.Engine.Abstractions.Verdict.Pass, 0L, null));

        // ── Sprint-7: ScriptGlobalVariables.Webhooks read path (cheap — every iter) ─
        // Read the host-owned webhook capture snapshot through the NEW Webhooks accessor
        // (paralleling Secrets) and iterate the returned CapturedWebhookRequest records.
        // This forces the CSX (collectible ALC) to (a) call the IWebhookCaptureAccessor
        // instance handed in via ScriptGlobalVariables.Webhooks and (b) walk the immutable
        // CapturedWebhookRequest record graph (Method/Path/Body) seeded by the harness stub.
        // If either pinned the collectible context, the per-cycle heap delta would grow.
        // In the closure run the stub returns two requests for "probe"; under the trivial
        // probe (and any run with no listener) Webhooks is a NullWebhookCaptureAccessor that
        // returns an empty list, so this loop runs zero times and is harmless.  Result via
        // Vars only; fully-qualified types; no 'using var' (CSX rules, §13.3.1).
        var __wh = Webhooks.GetCaptured("probe");
        long __whlen = 0;
        for (int i = 0; i < __wh.Count; i++)
        {
            __whlen += __wh[i].Method.Length + __wh[i].Path.Length + __wh[i].Body.Length;
        }
        Vars["webhook_touch_len"] = __whlen;
        """;
}
