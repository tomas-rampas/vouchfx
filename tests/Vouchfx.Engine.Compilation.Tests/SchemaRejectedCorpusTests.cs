// Schema-corpus gate — Task 3: the rejected-corpus half.
//
// The sibling to SchemaAcceptedCorpusTests: a corpus of documents that must be
// REJECTED by DocumentValidator, so a future narrowing that overreaches (or a
// future widening that overreaches the other way) cannot silently change which
// shapes are refused. A bare `IsValid == false` assertion would be worthless
// here — it passes when a document is rejected for an entirely unrelated
// reason, so it would stay green through a real regression in the SPECIFIC
// rejection this fixture exists to pin. Every fixture is therefore pinned to
// its expected InstanceLocation AND keyword, exactly as
// RootSchemaTests.Validate_BareTypeWithoutDot_IsRejected already does for the
// step 'type' pattern.
//
// The pairing (document, expected location, expected keyword) travels WITH the
// document as a YAML comment header, so nobody can add a Corpus/Rejected
// fixture without a pinned expectation and nobody can silently loosen the
// pairing by editing only the C# side.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Schema-corpus gate: proves documents seeded from a real regression (commit
/// 66aef95's four deliberately-kept-rejected shapes) stay rejected at the
/// SAME location and keyword, not merely "somehow invalid".
/// </summary>
public sealed class SchemaRejectedCorpusTests
{
    // Re-uses the same full 25-provider Core registry as the accepted-corpus
    // gate — a rejection this corpus pins must hold against the real composed
    // schema an author's suite validates against, exactly like the accepted
    // half.
    private static readonly StepKindRegistry Registry = SchemaAcceptedCorpusTests.Registry;

    private static string RejectedCorpusDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Corpus", "Rejected");

    private const string LocationHeaderPrefix = "# expected-instance-location:";
    private const string KeywordHeaderPrefix = "# expected-keyword:";

    /// <summary>
    /// One rejected-corpus fixture: its full path, plus the expectation parsed
    /// from its leading comment header (see <see cref="ParseExpectedRejection"/>).
    /// </summary>
    public static IEnumerable<object[]> RejectedFiles()
    {
        var dir = RejectedCorpusDirectory;
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var path in Directory
            .GetFiles(dir, "*.e2e.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            var (location, keyword) = ParseExpectedRejection(path);
            yield return new object[] { path, location, keyword };
        }
    }

    /// <summary>
    /// <see cref="RejectedFiles"/> must discover AT LEAST a known-safe floor of
    /// files — a bare "at least one" would still pass if a glob/path
    /// regression silently dropped all but a single fixture, letting every
    /// OTHER <see cref="RejectedDocument_IsInvalidAtExpectedLocation"/> case
    /// vanish instead of failing loudly. Raised from 15 to 40 (critic NIT-9):
    /// the original floor was set when the committed count was 25 and never
    /// tightened as fixtures accumulated, leaving it comfortable enough (15
    /// against 44, pre-this-PR) to silently tolerate losing dozens of
    /// fixtures to a regression. The committed count as of this PR is 45 (44
    /// pre-existing, plus <c>security-serverartifacts-target-not-absolute.e2e.yaml</c>,
    /// MINOR-4); 40 is sensibly below-but-near that — tight enough that
    /// dropping several fixtures fails loudly, with enough headroom that
    /// routine single-fixture additions/reorganisation do not require raising
    /// it every time. Safe to raise again as fixtures accumulate further.
    /// </summary>
    [Fact]
    public void RejectedFiles_DiscoversAtLeastOneFile()
    {
        const int minRejectedFiles = 40;

        var count = RejectedFiles().Count();

        Assert.True(count >= minRejectedFiles,
            $"Expected at least {minRejectedFiles} Corpus/Rejected files under " +
            $"'{RejectedCorpusDirectory}', found {count}.");
    }

    /// <summary>
    /// Every document under <c>Corpus/Rejected</c> must be rejected by
    /// <see cref="DocumentValidator"/> with at least one error whose
    /// <see cref="SchemaValidationError.InstanceLocation"/> equals the
    /// fixture's pinned expectation AND whose message carries the pinned
    /// keyword (in the <c>[keyword]</c> form <c>SchemaErrorCollector</c>
    /// emits) — never a bare "some error happened somewhere" check.
    /// </summary>
    [Theory]
    [MemberData(nameof(RejectedFiles))]
    public void RejectedDocument_IsInvalidAtExpectedLocation(string path, string expectedLocation, string expectedKeyword)
    {
        var yaml = File.ReadAllText(path);
        var result = DocumentValidator.Validate(yaml, Registry);

        Assert.False(result.IsValid,
            $"{Path.GetFileName(path)}: expected this document to be REJECTED, but it validated successfully.");

        var expectedKeywordTag = $"[{expectedKeyword}]";
        var matched = result.Errors.Any(e =>
            string.Equals(e.InstanceLocation, expectedLocation, StringComparison.Ordinal) &&
            e.Message.Contains(expectedKeywordTag, StringComparison.Ordinal));

        Assert.True(matched,
            $"{Path.GetFileName(path)}: expected a rejection at '{expectedLocation}' carrying " +
            $"'{expectedKeywordTag}', but got:{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Errors.Select(e => $"  at {e.InstanceLocation}: {e.Message}")));
    }

    /// <summary>
    /// Parses the <c># expected-instance-location: …</c> and
    /// <c># expected-keyword: …</c> header comments a <c>Corpus/Rejected</c>
    /// fixture must carry at the top of the file (before the first non-comment,
    /// non-blank line). Keeping the expectation in the document itself — rather
    /// than only in a C# array — means the pairing cannot drift: whoever adds a
    /// fixture without both headers gets a clear failure here, not a silently
    /// unpinned <c>IsValid == false</c> test.
    /// </summary>
    private static (string Location, string Keyword) ParseExpectedRejection(string path)
    {
        string? location = null;
        string? keyword = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.TrimStart();

            if (line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith('#'))
            {
                // First substantive (non-comment) line: the header block is over.
                break;
            }

            if (line.StartsWith(LocationHeaderPrefix, StringComparison.Ordinal))
            {
                location = line[LocationHeaderPrefix.Length..].Trim();
            }
            else if (line.StartsWith(KeywordHeaderPrefix, StringComparison.Ordinal))
            {
                keyword = line[KeywordHeaderPrefix.Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(keyword))
        {
            throw new InvalidOperationException(
                $"'{path}' is missing a '{LocationHeaderPrefix}' and/or '{KeywordHeaderPrefix}' " +
                "header comment. Every Corpus/Rejected fixture must pin both, at the top of the " +
                "file, so the expected rejection travels with the document.");
        }

        return (location, keyword);
    }
}
