// Vouchfx.Engine.Abstractions.Tests - DisplaySanitiser (issue #266, Item 4). Non-docker.
//
// Exercises SanitiseForDisplay directly: ordinary text passes through untouched, tab/newline
// are preserved, every other C0/C1 control character is stripped, a bare escape / a full
// 7-bit CSI sequence (colour codes, cursor movement, screen clear) is neutralised in one
// step rather than merely dropping the introducer and letting the rest of the sequence
// print, and (MINOR-4, added when the sanitiser moved here from Vouchfx.Engine.Runtime) an
// OSC sequence (window-title / hyperlink) and an 8-bit (C1) CSI sequence are neutralised the
// same way.
//
// Every control character used below is constructed via an explicit (char) cast on a
// hex/decimal int literal or a standard C# escape sequence (\t, \n, \r) - deliberately never
// a raw literal control byte typed directly into this source file.

using Vouchfx.Engine.Abstractions;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests;

public sealed class DisplaySanitiserTests
{
    private const char Esc = (char)0x1B;
    private const char Bel = (char)0x07;
    private const char C1Csi = (char)0x9B;

    // -- Null / empty pass-through -------------------------------------------------

    [Fact]
    public void SanitiseForDisplay_Null_ReturnsNull()
    {
        Assert.Null(DisplaySanitiser.SanitiseForDisplay(null));
    }

