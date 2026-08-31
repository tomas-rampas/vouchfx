// Vouchfx.Engine.Abstractions — SecretReference (S05-B-01, §17).
//
// A strongly-typed parse of a ${secret:source/path} reference. The engine never
// stores secret *values* — only references — and resolution happens at run time,
// never at compile time (§17). This type carries no value; it is the
// compile-time/validation-time representation of a reference.
//
// "At run time" rather than "at step-execution time", deliberately: a step's field does
// resolve at step-execution time, but this type also serves environment-level
// `security.clientKeyPassword`, which resolves when the certificate material is first used
// — after the topology is up and BEFORE any step runs. The compile-time prohibition is the
// invariant common to both; the exact moment is the caller's property, not this type's.
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
/// to a secret value that the engine resolves at run time, never at compile time.
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
/// The exact resolution moment belongs to the CONSUMING field, not to this type: a step's
/// substitutable field resolves at step-execution time, while environment-level
/// <c>security.clientKeyPassword</c> resolves when the certificate material is first used —
/// after the topology is up and before any step runs. What both share, and what §17 fixes, is
/// that neither resolves at compile time.
/// </para>
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

    /// <summary>
    /// The refusal <see cref="ValidateSecretBearingField"/> reports for a field that MAY ITSELF
    /// HOLD A SECRET and does not carry a single, whole, well-formed reference — it names the
    /// fault WITHOUT quoting the declared text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>There are TWO refusals in the tree for this fault, not one</strong>, and that is a
    /// deliberate position rather than drift left unrecorded. This one is the VALIDATION-time
    /// refusal, reported by <see cref="ValidateSecretBearingField"/> and prefixed by the caller
    /// with a field path. <c>SecurityConfigurationAccessor</c>'s
    /// <c>SecurityMaterialException</c> is the RUN-time one, raised when the certificate material
    /// is loaded. They differ in wording, and may: they address different audiences at different
    /// stages, and the run-time one names <c>clientKeyPassword</c> and carries a corrective
    /// example because by then the author has one specific field in hand. This text carries no
    /// example, because it is field-agnostic and an example beside a withheld value invites a
    /// reader to mistake the one for the other. Both withhold the declared value, which is the
    /// property that matters; neither may be changed to quote it.
    /// </para>
    /// <para>
    /// <see langword="internal"/>: the only consumers are this type itself and, through
    /// <c>InternalsVisibleTo</c>, the Abstractions test project. Callers receive the text through
    /// <c>error</c> and never name it, so it is not part of the shipped surface — which also
    /// means a correction to it reaches every consumer on recompile rather than being baked into
    /// their assemblies as a <see langword="const"/> literal would be.
    /// </para>
    /// <para>
    /// The value is WITHHELD rather than escaped: escaping makes text safe to RENDER, and the
    /// hazard this closes is disclosure, which no amount of escaping addresses.
    /// </para>
    /// </remarks>
    internal static readonly string WithheldValueMessage =
        "the declared value is not a single, whole secret reference. IT IS DELIBERATELY NOT "
        + "REPORTED HERE, because a value in this position may be the secret itself. Declare a "
        + "reference of the form '${secret:<source>/<path>}' and nothing else. Note that a stray "
        + "'${secret:' INSIDE the path counts: the path runs to the first '}', so a second "
        + "lead-in is swallowed into it rather than starting a token of its own. A literal is "
        + "refused by design (section 17), as is a reference with any text around it.";

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
    /// <remarks>
    /// This overload QUOTES the field text in its diagnostics, which is correct for a step's
    /// substitutable field — author-written template text, and quoting it is what makes the
    /// message actionable. A field that may hold the SECRET ITSELF must use
    /// <see cref="ValidateSecretBearingField"/> instead.
    /// </remarks>
    public static bool ValidateField(
        string fieldValue,
        IReadOnlyCollection<string> knownSources,
        out string? error) =>
        ValidateFieldCore(fieldValue, knownSources, out error, fieldMayBeSecret: false);

    /// <summary>
    /// Validates a field that may itself hold a SECRET VALUE rather than only a reference to one
    /// (<c>security.clientKeyPassword</c> is the case in this engine): the value must be one
    /// whole, well-formed reference naming a known source, and no diagnostic on the failing paths
    /// discloses the declared text.
    /// </summary>
    /// <param name="fieldValue">The raw field text to validate.</param>
    /// <param name="knownSources">The resolver source identifiers the engine can resolve.</param>
    /// <param name="error">
    /// On failure, an actionable British-English message; otherwise <see langword="null"/>. The
    /// caller prefixes it with the field path.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="fieldValue"/> is exactly one well-formed
    /// reference naming a known source; otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Two rules, applied here ATOMICALLY and in this order</strong>, rather than left to
    /// the caller to compose:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <strong>Whole-token.</strong> <see cref="TryParse"/> — the value must be exactly one
    ///     reference and nothing else. <see cref="ValidateField"/> alone cannot stand in for
    ///     this: it deliberately PERMITS a field carrying no sigil at all, which for a
    ///     secret-bearing field is the plaintext-literal case.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>Known source</strong>, evaluated with the malformed diagnostic in withholding
    ///     mode.
    ///   </description></item>
    /// </list>
    /// <para>
    /// They are atomic because a caller composing them from two public calls can get the ORDER
    /// wrong, and the wrong order discloses the value — the whole reason this method exists.
    /// </para>
    /// <para>
    /// <strong>Divergence from <see cref="ValidateField"/>, deliberate and worth knowing:</strong>
    /// an EMPTY or <see langword="null"/> value returns <see langword="false"/> here and
    /// <see langword="true"/> there. <see cref="ValidateField"/> permits it because a step's field
    /// may legitimately be empty text carrying no reference; for a secret-bearing field an empty
    /// value is a declaration that resolves to nothing, which cannot be honoured. Same reasoning
    /// as the plain-literal case, and the same rule 1 refuses both.
    /// </para>
    /// <para>
    /// <strong>The UNKNOWN-SOURCE message is identical to <see cref="ValidateField"/>'s</strong>,
    /// deliberately: it quotes <c>match.Value</c>, a whole well-formed reference, and §17 is
    /// explicit that a reference is a pointer and never a secret. That message is the one surface
    /// where the step and security callers must agree word for word (#387's asymmetry). Only the
    /// MALFORMED diagnostic differs between the two methods.
    /// </para>
    /// <para>
    /// <strong>Why the withholding decision lives in this type</strong> rather than at the call
    /// site: the condition selecting the malformed branch is this file's own sigil ARITHMETIC,
    /// not any property a caller can observe. A predicate written from outside gets it wrong —
    /// asking "is the source unknown?" is not the same question as "do the sigil counts agree?",
    /// and <c>${secret:nosuchsource/PASS${secret:LEAKED}</c> (schema-valid, so CLI-reachable)
    /// answers yes to the first and no to the second, so it takes the malformed branch while an
    /// outside predicate expects the unknown-source one. Measured leaking, then fixed here.
    /// </para>
    /// </remarks>
    public static bool ValidateSecretBearingField(
        string fieldValue,
        IReadOnlyCollection<string> knownSources,
        out string? error)
    {
        if (!TryParse(fieldValue, out _))
        {
            error = WithheldValueMessage;
            return false;
        }

        return ValidateFieldCore(fieldValue, knownSources, out error, fieldMayBeSecret: true);
    }

    private static bool ValidateFieldCore(
        string fieldValue,
        IReadOnlyCollection<string> knownSources,
        out string? error,
        bool fieldMayBeSecret)
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
            error = fieldMayBeSecret
                ? WithheldValueMessage
                : $"the field '{fieldValue}' contains a malformed secret reference; " +
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
