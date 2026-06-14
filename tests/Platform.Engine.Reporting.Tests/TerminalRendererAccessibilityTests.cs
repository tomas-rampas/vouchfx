// Accessibility tests for TerminalRenderer (S10-G-03a).
//
// The four §12.1 verdicts (Pass / Fail / Environment error / Inconclusive) must be
// distinguishable WITHOUT relying on colour alone (WCAG 1.4.1). The renderer guarantees
// this two ways:
//   1. ALWAYS: the verdict's distinct TEXT token (PASS / FAIL / ENV_ERROR / INCONCLUSIVE).
//      This is the unconditional, colour-free, screen-reader cue.
//   2. OPTIONALLY (decorate=true): a per-verdict ASCII SHAPE glyph prefix ([+] / [x] /
//      [!] / [?]) AND an ANSI colour wrap around the verdict token. The glyph is a shape
//      cue independent of colour, so a colour-blind user still sees a distinct glyph+token.
//
// Default = plain (decorate=false): byte-identical to the pre-S10-G-03a output, so all
// existing TerminalRenderer tests stay green (see TerminalRendererTests). These tests pin
// the NEW decorate behaviour and the plain-mode accessibility guarantees.

using System;
using System.IO;
using System.Linq;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Reporting;
using Xunit;

namespace Platform.Engine.Reporting.Tests;

public sealed class TerminalRendererAccessibilityTests
{
    // The ANSI escape (ESC, U+001B) introduces every SGR colour sequence.
    private static readonly string Esc = ((char)0x1b).ToString();

    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    /// <summary>
    /// A four-verdict buffer: one step-completed event per §12.1 verdict, each with a
    /// distinct step id so the rendered line is unambiguous.
    /// </summary>
    private static string[] FourVerdictBuffer() => new[]
    {
        Line(new StepCompletedEvent
        {
            RunId = "run-acc",
            StepId = "step-pass",
            Verdict = Verdict.Pass,
            DurationMs = 1,
        }),
        Line(new StepCompletedEvent
        {
            RunId = "run-acc",
            StepId = "step-fail",
            Verdict = Verdict.Fail,
            DurationMs = 2,
        }),
        Line(new StepCompletedEvent
        {
            RunId = "run-acc",
            StepId = "step-env",
            Verdict = Verdict.EnvironmentError,
            DurationMs = 3,
        }),
        Line(new StepCompletedEvent
        {
            RunId = "run-acc",
            StepId = "step-inc",
            Verdict = Verdict.Inconclusive,
            DurationMs = 4,
        }),
    };

