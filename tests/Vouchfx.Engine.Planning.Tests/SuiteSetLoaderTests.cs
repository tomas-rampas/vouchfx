// Vouchfx.Engine.Planning.Tests — SuiteSetLoaderTests (M3 Planner, MINOR fix-round).
//
// Direct, deterministic unit coverage for SuiteSetLoader.TruncateParseErrorMessage — the
// EDGE-003 parse-error-message bound that PlanSafetyTests' end-to-end fixture already proves
// engages (a malformed suite's raw content is truncated before reaching the report), but that
// fixture cannot cheaply pin the surrogate-pair edge case: it would need a real astral-plane
// character (e.g. an emoji) landing at the EXACT MaxParseErrorMessageChars offset inside
// AstBuilder's real exception-message template, which is fragile to construct and brittle to
// keep in sync with that template's own prefix text. Calling the method directly with
// hand-built strings pins the exact boundary condition instead.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Vouchfx.Engine.Planning.Ingest;
using Xunit;

namespace Vouchfx.Engine.Planning.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "xUnit test methods use Given_When_Then underscore convention.")]
public sealed class SuiteSetLoaderTests
{
    [Fact]
    public void TruncateParseErrorMessage_AtOrUnderTheLimit_ReturnsTheMessageUnchanged()
    {
        var atLimit = new string('a', SuiteSetLoader.MaxParseErrorMessageChars);
        var underLimit = new string('a', SuiteSetLoader.MaxParseErrorMessageChars - 1);

        Assert.Same(atLimit, SuiteSetLoader.TruncateParseErrorMessage(atLimit));
        Assert.Same(underLimit, SuiteSetLoader.TruncateParseErrorMessage(underLimit));
    }

    [Fact]
    public void TruncateParseErrorMessage_ExceedingTheLimitWithNoSurrogateAtTheBoundary_TruncatesAtExactlyTheLimit()
    {
        var message = new string('a', SuiteSetLoader.MaxParseErrorMessageChars + 50);

        var result = SuiteSetLoader.TruncateParseErrorMessage(message);

        Assert.Equal(
            new string('a', SuiteSetLoader.MaxParseErrorMessageChars) + "... (truncated)",
            result);
    }

    // ── MINOR fix-round regression pin: the naive `message[..MaxParseErrorMessageChars]`
    // slice this replaced could land exactly between a high surrogate and its low-surrogate
    // partner, leaving a lone, unpaired high surrogate — invalid UTF-16 that can throw when
    // later serialised. Build a message where the astral-plane character (a UTF-16 surrogate
    // pair) straddles the cut boundary: its high half sits at the last INCLUDED index under a
    // naive slice, and its low half sits at the first EXCLUDED index. ─────────────────────────

    [Fact]
    public void TruncateParseErrorMessage_WhereTheLimitWouldSplitASurrogatePair_BacksOffToKeepThePairIntact()
    {
        const string astralChar = "😀"; // U+1F600 GRINNING FACE — one high + one low surrogate.
        var limit = SuiteSetLoader.MaxParseErrorMessageChars;

        // Prefix fills indices [0, limit - 1) so the surrogate pair's high half lands at index
        // (limit - 1) — the LAST index a naive `message[..limit]` slice would include — and its
        // low half at index `limit`, the first index that slice would EXCLUDE. A few trailing
        // characters push the total length past the limit so truncation actually triggers.
        var prefix = new string('a', limit - 1);
        var message = prefix + astralChar + "tail-content-after-the-pair";

        var result = SuiteSetLoader.TruncateParseErrorMessage(message);

        Assert.EndsWith("... (truncated)", result, StringComparison.Ordinal);

        var withoutSuffix = result[..^"... (truncated)".Length];

        // The whole astral character (both surrogate halves) must be dropped together, never
        // split — the fix backs the cut off by one from the naive boundary.
        Assert.Equal(prefix, withoutSuffix);
        Assert.False(
            withoutSuffix.Length > 0 && char.IsHighSurrogate(withoutSuffix[^1]),
            "Truncated message must never end with an unpaired high surrogate.");

        // Prove the result is genuinely valid UTF-16 end-to-end (the actual risk this guards
        // against: a lone surrogate can throw when later serialised/transcoded), not merely
        // that the specific assertions above happen to pass.
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var encodingException = Record.Exception(() => { strictUtf8.GetBytes(result); });
        Assert.Null(encodingException);
    }
}
