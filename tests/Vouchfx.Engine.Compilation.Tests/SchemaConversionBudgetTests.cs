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
//   • The refusal arrives, carries an actionable diagnostic, and is BOUNDED — it never
//     materialises more than a small multiple of the budget. That allocation assertion is
//     what fails if the implementation ever regresses to building the whole string and
//     measuring its length afterwards.
//   • A legitimate suite — including the largest real .e2e.yaml in this repository — is
//     unaffected.
//   • The refusal reaches the caller as SchemaValidationResult.Invalid through BOTH entry
//     points, not as an escaping exception, and the pre-existing empty-document diagnostic
//     is unchanged.
using System;
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
    // Four levels of ten aliases each over a 32,000-character leaf scalar. The leaf is emitted
    // once for the anchor and once per alias site at every level, so the occurrences are the
    // geometric sum 10^0 + … + 10^4 = 11,111 and the expansion is at least
    // 11,111 x 32,000 = 355,552,000 characters of JSON — the floor MinExpansionChars derives
    // below, and the figure the discrimination assertion rests on. Actually emitted, once
    // quoting and punctuation are counted: 355,599,043, recorded in ExpectedExpansionChars
    // because the byte-level accounting further down needs the exact number. That is 21.2x the
    // 16 Mi-character budget, and that ratio is what the allocation ceiling below rests on.
    //
    // The LEAF IS A LONG SCALAR RATHER THAN MORE LEVELS, and the choice is weighed on the two
    // axes that matter. A guarded run of EITHER shape emits about one budget before it trips —
    // that is what the guard bounds — but the shapes differ in what an emitted character costs
    // and in how far the UNGUARDED expansion overshoots the budget.
    //   • Cost. Measured here against the pinned YamlDotNet 16.3.0, timing the conversion's
    //     two steps alone (no registry build, no schema composition): this document's guarded
    //     refusal takes 591 / 481 / 528 ms over three runs, a seven-level all-sequence chain
    //     1,325 / 1,399 / 1,207 ms. One budget of tiny scalars visits far more nodes than one
    //     budget of a repeated 32 KB leaf.
    //   • Margin, which is the axis that decides whether this row can SEE a regression at all.
    //     This document overshoots the budget 21.2x, against the 8x the discrimination
    //     assertion in AssertBoundedRefusal requires. SchemaResources' own measurement table
    //     puts a seven-level all-sequence chain at 80,246,964 characters — 4.8x the budget,
    //     4.4x less margin, and BELOW that 8x: the comparator shape would not merely have less
    //     margin here, it would have too little to discriminate at all. That is the TABLE's
    //     variant of the same shape, at a different leaf length from the one the Cost bullet
    //     timed, so the two bullets are not measurements of one document; the totals are
    //     nowhere near comparable either way.
    // SEVEN LEVELS IS THE COMPARATOR, NOT THE SHAPE CLASS, and saying otherwise would overclaim.
    // Seven is the deepest instance SchemaResources measured and the only one within an order of
    // magnitude of this document. The chain multiplies by ten per level, so an EIGHTH level
    // reaches about 802,000,000 characters and takes the margin axis outright. What an added
    // level does NOT move is cost: a guarded run of either shape stops at the budget, as the
    // paragraph above says, so the emitting work is the same at any depth. Cost is therefore the
    // axis that decides, the long leaf wins it at every depth, and its margin — 21.2x against a
    // ceiling that admits 8x — is already sufficient rather than merely larger.

    private const int AliasLevels = 4;
    private const int AliasFanout = 10;
    private const int LeafScalarChars = 32_000;

    /// <summary>
    /// One budget's worth of UTF-16 output, in bytes — the floor every correct
    /// implementation pays, because the guard only trips once the budget has been emitted.
    /// </summary>
    /// <remarks>
    /// READ FROM THE ENGINE, not mirrored. <c>SchemaResources.MaxJsonChars</c> is
    /// <see langword="internal"/> and this assembly holds the <c>InternalsVisibleTo</c>
    /// grant, so there is no second copy of the figure to drift out of step with the first.
    /// The claim the PASS side of these rows makes is scale-free — that a refusal allocates a
    /// small multiple of the conversion's OWN budget rather than a multiple of the document's
    /// full expansion — and it stays true at whatever the budget is set to. The FAIL side is
    /// not scale-free, which is why <see cref="AssertBoundedRefusal"/> asserts the relation
    /// between this figure and <see cref="MinExpansionChars"/> instead of describing it;
    /// see <see cref="RefusalAllocationCeilingBytes"/>'s remarks. The ABSOLUTE figure is
    /// pinned separately, by <see cref="AssertBudgetRefusal"/>'s assertion on the grouped
    /// <c>16,777,216 characters</c> the engine's own diagnostic prints.
    /// </remarks>
    private const long BudgetBytes = 2L * SchemaResources.MaxJsonChars;

    /// <summary>
    /// The characters this document's step 2 emits when NOTHING bounds it — the quantity the
    /// ceiling below has to stay under to keep discriminating.
    /// </summary>
    /// <remarks>
    /// Measured against the pinned YamlDotNet 16.3.0 by deserialising this exact document —
    /// 32,346 characters of YAML on a CRLF host and 32,335 on an LF one, since the eleven
    /// <c>AppendLine</c> calls in <see cref="AliasAmplifiedYaml"/> emit
    /// <c>Environment.NewLine</c>; otherwise byte-for-byte what that method builds — and
    /// serialising the graph through a counting <c>TextWriter</c> with no budget, which is the
    /// serialise-then-measure shape these rows exist to catch. The EMITTED figure is a property
    /// of the DOCUMENT and of nothing else: <c>AliasLevels</c>, <c>AliasFanout</c> and
    /// <c>LeafScalarChars</c> fix it, the budget does not enter it, and neither does the host's
    /// newline, which the deserialiser consumes and the emitter never reproduces. That
    /// independence is precisely why it and the ceiling can drift apart, and why the relation
    /// between them is asserted.
    /// </remarks>
    /// <seealso cref="MinExpansionChars"/>
    private const long ExpectedExpansionChars = 355_599_043;

    /// <summary>
    /// How many times the leaf scalar is emitted when nothing bounds the expansion, DERIVED
    /// from the three constants that build the document rather than measured.
    /// </summary>
    /// <remarks>
    /// The alias graph is a complete <see cref="AliasFanout"/>-ary tree of depth
    /// <see cref="AliasLevels"/> whose every node is the same leaf: level 0 is the anchor
    /// itself, emitted once, and level <c>k</c> contributes <c>AliasFanout^k</c> sites. So the
    /// occurrences are the geometric sum over <c>k = 0 … AliasLevels</c> — 11,111 at the shipped
    /// fanout 10 and levels 4. Computed rather than written down so that changing either
    /// constant carries through.
    /// </remarks>
    private static readonly long LeafOccurrences = ComputeLeafOccurrences();

    /// <summary>
    /// A LOWER BOUND on <see cref="ExpectedExpansionChars"/>, derived structurally — 355,552,000
    /// characters as shipped, 47,043 below the measured figure, the difference being the quoting,
    /// punctuation and keys the derivation deliberately does not model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This, not <see cref="ExpectedExpansionChars"/>, is what
    /// <see cref="AssertBoundedRefusal"/>'s discrimination assertion compares against the
    /// ceiling</strong>, and the reason is that the measured constant has no mechanical tie to
    /// the document. It was hand-taken, so it moves only when someone re-measures it: raising
    /// the ceiling makes the inequality fail (the direction that has actually occurred), but
    /// SHRINKING the document does not. Drop <see cref="LeafScalarChars"/> to 3,200 and the real
    /// expansion falls to about 35.6 M characters — comfortably under a ceiling that admits
    /// 134 M — while a stale 355,599,043 still satisfies the comparison and both refusal rows go
    /// green with a serialise-then-measure regression present. That is the exact silent pass the
    /// assertion exists to prevent, so the assertion may not depend on a figure a human has to
    /// remember to update.
    /// </para>
    /// <para>
    /// Being a floor rather than the exact count is harmless here and load-bearing in the right
    /// direction: the assertion needs the document to out-expand the ceiling, and proving that a
    /// guaranteed MINIMUM does so is strictly stronger than proving it of a measurement.
    /// <see cref="ExpectedExpansionChars"/> stays for the byte-level accounting in
    /// <see cref="RefusalAllocationCeilingBytes"/>'s remarks, which needs the exact figure.
    /// </para>
    /// </remarks>
    private static readonly long MinExpansionChars = LeafOccurrences * LeafScalarChars;

    /// <summary>
    /// Ceiling on the bytes a refusal may allocate on the calling thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Allocation, not wall-clock.</strong> The property the guard actually has is
    /// that it stops as the character count crosses the budget, so the material it ever
    /// materialises is bounded. A timer observes that only through host speed, and on
    /// GitHub Actions it observed host speed instead: these two rows failed a 4,000 ms
    /// ceiling on a contended shared runner at 6,310 ms (composed) and 4,111 ms (root) — run
    /// 34324860152, head <c>eafa26b</c> — with the guard working correctly in both cases.
    /// Locally the same two rows have measured anywhere between 419 ms and 810 ms across four
    /// readings on one machine: 601 / 643 ms and 799 / 705 ms in two full runs of this
    /// assembly, 570 / 419 ms in a filtered run of this class alone, and 810 / 621 ms when the
    /// CI failure was first investigated. That spread — nearly 2x on ONE unloaded machine, and
    /// 10x against the runner — is the argument rather than an inconsistency to tidy away.
    /// The smaller 591 / 481 / 528 ms quoted at the head of this file is a different SCOPE, not
    /// a fourth reading of the same one: it times the CONVERSION alone, where a row
    /// additionally builds the provider registry and, on the composed path, composes the
    /// schema. <see cref="GC.GetAllocatedBytesForCurrentThread"/> has no such dependence: it
    /// counts bytes allocated rather than bytes live, so neither GC mode nor collection
    /// timing moves it, and it is per-THREAD, so concurrent work on a loaded agent cannot
    /// reach it.
    /// </para>
    /// <para>
    /// <strong>Measured</strong> against the pinned YamlDotNet 16.3.0, driving the two
    /// public entry points these rows drive. As shipped: 34,138,160 / 34,188,512 /
    /// 34,138,160 bytes through the root validator and 34,139,312 / 34,138,160 /
    /// 34,140,904 through the composed one — 1.017-1.019x <see cref="BudgetBytes"/>, a
    /// spread of 0.15%. The same code with step 2 replaced by "serialise the graph to a
    /// string, then check its length": 1,429,159,144 / 1,429,164,552 / 1,429,166,144 bytes
    /// — 42.59x <see cref="BudgetBytes"/>, a spread of 0.0005%. A 42x separation between
    /// two figures that each wander by under a fifth of one percent. Re-measured when the
    /// multiplier was raised from an earlier draft's 4x to 8x, to show that raising it moved
    /// nothing it measures: 34,176,192 and 34,145,776 bytes, inside the same band.
    /// </para>
    /// <para>
    /// <strong>Both figures are accounted for arithmetically</strong>, which is also the
    /// evidence that a per-thread counter sees the whole emit. 2 bytes x 16,777,216
    /// budgeted characters is 33,554,432 — 98.3% of the shipped measurement, the remainder
    /// being the parse of a 32 KB document. <see cref="ExpectedExpansionChars"/> emitted
    /// characters x 2 bytes x two copies (the StringBuilder's chunks and
    /// <c>ToString()</c>'s) is 1,422,396,172 — 99.5% of the regressed measurement. Neither
    /// residual leaves room for a slice of the serialiser running off the calling thread,
    /// where the counter could not see it.
    /// </para>
    /// <para>
    /// <strong>8x is headroom, not a fitted threshold.</strong> Slack is counted here the one
    /// way throughout — ADDITIVELY, as copies of the budget left over above the shipped 1.02x,
    /// which is the unit the benign changes below are themselves counted in. On that unit 8x
    /// leaves just under 7x of slack, where the 4x an earlier draft of this row used left just
    /// under 3x, and 3x is inside the reach of a BENIGN change: a <c>StringBuilder</c>
    /// reimplemented as a doubling array costs about one extra copy of the budget, a YamlDotNet
    /// upgrade that copies each emitted scalar about one more, and the two together — 1.02x plus
    /// two, so about 3.02x in total — cleared 4x only marginally. Downwards, ANY implementation
    /// that materialises this document's full expansion pays at least 2 bytes x
    /// <see cref="ExpectedExpansionChars"/> = 21.2x <see cref="BudgetBytes"/> even holding
    /// zero copies, so the cheapest conceivable serialise-then-measure regression still lands
    /// 2.65x above this ceiling; the actual one, at 42.59x, lands 5.3x above it.
    /// </para>
    /// <para>
    /// <strong>The two sides do not scale alike, which is why
    /// <see cref="AssertBoundedRefusal"/> asserts the relation rather than trusting this
    /// paragraph.</strong> A correct refusal costs about one budget at any budget, so the PASS
    /// side is scale-free; the FAIL side is not, because this ceiling tracks
    /// <see cref="SchemaResources.MaxJsonChars"/> while the document's expansion does not.
    /// Raise the budget to <see cref="MinExpansionChars"/> / 8 = 44,444,000 characters or
    /// beyond (about 42.4 Mi, 2.65x today's), or raise the multiplier above 21, and the
    /// CHEAPEST CONCEIVABLE serialise-then-measure regression fits under this ceiling with BOTH
    /// rows green — SILENTLY, where the wall-clock ceiling this replaced would at least have
    /// failed loudly. The regression actually measured holds two copies and so needs the
    /// multiplier above 42; the assertion fires at the cheaper of the two ON PURPOSE, because
    /// how many copies a future regression happens to hold is not something this row may assume.
    /// </para>
    /// <para>
    /// The bound is one-sided ON PURPOSE. An implementation that refused earlier and more
    /// cheaply than this one would be an improvement and must not redden these rows; what
    /// must never pass is one that builds the expansion before measuring it.
    /// </para>
    /// </remarks>
    private const long RefusalAllocationCeilingBytes = 8 * BudgetBytes;

    // ── Refusal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// An alias-amplified document is refused through <see cref="YamlSchemaValidator"/> with
    /// a diagnostic that names the budget and names anchors/aliases as the cause — and the
    /// refusal is bounded, not the result of building the whole 355-million-character string.
    /// </summary>
    [Fact]
    public void Validate_AliasAmplifiedDocument_IsRefusedInBoundedMemoryByTheRootSchemaValidator()
    {
        var yaml = AliasAmplifiedYaml();

        var (result, allocatedBytes) = MeasureRefusal(() => YamlSchemaValidator.Validate(yaml));

        AssertBudgetRefusal(result);
        AssertBoundedRefusal(allocatedBytes);
    }

    /// <summary>
    /// The same document is refused identically through <see cref="SchemaComposer"/> — the
    /// composed path an author's suite actually hits.
    /// </summary>
    [Fact]
    public void Validate_AliasAmplifiedDocument_IsRefusedInBoundedMemoryByTheComposedValidator()
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(HttpRestProvider).Assembly });
        var yaml = AliasAmplifiedYaml();

        // The registry is built OUTSIDE the measured window on purpose: its allocations
        // belong to test setup, not to the refusal, and billing them here would blunt the
        // bound for no gain.
        var (result, allocatedBytes) = MeasureRefusal(() => SchemaComposer.Validate(registry, yaml));

        AssertBudgetRefusal(result);
        AssertBoundedRefusal(allocatedBytes);
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
    /// The geometric sum behind <see cref="LeafOccurrences"/>:
    /// <c>AliasFanout^0 + … + AliasFanout^AliasLevels</c>.
    /// </summary>
    /// <remarks>
    /// Summed in a loop rather than written as a closed form because the closed form needs
    /// <c>Math.Pow</c> — a <see langword="double"/> round trip on a figure the assertions then
    /// compare exactly. The loop is exact in <see langword="long"/> and cheap: it runs
    /// <see cref="AliasLevels"/> + 1 times, once, at type initialisation.
    /// </remarks>
    private static long ComputeLeafOccurrences()
    {
        var occurrences = 0L;
        var sitesAtThisLevel = 1L;

        for (var level = 0; level <= AliasLevels; level++)
        {
            occurrences += sitesAtThisLevel;
            sitesAtThisLevel *= AliasFanout;
        }

        return occurrences;
    }

    /// <summary>
    /// Runs <paramref name="validate"/> and reports what it allocated on the calling thread.
    /// </summary>
    /// <remarks>
    /// Both entry points are synchronous from the call through YamlDotNet's deserialiser and
    /// emitter and back, so the whole conversion runs on this thread and a per-thread counter
    /// captures all of it — the byte-level accounting in
    /// <see cref="RefusalAllocationCeilingBytes"/>'s remarks is the evidence for that, not
    /// the assumption behind it.
    /// </remarks>
    private static (SchemaValidationResult Result, long AllocatedBytes) MeasureRefusal(
        Func<SchemaValidationResult> validate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = validate();
        var after = GC.GetAllocatedBytesForCurrentThread();

        return (result, after - before);
    }

    /// <summary>
    /// Asserts that the refusal stayed inside <see cref="RefusalAllocationCeilingBytes"/> —
    /// the row that fails if the conversion ever goes back to building the whole expansion —
    /// and, before that, that the ceiling and the document are still far enough apart for the
    /// first assertion to mean anything.
    /// </summary>
    private static void AssertBoundedRefusal(long allocatedBytes)
    {
        var allocated = allocatedBytes.ToString("N0", CultureInfo.InvariantCulture);
        var ceiling = RefusalAllocationCeilingBytes.ToString("N0", CultureInfo.InvariantCulture);
        var multiple = (allocatedBytes / (double)BudgetBytes).ToString(
            "F1", CultureInfo.InvariantCulture);
        var budgetChars = SchemaResources.MaxJsonChars.ToString("N0", CultureInfo.InvariantCulture);

        // FIRST, the meta-assertion: this row can only SEE a serialise-then-measure regression
        // while the document out-expands the ceiling, and that is a relation between a figure
        // that tracks the budget and one that does not. A regression pays at least one UTF-16
        // copy of the whole expansion, so the comparison is in CHARACTERS — the ceiling halved.
        // Asserted rather than described because both rows would otherwise go GREEN with the
        // regression present; see RefusalAllocationCeilingBytes' remarks for the arithmetic.
        //
        // The left-hand side is the DERIVED floor, never the hand-measured
        // ExpectedExpansionChars. The two are within 0.02% of each other today, but the measured
        // constant has no mechanical tie to AliasLevels / AliasFanout / LeafScalarChars, so it
        // catches a raised ceiling and misses a SHRUNK document — the one direction in which a
        // stale figure would keep this comparison satisfied while the real expansion had already
        // fallen under the ceiling. MinExpansionChars moves with the document by construction.
        Assert.True(
            MinExpansionChars > RefusalAllocationCeilingBytes / 2,
            "The amplifying document no longer out-expands the ceiling by enough for this row to "
            + "discriminate: the CHEAPEST CONCEIVABLE serialise-then-measure regression - one "
            + "holding no copy beyond the emitted string itself - would now fit under it, and "
            + "both refusal rows would pass with that regression present. (The regression "
            + "actually measured holds two copies and would still be caught; this assertion "
            + "fires at the cheaper of the two deliberately, because how many copies a future "
            + "regression holds is not something this row may assume.) The document emits at "
            + "least " + MinExpansionChars.ToString("N0", CultureInfo.InvariantCulture)
            + " characters - " + LeafOccurrences.ToString("N0", CultureInfo.InvariantCulture)
            + " leaf occurrences of " + LeafScalarChars.ToString("N0", CultureInfo.InvariantCulture)
            + " characters, derived from AliasLevels and AliasFanout - against a ceiling of "
            + ceiling + " bytes, which admits "
            + (RefusalAllocationCeilingBytes / 2).ToString("N0", CultureInfo.InvariantCulture)
            + " characters of UTF-16. Either the budget (" + budgetChars
            + " characters) or the ceiling's multiple of it has been raised past what the "
            + "document can out-run, or the document itself has been shrunk. Raise AliasLevels, "
            + "AliasFanout or LeafScalarChars - MinExpansionChars follows them automatically, "
            + "though ExpectedExpansionChars must still be re-measured for the byte accounting "
            + "in RefusalAllocationCeilingBytes' remarks - or lower the multiple. Do not simply "
            + "delete this assertion, because nothing else in this file can tell the two cases "
            + "apart.");

        // SECOND, the one check that can be made on the hand-measured figure. Nothing mechanical
        // keeps ExpectedExpansionChars in step with the document, but it can never legitimately
        // sit BELOW what the constants guarantee — a value that does is a mis-measurement or a
        // stale edit, and every byte-accounting figure quoting it is then wrong too.
        Assert.True(
            ExpectedExpansionChars >= MinExpansionChars,
            "ExpectedExpansionChars ("
            + ExpectedExpansionChars.ToString("N0", CultureInfo.InvariantCulture)
            + " characters) is below the expansion this document's own constants guarantee ("
            + MinExpansionChars.ToString("N0", CultureInfo.InvariantCulture)
            + " characters), so it cannot be a measurement of this document. Re-measure it "
            + "against the current AliasLevels / AliasFanout / LeafScalarChars before trusting "
            + "the byte accounting in RefusalAllocationCeilingBytes' remarks, which quotes it.");

        Assert.True(
            allocatedBytes < RefusalAllocationCeilingBytes,
            $"The refusal allocated {allocated} bytes on the calling thread — {multiple}x the "
            + $"{budgetChars}-character budget's own UTF-16 size, against a ceiling of "
            + $"{ceiling} bytes. The guard is supposed to stop the expansion AS the character "
            + "count crosses the budget, so a refusal costs about ONE budget's worth of "
            + "output and nothing more; several times that means the conversion materialised "
            + "far more of the document than the budget allows. Look first at step 2 of "
            + "SchemaResources.ConvertYamlToJsonDocument: the regression this row exists to "
            + "catch is serialising the whole object graph to a string and measuring its "
            + "length afterwards, instead of writing THROUGH the counting TextWriter that "
            + "refuses mid-emit. That regression still throws the right diagnostic, so the "
            + "sibling assertions cannot see it — only this one can. A slow or contended "
            + "agent is not an explanation: this is an allocation count, not a timing.");
    }

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
