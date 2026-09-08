// Issue #505 — the YAML→JSON conversion's output budget.
//
// SchemaResources.ConvertYamlToJsonDocument used to have no bound of any kind. Its step 1
// (the deserialiser) binds ONE object instance per anchor and hands back the same reference
// at every alias site, so the graph it returns is a shared DAG whose size tracks the
// document; its step 2 (the JSON-compatible emitter) walks that DAG and re-materialises
// every alias into the output text. Step 2 therefore pays the full expansion. Measured on
// the pinned YamlDotNet 16.3.0: an EIGHT-line document of anchored sequences, ten aliases
// per level, emitted 80,246,964 characters — 160 MB of UTF-16 — in 13.8 s, and every further
// line multiplies that by ten. (The issue reports ~1.2 GB on its own reporter's shape; the
// figures here are this repository's own re-measurement, not that report restated.)
// YamlDotNet's 50-level recursion ceiling does not help: this shape is wide, not deep.
//
// The fix is an output-size budget enforced by a counting TextWriter, so the expansion stops
// EARLY and in bounded memory. These rows pin the three properties that matter:
//
//   • The refusal arrives, carries an actionable diagnostic, and is FAST — the timing
//     assertion is what fails if the implementation ever regresses to building the whole
//     string and measuring it afterwards.
//   • A legitimate suite — including the largest real .e2e.yaml in this repository — is
//     unaffected.
//   • The refusal reaches the caller as SchemaValidationResult.Invalid through BOTH entry
//     points, not as an escaping exception, and the pre-existing empty-document diagnostic
//     is unchanged.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.HttpRest;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Issue #505: bounds on the YAML→JSON bridge shared by
/// <see cref="YamlSchemaValidator"/> and <see cref="SchemaComposer"/>.
/// </summary>
public sealed class SchemaConversionBudgetTests
{
    // ── The amplifying document ──────────────────────────────────────────────
    //
    // Four levels of ten aliases each over a 32,000-character leaf scalar:
    // 10^4 x 32,000 = 320,000,000 characters of JSON (355,598,990 as actually emitted,
    // once quoting and punctuation are counted). That is ~21x the 16 Mi-character budget,
    // which is the ratio the timing assertion below rests on.
    //
    // The LEAF IS A LONG SCALAR RATHER THAN MORE LEVELS, and the choice is what keeps this
    // row cheap. A guarded run must always emit the whole budget before it can trip, so its
    // cost is fixed; only the UNGUARDED cost is a free variable, and a long leaf buys a
    // large total output for very little parsing. Measured on the pinned YamlDotNet 16.3.0:
    // guarded 558/562/573 ms over three runs, unguarded 10,599/11,836/11,104 ms — a ~19x
    // separation. An all-sequence chain reaching a comparable total needs seven levels and
    // costs the guarded path 1,193 ms for an 11.6x separation, i.e. strictly worse on both
    // sides.

    private const int AliasLevels = 4;
    private const int AliasFanout = 10;
    private const int LeafScalarChars = 32_000;

    /// <summary>
    /// Wall-clock ceiling for a refusal, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Sits between the two measured figures above with room on both sides, and is close to
    /// their geometric midpoint on purpose — that is the figure that trades false failure on
    /// a slow host against a missed regression on a fast one most evenly. These two rows
    /// were run with the ceiling temporarily set to 1 ms so the assertion would print what
    /// it actually measures: <strong>576 ms</strong> and <strong>434 ms</strong>. So the
    /// ceiling is ~7x the guarded refusal and ~2.7x below the measured unguarded run
    /// (10,599 ms) — a ~18x separation, of which this figure keeps roughly the middle.
    /// An implementation that regressed to "serialise, then measure" FAILS this row rather
    /// than hanging the suite: it still finishes, in about eleven seconds.
    /// Intra-assembly parallelism is disabled for this assembly (see AssemblyInfo.cs), so
    /// this measurement is not perturbed by concurrent test classes.
    /// </remarks>
    private const int RefusalBudgetMs = 4_000;

    // ── Refusal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An alias-amplified document is refused through <see cref="YamlSchemaValidator"/> with
    /// a diagnostic that names the budget and names anchors/aliases as the cause — and the
    /// refusal is bounded, not the result of building the whole 355 Mi-character string.
    /// </summary>
    [Fact]
    public void Validate_AliasAmplifiedDocument_IsRefusedQuicklyByTheRootSchemaValidator()
    {
        var yaml = AliasAmplifiedYaml();

        var stopwatch = Stopwatch.StartNew();
        var result = YamlSchemaValidator.Validate(yaml);
        stopwatch.Stop();

        AssertBudgetRefusal(result);

        Assert.True(
            stopwatch.ElapsedMilliseconds < RefusalBudgetMs,
            $"The refusal took {stopwatch.ElapsedMilliseconds} ms, which exceeds the "
            + $"{RefusalBudgetMs} ms ceiling. The guard is supposed to stop the expansion as "
            + "the character count crosses the budget; this timing says the conversion is "
            + "materialising far more of the document than the budget allows — the most "
            + "likely cause is a regression to serialising the whole graph to a string and "
            + "measuring its length afterwards.");
    }

