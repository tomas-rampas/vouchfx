// Vouchfx.Engine.Planning — PlanRequest / PlanThresholds / PlanInputException
// (M3 Planner, planner-coverage-and-gap-report REQ-001, REQ-006).
//
// PlanRequest takes PATHS (not materialised inputs) because REQ-001's acceptance calls the
// public entry point "with a fixture suite folder + fixture events + the Core registry" —
// the library owns loading and parsing, so in-process hosts never need to pre-parse anything.

namespace Vouchfx.Engine.Planning;

/// <summary>
/// The input to a single Planner analysis: a declared suite folder (or single suite file),
/// an optional event history, and optional threshold overrides.
/// </summary>
/// <param name="SuitePath">
/// A directory to search recursively for <c>*.e2e.yaml</c> suite files, or a single
/// <c>*.e2e.yaml</c> file, exactly like the <c>run</c>/<c>validate</c> CLI commands'
/// discovery behaviour. A directory that discovers zero suites is a usage error
/// (<see cref="PlanInputException"/>, EDGE-009); a single named file that does not exist,
/// or that exists but does not end in <c>.e2e.yaml</c>, is likewise rejected.
/// </param>
/// <param name="EventsPath">
/// Optional path to a JSON Lines event history: a single <c>.jsonl</c> file, or a directory
/// whose <c>*.jsonl</c> files (directly within it, name-sorted) are read as one combined
/// history. <see langword="null"/> or an absent path is a valid, successful analysis
/// (REQ-009): every declared suite and step is reported never-run and history-health
/// findings are empty.
/// </param>
/// <param name="Thresholds">
/// Optional history-health threshold overrides (REQ-006). <see langword="null"/> uses
/// <see cref="PlanThresholds.Defaults"/>.
/// </param>
public sealed record PlanRequest(
    string SuitePath,
    string? EventsPath = null,
    PlanThresholds? Thresholds = null);

/// <summary>
/// Documented, overridable thresholds that classify per-step history health (REQ-006).
/// </summary>
/// <param name="StaleDays">
/// A step is <c>step-stale</c> when its last observed <c>step-completed</c> event is more
/// than this many days before the newest event timestamp in the analysed history (never
/// wall-clock time). Default <c>30</c>.
/// </param>
/// <param name="FlakyMinRuns">
/// A step is <c>step-flaky</c> when it shows at least one <c>Pass</c> and at least one
/// <c>Fail</c> <c>step-completed</c> verdict across at least this many distinct
/// <c>runId</c>s. Default <c>2</c>.
/// </param>
/// <param name="FragileMinEnvErrors">
/// A step is <c>step-fragile</c> when it shows at least this many <c>EnvironmentError</c>
/// <c>step-completed</c> outcomes. Default <c>2</c>.
/// </param>
/// <param name="InconclusiveMin">
/// A step is <c>step-inconclusive-prone</c> when it shows at least this many
/// <c>Inconclusive</c> <c>step-completed</c> outcomes. Default <c>2</c>.
/// </param>
/// <remarks>
/// Only <c>step-completed.verdict</c> ever counts toward these tallies — never
/// <c>step-attempt.outcome</c>. A RETRY step emits one <c>step-attempt</c> per polling
/// cycle, so counting attempts would flag every RETRY step as flaky the moment one poll
/// failed before succeeding, which inverts the intent.
/// </remarks>
public sealed record PlanThresholds(
    int StaleDays = 30,
    int FlakyMinRuns = 2,
    int FragileMinEnvErrors = 2,
    int InconclusiveMin = 2)
{
    /// <summary>The documented default thresholds (30 / 2 / 2 / 2).</summary>
    public static PlanThresholds Defaults { get; } = new();
}

/// <summary>
/// Thrown when a <see cref="PlanRequest"/>'s suite path is unusable: it does not exist, it
/// is a file that does not end in <c>.e2e.yaml</c>, or it discovers zero suites at all
/// (EDGE-009 — a configuration error, not a finding).
/// </summary>
/// <remarks>
/// Deliberately distinct from <see cref="Vouchfx.Engine.Compilation.Schema.CatalogueExportException"/>,
/// which signals incomplete catalogue metadata (a different fail-closed class the CLI maps
/// to a different exit code).
/// </remarks>
public sealed class PlanInputException : InvalidOperationException
{
    /// <summary>Initialises a new instance with a message naming the offending path.</summary>
    /// <param name="message">A clear description of why the suite path is unusable.</param>
    public PlanInputException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and an inner cause.</summary>
    /// <param name="message">A clear description of why the suite path is unusable.</param>
    /// <param name="innerException">The underlying cause (e.g. an I/O failure).</param>
    public PlanInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
