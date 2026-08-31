// Vouchfx.Engine.Planning.Ingest — EventHistoryReader (M3 Planner, REQ-009, EDGE-004,
// fix-round M4/M5/N5, fix-round-2 NEW-1).
//
// Reads the JSON Lines event history: a single .jsonl file, or a directory whose *.jsonl
// files (directly within it, name-sorted) are read as one combined history. An absent or
// empty history is a successful, empty result — never an error (REQ-009).
//
// Hardening (fix-round): mirrors SuiteSetLoader.ParseFile's capture-never-throw discipline
// for I/O faults (a permission denial, a delete race, or a .jsonl still held open by an
// in-flight `vouchfx run` must never escape BuildPlan as a raw exception — M4). Two genuinely
// distinct failure classes, kept deliberately separate (fix-round-2 NEW-1):
//   - a TRANSIENT I/O RACE (directory not enumerable, or a file that became unreadable
//     between enumeration and read) is data loss the caller cannot prevent — captured as
//     ONE skipped entry (SkippedEventLines), same discipline as EDGE-004's corrupt lines;
//   - an OVERSIZED file (checked via FileInfo.Length BEFORE reading, mirroring
//     SuiteSetLoader.MaxDocumentSizeBytes's reject-before-reading pattern) is a
//     CONFIGURATION PROBLEM, not corrupt data — it throws PlanInputException (EDGE-009's
//     "loud, exit 2" philosophy) rather than being silently folded into a line-count field,
//     which would otherwise let a 64 MiB history vanish while reporting "skippedEventLines: 1".
// A per-line length cap remains as defence in depth against a single pathological line within
// an otherwise acceptably-sized file (M5).

using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Planning;

namespace Vouchfx.Engine.Planning.Ingest;

/// <summary>Reads and type-parses a JSON Lines event history into the Planner-relevant event subsets.</summary>
internal static class EventHistoryReader
{
    /// <summary>The raw, uncorrelated event data extracted from a history (before <see cref="RunCorrelator"/> attributes it to declared suites).</summary>
    /// <param name="ScenarioStarts">Every successfully-parsed <c>scenario-started</c> event.</param>
    /// <param name="StepCompletions">Every successfully-parsed <c>step-completed</c> event.</param>
    /// <param name="RunIds">Every <c>runId</c> observed on a successfully recognised event (may repeat).</param>
    /// <param name="SkippedLines">EDGE-004: the count of unparseable or unrecognisable event lines.</param>
    /// <param name="FirstTimestamp">The earliest timestamp across every recognised event, or <see langword="null"/> when none.</param>
    /// <param name="LastTimestamp">The latest timestamp across every recognised event, or <see langword="null"/> when none.</param>
    internal sealed record RawEventHistory(
        IReadOnlyList<ScenarioStartedEvent> ScenarioStarts,
        IReadOnlyList<StepCompletedEvent> StepCompletions,
        IReadOnlyList<string> RunIds,
        int SkippedLines,
        DateTimeOffset? FirstTimestamp,
        DateTimeOffset? LastTimestamp);

    /// <summary>An empty, successful read result — REQ-009's absent/empty-history case.</summary>
    internal static RawEventHistory Empty { get; } = new(
        Array.Empty<ScenarioStartedEvent>(),
        Array.Empty<StepCompletedEvent>(),
        Array.Empty<string>(),
        SkippedLines: 0,
        FirstTimestamp: null,
        LastTimestamp: null);

    /// <summary>
    /// The maximum permitted size, in bytes, of a single <c>.jsonl</c> event-history file
    /// (fix-round M5). Checked via <see cref="FileInfo.Length"/> BEFORE the file is opened —
    /// mirroring <c>SuiteSetLoader.MaxDocumentSizeBytes</c>'s reject-before-reading
    /// discipline. An oversized file throws <see cref="PlanInputException"/> (fix-round-2
    /// NEW-1) rather than being silently folded into <c>SkippedEventLines</c>: that field is
    /// frozen documentation for a COUNT OF LINES, and a whole file dropped whole is a
    /// configuration problem (EDGE-009's "loud, exit 2" philosophy), not corrupt data — never
    /// materialised into memory either way.
    /// </summary>
    internal const long MaxEventFileSizeBytes = 64 * 1024 * 1024; // 64 MiB.

    /// <summary>
    /// The maximum permitted length, in UTF-16 characters, of a single event-history line
    /// (fix-round M5) — a defence-in-depth bound alongside
    /// <see cref="MaxEventFileSizeBytes"/>: <see cref="File.ReadLines(string)"/> must fully
    /// materialise a line into a <see cref="string"/> before its length can be checked, so
    /// this cannot prevent that one-time allocation, but it does stop a single pathological
    /// line from being processed any further. A line over this length is skipped and counted
    /// (EDGE-004).
    /// </summary>
    internal const int MaxEventLineLengthChars = 1024 * 1024; // 1 Mi characters.