    /// <summary>
    /// The same document is refused identically through <see cref="SchemaComposer"/> — the
    /// composed path an author's suite actually hits.
    /// </summary>
    [Fact]
    public void Validate_AliasAmplifiedDocument_IsRefusedQuicklyByTheComposedValidator()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });
        var yaml = AliasAmplifiedYaml();

        var stopwatch = Stopwatch.StartNew();
        var result = SchemaComposer.Validate(registry, yaml);
        stopwatch.Stop();

        AssertBudgetRefusal(result);

        Assert.True(
            stopwatch.ElapsedMilliseconds < RefusalBudgetMs,
            $"The refusal took {stopwatch.ElapsedMilliseconds} ms, which exceeds the "
            + $"{RefusalBudgetMs} ms ceiling — see the sibling row's message.");
    }

    // ── The budget does not refuse real input ────────────────────────────────

    /// <summary>
    /// The largest real <c>.e2e.yaml</c> in this repository still validates. This is the
    /// document the budget was derived against: 23,528 characters of YAML converting to
    /// 8,293 characters of JSON, three orders of magnitude inside the bound.
    /// </summary>
    /// <remarks>
    /// Validated through the COMPOSED path against the full 25-provider Core registry
    /// (borrowed from <c>SchemaAcceptedCorpusTests</c>, exactly as
    /// <c>SchemaRejectedCorpusTests</c> borrows it). The root-only
    /// <see cref="YamlSchemaValidator"/> would reject a real suite's provider-contributed
    /// properties, which is a fact about that validator and not about this budget.
    /// </remarks>
    [Fact]
    public void Validate_LargestRealSuiteInTheRepository_IsUnaffectedByTheBudget()
    {
        var path = Path.Combine(FindRepoRoot(), "examples", "security-mtls.e2e.yaml");
        Assert.True(File.Exists(path), $"Expected the largest real example suite at '{path}'.");

        var result = SchemaComposer.Validate(
            SchemaAcceptedCorpusTests.Registry, File.ReadAllText(path));

        Assert.True(
            result.IsValid,
            "The largest real suite in the repository must validate: "
            + string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    /// <summary>
    /// A synthetic suite an order of magnitude larger than anything the repository contains
    /// — 2,000 ordinary <c>http.rest</c> steps — still validates through the composed path.
    /// The budget bounds ALIAS AMPLIFICATION, not suite size.
    /// </summary>
    [Fact]
    public void Validate_LargeButOrdinarySuite_IsUnaffectedByTheBudget()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });

        var builder = new StringBuilder();
        builder.AppendLine("steps:");
        for (var i = 0; i < 2_000; i++)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  - id: step-{i}");
            builder.AppendLine("    type: http.rest");
            builder.AppendLine("    target: api");
            builder.AppendLine("    method: GET");
            builder.AppendLine(CultureInfo.InvariantCulture, $"    path: /orders/{i}");
        }

        var result = SchemaComposer.Validate(registry, builder.ToString());

        Assert.True(
            result.IsValid,
            "A large but entirely ordinary suite must validate: "
            + string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    // ── The pre-existing empty-document diagnostic is unchanged ──────────────

    /// <summary>
    /// A comment-only document — not whitespace-only, so it passes the callers' own
    /// short-circuit and reaches the converter — still reports
    /// <c>The YAML document is empty.</c> through both entry points, wrapped by the same
    /// <c>Failed to parse YAML:</c> prefix as before.
    /// </summary>
    [Fact]
    public void Validate_CommentOnlyDocument_StillReportsTheEmptyDiagnostic()
    {
        const string yaml = "# a document of nothing but a comment\n";
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });

        foreach (var result in new[]
        {
            YamlSchemaValidator.Validate(yaml),
            SchemaComposer.Validate(registry, yaml),
        })
        {
            Assert.False(result.IsValid);
            var message = Assert.Single(result.Errors).Message;
            Assert.Equal("Failed to parse YAML: The YAML document is empty.", message);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that <paramref name="result"/> is the budget refusal, arriving as an ordinary
    /// <see cref="SchemaValidationResult"/> rather than an escaping exception, and carrying a
    /// diagnostic an author can act on.
    /// </summary>
    private static void AssertBudgetRefusal(SchemaValidationResult result)
    {
        Assert.False(result.IsValid);

        var message = Assert.Single(result.Errors).Message;

        Assert.StartsWith("Failed to parse YAML: ", message, StringComparison.Ordinal);

        // The budget is named, so the author knows what was exceeded …
        Assert.Contains("16,777,216 characters", message, StringComparison.Ordinal);

        // … and the cause is named, so the author knows what to change. A bare
        // "limit exceeded" would satisfy neither.
        Assert.Contains("anchors and aliases", message, StringComparison.Ordinal);
        Assert.Contains("&anchor / *alias", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the alias-amplified document described in the block comment at the top of this
    /// class. The <c>steps</c> section is a single ordinary step, so the document is refused
    /// for its expansion and for nothing else.
    /// </summary>
    private static string AliasAmplifiedYaml()
    {
        var builder = new StringBuilder();

        builder.AppendLine("steps:");
        builder.AppendLine("  - id: s1");
        builder.AppendLine("    type: http.rest");
        builder.AppendLine("    target: api");
        builder.AppendLine("    method: GET");
        builder.AppendLine("    path: /health");

        builder.Append("l0: &l0 \"").Append('x', LeafScalarChars).AppendLine("\"");

        for (var level = 1; level <= AliasLevels; level++)
        {
            builder.Append('l').Append(level).Append(": &l").Append(level).Append(" [");
            for (var alias = 0; alias < AliasFanout; alias++)
            {
                if (alias > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("*l").Append(level - 1);
            }

            builder.AppendLine("]");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the directory
    /// containing <c>vouchfx.sln</c> — the repo root. Mirrors
    /// <c>SchemaAcceptedCorpusTests.FindRepoRoot</c>.
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
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').");
    }
}
