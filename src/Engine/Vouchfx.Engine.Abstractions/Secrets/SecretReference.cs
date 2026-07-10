// Vouchfx.Engine.Abstractions — SecretReference (S05-B-01, §17).
//
// A strongly-typed parse of a ${secret:source/path} reference. The engine never
// stores secret *values* — only references — and resolution happens at step
// execution time, not compile time (§17). This type carries no value; it is the
// compile-time/validation-time representation of a reference.
//
// Design constraints (§17, CLAUDE.md "Secrets"):
//   • References only — never literals. This type models the reference syntax.
//   • The grammar must NOT collide with the {placeholder} substitution grammar
//     (B-03, regex \{([A-Za-z_][A-Za-z0-9_]*)\}). The secret sigil is the
//     three-character lead "${" + "secret:" which a bare {name} placeholder can
//     never produce, so the two never overlap.
//   • Pure BCL only — Vouchfx.Engine.Abstractions is a leaf library that
//     references nothing beyond the in-box framework.
//   • Compiled-once Regex (RegexOptions.Compiled) to keep the validation pass
//     allocation-light across large suites.

using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Vouchfx.Engine.Abstractions.Secrets;

/// <summary>
/// A parsed <c>${secret:source/path}</c> reference (§17): the declarative pointer
/// to a secret value that the engine resolves at step-execution time.
/// </summary>
/// <param name="Source">
/// The resolver source identifier (e.g. <c>env</c>, <c>vault</c>) — the segment
/// between <c>${secret:</c> and the first <c>/</c>.
/// </param>
/// <param name="Path">
/// The source-specific lookup path — everything after the first <c>/</c> up to
/// the closing <c>}</c>. May itself contain <c>/</c> and punctuation (e.g. a
/// Vault KV path such as <c>kv/data/db</c>).
/// </param>
/// <param name="Raw">
/// The verbatim token text exactly as it appeared in the field, including the
/// <c>${secret:</c> sigil and closing brace. Preserving the raw text lets the
/// reproducibility envelope hash the <em>reference</em> (never the value, §17)
/// and lets callers perform a literal replacement on the original field.
/// </param>
/// <remarks>
/// <para>
/// This record models the <em>reference</em> only. It deliberately carries no
/// resolved value and exposes no value-bearing member: redaction is enforced at
/// the resolution boundary (<c>SecretString</c>), not here.
/// </para>
/// <para>
/// The accepted source charset is <c>[A-Za-z0-9_-]</c>; the path is any run of
/// characters that are not the closing brace (<c>[^}]+</c>).
/// </para>
/// </remarks>
public sealed record SecretReference(string Source, string Path, string Raw)
{
    /// <summary>
    /// The literal lead-in for a secret reference: <c>${secret:</c>. A field that
    /// does not contain this substring contains no secret reference at all.
    /// </summary>
    public const string Sigil = "${secret:";

