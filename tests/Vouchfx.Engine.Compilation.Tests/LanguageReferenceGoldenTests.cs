// S10-E-02 (the auto-generated part) — the LANGUAGE-REFERENCE golden gate.
//
// docs/language-reference.md is GENERATED from the composed v1 JSON Schema by
// LanguageReferenceGenerator.  This Docker-free golden gate recomposes that schema
// from the full six-Core-provider registry, regenerates the Markdown, and asserts
// the committed docs/language-reference.md is byte-for-byte identical to it.  The
// reference therefore tracks exactly what the compiler accepts and can never drift:
// a schema change (a new optional field, a tightened enum) OR a hand-edit to the
// .md fails this gate until the file is regenerated and re-reviewed.
//
// Mirrors SchemaFreezeTests / VsCodeShippedSchemaSyncTests: same six-provider
// anchor list, same Normalise / FirstDifference helpers, same FindRepoRoot-up-to-
// vouchfx.sln source-tree read (the .md is read from the SOURCE tree, not a build
// copy, so an editor's hand-edit is caught, not masked by a stale artifact).
//
// REGENERATION (when the schema legitimately changes — also documented in the
// generated file's footer):
//   VOUCHFX_REGEN_LANGUAGE_REFERENCE=1 dotnet test tests/Vouchfx.Engine.Compilation.Tests \
//     --filter "FullyQualifiedName~LanguageReferenceGoldenTests"
//   This rewrites docs/language-reference.md from the freshly-composed schema.
//   Review the diff, then commit.
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
/// S10-E-02: the language-reference golden gate.  Proves
/// <c>docs/language-reference.md</c> is exactly what
/// <see cref="LanguageReferenceGenerator.Generate"/> produces from the composed v1
/// schema, so the published reference can never advertise (or omit) a field the
/// compiler would (not) accept.
/// </summary>
public sealed class LanguageReferenceGoldenTests
{
    /// <summary>
    /// Set <c>VOUCHFX_REGEN_LANGUAGE_REFERENCE=1</c> to make the gate REWRITE the
    /// committed <c>docs/language-reference.md</c> from the freshly-composed schema
    /// instead of asserting against it.  This is the supported regeneration path
    /// when the schema legitimately changes.
    /// </summary>
    private const string RegenEnvVar = "VOUCHFX_REGEN_LANGUAGE_REFERENCE";

