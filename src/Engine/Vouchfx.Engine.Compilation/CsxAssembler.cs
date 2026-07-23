// Vouchfx.Engine.Compilation — CsxAssembler (§13.3.1, §5).
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
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Compilation;

/// <summary>
/// A fully assembled CSX source string ready to hand to
/// <see cref="RoslynScriptCompiler.CompileOnce"/>, together with the ordered
/// list of step identifiers whose fragments were merged to produce it.
/// </summary>
/// <param name="CsxSource">
/// The assembled C# script source.  Contains deduplicated <c>using</c>
/// directives, helper class definitions, and the concatenated statement blocks
/// from every contributing fragment — in the order the steps were supplied to
/// <see cref="CsxAssembler.Assemble(IReadOnlyList{StepCompilePlan})"/>.
/// </param>
/// <param name="StepIds">
/// The step identifiers in the order they were passed to
/// <see cref="CsxAssembler.Assemble(IReadOnlyList{StepCompilePlan})"/>.  Preserved here so callers can
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
/// <c>await Vouchfx.Engine.Abstractions.Retry.RetryRunner.PollAsync(...)</c> call so
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
/// The step's overall time budget in milliseconds, or <see langword="null"/> when the
/// step declares no <c>timeout</c>.  For a RETRY step this is the overall polling
/// window (<see langword="null"/> lets
/// <see cref="Vouchfx.Engine.Abstractions.Retry.RetryRunner"/> apply its engine
/// default).  For an IMMEDIATE step this is the enforced upper bound on the step
/// (§4 common step fields): the emitted wrapper arms a per-step
/// <see cref="System.Threading.CancellationTokenSource"/> with this budget, classifies
/// a body that observes the cancellation as <c>Inconclusive</c> (step-timeout), and
/// supersedes the outcome of a body that completes past the budget with the same
/// <c>Inconclusive</c> classification.  <see langword="null"/> or non-positive means
/// no IMMEDIATE enforcement (the provider's transport conventions are the only bound).
/// </param>
/// <param name="PollIntervalMs">
/// The base delay between RETRY attempts in milliseconds, or <see langword="null"/> to
/// let <see cref="Vouchfx.Engine.Abstractions.Retry.RetryRunner"/> apply its engine
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
            // IMMEDIATE: wrap the provider block in the per-step cancellation scope
            //   (the step-timeout upper bound, §4 — a no-op scope when the step
            //   declares no timeout).
            // RETRY: wrap it in the engine-owned polling loop (§7).
            blocks.Add(plan.Retry
                ? WrapForRetry(stepId, fragment.StatementBlock, plan.TimeoutMs, plan.PollIntervalMs)
                : WrapForImmediate(stepId, fragment.StatementBlock, plan.TimeoutMs));
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
    /// Wraps a provider's IMMEDIATE statement block in the per-step cancellation
    /// scope that enforces the step's <c>timeout</c> as an upper bound (§4 common
    /// step fields, issue #232).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every IMMEDIATE step — with or without a declared timeout — receives a
    /// step-scoped <see cref="System.Threading.CancellationToken"/> local named
    /// <c>__stepCt_&lt;safeId&gt;</c>.  This is the emitted-CSX convention providers
    /// use to make their client calls cooperatively cancellable: pass it into every
    /// awaited call that accepts a token, and rethrow an
    /// <see cref="OperationCanceledException"/> past the provider's own generic
    /// catch when the step token has fired (the assembler's wrapper then classifies
    /// it).  The RETRY wrapper exposes the same local name inside the attempt
    /// function, so provider bodies reference one token in both modes.
    /// </para>
    /// <para>
    /// <strong>No timeout declared:</strong> the token local aliases
    /// <see cref="System.Threading.CancellationToken.None"/> and the block is spliced
    /// with no try/catch, no timer and no post-check — byte-for-byte behavioural
    /// parity with the pre-#232 splice apart from the two token locals.
    /// </para>
    /// <para>
    /// <strong>Timeout declared:</strong> enforcement is two-fold and entirely
    /// cooperative (no task abandonment — a zombie body mutating the shared,
    /// non-thread-safe <c>Vars</c> dictionary while later steps run, or outliving
    /// the collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, is
    /// exactly the hazard the §5 memory model forbids):
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <strong>Early cut:</strong> a body that observes the token throws
    ///     <see cref="OperationCanceledException"/>; the wrapper's filtered catch
    ///     (token fired) writes an <c>Inconclusive</c> outcome with observation
    ///     <c>{"reason":"step-timeout","timeoutMs":N}</c> — timeout is Inconclusive,
    ///     never Fail (§12.1), mirroring the RETRY window semantics.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>Late enforcement:</strong> a body that ignores the token but
    ///     completes past the budget has its outcome superseded by the same
    ///     <c>Inconclusive</c> classification, with the superseded verdict preserved
    ///     in the observation (<c>"supersededVerdict"</c>).  This guarantees the
    ///     documented upper bound is enforced deterministically for every provider,
    ///     wired or not — a RETRY window elapse likewise discards what a late
    ///     attempt would have said.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Built with a <see cref="StringBuilder"/> for the same nested-raw-string
    /// reason as <see cref="WrapForRetry"/>; every engine-introduced local carries
    /// the sanitised step id suffix; fully-qualified type names throughout; no
    /// <c>using var</c> (illegal in a Roslyn script body, §13.3.1).
    /// </para>
    /// </remarks>
    private static string WrapForImmediate(string stepId, string statementBlock, long? timeoutMs)
    {
        var safe = CsxFragment.SanitiseId(stepId);

        // Issue #262: the raw step id + the two Vars keys the live-sink OnStepCompleted call
        // reads back are computed ONCE, up front, so both branches below (no-timeout /
        // timeout) share identical literals.
        var rawIdLit = JsonSerializer.Serialize(stepId);
        var outcomeLit = JsonSerializer.Serialize(VarKeys.Outcome(safe));
        var captureStatusLit = JsonSerializer.Serialize(VarKeys.CaptureStatus(safe));

        // Normalise: null or non-positive → no enforcement (mirrors RetryRunner's
        // treatment of its window parameter).  Clamp the upper end to what
        // CancellationTokenSource accepts (an absurd ~24.8-day budget must not
        // throw ArgumentOutOfRangeException and abort the whole scenario).
        var budget = timeoutMs is { } t and > 0
            ? (long?)Math.Min(t, int.MaxValue - 1)
            : null;

        var sb = new StringBuilder();
        sb.Append('{').Append('\n');

        if (budget is not { } b)
        {
            // No declared timeout: token convention only, zero behavioural change.
            // __stepBudgetGoverned_<safeId> is the second convention local: false here,
            // so wired providers keep their own transport-timeout conventions as the
            // step's de facto bound.
            sb.Append("    System.Threading.CancellationToken __stepCt_").Append(safe)
              .Append(" = System.Threading.CancellationToken.None;\n");
            sb.Append("    _ = __stepCt_").Append(safe).Append(";\n");
            sb.Append("    bool __stepBudgetGoverned_").Append(safe).Append(" = false;\n");
            sb.Append("    _ = __stepBudgetGoverned_").Append(safe).Append(";\n");
            AppendStepStartedCall(sb, rawIdLit);
            sb.Append(statementBlock).Append('\n');
            AppendStepCompletedCall(sb, safe, rawIdLit, outcomeLit, captureStatusLit);
            sb.Append('}');
            return sb.ToString();
        }

        var budgetLit = b.ToString(CultureInfo.InvariantCulture) + "L";

        // The observation JSON is assembled from compile-time literals only, so it is
        // emitted as an escaped C# string literal via JsonSerializer.Serialize.
        var timeoutObservationLit = JsonSerializer.Serialize(
            "{\"reason\":\"step-timeout\",\"timeoutMs\":" +
            b.ToString(CultureInfo.InvariantCulture) + "}");
        var supersededPrefixLit = JsonSerializer.Serialize(
            "{\"reason\":\"step-timeout\",\"timeoutMs\":" +
            b.ToString(CultureInfo.InvariantCulture) + ",\"supersededVerdict\":\"");
        var supersededSuffixLit = JsonSerializer.Serialize("\"}");

        sb.Append("    var __stepSw_").Append(safe)
          .Append(" = System.Diagnostics.Stopwatch.StartNew();\n");
        sb.Append("    var __stepCts_").Append(safe)
          .Append(" = new System.Threading.CancellationTokenSource(System.TimeSpan.FromMilliseconds(")
          .Append(budgetLit).Append("));\n");
        sb.Append("    System.Threading.CancellationToken __stepCt_").Append(safe)
          .Append(" = __stepCts_").Append(safe).Append(".Token;\n");
        sb.Append("    _ = __stepCt_").Append(safe).Append(";\n");
        // The declared budget governs this step: wired providers lift their own
        // transport-timeout conventions (infinite transport bound) and defer to the
        // step token, so a budget LONGER than a convention is honoured (#232).
        sb.Append("    bool __stepBudgetGoverned_").Append(safe).Append(" = true;\n");
        sb.Append("    _ = __stepBudgetGoverned_").Append(safe).Append(";\n");
        AppendStepStartedCall(sb, rawIdLit);
        sb.Append("    var __stepTimedOut_").Append(safe).Append(" = false;\n");
        sb.Append("    try\n");
        sb.Append("    {\n");
        sb.Append(statementBlock).Append('\n');
        sb.Append("    }\n");
        sb.Append("    catch (System.OperationCanceledException) when (__stepCts_")
          .Append(safe).Append(".IsCancellationRequested)\n");
        sb.Append("    {\n");
        sb.Append("        // Early cooperative cut: the body observed the step token (§4, #232).\n");
        sb.Append("        __stepTimedOut_").Append(safe).Append(" = true;\n");
        sb.Append("        Vars[").Append(outcomeLit)
          .Append("] = new Vouchfx.Engine.Abstractions.StepOutcome(\n");
        sb.Append("            Vouchfx.Engine.Abstractions.Verdict.Inconclusive,\n");
        sb.Append("            __stepSw_").Append(safe).Append(".ElapsedMilliseconds,\n");
        sb.Append("            ").Append(timeoutObservationLit).Append(");\n");
        sb.Append("    }\n");
        sb.Append("    finally\n");
        sb.Append("    {\n");
        sb.Append("        __stepSw_").Append(safe).Append(".Stop();\n");
        sb.Append("        __stepCts_").Append(safe).Append(".Dispose();\n");
        sb.Append("    }\n");

        // Late enforcement: the body completed (any verdict) but exceeded the budget.
        // Strictly-greater comparison — the budget itself is within the bound.  The
        // early-cut flag guards against superseding the cleaner early observation.
        sb.Append("    if (!__stepTimedOut_").Append(safe)
          .Append(" && __stepSw_").Append(safe).Append(".ElapsedMilliseconds > ")
          .Append(budgetLit).Append(")\n");
        sb.Append("    {\n");
        sb.Append("        var __prior_").Append(safe)
          .Append(" = Vars.TryGetValue(").Append(outcomeLit)
          .Append(", out var __praw_").Append(safe)
          .Append(") && __praw_").Append(safe)
          .Append(" is Vouchfx.Engine.Abstractions.StepOutcome __pso_").Append(safe).Append('\n');
        sb.Append("            ? __pso_").Append(safe).Append(".Verdict.ToString()\n");
        sb.Append("            : \"(none)\";\n");
        sb.Append("        Vars[").Append(outcomeLit)
          .Append("] = new Vouchfx.Engine.Abstractions.StepOutcome(\n");
        sb.Append("            Vouchfx.Engine.Abstractions.Verdict.Inconclusive,\n");
        sb.Append("            __stepSw_").Append(safe).Append(".ElapsedMilliseconds,\n");
        sb.Append("            ").Append(supersededPrefixLit)
          .Append(" + __prior_").Append(safe)
          .Append(" + ").Append(supersededSuffixLit).Append(");\n");
        sb.Append("    }\n");

        // Issue #262: read the FINAL outcome — after the late-timeout supersession above has
        // already run — matching exactly what the post-run reconstruction reads from Vars.
        AppendStepCompletedCall(sb, safe, rawIdLit, outcomeLit, captureStatusLit);

        sb.Append('}');
        return sb.ToString();
    }

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

        // Issue #262: the raw step id + the capture-status Vars key the live-sink
        // OnStepCompleted call reads back, alongside outcomeLit above.
        var rawIdLit = JsonSerializer.Serialize(stepId);
        var captureStatusLit = JsonSerializer.Serialize(VarKeys.CaptureStatus(safe));

        // Emit the long? parameters as C# numeric literals or 'null'.
        var timeoutLit = timeoutMs is { } t
            ? t.ToString(CultureInfo.InvariantCulture) + "L"
            : "null";
        var pollLit = pollIntervalMs is { } p
            ? p.ToString(CultureInfo.InvariantCulture) + "L"
            : "null";

        var sb = new StringBuilder();
        sb.Append('{').Append('\n');
        AppendStepStartedCall(sb, rawIdLit);

        // Per-step attempt local function.  Fully-qualified types — no new usings.
        sb.Append("    async System.Threading.Tasks.Task<Vouchfx.Engine.Abstractions.StepOutcome> __attempt_")
          .Append(safe)
          .Append("(System.Threading.CancellationToken __ct_")
          .Append(safe)
          .Append(")\n");
        sb.Append("    {\n");

        // Expose the uniform per-step token name inside the attempt so provider
        // bodies reference __stepCt_<safeId> identically in both verify modes
        // (§4, #232).  In RETRY mode it is the RetryRunner-linked attempt token,
        // so a wired provider's in-flight attempt is cut when the window elapses.
        // __stepBudgetGoverned is FALSE in RETRY: the window bounds the poll, and
        // each attempt keeps the provider's own transport convention as its bound.
        sb.Append("        System.Threading.CancellationToken __stepCt_").Append(safe)
          .Append(" = __ct_").Append(safe).Append(";\n");
        sb.Append("        _ = __stepCt_").Append(safe).Append(";\n");
        sb.Append("        bool __stepBudgetGoverned_").Append(safe).Append(" = false;\n");
        sb.Append("        _ = __stepBudgetGoverned_").Append(safe).Append(";\n");

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
          .Append(" is Vouchfx.Engine.Abstractions.StepOutcome __so_").Append(safe).Append('\n');
        sb.Append("            ? __so_").Append(safe).Append('\n');
        sb.Append("            : new Vouchfx.Engine.Abstractions.StepOutcome(Vouchfx.Engine.Abstractions.Verdict.Inconclusive, 0L, null);\n");
        sb.Append("        Vars.Remove(").Append(outcomeLit).Append(");\n");
        sb.Append("        return __o_").Append(safe).Append(";\n");
        sb.Append("    }\n");

        // The engine-owned poll: writes the final StepOutcome + List<AttemptRecord> back
        // into Vars under outcomeKey / attemptsKey.  Method group → Func<CT, Task<…>>.
        // Issue #262: the two trailing arguments route into the sink-aware PollAsync
        // overload, so RetryRunner reports each attempt to StepEvents in real time as it
        // resolves — the caller-side ct parameter is left at its default.
        sb.Append("    await Vouchfx.Engine.Abstractions.Retry.RetryRunner.PollAsync(\n");
        sb.Append("        Vars, ").Append(outcomeLit).Append(", ").Append(attemptsLit)
          .Append(", ").Append(timeoutLit).Append(", ").Append(pollLit)
          .Append(", __attempt_").Append(safe)
          .Append(", StepEvents, ").Append(rawIdLit).Append(");\n");

        // Issue #262: read the FINAL outcome — the value PollAsync just wrote back to
        // Vars[outcomeKey] — matching exactly what the post-run reconstruction reads.
        AppendStepCompletedCall(sb, safe, rawIdLit, outcomeLit, captureStatusLit);

        sb.Append('}');

        return sb.ToString();
    }

    /// <summary>
    /// Emits <c>StepEvents?.OnStepStarted("&lt;rawId&gt;");</c> — the top-of-block live
    /// signal shared by <see cref="WrapForImmediate"/> (both branches) and
    /// <see cref="WrapForRetry"/> (issue #262). A <see langword="null"/>
    /// <c>ScriptGlobalVariables.StepEvents</c> (no <c>--events-stream</c> conduit) makes this
    /// a pure no-op, so the emitted CSX behaves identically whether or not a live sink is
    /// configured.
    /// </summary>
    private static void AppendStepStartedCall(StringBuilder sb, string rawIdLit)
    {
        sb.Append("    StepEvents?.OnStepStarted(").Append(rawIdLit).Append(");\n");
    }

    /// <summary>
    /// Emits the end-of-block <c>StepEvents?.OnStepCompleted(...)</c> live signal shared by
    /// <see cref="WrapForImmediate"/> (both branches) and <see cref="WrapForRetry"/> (issue
    /// #262). Reads the FINAL <c>Vars[outcomeKey]</c> / <c>Vars[captureStatusKey]</c> values —
    /// by the time this is reached, any RETRY poll or IMMEDIATE step-timeout supersession has
    /// already written its last word — so the reported <see cref="StepOutcome"/> matches
    /// exactly what the post-run reconstruction reads back from the same keys.
    /// </summary>
    private static void AppendStepCompletedCall(
        StringBuilder sb, string safe, string rawIdLit, string outcomeLit, string captureStatusLit)
    {
        sb.Append("    StepEvents?.OnStepCompleted(\n");
        sb.Append("        ").Append(rawIdLit).Append(",\n");
        sb.Append("        Vars.TryGetValue(").Append(outcomeLit).Append(", out var __sco_").Append(safe)
          .Append(") ? __sco_").Append(safe).Append(" as Vouchfx.Engine.Abstractions.StepOutcome : null,\n");
        sb.Append("        Vars.TryGetValue(").Append(captureStatusLit).Append(", out var __scs_").Append(safe)
          .Append(") ? __scs_").Append(safe).Append(" as string : null);\n");
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
