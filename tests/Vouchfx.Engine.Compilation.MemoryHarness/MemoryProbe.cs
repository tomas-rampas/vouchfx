// Vouchfx.Engine.Compilation.MemoryHarness — MemoryProbe (S01-B-03 / S02-B-01, §5).
//
// Measures the managed heap delta across a high-volume load-unload workload.
// This is the seed of the permanent CI leak gate (wired blocking in Sprint 2, S02-D-01).
//
// Two measurement modes:
//   trivial  — a single Vars["probe"]=1; script; establishes the ALC lifecycle baseline.
//   closure  — a script that touches one type from each Core-provider canonical client
//              (Npgsql, Confluent.Kafka, MongoDB.Driver, StackExchange.Redis, HttpClient)
//              so that their static initialisers run and any singleton pinners are exercised.
//              Sprint-6 also drives the NEW Kafka code paths inside the collectible ALC:
//              a real native Confluent producer + consumer handle (build/dispose), the
//              Confluent.SchemaRegistry Avro serdes + CachedSchemaRegistryClient, and the
//              Polly-backed RetryRunner — proving they do not pin the collectible context.
//
// Protocol (both modes):
//   1. Compile the probe script ONCE (§5: compile-once invariant).
//   2. Warm up a few load-unload cycles + forced GC to amortise one-time Roslyn /
//      JIT costs before the baseline is taken.
//   3. Take BaselineBytes after a quiescent GC.
//   4. Run the load-unload workload <iterations> times — one full collectible-ALC
//      lifecycle per iteration (the leak-sensitive path).
//   5. Force a bounded post-settle loop (up to MaxPostSettlePasses quiescent GC
//      rounds) and break as soon as collectibleAfter ≤ collectibleBefore.
//   6. Take PostBytes.
//   7. NetDeltaBytes = PostBytes - BaselineBytes.
//      If the ALC lifecycle is broken, uncollectable assemblies accumulate and
//      the delta explodes; if it is correct the delta stays near zero.
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Traces;
using Vouchfx.Engine.Abstractions.Webhooks;
using Vouchfx.Engine.Compilation;

namespace Vouchfx.Engine.Compilation.MemoryHarness;

/// <summary>
/// The structured result produced by <see cref="MemoryProbe.RunAsync"/> or
/// <see cref="MemoryProbe.RunClosureAsync"/>.
/// Serialised as a single-line JSON object to stdout by the harness executable.
/// </summary>
/// <param name="Mode">
/// The probe mode: <c>"trivial"</c> for the baseline ALC lifecycle test or
/// <c>"closure"</c> for the full Core-provider dependency closure test.
/// </param>
/// <param name="Iterations">Number of load-unload cycles exercised.</param>
/// <param name="BaselineBytes">
/// Managed heap size (bytes) after warm-up, before the workload.
/// </param>
/// <param name="PostBytes">
/// Managed heap size (bytes) after the workload and a quiescent GC.
/// </param>
/// <param name="NetDeltaBytes">
/// <see cref="PostBytes"/> − <see cref="BaselineBytes"/>.
/// A value close to zero confirms no assemblies were retained across iterations.
/// </param>
/// <param name="ThresholdBytes">
/// Acceptance ceiling for <see cref="NetDeltaBytes"/>.
/// </param>
/// <param name="Passed">
/// <see langword="true"/> when <see cref="NetDeltaBytes"/> is strictly below
/// <see cref="ThresholdBytes"/>.
/// </param>
/// <param name="CollectibleBefore">
/// Number of collectible assemblies in <see cref="System.AppDomain.CurrentDomain"/>
/// after warm-up, before the workload.
/// </param>
/// <param name="CollectibleAfter">
/// Number of collectible assemblies after the workload and a quiescent GC.
/// </param>
/// <param name="ContextReclaimed">
/// <see langword="true"/> when <see cref="CollectibleAfter"/> ≤
/// <see cref="CollectibleBefore"/>, confirming the ALC unload graph was fully
/// traversed by the GC.
/// </param>
/// <param name="SingletonsReset">
/// Names of the singletons (or singleton pools) reset by <see cref="SingletonReset"/>
/// during the workload.  Empty for the trivial run where no client libraries are touched.
/// </param>
public sealed record HeapMeasurement(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("iterations")] int Iterations,
    [property: JsonPropertyName("baselineBytes")] long BaselineBytes,
    [property: JsonPropertyName("postBytes")] long PostBytes,
    [property: JsonPropertyName("netDeltaBytes")] long NetDeltaBytes,
    [property: JsonPropertyName("thresholdBytes")] long ThresholdBytes,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("collectibleBefore")] int CollectibleBefore,
    [property: JsonPropertyName("collectibleAfter")] int CollectibleAfter,
    [property: JsonPropertyName("contextReclaimed")] bool ContextReclaimed,
    [property: JsonPropertyName("singletonsReset")] IReadOnlyList<string> SingletonsReset);

