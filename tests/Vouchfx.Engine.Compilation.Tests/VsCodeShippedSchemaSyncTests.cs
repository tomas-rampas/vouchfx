// S09-C-01 (the load-bearing .NET sync gate for the VSCode extension's shipped schema).
//
// The VSCode extension ships a LOCAL copy of the composed v1 JSON Schema at
// tools/vscode-vouchfx/src/schema/composed-schema.v1.json and binds it to
// *.e2e.yaml for editor autocomplete and validation.  This gate proves that
// shipped copy is byte-for-byte identical to what the engine's SchemaComposer
// actually emits — so the editor can NEVER advertise a construct the compiler
// would reject (DSL §9 core invariant).
//
// Why this gate exists:
//   • The editor's view of the language MUST equal the compiler's view of the
//     language.  If the shipped schema drifts from SchemaComposer, authors get
//     green squiggles for steps the engine will reject (or red squiggles for
//     steps it accepts) — silently eroding trust in the extension.
//   • This is the DLL-side counterpart of the extension's own packaging check:
//     the engine recomposes the schema from the FULL thirteen-Core-provider registry
//     (exactly as SchemaFreezeTests does) and diffs it against the source-tree
//     file the extension ships, catching drift the moment a provider's schema
//     fragment changes without the shipped copy being regenerated.
//
// The shipped copy is read from the SOURCE tree (not the test output dir — it is
// deliberately NOT copied to output) so that a developer who edits the extension's
// schema is caught by this gate, not masked by a stale build artifact.
using System;
using System.IO;
using System.Reflection;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.CacheAssert.Elasticsearch;
using Vouchfx.Steps.CacheAssert.Redis;
using Vouchfx.Steps.DbAssert.Dynamodb;
using Vouchfx.Steps.DbAssert.Mongodb;
using Vouchfx.Steps.DbAssert.Mysql;
using Vouchfx.Steps.DbAssert.Postgres;
using Vouchfx.Steps.DbAssert.SqlServer;
using Vouchfx.Steps.Http.Soap;
using Vouchfx.Steps.HttpRest;
using Vouchfx.Steps.MailExpect.Smtp;
using Vouchfx.Steps.MetricsAssert.Prometheus;
using Vouchfx.Steps.MqExpect.AzureServiceBus;
using Vouchfx.Steps.MqExpect.Kafka;
using Vouchfx.Steps.MqExpect.Nats;
using Vouchfx.Steps.MqExpect.Rabbitmq;
using Vouchfx.Steps.MqExpect.Redis;
using Vouchfx.Steps.MqPublish.AzureServiceBus;
using Vouchfx.Steps.MqPublish.Kafka;
using Vouchfx.Steps.MqPublish.Nats;
using Vouchfx.Steps.MqPublish.Rabbitmq;
using Vouchfx.Steps.MqPublish.Redis;
using Vouchfx.Steps.Script.Csharp;
using Vouchfx.Steps.StorageAssert.S3;
using Vouchfx.Steps.TraceExpect.Otlp;
using Vouchfx.Steps.WebhookListen.Http;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// S09-C-01: the VSCode-extension shipped-schema sync gate.  Proves the schema the
/// extension ships for editor autocomplete/validation stays byte-for-byte identical
/// to what <see cref="SchemaComposer.ComposeSchemaJson"/> emits, so the editor can
/// never diverge from the compiler.
/// </summary>
public sealed class VsCodeShippedSchemaSyncTests
{
    /// <summary>
    /// Set <c>VOUCHFX_REGEN_SCHEMA=1</c> to make the gate REWRITE the shipped VSCode
    /// schema from the freshly-composed schema instead of asserting against it.
    /// </summary>
    private const string RegenEnvVar = "VOUCHFX_REGEN_SCHEMA";

