// S10-D-02 — Memory-leak gate provably covers all TEN Core providers' closure.
//
// The permanent M1 memory-leak gate (ClosureMemoryProbeTests + the 5,000-iteration
// harness in CI) exercises the transitive client closure of every Core provider inside
// a collectible AssemblyLoadContext, so a singleton pinner that anchors a reference
// across the collectible boundary is caught.  But that gate's value depends on the
// closure probe ACTUALLY touching each provider's canonical client: an 11th Core provider
// could be added (say, a `cache.redis` family) whose client never gets exercised, and
// the leak gate would stay green while silently NOT covering it.
//
// This guard makes that failure impossible to introduce silently.  It pins, as the
// source of truth, an explicit table mapping each of the nine Core providers to the
// canonical client / closure marker its leak coverage depends on, and then asserts:
//
//   1. The enumerated nine-provider table EQUALS the real Core-provider set — built by
//      reflecting the SAME ten anchor assemblies SchemaFreezeTests /
//      VsCodeShippedSchemaSyncTests / the CLI's ProviderRegistryFactory use, frozen
//      through StepKindRegistry.BuildAndFreeze.  So if an 11th Core provider is added (or
//      one is renamed/removed) without updating this table, THIS test fails — it can't
//      go stale against the real registry.
//
//   2. ClosureProbeScript.Source actually CONTAINS each provider's closure marker.  So
//      adding the 10th provider to the table (to satisfy #1) without also extending
//      ClosureProbeScript to exercise its client makes THIS test fail too.
//
// The net effect: adding a Core provider is a deliberate three-place edit — the new
// provider project, ClosureProbeScript.Source, and this table — and any partial edit is
// a red test.  This is the enumeration guard S10-D-02 asks for, in its preferred form
// (an explicit enumerated list cross-checked against the real provider set so it cannot
// drift), as a TEST-ONLY guard that touches no production logic.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Platform.Engine.Compilation.MemoryHarness;
using Platform.Sdk;
using Platform.Steps.DbAssert.Mongodb;
using Platform.Steps.DbAssert.Mysql;
using Platform.Steps.DbAssert.Postgres;
using Platform.Steps.DbAssert.SqlServer;
using Platform.Steps.HttpRest;
using Platform.Steps.MailExpect.Smtp;
using Platform.Steps.MqExpect.Kafka;
using Platform.Steps.MqPublish.Kafka;
using Platform.Steps.Script.Csharp;
using Platform.Steps.WebhookListen.Http;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S10-D-02: the enumeration guard that ties the memory-leak closure probe to the TEN
/// Core providers, cross-checked against the real frozen registry so it cannot go stale.
/// </summary>
public sealed class ClosureProbeCoverageGuardTests
{
    /// <summary>
    /// Describes one Core provider's leak-gate closure coverage: its
    /// <c>&lt;family&gt;.&lt;provider&gt;</c> step kind, a human-readable name for the
    /// canonical client whose static initialisers/handles its closure must exercise, and
    /// the literal MARKER string that must appear in <see cref="ClosureProbeScript.Source"/>
    /// to prove the probe genuinely touches that client.
    /// </summary>
    /// <param name="StepKind">
    /// The composite step-kind key in the form <c>&lt;family&gt;.&lt;provider&gt;</c>.
    /// </param>
    /// <param name="CanonicalClient">
    /// A human-readable description of the canonical client / closure the leak gate must
    /// touch for this provider (used only in failure messages).
    /// </param>
    /// <param name="ProbeMarker">
    /// A fully-qualified type reference (or accessor) that must be present verbatim in the
    /// closure probe CSX source, proving the probe exercises this provider's closure.
    /// </param>
    private sealed record CoreProviderCoverage(
        string StepKind,
        string CanonicalClient,
        string ProbeMarker);