/// <summary>
/// Memory measurement harness for the compile-once / collectible-unload cycle (§5).
/// </summary>
/// <remarks>
/// <para>
/// Call <see cref="RunAsync"/> (trivial mode) to verify the ALC lifecycle baseline, or
/// <see cref="RunClosureAsync"/> (closure mode — the M1 CI gate) to exercise the full
/// transitive dependency closure of every Core provider's canonical client library.
/// Both methods are safe to call from any thread; neither mutates any static state.
/// </para>
/// <para>
/// The post-workload GC settle uses a bounded retry loop (up to
/// <see cref="MaxPostSettlePasses"/> rounds) rather than a single
/// <see cref="QuiescentGc"/> call.  This eliminates intermittent false failures caused
/// by finalisation timing on fast hardware (where the GC may need an extra pass to
/// fully unload all collectible ALCs) whilst still detecting real leaks (a leaking
/// context is never reclaimed regardless of how many passes the loop runs).
/// </para>
/// </remarks>
public static class MemoryProbe
{
    /// <summary>
    /// The CSX source compiled for the trivial probe.  Intentionally minimal —
    /// the goal is to measure ALC lifecycle overhead, not script body cost.
    /// </summary>
    private const string TrivialProbeScript = """
        Vars["probe"] = 1;
        """;

    /// <summary>
    /// The mode label written to <see cref="HeapMeasurement.Mode"/> for the trivial run.
    /// </summary>
    private const string TrivialMode = "trivial";

    /// <summary>
    /// The mode label written to <see cref="HeapMeasurement.Mode"/> for the closure run.
    /// </summary>
    public const string ClosureMode = "closure";

    /// <summary>
    /// Number of warm-up load-unload cycles executed before the baseline is taken.
    /// Enough to amortise Roslyn metadata caches and JIT compilation of the hot paths.
    /// </summary>
    private const int WarmUpIterations = 10;

    /// <summary>
    /// Number of quiescent GC passes (Collect + WaitForFinalizers) used at both the
    /// baseline and inside each post-settle iteration.
    /// </summary>
    private const int QuiescentGcPasses = 3;

    /// <summary>
    /// Maximum number of post-settle rounds when waiting for collectible ALCs to be
    /// reclaimed by the GC.  Ten rounds is far more than any correct implementation needs;
    /// exhausting the loop without reclaiming indicates a genuine leak.
    /// </summary>
    private const int MaxPostSettlePasses = 10;

    /// <summary>
    /// How often (every N iterations) <see cref="SingletonReset.ResetAll"/> is called in
    /// <see cref="RunClosureAsync"/> to prevent client connection-pool accumulation from
    /// perturbing the per-cycle heap measurement.
    /// </summary>
    private const int SingletonResetEveryN = 50;

