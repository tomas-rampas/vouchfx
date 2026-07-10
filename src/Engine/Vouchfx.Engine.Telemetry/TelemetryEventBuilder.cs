// Vouchfx.Engine.Telemetry — TelemetryEventBuilder (S10-G-04).
//
// The PURE FUNCTION that turns the buffered v1 event stream (the SAME JSON Lines the
// terminal / HTML / JUnit renderers consume) plus the tool/engine versions into an
// allowlisted TelemetryEvent.  It READS the frozen event records; it never mutates or
// extends them.  Because it derives ONLY counts and timings from the stream — and
// emits ONLY the allowlisted TelemetryEvent shape — it physically cannot leak step
// contents, captured values, secrets, URLs, image names, scenario names, or step ids.
//
// Mapping (from the buffered events):
//   • scenarioCount        = number of scenario-completed events
//   • scenarioVerdicts     = tally of each scenario-completed event's verdict
//   • stepVerdicts         = SUM of each scenario-completed event's nested `counts`
//                            {pass,fail,envError,inconclusive} (the engine already
//                            aggregates per-step verdicts there)
//   • stepFamilies         = tally of step-started `kind` split on the FIRST '.' →
//                            family ("http.rest" → "http")
//   • stepProviders        = tally of step-started `kind` ("http.rest") — a full id
//                            outside the frozen Core taxonomy is BUCKETED as "custom" so
//                            an author-chosen provider id never leaves the box (likewise
//                            for stepFamilies)
//   • startupMs            = (earliest scenario-started ts) − (run start ts)
//   • timeToFirstTestMs    = (earliest step-completed ts) − (run start ts)
// where "run start ts" is the earliest timestamp of ANY event in the buffer (there is
// no SuiteStarted record in the v1 stream, so the earliest scenario-started is used
// as the run-start anchor — startupMs is therefore 0 unless an earlier event exists,
// and timeToFirstTest is measured from that same anchor).

using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;

namespace Vouchfx.Engine.Telemetry;

/// <summary>
/// Builds an allowlisted <see cref="TelemetryEvent"/> from a buffered v1 event stream
/// (S10-G-04).
/// </summary>
/// <remarks>
/// <para>
/// This is a pure, deterministic function of its inputs — the buffered JSON Lines
/// event stream and the supplied version strings — so it is fully unit-testable from a
/// synthetic event list with no run, no container, and no I/O.
/// </para>
/// <para>
/// <strong>Privacy by construction:</strong> the builder derives ONLY non-identifying
/// counts and timings, and returns ONLY the allowlisted <see cref="TelemetryEvent"/>
/// shape.  It reads the step <c>kind</c>, BUCKETED against the frozen Core taxonomy so
/// only a built-in Core family/provider id (e.g. <c>http.rest</c>) is counted under its
/// real name while any custom/non-Core provider's author-chosen <c>kind</c> is counted
/// under the constant <c>"custom"</c> bucket (so it never leaves the machine), and the
/// per-verdict counts — never a captured value, a URL, an image name, a scenario name,
/// or a step id's data.  Lines that fail to parse are skipped (forward-compatible with
/// unknown event types).
/// </para>
/// </remarks>
public static class TelemetryEventBuilder
{
    /// <summary>The current <see cref="TelemetryEvent.SchemaVersion"/> emitted by the builder.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The bucket key used for any step <c>kind</c> outside the frozen Core taxonomy.
    /// A custom/non-Core provider's family and provider ids are author-chosen strings;
    /// counting them under this constant (instead of their real value) keeps the metric
    /// meaningful ("how many custom-provider steps ran") while ensuring an author-chosen
    /// id is NEVER written into the telemetry event — the one place a customer-derived
    /// string could otherwise flow onto the wire.
    /// </summary>
    private const string CustomBucket = "custom";

    /// <summary>
    /// The FROZEN v1 Core step-FAMILY taxonomy — the six built-in Core families.  A
    /// step-started <c>kind</c> whose family is in this closed set is counted under that
    /// real family name; any other (custom/non-Core) family is bucketed as
    /// <see cref="CustomBucket"/> so an author-chosen family id never leaves the machine.
    /// </summary>
    private static readonly HashSet<string> CoreFamilies = new(StringComparer.Ordinal)
    {
        "http",
        "db-assert",
        "script",
        "mq-publish",
        "mq-expect",
        "webhook-listen",
    };

