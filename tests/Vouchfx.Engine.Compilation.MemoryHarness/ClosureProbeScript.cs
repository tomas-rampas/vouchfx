// Vouchfx.Engine.Compilation.MemoryHarness — ClosureProbeScript (S02-B-01, §5).
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

namespace Vouchfx.Engine.Compilation.MemoryHarness;

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
    ///     <b>StackExchange.Redis (cache-assert.redis)</b> — builds a REAL
    ///     <c>ConnectionMultiplexer.Connect(…,abortConnect=false)</c> (the exact
    ///     create-and-dispose discipline the cache-assert.redis provider's emitted helper
    ///     relies on): <c>abortConnect=false</c> returns a multiplexer without a running
    ///     server but still spins its heartbeat timer, socket, and reconnect thread, reads
    ///     <c>Configuration.Length</c>, then <c>Dispose()</c>s it in a <c>finally</c>.  No
    ///     <c>GetDatabase</c>/<c>StringGet</c> — there is no broker.  Gated to
    ///     <c>iter % 50 == 0</c> (same cadence as the Kafka native-handle build) because each
    ///     <c>Connect()</c> spins background threads.
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
    ///   <item><description>
    ///     <b>Traces read path (Phase C)</b> — reads the host-owned OTLP span-capture
    ///     snapshot through the NEW <c>ScriptGlobalVariables.Traces</c> accessor
    ///     (<c>Traces.GetCaptured("probe")</c>, paralleling <c>Webhooks</c>/<c>Secrets</c>)
    ///     and iterates the returned <c>CapturedSpan</c> records. Proves the accessor +
    ///     record graph read path does not pin the collectible ALC, backing
    ///     <c>trace-expect.otlp</c>. Under the closure run the harness stub seeds two spans;
    ///     under the trivial probe the null accessor returns an empty list and the loop is a
    ///     no-op.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Microsoft.Data.SqlClient real SqlConnection (Phase 1b)</b> — builds a REAL
    ///     <c>SqlConnection</c> (exercises SNI initialisation and connection-string parsing
    ///     static initialisers, the exact state <c>db-assert.sqlserver</c>'s emitted
    ///     helper relies on), reads <c>ConnectionString.Length</c>, then <c>Dispose()</c>s
    ///     in <c>finally</c>.  No <c>Open()</c> — the host does not exist.  Gated to
    ///     <c>iter % 50 == 0</c> (same cadence as the Kafka native-handle build).
    ///   </description></item>
    ///   <item><description>
    ///     <b>AWSSDK.DynamoDBv2 / AWSSDK.S3 real client build + Dispose (Phase B)</b> —
    ///     builds a REAL <c>AmazonDynamoDBClient</c> and a REAL <c>AmazonS3Client</c>
    ///     (each exercises the AWS SDK's static initialisers and credential/config
    ///     resolution path the db-assert.dynamodb / storage-assert.s3 providers' emitted
    ///     helpers rely on), pointed at a deliberately unreachable local port so no
    ///     network I/O completes, then <c>Dispose()</c>s each in <c>finally</c>.  Gated to
    ///     <c>iter % 50 == 0</c> (same cadence as the other native/SDK-handle builds).
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

            // ── Microsoft.Data.SqlClient real SqlConnection build + Dispose ─────
            // Build a REAL SqlConnection (exercises static initialisers, connection string
            // validation, SNI initialisation on first touch).  Read a trivial property
            // (ConnectionString), then Dispose() in finally.  No Open() / query — the
            // address does not exist.  Exercises the same discipline the db-assert.sqlserver
            // provider's emitted helper uses (Microsoft.Data.SqlClient).
            // 'using var' is prohibited in CSX bodies (§13.3.1); explicit Dispose() in finally.
            Microsoft.Data.SqlClient.SqlConnection? sqlConn = null;
            try
            {
                sqlConn = new Microsoft.Data.SqlClient.SqlConnection(
                    "Server=localhost,1433;Database=probe_db;User Id=sa;Password=P@ssw0rd;TrustServerCertificate=true;");
                Vars["sql_conn_str_len"] = sqlConn.ConnectionString.Length;
            }
            finally
            {
                sqlConn?.Dispose();
            }

            // ── MySqlConnector real MySqlConnection build + Dispose ─────────────
            // Build a REAL MySqlConnection (exercises static initialisers, connection
            // string parsing, pool registration logic).  Read a trivial property
            // (ConnectionString), then Dispose() in finally.  No Open() / query —
            // the host does not exist.  Exercises the same discipline the
            // db-assert.mysql provider's emitted helper uses (MySqlConnector).
            // 'using var' is prohibited in CSX bodies (§13.3.1); explicit Dispose() in finally.
            MySqlConnector.MySqlConnection? mysqlConn = null;
            try
            {
                mysqlConn = new MySqlConnector.MySqlConnection(
                    "Server=localhost;Database=probe;Uid=probe;Pwd=probe;");
                Vars["mysql_conn_str_len"] = mysqlConn.ConnectionString.Length;
            }
            finally
            {
                mysqlConn?.Dispose();
            }

            // ── StackExchange.Redis real ConnectionMultiplexer build + Dispose ──
            // Build a REAL ConnectionMultiplexer (the exact create-and-dispose discipline
            // the cache-assert.redis provider's emitted helper relies on).  abortConnect=false
            // returns a multiplexer WITHOUT a running server (no Redis needed) but still spins
            // up the heartbeat timer, socket, and reconnect thread that MUST be released before
            // the collectible ALC unloads (§5).  Read a trivial property (Configuration), then
            // Dispose() in finally.  No StringGet / GetDatabase call — there is no server; only
            // the multiplexer's internal resources are allocated and released.
            // 'using var' is prohibited in CSX bodies (§13.3.1); explicit Dispose() in finally.
            StackExchange.Redis.ConnectionMultiplexer? mux = null;
            try
            {
                mux = StackExchange.Redis.ConnectionMultiplexer.Connect(
                    "localhost:6379,abortConnect=false,connectTimeout=200,connectRetry=0");
                Vars["redis_cfg_len"] = mux.Configuration.Length;
            }
            finally
            {
                mux?.Dispose();
            }

            // ── RabbitMQ.Client ConnectionFactory build (every 50 iters) ────────────────────────
            // Build a REAL ConnectionFactory and attempt CreateConnectionAsync to a deliberately
            // unreachable address (timeout set to 10ms so it fails fast).  The factory ctor
            // exercises the same static initialisers the mq-publish.rabbitmq / mq-expect.rabbitmq
            // providers rely on.  No broker is needed: the test is that the reference graph loads
            // into and is released from the collectible ALC without anchoring static state.
            // IConnection is IAsyncDisposable only in RabbitMQ.Client 7.x — disposed via
            // await DisposeAsync().ConfigureAwait(false) in finally (§5 / §13.3.1).
            RabbitMQ.Client.IConnection? rmqConn = null;
            try
            {
                var rmqFactory = new RabbitMQ.Client.ConnectionFactory
                {
                    Uri = new System.Uri("amqp://localhost:5672"),
                    RequestedConnectionTimeout = System.TimeSpan.FromMilliseconds(10)
                };
                try
                {
                    rmqConn = await rmqFactory.CreateConnectionAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                Vars["rmq_factory_built"] = 1L;
            }
            finally
            {
                if (rmqConn is not null)
                    await rmqConn.DisposeAsync().ConfigureAwait(false);
            }

            // ── NATS.Net NatsConnection build + DisposeAsync (every 50 iters) ─────
            // Build a REAL NatsConnection (exercises NATS.Client.Core static initialisers
            // and the JetStream context path the mq-publish.nats / mq-expect.nats
            // providers rely on).  The connect attempt targets a deliberately unreachable
            // address (no server running locally) so no network I/O completes; only the
            // handle allocation and async-dispose discipline are exercised.  Swallow any
            // connection-level exception so the probe is non-destructive.
            // NatsConnection is IAsyncDisposable.  Dispose via
            // await natsConn.DisposeAsync().ConfigureAwait(false) in a finally block —
            // 'using var' / 'await using var' are prohibited in CSX bodies (§13.3.1).
            NATS.Client.Core.NatsConnection? natsConn = null;
            try
            {
                natsConn = new NATS.Client.Core.NatsConnection(
                    new NATS.Client.Core.NatsOpts { Url = "nats://localhost:4222" });
                Vars["nats_conn_built"] = 1L;
            }
            catch { }
            finally
            {
                if (natsConn is not null)
                {
                    try { await natsConn.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }

            // ── Azure.Messaging.ServiceBus ServiceBusClient build + DisposeAsync ────
            // Build a REAL ServiceBusClient (exercises static initialisers, connection-
            // string parsing, and the AMQP transport-layer setup the
            // mq-publish.azureservicebus / mq-expect.azureservicebus providers' emitted
            // helpers rely on).  Read a trivial property, then DisposeAsync() in finally.
            // No SendAsync / PeekMessagesAsync — there is no broker; only the client
            // instance is allocated and released.
            // ServiceBusClient is IAsyncDisposable.  Dispose via
            // await asbClient.DisposeAsync().ConfigureAwait(false) in a finally block —
            // 'using var' / 'await using var' are prohibited in CSX bodies (§13.3.1).
            Azure.Messaging.ServiceBus.ServiceBusClient? asbClient = null;
            try
            {
                asbClient = new Azure.Messaging.ServiceBus.ServiceBusClient(
                    "Endpoint=sb://localhost:5672;SharedAccessKeyName=RootManageSharedAccessKey;" +
                    "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");
                Vars["asb_client_built"] = 1L;
            }
            catch { }
            finally
            {
                if (asbClient is not null)
                {
                    try { await asbClient.DisposeAsync().ConfigureAwait(false); } catch { }
                }
            }

            // ── Amazon.DynamoDBv2.AmazonDynamoDBClient build + Dispose ──────────
            // Build a REAL AmazonDynamoDBClient (exercises AWS SDK static initialisers and
            // the credential/config resolution path db-assert.dynamodb's emitted helper
            // relies on). ServiceURL points at a deliberately unreachable local port so no
            // network I/O completes; only the handle allocation and dispose discipline are
            // exercised. AmazonDynamoDBClient : IDisposable — explicit Dispose() in finally
            // ('using var' is prohibited in CSX bodies, §13.3.1).
            Amazon.DynamoDBv2.AmazonDynamoDBClient? dynamoClient = null;
            try
            {
                var dynamoConfig = new Amazon.DynamoDBv2.AmazonDynamoDBConfig
                {
                    ServiceURL = "http://127.0.0.1:1",
                    MaxErrorRetry = 0,
                };
                dynamoClient = new Amazon.DynamoDBv2.AmazonDynamoDBClient(
                    new Amazon.Runtime.BasicAWSCredentials("probe", "probe"), dynamoConfig);
                Vars["dynamo_client_built"] = 1L;
            }
            finally
            {
                dynamoClient?.Dispose();
            }

            // ── Amazon.S3.AmazonS3Client build + Dispose ────────────────────────
            // Build a REAL AmazonS3Client (exercises AWS SDK static initialisers and the
            // credential/config resolution path storage-assert.s3's emitted helper relies
            // on). ServiceURL points at a deliberately unreachable local port so no network
            // I/O completes; only the handle allocation and dispose discipline are exercised.
            // AmazonS3Client : IDisposable — explicit Dispose() in finally.
            Amazon.S3.AmazonS3Client? s3Client = null;
            try
            {
                var s3Config = new Amazon.S3.AmazonS3Config
                {
                    ServiceURL = "http://127.0.0.1:1",
                    ForcePathStyle = true,
                    MaxErrorRetry = 0,
                };
                s3Client = new Amazon.S3.AmazonS3Client(
                    new Amazon.Runtime.BasicAWSCredentials("probe", "probe"), s3Config);
                Vars["s3_client_built"] = 1L;
            }
            finally
            {
                s3Client?.Dispose();
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
        await Vouchfx.Engine.Abstractions.Retry.RetryRunner.PollAsync(
            Vars,
            "rr_outcome",
            "rr_attempts",
            1000L,
            1L,
            async (System.Threading.CancellationToken __ct) =>
                new Vouchfx.Engine.Abstractions.StepOutcome(
                    Vouchfx.Engine.Abstractions.Verdict.Pass, 0L, null));

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

        // ── Phase C: ScriptGlobalVariables.Traces read path (cheap — every iter) ─
        // Read the host-owned OTLP span-capture snapshot through the NEW Traces accessor
        // (paralleling Webhooks/Secrets) and iterate the returned CapturedSpan records. This
        // forces the CSX (collectible ALC) to (a) call the ITraceCaptureAccessor instance
        // handed in via ScriptGlobalVariables.Traces and (b) walk the immutable CapturedSpan
        // record graph (TraceId/SpanId/Name/ServiceName/StatusCode + the Attributes
        // dictionary) seeded by the harness stub. If either pinned the collectible context,
        // the per-cycle heap delta would grow. In the closure run the stub returns two spans
        // for "probe"; under the trivial probe (and any run with no receiver) Traces is a
        // NullTraceCaptureAccessor that returns an empty list, so this loop runs zero times
        // and is harmless. Result via Vars only; fully-qualified types; no 'using var'.
        var __tr = Traces.GetCaptured("probe");
        long __trlen = 0;
        for (int i = 0; i < __tr.Count; i++)
        {
            __trlen += __tr[i].TraceId.Length + __tr[i].SpanId.Length + __tr[i].Name.Length +
                __tr[i].ServiceName.Length + __tr[i].StatusCode.Length;
            foreach (var kv in __tr[i].Attributes)
            {
                __trlen += kv.Key.Length + kv.Value.Length;
            }
        }
        Vars["trace_touch_len"] = __trlen;
        """;
}