    // -------------------------------------------------------------------------
    // Plain mode (decorate=false): WCAG-by-text, no colour, no glyph.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_PlainMode_ContainsAllFourDistinctTextTokens_AndNoColourOrGlyph()
    {
        using var writer = new StringWriter();
        TerminalRenderer.Render(FourVerdictBuffer(), writer, decorate: false);
        var output = writer.ToString();

        // The four distinct verdict TEXT tokens are present — the colour-free, screen-reader
        // accessibility guarantee (WCAG 1.4.1).
        Assert.Contains("PASS", output, StringComparison.Ordinal);
        Assert.Contains("FAIL", output, StringComparison.Ordinal);
        Assert.Contains("ENV_ERROR", output, StringComparison.Ordinal);
        Assert.Contains("INCONCLUSIVE", output, StringComparison.Ordinal);

        // No ANSI escape sequence whatsoever in plain mode.
        Assert.DoesNotContain(Esc, output, StringComparison.Ordinal);

        // None of the shape glyphs in plain mode.
        Assert.DoesNotContain("[+]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[x]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[!]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[?]", output, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Decorated mode (decorate=true): glyph + colour per verdict.
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_DecoratedMode_EachVerdictCarriesItsDistinctGlyphAndAnAnsiColourWithReset()
    {
        using var writer = new StringWriter();
        TerminalRenderer.Render(FourVerdictBuffer(), writer, decorate: true);
        var output = writer.ToString();

        // Each verdict line carries its DISTINCT shape glyph (independent of colour).
        Assert.Contains("[+]", output, StringComparison.Ordinal); // Pass
        Assert.Contains("[x]", output, StringComparison.Ordinal); // Fail
        Assert.Contains("[!]", output, StringComparison.Ordinal); // Environment error
        Assert.Contains("[?]", output, StringComparison.Ordinal); // Inconclusive

        // Each verdict token is wrapped in an ANSI colour that is reset afterwards.
        // Green Pass / red Fail / yellow EnvError / blue Inconclusive.
        Assert.Contains($"{Esc}[32mPASS{Esc}[0m", output, StringComparison.Ordinal);
        Assert.Contains($"{Esc}[31mFAIL{Esc}[0m", output, StringComparison.Ordinal);
        Assert.Contains($"{Esc}[33mENV_ERROR{Esc}[0m", output, StringComparison.Ordinal);
        Assert.Contains($"{Esc}[34mINCONCLUSIVE{Esc}[0m", output, StringComparison.Ordinal);

        // An ANSI reset follows every coloured token (no unterminated colour leaks onto a
        // following line / the prompt).
        Assert.Contains($"{Esc}[0m", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_DecoratedMode_GlyphsAreMutuallyDistinct_NotColourAlone()
    {
        using var writer = new StringWriter();
        TerminalRenderer.Render(FourVerdictBuffer(), writer, decorate: true);
        var output = writer.ToString();

        // The four glyphs are mutually distinct, so the verdicts are separable by SHAPE even
        // for a user who cannot perceive the colour difference (the core WCAG-1.4.1 belt-and-
        // braces: glyph PLUS text token, never colour alone).
        var glyphs = new[] { "[+]", "[x]", "[!]", "[?]" };
        Assert.Equal(glyphs.Length, glyphs.Distinct().Count());
        foreach (var glyph in glyphs)
        {
            Assert.Contains(glyph, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Render_DecoratedMode_GlyphIsAttachedToTheCorrectVerdictLine()
    {
        using var writer = new StringWriter();
        TerminalRenderer.Render(FourVerdictBuffer(), writer, decorate: true);
        var lines = writer.ToString().Split('\n');

        AssertLineForStepCarriesGlyph(lines, "step-pass", "[+]");
        AssertLineForStepCarriesGlyph(lines, "step-fail", "[x]");
        AssertLineForStepCarriesGlyph(lines, "step-env", "[!]");
        AssertLineForStepCarriesGlyph(lines, "step-inc", "[?]");
    }

    private static void AssertLineForStepCarriesGlyph(string[] lines, string stepId, string glyph)
    {
        var line = Array.Find(lines, l => l.Contains(stepId, StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.Contains(glyph, line!, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // The CORE accessibility guarantee: every verdict is distinguishable from
    // every other by the TEXT token ALONE (colour + glyph stripped).
    // -------------------------------------------------------------------------

    [Fact]
    public void Render_VerdictsAreDistinguishableByTextTokenAlone_WithColourAndGlyphStripped()
    {
        using var writer = new StringWriter();
        TerminalRenderer.Render(FourVerdictBuffer(), writer, decorate: true);

        // Strip ANSI colour AND glyph, leaving only the plain text a screen reader would read.
        var stripped = StripAnsi(writer.ToString())
            .Replace("[+]", string.Empty, StringComparison.Ordinal)
            .Replace("[x]", string.Empty, StringComparison.Ordinal)
            .Replace("[!]", string.Empty, StringComparison.Ordinal)
            .Replace("[?]", string.Empty, StringComparison.Ordinal);

        // The four tokens remain present and are mutually distinct strings, so a reader relying
        // on TEXT ALONE can still tell every verdict apart.
        var tokens = new[] { "PASS", "FAIL", "ENV_ERROR", "INCONCLUSIVE" };
        Assert.Equal(tokens.Length, tokens.Distinct().Count());
        foreach (var token in tokens)
        {
            Assert.Contains(token, stripped, StringComparison.Ordinal);
        }

        // No ANSI escape survived the strip.
        Assert.DoesNotContain(Esc, stripped, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes ANSI SGR escape sequences (ESC '[' … 'm') so the remaining text is what a
    /// screen reader / plain terminal would surface.
    /// </summary>
    private static string StripAnsi(string s)
    {
        const char esc = (char)0x1b;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == esc && i + 1 < s.Length && s[i + 1] == '[')
            {
                // Skip the ESC and '[' then advance to the terminating 'm' (consumed by the loop).
                i += 2;
                while (i < s.Length && s[i] != 'm')
                {
                    i++;
                }

                continue;
            }

            sb.Append(s[i]);
        }

        return sb.ToString();
    }
}
