using System;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Secrets;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class DiagnosticTextTests
{
    [Theory]
    [InlineData("\u001b")]
    [InlineData("\r")]
    [InlineData("\u009b")]
    [InlineData("\u007f")]
    [InlineData("\u001b[31m")]
    [InlineData("\0")]
    [InlineData("\b")]
    [InlineData("\a")]
    public void ControlByteSecret_IsRedactedWholeBeforeDisplayRewriting(string control)
    {
        var ledger = new ResolvedSecretLedger();
        var secret = "prefix" + control + "suffix";
        ledger.Record(secret);
        var raw = "failure: " + secret + "\r\u001b[31m";

        var displayed = DiagnosticText.ScrubForDisplay(raw, ledger, pathLedger: null);

        Assert.Equal("failure: " + SecretString.RedactedMarker, displayed);
        Assert.DoesNotContain("prefix", displayed, StringComparison.Ordinal);
        Assert.DoesNotContain("suffix", displayed, StringComparison.Ordinal);

        // Discriminating control: this same value actually leaks if the order is reversed.
        var reversed = ledger.Scrub(DisplaySanitiser.SanitiseForDisplay(raw));
        Assert.DoesNotContain(SecretString.RedactedMarker, reversed, StringComparison.Ordinal);
        Assert.Contains("prefix", reversed, StringComparison.Ordinal);
        Assert.NotEqual(displayed, reversed);
    }

    [Fact]
    public void EventText_RemainsUnsanitised_AndNullLedgersStillSanitiseDisplay()
    {
        const string raw = "public\rdiagnostic\u001b[31m";
        Assert.Equal(raw, DiagnosticText.Scrub(raw, ledger: null, pathLedger: null));
        Assert.Equal("publicdiagnostic", DiagnosticText.ScrubForDisplay(raw, ledger: null, pathLedger: null));
        Assert.Null(DiagnosticText.ScrubForDisplay(null, ledger: null, pathLedger: null));
    }
}
