// Vouchfx.Engine.Telemetry — TelemetryEventBuilder (S10-G-04; anchor fixed #568).
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
//   • startupMs            = (earliest VALID scenario-started ts) − runStartedAt
//   • timeToFirstTestMs    = (earliest VALID step-completed ts) − runStartedAt
//   • skippedEventLines    = number of lines this builder could not read, counted once
//                            per line: the untyped envelope parse failed or FromLine
//                            refused the line (malformed JSON, a null runId/type —
//                            #571), or the typed FromLine<T> read of a scenario-started,
//                            scenario-completed, step-started or step-completed line was
//                            refused.  NOT counted: a blank line, which is skipped
//                            unread; nor a line that was read but carries an event type
//                            this builder does not measure, a default (absent) `ts`, or a
//                            blank `kind`
// where runStartedAt is a CALLER-SUPPLIED anchor (#568): the instant `vouchfx run`
// began its pipeline, captured once via DateTimeOffset.UtcNow before discovery
// (RunCommand.ExecuteRunPipelineAsync) and threaded through TelemetryRunHook.EmitAsync.
// The builder no longer derives an anchor from the buffer itself — the previous anchor
// (the earliest timestamp of ANY event in the buffer) was NORMALLY the first
// scenario-started line itself, which pinned startupMs at 0 on every run without a
// transport notice; when a transport notice WAS emitted before the first scenario
// (CHANGELOG #566), that notice line was earlier still, and the previous anchor moved
// to IT instead.
// "VALID" means the line's typed record parsed (EventStreamJson.FromLine<T>, the same
// `JsonException or InvalidOperationException` filter every consumer uses — a line the
// typed guard refuses, e.g. a null scenarioId/stepId, contributes to NEITHER timing)
// AND its `ts` is not default(DateTimeOffset) (an absent `ts` deserialises to default
// and must never silently feed a timing) — mirroring EventHistoryReader's own rule
// for the Planner (Ingest/EventHistoryReader.cs), closing the divergence where a line
// the typed guard refused still fed this builder's timings while the Planner refused
// it outright.
// timeToFirstTestMs is measured to the earliest step-completed LINE IN THE ARCHIVE,
// which is not the same instant as the first step's own completion: the `--events`
// archive the hook reads is RECONSTRUCTED after each scenario's script returns, and
// every step line in it (step-started/attempt/completed) carries that one shared
// batch timestamp (StepEventBuilder, reconstructed via ScenarioRunner) — only the
// live `--events-stream` copy stamps each step at its real moment. So this figure
// spans the whole first scenario's steps, not its first step alone. startupMs is
// unaffected: since #566 the archived scenario-started line carries the scenario's
// own real start time.

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
/// event stream, the supplied version strings, the run-start anchor, and the
/// timestamp to stamp on the event — so it is fully unit-testable from a synthetic
/// event list with no run, no container, and no I/O.
/// </para>
/// <para>
/// <strong>Privacy by construction:</strong> the builder derives ONLY non-identifying
/// counts and timings, and returns ONLY the allowlisted <see cref="TelemetryEvent"/>
/// shape.  It reads the step <c>kind</c>, BUCKETED against the frozen Core taxonomy so
/// only a built-in Core family/provider id (e.g. <c>http.rest</c>) is counted under its
/// real name while any custom/non-Core provider's author-chosen <c>kind</c> is counted
/// under the constant <c>"custom"</c> bucket (so it never leaves the machine), and the
/// per-verdict counts — never a captured value, a URL, an image name, a scenario name,
/// or a step id's data.  A line this builder cannot read is skipped and counted in
/// <see cref="TelemetryEvent.SkippedEventLines"/>; a line of an event type this builder
/// does not measure is ignored and not counted (forward compatibility with unknown
/// event types).
/// </para>
/// </remarks>
public static class TelemetryEventBuilder
{
    /// <summary>
    /// The current <see cref="TelemetryEvent.SchemaVersion"/> emitted by the builder.
    /// </summary>
    /// <remarks>
    /// 1 -> 2 (issue #588): the event gained <see cref="TelemetryEvent.SkippedEventLines"/>.
    /// A backend that predates tomas-rampas/vouchfx-telemetry-backend#30 (the reference
    /// backend, Ingestion/AllowlistParser.cs) parses schemaVersion 1 with
    /// <c>UnmappedMemberHandling.Disallow</c> and refuses the WHOLE batch at the first
    /// unrecognised field, while it parses any later version leniently — so sending the
    /// new field under version 1 would get every drained batch containing this event
    /// refused by such a backend, whereas version 2 lets it accept the event and drop
    /// the unknown field. vouchfx-telemetry-backend#30 tracks the backend change that
    /// parses version 2 strictly and stores the count.
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

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
    /// The FROZEN v1 Core step-FAMILY taxonomy — the eleven built-in Core families
    /// (Table 5.1 / docs §13), current since the 2026-07-08 provider-batch expansion
    /// (engine #174-177: bare aliases retired, 18 → 25 providers).  A step-started
    /// <c>kind</c> whose family is in this closed set is counted under that real family
    /// name; any other (custom/non-Core) family is bucketed as <see cref="CustomBucket"/>
    /// so an author-chosen family id never leaves the machine.
    /// </summary>
    private static readonly HashSet<string> CoreFamilies = new(StringComparer.Ordinal)
    {
        "http",
        "db-assert",
        "mq-publish",
        "mq-expect",
        "cache-assert",
        "mail-expect",
        "webhook-listen",
        "metrics-assert",
        "storage-assert",
        "trace-expect",
        "script",
    };

