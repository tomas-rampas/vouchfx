// Vouchfx.Cli — InvocationScope (vouchfx-mcp#17; peer-review MINOR-1 fix).
//
// Program.cs's ProcessTerminationTimeout gate needs to know which SUBCOMMAND is being invoked
// (only `run` stands up a container topology and therefore needs a widened teardown budget) and
// whether that invocation is a `--watch` run. Factored out of Program.cs's top-level-statement
// local functions into this small internal static class so it is directly unit-testable — a
// local function nested inside top-level statements cannot be exercised across assemblies the
// way an internal static method can (via this project's InternalsVisibleTo to
// Vouchfx.Cli.Tests). Program.cs itself is NOT exercised by the unit tests (there is nothing
// left in it to test once this gate is factored out); this seam is.

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
}