    /// <summary>
    /// Reads <paramref name="eventsPath"/> — a single <c>.jsonl</c> file, a directory of
    /// them, or <see langword="null"/>/absent (returns <see cref="Empty"/>) — into a
    /// <see cref="RawEventHistory"/>.
    /// </summary>
    /// <param name="eventsPath">The events file or directory path, or <see langword="null"/>/empty.</param>
    internal static RawEventHistory Read(string? eventsPath)
    {
        if (string.IsNullOrWhiteSpace(eventsPath))
        {
            return Empty;
        }

        var fullPath = Path.GetFullPath(eventsPath);

        IReadOnlyList<string> files;
        try
        {
            if (File.Exists(fullPath))
            {
                files = new[] { fullPath };
            }
            else if (Directory.Exists(fullPath))
            {
                files = Directory
                    .EnumerateFiles(fullPath, "*.jsonl", SearchOption.TopDirectoryOnly)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                // An absent history path is a valid, successful analysis (REQ-009) — never an
                // error, so the caller-supplied path simply not existing is treated the same
                // as an omitted --events argument.
                return Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // TRANSIENT I/O RACE (fix-round-2 NEW-1's first class, not the oversized-file
            // class below): the path exists but cannot even be enumerated (permission
            // denial, a delete race). Never escape BuildPlan as a raw I/O exception (M4) —
            // mirrors SuiteSetLoader.ParseFile's capture-never-throw discipline. Counted as
            // one skipped entry so the report signals data was lost rather than reading as a
            // clean, fully-analysed empty history.
            return Empty with { SkippedLines = 1 };
        }

        var scenarioStarts = new List<ScenarioStartedEvent>();
        var stepCompletions = new List<StepCompletedEvent>();
        var runIds = new List<string>();
        var skipped = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;

        foreach (var file in files)
        {
            try
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.Length > MaxEventFileSizeBytes)
                {
                    // Fix-round-2 NEW-1: a configuration problem, not corrupt data — throws
                    // rather than silently folding an entire dropped file into a field frozen
                    // as a LINE count (SkippedEventLines). Never caught by the
                    // IOException/UnauthorizedAccessException filter below, so it propagates
                    // straight out of BuildPlan, exactly like SuiteSetLoader.Load's EDGE-009
                    // exceptions.
                    throw new PlanInputException(
                        $"Event-history file '{file}' is {fileInfo.Length} bytes, exceeding "
                        + $"the {MaxEventFileSizeBytes}-byte (64 MiB) limit for a single "
                        + ".jsonl file. This is a configuration problem, not corrupt data - "
                        + "split the history into smaller files rather than risk an entire "
                        + "run's worth of events silently vanishing from the report.");
                }

                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        // A blank line is a formatting artefact (e.g. a trailing newline), not
                        // corrupt data — it is silently ignored rather than counted as skipped.
                        continue;
                    }

                    if (line.Length > MaxEventLineLengthChars)
                    {
                        // Defence in depth (M5): the line is already materialised, but it is
                        // discarded here rather than handed to the JSON parser.
                        skipped++;
                        continue;
                    }

                    EventEnvelope envelope;
                    try
                    {
                        envelope = EventStreamJson.FromLine(line);
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                    {
                        skipped++;
                        continue;
                    }

                    if (envelope.Timestamp == default)
                    {
                        // EventEnvelope.Timestamp is not `required`; a line missing `ts`
                        // deserialises to default(DateTimeOffset) (N5), which must never
                        // silently become firstEventTs/lastEventTs or corrupt REQ-006's "now"
                        // — treat it as unrecognisable and count it (EDGE-004).
                        skipped++;
                        continue;
                    }

                    var recognisedFully = true;
                    if (string.Equals(envelope.Type, EventTypes.ScenarioStarted, StringComparison.Ordinal))
                    {
                        try
                        {
                            scenarioStarts.Add(EventStreamJson.FromLine<ScenarioStartedEvent>(line));
                        }
                        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                        {
                            recognisedFully = false;
                        }
                    }
                    else if (string.Equals(envelope.Type, EventTypes.StepCompleted, StringComparison.Ordinal))
                    {
                        try
                        {
                            stepCompletions.Add(EventStreamJson.FromLine<StepCompletedEvent>(line));
                        }
                        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                        {
                            recognisedFully = false;
                        }
                    }

                    // Any other recognised event type (suite-started, step-started,
                    // step-attempt, environment-error, reproducibility-envelope) parses fine
                    // as an envelope but carries no Planner-relevant payload; it still counts
                    // toward the timestamp range and the observed run-id set below.
                    if (!recognisedFully)
                    {
                        skipped++;
                        continue;
                    }

                    runIds.Add(envelope.RunId);
                    if (first is null || envelope.Timestamp < first)
                    {
                        first = envelope.Timestamp;
                    }

                    if (last is null || envelope.Timestamp > last)
                    {
                        last = envelope.Timestamp;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // TRANSIENT I/O RACE (fix-round-2 NEW-1's first class): the file existed at
                // enumeration time but became unreadable before/while being read — held open
                // by an in-flight `vouchfx run`, a permission change, or a delete race (M4).
                // Never escape BuildPlan as a raw I/O exception: skip this one source and
                // keep going, counted as one skipped entry. Distinct from the oversized-file
                // case above, which throws PlanInputException instead — this catch can never
                // observe that throw (PlanInputException does not match the filter here).
                skipped++;
            }
        }

        return new RawEventHistory(scenarioStarts, stepCompletions, runIds, skipped, first, last);
    }
}
