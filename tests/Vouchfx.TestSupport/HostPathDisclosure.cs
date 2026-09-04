// The ONE property assertion for "no absolute host path reached this channel" — issues #357,
// #375, #473.
//
// WHY IT LIVES HERE RATHER THAN IN A TEST ASSEMBLY
// ───────────────────────────────────────────────
// It was born `private static` in Vouchfx.Engine.Runtime.Tests/SecurityDiagnosticPathDisclosureTests
// as the check over a validation-time diagnostic's three written artefacts, and was COPIED — not
// shared — into Vouchfx.Engine.Runtime.Tests/SecurityPathDisclosureLedgerTests when #375 needed the
// same property over the three scrub chokepoints. #473 then needed it at three more sites
// (security.serverArtifacts[].source, the seed applier's resolved SQL paths, and SeedFixtures' own
// throw), two of which are tested from Vouchfx.Engine.Orchestration.Tests, which cannot see a
// private member of another assembly. The choice was a THIRD copy or a lift.
//
// THE COPIES HAD ALREADY DIVERGED, AND THAT IS THE ARGUMENT — not the tidiness. The two were not
// equal predicates: the ledger suite's carried an assertion the original never had, over the
// JSON-ESCAPED form of the host directory. That is precisely the form
// SecurityPathDisclosureLedger.BuildOrderedForms exists to substitute, and for a concrete reason —
// a Windows path is full of backslashes, which encode to `\\`, so a path already embedded in
// serialised JSON survives a raw-only match and is recoverable from the on-disk --events artifact
// by any consumer that JSON-decodes it.
//
// #473's FIRST ATTEMPT LIFTED THE WEAKER COPY AND LEFT THE STRONGER ONE IN THE TREE. That would
// have shipped a "shared" assertion blind to the one spelling the ledger was built for, under a
// header claiming this check must not be copied — while a second copy sat two files away. Both
// copies are now deleted and the STRONGER predicate is the one below, so the fold strengthened all
// seven call sites rather than levelling them down to the weaker of the two.
//
// The rules must be ONE set of rules for the same reason the divergence happened: the original
// suite had already burned a measured correction into them (trimming '.' from the FRONT turns the
// author's own './certs/client.pem' into a rooted-looking '/certs/client.pem' and fails a correct
// message), and a divergent copy is that correction present in one lane only.
//
// Vouchfx.TestSupport is the right home because all three test assemblies that need it already
// reference it, exactly as ChildProcess.KillTreeQuietly was lifted here by #475.
//
// PURE BCL, WHICH IS WHY IT THROWS RATHER THAN CALLING Assert. Nothing in this project references
// any Vouchfx.* type or any test framework (see the .csproj header), and adding xunit here to keep
// one `Assert.False` would put a test-framework dependency into every assembly that references it.
// A thrown exception fails the calling test with this message either way; what a test framework
// would add is a nicer category name, not a different outcome.
using System;
using System.IO;
using System.Text.Json;

namespace Vouchfx.TestSupport;

/// <summary>
/// The shared property assertion that a diagnostic channel names no absolute host path.
/// </summary>
public static class HostPathDisclosure
{
    /// <summary>
    /// CA1861: the token separators and path separators are fields, not inline arrays.
    /// </summary>
    /// <remarks>
    /// <c>&amp;</c> and <c>;</c> are separators so an HTML-escaped quote (<c>&amp;#39;</c>) splits
    /// off the path it wraps instead of gluing itself to the front of it.
    /// </remarks>
    private static readonly char[] s_tokenSeparators =
        { ' ', '\t', '\r', '\n', '"', '\'', '<', '>', '&', ';', ',', '(', ')', '[', ']' };

    private static readonly char[] s_pathSeparators = { '\\', '/' };

    /// <summary>
    /// Trimmed from the END only. Trimming <c>.</c> from the FRONT would turn the author's own
    /// <c>./certs/client.pem</c> into the rooted-looking <c>/certs/client.pem</c> and fail a
    /// correct message — measured, on the first run of the test this was lifted from.
    /// </summary>
    private static readonly char[] s_trailingPunctuation = { '.', ':' };

