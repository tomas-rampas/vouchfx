// Schema-corpus gate — Task 2: the accepted-corpus half.
//
// SchemaFreezeTests is golden-EQUALITY only: it proves the composed schema has
// not drifted from a byte-for-byte snapshot, never whether a drift NARROWED what
// a real author's document validates against. Two schema regressions shipped in
// one week (29f910b, 66aef95) sailed past that gate, because neither change
// touched the golden's CONTENT in a way the freeze test could distinguish from
// "fine" — the documents the narrowing broke were simply never exercised.
//
// This class is the missing half: a corpus of real .e2e.yaml bodies, each
// validated through DocumentValidator.Validate — the composed path (root schema
// + every registered provider's fragment) an author's suite actually hits —
// against the FULL 25-provider Core registry (mirrors SchemaFreezeTests'
// CoreProviderAssemblies). Deliberately NOT YamlSchemaValidator: grep proves
// nothing under src/ calls it in the real pipeline — it validates the root
// schema alone, without provider fragments, so a gate built on it would prove
// nothing about what an author's suite actually hits.
//
// Three corpora, three failure modes this class exists to catch:
//   • Corpus/Accepted/regression-*   — the ten documents 29f910b and 66aef95
//     actually broke (seeded from their commit messages, not summarised).
//   • Corpus/Accepted/surface-*      — adversarial-but-legal documents across
//     surfaces (environment.services, all thirteen dependency kinds, capture,
//     timeout, and — since the T2a scalar-coercion widening tranche
//     (feat/fragment-completeness) — the scalar/map fields the ENGINE always
//     accepted via raw-scalar-text binding but the schema used to reject:
//     headers/parameters/payload/expect.status and their siblings) that a
//     later narrowing is likely to touch next.
//   • tests/Vouchfx.Engine.Planning.Tests/Fixtures/**/*.e2e.yaml — the brief
//     said "~20"; the actual count at time of writing is 41 real suite
//     fixtures, none previously validated against the language schema.
//
// See SchemaRejectedCorpusTests.cs for the sibling gate over documents that
// must be rejected at a pinned location + keyword.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Schema-corpus gate: proves real <c>.e2e.yaml</c> documents keep validating
/// through <see cref="DocumentValidator"/> against the full Core-provider
/// registry — the safety net that lets the v1 schema be tightened before GA
/// without silently breaking valid documents.
/// </summary>
public sealed class SchemaAcceptedCorpusTests
{
    /// <summary>
    /// The Core provider assemblies that compose the v1 schema — byte-for-byte
    /// the same list as <c>SchemaFreezeTests.CoreProviderAssemblies</c> and
    /// <c>Vouchfx.Cli.ProviderRegistryFactory.CoreProviderAssemblies</c>. Kept as
    /// its own copy (rather than shared) because that is the established
    /// convention in this test project (see also
    /// <c>VsCodeShippedSchemaSyncTests.CoreProviderAssemblies</c>) — anchoring by
    /// concrete type per assembly makes a renamed/removed provider a compile
    /// error here, not a silent registry gap.
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
        typeof(MqPublishNatsProvider).Assembly,        // mq-publish.nats
        typeof(MqExpectNatsProvider).Assembly,         // mq-expect.nats
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