    /// <summary>
    /// The Core provider assemblies that compose the v1 schema, anchored by
    /// one concrete provider type per assembly (mirrors
    /// <c>SchemaFreezeTests.CoreProviderAssemblies</c> and
    /// <c>Vouchfx.Cli.ProviderRegistryFactory.CoreProviderAssemblies</c>).  Listing
    /// them by anchor type makes a renamed/removed provider a compile error.
    /// </summary>
    private static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,            // http.rest
        typeof(DbAssertPostgresProvider).Assembly,    // db-assert.postgres
        typeof(DbAssertSqlServerProvider).Assembly,   // db-assert.sqlserver
        typeof(DbAssertMongodbProvider).Assembly,     // db-assert.mongodb
        typeof(DbAssertMysqlProvider).Assembly,       // db-assert.mysql
        typeof(ScriptCsharpProvider).Assembly,        // script.csharp
        typeof(MqPublishKafkaProvider).Assembly,      // mq-publish.kafka
        typeof(MqExpectKafkaProvider).Assembly,       // mq-expect.kafka
        typeof(WebhookListenHttpProvider).Assembly,   // webhook-listen.http
        typeof(MailExpectSmtpProvider).Assembly,      // mail-expect.smtp
        typeof(CacheAssertRedisProvider).Assembly,    // cache-assert.redis
        typeof(MqPublishRabbitmqProvider).Assembly,   // mq-publish.rabbitmq
        typeof(MqExpectRabbitmqProvider).Assembly,    // mq-expect.rabbitmq
        typeof(MqPublishNatsProvider).Assembly,       // mq-publish.nats
        typeof(MqExpectNatsProvider).Assembly,        // mq-expect.nats
        typeof(CacheAssertElasticsearchProvider).Assembly, // cache-assert.elasticsearch
        typeof(MqPublishAzureServiceBusProvider).Assembly, // mq-publish.azureservicebus
        typeof(MqExpectAzureServiceBusProvider).Assembly,  // mq-expect.azureservicebus
        typeof(MqPublishRedisProvider).Assembly,      // mq-publish.redis
        typeof(MqExpectRedisProvider).Assembly,       // mq-expect.redis
        typeof(MetricsAssertPrometheusProvider).Assembly,  // metrics-assert.prometheus
        typeof(DbAssertDynamodbProvider).Assembly,    // db-assert.dynamodb
        typeof(StorageAssertS3Provider).Assembly,     // storage-assert.s3
        typeof(TraceExpectOtlpProvider).Assembly,     // trace-expect.otlp
        typeof(HttpSoapProvider).Assembly,             // http.soap
    };

    /// <summary>
    /// The schema the VSCode extension ships must be byte-for-byte identical to the
    /// schema the engine's <see cref="SchemaComposer"/> composes from the full
    /// Core-provider registry.  If this fails, the extension's editor view of the
    /// language has drifted from the compiler's view — the core DSL §9 invariant.
    /// </summary>
    [Fact]
    public void ShippedVsCodeSchema_MatchesComposer_ByteForByte()
    {
        var registry = StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());

        var expected = SchemaComposer.ComposeSchemaJson(registry);

        // Regeneration mode: rewrite the shipped VSCode schema from the freshly-composed
        // schema and pass.  Used by a developer after a legitimate (additive) schema change.
        if (IsRegenRequested())
        {
            var repoRoot = FindRepoRoot();
            var vscPath = Path.Combine(
                repoRoot, "tools", "vscode-vouchfx", "src", "schema", "composed-schema.v1.json");
            File.WriteAllText(vscPath, expected);
            return;
        }

        var shipped = ReadShippedVsCodeSchema();

        // Normalise line endings AND any trailing final newline on both sides so a
        // CRLF/LF checkout difference (Git autocrlf, editor settings) or an editor's
        // insert_final_newline rewrite never produces a spurious drift; the sync
        // contract is about schema content, not byte-level newline style.
        var expectedNormalised = Normalise(expected);
        var shippedNormalised = Normalise(shipped);

        Assert.True(
            string.Equals(expectedNormalised, shippedNormalised, StringComparison.Ordinal),
            "The shipped VSCode schema has DRIFTED from SchemaComposer.ComposeSchemaJson. "
            + "The editor must never diverge from the compiler (DSL §9): regenerate with "
            + $"VOUCHFX_REGEN_SCHEMA=1 and re-review."
            + Environment.NewLine
            + FirstDifference(expectedNormalised, shippedNormalised));
    }

    /// <summary>
    /// The shipped schema must self-identify as <c>v1</c> via the in-schema version
    /// marker, so the extension is unambiguously binding the v1 contract (mirrors
    /// <c>SchemaFreezeTests.ComposedV1Schema_SelfIdentifiesAsV1</c>).
    /// </summary>
    [Fact]
    public void ShippedVsCodeSchema_SelfIdentifiesAsV1()
    {
        var shipped = ReadShippedVsCodeSchema();

        Assert.Contains("\"x-vouchfx-schema-version\"", shipped, StringComparison.Ordinal);
        Assert.Contains("\"v1\"", shipped, StringComparison.Ordinal);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // Collapse CRLF/CR → LF and drop any trailing final newline(s): the sync
    // contract compares schema CONTENT, immune to line-ending style and to an
    // editor's insert_final_newline rewrite of the shipped file.
    private static string Normalise(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

    private static bool IsRegenRequested()
    {
        var value = Environment.GetEnvironmentVariable(RegenEnvVar);
        return !string.IsNullOrEmpty(value)
            && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the schema the VSCode extension ships, from the SOURCE tree (not the
    /// test output directory — it is deliberately not copied to output).  Resolves
    /// the repo root by walking up from <see cref="AppContext.BaseDirectory"/> until
    /// the directory containing <c>vouchfx.sln</c> is found, then reads
    /// <c>tools/vscode-vouchfx/src/schema/composed-schema.v1.json</c>.  Reading the
    /// source-tree file (rather than a build copy) is what makes this gate catch a
    /// developer who edits the extension schema.
    /// </summary>
    private static string ReadShippedVsCodeSchema()
    {
        var repoRoot = FindRepoRoot();
        var path = Path.Combine(repoRoot, "tools", "vscode-vouchfx", "src", "schema", "composed-schema.v1.json");

        Assert.True(
            File.Exists(path),
            $"The VSCode extension's shipped schema was not found at '{path}'.  It must "
            + "exist and stay in sync with SchemaComposer so the editor can never "
            + "advertise a construct the compiler would reject (DSL §9).");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the directory
    /// containing <c>vouchfx.sln</c> — the repo root.  This locates the source tree
    /// regardless of the build output's nesting depth.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').  This gate must read "
            + "the VSCode extension's shipped schema from the source tree.");
    }

    /// <summary>
    /// Produces a short, human-readable description of the first line that differs
    /// between the composed (expected) and the shipped schema, to make a drift
    /// failure diagnosable from the test output alone.
    /// </summary>
    private static string FirstDifference(string expected, string shipped)
    {
        var expectedLines = expected.Split('\n');
        var shippedLines = shipped.Split('\n');
        var max = Math.Max(expectedLines.Length, shippedLines.Length);

        for (var i = 0; i < max; i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "<EOF>";
            var s = i < shippedLines.Length ? shippedLines[i] : "<EOF>";
            if (!string.Equals(e, s, StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:"
                    + $"{Environment.NewLine}  composer: {e}"
                    + $"{Environment.NewLine}  shipped:  {s}";
            }
        }

        return "(no line-level difference detected; check trailing whitespace or length)";
    }
}