    /// <summary>
    /// The FROZEN v1 Core step-PROVIDER taxonomy — the twenty-five built-in Core
    /// <c>family.provider</c> ids across the eleven families above, current since the
    /// 2026-07-08 provider-batch expansion (engine #174-177: bare aliases retired,
    /// 18 → 25 providers).  A step-started <c>kind</c> in this closed set is counted
    /// under that real id; any other (custom/non-Core, including Community-tier hub
    /// provider ids such as <c>rpc.json-rpc</c>) id is bucketed as
    /// <see cref="CustomBucket"/> so an author-chosen provider id never leaves the
    /// machine.
    /// </summary>
    private static readonly HashSet<string> CoreFullIds = new(StringComparer.Ordinal)
    {
        "http.rest",
        "http.soap",
        "db-assert.postgres",
        "db-assert.mysql",
        "db-assert.sqlserver",
        "db-assert.mongodb",
        "db-assert.dynamodb",
        "mq-publish.kafka",
        "mq-publish.rabbitmq",
        "mq-publish.nats",
        "mq-publish.azureservicebus",
        "mq-publish.redis",
        "mq-expect.kafka",
        "mq-expect.rabbitmq",
        "mq-expect.nats",
        "mq-expect.azureservicebus",
        "mq-expect.redis",
        "cache-assert.redis",
        "cache-assert.elasticsearch",
        "mail-expect.smtp",
        "webhook-listen.http",
        "metrics-assert.prometheus",
        "storage-assert.s3",
        "trace-expect.otlp",
        "script.csharp",
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
    /// <param name="runStartedAt">
    /// The instant <c>vouchfx run</c> began its pipeline (UTC), captured once by the
    /// caller before discovery (#568) — the anchor <c>startupMs</c> and
    /// <c>timeToFirstTestMs</c> are measured from. Must not be
    /// <see langword="default"/>.
    /// </param>
    /// <param name="timestamp">The UTC timestamp to stamp on the event (the run's end).</param>
    /// <returns>The fully-populated, allowlisted telemetry event.</returns>
    /// <exception cref="ArgumentNullException">A required reference argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="runStartedAt"/> is <see langword="default"/>(<see cref="DateTimeOffset"/>) —
    /// almost certainly a caller bug (an un-set anchor), so this fails loudly rather
    /// than reporting durations measured from 0001-01-01.
    /// </exception>
    public static TelemetryEvent Build(
        IReadOnlyList<string> eventLines,
        Guid installId,
        string toolVersion,
        string engineVersion,
        string dotnetVersion,
        DateTimeOffset runStartedAt,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(eventLines);
        ArgumentNullException.ThrowIfNull(toolVersion);
        ArgumentNullException.ThrowIfNull(engineVersion);
        ArgumentNullException.ThrowIfNull(dotnetVersion);

        if (runStartedAt == default)
        {
            throw new ArgumentException(
                "Must not be default(DateTimeOffset) - the run-start anchor is required.",
                nameof(runStartedAt));
        }

        var scenarioCount = 0;
        var skippedEventLines = 0;
        var stepVerdicts = new MutableVerdictCounts();
        var scenarioVerdicts = new MutableVerdictCounts();
        var stepFamilies = new Dictionary<string, int>(StringComparer.Ordinal);
        var stepProviders = new Dictionary<string, int>(StringComparer.Ordinal);

        DateTimeOffset? earliestScenarioStarted = null;
        DateTimeOffset? earliestStepCompleted = null;

        foreach (var line in eventLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Parse the envelope to read the type discriminator (routes the switch below;
            // timestamps are read from the TYPED record per case, not from here — see
            // #568 above).  An UNPARSEABLE line — malformed JSON, a truncated line, or a
            // FromLine refusal such as a null runId/type (#571) — is skipped and counted
            // (issue #588).  A future/unknown event TYPE is a DIFFERENT case: the
            // envelope parses fine (type is just a string discriminator) and falls into
            // the switch's default arm below, forward-compatible and NOT counted —
            // exactly as the renderers tolerate unknown input.
            EventEnvelope envelope;
            try
            {
                envelope = EventStreamJson.FromLine(line);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // Issue #588: an envelope that fails to parse (malformed JSON, or a
                // FromLine refusal such as a null runId/type — #571) is one skipped
                // line, counted once.
                skippedEventLines++;
                continue;
            }

            switch (envelope.Type)
            {
                case EventTypes.ScenarioStarted:
                    // #568: only a line whose typed record parses AND carries a real `ts`
                    // (not default(DateTimeOffset), i.e. not an absent `ts`) may feed
                    // this timing — mirroring EventHistoryReader's own rule for the
                    // Planner, so a line the typed guard refuses (e.g. a null
                    // scenarioId) no longer feeds this builder while the Planner
                    // refuses it outright.  Issue #588: a refused typed parse here (the
                    // scenario-started's OWN typed read, distinct from the untyped
                    // envelope parse above) is also one skipped line — never a timing
                    // line that merely carries a default/absent `ts`, which parsed fine
                    // and is simply unused.
                    if (!AccumulateScenarioStartedTimestamp(line, ref earliestScenarioStarted))
                    {
                        skippedEventLines++;
                    }

                    break;

                case EventTypes.ScenarioCompleted:
                    // #573: count only a line that actually yielded a scenario — a
                    // "counts": null line now fails EventStreamJson.FromLine<ScenarioCompletedEvent>'s
                    // required-reference-member guard (Counts is `required`), so incrementing
                    // scenarioCount before the typed parse would count a scenario that
                    // contributed no verdicts at all.  Issue #588: that SAME refusal is
                    // one skipped line.
                    if (AccumulateScenarioCompleted(line, scenarioVerdicts, stepVerdicts))
                    {
                        scenarioCount++;
                    }
                    else
                    {
                        skippedEventLines++;
                    }

                    break;

                case EventTypes.StepStarted:
                    // The step `kind` (e.g. "http.rest") lives on step-started.  Tally it
                    // into the family + provider maps, BUCKETING any non-Core kind as
                    // "custom" so an author-chosen id is never written onto the wire.
                    // Issue #588: a refused typed parse here is one skipped line.
                    if (!AccumulateStepKind(line, stepFamilies, stepProviders))
                    {
                        skippedEventLines++;
                    }

                    break;

                case EventTypes.StepCompleted:
                    // #568: same typed-guard + real-`ts` rule as ScenarioStarted above.
                    // Issue #588: same skip-counting rule too.
                    if (!AccumulateStepCompletedTimestamp(line, ref earliestStepCompleted))
                    {
                        skippedEventLines++;
                    }

                    break;

                default:
                    // Unknown / unmeasured event type (step-attempt, environment-error,
                    // reproducibility-envelope, …): ignored for telemetry — NOT counted
                    // as skipped (issue #588): the envelope parsed fine, this consumer
                    // simply does not measure it (§14 forward-compatibility).
                    break;
            }
        }

        // Run-start anchor: the caller-supplied instant `vouchfx run` began its
        // pipeline (#568) — never derived from the buffer itself.  Durations are
        // clamped at 0 so a clock skew can never produce a negative (or misleading)
        // value.
        var startupMs = DiffMsClamped(runStartedAt, earliestScenarioStarted);
        var timeToFirstTestMs = DiffMsClamped(runStartedAt, earliestStepCompleted);

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
            SkippedEventLines = skippedEventLines,
        };
    }

    /// <summary>
    /// Reads a scenario-completed line's verdict (into the scenario tally) and its
    /// nested per-step <c>counts</c> (summed into the step tally).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the line parsed as a <see cref="ScenarioCompletedEvent"/>
    /// and was accumulated; <see langword="false"/> when it was skipped (unparseable, or a
    /// required reference member — e.g. <c>counts</c> — deserialised to null), so the caller
    /// knows not to count it toward <c>scenarioCount</c>.
    /// </returns>
    private static bool AccumulateScenarioCompleted(
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
            return false;
        }

        scenarioVerdicts.Add(scenario.Verdict, 1);

        // The engine already aggregates per-step verdicts into the scenario's nested
        // `counts` — sum those across scenarios for the run-wide step tally.
        var counts = scenario.Counts;
        stepVerdicts.Pass += counts.Pass;
        stepVerdicts.Fail += counts.Fail;
        stepVerdicts.EnvError += counts.EnvError;
        stepVerdicts.Inconclusive += counts.Inconclusive;
        return true;
    }

