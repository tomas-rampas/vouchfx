// Vouchfx.Engine.Orchestration — ImageReference / ImageReferenceParser
// (feat/dependency-image-override).
//
// THE PROBLEM
// -----------
// `environment.dependencies[].image` lets an author on a private registry (e.g. an
// internal Nexus) name their own image as a single string, e.g.
//   nexus.corp.local:5000/platform/mongo:8.0
// Aspire's `WithImage(repository, tag)` — the API this feeds — takes the repository
// and the tag as SEPARATE arguments; there is no single-string overload that accepts
// a full reference. So the author's one string has to be split correctly before it
// ever reaches Aspire.
//
// THE HARD PART
// -------------
// A ':' only introduces a TAG when it appears after the LAST '/'. A ':' before the
// last '/' is a registry PORT, not a tag separator, e.g.
//   nexus.corp.local:5000/platform/mongo:8.0
//                    ^^^^ port                ^^^ tag
// A naive `IndexOf(':')` or `Split(':')` gets this wrong on exactly the Nexus-style
// example the feature exists for — splitting on the FIRST colon would produce
// repository "nexus.corp.local" and tag "5000/platform/mongo:8.0", silently sending
// the customer's suite to a nonexistent image. This parser instead scans only the
// LAST path segment (everything after the last '/', or the whole string when there
// is no '/' at all) for a colon — the only place a tag can legally appear.
//
// A digest (`@sha256:...`) is introduced by '@' and is stripped FIRST, before the
// repository/tag split — Docker itself treats `name:tag@digest` as "pull by digest,
// tag is informational only", and a '@' can appear inside neither a repository nor a
// tag, so there is no ordering ambiguity with the colon rule above.
//
// PRIOR ART — READ BEFORE CHANGING THE ALGORITHM
// ------------------------------------------------
// EnvironmentMapper.ResolveImage (registry-prefix application) and
// OrchestrationErrorClassifier.ParseRegistryHost (registry-host extraction for error
// reporting) already strip a digest via `IndexOf('@', StringComparison.Ordinal)` and
// decide "is the first path segment an explicit registry host?" via the same
// contains-'.'-or-':'-or-equals-"localhost" heuristic. This class is deliberately
// consistent with both — same digest-first ordering, same Ordinal comparisons — so a
// later step can fold all three onto one shared helper without changing any
// observable behaviour. Neither of those two files is modified here.
//
// This class is a pure, dependency-free string parser — no Aspire, no Docker, no
// I/O — so every branch is exhaustively unit-testable.

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// The three components of a Docker/OCI image reference, split the way
/// <c>IResourceBuilder.WithImage(repository, tag)</c> needs them.
/// </summary>
/// <param name="Repository">
/// The repository portion — everything before the tag/digest, e.g.
/// <c>"nexus.corp.local:5000/platform/mongo"</c> or <c>"mongo"</c>. Never
/// <see langword="null"/> or empty.
/// </param>
/// <param name="Tag">
/// The tag portion (e.g. <c>"8.0"</c>), or <see langword="null"/> when the reference
/// carries no explicit tag.
/// </param>
/// <param name="Digest">
/// The digest portion including its algorithm prefix (e.g. <c>"sha256:abc..."</c>),
/// or <see langword="null"/> when the reference carries no <c>@digest</c> suffix.
/// </param>
internal readonly record struct ImageReference(string Repository, string? Tag, string? Digest);

