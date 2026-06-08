// Platform.Engine.Compilation — CsxAssembler (§13.3.1, §5).
//
// Promotes the multi-step CSX splice pattern that was previously duplicated as
// a test helper (ReferenceProviderEndToEndTests, HttpRestEmitTests) into a
// first-class production type.  This is the canonical form of the splice and
// the single place where §13.3.1 dedup rules are enforced.
using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Platform.Sdk;

namespace Platform.Engine.Compilation;

/// <summary>
/// A fully assembled CSX source string ready to hand to
/// <see cref="RoslynScriptCompiler.CompileOnce"/>, together with the ordered
/// list of step identifiers whose fragments were merged to produce it.
/// </summary>
/// <param name="CsxSource">
/// The assembled C# script source.  Contains deduplicated <c>using</c>
/// directives, helper class definitions, and the concatenated statement blocks
/// from every contributing fragment — in the order the steps were supplied to
/// <see cref="CsxAssembler.Assemble"/>.
/// </param>
/// <param name="StepIds">
/// The step identifiers in the order they were passed to
/// <see cref="CsxAssembler.Assemble"/>.  Preserved here so callers can
/// correlate compiled artefacts back to the originating YAML steps.
/// </param>
public sealed record AssembledScript(string CsxSource, IReadOnlyList<string> StepIds);

