// Vouchfx.Sdk — SecretHelper (S05-B-02, §17).
//
// Provides the compile-time constant source for the Secret_Helpers static class
// that providers splice into their RequiredHelpers set, exactly mirroring
// SubstituteHelper (S04-B-03). Deduplication is handled by the CsxAssembler
// helper-dedup logic (§13.3.1).
//
// Design constraints (§13.3.1, §17):
//   • The helper is a static class whose name begins with the shared prefix
//     'Secret_' so it does not collide with provider-specific helpers.
//   • All types are fully-qualified — the helper compiles independently of any
//     'using' ordering in the surrounding script. ISecretAccessor / SecretString
//     resolve because RoslynScriptCompiler references Vouchfx.Engine.Abstractions.
//   • No 'using var' — CSX disallows it.
//   • 'Resolve' uses a compiled-once static readonly Regex.
//   • The revealed value is a TRANSIENT feeding a sink; it is NEVER written back to
//     Vars. No secret value is ever returned to the engine — secret-safe by
//     construction (§17).
//   • Byte-identical across providers so CsxAssembler dedupes it to one copy.
namespace Vouchfx.Sdk;

/// <summary>
/// Supplies the canonical source text for the <c>Secret_Helpers</c> static class
/// that providers splice into their <see cref="CsxFragment.RequiredHelpers"/> to
/// resolve <c>${secret:source/path}</c> references at step-execution time
/// (S05-B-02, §17).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Source"/> is byte-identical for every provider that includes it, which
/// satisfies the <see cref="CsxFragment"/> deduplication invariant (§13.3.1): the
/// <c>CsxAssembler</c> will include the helper class exactly once regardless of how
/// many providers reference it.
/// </para>
/// <para>
/// Resolution (the canonical wrapping a provider emits): a field is resolved in a
/// SINGLE left-to-right pass via <c>Secret_Helpers.ResolveTemplate(Secrets, Vars, …)</c>,
/// which handles both <c>${secret:source/path}</c> tokens and <c>{placeholder}</c>
/// tokens over the original template text.  Doing both in one pass is the security
/// property (§17): a substituted placeholder value is never re-scanned for secret
/// tokens (no secret-reference injection) and a revealed secret value is never
/// re-scanned for placeholders (no secret corruption).  The revealed value is consumed
/// immediately at the injection sink and never written back to <c>Vars</c>.  The older
/// single-purpose <c>Resolve(Secrets, …)</c> remains for callers that handle
/// substitution separately.
/// </para>
/// <para>
/// Secret safety (§17): a missing or unknown secret throws
/// <c>Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException</c> from inside
/// <c>Resolve</c>; the caller MUST invoke this method inside its own guarded region
/// so the exception maps to a step-scoped <c>Verdict.EnvironmentError</c> rather than
/// a scenario-level abort.
/// </para>
/// </remarks>
public static class SecretHelper
{
    /// <summary>
    /// The full C# source text of the <c>Secret_Helpers</c> static class.
    /// </summary>
    /// <remarks>
    /// Paste this constant as a single element of
    /// <see cref="CsxFragment.RequiredHelpers"/>; the assembler will deduplicate it
    /// across all steps in the suite.
    /// </remarks>
    public const string Source =
        "static class Secret_Helpers\n" +
        "{\n" +
        "    // Compiled-once token regex — matches every ${secret:source/path} reference.\n" +
        "    // Mirrors the SecretReference grammar (S05-B-01): source is [A-Za-z0-9_-]+,\n" +
        "    // path is [^}]+ (everything up to the closing brace).\n" +
        "    private static readonly System.Text.RegularExpressions.Regex s_pattern =\n" +
        "        new System.Text.RegularExpressions.Regex(\n" +
        "            @\"\\$\\{secret:[A-Za-z0-9_-]+/[^}]+\\}\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.Compiled);\n" +
        "\n" +
        "    // Compiled-once COMBINED regex used by ResolveTemplate. A single alternation\n" +
        "    // matches EITHER a secret token (tried first) OR a {placeholder} identifier,\n" +
        "    // so one left-to-right pass over the ORIGINAL template handles both. Because\n" +
        "    // the pass never re-scans the text it has already produced, a revealed secret\n" +
        "    // value is never re-substituted (no placeholder corruption) and a substituted\n" +
        "    // placeholder value is never secret-resolved (no secret-reference injection, §17).\n" +
        "    // Named groups disambiguate which alternative fired without re-parsing m.Value.\n" +
        "    private static readonly System.Text.RegularExpressions.Regex s_combined =\n" +
        "        new System.Text.RegularExpressions.Regex(\n" +
        "            @\"(?<secret>\\$\\{secret:[A-Za-z0-9_-]+/[^}]+\\})|(?<ph>\\{(?<phName>[A-Za-z_][A-Za-z0-9_]*)\\})\",\n" +
        "            System.Text.RegularExpressions.RegexOptions.Compiled);\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Replaces every <c>${secret:source/path}</c> token in <paramref name=\"template\"/>\n" +
        "    /// with the revealed value resolved through <paramref name=\"secrets\"/>.\n" +
        "    /// The revealed value is a transient destined for an injection sink; it is\n" +
        "    /// NEVER written back to Vars (§17).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// A missing or unknown secret throws\n" +
        "    /// <c>Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException</c>.\n" +
        "    /// <strong>Callers MUST invoke this inside their own guarded region</strong>\n" +
        "    /// so the exception maps to a step-scoped <c>Verdict.EnvironmentError</c>\n" +
        "    /// (§12.1) rather than a scenario-level abort.\n" +
        "    /// </remarks>\n" +
        "    internal static string Resolve(\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        string template)\n" +
        "    {\n" +
        "        return s_pattern.Replace(template, m =>\n" +
        "        {\n" +
        "            // m.Value is the raw '${secret:source/path}' token. Resolve it through\n" +
        "            // the accessor and reveal at the sink. Reveal() is the single, greppable\n" +
        "            // escape hatch (§17); the value is not retained beyond this expression.\n" +
        "            return secrets.Resolve(m.Value).Reveal();\n" +
        "        });\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>\n" +
        "    /// Resolves BOTH <c>${secret:source/path}</c> tokens AND <c>{placeholder}</c>\n" +
        "    /// tokens in <paramref name=\"template\"/> in a SINGLE left-to-right pass over the\n" +
        "    /// ORIGINAL text. Secret tokens are revealed via <paramref name=\"secrets\"/>;\n" +
        "    /// placeholders are resolved from <paramref name=\"vars\"/> with exactly the same\n" +
        "    /// semantics as <c>Substitute_Helpers.Resolve</c> (Convert.ToString with the\n" +
        "    /// invariant culture; empty string when the key is absent or the value is null).\n" +
        "    /// </summary>\n" +
        "    /// <remarks>\n" +
        "    /// The single pass is the security property (§17): the output of one match is\n" +
        "    /// never re-scanned, so a placeholder whose runtime value happens to contain the\n" +
        "    /// literal text <c>${secret:...}</c> is NOT secret-resolved (no secret-reference\n" +
        "    /// injection), and a revealed secret value that happens to contain <c>{ident}</c>\n" +
        "    /// is NOT placeholder-substituted (no secret corruption). A missing or unknown\n" +
        "    /// secret throws\n" +
        "    /// <c>Vouchfx.Engine.Abstractions.Secrets.SecretResolutionException</c>;\n" +
        "    /// <strong>callers MUST invoke this inside their own guarded region</strong> so\n" +
        "    /// the exception maps to a step-scoped <c>Verdict.EnvironmentError</c> (§12.1).\n" +
        "    /// </remarks>\n" +
        "    internal static string ResolveTemplate(\n" +
        "        Vouchfx.Engine.Abstractions.Secrets.ISecretAccessor secrets,\n" +
        "        System.Collections.Generic.IDictionary<string, object?> vars,\n" +
        "        string template)\n" +
        "    {\n" +
        "        return s_combined.Replace(template, m =>\n" +
        "        {\n" +
        "            if (m.Groups[\"secret\"].Success)\n" +
        "            {\n" +
        "                // A secret token: resolve through the accessor and reveal at the sink.\n" +
        "                // Reveal() is the single, greppable escape hatch (§17); the value is\n" +
        "                // not retained beyond this expression and is never re-scanned.\n" +
        "                return secrets.Resolve(m.Value).Reveal();\n" +
        "            }\n" +
        "\n" +
        "            // Otherwise a {placeholder}: resolve from vars with the SAME semantics as\n" +
        "            // Substitute_Helpers.Resolve (Convert.ToString / InvariantCulture; empty\n" +
        "            // string when absent or null).\n" +
        "            var key = m.Groups[\"phName\"].Value;\n" +
        "            if (vars.TryGetValue(key, out var val) && val is not null)\n" +
        "                return System.Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;\n" +
        "            return string.Empty;\n" +
        "        });\n" +
        "    }\n" +
        "}";
}