    /// <summary>
    /// THE SOURCE OF TRUTH for which Core providers the memory-leak gate must cover.
    ///
    /// A new Core provider MUST be added here AND its canonical client exercised in
    /// <see cref="ClosureProbeScript.Source"/> (the <see cref="CoreProviderCoverage.ProbeMarker"/>
    /// must appear in that CSX body).  Both halves of this guard then enforce the pairing:
    /// <see cref="EnumeratedCoverage_EqualsRealCoreProviderSet"/> proves this list equals the
    /// real registry, and <see cref="ClosureProbe_Exercises_EachCoreProviderClient"/> proves
    /// each marker is present in the probe.
    /// </summary>
    /// <remarks>
    /// Marker choices, per provider:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>http.rest</c> → <c>System.Net.Http.HttpClient</c>: the provider issues REST
    ///     calls through <c>HttpClient</c>/<c>SocketsHttpHandler</c>; the probe creates one
    ///     to trigger the handler pool.
    ///   </description></item>
    ///   <item><description>
    ///     <c>db-assert.postgres</c> → <c>Npgsql</c>: the provider asserts against Postgres
    ///     via Npgsql; the probe builds an <c>NpgsqlConnectionStringBuilder</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>script.csharp</c> → the Polly-backed <c>RetryRunner</c> + <c>Vars</c> boundary:
    ///     script.csharp runs author CSX through the SAME compile-once/collectible-ALC path
    ///     the probe itself uses, and the probe drives <c>RetryRunner.PollAsync</c> across the
    ///     ALC boundary — the closure this provider's leak behaviour depends on.
    ///   </description></item>
    ///   <item><description>
    ///     <c>mq-publish.kafka</c> → <c>Confluent.Kafka.ProducerBuilder</c>: the publish
    ///     provider builds a producer (native librdkafka handle); the probe builds + disposes
    ///     a real producer.
    ///   </description></item>
    ///   <item><description>
    ///     <c>mq-expect.kafka</c> → <c>Confluent.Kafka.ConsumerBuilder</c>: the expect
    ///     provider builds a consumer (native librdkafka handle); the probe builds + disposes
    ///     a real consumer.
    ///   </description></item>
    ///   <item><description>
    ///     <c>webhook-listen.http</c> → <c>Webhooks.GetCaptured</c>: the listen provider
    ///     reads captured requests through <c>ScriptGlobalVariables.Webhooks</c>; the probe
    ///     walks that exact read path + record graph across the collectible ALC.
    ///   </description></item>
    ///   <item><description>
    ///     <c>db-assert.sqlserver</c> → <c>Microsoft.Data.SqlClient.SqlConnection</c>: the
    ///     provider asserts against SQL Server via <c>SqlCommand</c>/<c>SqlDataReader</c>;
    ///     the probe builds a real <c>SqlConnection</c> (exercises SNI init + connection-pool
    ///     static state) and disposes it in <c>finally</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>db-assert.mongodb</c> → <c>MongoDB.Driver.MongoClient</c>: the provider asserts
    ///     against MongoDB; the probe builds a real <c>MongoClient</c> (exercises the
    ///     connection-pool and SDAM background-thread static state) and disposes it in
    ///     <c>finally</c>.  MongoDB.Driver 3.x: <c>IMongoClient</c> extends <c>IDisposable</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <c>mail-expect.smtp</c> → <c>System.Net.Http.HttpClient</c> (BCL): the provider
    ///     queries the Mailpit HTTP API via <c>HttpClient</c>.  Its closure is subsumed by
    ///     the <c>http.rest</c> probe — both use only the BCL <c>HttpClient</c>/
    ///     <c>SocketsHttpHandler</c> pool, which the probe already exercises.  The shared
    ///     probe marker (<c>new System.Net.Http.HttpClient()</c>) satisfies both rows.
    ///   </description></item>
    /// </list>
    /// </remarks>
    private static readonly IReadOnlyList<CoreProviderCoverage> EnumeratedCoverage = new[]
    {
        new CoreProviderCoverage(
            StepKind: "http.rest",
            CanonicalClient: "System.Net.Http.HttpClient (REST client / SocketsHttpHandler pool)",
            ProbeMarker: "new System.Net.Http.HttpClient()"),
        new CoreProviderCoverage(
            StepKind: "db-assert.postgres",
            CanonicalClient: "Npgsql (Postgres client / connection pool)",
            ProbeMarker: "Npgsql.NpgsqlConnectionStringBuilder"),
        new CoreProviderCoverage(
            StepKind: "script.csharp",
            CanonicalClient: "Polly-backed RetryRunner across the collectible-ALC Vars boundary",
            ProbeMarker: "Platform.Engine.Abstractions.Retry.RetryRunner.PollAsync"),
        new CoreProviderCoverage(
            StepKind: "mq-publish.kafka",
            CanonicalClient: "Confluent.Kafka producer (native librdkafka handle)",
            ProbeMarker: "Confluent.Kafka.ProducerBuilder<string, string>"),
        new CoreProviderCoverage(
            StepKind: "mq-expect.kafka",
            CanonicalClient: "Confluent.Kafka consumer (native librdkafka handle)",
            ProbeMarker: "Confluent.Kafka.ConsumerBuilder<string, string>"),
        new CoreProviderCoverage(
            StepKind: "webhook-listen.http",
            CanonicalClient: "ScriptGlobalVariables.Webhooks captured-request read path",
            ProbeMarker: "Webhooks.GetCaptured("),
        new CoreProviderCoverage(
            StepKind: "db-assert.sqlserver",
            CanonicalClient: "Microsoft.Data.SqlClient.SqlConnection (SQL Server client / SNI handle)",
            ProbeMarker: "Microsoft.Data.SqlClient.SqlConnection"),
        new CoreProviderCoverage(
            StepKind: "db-assert.mongodb",
            CanonicalClient: "MongoDB.Driver.MongoClient (MongoDB client / connection pool)",
            ProbeMarker: "MongoDB.Driver.MongoClient(\"mongodb://localhost:27017\")"),
        new CoreProviderCoverage(
            StepKind: "mail-expect.smtp",
            CanonicalClient: "System.Net.Http.HttpClient (BCL — closure subsumed by http.rest probe; mail-expect.smtp queries the Mailpit HTTP API via HttpClient)",
            ProbeMarker: "new System.Net.Http.HttpClient()"),
        new CoreProviderCoverage(
            StepKind: "db-assert.mysql",
            CanonicalClient: "MySqlConnector.MySqlConnection (MySQL client / connection pool)",
            ProbeMarker: "MySqlConnector.MySqlConnection"),
    };

