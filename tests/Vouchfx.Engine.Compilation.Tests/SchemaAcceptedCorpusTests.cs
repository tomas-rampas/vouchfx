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
//     surfaces (environment.services, every dependency kind, capture,
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
    /// Raised from 15 to 34 (2026-08-12). 15 was set when the
    /// corpus was young and never tightened as fixtures accumulated: the
    /// committed count is <b>38</b> — counted directly off the tree on
    /// 2026-08-28 (every <c>*.e2e.yaml</c> under <c>Corpus/Accepted</c>,
    /// recursively, the same enumeration <see cref="AcceptedFiles"/> itself
    /// performs), not carried forward from the previous revision of this
    /// comment, which said 36 and had already drifted a fixture behind the
    /// tree before this recount — so the old floor would have tolerated losing
    /// twenty-three fixtures in silence, which is the whole failure this gate
    /// exists to prevent. Its sibling <c>SchemaRejectedCorpusTests</c> was
    /// retightened the same way (40 to 55 against a then-counted 57) and this
    /// one was left behind. 34 is sensibly below-but-near the true count:
    /// tight enough that dropping several fixtures fails loudly, with enough
    /// headroom that routine reorganisation does not require raising it every
    /// time. Safe to raise as fixtures accumulate; recount rather than
    /// increment when doing so.
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
    /// Every document under <c>Corpus/Accepted</c> — <see cref="AcceptedFiles"/> excludes
    /// nothing, the scalar-coercion tranche having landed (see the RETIRED note below) —
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

    // Planning fixtures exercise the planner's graph and history rules. Positive
    // fixtures are complete suite documents, validated against the composed Core
    // language schema. Only deliberate negative-domain fixtures remain excluded:
    // zero-step documents, malformed steps, and the unknown-kind/target case.

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
    /// The 33 positive fixtures are schema-gated; the eight exclusions retain
    /// deliberately invalid input needed by the planner's negative-domain tests.
    /// </remarks>
    private static readonly Dictionary<string, string> ExcludedPlanningFixtures =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── (B) Deliberately zero-step — a suite that exists only to
            //        declare an unreferenced dependency/service for a
            //        vocabulary or coverage-gap test. Violates 'steps'
            //        minItems: 1 (3 fixtures).
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
            //        malformed-suite handling (4 fixtures).
            ["ingest/all-malformed/bad-one.e2e.yaml"] =
                "Deliberately malformed for PlanIngestTests' malformed-suite handling: the one step omits " +
                "'id' AND the http.rest-required fields. " +
                "at /steps/0: (line 5) Required properties [\"id\"] are not present; " +
                "at /steps/0: (line 5) Required properties [\"target\",\"method\",\"path\"] are not present",

            ["ingest/all-malformed/bad-two.e2e.yaml"] =
                "Deliberately malformed for PlanIngestTests' malformed-suite handling: the one step omits " +
                "'id' AND the db-assert.postgres-required fields. " +
                "at /steps/0: (line 5) Required properties [\"id\"] are not present; " +
                "at /steps/0: (line 5) Required properties [\"target\",\"query\",\"expect\"] are not present",

            ["ingest/one-malformed/bad.e2e.yaml"] =
                "Deliberately malformed for PlanIngestTests' malformed-suite handling: the one step omits " +
                "'id' AND the http.rest-required fields. " +
                "at /steps/0: (line 5) Required properties [\"id\"] are not present; " +
                "at /steps/0: (line 5) Required properties [\"target\",\"method\",\"path\"] are not present",

            ["acceptance/secret-safety/malformed-with-planted-secret.e2e.yaml"] =
                "Deliberately malformed for PlanSafetyTests: the step 'type' is an ~800-character garbage " +
                "token (planted past SuiteSetLoader's MaxParseErrorMessageChars truncation bound) so the " +
                "redaction test can prove the bound truncates it away. Fails the root schema's " +
                "'^[a-z0-9-]+\\.[a-z0-9-]+$' type pattern. " +
                "at /steps/0/type: (line 16) The string value is not a match for the indicated regular expression",

            // ── (D) Planner-only cross-cutting field on a provider that
            //        doesn't declare it — added by the typo-closing change,
            //        see the section header above (1 fixture).
            ["vocabulary/unknown-kind-targeted/suite.e2e.yaml"] =
                "script.csharp step uses the planner's generic 'target' convention (REQ-004/REQ-007), which " +
                "script.csharp's own schema never declares (only 'code'/'file'); previously silently tolerated " +
                "(additionalProperties: true), now correctly rejected as unevaluated. " +
                "at /steps/0/target: (line 12) Unknown property 'target' on step type 'script.csharp'. " +
                "UPDATE (feat/close-remaining-surfaces, Part A2): this fixture's own 'code:' is set, so " +
                "script.csharp's frozen oneOf is SATISFIED — the 'required: [\"file\"]' branch noise this entry " +
                "used to also document is now suppressed by SchemaErrorCollector's composite-branch-noise fix " +
                "(a satisfied oneOf/anyOf's non-matching sibling branch is exploration noise, not a genuine " +
                "defect); only the unevaluatedProperties finding above survives from this step. ALSO now fails " +
                "independently (services/dependencies schema closure, branch feat/close-environment-surface): " +
                "'legacy-cache' declares 'type: cassandra', not one of the kinds $defs/dependency's new " +
                "'type' enum recognises (this fixture's own name — 'unknown-kind' — is exactly the shape that enum " +
                "now catches at schema time) — at /environment/dependencies/legacy-cache/type: 'cassandra' " +
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
    /// Prevents broken discovery or additional exclusions from silently shrinking
    /// the 33-document positive Planning corpus.
    /// </summary>
    [Fact]
    public void PlanningFixtureFiles_DiscoversAtLeastOneFile()
    {
        const int minPlanningFixtureFiles = 33;

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
    /// <c>SchemaFreezeTests.FindRepoRoot</c> — one of a family of copies of this same walk that
    /// pre-date, and were not folded into, <c>Vouchfx.TestSupport.RepoRoot.Resolve()</c> (#551);
    /// see that type's own remarks for which walks it did and did not collapse.
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