/// <summary>
/// Parses a single author-supplied image reference string into its
/// <see cref="ImageReference"/> components. See the file-header remarks for why the
/// split is not a trivial <c>IndexOf(':')</c>.
/// </summary>
internal static class ImageReferenceParser
{
    /// <summary>
    /// Splits <paramref name="reference"/> into repository, tag, and digest.
    /// </summary>
    /// <param name="reference">
    /// The full image reference as authored, e.g. <c>"mongo"</c>, <c>"mongo:8.0"</c>,
    /// <c>"nexus.corp.local:5000/platform/mongo:8.0"</c>, or
    /// <c>"mongo@sha256:abc..."</c>.
    /// </param>
    /// <returns>
    /// <para>The parsed <see cref="ImageReference"/>. Examples:</para>
    /// <list type="table">
    ///   <item><term><c>mongo</c></term>
    ///     <description>Repository <c>mongo</c>, no tag, no digest.</description></item>
    ///   <item><term><c>mongo:8.0</c></term>
    ///     <description>Repository <c>mongo</c>, tag <c>8.0</c>.</description></item>
    ///   <item><term><c>library/mongo</c></term>
    ///     <description>Repository <c>library/mongo</c>, no tag.</description></item>
    ///   <item><term><c>nexus.corp.local/platform/mongo:8.0</c></term>
    ///     <description>Repository <c>nexus.corp.local/platform/mongo</c>, tag <c>8.0</c>.</description></item>
    ///   <item><term><c>nexus.corp.local:5000/platform/mongo:8.0</c></term>
    ///     <description>The <c>:5000</c> is a registry PORT, not a tag separator — repository
    ///     <c>nexus.corp.local:5000/platform/mongo</c>, tag <c>8.0</c>.</description></item>
    ///   <item><term><c>nexus.corp.local:5000/platform/mongo</c></term>
    ///     <description>Repository <c>nexus.corp.local:5000/platform/mongo</c>, no tag (the
    ///     port colon is still not a tag separator).</description></item>
    ///   <item><term><c>mongo@sha256:abc...</c></term>
    ///     <description>Repository <c>mongo</c>, digest <c>sha256:abc...</c>.</description></item>
    ///   <item><term><c>nexus.corp.local:5000/platform/mongo@sha256:abc...</c></term>
    ///     <description>Repository <c>nexus.corp.local:5000/platform/mongo</c>, digest
    ///     <c>sha256:abc...</c>.</description></item>
    ///   <item><term><c>mongo:8.0@sha256:abc...</c></term>
    ///     <description>Repository <c>mongo</c>, tag <c>8.0</c>, digest <c>sha256:abc...</c>.</description></item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="reference"/> is <see langword="null"/>, empty, or
    /// whitespace-only; or ends with a bare <c>:</c> with no tag text after it (either
    /// at the very end of the string, or immediately before a valid <c>@digest</c>);
    /// or ends with a bare <c>@</c> with no digest text after it; or resolves to an
    /// empty repository (e.g. <c>"@sha256:abc"</c> or <c>":8.0"</c>).
    /// <para>
    /// Every rejected input is degenerate author YAML: the caller is validating a
    /// <c>.e2e.yaml</c> <c>environment.dependencies[].image</c> value and wants a loud,
    /// specific failure over silently resolving an ambiguous or wrong image (see the
    /// file-header remarks) — so this throws rather than returning a best-effort guess.
    /// </para>
    /// </exception>
    internal static ImageReference Parse(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException(
                $"Image reference must not be null, empty, or whitespace. Was: '{reference}'.",
                nameof(reference));
        }

        // Digest is stripped FIRST and always wins: a '@' can appear at most once in a
        // well-formed reference, and everything after it (including its own "sha256:"
        // prefix) is the digest verbatim, per the file-header remarks. Matches the
        // first-'@' convention already used by EnvironmentMapper.ResolveImage and
        // OrchestrationErrorClassifier.ParseRegistryHost.
        var withoutDigest = reference;
        string? digest = null;
        var atIndex = reference.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
        {
            digest = reference[(atIndex + 1)..];
            if (digest.Length == 0)
            {
                throw new ArgumentException(
                    $"Image reference '{reference}' has a trailing '@' with no digest after it.",
                    nameof(reference));
            }

            withoutDigest = reference[..atIndex];
        }

        // A ':' only introduces a tag when it appears in the LAST path segment (after
        // the last '/', or the whole remaining string when there is no '/' at all). A
        // ':' in any earlier segment is a registry port (nexus.corp.local:5000/...),
        // never a tag separator — this is the rule that makes both bolded rows in the
        // parse table (registry port + tag, registry port + no tag) come out right.
        var lastSlashIndex = withoutDigest.LastIndexOf('/');
        var lastSegmentStart = lastSlashIndex + 1; // 0 when there is no '/' at all.
        var lastSegment = withoutDigest[lastSegmentStart..];

        string repository;
        string? tag = null;
        var colonIndex = lastSegment.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex >= 0)
        {
            tag = lastSegment[(colonIndex + 1)..];
            if (tag.Length == 0)
            {
                throw new ArgumentException(
                    $"Image reference '{reference}' has a trailing ':' with no tag after it.",
                    nameof(reference));
            }

            repository = withoutDigest[..(lastSegmentStart + colonIndex)];
        }
        else
        {
            repository = withoutDigest;
        }

        if (repository.Length == 0)
        {
            throw new ArgumentException(
                $"Image reference '{reference}' has no repository name.",
                nameof(reference));
        }

        return new ImageReference(repository, tag, digest);
    }
}