    /// <summary>
    /// The cadence (every N iterations) at which the closure probe builds the REAL
    /// native librdkafka producer/consumer handles.  Each <c>Build()</c> allocates a
    /// native handle and spins up librdkafka background threads, so building one every
    /// single one of 5,000 iterations would dominate the wall-clock and churn OS
    /// threads — far more cost than is needed to detect a per-iteration ALC pin (a pin
    /// accumulates, so a few hundred real builds over the run is more than enough to
    /// reveal one).  The CHEAP static-init touches — the Avro serdes / schema-registry
    /// client and the Polly RETRY runner — run on EVERY iteration; only the native
    /// handle build is gated by this cadence.  The CSX body reads
    /// <c>Vars["__iter"] % HandleBuildEveryN == 0</c> to decide.
    /// </summary>
    /// <remarks>
    /// The probe passes the current iteration index into <c>Vars["__iter"]</c>; warm-up
    /// cycles pass <c>0</c>, so each warm-up also builds the handles once (amortising the
    /// one-time librdkafka native-init cost before the baseline is taken).
    /// </remarks>
    private const int HandleBuildEveryN = 50;

    /// <summary>
    /// The shared stub webhook-capture accessor used by the closure probe (S07-E1, §5).
    /// </summary>
    /// <remarks>
    /// Created once and reused across every closure iteration.  It is a by-reference
    /// Default-ALC instance (it lives in this harness, exactly like the real host-owned
    /// accessor lives in Orchestration's Default ALC) holding an immutable pre-seeded
    /// snapshot with no mutable or static state, so reuse is safe.  The closure CSX reads
    /// it through <c>ScriptGlobalVariables.Webhooks</c> to prove that read path does not
    /// pin the collectible context.  The trivial probe leaves <see cref="ScriptGlobalVariables.Webhooks"/> as
    /// the <see cref="NullWebhookCaptureAccessor"/> (it passes <see langword="null"/> here).
    /// </remarks>
    private static readonly ProbeWebhookAccessor ClosureWebhookAccessor = new();

    /// <summary>
    /// The shared stub OTLP trace-capture accessor used by the closure probe (Phase C, §5).
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="ClosureWebhookAccessor"/> exactly: created once and reused across
    /// every closure iteration, a by-reference Default-ALC instance holding an immutable
    /// pre-seeded snapshot with no mutable or static state. The closure CSX reads it through
    /// <c>ScriptGlobalVariables.Traces</c> to prove that read path does not pin the collectible
    /// context. The trivial probe leaves <see cref="ScriptGlobalVariables.Traces"/> as the
    /// <see cref="NullTraceCaptureAccessor"/> (it passes <see langword="null"/> here).
    /// </remarks>
    private static readonly ProbeTraceAccessor ClosureTraceAccessor = new();

    /// <summary>
    /// The shared stub step-event sink used by the closure probe (issue #262, §5).
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="ClosureWebhookAccessor"/> / <see cref="ClosureTraceAccessor"/> exactly:
    /// created once and reused across every closure iteration, a by-reference Default-ALC
    /// instance holding only bounded counters. The closure CSX calls
    /// <c>StepEvents?.OnStepStarted/OnStepAttempt/OnStepCompleted(...)</c> through it to prove
    /// that read path does not pin the collectible context. The trivial probe leaves
    /// <see cref="ScriptGlobalVariables.StepEvents"/> <see langword="null"/> (it passes
    /// <see langword="null"/> here).
    /// </remarks>
    private static readonly ProbeStepEventSink ClosureStepEventSink = new();

    /// <summary>
    /// The number of <see cref="IStepEventSink.OnStepCompleted"/> calls the shared
    /// <see cref="ClosureStepEventSink"/> has observed so far, across every closure run in this
    /// process (issue #262). Exposed so <c>ClosureMemoryProbeTests</c> can assert the sink was
    /// genuinely exercised by the just-completed <see cref="RunClosureAsync"/> call, proving the
    /// leak gate's coverage of the <c>StepEvents</c> read path is real, not merely wired but dead.
    /// </summary>
    public static long ClosureStepEventCompletedCount => ClosureStepEventSink.CompletedCount;