    /// <summary>
    /// Reads a scenario-started line's <c>ts</c> and folds it into
    /// <paramref name="earliestScenarioStarted"/> — but only when the line's typed
    /// record parses AND its timestamp is a real one (#568).
    /// </summary>
    /// <remarks>
    /// Mirrors <c>EventHistoryReader</c>'s own rule for the Planner: a line the typed
    /// guard refuses (e.g. <c>"scenarioId": null</c>) never reaches
    /// <see cref="EventStreamJson.FromLine{T}"/>'s return, and a line with no <c>ts</c>
    /// on the wire deserialises <see cref="ScenarioStartedEvent.Timestamp"/> to
    /// <see langword="default"/> — both are treated as "no timing information", never
    /// folded in as a real timestamp. A default timestamp is 0001-01-01, not the Unix
    /// epoch; were it folded in anyway it would be earlier than any real run start, so
    /// <c>DiffMsClamped</c> would clamp <c>startupMs</c> to 0 rather than report a huge
    /// duration — a silently WRONG zero rather than a crash, which is exactly why this
    /// guard exists instead of relying on the clamp to mask it.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the line parsed as a <see cref="ScenarioStartedEvent"/>
    /// — regardless of whether its <c>ts</c> was usable — so the caller (issue #588) knows
    /// this line was READ, even though a default/absent <c>ts</c> contributed nothing to
    /// the timing; <see langword="false"/> only when the typed parse itself was refused.
    /// </returns>
    private static bool AccumulateScenarioStartedTimestamp(
        string line, ref DateTimeOffset? earliestScenarioStarted)
    {
        ScenarioStartedEvent scenario;
        try
        {
            scenario = EventStreamJson.FromLine<ScenarioStartedEvent>(line);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }

        if (scenario.Timestamp == default)
        {
            return true;
        }

        earliestScenarioStarted = Min(earliestScenarioStarted, scenario.Timestamp);
        return true;
    }

