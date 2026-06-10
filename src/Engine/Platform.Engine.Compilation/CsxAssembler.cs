// Platform.Engine.Compilation — CsxAssembler (§13.3.1, §5).
//
// Promotes the multi-step CSX splice pattern that was previously duplicated as
// a test helper (ReferenceProviderEndToEndTests, HttpRestEmitTests) into a
// first-class production type.  This is the canonical form of the splice and
// the single place where §13.3.1 dedup rules are enforced.
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Platform.Engine.Abstractions;
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
/// A single step's compilation plan: the provider's <see cref="CsxFragment"/>
/// contribution plus the RETRY parameters the engine needs to decide whether the
/// fragment's statement block is spliced directly (IMMEDIATE) or wrapped in the
/// engine-owned RETRY polling loop (<c>verifyMode: RETRY</c>, §7).
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="Retry"/> is <see langword="false"/> the
/// <see cref="CsxFragment.StatementBlock"/> is appended verbatim, exactly as the
/// legacy tuple-based <see cref="CsxAssembler.Assemble(IReadOnlyList{ValueTuple{string, CsxFragment}})"/>
/// overload does.  When <see cref="Retry"/> is <see langword="true"/> the block is
/// wrapped in a per-step local <c>async</c> attempt function and an
/// <c>await Platform.Engine.Abstractions.Retry.RetryRunner.PollAsync(...)</c> call so
/// the engine owns the backoff timeline — authors never write <c>Thread.Sleep</c>
/// (§7).  The provider block writes its <see cref="StepOutcome"/> into
/// <c>Vars[VarKeys.Outcome(sanitisedId)]</c> on every attempt; the generated wrapper
/// reads it back, removes it (clean slate for the next poll), and returns it so the
/// runner can classify the verdict (§12.1).
/// </para>
/// </remarks>
/// <param name="StepId">
/// The raw step identifier from the YAML source (may contain hyphens).  It is
/// sanitised via <see cref="CsxFragment.SanitiseId"/> before being spliced into any
/// generated identifier, and preserved un-sanitised in
/// <see cref="AssembledScript.StepIds"/>.
/// </param>
/// <param name="Fragment">
/// The provider's CSX contribution for this step.
/// </param>
/// <param name="Retry">
/// <see langword="true"/> to wrap <paramref name="Fragment"/>'s statement block in the
/// engine-owned RETRY polling loop; <see langword="false"/> to splice it directly
/// (IMMEDIATE).
/// </param>
/// <param name="TimeoutMs">
/// The overall RETRY polling window in milliseconds, or <see langword="null"/> to let
/// <see cref="Platform.Engine.Abstractions.Retry.RetryRunner"/> apply its engine
/// default.  Ignored when <paramref name="Retry"/> is <see langword="false"/>.
/// </param>
/// <param name="PollIntervalMs">
/// The base delay between RETRY attempts in milliseconds, or <see langword="null"/> to
/// let <see cref="Platform.Engine.Abstractions.Retry.RetryRunner"/> apply its engine
/// default.  Ignored when <paramref name="Retry"/> is <see langword="false"/>.
/// </param>
public sealed record StepCompilePlan(
    string StepId,
    CsxFragment Fragment,
    bool Retry,
    long? TimeoutMs,
    long? PollIntervalMs);

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

        // Back-compat shim: every legacy tuple maps to an IMMEDIATE (non-retry) plan,
        // then defers to the StepCompilePlan overload — the single real implementation.
        var plans = new List<StepCompilePlan>(steps.Count);
        foreach (var (stepId, fragment) in steps)
        {
            plans.Add(new StepCompilePlan(
                stepId, fragment, Retry: false, TimeoutMs: null, PollIntervalMs: null));
        }

        return Assemble(plans);
    }

    /// <summary>
    /// Merges <paramref name="steps"/> into a single <see cref="AssembledScript"/>,
    /// enforcing the §13.3.1 dedup and validation rules for usings and helpers, and —
    /// for each plan with <see cref="StepCompilePlan.Retry"/> set — wrapping the
    /// provider's statement block in the engine-owned RETRY polling loop (§7).
    /// </summary>
    /// <param name="steps">
    /// Ordered sequence of <see cref="StepCompilePlan"/> entries.  The order determines
    /// both the statement-block concatenation order and the
    /// <see cref="AssembledScript.StepIds"/> list.  May be empty; an empty input
    /// produces a no-op script with empty <see cref="AssembledScript.StepIds"/>.
    /// </param>
    /// <returns>
    /// An <see cref="AssembledScript"/> whose <see cref="AssembledScript.CsxSource"/>
    /// is ready to pass to <see cref="RoslynScriptCompiler.CompileOnce"/> and whose
    /// <see cref="AssembledScript.StepIds"/> preserves the input order (un-sanitised).
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="steps"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="CsxAssemblyException">
    /// Thrown when a <see cref="CsxFragment.RequiredUsings"/> entry is not a bare
    /// namespace string, or when two <see cref="CsxFragment.RequiredHelpers"/>
    /// entries share a class name but have different source text.
    /// </exception>
    public static AssembledScript Assemble(IReadOnlyList<StepCompilePlan> steps)
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

        foreach (var plan in steps)
        {
            var stepId = plan.StepId;
            var fragment = plan.Fragment;

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
            // IMMEDIATE: splice the provider block verbatim.
            // RETRY: wrap it in the engine-owned polling loop (§7).
            blocks.Add(plan.Retry
                ? WrapForRetry(stepId, fragment.StatementBlock, plan.TimeoutMs, plan.PollIntervalMs)
                : fragment.StatementBlock);
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
    /// Wraps a provider's IMMEDIATE statement block in the engine-owned RETRY polling
    /// loop (<c>verifyMode: RETRY</c>, §7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wrapper is built with a <see cref="StringBuilder"/> — <em>not</em> a
    /// <c>$$"""…"""</c> interpolation hole — because the spliced provider block may
    /// itself contain raw strings; nesting raw strings inside an interpolated raw string
    /// is brittle.  Pure concatenation keeps the braces correct.
    /// </para>
    /// <para>
    /// Every local identifier carries the sanitised step id as a suffix so concurrent
    /// steps never collide.  Fully-qualified type names are used throughout so the
    /// wrapper introduces no new <c>using</c> directives, and the body contains no
    /// <c>using var</c> (illegal in a Roslyn script body, §13.3.1).
    /// </para>
    /// <para>
    /// The provider block writes <c>Vars[outcomeKey]</c> on each attempt.  The generated
    /// local function reads it back, removes it (clean slate for the next poll), and
    /// returns it so <c>RetryRunner.PollAsync</c> can classify the verdict.  After
    /// <c>PollAsync</c> returns, <c>Vars[outcomeKey]</c> holds the FINAL outcome and
    /// <c>Vars[attemptsKey]</c> holds the <c>List&lt;AttemptRecord&gt;</c> — both consumed
    /// later by the runner (§14).
    /// </para>
    /// </remarks>
    private static string WrapForRetry(
        string stepId, string statementBlock, long? timeoutMs, long? pollIntervalMs)
    {
        var safe = CsxFragment.SanitiseId(stepId);

        var outcomeKey = VarKeys.Outcome(safe);
        var attemptsKey = VarKeys.Attempts(safe);

        // Emit the keys as quoted, escaped C# string literals (same idiom providers use).
        var outcomeLit = JsonSerializer.Serialize(outcomeKey);
        var attemptsLit = JsonSerializer.Serialize(attemptsKey);

        // Emit the long? parameters as C# numeric literals or 'null'.
        var timeoutLit = timeoutMs is { } t
            ? t.ToString(CultureInfo.InvariantCulture) + "L"
            : "null";
        var pollLit = pollIntervalMs is { } p
            ? p.ToString(CultureInfo.InvariantCulture) + "L"
            : "null";

        var sb = new StringBuilder();
        sb.Append('{').Append('\n');

        // Per-step attempt local function.  Fully-qualified types — no new usings.
        sb.Append("    async System.Threading.Tasks.Task<Platform.Engine.Abstractions.StepOutcome> __attempt_")
          .Append(safe)
          .Append("(System.Threading.CancellationToken __ct_")
          .Append(safe)
          .Append(")\n");
        sb.Append("    {\n");

        // The provider's original statement block, verbatim — it is already a { … } block.
        // Note: this block re-executes on every poll, so RETRY is intended for idempotent
        // assertion/expectation providers; a side-effecting block under verifyMode: RETRY
        // fires once per attempt (by design, §7).
        sb.Append(statementBlock).Append('\n');

        // Read the outcome the provider block wrote, remove it (clean slate), return it.
        sb.Append("        var __o_").Append(safe)
          .Append(" = Vars.TryGetValue(").Append(outcomeLit)
          .Append(", out var __raw_").Append(safe)
          .Append(") && __raw_").Append(safe)
          .Append(" is Platform.Engine.Abstractions.StepOutcome __so_").Append(safe).Append('\n');
        sb.Append("            ? __so_").Append(safe).Append('\n');
        sb.Append("            : new Platform.Engine.Abstractions.StepOutcome(Platform.Engine.Abstractions.Verdict.Inconclusive, 0L, null);\n");
        sb.Append("        Vars.Remove(").Append(outcomeLit).Append(");\n");
        sb.Append("        return __o_").Append(safe).Append(";\n");
        sb.Append("    }\n");

        // The engine-owned poll: writes the final StepOutcome + List<AttemptRecord> back
        // into Vars under outcomeKey / attemptsKey.  Method group → Func<CT, Task<…>>.
        sb.Append("    await Platform.Engine.Abstractions.Retry.RetryRunner.PollAsync(\n");
        sb.Append("        Vars, ").Append(outcomeLit).Append(", ").Append(attemptsLit)
          .Append(", ").Append(timeoutLit).Append(", ").Append(pollLit)
          .Append(", __attempt_").Append(safe).Append(");\n");

        sb.Append('}');

        return sb.ToString();
    }

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
