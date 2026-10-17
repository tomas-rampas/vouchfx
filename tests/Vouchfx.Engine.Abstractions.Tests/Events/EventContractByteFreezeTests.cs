using System.Collections;
using System.Text;
using System.Text.Json;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

public sealed partial class EventContractFreezeTests
{
    [Fact]
    public void EventWireBytes_EnvelopeOverloadMatchesFrozenGenericBytes()
    {
        foreach (var fixture in CreateCanonicalFixtures().Where(fixture => fixture.Type == typeof(EventEnvelope)))
        {
            var envelope = Assert.IsType<EventEnvelope>(fixture.Value);
            Assert.Equal(Encoding.UTF8.GetBytes(fixture.ToLine()),
                Encoding.UTF8.GetBytes(EventStreamJson.ToLine(envelope)));
        }
    }

    [Fact]
    public void EventWireBytes_ToLineMatchesGolden_ExactUtf8IncludingFinalLf()
    {
        var fixtures = CreateCanonicalFixtures();
        AssertCanonicalCoverage(fixtures);
        var actual = Encoding.UTF8.GetBytes(string.Concat(fixtures.Select(fixture => fixture.ToLine() + "\n")));
        if (IsRegenRequested())
        {
            // The SAME policy leaf protects both event goldens before either writes.
            File.WriteAllBytes(Path.Combine(FindRepoRoot(), "tests", "Vouchfx.Engine.Abstractions.Tests",
                "Golden", "event-stream-wire-bytes.v1.jsonl"), actual);
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "event-stream-wire-bytes.v1.jsonl");
        Assert.True(File.Exists(path), $"Missing exact-byte event golden: {path}");
        var golden = File.ReadAllBytes(path);
        Assert.True(golden.AsSpan().SequenceEqual(actual),
            "The frozen v1 EventStreamJson.ToLine UTF-8 bytes drifted. Review escaping, member order, "
            + "verdict tokens, null/default fields and the final LF. Intentional local regeneration uses "
            + "VOUCHFX_REGEN_EVENT_CONTRACT=1 with CI markers unset, followed by review. "
            + FirstByteDifference(golden, actual));
    }

    [Fact]
    public void EventWireBytes_CanonicalFixturesCoverEveryFrozenRecordAndVerdict()
    {
        var fixtures = CreateCanonicalFixtures();
        AssertCanonicalCoverage(fixtures);
        foreach (var type in new[] { typeof(StepAttemptEvent), typeof(StepCompletedEvent), typeof(ScenarioCompletedEvent) })
        {
            var verdicts = fixtures.Where(fixture => fixture.Type == type)
                .Select(fixture => fixture.Value switch
                {
                    StepAttemptEvent attempt => attempt.Outcome,
                    StepCompletedEvent completed => completed.Verdict,
                    ScenarioCompletedEvent completed => completed.Verdict,
                    _ => null,
                }).Where(verdict => verdict.HasValue).Select(verdict => verdict!.Value).Distinct().OrderBy(verdict => verdict);
            Assert.Equal(Enum.GetValues<Verdict>().OrderBy(verdict => verdict), verdicts);
        }
    }

    [Fact]
    public void EventWireBytes_CoverageRefusesMissingOrExtraCanonicalRootRecords()
    {
        var fixtures = CreateCanonicalFixtures();
        Assert.Throws<Xunit.Sdk.TrueException>(() => AssertCanonicalCoverage(
            fixtures.Where(fixture => fixture.Type != typeof(StepStartedEvent)).ToArray()));
        Assert.Throws<Xunit.Sdk.TrueException>(() => AssertCanonicalCoverage(
            fixtures.Append(Canonical(new VerdictCounts())).ToArray()));
    }

