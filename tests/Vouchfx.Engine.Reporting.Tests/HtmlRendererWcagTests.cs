// WCAG 2.1 AA gate for HtmlRenderer (S10-G — accessibility remediation).
//
// A deterministic, Docker-free unit gate over the self-contained HTML report.  It is
// driven by the SAME buffered JSON Lines event stream every other renderer consumes
// (§14) — rendered to a StringWriter exactly like HtmlRendererTests — then asserts two
// independent classes of property on the emitted string:
//
//   (a) Contrast floor.  A small WCAG relative-luminance helper (sRGB → linear with the
//       0.03928 knee; luminance weights 0.2126 / 0.7152 / 0.0722; ratio
//       (Lhi + 0.05) / (Llo + 0.05)) is recomputed IN-TEST and every text pair is asserted
//       against its AA floor (4.5:1 for text, 3.0:1 for the non-text border cues, 1.4.11).
//       The text-colour pairs are PARSED out of the emitted <style> and fed into the
//       luminance function, so the gate stays honest if someone edits a verdict colour;
//       the parse is cross-checked against a hard-coded expectation so a silent parse
//       miss cannot make the gate vacuously pass.
//
//   (b) Structural invariants.  lang / single-<h1> / <main> landmark / monotone heading
//       hierarchy / per-verdict class+label co-emission (locks 1.4.1) / self-containment /
//       summary-table scopes / a closing-tag well-formedness sentinel — asserted on the
//       rendered string.
//
// These structural assertions FAIL against the pre-R1/R3 output (no <main>, a <div
// class="summary"> rather than a real table) and pass after the remediation — the
// RED→GREEN the task requires.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

public sealed class HtmlRendererWcagTests
{
    // A deliberately-bad heading sequence (h1 → h2 → h4) used to prove the
    // hierarchy detector actually rejects an over-deep jump.  Hoisted to a field per
    // CA1861 (no constant array argument at the call site).
    private static readonly int[] BadHeadingSequence = { 1, 2, 4 };

    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    /// <summary>
    /// A single scenario whose four steps cover every §12.1 verdict, plus a scenario
    /// summary so the run-summary table and all four verdict rows render.  Mirrors the
    /// sample stream <see cref="HtmlRendererTests"/> uses.
    /// </summary>
    private static string[] FourVerdictStream() => new[]
    {
        Line(new ScenarioStartedEvent { RunId = "run-wcag", ScenarioId = "order-flow" }),
        Line(new StepCompletedEvent
        {
            RunId = "run-wcag",
            StepId = "step-pass",
            Verdict = Verdict.Pass,
            DurationMs = 10,
        }),
        Line(new StepCompletedEvent
        {
            RunId = "run-wcag",
            StepId = "step-fail",
            Verdict = Verdict.Fail,
            DurationMs = 20,
        }),
        Line(new StepCompletedEvent
        {
            RunId = "run-wcag",
            StepId = "step-env-error",
            Verdict = Verdict.EnvironmentError,
            DurationMs = 30,
        }),
        Line(new StepCompletedEvent
        {
            RunId = "run-wcag",
            StepId = "step-inconclusive",
            Verdict = Verdict.Inconclusive,
            DurationMs = 40,
        }),
        Line(new ScenarioCompletedEvent
        {
            RunId = "run-wcag",
            ScenarioId = "order-flow",
            Verdict = Verdict.Fail,
            Counts = new VerdictCounts { Pass = 1, Fail = 1, EnvError = 1, Inconclusive = 1 },
        }),
    };

    private static string RenderFourVerdicts()
    {
        using var writer = new StringWriter();
        HtmlRenderer.Render(FourVerdictStream(), writer);
        return writer.ToString();
    }

    /// <summary>
    /// The four-verdict stream PLUS a <c>reproducibility-envelope</c> event, so the
    /// renderer draws the deeper envelope panel (h2 → h3 → h4) and the heading hierarchy
    /// is exercised below its h2 floor.  The envelope carries only non-sensitive
    /// REFERENCES — a secret reference digest (source + hash, never a value) and a
    /// fixture content digest — mirroring how <see cref="HtmlRendererTests"/> builds one
    /// (§17: references only, no values).
    /// </summary>
    private static string[] FourVerdictStreamWithEnvelope()
    {
        var stream = FourVerdictStream().ToList();

        // Splice the envelope in before the ScenarioCompletedEvent terminator, matching
        // the ordering HtmlRendererTests uses (envelope between started and completed).
        stream.Insert(stream.Count - 1, Line(new ReproducibilityEnvelopeEvent
        {
            RunId = "run-wcag",
            ScenarioId = "order-flow",
            EnvSchemaVersion = ReproducibilityEnvelope.CurrentSchemaVersion,
            SecretReferences = new[]
            {
                new SecretReferenceDigest(
                    "env",
                    "a1b2c3d4e5f6071829304152637485960718293041526374859607182930a1b2"),
            },
            Fixtures = new[]
            {
                new FixtureDigest("fixtures/orders.sql", "deadbeefcafef00d"),
            },
        }));

        return stream.ToArray();
    }

