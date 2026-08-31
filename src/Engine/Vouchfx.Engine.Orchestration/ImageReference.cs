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
// consistent with both — same digest-first ordering, same Ordinal comparisons.
//
// These are NOT interchangeable, and folding them onto one shared helper would change
// observable behaviour: ResolveImage is a bare prefix check with no validation at all,
// and silently passes through every shape this parser rejects outright — leading/
// trailing whitespace, empty path segments ("myorg/", "/mongo", "a//b"), a second
// colon inside a tag, and a missing or non-hex digest body. ResolveImage feeds
// services' 'image:' (handed to AddContainer as a single opaque string); routing it
// through this parser's strict validation would start THROWING on service images that
// are malformed in exactly those ways and pass through untouched today — an
// observable behaviour change for every existing suite that happens to author one.
// Nor is "EnvironmentMapper.cs is not modified here" a safe assumption for that later
// step: this class's own commit (feat/dependency-image-override) already modified
// EnvironmentMapper.cs extensively (ApplyImageOverrides, the eager dependency-image
// validation loop) — it left the ResolveImage FUNCTION specifically untouched, which
// is the only claim that ever held.
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
    /// whitespace-only; or carries leading/trailing whitespace; or ends with a bare
    /// <c>:</c> with no tag text after it (either at the very end of the string, or
    /// immediately before a valid <c>@digest</c>); or has a tag containing a <c>:</c>
    /// (a second colon in the last path segment); or ends with a bare <c>@</c> with no
    /// digest text after it; or has a digest whose hash body (after the algorithm
    /// prefix, e.g. <c>"sha256:"</c>) is empty or not valid hexadecimal (e.g.
    /// <c>"mongo@sha256:"</c>); or resolves to an empty repository (e.g.
    /// <c>"@sha256:abc"</c> or <c>":8.0"</c>); or has a repository containing an empty
    /// path segment (leading, trailing, or doubled <c>/</c>, e.g. <c>"myorg/"</c>,
    /// <c>"/mongo"</c>, <c>"a//b"</c>).
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

        // MN5 fix: a leading/trailing-whitespace-padded reference (e.g. " mongo:8.0") is not
        // all-whitespace, so it survives the check above, but it is still degenerate author
        // YAML — passing " mongo" through to Aspire's WithImage(repository, tag) throws an
        // ArgumentOutOfRangeException from deep inside the builder call, INSIDE the Configure
        // closure, well past the "reject before any builder mutation" point every other
        // malformed-input case in this method honours. Reject it here, loudly and early,
        // instead of silently trimming (trimming would make "image: ' mongo:8.0'" and
        // "image: 'mongo:8.0'" behave identically with no author-visible signal that the
        // leading space was ever there).
        if (reference != reference.Trim())
        {
            throw new ArgumentException(
                $"Image reference '{reference}' has leading or trailing whitespace, which is " +
                "never part of a valid image reference - remove it.",
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

            // M4 fix: "mongo@sha256:" previously passed this far — digest.Length is 7
            // ("sha256:"), so the check above never fires — and ApplyImageOverrides went on to
            // call WithImageSHA256("") with the empty hash body, which Aspire/Docker silently
            // resolves as if no digest (and no tag) had been given at all, i.e. ":latest". A
            // one-character typo in a digest-pinned reference must be a loud parse failure, not
            // a silent fall-through to the floating tag. Validate the hash BODY — everything
            // after the algorithm's own ':' separator, or the whole digest string when it
            // carries no ':' at all — is both present and valid hexadecimal.
            var digestAlgorithmSeparator = digest.IndexOf(':', StringComparison.Ordinal);
            var digestBody = digestAlgorithmSeparator >= 0
                ? digest[(digestAlgorithmSeparator + 1)..]
                : digest;
            if (digestBody.Length == 0)
            {
                throw new ArgumentException(
                    $"Image reference '{reference}' has a digest ('{digest}') with no hash " +
                    "characters after its algorithm prefix.",
                    nameof(reference));
            }

            if (!IsHexString(digestBody))
            {
                throw new ArgumentException(
                    $"Image reference '{reference}' has a digest ('{digest}') whose hash body " +
                    $"'{digestBody}' is not valid hexadecimal.",
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

            // MN4 fix: a SECOND ':' inside the tag (e.g. "mongo:8.0:extra") is not a valid tag —
            // Docker tags never contain ':' themselves (that is exactly why a ':' is usable as
            // the tag separator in the first place). Reject rather than silently accepting
            // "8.0:extra" as a tag string Aspire/Docker would then fail on downstream, further
            // from the author's YAML.
            if (tag.Contains(':', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Image reference '{reference}' has a tag ('{tag}') containing ':', which " +
                    "is never valid in a Docker tag.",
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

        // MN4 fix: an empty path segment — leading ("/mongo"), trailing ("myorg/"), or doubled
        // ("a//b") — is not a valid repository path. None of these trip the repository-is-empty
        // check above (the repository string itself is non-empty), so a stray extra '/' would
        // otherwise reach Aspire's WithImage(repository, ...) call unchecked.
        if (repository.Split('/').Any(segment => segment.Length == 0))
        {
            throw new ArgumentException(
                $"Image reference '{reference}' has an empty path segment in its repository " +
                $"('{repository}') - repository segments must not be empty (no leading, " +
                "trailing, or doubled '/').",
                nameof(reference));
        }

        return new ImageReference(repository, tag, digest);
    }

    /// <summary>
    /// Returns <see langword="true"/> when every character in <paramref name="value"/> is a
    /// valid hexadecimal digit (<c>0-9</c>, <c>a-f</c>, <c>A-F</c>). Used to validate a digest's
    /// hash body (see the M4 fix remarks at its call site) — <paramref name="value"/> is assumed
    /// non-empty by the caller.
    /// </summary>
    private static bool IsHexString(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