    internal static readonly StepKindRegistry Registry =
        StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());

    // ── Corpus/Accepted discovery ────────────────────────────────────────────
    //
    // Corpus/**/*.e2e.yaml ships as a Content item (CopyToOutputDirectory) —
    // see the .csproj — so it is read from AppContext.BaseDirectory, exactly
    // like the Golden/ artifacts, rather than requiring repo-root resolution.

    private static string AcceptedCorpusDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Corpus", "Accepted");

    public static IEnumerable<object[]> AcceptedFiles() =>
        DiscoverYamlFiles(AcceptedCorpusDirectory)
            .Select(p => new object[] { p });

    /// <summary>
    /// <see cref="AcceptedFiles"/> must discover AT LEAST a known-safe floor of
    /// files — a bare "at least one" would still pass if a glob/path
    /// regression silently dropped all but a single fixture (every OTHER
    /// downstream <c>[Theory]</c> case would then just vanish instead of
    /// failing loudly, mirroring
    /// <c>ExamplesCompileTests.ExampleFiles_DiscoversAtLeastOneFile</c>'s own
    /// concern, but for a PARTIAL loss rather than a total one).
    /// </summary>
    /// <remarks>
    /// Raised from 15 to 34 (critic NIT-8, 2026-08-12). 15 was set when the
    /// corpus was young and never tightened as fixtures accumulated: the
    /// committed count is <b>36</b> — counted directly off the tree on
    /// 2026-08-12 (every <c>*.e2e.yaml</c> under <c>Corpus/Accepted</c>,
    /// recursively, the same enumeration <see cref="AcceptedFiles"/> itself
    /// performs), not carried forward from the previous revision of this
    /// comment — so the old floor would have tolerated losing twenty-one
    /// fixtures in silence, which is the whole failure this gate exists to
    /// prevent. Its sibling <c>SchemaRejectedCorpusTests</c> was retightened
    /// the same way (40 to 55 against a counted 57) and this one was left
    /// behind. 34 is sensibly below-but-near the true count: tight enough that
    /// dropping several fixtures fails loudly, with enough headroom that
    /// routine reorganisation does not require raising it every time. Safe to
    /// raise as fixtures accumulate; recount rather than increment when doing so.
    /// </remarks>
    [Fact]
    public void AcceptedFiles_DiscoversAtLeastOneFile()
    {
        const int minAcceptedFiles = 34;

        var acceptedCount = AcceptedFiles().Count();

        Assert.True(acceptedCount >= minAcceptedFiles,
            $"Expected at least {minAcceptedFiles} accepted regression/surface files under " +
            $"'{AcceptedCorpusDirectory}', found {acceptedCount}.");
    }

    /// <summary>
    /// Every document under <c>Corpus/Accepted</c> (excluding the
    /// scalar-coercion tranche, see <see cref="ScalarCoercionCase_WillBeAcceptedInFutureTranche"/>)
    /// must validate through <see cref="DocumentValidator"/> against the full
    /// Core registry. On failure, the assertion message names the file and
    /// every error's location and message — the whole point of this gate is
    /// that a future narrowing names exactly which construct it broke.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptedFiles))]
    public void AcceptedDocument_IsValid(string path)
    {
        var yaml = File.ReadAllText(path);
        var result = DocumentValidator.Validate(yaml, Registry);

        Assert.True(result.IsValid, DescribeUnexpectedInvalid(path, result));
    }

    // ── Scalar-coercion widening (T2a) — RETIRED, tranche landed ────────────────
    //
    // ScalarCoercionFiles()/ScalarCoercionCase_WillBeAcceptedInFutureTranche used
    // to pin the shapes the ENGINE accepted but the schema rejected (headers/
    // parameters additionalProperties, expect.status, payload — all read back
    // via raw scalar text regardless of the YAML value's actual type). The T2a
    // widening tranche (feat/fragment-completeness) landed: every Core provider's
    // string-valued map now accepts ["string","integer","number","boolean"], every
    // int.TryParse-read integer field now accepts ["integer","string"], and the
    // named VALUE scalars (payload, key, expect.value, payloadContains, …) widened
    // the same way — see each provider's SchemaFragment and the CHANGELOG entry for
    // the full field-by-field list. The four fixtures this theory used to pin now
    // live in Corpus/Accepted as plain surface-* fixtures (surface-expect-status-string,
    // surface-headers-numeric-value, surface-parameters-numeric-value,
    // surface-payload-numeric), exercised by AcceptedDocument_IsValid like every
    // other accepted fixture — this theory (and its now-empty discovery method) is
    // deleted rather than left as a permanent no-op.

    // ── Planning.Tests fixtures: real environment: blocks, never schema-checked ──
    //
    // tests/Vouchfx.Engine.Planning.Tests/Fixtures/**/*.e2e.yaml carries real
    // suite bodies (environment: blocks and all) used to exercise the PLANNER
    // (coverage, vocabulary, determinism, ingest, history). Nothing validated
    // them against the language schema before this gate.
    //
    // MAJOR FINDING (contradicts part of the brief's framing — reported here
    // rather than worked around silently): the brief's premise was that these
    // fixtures "carry real environment: blocks" that "nothing validates
    // today", implying the environment: shapes were the risk. Running the
    // gate against the full, unfiltered fixture set (41 files) shows the
    // environment: blocks are NOT the problem — they validate cleanly
    // everywhere (environment.services/dependencies is barely constrained by
    // the schema, as established by the accepted-corpus surface fixtures
    // above). The actual, and much larger, finding is that 39 of the 41
    // fixtures fail schema validation because their STEP bodies are
    // deliberately minimal: the planner inspects only a step's 'id', 'type',
    // 'target' and 'capture' (coverage/vocabulary graph shape), so these
    // fixtures were authored WITHOUT the provider-required fields
    // (http.rest's method/path, db-assert.postgres's query/expect,
    // mq-publish.kafka's topic/payload, cache-assert.redis's
    // key/operation/expect, …) a real compilable document needs. Only the
    // three fixtures that happen to use script.csharp — the one Core
    // provider whose schema requires nothing beyond 'code' or 'file' — pass
    // as-is. None of the 39 are edited here to make them pass, and none are
    // silently dropped from discovery: every one is excluded below by exact
    // relative path, categorised by its real root cause, and paired with the
    // precise DocumentValidator finding that justifies the exclusion
    // (obtained by actually running the validator with zero exclusions —
    // never guessed).
    //
    // Four categories, in order of how many fixtures they cover:
    //   (A) Deliberately minimal step bodies (31 fixtures) — the systemic
    //       case above: real steps present, but shaped for the planner's own
    //       purposes, never meant to be schema-complete documents.
    //   (B) Deliberately zero-step ('steps: []', 3 fixtures) — a suite that
    //       exists only to declare an unreferenced/uncovered dependency or
    //       service for a vocabulary/coverage-gap test. Violates the root
    //       schema's 'steps' minItems: 1.
    //   (C) Deliberately malformed (4 fixtures) — exercises the planner's own
    //       malformed-suite handling: a step missing 'id', or (in the
    //       secret-safety fixture) a 'type' replaced by an ~800-character
    //       garbage token planted to prove SuiteSetLoader's
    //       MaxParseErrorMessageChars bound truncates it out of a reported
    //       error. Fails 'required'/'pattern', never a construct this gate's
    //       narrowing-detection purpose cares about.
    //   (D) Planner-only cross-cutting field on a provider that doesn't
    //       declare it (1 fixture) — added by the typo-closing change
    //       (branch feat/close-step-surface, $defs/step's
    //       'unevaluatedProperties: false'). The planner treats 'target' as a
    //       generic dependency-reference convention available on ANY step
    //       type for graph-shape purposes (REQ-004/REQ-007); script.csharp's
    //       own schema never declared 'target' as one of its fields (only
    //       'code'/'file'). Before this change that was silently tolerated
    //       (additionalProperties: true); now it is correctly rejected as an
    //       unevaluated property — a genuine, and genuinely interesting,
    //       tension between the planner's cross-cutting convention and the
    //       schema's per-provider vocabulary, surfaced here rather than
    //       resolved unilaterally (flagged for maintainer attention).

    private static string RepoRoot => FindRepoRoot();

    private static string PlanningFixturesDirectory =>
        Path.Combine(RepoRoot, "tests", "Vouchfx.Engine.Planning.Tests", "Fixtures");

    /// <summary>
    /// Planning.Tests fixtures excluded from <see cref="PlanningFixtureFiles"/>,
    /// keyed by their path relative to <see cref="PlanningFixturesDirectory"/>
    /// (forward-slash separated). Each value is the exact
    /// <see cref="DocumentValidator"/> finding that justifies the exclusion —
    /// obtained by actually running the validator against the fixture, not
    /// guessed — together with why the fixture is deliberately shaped that way.
    /// </summary>
    /// <remarks>
    /// Every one of these is a genuine schema-validation failure (per the
    /// brief: "a fixture that fails schema validation is a genuine finding").
    /// None are edited to make them pass, and none are silently dropped from
    /// discovery without a reason recorded here. Only 2 of the 41 discovered
    /// fixtures are NOT excluded (both use script.csharp exclusively, and
    /// neither carries a planner-only 'target' field — see the section header
    /// above for why) and remain actively gated by
    /// <see cref="PlanningFixture_IsValid"/>.
    /// </remarks>
    private static readonly Dictionary<string, string> ExcludedPlanningFixtures =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── (A) Deliberately minimal step bodies — planner tests coverage/
            //        vocabulary/determinism/history graph shape only; steps
            //        omit the provider-required fields a compilable document
            //        needs. This is the dominant category (31 of 39).
            ["acceptance/determinism/two-collisions/collision-alpha-1.e2e.yaml"] =
                "Minimal http.rest step body (planner determinism fixture — only 'id'/'type'/'target' matter). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["acceptance/determinism/two-collisions/collision-alpha-2.e2e.yaml"] =
                "Minimal http.rest step body (planner determinism fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["acceptance/determinism/two-collisions/collision-beta-1.e2e.yaml"] =
                "Minimal http.rest step body (planner determinism fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["acceptance/determinism/two-collisions/collision-beta-2.e2e.yaml"] =
                "Minimal http.rest step body (planner determinism fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["acceptance/precedence/suite.e2e.yaml"] =
                "Minimal mq-publish.kafka step body (planner precedence fixture). " +
                "at /steps/0: (line 9) [required] Required properties [\"topic\",\"payload\"] are not present",

            ["acceptance/secret-safety/suite.e2e.yaml"] =
                "Minimal http.rest step body (planner secret-safety fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/distinct-runid-same-stepid/suite-a.e2e.yaml"] =
                "Minimal http.rest step body (planner coverage fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/distinct-runid-same-stepid/suite-b.e2e.yaml"] =
                "Minimal http.rest step body (planner coverage fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/fully-exercised/suite.e2e.yaml"] =
                "Minimal http.rest + db-assert.postgres step bodies (planner coverage fixture). " +
                "at /steps/0: (line 13) [required] Required properties [\"method\",\"path\"] are not present; " +
                "at /steps/1: (line 16) [required] Required properties [\"query\",\"expect\"] are not present",

            ["coverage/interleaved-parallel-runs/suite-a.e2e.yaml"] =
                "Minimal http.rest step body (planner coverage fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/interleaved-parallel-runs/suite-b.e2e.yaml"] =
                "Minimal http.rest step body (planner coverage fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/listener-name-collision/suite.e2e.yaml"] =
                "Minimal mq-publish.kafka step body (planner coverage fixture). " +
                "at /steps/0: (line 13) [required] Required properties [\"topic\",\"payload\"] are not present",

            ["coverage/mixed-gaps/checkout.e2e.yaml"] =
                "Minimal http.rest/db-assert.postgres/mq-publish.kafka step bodies (planner coverage fixture). " +
                "at /steps/0: (line 15) [required] Required properties [\"method\",\"path\"] are not present; " +
                "at /steps/1: (line 18) [required] Required properties [\"query\",\"expect\"] are not present; " +
                "at /steps/2: (line 21) [required] Required properties [\"topic\",\"payload\"] are not present; " +
                "at /steps/3: (line 24) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/mixed-gaps/shipping.e2e.yaml"] =
                "Minimal http.rest step body (planner coverage fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/receiver-name-collision/suite.e2e.yaml"] =
                "Minimal mq-publish.kafka step body (planner coverage fixture). " +
                "at /steps/0: (line 13) [required] Required properties [\"topic\",\"payload\"] are not present",

            ["coverage/renamed-file-ambiguity/current.e2e.yaml"] =
                "Minimal http.rest step body (planner coverage fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/scenario-id-collision/suite-one.e2e.yaml"] =
                "Minimal http.rest step body (planner coverage fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/scenario-id-collision/suite-two.e2e.yaml"] =
                "Minimal http.rest step body (planner coverage fixture). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["coverage/unresolvable-targets/suite.e2e.yaml"] =
                "Minimal http.rest + cache-assert.redis step bodies (planner coverage fixture). " +
                "at /steps/0: (line 13) [required] Required properties [\"method\",\"path\"] are not present; " +
                "at /steps/1: (line 16) [required] Required properties [\"key\",\"operation\",\"expect\"] are not present",

            ["coverage/zero-step-dependency/suite.e2e.yaml"] =
                "Minimal http.rest + mq-publish.kafka step bodies (planner coverage fixture — misleadingly " +
                "named: it has steps, just none targeting the fixture's titular zero-coverage dependency). " +
                "at /steps/0: (line 15) [required] Required properties [\"method\",\"path\"] are not present; " +
                "at /steps/1: (line 18) [required] Required properties [\"topic\",\"payload\"] are not present",

            ["history/default-thresholds/history-health.e2e.yaml"] =
                "Minimal http.rest step bodies ×4 (planner history fixture). " +
                "at /steps/0: (line 11) [required] Required properties [\"method\",\"path\"] are not present; " +
                "at /steps/1: (line 14) [required] Required properties [\"method\",\"path\"] are not present; " +
                "at /steps/2: (line 17) [required] Required properties [\"method\",\"path\"] are not present; " +
                "at /steps/3: (line 20) [required] Required properties [\"method\",\"path\"] are not present",

            ["history/phantom-step/phantom-step.e2e.yaml"] =
                "Minimal http.rest step body (planner history fixture). " +
                "at /steps/0: (line 10) [required] Required properties [\"method\",\"path\"] are not present",

            ["history/retry-noise/retry-noise.e2e.yaml"] =
                "Minimal http.rest step body (planner history fixture). " +
                "at /steps/0: (line 10) [required] Required properties [\"method\",\"path\"] are not present",

            ["ingest/basic-suites/checkout.e2e.yaml"] =
                "Minimal http.rest + db-assert.postgres step bodies (planner ingest fixture). " +
                "at /steps/0: (line 17) [required] Required properties [\"method\",\"path\"] are not present; " +
                "at /steps/1: (line 20) [required] Required properties [\"query\",\"expect\"] are not present",

            ["ingest/basic-suites/refund.e2e.yaml"] =
                "Minimal mq-publish.kafka step body (planner ingest fixture). " +
                "at /steps/0: (line 2) [required] Required properties [\"topic\",\"payload\"] are not present",

            ["ingest/one-malformed/good.e2e.yaml"] =
                "Minimal http.rest step body (planner ingest fixture — 'good' relative to its malformed sibling, " +
                "not relative to the schema). " +
                "at /steps/0: (line 5) [required] Required properties [\"method\",\"path\"] are not present",

            ["vocabulary/fully-covered/suite.e2e.yaml"] =
                "Minimal http.rest + db-assert.postgres step bodies (planner vocabulary fixture). " +
                "at /steps/0: (line 10) [required] Required properties [\"method\",\"path\"] are not present; " +
                "at /steps/1: (line 13) [required] Required properties [\"query\",\"expect\"] are not present",

            ["vocabulary/kafka-published-only/suite.e2e.yaml"] =
                "Minimal mq-publish.kafka step body (planner vocabulary fixture). " +
                "at /steps/0: (line 7) [required] Required properties [\"topic\",\"payload\"] are not present",

            ["vocabulary/redis-partial-coverage/suite.e2e.yaml"] =
                "Minimal cache-assert.redis step body (planner vocabulary fixture). " +
                "at /steps/0: (line 7) [required] Required properties [\"key\",\"operation\",\"expect\"] are not present",

            ["vocabulary/service-no-http/suite.e2e.yaml"] =
                "script.csharp step missing BOTH oneOf branches — the fixture declares neither 'code' nor " +
                "'file' at all (planner vocabulary fixture; the step exists only to occupy the suite, its " +
                "body is irrelevant to the vocabulary check). " +
                "at /steps/0: (line 7) [required] Required properties [\"code\"] are not present; " +
                "at /steps/0: (line 7) [required] Required properties [\"file\"] are not present",

            ["vocabulary/two-suites-same-dependency-name/suite-b-covered.e2e.yaml"] =
                "Minimal cache-assert.redis + http.rest step bodies (planner vocabulary fixture). " +
                "at /steps/0: (line 21) [required] Required properties [\"key\",\"operation\",\"expect\"] are not present; " +
                "at /steps/1: (line 24) [required] Required properties [\"method\",\"path\"] are not present",

            // ── (B) Deliberately zero-step — a suite that exists only to
            //        declare an unreferenced dependency/service for a
            //        vocabulary or coverage-gap test. Violates 'steps'
            //        minItems: 1 (3 of 39).
            ["acceptance/loop-closure-dependency/suite.e2e.yaml"] =
                "Deliberately zero-step: exercises a suite whose only dependency has no steps targeting it. " +
                "at /steps: (line 6) [minItems] Value should have at least 1 items",

            ["vocabulary/mailpit-no-steps/suite.e2e.yaml"] =
                "Deliberately zero-step: VocabularyTests exercises a declared-but-unexercised mailpit dependency. " +
                "at /steps: (line 6) [minItems] Value should have at least 1 items",

            ["vocabulary/unknown-kind/suite.e2e.yaml"] =
                "Deliberately zero-step: VocabularyTests exercises an unreferenced dependency of an unknown kind. " +
                "at /steps: (line 6) [minItems] Value should have at least 1 items",

            // ── (C) Deliberately malformed — exercises the planner's own
            //        malformed-suite handling (4 of 39).
            ["ingest/all-malformed/bad-one.e2e.yaml"] =
                "Deliberately malformed for PlanIngestTests' malformed-suite handling: the one step omits " +
                "'id' AND the http.rest-required fields. " +
                "at /steps/0: (line 5) [required] Required properties [\"id\"] are not present; " +
                "at /steps/0: (line 5) [required] Required properties [\"target\",\"method\",\"path\"] are not present",

            ["ingest/all-malformed/bad-two.e2e.yaml"] =
                "Deliberately malformed for PlanIngestTests' malformed-suite handling: the one step omits " +
                "'id' AND the db-assert.postgres-required fields. " +
                "at /steps/0: (line 5) [required] Required properties [\"id\"] are not present; " +
                "at /steps/0: (line 5) [required] Required properties [\"target\",\"query\",\"expect\"] are not present",

            ["ingest/one-malformed/bad.e2e.yaml"] =
                "Deliberately malformed for PlanIngestTests' malformed-suite handling: the one step omits " +
                "'id' AND the http.rest-required fields. " +
                "at /steps/0: (line 5) [required] Required properties [\"id\"] are not present; " +
                "at /steps/0: (line 5) [required] Required properties [\"target\",\"method\",\"path\"] are not present",

            ["acceptance/secret-safety/malformed-with-planted-secret.e2e.yaml"] =
                "Deliberately malformed for PlanSafetyTests: the step 'type' is an ~800-character garbage " +
                "token (planted past SuiteSetLoader's MaxParseErrorMessageChars truncation bound) so the " +
                "redaction test can prove the bound truncates it away. Fails the root schema's " +
                "'^[a-z0-9-]+\\.[a-z0-9-]+$' type pattern. " +
                "at /steps/0/type: (line 16) [pattern] The string value is not a match for the indicated regular expression",

            // ── (D) Planner-only cross-cutting field on a provider that
            //        doesn't declare it — added by the typo-closing change,
            //        see the section header above (1 of 39).
            ["vocabulary/unknown-kind-targeted/suite.e2e.yaml"] =
                "script.csharp step uses the planner's generic 'target' convention (REQ-004/REQ-007), which " +
                "script.csharp's own schema never declares (only 'code'/'file'); previously silently tolerated " +
                "(additionalProperties: true), now correctly rejected as unevaluated. " +
                "at /steps/0/target: (line 12) [unevaluatedProperties] Unknown property 'target' on step type 'script.csharp'. " +
                "UPDATE (feat/close-remaining-surfaces, Part A2): this fixture's own 'code:' is set, so " +
                "script.csharp's frozen oneOf is SATISFIED — the 'required: [\"file\"]' branch noise this entry " +
                "used to also document is now suppressed by SchemaErrorCollector's composite-branch-noise fix " +
                "(a satisfied oneOf/anyOf's non-matching sibling branch is exploration noise, not a genuine " +
                "defect); only the unevaluatedProperties finding above survives from this step. ALSO now fails " +
                "independently (services/dependencies schema closure, branch feat/close-environment-surface): " +
                "'legacy-cache' declares 'type: cassandra', not one of the thirteen kinds $defs/dependency's new " +
                "'type' enum recognises (this fixture's own name — 'unknown-kind' — is exactly the shape that enum " +
                "now catches at schema time) — at /environment/dependencies/legacy-cache/type: [enum] 'cassandra' " +
                "does not match one of the values in the enumeration.",
        };

    public static IEnumerable<object[]> PlanningFixtureFiles()
    {
        var dir = PlanningFixturesDirectory;
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var path in Directory
            .GetFiles(dir, "*.e2e.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(dir, path).Replace('\\', '/');
            if (ExcludedPlanningFixtures.ContainsKey(relative))
            {
                continue;
            }

            yield return new object[] { path };
        }
    }

    /// <summary>
    /// <see cref="PlanningFixtureFiles"/> must discover at least its known-safe
    /// floor of files — otherwise a broken repo-root resolution, or a
    /// mass-exclusion regression, would silently make this gate a no-op (or a
    /// near-no-op) instead of failing loudly. The floor matches the current
    /// committed count exactly (2 of 41 fixtures survive exclusion — see the
    /// class remarks above <see cref="ExcludedPlanningFixtures"/>): unlike
    /// <see cref="AcceptedFiles_DiscoversAtLeastOneFile"/>'s more comfortable
    /// margin, there is no room to spare below the true count here without
    /// the floor becoming meaningless (feat/close-remaining-surfaces, Part D).
    /// </summary>
    [Fact]
    public void PlanningFixtureFiles_DiscoversAtLeastOneFile()
    {
        const int minPlanningFixtureFiles = 2;

        var count = PlanningFixtureFiles().Count();

        Assert.True(count >= minPlanningFixtureFiles,
            $"Expected at least {minPlanningFixtureFiles} Planning.Tests fixtures under " +
            $"'{PlanningFixturesDirectory}' (after exclusions), found {count}. Either the " +
            "repo-root resolution is wrong, the fixtures directory moved, or too many " +
            "fixtures are now excluded.");
    }

    /// <summary>
    /// Every entry in <see cref="ExcludedPlanningFixtures"/> must still exist AND
    /// still fail validation. Without this, the exclusion list rots silently in
    /// two directions: a fixture that is deleted or renamed leaves a dead entry
    /// behind, and one that becomes valid — because it was fixed, or because the
    /// schema widened — stays excluded forever, quietly shrinking this gate's
    /// coverage while looking untouched.
    /// </summary>
    /// <remarks>
    /// This is what makes the exclusions self-maintaining rather than a
    /// write-once list: the day an exclusion becomes unnecessary, CI says so and
    /// names the entry to delete. Burning the list down is tracked in #330.
    /// </remarks>
    [Fact]
    public void ExcludedPlanningFixtures_AreAllStillPresentAndStillInvalid()
    {
        var dir = PlanningFixturesDirectory;
        Assert.True(Directory.Exists(dir), $"Planning fixtures directory not found: {dir}");

        var missing = new List<string>();
        var nowValid = new List<string>();

        foreach (var (relative, reason) in ExcludedPlanningFixtures)
        {
            var path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                missing.Add($"  {relative} — excluded for: {reason}");
                continue;
            }

            if (DocumentValidator.Validate(File.ReadAllText(path), Registry).IsValid)
            {
                nowValid.Add($"  {relative} — excluded for: {reason}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "These excluded Planning fixtures no longer exist — remove their entries from " +
            $"ExcludedPlanningFixtures:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");

        Assert.True(
            nowValid.Count == 0,
            "These excluded Planning fixtures now VALIDATE, so their exclusions are obsolete. " +
            "Delete each entry from ExcludedPlanningFixtures so the fixture rejoins the gate " +
            $"(see #330):{Environment.NewLine}{string.Join(Environment.NewLine, nowValid)}");
    }

    /// <summary>
    /// Every non-excluded Planning.Tests fixture must validate through
    /// <see cref="DocumentValidator"/> against the full Core registry — proof
    /// that a schema narrowing cannot silently break a real planner fixture.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlanningFixtureFiles))]
    public void PlanningFixture_IsValid(string path)
    {
        var yaml = File.ReadAllText(path);
        var result = DocumentValidator.Validate(yaml, Registry);

        Assert.True(result.IsValid, DescribeUnexpectedInvalid(path, result));
    }

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the
    /// directory containing <c>vouchfx.sln</c> — the repo root. Mirrors
    /// <c>SchemaFreezeTests.FindRepoRoot</c> / <c>ExamplesCompileTests.ResolveRepoRoot</c>.
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
            "Could not locate the repository root (no ancestor of " +
            $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln'). This gate must locate " +
            "the Planning.Tests fixtures directory from the source tree.");
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private static IEnumerable<string> DiscoverYamlFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var path in Directory
            .GetFiles(dir, "*.e2e.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return path;
        }
    }

    /// <summary>
    /// Formats a diagnostic assertion message naming the file and every
    /// error's <see cref="SchemaValidationError.InstanceLocation"/> and
    /// <see cref="SchemaValidationError.Message"/> — so a narrowing that
    /// breaks this gate tells the engineer exactly which construct it broke,
    /// without needing to reproduce the failure locally first.
    /// </summary>
    internal static string DescribeUnexpectedInvalid(string path, SchemaValidationResult result)
    {
        // Both call sites pass this as the message of an Assert.True(result.IsValid, …),
        // so a reader only ever sees it when validation FAILED. The guard therefore
        // cannot be reached through the assertion path; it exists so that a future
        // caller (or a debugger stepping in) gets a truthful sentence instead of the
        // nonsensical "expected VALID but … reported 0 error(s)" the main path would
        // produce with an empty error list.
        if (result.IsValid)
        {
            return $"{Path.GetFileName(path)}: validation passed.";
        }

        var lines = result.Errors.Select(e => $"  at {e.InstanceLocation}: {e.Message}");
        return $"{Path.GetFileName(path)} ({path}): expected VALID but schema validation " +
            $"reported {result.Errors.Count} error(s):{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }
}
