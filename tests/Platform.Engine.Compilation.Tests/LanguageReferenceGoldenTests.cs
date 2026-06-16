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
//   VOUCHFX_REGEN_LANGUAGE_REFERENCE=1 dotnet test tests/Platform.Engine.Compilation.Tests \
//     --filter "FullyQualifiedName~LanguageReferenceGoldenTests"
//   This rewrites docs/language-reference.md from the freshly-composed schema.
//   Review the diff, then commit.
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
        typeof(ScriptCsharpProvider).Assembly,        // script.csharp
        typeof(MqPublishKafkaProvider).Assembly,      // mq-publish.kafka
        typeof(MqExpectKafkaProvider).Assembly,       // mq-expect.kafka
        typeof(WebhookListenHttpProvider).Assembly,   // webhook-listen.http
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
            + "tests/Platform.Engine.Compilation.Tests "
            + "--filter \"FullyQualifiedName~LanguageReferenceGoldenTests\" and re-review."
            + Environment.NewLine
            + FirstDifference(committedNormalised, generatedNormalised));
    }

    /// <summary>
    /// The generated reference must cover all six Core providers, the common step
    /// fields, and a Required/Optional breakdown — a structural guard so a future
    /// generator change that silently drops a section is caught even if the golden
    /// were regenerated in the same commit.
    /// </summary>
    [Fact]
    public void GeneratedReference_CoversAllSixCoreProvidersAndCommonFields()
    {
        var generated = GenerateReference();

        // All six Core step types.
        Assert.Contains("### `http.rest`", generated, StringComparison.Ordinal);
        Assert.Contains("### `db-assert.postgres`", generated, StringComparison.Ordinal);
        Assert.Contains("### `script.csharp`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-publish.kafka`", generated, StringComparison.Ordinal);
        Assert.Contains("### `mq-expect.kafka`", generated, StringComparison.Ordinal);
        Assert.Contains("### `webhook-listen.http`", generated, StringComparison.Ordinal);

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
