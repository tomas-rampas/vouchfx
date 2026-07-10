// S11-B-01 — ResolvedSecretLedger unit tests (§17).
//
// Pins the behaviour of the defence-in-depth scrub net used at the reporting boundary:
//   • records only meaningful values (never null / empty / whitespace);
//   • scrubs every verbatim occurrence of a recorded value to the redaction marker;
//   • returns unrelated text unchanged (targeted net, not a blanket rewrite);
//   • scrubs longest-first so a shorter recorded value cannot strand a fragment of a
//     longer overlapping one;
//   • is null-safe in and out.

using System;
using Vouchfx.Engine.Abstractions.Secrets;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Secrets;

/// <summary>
/// Unit tests for <see cref="ResolvedSecretLedger"/> — the §17 defence-in-depth scrub net.
/// </summary>
public sealed class ResolvedSecretLedgerTests
{
    [Fact]
    public void Scrub_ReplacesRecordedValue_WithMarker()
    {
        var ledger = new ResolvedSecretLedger();
        ledger.Record("hunter2-secret-value");

        var scrubbed = ledger.Scrub("the password is hunter2-secret-value here");

        Assert.DoesNotContain("hunter2-secret-value", scrubbed, StringComparison.Ordinal);
        Assert.Contains(SecretString.RedactedMarker, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_ReplacesEveryOccurrence()
    {
        var ledger = new ResolvedSecretLedger();
        ledger.Record("abc123");

        var scrubbed = ledger.Scrub("abc123 / abc123 / abc123");

        Assert.DoesNotContain("abc123", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_LeavesUnrelatedTextUnchanged()
    {
        var ledger = new ResolvedSecretLedger();
        ledger.Record("the-secret");

        const string input = "{\"status\":500,\"expected\":200}";
        var scrubbed = ledger.Scrub(input);

        Assert.Equal(input, scrubbed);
    }

    [Fact]
    public void Scrub_WithNothingRecorded_ReturnsInputUnchanged()
    {
        var ledger = new ResolvedSecretLedger();

        const string input = "no secrets here";
        Assert.Same(input, ledger.Scrub(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Record_IgnoresNullEmptyOrWhitespace(string? value)
    {
        var ledger = new ResolvedSecretLedger();
        ledger.Record(value);

        Assert.Equal(0, ledger.Count);

        // And a whitespace 'secret' must NOT cause unrelated whitespace to be rewritten.
        const string input = "a b\tc";
        Assert.Equal(input, ledger.Scrub(input));
    }

    [Fact]
    public void Scrub_LongestFirst_NoFragmentOfOverlappingValueSurvives()
    {
        // Two recorded values where one is a substring of the other.  Scrubbing the
        // shorter first would replace the inner part of the longer and strand its tail;
        // longest-first prevents that.
        var ledger = new ResolvedSecretLedger();
        ledger.Record("TOKEN");
        ledger.Record("TOKEN-EXTENDED-9f3");

        var scrubbed = ledger.Scrub("value=TOKEN-EXTENDED-9f3;");

        Assert.DoesNotContain("TOKEN-EXTENDED-9f3", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("EXTENDED", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("9f3", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_NullInput_ReturnsNull()
    {
        var ledger = new ResolvedSecretLedger();
        ledger.Record("anything");

        Assert.Null(ledger.Scrub(null));
    }

    [Fact]
    public void Record_DeduplicatesIdenticalValues()
    {
        var ledger = new ResolvedSecretLedger();
        ledger.Record("same");
        ledger.Record("same");

        Assert.Equal(1, ledger.Count);
    }
}