    /// <summary>
    /// The ten Core provider assemblies, anchored by one concrete provider type each —
    /// mirrors <see cref="SchemaFreezeTests"/>, <see cref="VsCodeShippedSchemaSyncTests"/>,
    /// and <c>Vouchfx.Cli.ProviderRegistryFactory.CoreProviderAssemblies</c>.  Listing them
    /// by anchor type makes a renamed/removed provider a COMPILE error here, and building the
    /// registry from them makes an ADDED Core provider a runtime failure in
    /// <see cref="EnumeratedCoverage_EqualsRealCoreProviderSet"/>.
    /// </summary>
    private static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,            // http.rest
        typeof(DbAssertPostgresProvider).Assembly,    // db-assert.postgres
        typeof(ScriptCsharpProvider).Assembly,        // script.csharp
        typeof(MqPublishKafkaProvider).Assembly,      // mq-publish.kafka
        typeof(MqExpectKafkaProvider).Assembly,       // mq-expect.kafka
        typeof(WebhookListenHttpProvider).Assembly,   // webhook-listen.http
        typeof(DbAssertSqlServerProvider).Assembly,   // db-assert.sqlserver
        typeof(DbAssertMongodbProvider).Assembly,     // db-assert.mongodb
        typeof(MailExpectSmtpProvider).Assembly,      // mail-expect.smtp
        typeof(DbAssertMysqlProvider).Assembly,       // db-assert.mysql
    };

    // -------------------------------------------------------------------------
    // Guard #1 — the enumerated coverage table EQUALS the real Core-provider set.
    // Adding/renaming/removing a Core provider without updating the table fails here.
    // -------------------------------------------------------------------------

    /// <summary>
    /// The set of step kinds in <see cref="EnumeratedCoverage"/> must be exactly the set
    /// of <c>&lt;family&gt;.&lt;provider&gt;</c> keys the real frozen registry reports for
    /// the eight Core provider assemblies.  This cross-check is what stops the table going
    /// stale: a 10th Core provider (or a rename) changes the registry's key set and this
    /// equality breaks until the table is deliberately updated.
    /// </summary>
    [Fact]
    public void EnumeratedCoverage_EqualsRealCoreProviderSet()
    {
        var registry = StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());

        var actualKinds = registry.All
            .Select(p => $"{p.Kind.Family}.{p.Kind.Provider}")
            .ToHashSet(StringComparer.Ordinal);

        var enumeratedKinds = EnumeratedCoverage
            .Select(c => c.StepKind)
            .ToHashSet(StringComparer.Ordinal);

        // Ten Core providers for the v1.x engine (6 original + db-assert.sqlserver + db-assert.mongodb + mail-expect.smtp + db-assert.mysql).
        Assert.Equal(10, actualKinds.Count);

        var missingFromTable = actualKinds.Except(enumeratedKinds, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        var staleInTable = enumeratedKinds.Except(actualKinds, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missingFromTable.Count == 0,
            "A Core provider exists in the registry but is NOT in the closure-coverage "
            + "table. A new Core provider MUST be added to "
            + $"{nameof(ClosureProbeCoverageGuardTests)}.{nameof(EnumeratedCoverage)} AND its "
            + "canonical client exercised in ClosureProbeScript.Source. Missing: "
            + string.Join(", ", missingFromTable));

        Assert.True(
            staleInTable.Count == 0,
            "The closure-coverage table lists a step kind that the real Core-provider "
            + "registry no longer reports (renamed/removed provider). Update "
            + $"{nameof(ClosureProbeCoverageGuardTests)}.{nameof(EnumeratedCoverage)}. Stale: "
            + string.Join(", ", staleInTable));

        // Belt-and-braces: the two sets are equal (catches any case the diffs above miss).
        Assert.Equal(actualKinds, enumeratedKinds);

        // The table must have no duplicate step kinds (ten distinct rows).
        Assert.Equal(10, enumeratedKinds.Count);
    }

    // -------------------------------------------------------------------------
    // Guard #2 — the closure probe ACTUALLY exercises each provider's client.
    // Adding a row to the table without extending ClosureProbeScript fails here.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Every Core provider in the coverage table must have its canonical-client marker
    /// present verbatim in <see cref="ClosureProbeScript.Source"/>.  If a row is added to
    /// the table (to satisfy guard #1 when a provider is added) without extending the probe
    /// CSX to touch that client, this fails — closing the loop so the leak gate genuinely
    /// covers all six providers' closure.
    /// </summary>
    [Theory]
    [MemberData(nameof(CoverageRows))]
    public void ClosureProbe_Exercises_EachCoreProviderClient(
        string stepKind,
        string canonicalClient,
        string probeMarker)
    {
        Assert.True(
            ClosureProbeScript.Source.Contains(probeMarker, StringComparison.Ordinal),
            $"The memory-leak closure probe (ClosureProbeScript.Source) does NOT exercise "
            + $"the canonical client for Core provider '{stepKind}' ({canonicalClient}). "
            + $"Expected to find the marker '{probeMarker}' in the probe CSX. A new Core "
            + "provider MUST be added to ClosureProbeScript.Source AND to "
            + $"{nameof(ClosureProbeCoverageGuardTests)}.{nameof(EnumeratedCoverage)}.");
    }

    /// <summary>
    /// xUnit member-data feed for <see cref="ClosureProbe_Exercises_EachCoreProviderClient"/>:
    /// one row per Core provider in <see cref="EnumeratedCoverage"/>.
    /// </summary>
    public static IEnumerable<object[]> CoverageRows() =>
        EnumeratedCoverage.Select(c => new object[]
        {
            c.StepKind,
            c.CanonicalClient,
            c.ProbeMarker,
        });
}