    // Compiled-once grammar for a secret reference. Anchored at neither end so it
    // can locate tokens embedded in a larger field value via FindAll; TryParse
    // additionally requires a whole-string match.
    //   source : [A-Za-z0-9_-]+   (resolver id)
    //   path   : [^}]+            (everything up to the closing brace)
    // Known limitation: '}' is a HARD path terminator — a literal '}' inside a secret
    // path truncates the reference at that point (the path stops at the first '}', and
    // the remainder is treated as trailing literal text). MVP sources (env, vault) use
    // no '}' in their lookup paths, so this is acceptable for v1; revisit if a future
    // source needs '}' in its path (would require an escape rule, not just a wider class).
    private static readonly Regex s_pattern = new(
        @"\$\{secret:(?<source>[A-Za-z0-9_-]+)/(?<path>[^}]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to parse <paramref name="token"/> as a single, complete secret
    /// reference — the entire string must be exactly one
    /// <c>${secret:source/path}</c> token with no surrounding literal text.
    /// </summary>
    /// <param name="token">The candidate token text.</param>
    /// <param name="reference">
    /// On success, the parsed <see cref="SecretReference"/>; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="token"/> is exactly one
    /// well-formed secret reference; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryParse(string token, out SecretReference? reference)
    {
        reference = null;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var match = s_pattern.Match(token);

        // Require the match to span the WHOLE token — TryParse is for a single,
        // standalone reference, not a reference embedded in literal text.
        if (!match.Success || match.Index != 0 || match.Length != token.Length)
        {
            return false;
        }

        reference = new SecretReference(
            match.Groups["source"].Value,
            match.Groups["path"].Value,
            match.Value);
        return true;
    }

    /// <summary>
    /// Finds every well-formed secret reference embedded in <paramref name="text"/>.
    /// A field value may freely mix literal text and references (e.g.
    /// <c>Bearer ${secret:env/API_TOKEN}</c>).
    /// </summary>
    /// <param name="text">The field value to scan.</param>
    /// <returns>
    /// Every <see cref="SecretReference"/> found, in left-to-right order; an empty
    /// list when <paramref name="text"/> contains none.
    /// </returns>
    public static IReadOnlyList<SecretReference> FindAll(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return System.Array.Empty<SecretReference>();
        }

        var results = new List<SecretReference>();
        foreach (Match match in s_pattern.Matches(text))
        {
            results.Add(new SecretReference(
                match.Groups["source"].Value,
                match.Groups["path"].Value,
                match.Value));
        }

        return results;
    }

    /// <summary>
    /// Validates the secret references in a single field value against the set of
    /// resolver sources the engine currently knows how to resolve (§17).
    /// </summary>
    /// <param name="fieldValue">The raw field text to validate.</param>
    /// <param name="knownSources">
    /// The resolver source identifiers the engine can resolve this sprint (e.g.
    /// <c>env</c>). Vault is added in Sprint 8 by extending this set — no change
    /// to this method is required.
    /// </param>
    /// <param name="error">
    /// On failure, an actionable British-English message naming the problem;
    /// otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when every secret token is well-formed and uses a
    /// known source, OR when the field contains no secret sigil at all (plain
    /// literals are permitted; general secret-shaped-literal linting is a future
    /// rule and is deliberately not implemented here). Otherwise
    /// <see langword="false"/>.
    /// </returns>
    public static bool ValidateField(
        string fieldValue,
        IReadOnlyCollection<string> knownSources,
        out string? error)
    {
        error = null;

        // No sigil anywhere → no secret references → nothing to validate.
        // Plain literals are explicitly allowed (§17 future-rule note).
        if (string.IsNullOrEmpty(fieldValue)
            || fieldValue.IndexOf(Sigil, System.StringComparison.Ordinal) < 0)
        {
            return true;
        }

        var matches = s_pattern.Matches(fieldValue);

        // The field contains the literal sigil but the well-formed-token count does
        // not account for every occurrence → at least one token is malformed (e.g.
        // a missing '/path', or an unterminated brace).
        var sigilCount = CountOccurrences(fieldValue, Sigil);
        if (matches.Count < sigilCount)
        {
            error =
                $"the field '{fieldValue}' contains a malformed secret reference; " +
                $"the expected form is '{Sigil}<source>/<path>}}' " +
                "(for example '${secret:env/API_TOKEN}').";
            return false;
        }

        // Every token is well-formed; confirm each names a known source.
        foreach (Match match in matches)
        {
            var source = match.Groups["source"].Value;
            if (!ContainsOrdinal(knownSources, source))
            {
                error =
                    $"the secret reference '{match.Value}' names an unknown source '{source}'; " +
                    $"known sources are: {FormatKnownSources(knownSources)}.";
                return false;
            }
        }

        return true;
    }

    // Counts non-overlapping occurrences of a literal substring (ordinal).
    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    // Ordinal membership test that does not allocate an enumerator-state machine
    // beyond the collection's own iterator (knownSources is tiny).
    private static bool ContainsOrdinal(IReadOnlyCollection<string> sources, string value)
    {
        foreach (var s in sources)
        {
            if (string.Equals(s, value, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatKnownSources(IReadOnlyCollection<string> sources)
    {
        if (sources.Count == 0)
        {
            return "(none configured)";
        }

        return string.Join(", ", sources);
    }
}