    /// <summary>
    /// Throws when <paramref name="text"/> names an absolute host path, by the PROPERTY rather
    /// than by any one expected string.
    /// </summary>
    /// <param name="channel">
    /// What is being asserted about, named in the failure message — "the event stream", "the JUnit
    /// message attribute", "the substituted observation".
    /// </param>
    /// <param name="text">The rendered diagnostic text.</param>
    /// <param name="hostDirectory">
    /// The specific host directory this case resolves against — the suite directory, or the temp
    /// directory a fixture was written into. Checked as a plain substring, which catches the leak
    /// even where no token boundary exists.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="text"/> contains <paramref name="hostDirectory"/> raw or JSON-escaped, or
    /// contains a token that is a rooted path.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Three checks, and the third is the one that generalises. (a) The host directory itself must
    /// not appear — that is the specific leak a given case triggers, and a substring test catches
    /// it even where no token boundary exists. (b) Nor may its JSON-ESCAPED form. (c) No
    /// whitespace- or quote-delimited token may be a rooted path CONTAINING a separator: that is
    /// what a leaked host path looks like on either platform (<c>C:\…</c> / <c>\\host\…</c> on
    /// Windows, <c>/…/…</c> elsewhere), and it holds for a path the case never names. The separator
    /// clause is what keeps ordinary message text out of the net — a bare <c>drive:</c>-shaped
    /// token with no separator is not a path reference.
    /// </para>
    /// <para>
    /// <strong>(b) is not belt-and-braces, and it is why the two former copies of this method were
    /// not interchangeable.</strong> A Windows path is full of <c>\</c>, which encodes to
    /// <c>\\</c>; the token scan in (c) is defeated by that escaping, because
    /// <c>C:\\work\\certs</c> is not what <see cref="Path.IsPathRooted"/> was shown, and (a)'s raw
    /// substring does not match it either. So a resolved path already embedded in serialised JSON —
    /// an event line, the <c>--events</c> artifact — passes (a) and (c) and is still recoverable by
    /// any consumer that JSON-decodes it. This is the same reasoning, over the same spelling, that
    /// <c>SecurityPathDisclosureLedger.BuildOrderedForms</c> applies when it registers both forms of
    /// every recorded path.
    /// </para>
    /// </remarks>
    public static void AssertNoAbsoluteHostPath(string channel, string text, string hostDirectory)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(hostDirectory);

        if (text.Contains(hostDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{channel} names the host directory '{hostDirectory}'. A diagnostic must name the "
                + "declared path and the concept it resolves against (#357's rule), never a "
                + $"resolved one. Full text: {text}");
        }

        // The JSON-escaped form, checked SEPARATELY rather than by normalising the text: decoding
        // the whole input would require knowing it is JSON, and most channels this runs over are
        // not (a terminal line, an exception message, an HTML fragment). Encoding the needle works
        // for every channel — an unescaped text simply never contains the escaped form.
        var escapedHostDirectory = JsonEncodedText.Encode(hostDirectory).ToString();
        if (!string.Equals(escapedHostDirectory, hostDirectory, StringComparison.Ordinal)
            && text.Contains(escapedHostDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{channel} names the host directory '{hostDirectory}' in its JSON-ESCAPED form "
                + $"('{escapedHostDirectory}'). A serialised channel discloses the host layout to "
                + "any consumer that JSON-decodes it, so the escaped form is a leak exactly as the "
                + $"raw one is (#357's rule). Full text: {text}");
        }

        foreach (var token in text.Split(s_tokenSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.TrimEnd(s_trailingPunctuation);
            if (candidate.Length < 2 || candidate.IndexOfAny(s_pathSeparators) < 0)
            {
                continue;
            }

            if (Path.IsPathRooted(candidate))
            {
                throw new InvalidOperationException(
                    $"{channel} names an absolute host path '{candidate}'. A diagnostic must name "
                    + "the declared path and the concept it resolves against (#357's rule), never "
                    + $"a resolved one. Full text: {text}");
            }
        }
    }
}