    private static List<CanonicalFixture> CreateCanonicalFixtures()
    {
        var timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)).AddTicks(1234567);
        const string escaped = "a<b>+c&d'eé\"\\\n\r\t";
        var correlations = new Dictionary<string, string>(StringComparer.Ordinal) { ["Trace<Key>"] = escaped, ["span"] = "span-2" };
        var observation = JsonSerializer.SerializeToElement(new { text = escaped, zero = 0, no = false, absent = (string?)null });
        var captured = new[] { new CapturedVar(escaped, "$.id", true), new CapturedVar("missing", "$.absent", false) };
        var substitutions = new[] { new SubstitutionRef(escaped, "step-origin", false), new SubstitutionRef("env/API_TOKEN", null, true) };
        var counts = new VerdictCounts { Pass = 1, Fail = 2, EnvError = 3, Inconclusive = 0 };
        var fixtures = new List<CanonicalFixture>
        {
            Canonical(new EventEnvelope
            {
                Type = "future-event", Timestamp = timestamp, RunId = "run-1", CorrelationIds = correlations,
                Extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["Extra<Key>"] = observation, ["futureNull"] = JsonSerializer.SerializeToElement((string?)null),
                },
            }),
            Canonical(new EventEnvelope { Type = "future-event", RunId = "run-default" }),
            Canonical(new ScenarioStartedEvent
            {
                Timestamp = timestamp, RunId = "run-1", ScenarioId = "scenario-1", CorrelationIds = correlations,
                File = escaped, ContentHash = "sha256:scenario",
            }),
            Canonical(new ScenarioStartedEvent { RunId = "run-default", ScenarioId = "scenario-default" }),
            Canonical(new StepStartedEvent
            {
                Timestamp = timestamp, RunId = "run-1", StepId = "step-1", CorrelationIds = correlations,
                Kind = "http.rest", VerifyMode = "RETRY", TimeoutMs = 0,
            }),
            Canonical(new StepStartedEvent { RunId = "run-default", StepId = "step-default" }),
            Canonical(new StepAttemptEvent { RunId = "run-default", StepId = "step-default" }),
            Canonical(new ReproducibilityEnvelopeEvent
            {
                Timestamp = timestamp, RunId = "run-1", ScenarioId = "scenario-1", CorrelationIds = correlations,
                EnvSchemaVersion = "v1", SecretReferences = new[] { new SecretReferenceDigest("env", "sha256:reference") },
                Fixtures = new[] { new FixtureDigest(escaped, "sha256:fixture"), new FixtureDigest("seed.sql", null) },
            }),
            Canonical(new ReproducibilityEnvelopeEvent
            {
                RunId = "run-default", ScenarioId = "scenario-default", EnvSchemaVersion = "v1",
                SecretReferences = Array.Empty<SecretReferenceDigest>(), Fixtures = Array.Empty<FixtureDigest>(),
            }),
            Canonical(new SecurityConfirmationEvent
            {
                RunId = "security-run", Timestamp = timestamp, TargetName = escaped, TargetKind = "service",
                DeclaredProfile = "mtls", DeclaredEndpoint = "https", ObservedAddress = "localhost:443",
                ObservedProtocol = "Tls13", ClientIdentityResolved = true, Level = "AuthenticatedRoundTrip", Replayed = false,
            }),
            Canonical(new SecurityConfirmationEvent
            {
                RunId = "security-replay", TargetName = "target", TargetKind = "kafka",
                DeclaredProfile = "tls", DeclaredEndpoint = "tls", ObservedAddress = "localhost:9093",
                ObservedProtocol = "Tls12", ClientIdentityResolved = false, Level = "TransportConfirmed", Replayed = true,
            }),
            Canonical(new TransportNoticeEvent
            {
                Timestamp = timestamp, RunId = "run-1", CorrelationIds = correlations,
                Kind = TransportNoticeKinds.PlaintextDowngrade, Service = escaped,
                SelectedEndpoint = "http", RejectedEndpoint = "https", Replayed = false,
            }),
            Canonical(new TransportNoticeEvent
            {
                RunId = "run-default", Kind = TransportNoticeKinds.NoEngineTrust,
                Service = "service-default", SelectedEndpoint = "https",
            }),
        };
        foreach (var verdict in new[] { Verdict.Pass, Verdict.Fail, Verdict.EnvironmentError, Verdict.Inconclusive })
        {
            fixtures.Add(Canonical(new StepAttemptEvent
            {
                Timestamp = timestamp,
                RunId = "run-1",
                StepId = "step-1",
                CorrelationIds = correlations,
                Attempt = 2,
                TMs = 123,
                Outcome = verdict,
                Observation = observation,
            }));
            fixtures.Add(Canonical(new StepCompletedEvent
            {
                Timestamp = timestamp,
                RunId = "run-1",
                StepId = "step-1",
                CorrelationIds = correlations,
                Verdict = verdict,
                DurationMs = 456,
                Captured = captured,
                Substitutions = substitutions,
                Observation = observation,
            }));
            fixtures.Add(Canonical(new ScenarioCompletedEvent
            {
                Timestamp = timestamp,
                RunId = "run-1",
                ScenarioId = "scenario-1",
                CorrelationIds = correlations,
                Verdict = verdict,
                Counts = counts,
                Message = escaped,
            }));
        }

        fixtures.Add(Canonical(new StepCompletedEvent
        {
            RunId = "run-default",
            StepId = "step-default",
            Verdict = Verdict.Pass,
            DurationMs = 0,
        }));
        fixtures.Add(Canonical(new ScenarioCompletedEvent
        {
            RunId = "run-default",
            ScenarioId = "scenario-default",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts(),
        }));
        return fixtures;
    }

    // Keep the generic T of the real ToLine call: serialising object would select a
    // different STJ contract and would not exercise the API that production calls.
    private static CanonicalFixture Canonical<T>(T value) where T : notnull =>
        new(typeof(T), value, () => EventStreamJson.ToLine(value));

    private sealed record CanonicalFixture(Type Type, object Value, Func<string> ToLine);

    private static void AssertCanonicalCoverage(IReadOnlyList<CanonicalFixture> fixtures)
    {
        var expectedRoots = s_eventRecords.Where(type => type.GetProperty(nameof(EventEnvelope.Type)) is not null).ToHashSet();
        var roots = fixtures.Select(fixture => fixture.Type).ToHashSet();
        Assert.True(expectedRoots.SetEquals(roots),
            $"Canonical root coverage differs: missing {Names(expectedRoots.Except(roots))}; extra {Names(roots.Except(expectedRoots))}.");
        var frozen = s_eventRecords.ToHashSet();
        var observed = new HashSet<Type>();
        foreach (var fixture in fixtures)
        {
            Assert.Equal(fixture.Type, fixture.Value.GetType());
            Visit(fixture.Value);
        }

        Assert.True(frozen.SetEquals(observed),
            $"Canonical object graphs differ from frozen records: missing {Names(frozen.Except(observed))}; extra {Names(observed.Except(frozen))}.");

        void Visit(object? value)
        {
            if (value is null)
            {
                return;
            }

            var type = value.GetType();
            if (frozen.Contains(type))
            {
                observed.Add(type);
                foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    Visit(property.GetValue(value));
                }
            }
            else if (value is IEnumerable sequence and not string)
            {
                foreach (var item in sequence)
                {
                    Visit(item);
                }
            }
        }
    }

    private static string Names(IEnumerable<Type> types) => string.Join(", ", types.Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal));

    private static string FirstByteDifference(byte[] golden, byte[] actual)
    {
        var offset = 0;
        while (offset < Math.Min(golden.Length, actual.Length) && golden[offset] == actual[offset])
        {
            offset++;
        }

        var expected = offset < golden.Length ? $"0x{golden[offset]:X2}" : "EOF";
        var observed = offset < actual.Length ? $"0x{actual[offset]:X2}" : "EOF";
        return $"First differing byte offset {offset}: golden {expected}, actual {observed}; lengths {golden.Length}/{actual.Length}.";
    }
}