    /// <summary>
    /// Reads a step-completed line's <c>ts</c> and folds it into
    /// <paramref name="earliestStepCompleted"/> — but only when the line's typed
    /// record parses AND its timestamp is a real one (#568). See
    /// <see cref="AccumulateScenarioStartedTimestamp"/> for the identical rule and
    /// its rationale, including the (issue #588) return-value contract.
    /// </summary>
    private static bool AccumulateStepCompletedTimestamp(
        string line, ref DateTimeOffset? earliestStepCompleted)
    {
        StepCompletedEvent step;
        try
        {
            step = EventStreamJson.FromLine<StepCompletedEvent>(line);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }

        if (step.Timestamp == default)
        {
            return true;
        }

        earliestStepCompleted = Min(earliestStepCompleted, step.Timestamp);
        return true;
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
    /// <returns>
    /// <see langword="true"/> when the line parsed as a <see cref="StepStartedEvent"/> —
    /// including a blank/whitespace <c>kind</c>, which is READ, just not tallied — and
    /// <see langword="false"/> only when the typed parse itself was refused (issue #588).
    /// </returns>
    private static bool AccumulateStepKind(
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
            return false;
        }

        var kind = step.Kind;
        if (string.IsNullOrWhiteSpace(kind))
        {
            return true;
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
        return true;
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