    /// <summary>
    /// The FROZEN v1 Core step-PROVIDER taxonomy — the six built-in Core
    /// <c>family.provider</c> ids.  A step-started <c>kind</c> in this closed set is
    /// counted under that real id; any other (custom/non-Core) id is bucketed as
    /// <see cref="CustomBucket"/> so an author-chosen provider id never leaves the machine.
    /// </summary>
    private static readonly HashSet<string> CoreFullIds = new(StringComparer.Ordinal)
    {
        "http.rest",
        "db-assert.postgres",
        "script.csharp",
        "mq-publish.kafka",
        "mq-expect.kafka",
        "webhook-listen.http",
    };

    /// <summary>
    /// Builds the <see cref="TelemetryEvent"/> for a completed run.
    /// </summary>
    /// <param name="eventLines">
    /// The buffered v1 JSON Lines event stream (one JSON object per element) — the
    /// SAME buffer the renderers consume.
    /// </param>
    /// <param name="installId">The opaque install identifier (from the consent store).</param>
    /// <param name="toolVersion">The CLI tool informational version.</param>
    /// <param name="engineVersion">The engine assembly informational version.</param>
    /// <param name="dotnetVersion">The .NET runtime description.</param>
    /// <param name="timestamp">The UTC timestamp to stamp on the event (the run's end).</param>
    /// <returns>The fully-populated, allowlisted telemetry event.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    public static TelemetryEvent Build(
        IReadOnlyList<string> eventLines,
        Guid installId,
        string toolVersion,
        string engineVersion,
        string dotnetVersion,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(eventLines);
        ArgumentNullException.ThrowIfNull(toolVersion);
        ArgumentNullException.ThrowIfNull(engineVersion);
        ArgumentNullException.ThrowIfNull(dotnetVersion);

        var scenarioCount = 0;
        var stepVerdicts = new MutableVerdictCounts();
        var scenarioVerdicts = new MutableVerdictCounts();
        var stepFamilies = new Dictionary<string, int>(StringComparer.Ordinal);
        var stepProviders = new Dictionary<string, int>(StringComparer.Ordinal);

        DateTimeOffset? earliestEvent = null;
        DateTimeOffset? earliestScenarioStarted = null;
        DateTimeOffset? earliestStepCompleted = null;

        foreach (var line in eventLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Parse the envelope to read the type discriminator + timestamp.  An
            // unparseable line (a future event type, a truncated line) is skipped —
            // forward-compatible, exactly as the renderers tolerate unknown input.
            EventEnvelope envelope;
            try
            {
                envelope = EventStreamJson.FromLine(line);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                continue;
            }

            var ts = envelope.Timestamp;
            earliestEvent = Min(earliestEvent, ts);

            switch (envelope.Type)
            {
                case EventTypes.ScenarioStarted:
                    earliestScenarioStarted = Min(earliestScenarioStarted, ts);
                    break;

                case EventTypes.ScenarioCompleted:
                    scenarioCount++;
                    AccumulateScenarioCompleted(line, scenarioVerdicts, stepVerdicts);
                    break;

                case EventTypes.StepStarted:
                    // The step `kind` (e.g. "http.rest") lives on step-started.  Tally it
                    // into the family + provider maps, BUCKETING any non-Core kind as
                    // "custom" so an author-chosen id is never written onto the wire.
                    AccumulateStepKind(line, stepFamilies, stepProviders);
                    break;

                case EventTypes.StepCompleted:
                    earliestStepCompleted = Min(earliestStepCompleted, ts);
                    break;

                default:
                    // Unknown / unmeasured event type (step-attempt, environment-error,
                    // reproducibility-envelope, …): ignored for telemetry.
                    break;
            }
        }

        // Run-start anchor: the earliest event timestamp in the buffer (there is no
        // SuiteStarted record in the v1 stream).  Durations are clamped at 0 so a
        // clock skew can never produce a negative (or misleading) value.
        var runStart = earliestEvent;
        var startupMs = DiffMsClamped(runStart, earliestScenarioStarted);
        var timeToFirstTestMs = DiffMsClamped(runStart, earliestStepCompleted);