    /// <summary>
    /// Runs the trivial memory measurement protocol and returns a structured
    /// <see cref="HeapMeasurement"/> result.
    /// </summary>
    /// <remarks>
    /// The trivial probe script (<c>Vars["probe"] = 1;</c>) proves the ALC lifecycle
    /// is correct in isolation.  For the full M1 CI gate — which also exercises every
    /// Core provider's canonical client singleton — use <see cref="RunClosureAsync"/>.
    /// </remarks>
    /// <param name="iterations">
    /// Number of full load-unload cycles to execute in the measured workload.
    /// Default 5,000 for the CI gate run; use a smaller number (e.g. 200) in unit
    /// tests to keep the suite fast.
    /// </param>
    /// <param name="thresholdBytes">
    /// Maximum acceptable <see cref="HeapMeasurement.NetDeltaBytes"/>.
    /// Default 2,000,000 bytes (2 MB).
    /// </param>
    /// <param name="ct">Cancellation token propagated into each script invocation.</param>
    /// <returns>
    /// A <see cref="HeapMeasurement"/> describing the outcome of the run.  Inspect
    /// <see cref="HeapMeasurement.Passed"/> and <see cref="HeapMeasurement.ContextReclaimed"/>
    /// to determine overall success.
    /// </returns>
    public static async Task<HeapMeasurement> RunAsync(
        int iterations = 5_000,
        long thresholdBytes = 2_000_000,
        CancellationToken ct = default)
    {
        // ── Step 1: compile ONCE ────────────────────────────────────────────────
        var compiled = RoslynScriptCompiler.CompileOnce(TrivialProbeScript, cancellationToken: ct);

        // ── Step 2: warm up ─────────────────────────────────────────────────────
        // Run a handful of load-unload cycles through a NoInlining helper so the
        // JIT cannot keep any ALC reference alive on this frame.  Then force a
        // quiescent GC to flush all one-time costs before we take the baseline.
        for (var i = 0; i < WarmUpIterations; i++)
        {
            await RunIsolatedNoInlineAsync(compiled, null, $"warmup-{i}", ct).ConfigureAwait(false);
        }

        QuiescentGc();

        // ── Step 3: baseline ────────────────────────────────────────────────────
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
        var collectibleBefore = CountCollectibleAssemblies();

        // ── Step 4: workload ────────────────────────────────────────────────────
        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();
            await RunIsolatedNoInlineAsync(compiled, null, $"iter-{i}", ct).ConfigureAwait(false);
        }

        // ── Step 5: post measurement (bounded settle loop) ──────────────────────
        // A single QuiescentGc pass can miss ALCs whose finalisation spans multiple
        // GC rounds.  The loop drives the GC up to MaxPostSettlePasses times and
        // stops as soon as the collectible count returns to baseline.
        // A real leak never reclaims, so the loop exhausts and the test fails correctly.
        var collectibleAfter = collectibleBefore + 1; // sentinel: enter loop
        for (var pass = 0; pass < MaxPostSettlePasses && collectibleAfter > collectibleBefore; pass++)
        {
            QuiescentGc();
            collectibleAfter = CountCollectibleAssemblies();
        }

        var postBytes = GC.GetTotalMemory(forceFullCollection: true);

        // ── Step 6: verdict ─────────────────────────────────────────────────────
        var netDelta = postBytes - baselineBytes;
        var passed = netDelta < thresholdBytes;
        var contextReclaimed = collectibleAfter <= collectibleBefore;

