// Vouchfx.Engine.Abstractions — DisplaySanitiser (issue #266, Item 4).
//
// A small, dependency-free scrubber for free-form diagnostic text before it reaches a
// human-facing report (a developer's terminal, a CI log). It complements — and does NOT
// replace — the engine's two existing text-safety nets:
//   • ScenarioRunner.ScrubDiagnostic (§17)     — CONTENT-aware: redacts resolved secret
//     VALUES that may have leaked verbatim into an exception message.
//   • JunitXmlRenderer's XML-illegal-character strip — FORMAT-aware: keeps a diagnostic
//     string well-formed inside an XML document.
// DisplaySanitiser is DISPLAY-aware: it neutralises raw control bytes and ANSI/VT100
// escape sequences that hostile/untrusted content (a script.csharp author's own thrown
// exception message, a capture JSONPath, a field value, …) could otherwise embed, and
// have echoed back verbatim into a diagnostic or observation line — corrupting or
// spoofing the terminal / log it is rendered into (e.g. cursor movement, screen clear,
// colour injection, a window-title change, or a carriage return that overwrites
// already-printed text).
//
// Lives here (Vouchfx.Engine.Abstractions), not in Vouchfx.Engine.Runtime where it was
// first introduced, because it is applied from THREE assemblies that do not all depend
// on one another: Vouchfx.Engine.Runtime (ScenarioRunner), Vouchfx.Engine.Reporting
// (TerminalRenderer — Runtime depends on Reporting, not the reverse, so Reporting cannot
// reach a Runtime type), and Vouchfx.Cli (ValidateCommand). Abstractions is the one
// assembly all three already reference (directly or transitively), so it is the natural
// shared home — and it means any FUTURE site in any of those assemblies can reach this
// helper without a new project reference.
//
// HONEST COVERAGE NOTE (three review rounds each found a further gap before this list was
// complete — read this before assuming "it's handled"): there is NO enforcement mechanism
// that makes every human-output write path call this helper. Coverage here is CALL-SITE
// DISCIPLINE, not a structural guarantee — the compiler cannot check it, and nothing fails
// loudly if a new write site forgets to call it. Anyone adding a NEW place that echoes
// suite-derived text (a ParseError, a scenario/step name, a field value, an exception
// .Message that can carry author-declared config, …) to a human-facing stream (a
// TextWriter ultimately backed by a console/terminal, or an interactive CI log) MUST wrap
// that text in SanitiseForDisplay explicitly; there is no other backstop.
//
// Known sites this helper is applied at, as of the last completed sweep (grep for
// "issue #266" / "DisplaySanitiser" for the exact call sites; this list is a map of
// CATEGORIES, not a promise that no site was missed — treat it as a starting point for the
// next audit, not a proof of completeness):
//   • Vouchfx.Cli.ValidateCommand.WriteHumanReport   — per-diagnostic line (validate).
//   • Vouchfx.Cli.RunCommand.ExecuteAsync            — the parse-failure report loop (the
//     MOST reachable site: a plain `vouchfx run` over a malformed suite).
//   • Vouchfx.Cli.WatchRunner                        — the did-not-parse message, the
//     WatchSession `report` sink (covers WatchCompileResult's AST-error text), the
//     OrchestrationException environment-error catch, and the run-loop catch-all.
//   • Vouchfx.Engine.Runtime.ScenarioRunner           — every schema/parse/pipeline/
//     secret-reference/environment-configuration/isolation-failure/secret-resolution/
//     compile-error WriteLine site, in both the single-scenario and RunSuiteAsync paths.
//   • Vouchfx.Engine.Runtime.ParallelSuiteRunner      — the per-slot raw-writer diagnostic
//     that bypasses TerminalRenderer entirely (flushed verbatim to the terminal).
//   • Vouchfx.Engine.Reporting.TerminalRenderer       — GetStr / GetStrFromObject (the
//     choke point every event-derived string field this renderer displays passes through)
//     plus the expected-vs-observed diff text (arrives from a provider's
//     IStepDiffRenderer, so it does NOT pass through GetStr and is sanitised separately).
//
// The `--json` / `--events` paths need no equivalent treatment: System.Text.Json always
// \u-escapes control characters inside a JSON string (mandated by the JSON spec itself —
// a literal control byte is not valid, unescaped, in a JSON string), so a raw ESC/CSI byte
// can never appear literally in a JSON document; see ValidateCommand's --json write path.
// HTML (HtmlRenderer) and JUnit XML (JunitXmlRenderer) file reports are a STRUCTURALLY
// DIFFERENT surface (not a terminal — ANSI/CSI/OSC sequences are inert there) and are
// deliberately out of this helper's scope; JunitXmlRenderer has its own, separate
// XML-illegal-character strip for XML well-formedness (not ANSI safety).
using System.Text;