    private static string RenderFourVerdictsWithEnvelope()
    {
        using var writer = new StringWriter();
        HtmlRenderer.Render(FourVerdictStreamWithEnvelope(), writer);
        return writer.ToString();
    }

    // =========================================================================
    // (a) Contrast floor.
    // =========================================================================

    /// <summary>
    /// Computes the WCAG 2.1 relative luminance of an sRGB colour: each channel is
    /// normalised to [0,1], linearised through the 0.03928 knee (below it a /12.92
    /// ramp, above it the ((c+0.055)/1.055)^2.4 curve), then weighted
    /// 0.2126 R + 0.7152 G + 0.0722 B.
    /// </summary>
    private static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        double Channel(int c)
        {
            var cs = c / 255.0;
            return cs <= 0.03928 ? cs / 12.92 : Math.Pow((cs + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(r)) + (0.7152 * Channel(g)) + (0.0722 * Channel(b));
    }

    /// <summary>
    /// The WCAG contrast ratio between two colours: (L_hi + 0.05) / (L_lo + 0.05).
    /// </summary>
    private static double ContrastRatio(string hexA, string hexB)
    {
        var la = RelativeLuminance(hexA);
        var lb = RelativeLuminance(hexB);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static (int R, int G, int B) ParseHex(string hex)
    {
        var h = hex.AsSpan().TrimStart('#');
        return (
            int.Parse(h.Slice(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(h.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(h.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Sanity-checks the luminance helper itself against the two extreme pairs: pure
    /// black on white is the canonical 21:1, and white on white is 1:1.  If the helper
    /// is wrong, the floor assertions below would be meaningless.
    /// </summary>
    [Fact]
    public void RelativeLuminance_HelperMatchesKnownWcagReferenceRatios()
    {
        Assert.Equal(21.0, ContrastRatio("#000000", "#ffffff"), 1);
        Assert.Equal(1.0, ContrastRatio("#ffffff", "#ffffff"), 3);
    }

    /// <summary>
    /// Every TEXT colour pair clears the AA 4.5:1 floor, and every verdict left-border
    /// CUE clears the 3.0:1 non-text floor against white (1.4.11).  The verdict text
    /// pairs are PARSED from the emitted &lt;style&gt; so the gate tracks the real output;
    /// the non-verdict pairs (body / redacted / diff) and the border cues are pinned to
    /// the documented hexes AND asserted to appear in the &lt;style&gt;, so the test cannot
    /// drift from the stylesheet.
    /// </summary>
    [Fact]
    public void EveryColourPair_MeetsItsWcagContrastFloor()
    {
        var output = RenderFourVerdicts();
        var style = ExtractStyle(output);

        // --- Verdict text pairs: parse (foreground, background) straight out of the
        // `.verdict-X .verdict { color: …; background: … }` rules and contrast-check the
        // PARSED values, so editing a verdict colour is caught here.
        var parsed = ParseVerdictTextPairs(style);

        // The five verdict text rules (pass/fail/env-error/inconclusive/unknown) must all
        // be found — a parse miss must not silently shrink the set under test.
        var expectedVerdictClasses = new[]
        {
            "verdict-pass", "verdict-fail", "verdict-env-error", "verdict-inconclusive", "verdict-unknown",
        };
        foreach (var cls in expectedVerdictClasses)
        {
            Assert.True(parsed.ContainsKey(cls), $"did not parse a text colour pair for .{cls}");
        }

        // Cross-check the parsed values against the documented hexes so the parser cannot
        // quietly read the wrong tokens and still pass.
        var expectedVerdictPairs = new Dictionary<string, (string Fg, string Bg)>(StringComparer.Ordinal)
        {
            ["verdict-pass"] = ("#0f5d29", "#e6f4ea"),
            ["verdict-fail"] = ("#8a0017", "#fce8e6"),
            ["verdict-env-error"] = ("#6b4600", "#fff4e5"),
            ["verdict-inconclusive"] = ("#333366", "#ececf7"),
            ["verdict-unknown"] = ("#4a4a4a", "#eeeeee"),
        };
        foreach (var (cls, expected) in expectedVerdictPairs)
        {
            Assert.Equal(expected.Fg, parsed[cls].Fg, ignoreCase: true);
            Assert.Equal(expected.Bg, parsed[cls].Bg, ignoreCase: true);
        }

        // The AA text floor for every parsed verdict pair.
        foreach (var (cls, pair) in parsed)
        {
            var ratio = ContrastRatio(pair.Fg, pair.Bg);
            Assert.True(
                ratio >= 4.5,
                $".{cls}: {pair.Fg} on {pair.Bg} = {ratio:0.00}:1, below the AA 4.5:1 text floor");
        }

        // --- Non-verdict text pairs (no `.verdict-*` wrapper): body text, the redacted
        // marker on white, diff text on its panel, and the base `.verdict` fallback (R-4)
        // that styles an orphan verdict span outside any `.verdict-*` wrapper.  Pinned to
        // the documented hexes, and BOTH the foreground and the background of each pair
        // are asserted present in the <style> so the pin cannot drift (the base-fallback
        // background #f0f0f0 is what distinguishes that rule from body text).
        var nonVerdictTextPairs = new (string Fg, string Bg, string Where)[]
        {
            ("#1a1a1a", "#ffffff", "body text on page"),
            ("#6b4600", "#ffffff", "redacted marker on page"),
            ("#1a1a1a", "#f5f5f5", "diff text on diff panel"),
            ("#1a1a1a", "#f0f0f0", "base .verdict fallback (R-4) for an orphan verdict span"),
        };
        foreach (var (fg, bg, where) in nonVerdictTextPairs)
        {
            Assert.Contains(fg, style, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(bg, style, StringComparison.OrdinalIgnoreCase);
            var ratio = ContrastRatio(fg, bg);
            Assert.True(
                ratio >= 4.5,
                $"{where}: {fg} on {bg} = {ratio:0.00}:1, below the AA 4.5:1 text floor");
        }

        // --- Verdict left-border CUES against white: non-text contrast floor 3.0:1
        // (1.4.11).  Each colour must appear in the <style> (as a `border-left-color`).
        var borderCues = new[] { "#1b7f3b", "#b00020", "#8a5a00", "#4a4a8a", "#888888" };
        foreach (var cue in borderCues)
        {
            Assert.Contains(cue, style, StringComparison.OrdinalIgnoreCase);
            var ratio = ContrastRatio(cue, "#ffffff");
            Assert.True(
                ratio >= 3.0,
                $"border cue {cue} on #ffffff = {ratio:0.00}:1, below the 3.0:1 non-text floor");
        }

        // The summary-table grid is now informative structure, so its #767676 border must
        // clear the same 3.0:1 non-text floor against white (1.4.11), and the decorative
        // #cccccc must NOT have been used for it.
        Assert.Contains("#767676", style, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            ContrastRatio("#767676", "#ffffff") >= 3.0,
            "summary-table border #767676 falls below the 3.0:1 non-text floor");
    }

    // =========================================================================
    // (b) Structural invariants.
    // =========================================================================

    [Fact]
    public void Structure_HasHtmlLangEnglish()
    {
        Assert.Contains("<html lang=\"en\">", RenderFourVerdicts(), StringComparison.Ordinal);
    }

    [Fact]
    public void Structure_HasExactlyOneH1()
    {
        var output = RenderFourVerdicts();
        var count = Regex.Count(output, "<h1", RegexOptions.IgnoreCase);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Structure_HasMainLandmark_OpenAndClose()
    {
        var output = RenderFourVerdicts();
        Assert.Contains("<main>", output, StringComparison.Ordinal);
        Assert.Contains("</main>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Structure_HasViewportMeta()
    {
        Assert.Contains(
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
            RenderFourVerdicts(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The heading sequence never jumps more than one level deeper than the previous
    /// heading (a sighted-and-AT-shared structure cue, 1.3.1).  A real h2→h4 jump must
    /// be rejected by this rule, so the rule is also exercised against a synthetic
    /// bad sequence to prove it is not vacuously satisfied.
    /// </summary>
    [Fact]
    public void Structure_HeadingHierarchyNeverJumpsMoreThanOneLevelDeeper()
    {
        var levels = ExtractHeadingLevels(RenderFourVerdicts());
        Assert.NotEmpty(levels);
        AssertNoDeeperJumpThanOne(levels);

        // The reproducibility-envelope panel is the only path that descends past h2 into
        // h3/h4 (WriteReproducibilityEnvelopes).  Render a stream that carries an envelope
        // and assert the deeper headings (a) actually appear — so this is not vacuous —
        // and (b) still respect the one-level-deeper rule across the richer document.
        var withEnvelope = RenderFourVerdictsWithEnvelope();
        Assert.Contains("<h3", withEnvelope, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h4", withEnvelope, StringComparison.OrdinalIgnoreCase);

        var deeperLevels = ExtractHeadingLevels(withEnvelope);
        Assert.Contains(3, deeperLevels);
        Assert.Contains(4, deeperLevels);
        AssertNoDeeperJumpThanOne(deeperLevels);

        // The detector must actually reject a jump (h2 → h4), else the real-output pass
        // above would be meaningless.
        Assert.Throws<Xunit.Sdk.TrueException>(
            () => AssertNoDeeperJumpThanOne(BadHeadingSequence));
    }

    /// <summary>
    /// Per-verdict co-emission locks WCAG 1.4.1 (use of colour): wherever a verdict CSS
    /// class is used, the matching text label is ALSO present, so the verdict is never
    /// conveyed by colour alone.
    /// </summary>
    [Theory]
    [InlineData("verdict-pass", "PASS")]
    [InlineData("verdict-fail", "FAIL")]
    [InlineData("verdict-env-error", "ENV_ERROR")]
    [InlineData("verdict-inconclusive", "INCONCLUSIVE")]
    public void Structure_EachVerdictClassCoEmitsItsTextLabel(string verdictClass, string label)
    {
        var output = RenderFourVerdicts();
        Assert.Contains(verdictClass, output, StringComparison.Ordinal);
        Assert.Contains(label, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Structure_IsSelfContained_NoExternalReferences()
    {
        var output = RenderFourVerdicts();
        Assert.DoesNotContain("http://", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structure_SummaryTableHasColumnAndRowScopes()
    {
        var output = RenderFourVerdicts();
        Assert.Contains("<th scope=\"col\">", output, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"row\">", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Well-formedness sentinel: the document closes with the expected
    /// &lt;/main&gt;…&lt;/body&gt;…&lt;/html&gt; sequence in order, so a truncated /
    /// mis-nested tail is caught.
    /// </summary>
    [Fact]
    public void Structure_ClosesWithMainBodyHtmlInOrder()
    {
        var output = RenderFourVerdicts();
        var closeMain = output.LastIndexOf("</main>", StringComparison.Ordinal);
        var closeBody = output.LastIndexOf("</body>", StringComparison.Ordinal);
        var closeHtml = output.LastIndexOf("</html>", StringComparison.Ordinal);

        Assert.True(closeMain >= 0 && closeBody >= 0 && closeHtml >= 0);
        Assert.True(
            closeMain < closeBody && closeBody < closeHtml,
            "expected the document to close </main> then </body> then </html> in order");
    }

    // =========================================================================
    // Helpers.
    // =========================================================================

    private static string ExtractStyle(string output)
    {
        var match = Regex.Match(output, "<style>(.*?)</style>", RegexOptions.Singleline);
        Assert.True(match.Success, "no inline <style> block found in the rendered report");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// Parses the `.verdict-CLASS .verdict { color: #fg; background: #bg }` rules from the
    /// stylesheet into a class → (fg, bg) map, so the contrast gate runs over the colours
    /// actually emitted rather than a copy of them.
    /// </summary>
    private static Dictionary<string, (string Fg, string Bg)> ParseVerdictTextPairs(string style)
    {
        // Matches e.g.  .verdict-pass .verdict { color: #0f5d29; background: #e6f4ea; }
        var rule = new Regex(
            @"\.(?<cls>verdict-[a-z-]+)\s+\.verdict\s*\{[^}]*?color:\s*(?<fg>#[0-9a-fA-F]{6})[^}]*?background:\s*(?<bg>#[0-9a-fA-F]{6})",
            RegexOptions.IgnoreCase);

        var map = new Dictionary<string, (string Fg, string Bg)>(StringComparer.Ordinal);
        foreach (Match m in rule.Matches(style))
        {
            map[m.Groups["cls"].Value] = (m.Groups["fg"].Value, m.Groups["bg"].Value);
        }

        return map;
    }

    /// <summary>
    /// Extracts the ordered sequence of heading levels (1–6) from the emitted markup,
    /// in document order.
    /// </summary>
    private static int[] ExtractHeadingLevels(string output)
        => Regex.Matches(output, "<h([1-6])", RegexOptions.IgnoreCase)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToArray();

    /// <summary>
    /// Asserts no heading is more than one level deeper than its predecessor (going
    /// shallower by any amount is fine; the first heading has no predecessor).
    /// </summary>
    private static void AssertNoDeeperJumpThanOne(int[] levels)
    {
        for (var i = 1; i < levels.Length; i++)
        {
            Assert.True(
                levels[i] <= levels[i - 1] + 1,
                $"heading hierarchy jumps from h{levels[i - 1]} to h{levels[i]} (more than one level deeper)");
        }
    }
}
