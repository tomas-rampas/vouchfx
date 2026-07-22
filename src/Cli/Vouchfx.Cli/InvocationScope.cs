// Vouchfx.Cli — InvocationScope (vouchfx-mcp#17; peer-review MINOR-1 fix; parse-error exit
// code remap #269).
//
// Program.cs's ProcessTerminationTimeout gate needs to know which SUBCOMMAND is being invoked
// (only `run` stands up a container topology and therefore needs a widened teardown budget) and
// whether that invocation is a `--watch` run. Factored out of Program.cs's top-level-statement
// local functions into this small internal static class so it is directly unit-testable — a
// local function nested inside top-level statements cannot be exercised across assemblies the
// way an internal static method can (via this project's InternalsVisibleTo to
// Vouchfx.Cli.Tests). Program.cs itself is NOT exercised by the unit tests (there is nothing
// left in it to test once this gate is factored out); this seam is.
//
// #269 adds a second, unrelated concern to this same file for the same reason: Program.cs's
// single root-invocation seam is also where a System.CommandLine parse error (an unrecognised
// option or extra token on ANY subcommand) needs remapping from its own default exit code (1 —
// the same code the app's taxonomy reserves for a genuine Verdict.Fail) to ExitCodes.UsageError
// (2). See ResolveExitCode's remarks.

namespace Vouchfx.Cli;

/// <summary>
/// Bare token-scan predicates over the raw process <c>args</c>, used by Program.cs to decide
/// whether — and how — to widen <c>InvocationConfiguration.ProcessTerminationTimeout</c>.
/// </summary>
/// <remarks>
/// Both predicates deliberately scan the WHOLE <c>args</c> array rather than assuming any
/// particular token position: this is simple, and robust to anything (a future global option, a
/// bare <c>--help</c>/<c>--version</c> with no subcommand at all) that might otherwise shift
/// where the subcommand token lands. A false positive on either predicate only ever WIDENS a
/// teardown budget that did not need it — never the wrong behaviour — so the simplicity is a
/// deliberate, accepted trade-off, not an oversight.
/// </remarks>
internal static class InvocationScope
{
    /// <summary>
    /// Whether this invocation targets the <c>run</c> subcommand (the literal token
    /// <c>"run"</c> present anywhere in <paramref name="args"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>run</c> is the ONLY subcommand that stands up a container topology (via
    /// <see cref="Vouchfx.Engine.Runtime.ScenarioRunner"/> / <c>ParallelSuiteRunner</c> /
    /// <c>WatchRunner</c>) — <c>validate</c>, <c>list</c>, and <c>telemetry</c> (and a bare
    /// <c>--help</c> / <c>--version</c>) never touch Docker or
    /// <c>HeadlessTopology.DisposeAsync</c>, so none of them needs — or should get — <c>run</c>'s
    /// widened teardown budget. An earlier version of Program.cs's gate widened the budget for
    /// EVERY non-watch invocation (checking only <see cref="IsWatchInvocation"/>), so a Ctrl-C on
    /// a slow <c>validate</c> would ALSO wait up to the widened budget instead of
    /// System.CommandLine's ~2s default — an accidental widening this predicate exists to
    /// prevent (peer-review MINOR-1).
    /// </para>
    /// <para>
    /// A false positive here — e.g. a <c>validate</c> / <c>list</c> path argument that happens to
    /// be the literal string <c>"run"</c> — only widens ANOTHER command's teardown budget, never
    /// the wrong behaviour; the same accepted trade-off <see cref="IsWatchInvocation"/> already
    /// makes.
    /// </para>
    /// </remarks>
    internal static bool IsRunInvocation(string[] args) =>
        Array.Exists(args, a => string.Equals(a, "run", StringComparison.Ordinal));

    /// <summary>
    /// Whether this invocation is a watch run (the literal token <c>"--watch"</c> present
    /// anywhere in <paramref name="args"/>).
    /// </summary>
    /// <remarks>
    /// <c>--watch</c> is the engine's only long-lived (kept-topology) mode, and is only ever a
    /// valid option under <c>run</c> — System.CommandLine rejects it as unrecognised on any other
    /// subcommand — so this predicate is only meaningful (and only consulted by Program.cs) once
    /// <see cref="IsRunInvocation"/> is already <see langword="true"/>. A false positive only
    /// widens the teardown budget further (disabling the timeout entirely) — never the wrong
    /// behaviour.
    /// </remarks>
    internal static bool IsWatchInvocation(string[] args) =>
        Array.Exists(args, a => string.Equals(a, "--watch", StringComparison.Ordinal));

    /// <summary>
    /// Resolves the process exit code Program.cs returns to the OS from a single invocation,
    /// remapping a System.CommandLine parse error to <see cref="ExitCodes.UsageError"/> (#269).
    /// </summary>
    /// <param name="parseErrorCount">
    /// <c>ParseResult.Errors.Count</c> from the same <c>ParseResult</c> that produced
    /// <paramref name="invokeResult"/> — i.e. computed BEFORE <c>InvokeAsync</c> is called (it
    /// does not change once parsing has happened).
    /// </param>
    /// <param name="invokeResult">
    /// The exit code returned by <c>ParseResult.InvokeAsync</c> for the same invocation.
    /// </param>
    /// <returns>
    /// <see cref="ExitCodes.UsageError"/> (2) when <paramref name="parseErrorCount"/> is greater
    /// than zero — regardless of <paramref name="invokeResult"/>; otherwise
    /// <paramref name="invokeResult"/>, unchanged.
    /// </returns>
    /// <remarks>
    /// <para>
    /// An unrecognised option or extra token (e.g. <c>vouchfx validate &lt;suite&gt; --bogus</c>)
    /// is a genuine System.CommandLine parse error, and System.CommandLine's own
    /// <c>InvokeAsync</c> default for one is exit code 1 — colliding with
    /// <see cref="ExitCodes.TestFailure"/>, the code the app's own taxonomy reserves for a
    /// genuine <c>Verdict.Fail</c>. Left unremapped, a CI pipeline keying on the exit code
    /// cannot tell a malformed invocation apart from a real test failure. This method is the
    /// single place that correction happens, so it is directly unit-testable without spawning a
    /// process — Program.cs (a top-level-statement script) is not itself unit-testable.
    /// </para>
    /// <para>
    /// A parse error and a subcommand action's own return value are mutually exclusive in
    /// practice: when <c>ParseResult.Errors</c> is non-empty, System.CommandLine's
    /// <c>InvokeAsync</c> prints the errors and help WITHOUT invoking the matched command's
    /// action, so <paramref name="invokeResult"/> is always its own parse-error default in that
    /// case — this method ignores it and returns <see cref="ExitCodes.UsageError"/> regardless,
    /// which is also why passing <paramref name="invokeResult"/> through unchanged is safe and
    /// correct whenever <paramref name="parseErrorCount"/> is zero: it is then, and only then,
    /// the subcommand action's OWN taxonomy-aware exit code (0 / 1 / 2 / 3 / 4), which must
    /// never be second-guessed here. <c>--help</c> / <c>--version</c> resolve via their own
    /// System.CommandLine actions with NO parse errors, so they are never remapped and stay 0.
    /// </para>
    /// </remarks>
    internal static int ResolveExitCode(int parseErrorCount, int invokeResult) =>
        parseErrorCount > 0 ? ExitCodes.UsageError : invokeResult;
}
