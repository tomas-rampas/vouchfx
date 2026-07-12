// Vouchfx.Engine.Telemetry.Tests — aggregation correctness (S10-G-04).
//
// Proves the builder maps the buffered event stream to the allowlisted counts/timings:
// verdict counts (step + scenario), step family / provider counts, startup time and
// time-to-first-test.  All from a synthetic stream — no run, no container.

using System.Reflection;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Xunit;

namespace Vouchfx.Engine.Telemetry.Tests;

public sealed class TelemetryEventBuilderTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_ComputesScenarioAndStepVerdictCounts_FamilyAndProviderCounts()
    {
        // Two scenarios across two runIds: scenario A passes (2 steps), scenario B fails
        // (1 fail step + 1 inconclusive step).  Step verdicts come from each scenario's
        // nested `counts`; scenario verdicts from each scenario-completed verdict.
        var lines = new List<string>
        {
            // Scenario A (run-a): http.rest + db-assert.postgres, both pass.
            SyntheticEvents.ScenarioStarted("A", T0.AddMilliseconds(100), runId: "run-a"),
            SyntheticEvents.StepStarted("a1", "http.rest", T0.AddMilliseconds(110), runId: "run-a"),
            SyntheticEvents.StepCompleted("a1", Verdict.Pass, 5, T0.AddMilliseconds(120), runId: "run-a"),
            SyntheticEvents.StepStarted("a2", "db-assert.postgres", T0.AddMilliseconds(130), runId: "run-a"),
            SyntheticEvents.StepCompleted("a2", Verdict.Pass, 6, T0.AddMilliseconds(140), runId: "run-a"),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 2 }, T0.AddMilliseconds(150), runId: "run-a"),

            // Scenario B (run-b): http.rest fail + mq-expect.kafka inconclusive.
            SyntheticEvents.ScenarioStarted("B", T0.AddMilliseconds(200), runId: "run-b"),
            SyntheticEvents.StepStarted("b1", "http.rest", T0.AddMilliseconds(210), runId: "run-b"),
            SyntheticEvents.StepCompleted("b1", Verdict.Fail, 7, T0.AddMilliseconds(220), runId: "run-b"),
            SyntheticEvents.StepStarted("b2", "mq-expect.kafka", T0.AddMilliseconds(230), runId: "run-b"),
            SyntheticEvents.StepCompleted("b2", Verdict.Inconclusive, 8, T0.AddMilliseconds(240), runId: "run-b"),
            SyntheticEvents.ScenarioCompleted(
                "B", Verdict.Fail, new VerdictCounts { Fail = 1, Inconclusive = 1 },
                T0.AddMilliseconds(250), runId: "run-b"),
        };

        var ev = Build(lines);

        // Scenario count + scenario verdicts.
        Assert.Equal(2, ev.ScenarioCount);
        Assert.Equal(1, ev.ScenarioVerdicts.Pass);
        Assert.Equal(1, ev.ScenarioVerdicts.Fail);
        Assert.Equal(0, ev.ScenarioVerdicts.EnvError);
        Assert.Equal(0, ev.ScenarioVerdicts.Inconclusive);

        // Step verdicts (summed from each scenario's nested counts).
        Assert.Equal(2, ev.StepVerdicts.Pass);
        Assert.Equal(1, ev.StepVerdicts.Fail);
        Assert.Equal(0, ev.StepVerdicts.EnvError);
        Assert.Equal(1, ev.StepVerdicts.Inconclusive);

        // Family counts (before the first '.').
        Assert.Equal(2, ev.StepFamilies["http"]);
        Assert.Equal(1, ev.StepFamilies["db-assert"]);
        Assert.Equal(1, ev.StepFamilies["mq-expect"]);

        // Provider counts (full family.provider).
        Assert.Equal(2, ev.StepProviders["http.rest"]);
        Assert.Equal(1, ev.StepProviders["db-assert.postgres"]);
        Assert.Equal(1, ev.StepProviders["mq-expect.kafka"]);

        // run count is always 1; versions carried through.
        Assert.Equal(1, ev.RunCount);
        Assert.Equal(TelemetryEventBuilder.CurrentSchemaVersion, ev.SchemaVersion);
    }

    [Fact]
    public void Build_ComputesStartupAndTimeToFirstTest_FromTimestamps()
    {
        // Earliest event is at +0ms; first scenario-started at +40ms; first step-completed
        // at +90ms.  startupMs = 40, timeToFirstTestMs = 90.
        var lines = new List<string>
        {
            // An earlier event (a step-started before scenario-started can't happen, but a
            // probe at +0 establishes the run-start anchor as the earliest event).
            SyntheticEvents.ScenarioStarted("A", T0.AddMilliseconds(40)),
            SyntheticEvents.StepStarted("a1", "http.rest", T0.AddMilliseconds(50)),
            SyntheticEvents.StepCompleted("a1", Verdict.Pass, 5, T0.AddMilliseconds(90)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 1 }, T0.AddMilliseconds(100)),
        };

        var ev = Build(lines);

        // Earliest event is the scenario-started at +40, so startup (anchor→first
        // scenario-started) is 0; time-to-first-test (anchor→first step-completed) is 50.
        Assert.Equal(0, ev.StartupMs);
        Assert.Equal(50, ev.TimeToFirstTestMs);
    }

    [Fact]
    public void Build_WithEarlierAnchorEvent_ComputesPositiveStartup()
    {
        // Seed an earlier scenario-started (run anchor) at +0, a LATER scenario-started at
        // +40 — the EARLIEST scenario-started is the anchor, so this models a multi-scenario
        // run where startup is measured to the first scenario.  To exercise a positive
        // startup we put a non-scenario event earliest.
        var lines = new List<string>
        {
            // step-attempt at +0 → earliest event (run-start anchor), not a scenario-started.
            EventStreamJson.ToLine(new StepAttemptEvent
            {
                RunId = "run0000000000000000000000000000",
                Timestamp = T0,
                StepId = "warmup",
                Attempt = 1,
                TMs = 1,
            }),
            SyntheticEvents.ScenarioStarted("A", T0.AddMilliseconds(40)),
            SyntheticEvents.StepStarted("a1", "http.rest", T0.AddMilliseconds(50)),
            SyntheticEvents.StepCompleted("a1", Verdict.Pass, 5, T0.AddMilliseconds(90)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 1 }, T0.AddMilliseconds(100)),
        };

        var ev = Build(lines);

        Assert.Equal(40, ev.StartupMs);
        Assert.Equal(90, ev.TimeToFirstTestMs);
    }

    [Fact]
    public void Build_EmptyStream_ProducesZeroedAllowlistedEvent()
    {
        var ev = Build(new List<string>());

        Assert.Equal(0, ev.ScenarioCount);
        Assert.Empty(ev.StepFamilies);
        Assert.Empty(ev.StepProviders);
        Assert.Equal(0, ev.StartupMs);
        Assert.Equal(0, ev.TimeToFirstTestMs);
        Assert.Equal(0, ev.StepVerdicts.Pass);
        Assert.Equal(0, ev.ScenarioVerdicts.Pass);
        Assert.Equal(1, ev.RunCount);
    }

    [Fact]
    public void Build_SkipsUnparseableLines_WithoutThrowing()
    {
        var lines = new List<string>
        {
            "this is not json",
            string.Empty,
            "   ",
            SyntheticEvents.ScenarioStarted("A", T0.AddMilliseconds(10)),
            SyntheticEvents.StepStarted("a1", "http.rest", T0.AddMilliseconds(20)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 1 }, T0.AddMilliseconds(30)),
        };

        var ev = Build(lines);

        Assert.Equal(1, ev.ScenarioCount);
        Assert.Equal(1, ev.StepProviders["http.rest"]);
    }

    [Fact]
    public void Build_StepKindWithoutDot_TreatsKindAsItsOwnFamily_AndBucketsTheProvider()
    {
        // "script" is a Core FAMILY (the bare-family alias) so it counts under its real
        // family name; but "script" alone is NOT a Core FULL id (only "script.csharp"
        // is), so the provider tally buckets it as "custom" — proving the family/provider
        // allowlists are applied independently and only the exact frozen ids pass through.
        var lines = new List<string>
        {
            SyntheticEvents.ScenarioStarted("A", T0),
            SyntheticEvents.StepStarted("a1", "script", T0.AddMilliseconds(10)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 1 }, T0.AddMilliseconds(20)),
        };

        var ev = Build(lines);

        Assert.Equal(1, ev.StepFamilies["script"]);
        Assert.False(ev.StepProviders.ContainsKey("script"));
        Assert.Equal(1, ev.StepProviders["custom"]);
    }

    [Fact]
    public void Build_CustomProviderKind_BucketsBothFamilyAndProviderAsCustom()
    {
        // A custom/non-Core provider's `kind` is an author-chosen string (here an
        // intentionally sensitive-looking id).  Neither its family nor its full id is in
        // the frozen Core taxonomy, so BOTH tallies must count it under "custom" — the
        // author-chosen string is never written as a dictionary key.  A second custom
        // kind with a DIFFERENT id must aggregate into the SAME "custom" bucket, proving
        // the metric measures "how many custom-provider steps ran" without distinguishing
        // (or emitting) the ids.
        var lines = new List<string>
        {
            SyntheticEvents.ScenarioStarted("A", T0),
            SyntheticEvents.StepStarted(
                "a1", "acme-fraud-check.secret-internal", T0.AddMilliseconds(10)),
            SyntheticEvents.StepStarted(
                "a2", "another-custom.provider-xyz", T0.AddMilliseconds(20)),

            // A genuine Core step alongside the custom ones: it must still be counted
            // under its REAL family/provider, untouched by the bucketing.
            SyntheticEvents.StepStarted("a3", "http.rest", T0.AddMilliseconds(30)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 3 }, T0.AddMilliseconds(40)),
        };

        var ev = Build(lines);

        // Both custom kinds aggregate into the single "custom" bucket.
        Assert.Equal(2, ev.StepFamilies["custom"]);
        Assert.Equal(2, ev.StepProviders["custom"]);

        // The author-chosen ids are NEVER written as keys.
        Assert.False(ev.StepFamilies.ContainsKey("acme-fraud-check"));
        Assert.False(ev.StepFamilies.ContainsKey("another-custom"));
        Assert.False(ev.StepProviders.ContainsKey("acme-fraud-check.secret-internal"));
        Assert.False(ev.StepProviders.ContainsKey("another-custom.provider-xyz"));

        // The genuine Core step is still counted under its real family/provider.
        Assert.Equal(1, ev.StepFamilies["http"]);
        Assert.Equal(1, ev.StepProviders["http.rest"]);
    }

    [Fact]
    public void Build_PostExpansionCoreProviderIds_AreTalliedUnderRealKeys()
    {
        // Regression for the frozen v1 Core catalogue expanding from 6 providers/6
        // families (S10-G-04 era) to 25 providers across 11 families (2026-07-08,
        // engine #174-177): a representative sample of POST-EXPANSION Core ids must be
        // tallied under their REAL family/provider keys, not mis-bucketed as "custom".
        var lines = new List<string>
        {
            SyntheticEvents.ScenarioStarted("A", T0),
            SyntheticEvents.StepStarted("a1", "cache-assert.redis", T0.AddMilliseconds(10)),
            SyntheticEvents.StepStarted("a2", "mq-expect.rabbitmq", T0.AddMilliseconds(20)),
            SyntheticEvents.StepStarted("a3", "storage-assert.s3", T0.AddMilliseconds(30)),
            SyntheticEvents.StepStarted("a4", "trace-expect.otlp", T0.AddMilliseconds(40)),
            SyntheticEvents.StepStarted("a5", "http.soap", T0.AddMilliseconds(50)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 5 }, T0.AddMilliseconds(60)),
        };

        var ev = Build(lines);

        Assert.Equal(1, ev.StepProviders["cache-assert.redis"]);
        Assert.Equal(1, ev.StepProviders["mq-expect.rabbitmq"]);
        Assert.Equal(1, ev.StepProviders["storage-assert.s3"]);
        Assert.Equal(1, ev.StepProviders["trace-expect.otlp"]);
        Assert.Equal(1, ev.StepProviders["http.soap"]);

        Assert.Equal(1, ev.StepFamilies["cache-assert"]);
        Assert.Equal(1, ev.StepFamilies["mq-expect"]);
        Assert.Equal(1, ev.StepFamilies["storage-assert"]);
        Assert.Equal(1, ev.StepFamilies["trace-expect"]);
        Assert.Equal(1, ev.StepFamilies["http"]);

        // None of the above may have landed in "custom" — if any were mis-bucketed the
        // counts above would undercount and this key would be non-zero instead.
        Assert.False(ev.StepFamilies.ContainsKey("custom"));
        Assert.False(ev.StepProviders.ContainsKey("custom"));
    }

    [Fact]
    public void Build_CoreTaxonomy_MatchesFrozenV1CoreCatalogue()
    {
        // Hardcoded here to MIRROR the frozen v1 Core catalogue: 25 provider ids across
        // 11 families (Table 5.1 / docs §13), current since the 2026-07-08 provider-batch
        // expansion (engine #174-177: bare aliases retired, 7 providers added, 18 -> 25).
        // This test pins TelemetryEventBuilder's private CoreFamilies/CoreFullIds sets
        // via reflection so the NEXT provider expansion fails HERE, loudly, instead of
        // silently mis-bucketing new Core providers under "custom" forever.
        var expectedFamilies = new HashSet<string>(StringComparer.Ordinal)
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

        var expectedFullIds = new HashSet<string>(StringComparer.Ordinal)
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

        var actualFamilies = GetCoreTaxonomyField("CoreFamilies");
        var actualFullIds = GetCoreTaxonomyField("CoreFullIds");

        Assert.Equal(
            expectedFamilies.OrderBy(x => x, StringComparer.Ordinal),
            actualFamilies.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(11, actualFamilies.Count);

        Assert.Equal(
            expectedFullIds.OrderBy(x => x, StringComparer.Ordinal),
            actualFullIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(25, actualFullIds.Count);
    }

    [Fact]
    public void Build_CommunityAndUnknownProviderIds_StillBucketAsCustom()
    {
        // The closed-Core-taxonomy privacy design is UNCHANGED by the expansion: a
        // Community-tier provider id (rpc.json-rpc, hosted on the provider hub — never
        // part of the frozen Core catalogue) and an arbitrary unknown/customer id
        // (acme.widget) must still never become dictionary keys — both bucket under
        // "custom" for BOTH the family and provider maps.
        var lines = new List<string>
        {
            SyntheticEvents.ScenarioStarted("A", T0),
            SyntheticEvents.StepStarted("a1", "rpc.json-rpc", T0.AddMilliseconds(10)),
            SyntheticEvents.StepStarted("a2", "acme.widget", T0.AddMilliseconds(20)),
            SyntheticEvents.ScenarioCompleted(
                "A", Verdict.Pass, new VerdictCounts { Pass = 2 }, T0.AddMilliseconds(30)),
        };

        var ev = Build(lines);

        Assert.Equal(2, ev.StepFamilies["custom"]);
        Assert.Equal(2, ev.StepProviders["custom"]);

        Assert.False(ev.StepFamilies.ContainsKey("rpc"));
        Assert.False(ev.StepFamilies.ContainsKey("acme"));
        Assert.False(ev.StepProviders.ContainsKey("rpc.json-rpc"));
        Assert.False(ev.StepProviders.ContainsKey("acme.widget"));
    }

    private static HashSet<string> GetCoreTaxonomyField(string fieldName)
    {
        var field = typeof(TelemetryEventBuilder).GetField(
            fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = (HashSet<string>?)field!.GetValue(null);
        Assert.NotNull(value);
        return value!;
    }

    private static TelemetryEvent Build(IReadOnlyList<string> lines) =>
        TelemetryEventBuilder.Build(
            lines,
            installId: Guid.NewGuid(),
            toolVersion: "1.2.3",
            engineVersion: "1.2.3",
            dotnetVersion: ".NET 8.0.7",
            timestamp: T0.AddSeconds(5));
}