/// <summary>
/// Merges a sequence of per-step <see cref="CsxFragment"/> contributions into a
/// single <see cref="AssembledScript"/> that is ready for Roslyn compilation.
/// </summary>
/// <remarks>
/// <para>
/// This is the production form of the splice pattern previously duplicated
/// across test helpers.  It enforces the §13.3.1 composition rules that prevent
/// <c>using</c>-directive and helper-class collisions when multiple providers
/// contribute fragments to the same compiled scenario.
/// </para>
/// <para>
/// <strong>Dedup rules (§13.3.1):</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <strong>Usings:</strong> collected across all fragments; deduplicated
///       preserving first-seen order (<c>HashSet&lt;string&gt;</c> guard + ordered
///       list); emitted as <c>using {ns};</c> lines.  An entry that already
///       contains the <c>using</c> keyword, a semicolon, or whitespace is
///       rejected with <see cref="CsxAssemblyException"/> — only bare namespace
///       strings are accepted.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Helpers:</strong> collected across all fragments; deduplicated
///       by declared class name (extracted via regex).  If two entries share the
///       same declared class name but have different source text, a
///       <see cref="CsxAssemblyException"/> is thrown — providers must emit
///       byte-identical helper source across all step instances within one suite.
///       Identical entries are silently kept as one.  Entries with no detectable
///       <c>static class</c> name are included but deduplicated by exact string.
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Statement blocks:</strong> concatenated in the given step order,
///       separated by a newline.
///     </description>
///   </item>
/// </list>
/// </remarks>
public static class CsxAssembler
{
    // Matches the declared class name of a static class in a helper source entry.
    // Accepts optional access modifiers before "static class".
    // Example: "internal static class DbAssertPostgres_Helpers { … }"  → "DbAssertPostgres_Helpers"
    private static readonly Regex StaticClassNamePattern =
        new(@"\bstatic\s+class\s+(\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Whitespace characters used to validate that a RequiredUsings entry is a bare
    // namespace string (no spaces, tabs, CR or LF).
    private static readonly SearchValues<char> WhitespaceChars =
        SearchValues.Create(new[] { ' ', '\t', '\r', '\n' });

    /// <summary>
    /// Merges <paramref name="steps"/> into a single <see cref="AssembledScript"/>,
    /// enforcing the §13.3.1 dedup and validation rules for usings and helpers.
    /// </summary>
    /// <param name="steps">
    /// Ordered sequence of <c>(stepId, fragment)</c> pairs.  The order determines
    /// both the statement-block concatenation order and the
    /// <see cref="AssembledScript.StepIds"/> list.  May be empty; an empty input
    /// produces a no-op script with empty <see cref="AssembledScript.StepIds"/>.
    /// </param>
    /// <returns>
    /// An <see cref="AssembledScript"/> whose <see cref="AssembledScript.CsxSource"/>
    /// is ready to pass to <see cref="RoslynScriptCompiler.CompileOnce"/> and whose
    /// <see cref="AssembledScript.StepIds"/> preserves the input order.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="steps"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="CsxAssemblyException">
    /// Thrown when a <see cref="CsxFragment.RequiredUsings"/> entry is not a bare
    /// namespace string, or when two <see cref="CsxFragment.RequiredHelpers"/>
    /// entries share a class name but have different source text.
    /// </exception>
    public static AssembledScript Assemble(
        IReadOnlyList<(string StepId, CsxFragment Fragment)> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Count == 0)
            return new AssembledScript(string.Empty, Array.Empty<string>());

        // ── Usings ────────────────────────────────────────────────────────────
        // Collect bare namespace strings, dedup preserving first-seen order.
        var usingsSeen = new HashSet<string>(StringComparer.Ordinal);
        var usingsOrdered = new List<string>();

        // ── Helpers ───────────────────────────────────────────────────────────
        // Dedup by declared class name; collision = same name, different source.
        // Entries with no detectable class name are deduped by exact string.
        var helpersByClassName = new Dictionary<string, string>(StringComparer.Ordinal);
        var helpersByExactSource = new HashSet<string>(StringComparer.Ordinal);
        var helpersOrdered = new List<string>();

        // ── Statement blocks ──────────────────────────────────────────────────
        var blocks = new List<string>(steps.Count);
        var stepIds = new List<string>(steps.Count);

        foreach (var (stepId, fragment) in steps)
        {
            stepIds.Add(stepId);

            // ── Process usings ─────────────────────────────────────────────
            foreach (var ns in fragment.RequiredUsings)
            {
                ValidateBareNamespace(ns);

                if (usingsSeen.Add(ns))
                    usingsOrdered.Add(ns);
            }

            // ── Process helpers ────────────────────────────────────────────
            foreach (var helperSource in fragment.RequiredHelpers)
            {
                var className = ExtractStaticClassName(helperSource);

                if (className is not null)
                {
                    if (helpersByClassName.TryGetValue(className, out var existingSource))
                    {
                        // Same class name seen before.
                        if (!string.Equals(existingSource, helperSource, StringComparison.Ordinal))
                        {
                            throw new CsxAssemblyException(
                                $"Helper class '{className}' was declared by two fragments with " +
                                "different source text.  Providers must emit byte-identical helper " +
                                "source for a given class across all step instances within one suite " +
                                "(§13.3.1).  Ensure both steps use the same provider version and " +
                                "that the helper does not embed step-specific data.");
                        }
                        // Identical — skip; already in the ordered list.
                    }
                    else
                    {
                        helpersByClassName[className] = helperSource;
                        helpersOrdered.Add(helperSource);
                    }
                }
                else
                {
                    // No detectable class name — dedup by exact string.
                    if (helpersByExactSource.Add(helperSource))
                        helpersOrdered.Add(helperSource);
                }
            }

            // ── Collect block ──────────────────────────────────────────────
            blocks.Add(fragment.StatementBlock);
        }

        // ── Assemble final source ─────────────────────────────────────────────
        var sb = new StringBuilder();

        foreach (var ns in usingsOrdered)
        {
            sb.Append("using ").Append(ns).AppendLine(";");
        }

        if (usingsOrdered.Count > 0)
            sb.AppendLine();

        foreach (var helper in helpersOrdered)
        {
            sb.AppendLine(helper);
        }

        if (helpersOrdered.Count > 0)
            sb.AppendLine();

        sb.Append(string.Join("\n", blocks));

        return new AssembledScript(sb.ToString(), stepIds);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Validates that <paramref name="ns"/> is a bare namespace string.
    /// Throws <see cref="CsxAssemblyException"/> if it contains the <c>using</c>
    /// keyword, a semicolon, or leading/trailing whitespace.
    /// </summary>
    private static void ValidateBareNamespace(string ns)
    {
        // A bare namespace must not contain the 'using' keyword, a semicolon,
        // or any whitespace (namespaces are dot-separated identifiers only).
        if (ns.Contains("using ", StringComparison.OrdinalIgnoreCase) ||
            ns.Contains(';') ||
            ns.AsSpan().IndexOfAny(WhitespaceChars) >= 0)
        {
            throw new CsxAssemblyException(
                $"RequiredUsings entry '{ns}' is not a bare namespace string.  " +
                "Providers must supply bare namespace strings only — the engine emits " +
                "the 'using' keyword and the trailing semicolon (§13.3.1).  " +
                "Remove the 'using ' prefix and/or the trailing ';' from the entry.");
        }
    }

    /// <summary>
    /// Extracts the declared class name from a helper source entry, or returns
    /// <see langword="null"/> if no <c>static class</c> declaration is found.
    /// </summary>
    private static string? ExtractStaticClassName(string helperSource)
    {
        var m = StaticClassNamePattern.Match(helperSource);
        return m.Success ? m.Groups[1].Value : null;
    }
}