    /// <summary>
    /// The six Core provider assemblies that compose the v1 schema, anchored by one
    /// concrete provider type per assembly (mirrors
    /// <c>SchemaFreezeTests.CoreProviderAssemblies</c> and
    /// <c>Vouchfx.Cli.ProviderRegistryFactory.CoreProviderAssemblies</c>).  Listing
    /// them by anchor type makes a renamed/removed provider a compile error here.
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
        typeof(CacheAssertElasticsearchProvider).Assembly, // cache-assert.elasticsearch
        typeof(MqPublishNatsProvider).Assembly,        // mq-publish.nats
        typeof(MqExpectNatsProvider).Assembly,         // mq-expect.nats
        typeof(MqPublishAzureServiceBusProvider).Assembly, // mq-publish.azureservicebus
        typeof(MqExpectAzureServiceBusProvider).Assembly,  // mq-expect.azureservicebus
        typeof(MqPublishRedisProvider).Assembly,      // mq-publish.redis
        typeof(MqExpectRedisProvider).Assembly,        // mq-expect.redis
        typeof(MetricsAssertPrometheusProvider).Assembly,  // metrics-assert.prometheus
        typeof(DbAssertDynamodbProvider).Assembly,    // db-assert.dynamodb
        typeof(StorageAssertS3Provider).Assembly,     // storage-assert.s3
        typeof(TraceExpectOtlpProvider).Assembly,     // trace-expect.otlp
        typeof(HttpSoapProvider).Assembly,             // http.soap
    };

    /// <summary>
    /// The committed <c>docs/language-reference.md</c> must be byte-for-byte
    /// identical to the Markdown generated from the composed v1 schema.  If this
    /// fails, the reference has drifted from the schema (or someone hand-edited the
    /// generated file): regenerate it (see <see cref="RegenEnvVar"/>) and re-review.
    /// </summary>
    [Fact]
    public void LanguageReference_MatchesGeneratedFromSchema_ByteForByte()
    {
        var generated = GenerateReference();
        var path = LanguageReferencePath();

        // Regeneration mode: rewrite the committed file and pass.  Used by a
        // developer after a legitimate schema change.
        if (IsRegenRequested())
        {
            File.WriteAllText(path, generated);
            return;
        }

        Assert.True(
            File.Exists(path),
            $"The committed language reference was not found at '{path}'.  "
            + $"Generate it by running this gate with {RegenEnvVar}=1.");

        var committed = File.ReadAllText(path);

        var generatedNormalised = Normalise(generated);
        var committedNormalised = Normalise(committed);

        Assert.True(
            string.Equals(generatedNormalised, committedNormalised, StringComparison.Ordinal),
            "docs/language-reference.md has DRIFTED from the composed v1 schema.  The "
            + "reference is GENERATED and must never be hand-edited: regenerate it with "
            + $"{RegenEnvVar}=1 dotnet test "
            + "tests/Vouchfx.Engine.Compilation.Tests "
            + "--filter \"FullyQualifiedName~LanguageReferenceGoldenTests\" and re-review."
            + Environment.NewLine
            + FirstDifference(committedNormalised, generatedNormalised));
    }

    /// <summary>
    /// The generated reference must cover all Core providers (including
    /// <c>db-assert.sqlserver</c>), the common step fields, and a Required/Optional
    /// breakdown — a structural guard so a future generator change that silently drops
    /// a section is caught even if the golden were regenerated in the same commit.
    /// </summary>
    [Fact]
    public void GeneratedReference_CoversAllCoreProvidersAndCommonFields()
    {
        var generated = GenerateReference();

        // All Core step types (original six plus db-assert.sqlserver, db-assert.mongodb, db-assert.mysql, mq-publish.rabbitmq, mq-expect.rabbitmq, mq-publish.nats, mq-expect.nats).
        Assert.Contains("### `http.rest`", generated, StringComparison.Ordinal);
        Assert.Contains("### `db-assert.postgres`", generated, StringComparison.Ordinal);
        Assert.Contains("### `db-assert.sqlserver`", generated, StringComparison.Ordinal);
        Assert.Contains("### `db-assert.mongodb`", generated, StringComparison.Ordinal);
        Assert.Contains("### `db-assert.mysql`", generated, StringComparison.Ordinal);
        Assert.Contains("### `script.csharp`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-publish.kafka`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-expect.kafka`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-publish.rabbitmq`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-expect.rabbitmq`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-publish.nats`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-expect.nats`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-publish.azureservicebus`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-expect.azureservicebus`", generated, StringComparison.Ordinal);
        Assert.Contains("### `webhook-listen.http`", generated, StringComparison.Ordinal);
        Assert.Contains("### `cache-assert.redis`", generated, StringComparison.Ordinal);
        Assert.Contains("### `cache-assert.elasticsearch`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mail-expect.smtp`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-publish.redis`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-expect.redis`", generated, StringComparison.Ordinal);
        Assert.Contains("### `metrics-assert.prometheus`", generated, StringComparison.Ordinal);
        Assert.Contains("### `db-assert.dynamodb`", generated, StringComparison.Ordinal);
        Assert.Contains("### `storage-assert.s3`", generated, StringComparison.Ordinal);

        // The common step fields, documented once.
        Assert.Contains("## Common step fields", generated, StringComparison.Ordinal);
        Assert.Contains("| `id` |", generated, StringComparison.Ordinal);
        Assert.Contains("| `type` |", generated, StringComparison.Ordinal);
        Assert.Contains("| `capture` |", generated, StringComparison.Ordinal);
        Assert.Contains("| `verifyMode` |", generated, StringComparison.Ordinal);
        Assert.Contains("| `timeout` |", generated, StringComparison.Ordinal);
        Assert.Contains("| `continueOnFailure` |", generated, StringComparison.Ordinal);

        // Required/optional breakdown present (http.rest has both).
        Assert.Contains("**Required fields**", generated, StringComparison.Ordinal);
        Assert.Contains("**Optional fields**", generated, StringComparison.Ordinal);

        // Self-identifies the frozen schema version.
        Assert.Contains("**Schema version:** `v1`", generated, StringComparison.Ordinal);
    }

    /// <summary>
    /// M2 (gatekeeper, feat/fragment-completeness): before this fix, the
    /// reference listed script.csharp's code/file under "Optional fields"
    /// with NO Required section at all — actively disagreeing with the
    /// catalogue's own ExactlyOneOfGroups (EngineExport.cs, fixed under B1).
    /// script.csharp's `oneOf` (code XOR file) must now render its own
    /// "Required — exactly one of" table naming both fields, and neither may
    /// ALSO appear in the (now absent, since nothing else is required)
    /// Required/Optional tables — that would be a second, contradictory
    /// answer to "is this required".
    /// </summary>
    [Fact]
    public void GeneratedReference_ScriptCsharp_RendersExactlyOneOfTable_NotOptional()
    {
        var generated = GenerateReference();
        var section = ExtractStepTypeSection(generated, "script.csharp");

        Assert.Contains("**Required — exactly one of**", section, StringComparison.Ordinal);
        Assert.Contains("| `code` |", section, StringComparison.Ordinal);
        Assert.Contains("| `file` |", section, StringComparison.Ordinal);
        Assert.DoesNotContain("**Optional fields**", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// Generic mechanism, not a script.csharp special case: mq-publish.azureservicebus's
    /// identical oneOf (queue/topic) gets the same table.
    /// </summary>
    [Fact]
    public void GeneratedReference_MqPublishAzureServiceBus_RendersExactlyOneOfTable()
    {
        var generated = GenerateReference();
        var section = ExtractStepTypeSection(generated, "mq-publish.azureservicebus");

        Assert.Contains("**Required — exactly one of**", section, StringComparison.Ordinal);
        Assert.Contains("| `queue` |", section, StringComparison.Ordinal);
        Assert.Contains("| `topic` |", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// The anyOf counterpart: mq-expect.azureservicebus's expectPayloadContains/
    /// expectProperties ("at least one of") renders its own table, distinct
    /// heading from the oneOf case.
    /// </summary>
    [Fact]
    public void GeneratedReference_MqExpectAzureServiceBus_RendersAtLeastOneOfTable()
    {
        var generated = GenerateReference();
        var section = ExtractStepTypeSection(generated, "mq-expect.azureservicebus");

        Assert.Contains("**Required — at least one of**", section, StringComparison.Ordinal);
        Assert.Contains("| `expectPayloadContains` |", section, StringComparison.Ordinal);
        Assert.Contains("| `expectProperties` |", section, StringComparison.Ordinal);
        // mq-expect.azureservicebus's OWN queue-XOR-(topic+subscription) oneOf has a
        // two-name branch — degrade-don't-fabricate means NO "exactly one of" table
        // for THIS type; queue/topic/subscription fall back to the plain Optional table.
        Assert.DoesNotContain("**Required — exactly one of**", section, StringComparison.Ordinal);
        Assert.Contains("| `queue` |", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extracts one step type's own <c>### `type`</c> section (up to the next
    /// <c>### </c> heading or end of string) so a test can make assertions
    /// scoped to that type alone rather than the whole document.
    /// </summary>
    private static string ExtractStepTypeSection(string generated, string typeId)
    {
        var marker = $"### `{typeId}`";
        var start = generated.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to find a '{marker}' section in the generated reference.");

        var next = generated.IndexOf("\n### `", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? generated[start..] : generated[start..next];
    }

    /// <summary>
    /// MAJOR-2 / m1 (feat/close-remaining-surfaces, TWO rounds of the same
    /// finding): <c>timeout</c>'s A1 de-branching moved its <c>"type"</c>
    /// from a <c>oneOf</c> of typed alternatives (rendered as separate,
    /// backtick-wrapped tokens joined OUTSIDE every span, e.g.
    /// <c>`string` \| `number`</c>) onto a bare <c>"type": [...]</c> array,
    /// which the generator originally joined as a RAW, unescaped pipe INSIDE
    /// one shared span (<c>`string|number`</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Round 1 fixed that as an ESCAPED pipe still INSIDE the one shared span
    /// (<c>`string\|number`</c>) — WRONG, per round 2's measurement: fed all
    /// three forms through <c>markdown.markdown(...)</c> with this repo's
    /// pinned <c>mkdocs-material==9.7.7</c> and its configured
    /// <c>markdown_extensions</c> (MkDocs always adds the <c>tables</c>
    /// builtin regardless of that list — confirmed from
    /// <c>mkdocs.config.defaults</c>). Results: the ORIGINAL raw-pipe form
    /// does NOT actually split the table row in Python-Markdown's own
    /// <c>tables</c> extension (only GitHub's GFM renderer breaks on that
    /// shape); round 1's escaped-INSIDE-span fix renders the LITERAL
    /// BACKSLASH character visibly (code-span content is verbatim; escape
    /// processing never reaches inside it) — the WORST of the three forms on
    /// the deployed MkDocs Pages site. Round 1's "silently dropped the
    /// description column on the live site" claim was therefore an
    /// UNVERIFIED INFERENCE from the GFM-only defect, not a measurement of
    /// the actual deployed renderer; corrected here. This test now pins the
    /// OUTSIDE-span form, which measured clean on both renderers.
    /// </para>
    /// </remarks>
    [Fact]
    public void GeneratedReference_TimeoutTypeCell_UsesAnEscapedPipeOutsideTheCodeSpans()
    {
        var generated = GenerateReference();
        var timeoutLine = Array.Find(generated.Split('\n'), l => l.StartsWith("| `timeout` |", StringComparison.Ordinal));

        Assert.NotNull(timeoutLine);
        // timeout's schema is "type": ["string", "number"] (root-language-schema.json).
        Assert.Contains("`string` \\| `number`", timeoutLine, StringComparison.Ordinal);
        Assert.DoesNotContain("`string|number`", timeoutLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\\|number", timeoutLine, StringComparison.Ordinal);
        Assert.DoesNotContain("string\\|", timeoutLine, StringComparison.Ordinal);

        // Structural confirmation: the row splits into exactly 4 columns.
        Assert.Equal(4, CountUnescapedPipeDelimitedColumns(timeoutLine!));
    }

    /// <summary>
    /// General guard (not merely the one field that happened to expose the
    /// bug): NO backtick-delimited span anywhere in the generated reference
    /// may contain a raw, unescaped <c>|</c> — the same anti-pattern could
    /// recur for any future multi-type-array field (a provider's own
    /// <c>"type": [...]</c> declaration), not only <c>timeout</c>.
    /// </summary>
    [Fact]
    public void GeneratedReference_NeverEmitsAnUnescapedPipeInsideABacktickSpan()
    {
        var generated = GenerateReference();
        var offendingLines = new List<string>();

        foreach (var line in generated.Split('\n'))
        {
            if (ContainsPipeInsideBackticks(line))
            {
                offendingLines.Add(line);
            }
        }

        Assert.True(offendingLines.Count == 0,
            "A backtick-delimited span contains a '|' (escaped or not). Neither in-span "
            + "form is safe on BOTH renderers this file ships to: a RAW in-span pipe "
            + "splits the table row on GitHub's GFM (dropping a column there, though "
            + "python-markdown's tables extension renders it fine), while an ESCAPED "
            + "in-span '\\|' renders a literal backslash on the python-markdown/MkDocs "
            + "site (though GFM renders it fine). The only form correct on both is "
            + "keeping pipes OUTSIDE code spans, escaped between them — "
            + "`string` \\| `number` — which is what JoinAsBacktickedAlternatives "
            + "emits. Found on:\n"
            + string.Join("\n", offendingLines));
    }

    /// <summary>
    /// True when some backtick-delimited span on <paramref name="line"/>
    /// contains a <c>|</c>, escaped or not.
    /// </summary>
    /// <remarks>
    /// Deliberately flags the escaped form too. An earlier revision exempted
    /// <c>\|</c> — which is exactly the in-span shape a prior round of this
    /// work shipped and then had to fix (visible backslash on the MkDocs
    /// site), so the exemption made this guard blind to the one regression it
    /// exists to catch. Since both in-span forms are wrong on one renderer
    /// each, any pipe inside a span is a defect and the guard says so.
    /// </remarks>
    private static bool ContainsPipeInsideBackticks(string line)
    {
        var inSpan = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '`')
            {
                inSpan = !inSpan;
                continue;
            }

            if (inSpan && line[i] == '|')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts the pipe-delimited columns a Markdown table ROW splits into,
    /// treating a backslash-escaped <c>\|</c> as literal text rather than a
    /// column delimiter (mirroring GFM table parsing).
    /// </summary>
    private static int CountUnescapedPipeDelimitedColumns(string line)
    {
        var columns = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '|' && (i == 0 || line[i - 1] != '\\'))
            {
                columns++;
            }
        }

        // N unescaped pipes delimit N-1 columns for a well-formed
        // "| c1 | c2 | ... | cN |" row (leading/trailing pipes both count).
        return columns - 1;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string GenerateReference()
    {
        var registry = StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());
        var composedSchemaJson = SchemaComposer.ComposeSchemaJson(registry);
        return LanguageReferenceGenerator.Generate(composedSchemaJson);
    }

    private static bool IsRegenRequested()
    {
        var value = Environment.GetEnvironmentVariable(RegenEnvVar);
        return !string.IsNullOrEmpty(value)
            && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    // Collapse CRLF/CR → LF and drop any trailing final newline(s): the gate
    // compares CONTENT, immune to line-ending style and to an editor's
    // insert_final_newline rewrite of the .md.
    private static string Normalise(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

    /// <summary>
    /// Resolves <c>docs/language-reference.md</c> in the SOURCE tree by walking up
    /// from the test assembly's base directory to the repo root (the directory
    /// containing <c>vouchfx.sln</c>).  Reading the source-tree file (not a build
    /// copy) is what makes this gate catch a developer who hand-edits the reference.
    /// </summary>
    private static string LanguageReferencePath()
    {
        var repoRoot = FindRepoRoot();
        return Path.Combine(repoRoot, "docs", "language-reference.md");
    }

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the
    /// directory containing <c>vouchfx.sln</c> — the repo root.
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
            + "docs/language-reference.md from the source tree.");
    }

    /// <summary>
    /// Produces a short, human-readable description of the first line that differs
    /// between the committed and the generated reference, to make a drift failure
    /// diagnosable from the test output alone.
    /// </summary>
    private static string FirstDifference(string committed, string generated)
    {
        var committedLines = committed.Split('\n');
        var generatedLines = generated.Split('\n');
        var max = Math.Max(committedLines.Length, generatedLines.Length);

        for (var i = 0; i < max; i++)
        {
            var c = i < committedLines.Length ? committedLines[i] : "<EOF>";
            var g = i < generatedLines.Length ? generatedLines[i] : "<EOF>";
            if (!string.Equals(c, g, StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:"
                    + $"{Environment.NewLine}  committed: {c}"
                    + $"{Environment.NewLine}  generated: {g}";
            }
        }

        return "(no line-level difference detected; check trailing whitespace or length)";
    }
}
