// Platform.Engine.Telemetry — TelemetryEvent (S10-G-04).
//
// THE PRIVACY ALLOWLIST.
//
// This record is the ONLY shape ever serialised and handed to a telemetry sink.
// Its public properties are a CLOSED, hand-curated set — an allowlist, never a
// denylist.  The "provably never sent" guarantee for sensitive data is PHYSICAL:
// there is no property on this record that can carry test/step contents, captured
// values, secret references or values, system-under-test addresses or URLs,
// container image names, scenario names, step ids, or provider observations.  If a
// field is not in the list below, it cannot be transmitted — there is nowhere to
// put it.
//
// Two CI gates defend this guarantee (Platform.Engine.Telemetry.Tests):
//   1. An ALLOWLIST reflection test pins the exact set of public property names, so
//      ADDING ANY property fails the build and forces a privacy review.
//   2. A DENYLIST serialisation scan builds an event from a synthetic run seeded
//      with a SUT URL, an image name, a secret reference, a captured value, a
//      scenario name and raw step text, serialises it, and asserts NONE of those
//      sensitive sample substrings appear in the JSON.
//
// If you are tempted to add a field here: STOP.  Adding any field that could carry
// content from a customer's tests breaks the privacy contract.  The allowlist is the
// contract; the gates enforce it; this comment is the warning.

using System.Text.Json.Serialization;

namespace Platform.Engine.Telemetry;

/// <summary>
/// The complete, privacy-allowlisted telemetry payload (S10-G-04).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This record is an allowlist, not a denylist.</strong>  Its public
/// properties below are the <em>entire</em> set of values vouchfx will ever
/// transmit when telemetry is enabled.  Everything a customer's tests touch —
/// step contents, captured values, secret references/values, SUT addresses/URLs,
/// container image names, scenario names, step ids, provider observations — has
/// <em>no place to live</em> on this record and therefore can never be sent.  This
/// physical absence is the "provably never sent" guarantee, defended by the
/// allowlist-reflection and denylist-serialisation gates in the test project.
/// </para>
/// <para>
/// Every value here is either an environment/version fact (tool / engine / runtime
/// version) or a non-identifying AGGREGATE COUNT or TIMING derived from the event
/// stream.  Counts are keyed by step <em>family</em> (the intent, e.g. <c>http</c>)
/// and <em>family.provider</em> (the technology, e.g. <c>http.rest</c>) — both are
/// closed taxonomy tokens that describe WHICH built-in step kinds ran, never the
/// data those steps carried.
/// </para>
/// </remarks>
public sealed record TelemetryEvent
{
    /// <summary>
    /// The telemetry payload schema version.  Lets a future backend evolve the
    /// shape without misreading older events.  Distinct from the engine / event-stream
    /// versions: it versions THIS allowlist.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public required int SchemaVersion { get; init; }

    /// <summary>
    /// UTC timestamp at which this telemetry event was built (the end of the run).
    /// </summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The opaque, randomly-generated install identifier (a GUID, minted only when
    /// the user opts in).  It identifies the INSTALL, never the user, the machine, or
    /// any test content; deleting it (via <c>telemetry disable</c>) severs the link.
    /// </summary>
    [JsonPropertyName("installId")]
    public required Guid InstallId { get; init; }

    /// <summary>The vouchfx tool (CLI) informational version, e.g. <c>"1.0.0"</c>.</summary>
    [JsonPropertyName("toolVersion")]
    public required string ToolVersion { get; init; }

    /// <summary>The vouchfx engine assembly informational version.</summary>
    [JsonPropertyName("engineVersion")]
    public required string EngineVersion { get; init; }

    /// <summary>
    /// The .NET runtime description the tool ran on, e.g.
    /// <c>".NET 8.0.7"</c> (from <c>RuntimeInformation.FrameworkDescription</c>).
    /// </summary>
    [JsonPropertyName("dotnetVersion")]
    public required string DotnetVersion { get; init; }

    /// <summary>
    /// The number of suite runs this event represents.  Always <c>1</c> in v1 (one
    /// event per <c>vouchfx run</c>); present so a backend can aggregate without a
    /// schema change.
    /// </summary>
    [JsonPropertyName("runCount")]
    public required int RunCount { get; init; }

    /// <summary>The number of scenarios that executed in the run.</summary>
    [JsonPropertyName("scenarioCount")]
    public required int ScenarioCount { get; init; }

    /// <summary>
    /// Per-verdict STEP counts across the whole run (pass / fail / envError /
    /// inconclusive).  Counts only — never which step, never its data.
    /// </summary>
    [JsonPropertyName("stepVerdicts")]
    public required TelemetryVerdictCounts StepVerdicts { get; init; }

    /// <summary>
    /// Per-verdict SCENARIO counts across the whole run (pass / fail / envError /
    /// inconclusive).  Counts only — never which scenario, never its name.
    /// </summary>
    [JsonPropertyName("scenarioVerdicts")]
    public required TelemetryVerdictCounts ScenarioVerdicts { get; init; }

    /// <summary>
    /// Step counts keyed by step FAMILY (the intent token, e.g. <c>"http"</c>,
    /// <c>"db-assert"</c>), e.g. <c>{"http":3,"db-assert":1}</c>.  The keys are the
    /// closed built-in family taxonomy, never customer data.
    /// </summary>
    [JsonPropertyName("stepFamilies")]
    public required IReadOnlyDictionary<string, int> StepFamilies { get; init; }

    /// <summary>
    /// Step counts keyed by FAMILY.PROVIDER (the technology token, e.g.
    /// <c>"http.rest"</c>, <c>"db-assert.postgres"</c>), e.g.
    /// <c>{"http.rest":3}</c>.  The keys are the closed built-in provider taxonomy,
    /// never customer data.
    /// </summary>
    [JsonPropertyName("stepProviders")]
    public required IReadOnlyDictionary<string, int> StepProviders { get; init; }

    /// <summary>
    /// Wall-clock milliseconds from the run starting to the first scenario starting
    /// (topology + engine startup).  A non-identifying duration.
    /// </summary>
    [JsonPropertyName("startupMs")]
    public required long StartupMs { get; init; }

    /// <summary>
    /// Wall-clock milliseconds from the run starting to the first step completing
    /// (time-to-first-test).  A non-identifying duration.
    /// </summary>
    [JsonPropertyName("timeToFirstTestMs")]
    public required long TimeToFirstTestMs { get; init; }
}

/// <summary>
/// The four §12.1 verdict counts, reused for both the step-level and scenario-level
/// aggregates on a <see cref="TelemetryEvent"/>.
/// </summary>
/// <remarks>
/// This mirrors the four-outcome taxonomy (Pass / Fail / EnvError / Inconclusive)
/// exactly, kept separate everywhere — a count, never a label of WHAT passed or
/// failed.
/// </remarks>
public sealed record TelemetryVerdictCounts
{
    /// <summary>Number of items (steps or scenarios) that passed.</summary>
    [JsonPropertyName("pass")]
    public int Pass { get; init; }

    /// <summary>Number of items that failed their assertions.</summary>
    [JsonPropertyName("fail")]
    public int Fail { get; init; }

    /// <summary>Number of items that ended in an environment error.</summary>
    [JsonPropertyName("envError")]
    public int EnvError { get; init; }

    /// <summary>Number of items whose outcome was inconclusive.</summary>
    [JsonPropertyName("inconclusive")]
    public int Inconclusive { get; init; }
}