namespace Vouchfx.Engine.Abstractions;

/// <summary>
/// Strips or neutralises control characters and ANSI/VT100 escape sequences from
/// free-form diagnostic or observation text before it is written to a human-facing
/// report — a terminal or a CI log file (issue #266, Item 4).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SanitiseForDisplay"/> is intentionally simple and allocation-light: a single
/// linear pass over the input, with no regular expressions and no external dependency.
/// </para>
/// <para>
/// <strong>What is removed.</strong> Every C0 control character (<c>0x00</c>-<c>0x1F</c>)
/// except <c>\t</c> and <c>\n</c>, which are preserved because they are common and benign
/// in multi-line diagnostic text; every C1 control character (<c>0x80</c>-<c>0x9F</c>,
/// which also covers <c>DEL</c>, <c>0x7F</c>, folded conservatively into the same range
/// check); a bare escape character; and, when it introduces one of two recognised
/// sequence kinds, the WHOLE sequence:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <strong>CSI</strong> (Control Sequence Introducer) — either the 7-bit form
///     (<c>ESC [</c>) or the 8-bit form (the single C1 byte <c>0x9B</c>) — consumed up to
///     and including its final byte. This is how ANSI cursor-movement, colour, and
///     screen-clear codes are neutralised in one step rather than merely dropping the
///     introducer and letting the rest of the sequence print as inert-looking (but still
///     terminal-interpreted by some emulators) bracket/digit text.
///   </description></item>
///   <item><description>
///     <strong>OSC</strong> (Operating System Command, <c>ESC ]</c>) — consumed up to and
///     including its terminator, either BEL (<c>0x07</c>) or ST (<c>ESC \</c>). OSC
///     sequences are used for e.g. setting a terminal's window title or drawing an OSC 8
///     hyperlink; left unneutralised, either could rewrite the terminal chrome or wrap
///     rendered text in a misleading, clickable link.
///   </description></item>
/// </list>
/// <para>
/// An escape character followed by neither <c>[</c> nor <c>]</c> is treated as a bare,
/// unrecognised escape: only the escape character itself is consumed, and whatever
/// follows is handled by the next loop iteration like ordinary text (or, if it is itself
/// a control character, stripped by the generic control-character branch).
/// </para>
/// <para>
/// <c>\r</c> is deliberately NOT preserved alongside <c>\t</c>/<c>\n</c>: a lone carriage
/// return can move the terminal cursor back to the start of the current line and let
/// subsequent output overwrite already-rendered text — exactly the kind of display
/// corruption/spoofing this helper exists to close off.
/// </para>
/// </remarks>
public static class DisplaySanitiser
{
    /// <summary>The ASCII escape character (0x1B) that introduces a 7-bit ANSI/VT100 sequence.</summary>
    private const char Esc = '\u001b';

    /// <summary>The single-byte (8-bit, C1) form of the Control Sequence Introducer, equivalent to <c>ESC [</c>.</summary>
    private const char C1CsiIntroducer = '\u009b';

    /// <summary>BEL (0x07) — one of the two valid OSC sequence terminators.</summary>
    private const char Bel = '\u0007';