        return new HeapMeasurement(
            Mode: TrivialMode,
            Iterations: iterations,
            BaselineBytes: baselineBytes,
            PostBytes: postBytes,
            NetDeltaBytes: netDelta,
            ThresholdBytes: thresholdBytes,
            Passed: passed,
            CollectibleBefore: collectibleBefore,
            CollectibleAfter: collectibleAfter,
            ContextReclaimed: contextReclaimed,
            SingletonsReset: Array.Empty<string>());
    }

    /// <summary>
    /// Runs the closure memory measurement protocol — the definitive M1 CI gate —
    /// and returns a structured <see cref="HeapMeasurement"/> result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="RunAsync"/> (trivial probe), this method compiles and runs
    /// <see cref="ClosureProbeScript.Source"/>, which touches one type from each of
    /// the Core provider canonical client libraries (Npgsql, Confluent.Kafka,
    /// MongoDB.Driver, StackExchange.Redis, and HttpClient).  This forces those
    /// assemblies' static initialisers to run so that any singleton pinners that could
    /// anchor objects across the collectible boundary are exercised.
    /// </para>
    /// <para>
    /// Sprint-6 additions: the probe also exercises the REAL code paths the new Kafka
    /// providers depend on — a native <c>Confluent.Kafka</c> producer and consumer
    /// handle (create-and-dispose, the exact discipline mq-publish/mq-expect rely on),
    /// the <c>Confluent.SchemaRegistry</c> Avro serdes + <c>CachedSchemaRegistryClient</c>,
    /// and the Polly-backed <c>RetryRunner</c>.  The native handle build runs on a bounded
    /// cadence (<see cref="HandleBuildEveryN"/>) because each <c>Build()</c> allocates a
    /// native handle and librdkafka threads; the cheap Avro / Polly touches run every
    /// iteration.  Proving these do not pin the collectible ALC is the core Sprint-6 gate.
    /// </para>
    /// <para>
    /// <see cref="SingletonReset.ResetAll"/> is called every
    /// <see cref="SingletonResetEveryN"/> iterations to prevent connection-pool
    /// accumulation from perturbing the per-cycle heap delta.  The reset list is
    /// captured once and included in the returned <see cref="HeapMeasurement"/>.
    /// </para>
    /// <para>
    /// The threshold remains the same 2 MB default as the trivial run: the client
    /// static caches are amortised during warm-up; only the per-cycle delta is
    /// measured.  If the 5,000-iteration delta consistently exceeds this figure,
    /// the root cause must be investigated and a specific singleton reset added to
    /// <see cref="SingletonReset"/> — not simply a threshold widening.
    /// </para>
    /// </remarks>
    /// <param name="iterations">
    /// Number of full load-unload cycles.  Default 5,000 for the CI gate; 200 for
    /// the unit test smoke.
    /// </param>
    /// <param name="thresholdBytes">
    /// Maximum acceptable <see cref="HeapMeasurement.NetDeltaBytes"/>.
    /// Default 2,000,000 bytes (2 MB).
    /// </param>
    /// <param name="ct">Cancellation token propagated into each script invocation.</param>
    /// <returns>
    /// A <see cref="HeapMeasurement"/> describing the outcome of the run.
    /// </returns>
    public static async Task<HeapMeasurement> RunClosureAsync(
        int iterations = 5_000,
        long thresholdBytes = 2_000_000,
        CancellationToken ct = default)
    {
        // Build the list of additional reference paths from the canonical client
        // assemblies that are already loaded into the Default context (because the
        // harness project references them).  These are compile-time references only;
        // at runtime the assemblies resolve from the Default context automatically.
        // System.Net.Http is also included explicitly: although it is a BCL assembly,
        // Roslyn's TPA-based reference builder only picks up CoreLib/Runtime/Collections
        // by default, so HttpClient is not resolved without this entry.
        var additionalRefs = new[]
        {
            typeof(Npgsql.NpgsqlConnection).Assembly.Location,
            typeof(Confluent.Kafka.ProducerConfig).Assembly.Location,
            typeof(MongoDB.Driver.MongoClientSettings).Assembly.Location,
            typeof(StackExchange.Redis.ConfigurationOptions).Assembly.Location,
            typeof(System.Net.Http.HttpClient).Assembly.Location,

            // ── Sprint-6 Kafka serdes + schema-registry closure ─────────────────
            // The Sprint-6 mq-publish.kafka / mq-expect.kafka providers build a
            // CachedSchemaRegistryClient, an AvroSerializer/AvroDeserializer<GenericRecord>,
            // and parse Avro schemas.  Reference those three assemblies so the closure
            // probe's Avro touches compile.  All are already loaded in the Default ALC
            // (the harness references the packages directly) and resolve from there at
            // runtime — they must never load into the collectible ALC (§5).
            typeof(Confluent.SchemaRegistry.CachedSchemaRegistryClient).Assembly.Location,
            typeof(Confluent.SchemaRegistry.Serdes.AvroSerializer<Avro.Generic.GenericRecord>).Assembly.Location,
            typeof(Avro.Schema).Assembly.Location,

            // ── Phase 1b: db-assert.sqlserver closure ──────────────────────────
            // The db-assert.sqlserver provider uses Microsoft.Data.SqlClient.SqlConnection
            // (via SqlCommand/SqlDataReader).  The closure probe builds a real SqlConnection
            // to exercise SNI initialisation and connection-pool static state.  Must be
            // included here so Roslyn can resolve the type at compile time; the assembly is
            // loaded in the Default ALC via the MemoryHarness.csproj package reference.
            typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly.Location,

            // ── Phase 1c: db-assert.mysql closure ─────────────────────────────
            // The db-assert.mysql provider uses MySqlConnector.MySqlConnection.  The closure
            // probe builds and disposes a real MySqlConnection every 50th iteration to exercise
            // the connection-pool static state.  Must be included here so Roslyn can resolve
            // MySqlConnector types at compile time; the assembly loads in the Default ALC via
            // the MemoryHarness.csproj PackageReference.
            typeof(MySqlConnector.MySqlConnection).Assembly.Location,

            // ── Sprint-13: mq-publish.rabbitmq / mq-expect.rabbitmq closure ──────
            // The two RabbitMQ providers use RabbitMQ.Client 7.x (IConnection /
            // IChannel both IAsyncDisposable only).  System.Uri is forwarded to
            // System.Private.Uri — Roslyn does not pick it up from System.Runtime
            // automatically, so it must be listed explicitly (new System.Uri(amqpUri)
            // appears in the connection factory initialiser).
            typeof(RabbitMQ.Client.ConnectionFactory).Assembly.Location,
            typeof(System.Uri).Assembly.Location,

            // ── mq-publish.nats / mq-expect.nats closure ────────────────────────
            // The two NATS providers use NATS.Net 2.x which splits into two assemblies:
            // NATS.Client.Core (NatsConnection, NatsOpts, NatsException) and
            // NATS.Client.JetStream (NatsJSContext, StreamConfig, ConsumerConfig, etc.).
            // Both must be included so the closure probe's NatsConnection build/dispose
            // compiles.  The assemblies resolve from the Default ALC (the harness
            // references NATS.Net directly); they must never load into the collectible ALC.
            typeof(NATS.Client.Core.NatsConnection).Assembly.Location,
            typeof(NATS.Client.JetStream.NatsJSContext).Assembly.Location,

            // ── mq-publish.azureservicebus / mq-expect.azureservicebus closure ──
            // The two ASB providers use Azure.Messaging.ServiceBus.ServiceBusClient.
            // Azure.Core.dll is a transitive dependency of Azure.Messaging.ServiceBus
            // and is deployed to the same output directory; it must be listed explicitly
            // because Roslyn does not auto-resolve it from the TPA.
            typeof(Azure.Messaging.ServiceBus.ServiceBusClient).Assembly.Location,
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(
                    typeof(Azure.Messaging.ServiceBus.ServiceBusClient).Assembly.Location)!,
                "Azure.Core.dll"),

            // ── Phase B: db-assert.dynamodb / storage-assert.s3 closure ─────────
            // The two AWS SDK providers build a real AmazonDynamoDBClient and a real
            // AmazonS3Client.  Both must be listed here so the closure probe's client
            // builds compile; the assemblies resolve from the Default ALC (the harness
            // references AWSSDK.DynamoDBv2 / AWSSDK.S3 directly) and must never load
            // into the collectible ALC (§5).
            typeof(Amazon.DynamoDBv2.AmazonDynamoDBClient).Assembly.Location,
            typeof(Amazon.S3.AmazonS3Client).Assembly.Location,
            typeof(Amazon.Runtime.BasicAWSCredentials).Assembly.Location,

            // Vouchfx.Engine.Abstractions is ALREADY added unconditionally by
            // RoslynScriptCompiler.BuildBaseOptions (the script accesses Vars from
            // ScriptGlobalVariables), so Vouchfx.Engine.Abstractions.Retry.RetryRunner
            // resolves at compile time without a separate entry here; Polly.Core (which
            // RetryRunner uses internally) arrives transitively in the Default ALC.
        };

        // ── Step 1: compile ONCE with the canonical-client metadata references ──
        var compiled = RoslynScriptCompiler.CompileOnce(
            ClosureProbeScript.Source,
            additionalReferencePaths: additionalRefs,
            cancellationToken: ct);

        // Capture the singleton reset list once (used in reporting and the result).
        var singletonsReset = SingletonReset.ResetAll();

        // ── Step 2: warm up ─────────────────────────────────────────────────────
        // Warm-up passes the SAME stub webhook accessor as the measured workload so the
        // Webhooks read path (and the CapturedWebhookRequest record graph) is JIT-warmed
        // before the baseline is taken — exactly as the Kafka/Avro/Polly touches are.
        for (var i = 0; i < WarmUpIterations; i++)
        {
            await RunIsolatedNoInlineAsync(
                    compiled, null, $"warmup-{i}", ct,
                    webhooks: ClosureWebhookAccessor, traces: ClosureTraceAccessor,
                    stepEvents: ClosureStepEventSink)
                .ConfigureAwait(false);
        }

        QuiescentGc();

        // ── Step 3: baseline ────────────────────────────────────────────────────
        var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);
        var collectibleBefore = CountCollectibleAssemblies();

        // ── Step 4: workload ────────────────────────────────────────────────────
        for (var i = 0; i < iterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Reset known singleton pools every N iterations to prevent pool
            // accumulation from perturbing the per-cycle delta.
            if (i % SingletonResetEveryN == 0)
                SingletonReset.ResetAll();

            // Pass the iteration index so the closure probe can gate the native
            // librdkafka producer/consumer handle build to HandleBuildEveryN cadence
            // (the cheap Avro / Polly touches still run every iteration), and pass the
            // stub webhook + trace + step-event accessors so the CSX exercises the
            // Webhooks (S07-E1), Traces (Phase C), and StepEvents (issue #262) read paths.
            await RunIsolatedNoInlineAsync(
                    compiled, null, $"iter-{i}", ct,
                    iterIndex: i, webhooks: ClosureWebhookAccessor, traces: ClosureTraceAccessor,
                    stepEvents: ClosureStepEventSink)
                .ConfigureAwait(false);
        }

        // ── Step 5: post measurement (bounded settle loop) ──────────────────────
        var collectibleAfter = collectibleBefore + 1; // sentinel: enter loop
        for (var pass = 0; pass < MaxPostSettlePasses && collectibleAfter > collectibleBefore; pass++)
        {
            QuiescentGc();
            collectibleAfter = CountCollectibleAssemblies();
        }

        var postBytes = GC.GetTotalMemory(forceFullCollection: true);

        // ── Step 6: verdict ─────────────────────────────────────────────────────
        var netDelta = postBytes - baselineBytes;
        var passed = netDelta < thresholdBytes;
        var contextReclaimed = collectibleAfter <= collectibleBefore;

        return new HeapMeasurement(
            Mode: ClosureMode,
            Iterations: iterations,
            BaselineBytes: baselineBytes,
            PostBytes: postBytes,
            NetDeltaBytes: netDelta,
            ThresholdBytes: thresholdBytes,
            Passed: passed,
            CollectibleBefore: collectibleBefore,
            CollectibleAfter: collectibleAfter,
            ContextReclaimed: contextReclaimed,
            SingletonsReset: singletonsReset);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Thin <see cref="MethodImplOptions.NoInlining"/> wrapper around
    /// <see cref="RoslynScriptCompiler.RunIsolatedAsync"/> so that the JIT cannot keep
    /// any reference to the internal <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
    /// alive on the caller's stack frame after this method returns.
    /// </summary>
    /// <param name="compiled">The compiled script image.</param>
    /// <param name="collectibleProbingPaths">
    /// Optional satellite probing paths forwarded to
    /// <see cref="RoslynScriptCompiler.RunIsolatedAsync"/>.
    /// </param>
    /// <param name="label">Human-readable label for the ALC name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="iterIndex">
    /// Iteration index forwarded to the closure probe via <c>Vars["__iter"]</c> to gate
    /// the native librdkafka handle build cadence.
    /// </param>
    /// <param name="webhooks">
    /// Optional stub webhook-capture accessor (S07-E1).  When non-<see langword="null"/>
    /// (the closure path), the globals are built with the full constructor so the CSX can
    /// exercise the <c>Webhooks.GetCaptured</c> read path across the collectible ALC; when
    /// <see langword="null"/> (the trivial path), the 1-arg constructor is used and
    /// <c>Webhooks</c> stays a <see cref="NullWebhookCaptureAccessor"/>.
    /// </param>
    /// <param name="traces">
    /// Optional stub OTLP trace-capture accessor (Phase C).  When non-<see langword="null"/>
    /// (the closure path), the globals are built with the full constructor so the CSX
    /// can exercise the <c>Traces.GetCaptured</c> read path across the collectible ALC; when
    /// <see langword="null"/> (the trivial path), <c>Traces</c> stays a
    /// <see cref="NullTraceCaptureAccessor"/>.
    /// </param>
    /// <param name="stepEvents">
    /// Optional stub step-event sink (issue #262).  When non-<see langword="null"/> (the
    /// closure path), the globals are built with <see cref="ScriptGlobalVariables.StepEvents"/>
    /// wired to it, so the CSX exercises the <c>StepEvents?.OnStepStarted/OnStepAttempt/
    /// OnStepCompleted</c> call sites across the collectible ALC; when <see langword="null"/>
    /// (the trivial path), <c>StepEvents</c> stays <see langword="null"/> (the default — no
    /// Null-object, matching production).
    /// </param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunIsolatedNoInlineAsync(
        CompiledScript compiled,
        IReadOnlyList<string>? collectibleProbingPaths,
        string label,
        CancellationToken ct,
        int iterIndex = 0,
        IWebhookCaptureAccessor? webhooks = null,
        ITraceCaptureAccessor? traces = null,
        IStepEventSink? stepEvents = null)
    {
        var vars = new Dictionary<string, object?>();

        // The closure probe reads Vars["__iter"] to gate the expensive native
        // librdkafka handle build to a bounded cadence (HandleBuildEveryN); the cheap
        // Avro/Polly touches run every iteration regardless.  Warm-up passes index 0,
        // which builds the handles once to amortise librdkafka's native init before the
        // baseline.  The trivial probe ignores this key, so setting it is harmless there.
        vars["__iter"] = (long)iterIndex;

        // Trivial path: the 1-arg ctor (Webhooks → NullWebhookCaptureAccessor, Traces →
        // NullTraceCaptureAccessor, StepEvents → null). Closure path: the FULL 6-arg ctor with
        // an empty services map, the throwing NullSecretAccessor (the probe never resolves a
        // secret), and the stub webhook / trace / step-event accessors — so the CSX reads
        // ScriptGlobalVariables.Webhooks / .Traces / .StepEvents across the collectible ALC and
        // a pin through any read path would surface (S07-E1 / Phase C / issue #262, §5).
        var globals = webhooks is null
            ? new ScriptGlobalVariables(vars)
            : new ScriptGlobalVariables(
                vars,
                new Dictionary<string, object>(StringComparer.Ordinal),
                NullSecretAccessor.Instance,
                webhooks,
                traces ?? NullTraceCaptureAccessor.Instance,
                stepEvents);
        await RoslynScriptCompiler.RunIsolatedAsync(
                compiled,
                globals,
                runLabel: label,
                collectibleProbingPaths: collectibleProbingPaths,
                cancellationToken: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Forces a quiescent GC state: multiple passes of Collect + WaitForFinalizers
    /// so that finalisers have run and any pending ALC unload callbacks have fired.
    /// </summary>
    private static void QuiescentGc()
    {
        for (var pass = 0; pass < QuiescentGcPasses; pass++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        }
    }

    /// <summary>
    /// Returns the number of collectible assemblies currently registered in
    /// <see cref="AppDomain.CurrentDomain"/>.
    /// </summary>
    private static int CountCollectibleAssemblies()
        => AppDomain.CurrentDomain.GetAssemblies().Count(a => a.IsCollectible);
}
