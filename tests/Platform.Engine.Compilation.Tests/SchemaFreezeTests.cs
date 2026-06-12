// S08-F-01 (T2 — Freeze the v1 JSON Schema).
//
// The composed v1 JSON Schema — the root-language schema plus the
// JsonSchemaFragment of every Core provider — is FROZEN.  This golden-snapshot
// test recomposes that schema from the full six-Core-provider registry and
// asserts it is byte-for-byte identical to the committed golden artifact
// (Golden/composed-schema.v1.json).
//
// Why this gate exists:
//   • v1 is a published contract.  Any change to the composed schema (a new
//     keyword, a tightened enum, a reordered clause) changes what authors'
//     .e2e.yaml files validate against.  The freeze test makes every such change
//     a DELIBERATE, REVIEWED act: the test fails until the golden is regenerated
//     and re-reviewed.
//   • Determinism: SchemaComposer sorts the if/then clauses by typeKey, so the
//     composed JSON is byte-stable regardless of reflection/registration order.
//     ComposeSchemaJson emits canonical, indented JSON for snapshotting.
//
// This test builds the registry from the same six Core provider assemblies the
// CLI ships (see Vouchfx.Cli.ProviderRegistryFactory) — the test project
// references all six provider projects so a renamed/removed provider is a
// compile error here, not a silent schema gap.
using System;
using System.IO;
using System.Reflection;
using Platform.Engine.Compilation.Schema;
using Platform.Sdk;
using Platform.Steps.DbAssert.Postgres;
using Platform.Steps.HttpRest;
using Platform.Steps.MqExpect.Kafka;
using Platform.Steps.MqPublish.Kafka;
using Platform.Steps.Script.Csharp;
using Platform.Steps.WebhookListen.Http;
using Xunit;

namespace Platform.Engine.Compilation.Tests;

/// <summary>
/// S08-F-01: the frozen-v1-schema golden-snapshot gate.
/// </summary>
public sealed class SchemaFreezeTests
{
    /// <summary>
    /// The six Core provider assemblies that compose the v1 schema, anchored by
    /// one concrete provider type per assembly (mirrors
    /// <c>Vouchfx.Cli.ProviderRegistryFactory.CoreProviderAssemblies</c>).  Listing
    /// them by anchor type makes a renamed/removed provider a compile error.
    /// </summary>
    private static Assembly[] CoreProviderAssemblies() => new[]
    {
        typeof(HttpRestProvider).Assembly,            // http.rest
        typeof(DbAssertPostgresProvider).Assembly,    // db-assert.postgres
        typeof(ScriptCsharpProvider).Assembly,        // script.csharp
        typeof(MqPublishKafkaProvider).Assembly,      // mq-publish.kafka
        typeof(MqExpectKafkaProvider).Assembly,       // mq-expect.kafka
        typeof(WebhookListenHttpProvider).Assembly,   // webhook-listen.http
    };

    /// <summary>
    /// The composed v1 JSON Schema for the full six-Core-provider registry must
    /// be byte-for-byte identical to the committed golden.  If this fails, the v1
    /// schema has drifted.
    /// </summary>
    [Fact]
    public void ComposedV1Schema_MatchesGolden_ByteForByte()
    {
        var registry = StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());

        var actual = SchemaComposer.ComposeSchemaJson(registry);
        var golden = ReadGolden();

        // Normalise line endings AND any trailing final newline on both sides so a
        // CRLF/LF checkout difference (Git autocrlf, editor settings) or an editor's
        // insert_final_newline rewrite of the golden never produces a spurious drift;
        // the freeze contract is about schema content, not byte-level newline style.
        var actualNormalised = Normalise(actual);
        var goldenNormalised = Normalise(golden);

        Assert.True(
            string.Equals(actualNormalised, goldenNormalised, StringComparison.Ordinal),
            "The composed v1 JSON Schema has DRIFTED. v1 is FROZEN — if this change "
            + "is intentional and additive/non-breaking, regenerate "
            + "Golden/composed-schema.v1.json and get it reviewed; otherwise revert."
            + Environment.NewLine
            + FirstDifference(goldenNormalised, actualNormalised));
    }

    /// <summary>
    /// The frozen artifact must self-identify as <c>v1</c> — both via the
    /// composer's version constant and via the in-schema version marker — so the
    /// snapshot is unambiguously the v1 contract.
    /// </summary>
    [Fact]
    public void ComposedV1Schema_SelfIdentifiesAsV1()
    {
        Assert.Equal("v1", SchemaComposer.SchemaVersion);

        var registry = StepKindRegistry.BuildAndFreeze(CoreProviderAssemblies());
        var json = SchemaComposer.ComposeSchemaJson(registry);

        // The composed schema carries an explicit version annotation that names
        // the frozen contract.
        Assert.Contains("\"x-vouchfx-schema-version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"v1\"", json, StringComparison.Ordinal);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // Collapse CRLF/CR → LF and drop any trailing final newline(s): the freeze
    // contract compares schema CONTENT, immune to line-ending style and to an
    // editor's insert_final_newline rewrite of the golden file.
    private static string Normalise(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

    /// <summary>
    /// Reads the committed golden artifact from the test assembly's output
    /// directory.  The golden ships as a <c>Content</c> item copied next to the
    /// test DLL under <c>Golden/composed-schema.v1.json</c>.
    /// </summary>
    private static string ReadGolden()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Golden", "composed-schema.v1.json");

        Assert.True(
            File.Exists(path),
            $"Golden v1 schema not found at '{path}'.  The freeze gate requires "
            + "Golden/composed-schema.v1.json to be committed and copied to output.");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Produces a short, human-readable description of the first line that differs
    /// between the golden and the actual composed schema, to make a drift failure
    /// diagnosable from the test output alone.
    /// </summary>
    private static string FirstDifference(string golden, string actual)
    {
        var goldenLines = golden.Split('\n');
        var actualLines = actual.Split('\n');
        var max = Math.Max(goldenLines.Length, actualLines.Length);

        for (var i = 0; i < max; i++)
        {
            var g = i < goldenLines.Length ? goldenLines[i] : "<EOF>";
            var a = i < actualLines.Length ? actualLines[i] : "<EOF>";
            if (!string.Equals(g, a, StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:"
                    + $"{Environment.NewLine}  golden: {g}"
                    + $"{Environment.NewLine}  actual: {a}";
            }
        }

        return "(no line-level difference detected; check trailing whitespace or length)";
    }
}