    /// <summary>
    /// Returns <paramref name="text"/> with every control character and ANSI/VT100 escape
    /// sequence stripped or neutralised, safe to write to a terminal or CI log verbatim.
    /// </summary>
    /// <param name="text">
    /// The free-form diagnostic text to sanitise. May be <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// The sanitised text, or <paramref name="text"/> itself unchanged when it is
    /// <see langword="null"/> or empty (no allocation on that fast path). Otherwise a new
    /// string with every control character / escape sequence removed; ordinary text,
    /// including <c>\t</c> and <c>\n</c>, passes through untouched.
    /// </returns>
    public static string? SanitiseForDisplay(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Fast path (Copilot review #277, perf): most diagnostic text is ordinary and has
        // NOTHING that needs stripping — the common case for every field TerminalRenderer's
        // GetStr/GetStrFromObject choke points route through, for every rendered step. When
        // that is true, return the ORIGINAL string reference — no StringBuilder, no new
        // string, no allocation at all. Only text that actually contains a control
        // character / escape sequence falls through to the allocating pass below; its
        // behaviour (including idempotence) is unchanged.
        if (!NeedsSanitising(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == Esc)
            {
                i = SkipEscapeIntroducedSequence(text, i);
                continue;
            }

            if (c == C1CsiIntroducer)
            {
                // 8-bit CSI: the parameter/intermediate/final bytes follow directly (no
                // separate '[' — the single 0x9B byte IS the introducer).
                i = SkipCsiTail(text, i + 1);
                continue;
            }

            if (c == '\t' || c == '\n')
            {
                sb.Append(c);
                i++;
                continue;
            }

            if (IsControlChar(c))
            {
                // Drop every other C0 control (0x00-0x1F, \t/\n already handled above,
                // including \r — see the class remarks for why \r is not preserved) and
                // every C1 control (0x7F, 0x80-0x9F — 0x9B already handled above as the
                // 8-bit CSI introducer, so it never reaches this branch).
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsControlChar(char c) =>
        c <= 0x1F || (c >= 0x7F && c <= 0x9F);

    /// <summary>
    /// Cheap, allocation-free pre-scan: <see langword="true"/> as soon as
    /// <paramref name="text"/> contains at least one character the allocating pass below
    /// would strip or treat specially. <see cref="Esc"/> (0x1B) and
    /// <see cref="C1CsiIntroducer"/> (0x9B) both already satisfy <see cref="IsControlChar"/>
    /// (0x1B &lt;= 0x1F; 0x9B falls in the 0x7F-0x9F range), so this mirrors the main loop's
    /// classification exactly — <c>\t</c>/<c>\n</c> are the only control characters treated
    /// as ordinary text — without duplicating the escape/CSI dispatch logic.
    /// </summary>
    private static bool NeedsSanitising(string text)
    {
        foreach (var c in text)
        {
            if (c == '\t' || c == '\n')
            {
                continue;
            }

            if (IsControlChar(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Skips a single 7-bit escape character at <paramref name="index"/> and, when it
    /// introduces a recognised sequence kind, that whole sequence.
    /// </summary>
    /// <returns>The index of the first character AFTER the consumed escape/sequence.</returns>
    private static int SkipEscapeIntroducedSequence(string text, int index)
    {
        var i = index + 1; // consume the escape character itself

        if (i >= text.Length)
        {
            return i; // bare escape at the end of the string — nothing more to skip
        }

        if (text[i] == '[')
        {
            return SkipCsiTail(text, i + 1);
        }

        if (text[i] == ']')
        {
            return SkipOscTail(text, i + 1);
        }

        // An unrecognised two-character escape sequence: consume only the escape
        // character itself. The following character is handled by the next loop
        // iteration like ordinary text (or, if it is itself a control character,
        // stripped by the generic control-character branch).
        return i;
    }

    /// <summary>
    /// Skips a CSI (Control Sequence Introducer) sequence's parameter/intermediate/final
    /// bytes, given <paramref name="index"/> already positioned just AFTER the
    /// introducer (either the <c>[</c> of a 7-bit <c>ESC [</c> pair, or the 8-bit
    /// <c>0x9B</c> byte). Standard ANSI/VT100 CSI grammar: zero or more parameter bytes
    /// (<c>0x30</c>-<c>0x3F</c>), then zero or more intermediate bytes
    /// (<c>0x20</c>-<c>0x2F</c>), then exactly one final byte (<c>0x40</c>-<c>0x7E</c>). A
    /// truncated sequence that runs off the end of <paramref name="text"/> before a final
    /// byte appears is fully consumed rather than left dangling — there is nothing
    /// meaningful left to preserve either way.
    /// </summary>
    /// <returns>The index of the first character AFTER the consumed sequence.</returns>
    private static int SkipCsiTail(string text, int index)
    {
        var i = index;

        while (i < text.Length && text[i] >= 0x30 && text[i] <= 0x3F)
        {
            i++;
        }

        while (i < text.Length && text[i] >= 0x20 && text[i] <= 0x2F)
        {
            i++;
        }

        if (i < text.Length && text[i] >= 0x40 && text[i] <= 0x7E)
        {
            i++; // consume the final byte
        }

        return i;
    }

    /// <summary>
    /// Skips an OSC (Operating System Command) sequence's payload, given
    /// <paramref name="index"/> positioned just after the <c>ESC ]</c> introducer. An OSC
    /// sequence (used e.g. to set a terminal's window title, or for an OSC 8 hyperlink) is
    /// terminated by BEL (<c>0x07</c>) or ST (String Terminator, <c>ESC \</c>); either
    /// terminator, or running off the end of the string, ends the skip.
    /// </summary>
    /// <returns>The index of the first character AFTER the consumed sequence.</returns>
    private static int SkipOscTail(string text, int index)
    {
        var i = index;

        while (i < text.Length)
        {
            if (text[i] == Bel)
            {
                return i + 1; // consume the BEL terminator
            }

            if (text[i] == Esc && i + 1 < text.Length && text[i + 1] == '\\')
            {
                return i + 2; // consume the ST ("ESC \") terminator
            }

            i++;
        }

        return i; // ran off the end — fully consumed, nothing meaningful left to preserve
    }
}