    [Fact]
    public void SanitiseForDisplay_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DisplaySanitiser.SanitiseForDisplay(string.Empty));
    }

    // -- Ordinary text is unchanged -------------------------------------------------

    [Fact]
    public void SanitiseForDisplay_OrdinaryText_IsUnchanged()
    {
        const string text = "Step 'check-health' failed: expected 200, got 503.";

        Assert.Equal(text, DisplaySanitiser.SanitiseForDisplay(text));
    }

    // -- \t and \n are preserved -----------------------------------------------------

    [Fact]
    public void SanitiseForDisplay_TabAndNewline_ArePreserved()
    {
        const string text = "line one\tindented\nline two";

        Assert.Equal(text, DisplaySanitiser.SanitiseForDisplay(text));
    }

    // -- \r is dropped (see class remarks: a lone CR can overwrite rendered text) ---

    [Fact]
    public void SanitiseForDisplay_CarriageReturn_IsDropped()
    {
        var text = "before" + '\r' + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforeafter", result);
    }

    // -- Every other C0 control character is stripped -------------------------------

    [Theory]
    [InlineData(0x00)] // NUL
    [InlineData(0x07)] // BEL
    [InlineData(0x08)] // BS
    [InlineData(0x0B)] // VT
    [InlineData(0x0C)] // FF
    [InlineData(0x1F)] // US
    public void SanitiseForDisplay_C0ControlCharacters_AreStripped(int codePoint)
    {
        var control = (char)codePoint;
        var text = "before" + control + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforeafter", result);
    }

    // -- C1 control characters (0x7F, 0x80-0x9F) are stripped -----------------------
    //
    // 0x9B is deliberately excluded here: it is the 8-bit CSI introducer, exercised
    // separately below because it consumes a full trailing sequence, not just itself.

    [Theory]
    [InlineData(0x7F)] // DEL
    [InlineData(0x80)]
    [InlineData(0x9A)]
    [InlineData(0x9F)]
    public void SanitiseForDisplay_C1ControlCharacters_AreStripped(int codePoint)
    {
        var control = (char)codePoint;
        var text = "before" + control + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforeafter", result);
    }

    // -- Bare escape (no following CSI/OSC introducer) is dropped --------------------

    [Fact]
    public void SanitiseForDisplay_BareEscapeNotFollowedByCsi_IsDropped()
    {
        var text = "before" + Esc + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforeafter", result);
    }

    [Fact]
    public void SanitiseForDisplay_EscapeAtEndOfString_IsDropped()
    {
        var text = "before" + Esc;

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("before", result);
    }

    // -- A full 7-bit CSI sequence is neutralised in one step ------------------------

    [Fact]
    public void SanitiseForDisplay_AnsiColourSequence_IsNeutralised()
    {
        // ESC [ 31 m - sets foreground red; ESC [ 0 m - resets.
        var text = "before" + Esc + "[31m" + "danger" + Esc + "[0m" + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforedangerafter", result);
    }

    [Fact]
    public void SanitiseForDisplay_AnsiCursorMovementSequence_IsNeutralised()
    {
        // ESC [ 2 A - moves the cursor up 2 lines.
        var text = "before" + Esc + "[2A" + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforeafter", result);
    }

    [Fact]
    public void SanitiseForDisplay_AnsiScreenClearSequence_IsNeutralised()
    {
        // ESC [ 2 J - clears the entire screen.
        var text = "before" + Esc + "[2J" + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforeafter", result);
    }

    [Fact]
    public void SanitiseForDisplay_TruncatedCsiSequence_IsFullyConsumed()
    {
        // A CSI sequence with parameter bytes but no final byte - the string ends
        // mid-sequence.
        var text = "before" + Esc + "[31";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("before", result);
    }

    // -- MINOR-4: an 8-bit (C1) CSI sequence is neutralised the same way -------------

    [Fact]
    public void SanitiseForDisplay_C1CsiColourSequence_IsNeutralised()
    {
        // 0x9B is the single-byte (8-bit) equivalent of "ESC [" - the parameter/final
        // bytes follow it directly, with no separate '[' character.
        var text = "before" + C1Csi + "31m" + "danger" + C1Csi + "0m" + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforedangerafter", result);
    }

    [Fact]
    public void SanitiseForDisplay_TruncatedC1CsiSequence_IsFullyConsumed()
    {
        var text = "before" + C1Csi + "31";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("before", result);
    }

    // -- MINOR-4: an OSC sequence (window title / OSC 8 hyperlink) is neutralised ----

    [Fact]
    public void SanitiseForDisplay_OscSequenceTerminatedByBel_IsNeutralised()
    {
        // ESC ] 0 ; <title> BEL - sets the terminal window/tab title.
        var text = "before" + Esc + "]0;evil title" + Bel + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforeafter", result);
    }

    [Fact]
    public void SanitiseForDisplay_OscSequenceTerminatedByStringTerminator_IsNeutralised()
    {
        // ESC ] 8 ;; <url> ESC \ <link text> ESC ] 8 ;; ESC \ - an OSC 8 hyperlink; each
        // half is terminated by ST ("ESC \") rather than BEL.
        var text = "before"
            + Esc + "]8;;http://evil.example" + Esc + "\\"
            + "click me"
            + Esc + "]8;;" + Esc + "\\"
            + "after";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("beforeclick meafter", result);
    }

    [Fact]
    public void SanitiseForDisplay_TruncatedOscSequence_IsFullyConsumed()
    {
        // An OSC sequence with no terminator at all - the string ends mid-sequence.
        var text = "before" + Esc + "]0;untermina";

        var result = DisplaySanitiser.SanitiseForDisplay(text);

        Assert.Equal("before", result);
    }

    // -- A hostile diagnostic embedding a control/ANSI sequence renders inert ------

    [Fact]
    public void SanitiseForDisplay_HostileDiagnosticWithEmbeddedAnsi_RendersInert()
    {
        // Simulates a step id or field value from untrusted YAML carrying an embedded
        // ANSI sequence that a naive diagnostic write would echo back verbatim.
        var hostile = "step '" + Esc + "[2J" + Esc + "[H" + "PWNED" + "' failed";

        var result = DisplaySanitiser.SanitiseForDisplay(hostile);

        Assert.Equal("step 'PWNED' failed", result);
        Assert.DoesNotContain(Esc, result!);
    }
}
