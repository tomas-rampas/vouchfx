// Platform.Sdk — SubstituteHelper (S04-B-03).
//
// Provides the compile-time constant source for the Substitute_Helpers static class
// that is spliced into every provider's RequiredHelpers set.  Deduplication is handled
// by the existing CsxAssembler helper-dedup logic (§13.3.1).
//
// Design constraints (§13.3.1, §17):
//   • The helper is a static class whose name begins with the shared prefix
//     'Substitute_' so it does not collide with provider-specific helpers.
//   • All types are fully-qualified — the helper compiles independently of any
//     'using' ordering in the surrounding script.
//   • No 'using var' — CSX disallows it.
//   • 'Resolve' uses a compiled-once static readonly Regex to avoid per-call
//     compilation overhead.
//   • Provenance tracking (G-01) is derived at COMPILE time from the field text;
//     'Resolve' carries no provenance state.  Values are NEVER returned to the
//     engine — this is secret-safe by construction (§17).
namespace Platform.Sdk;

/// <summary>
/// Supplies the canonical source text for the <c>Substitute_Helpers</c> static class
/// that providers splice into their <see cref="CsxFragment.RequiredHelpers"/> to
/// enable <c>{placeholder}</c> substitution at step-execution time (S04-B-03, DSL §3).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Source"/> is byte-identical for every provider that includes it, which
/// satisfies the <see cref="CsxFragment"/> deduplication invariant (§13.3.1): the
/// <c>CsxAssembler</c> will include the helper class exactly once regardless of how many
/// providers reference it.
/// </para>
/// <para>
/// Substitution syntax: every <c>{name}</c> token in a template string where
/// <c>name</c> matches <c>[A-Za-z_][A-Za-z0-9_]*</c> is replaced with the string
/// representation of <c>vars[name]</c> (or the empty string when the key is absent or
/// the value is <see langword="null"/>).  Braces that do not match an identifier (e.g.
/// JSON object syntax) are passed through unchanged.
/// </para>
/// <para>
/// Secret safety (§17): the helper resolves values at runtime but the engine's event
/// stream (G-01) records only the placeholder <em>names</em> — values are never emitted.
/// </para>
/// </remarks>
public static class SubstituteHelper
{
    /// <summary>
    /// The full C# source text of the <c>Substitute_Helpers</c> static class.
    /// </summary>
    /// <remarks>
    /// Paste this constant as a single element of
    /// <see cref="CsxFragment.RequiredHelpers"/>; the assembler will deduplicate it
    /// across all steps in the suite.
    /// </remarks>
    public const string Source =
        "static class Substitute_Helpers\n" +
        "{\n" +
        "    // Compiled-once token regex — matches every {identifier} placeholder.\n" +
        "    // Cheaper than a per-call Regex.Replace call and avoids the generated-regex\n" +
        "    // source-generator constraint (not available in CSX).\n" +
        "    private static readonly System.Text.RegularExpressions.Regex s_pattern =\n" +
        "        new System.Text.RegularExpressions.Regex(\n" +
        "            @\"\\{([A-Za-z_][A-Za-z0-9_]*)\\}\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.Compiled);\n" +
        "\n" +
        "    // Compiled-once safe-identifier regex used by ResolveIdentifier.\n" +
        "    // Allows letters, digits, underscore, and dot (covers schema.table).\n" +
        "    // The empty string is accepted (absent/null placeholder → empty → harmless).\n" +
        "    private static readonly System.Text.RegularExpressions.Regex s_safeIdentifier =\n" +
        "        new System.Text.RegularExpressions.Regex(\n" +
        "            @\"^[A-Za-z0-9_.]*$\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.Compiled);\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Replaces every <c>{name}</c> token in <paramref name=\"template\"/> with\n" +
        "    /// the string representation of <c>vars[name]</c>, or the empty string when\n" +
        "    /// the key is absent or the value is <see langword=\"null\"/>.\n" +
        "    /// Braces that do not match an identifier pattern are passed through unchanged.\n" +
        "    /// </summary>\n" +
        "    internal static string Resolve(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string template)\n" +
        "    {\n" +
        "        return s_pattern.Replace(template, m =>\n" +
        "        {\n" +
        "            var key = m.Groups[1].Value;\n" +
        "            if (vars.TryGetValue(key, out var val) && val is not null)\n" +
        "                return System.Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;\n" +
        "            return string.Empty;\n" +
        "        });\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Replaces every <c>{name}</c> token in <paramref name=\"template\"/> with\n" +
        "    /// the string representation of <c>vars[name]</c>, exactly as\n" +
        "    /// <see cref=\"Resolve\"/> does, but additionally validates each resolved\n" +
        "    /// value against a safe SQL-identifier charset before substitution.\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// <para>\n" +
        "    /// Permitted resolved characters: <c>[A-Za-z0-9_.]</c> (letters, digits,\n" +
        "    /// underscore, dot — covers qualified names such as <c>myschema.orders</c>).\n" +
        "    /// The empty string (absent or null placeholder) is also permitted and\n" +
        "    /// is harmless in SQL context.\n" +
        "    /// </para>\n" +
        "    /// <para>\n" +
        "    /// If any resolved value contains a character outside the permitted set\n" +
        "    /// (e.g. a space, semicolon, quote, or SQL keyword fragment), the method\n" +
        "    /// throws <see cref=\"System.InvalidOperationException\"/> with an actionable\n" +
        "    /// message naming the placeholder and the offending value.  The caller's\n" +
        "    /// <c>catch (System.Exception)</c> block maps this to\n" +
        "    /// <c>Verdict.EnvironmentError</c> (§12.1) so the test cannot run and the\n" +
        "    /// injection attempt is surfaced in the observation without executing SQL.\n" +
        "    /// </para>\n" +
        "    /// <para>\n" +
        "    /// Untrusted or non-identifier data MUST be bound through a parameterised\n" +
        "    /// SQL parameter (via <c>AddWithValue</c>) rather than substituted into\n" +
        "    /// the query text.\n" +
        "    /// </para>\n" +
        "    /// </remarks>\n" +
        "    internal static string ResolveIdentifier(\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string template)\n" +
        "    {\n" +
        "        return s_pattern.Replace(template, m =>\n" +
        "        {\n" +
        "            var key = m.Groups[1].Value;\n" +
        "            string resolved;\n" +
        "            if (vars.TryGetValue(key, out var val) && val is not null)\n" +
        "                resolved = System.Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;\n" +
        "            else\n" +
        "                resolved = string.Empty;\n" +
        "            if (!s_safeIdentifier.IsMatch(resolved))\n" +
        "                throw new System.InvalidOperationException(\n" +
        "                    \"db-assert query-text placeholder '{\" + key + \"}' resolved to '\" + resolved +\n" +
        "                    \"', which is not a safe SQL identifier; bind untrusted or non-identifier \" +\n" +
        "                    \"data through a parameter instead.\");\n" +
        "            return resolved;\n" +
        "        });\n" +
        "    }\n" +
        "}";
}