        return new TelemetryEvent
        {
            SchemaVersion = CurrentSchemaVersion,
            Timestamp = timestamp,
            InstallId = installId,
            ToolVersion = toolVersion,
            EngineVersion = engineVersion,
            DotnetVersion = dotnetVersion,
            RunCount = 1,
            ScenarioCount = scenarioCount,
            StepVerdicts = stepVerdicts.ToRecord(),
            ScenarioVerdicts = scenarioVerdicts.ToRecord(),
            StepFamilies = stepFamilies,
            StepProviders = stepProviders,
            StartupMs = startupMs,
            TimeToFirstTestMs = timeToFirstTestMs,
        };
    }

    /// <summary>
    /// Reads a scenario-completed line's verdict (into the scenario tally) and its
    /// nested per-step <c>counts</c> (summed into the step tally).
    /// </summary>
    private static void AccumulateScenarioCompleted(
        string line,
        MutableVerdictCounts scenarioVerdicts,
        MutableVerdictCounts stepVerdicts)
    {
        ScenarioCompletedEvent scenario;
        try
        {
            scenario = EventStreamJson.FromLine<ScenarioCompletedEvent>(line);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return;
        }

        scenarioVerdicts.Add(scenario.Verdict, 1);

        // The engine already aggregates per-step verdicts into the scenario's nested
        // `counts` — sum those across scenarios for the run-wide step tally.
        var counts = scenario.Counts;
        stepVerdicts.Pass += counts.Pass;
        stepVerdicts.Fail += counts.Fail;
        stepVerdicts.EnvError += counts.EnvError;
        stepVerdicts.Inconclusive += counts.Inconclusive;
    }

    /// <summary>
    /// Reads a step-started line's <c>kind</c> and tallies it into the family
    /// (intent, before the first <c>.</c>) and provider (full <c>family.provider</c>)
    /// maps, BUCKETING anything outside the frozen Core taxonomy.
    /// </summary>
    /// <remarks>
    /// Only a built-in Core family / provider id is counted under its real name (via
    /// <see cref="CoreFamilies"/> / <see cref="CoreFullIds"/>).  A custom/non-Core
    /// provider's <c>kind</c> is author-chosen (e.g. <c>acme-fraud-check.internal</c>),
    /// so it is counted under the <see cref="CustomBucket"/> key — the metric still
    /// measures "how many custom-provider steps ran" without ever writing an
    /// author-chosen id into the event.
    /// </remarks>
    private static void AccumulateStepKind(
        string line,
        Dictionary<string, int> stepFamilies,
        Dictionary<string, int> stepProviders)
    {
        StepStartedEvent step;
        try
        {
            step = EventStreamJson.FromLine<StepStartedEvent>(line);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return;
        }

        var kind = step.Kind;
        if (string.IsNullOrWhiteSpace(kind))
        {
            return;
        }

        // Family = the portion before the FIRST '.' ("http.rest" -> "http"); a kind with
        // no '.' is its own family.  Provider = the full "family.provider" id.  Each is
        // emitted under its REAL value ONLY when it is in the frozen Core taxonomy;
        // otherwise it is bucketed as "custom" so a customer-chosen id never reaches the
        // wire (the one place a customer-derived string could otherwise flow out).
        var dot = kind.IndexOf('.', StringComparison.Ordinal);
        var family = dot >= 0 ? kind[..dot] : kind;

        var familyKey = CoreFamilies.Contains(family) ? family : CustomBucket;
        var providerKey = CoreFullIds.Contains(kind) ? kind : CustomBucket;

        Increment(stepFamilies, familyKey);
        Increment(stepProviders, providerKey);
    }

    private static void Increment(Dictionary<string, int> map, string key)
    {
        map.TryGetValue(key, out var current);
        map[key] = current + 1;
    }

    private static DateTimeOffset? Min(DateTimeOffset? current, DateTimeOffset candidate)
    {
        if (current is null || candidate < current.Value)
        {
            return candidate;
        }

        return current;
    }

    /// <summary>
    /// The non-negative millisecond difference between two optional timestamps, or
    /// <c>0</c> when either is absent.  Clamped at 0 so clock skew never yields a
    /// negative duration.
    /// </summary>
    private static long DiffMsClamped(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is null || to is null)
        {
            return 0;
        }

        var ms = (to.Value - from.Value).TotalMilliseconds;
        return ms <= 0 ? 0 : (long)ms;
    }

    /// <summary>
    /// A small mutable accumulator for the four §12.1 verdict counts, projected to the
    /// immutable <see cref="TelemetryVerdictCounts"/> at the end.
    /// </summary>
    private sealed class MutableVerdictCounts
    {
        public int Pass { get; set; }

        public int Fail { get; set; }

        public int EnvError { get; set; }

        public int Inconclusive { get; set; }

        public void Add(Verdict verdict, int n)
        {
            switch (verdict)
            {
                case Verdict.Pass:
                    Pass += n;
                    break;
                case Verdict.Fail:
                    Fail += n;
                    break;
                case Verdict.EnvironmentError:
                    EnvError += n;
                    break;
                case Verdict.Inconclusive:
                    Inconclusive += n;
                    break;
            }
        }

        public TelemetryVerdictCounts ToRecord() => new()
        {
            Pass = Pass,
            Fail = Fail,
            EnvError = EnvError,
            Inconclusive = Inconclusive,
        };
    }
}
