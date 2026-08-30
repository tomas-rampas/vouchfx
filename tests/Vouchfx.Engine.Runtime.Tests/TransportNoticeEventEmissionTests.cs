// Tests for the transport-notice PRODUCER (#450 / #453) — TransportNoticeEvents plus the three
// ScenarioRunner sites that call it. NO DOCKER: the notice records are ordinary typed values, so
// the mapping, the cardinality equivalence, the `replayed` policy and the producer-side
// rejected-endpoint invariant are all exercised directly, and the "every print site also emits"
// property is asserted by reading the production source — the idiom
// SecretObservationLeakPenetrationTests already uses for its scrubbing chokepoint.
//
// The WIRE record itself (its property names, CLR types, round-trip and the frozen golden) is
// pinned one layer down in Vouchfx.Engine.Abstractions.Tests/Events/TransportNoticeEventTests.cs.
// Nothing here re-asserts that; these tests are about what the ENGINE puts on the wire.
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Orchestration;
using Vouchfx.Engine.Runtime;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// No-docker tests for <see cref="TransportNoticeEvents"/> and the three
/// <see cref="ScenarioRunner"/> emission sites (#450, #453).
/// </summary>
public sealed class TransportNoticeEventEmissionTests
{
    private const string Run = "run-transport-notice";
    private static readonly DateTimeOffset At = new(2026, 8, 29, 10, 30, 0, TimeSpan.Zero);

    // ── The mapping: one notice kind, one wire kind, the right fields ────────────────────────

    /// <summary>
    /// A downgrade notice becomes exactly one <c>plaintext-downgrade</c> record naming BOTH
    /// endpoints — the selected one and the one that was available and not chosen.
    /// </summary>
    [Fact]
    public void SelectionNotice_BecomesOnePlaintextDowngradeRecord_NamingBothEndpoints()
    {
        var lines = TransportNoticeEvents.ToLines(
            new[] { new EndpointSelectionNotice("checkout-api", "http", "https") },
            Array.Empty<EndpointTrustNotice>(),
            Run,
            At,
            replayed: false);

        var record = Assert.Single(lines);
        using var doc = JsonDocument.Parse(record);
        var root = doc.RootElement;

        Assert.Equal(EventTypes.TransportNotice, root.GetProperty("type").GetString());
        Assert.Equal(TransportNoticeKinds.PlaintextDowngrade, root.GetProperty("kind").GetString());
        Assert.Equal("checkout-api", root.GetProperty("service").GetString());
        Assert.Equal("http", root.GetProperty("selectedEndpoint").GetString());
        Assert.Equal("https", root.GetProperty("rejectedEndpoint").GetString());
    }

    /// <summary>
    /// A trust notice becomes exactly one <c>no-engine-trust</c> record naming the selected
    /// endpoint and NO rejected one.
    /// </summary>
    [Fact]
    public void TrustNotice_BecomesOneNoEngineTrustRecord_NamingOnlyTheSelectedEndpoint()
    {
        var lines = TransportNoticeEvents.ToLines(
            Array.Empty<EndpointSelectionNotice>(),
            new[] { new EndpointTrustNotice("checkout-api", "https") },
            Run,
            At,
            replayed: false);

        var record = Assert.Single(lines);
        using var doc = JsonDocument.Parse(record);
        var root = doc.RootElement;

        Assert.Equal(TransportNoticeKinds.NoEngineTrust, root.GetProperty("kind").GetString());
        Assert.Equal("checkout-api", root.GetProperty("service").GetString());
        Assert.Equal("https", root.GetProperty("selectedEndpoint").GetString());
        Assert.False(root.TryGetProperty("rejectedEndpoint", out _));
    }

    /// <summary>
    /// The producer-side invariant, asserted AT THE PRODUCER because nothing else can enforce it:
    /// <see cref="TransportNoticeEvent.RejectedEndpoint"/> is set for the downgrade kind and null
    /// for the trust kind.
    /// </summary>
    /// <remarks>
    /// The wire record's shape permits a rejected endpoint on either kind — it is a nullable
    /// string with no discriminated union behind it, which is what lets a third advisory be added
    /// additively later. The pairing is therefore a property of the two <c>Create</c> overloads,
    /// and this asserts it over both at once rather than trusting each in isolation.
    /// </remarks>
    [Fact]
    public void RejectedEndpoint_IsSetForTheDowngradeKindAndNullForTheTrustKind()
    {
        var downgrade = TransportNoticeEvents.Create(
            new EndpointSelectionNotice("svc", "http", "https"), Run, At, replayed: false);
        var trust = TransportNoticeEvents.Create(
            new EndpointTrustNotice("svc", "https"), Run, At, replayed: false);

        Assert.Equal(TransportNoticeKinds.PlaintextDowngrade, downgrade.Kind);
        Assert.Equal("https", downgrade.RejectedEndpoint);

        Assert.Equal(TransportNoticeKinds.NoEngineTrust, trust.Kind);
        Assert.Null(trust.RejectedEndpoint);
    }

    /// <summary>
    /// <c>selectedEndpoint</c> carries the endpoint NAME the notice holds, verbatim — never a
    /// resolved URL and never a <c>host:port</c> authority.
    /// </summary>
    /// <remarks>
    /// <c>EnvironmentMapper.StageServiceEndpoint</c> stages bare authorities for some targets, so
    /// a producer that reached for the staged value rather than the notice's field would put one
    /// on the wire; the disclosure analysis that cleared this record for archived CI artefacts
    /// depends on it not doing that. Asserted with an endpoint deliberately named nothing like a
    /// scheme, so a producer that synthesised <c>"https"</c> from the kind would fail here.
    /// </remarks>
    [Fact]
    public void SelectedEndpoint_IsTheEndpointNameVerbatim_NotAUrlOrAuthority()
    {
        var downgrade = TransportNoticeEvents.Create(
            new EndpointSelectionNotice("svc", "public-plain", "secure-api"), Run, At, replayed: false);
        var trust = TransportNoticeEvents.Create(
            new EndpointTrustNotice("svc", "secure-api"), Run, At, replayed: false);

        Assert.Equal("public-plain", downgrade.SelectedEndpoint);
        Assert.Equal("secure-api", downgrade.RejectedEndpoint);
        Assert.Equal("secure-api", trust.SelectedEndpoint);
    }

    // ── `replayed`: set on the watch replay, ABSENT (not false) on a fresh build ─────────────

    /// <summary>
    /// The two fresh-build sites pass <c>replayed: false</c>, and the field must then be absent
    /// from the wire — NOT written as <c>"replayed":false</c>.
    /// </summary>
    /// <remarks>
    /// The distinction is real rather than pedantic: the shared serialiser's
    /// <c>DefaultIgnoreCondition = WhenWritingNull</c> omits a null and WRITES a
    /// <see langword="false"/> (pinned one layer down by
    /// <c>TransportNoticeEventTests.Replayed_False_IsWrittenExplicitly</c>). A producer that mapped
    /// <c>replayed: false</c> onto <see langword="false"/> rather than <see langword="null"/> would
    /// therefore put a new field on every stream carrying an advisory.
    /// </remarks>
    [Fact]
    public void FreshBuild_LeavesReplayedOffTheWireEntirely()
    {
        var lines = TransportNoticeEvents.ToLines(
            new[] { new EndpointSelectionNotice("svc", "http", "https") },
            new[] { new EndpointTrustNotice("other", "https") },
            Run,
            At,
            replayed: false);

        Assert.Equal(2, lines.Count);
        foreach (var line in lines)
        {
            Assert.DoesNotContain("\"replayed\"", line, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(line);
            Assert.False(doc.RootElement.TryGetProperty("replayed", out _));
        }

        // And the record itself carries null, not false — the property the omission rests on.
        Assert.Null(TransportNoticeEvents
            .Create(new EndpointSelectionNotice("svc", "http", "https"), Run, At, replayed: false)
            .Replayed);
    }

    /// <summary>
    /// The <c>--watch</c> replay passes <c>replayed: true</c>, and the field reaches the wire.
    /// </summary>
    [Fact]
    public void WatchReplay_WritesReplayedTrue()
    {
        var lines = TransportNoticeEvents.ToLines(
            new[] { new EndpointSelectionNotice("svc", "http", "https") },
            new[] { new EndpointTrustNotice("other", "https") },
            Run,
            At,
            replayed: true);

        Assert.Equal(2, lines.Count);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.True(doc.RootElement.GetProperty("replayed").GetBoolean());
        }
    }

    // ── Cardinality: the terminal's, per site — an equivalence, not a fixed count ────────────

    /// <summary>
    /// Records emitted == notices supplied, for every combination of the two collections.
    /// </summary>
    /// <remarks>
    /// This is the testable half of "match the terminal's cardinality exactly". The other half —
    /// that each print site hands this producer the SAME two collections it iterates to print — is
    /// <see cref="EveryPrintSite_AlsoEmitsTheRecord"/>. A fixed count would only hold on
    /// one path: <c>RunSuiteAsync</c> builds one topology for the whole selection and reports each
    /// advisory once, however many files that selection held, while
    /// <c>RunScenarioOwningTopologyAsync</c> is entered once per scenario-owned topology — which in
    /// production means <c>--parallel</c>'s fan-out — and legitimately reports several times in one
    /// run. Asserting the equivalence rather than a count is what makes this independent of which
    /// of the two <c>RunCommand</c>'s dispatch picked.
    /// </remarks>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    public void RecordsEmitted_EqualsNoticesSupplied(int selectionCount, int trustCount)
    {
        var selection = Enumerable.Range(0, selectionCount)
            .Select(i => new EndpointSelectionNotice($"svc-{i}", "http", "https"))
            .ToArray();
        var trust = Enumerable.Range(0, trustCount)
            .Select(i => new EndpointTrustNotice($"tls-{i}", "https"))
            .ToArray();

        var lines = TransportNoticeEvents.ToLines(selection, trust, Run, At, replayed: false);

        Assert.Equal(selectionCount + trustCount, lines.Count);
    }

    /// <summary>
    /// EDGE-001: a run with no advisory emits NOTHING, so the stream a consumer sees is
    /// byte-identical to one from before this record existed.
    /// </summary>
    /// <remarks>
    /// Asserted over a buffer rather than only over the returned list, because the emission sites
    /// append to a buffer that other events already occupy — an empty append must leave that buffer
    /// untouched, which is the property the byte-identity argument actually rests on.
    /// </remarks>
    [Fact]
    public void NoNotices_EmitNothing_AndLeaveTheStreamByteIdentical()
    {
        var buffer = new List<string> { "{\"type\":\"scenario-started\"}", "{\"type\":\"scenario-completed\"}" };
        var before = string.Join("\n", buffer);

        buffer.AddRange(TransportNoticeEvents.ToLines(
            Array.Empty<EndpointSelectionNotice>(),
            Array.Empty<EndpointTrustNotice>(),
            Run,
            At,
            replayed: false));

        Assert.Equal(before, string.Join("\n", buffer));
    }

    /// <summary>
    /// EDGE-002: an untargeted service raises neither notice, so neither record is emitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate itself lives at the producer of the NOTICES — <c>EnvironmentMapper</c> raises each
    /// only when <c>endpointTargets.Contains(name)</c> — and is pinned there by
    /// <c>ProjectServiceEndpointStagingTests.Configure_UntargetedProjectServiceDeclaringBothSchemes_AnnouncesNothing</c>,
    /// <c>…_UntargetedHttpsOnlyProjectWithNoDeclaredEndpoint_AnnouncesNothing</c> and
    /// <c>…_UntargetedProjectServiceDeclaringEndpointHttps_AnnouncesNothing</c>. What THIS test
    /// pins is the other half of the composition, which is the half that lives here: no notice
    /// produces no record. Together they are the edge case; separately neither is.
    /// </para>
    /// <para>
    /// Worth pinning because the gate is easy to lose when a producer moves — a future emitter that
    /// read the topology's services rather than its notices would reintroduce the record for a
    /// service no step addresses, and the notice-side tests above would all stay green.
    /// </para>
    /// </remarks>
    [Fact]
    public void UntargetedService_RaisesNoNotice_SoEmitsNoRecord()
    {
        // An untargeted service contributes nothing to either collection — this is the state
        // EnvironmentMapper's targeting gate leaves the topology in.
        Assert.Empty(TransportNoticeEvents.ToLines(
            Array.Empty<EndpointSelectionNotice>(),
            Array.Empty<EndpointTrustNotice>(),
            Run,
            At,
            replayed: false));
    }

    /// <summary>
    /// EDGE-003: two services each triggering an advisory produce two records, distinguishable by
    /// service name. <strong>Ordering is asserted as a SET</strong> — it is not part of the wire
    /// contract and must not be pinned as though it were.
    /// </summary>
    [Fact]
    public void TwoServices_ProduceTwoRecords_DistinguishableByService()
    {
        var lines = TransportNoticeEvents.ToLines(
            new[] { new EndpointSelectionNotice("orders", "http", "https") },
            new[] { new EndpointTrustNotice("payments", "https") },
            Run,
            At,
            replayed: false);

        Assert.Equal(2, lines.Count);

        // Disposed per line: JsonDocument rents from a shared pool, and the two
        // strings are all that outlive the parse — no JsonElement escapes, so the
        // `using` costs nothing here and keeps the file's one pattern.
        var byService = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            byService.Add(
                doc.RootElement.GetProperty("service").GetString()!,
                doc.RootElement.GetProperty("kind").GetString()!);
        }

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "orders", "payments" },
            byService.Keys.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(TransportNoticeKinds.PlaintextDowngrade, byService["orders"]);
        Assert.Equal(TransportNoticeKinds.NoEngineTrust, byService["payments"]);
    }

    // ── EDGE-009: the envelope the six existing records carry ────────────────────────────────

    /// <summary>
    /// The record's envelope fields are exactly those every other event on this stream carries —
    /// same wire names, same shapes — so a renderer keyed on the envelope can place it.
    /// </summary>
    /// <remarks>
    /// Compared against a real <see cref="ScenarioStartedEvent"/> line rather than a hand-written
    /// list, so a future envelope change that moves both moves this test with them, and one that
    /// moves only the new record fails here.
    /// </remarks>
    [Fact]
    public void Envelope_MatchesWhatTheExistingRecordsCarry()
    {
        var reference = EventStreamJson.ToLine(new ScenarioStartedEvent
        {
            RunId = Run,
            Timestamp = At,
            ScenarioId = "s",
        });
        var subject = TransportNoticeEvents.ToLines(
            new[] { new EndpointSelectionNotice("svc", "http", "https") },
            Array.Empty<EndpointTrustNotice>(),
            Run,
            At,
            replayed: false)[0];

        using var referenceDoc = JsonDocument.Parse(reference);
        using var subjectDoc = JsonDocument.Parse(subject);

        // The envelope: schema generation, schema version, type discriminator, timestamp, run id.
        Assert.Equal(
            referenceDoc.RootElement.GetProperty("v").GetInt32(),
            subjectDoc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal(
            referenceDoc.RootElement.GetProperty("schemaVersion").GetString(),
            subjectDoc.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(
            referenceDoc.RootElement.GetProperty("ts").GetDateTimeOffset(),
            subjectDoc.RootElement.GetProperty("ts").GetDateTimeOffset());
        Assert.Equal(Run, subjectDoc.RootElement.GetProperty("runId").GetString());
        Assert.Equal(EventTypes.TransportNotice, subjectDoc.RootElement.GetProperty("type").GetString());

        // And it parses as the generic envelope every renderer reads, with the payload fields
        // landing in Extra (the §14 forward-compatibility path).
        var envelope = EventStreamJson.FromLine(subject);
        Assert.Equal(EventTypes.TransportNotice, envelope.Type);
        Assert.Equal(Run, envelope.RunId);
    }

    /// <summary>
    /// The record carries NO <c>verdict</c> field: it is advisory, and §12.1's taxonomy and the
    /// exit code are decided elsewhere.
    /// </summary>
    /// <remarks>
    /// <c>EnvironmentErrorEvent</c> — the other run-level record and the shape precedent this
    /// producer follows — DOES carry one, hard-coded to <c>ENV_ERROR</c>, so the absence here is a
    /// deliberate difference rather than an oversight. A <c>verdict</c> appearing on this record
    /// would put a healthy run's advisory into the same channel renderers use to route outcomes.
    /// </remarks>
    [Fact]
    public void Record_CarriesNoVerdict_SoEmissionCannotTouchTheTaxonomy()
    {
        var lines = TransportNoticeEvents.ToLines(
            new[] { new EndpointSelectionNotice("svc", "http", "https") },
            new[] { new EndpointTrustNotice("svc2", "https") },
            Run,
            At,
            replayed: false);

        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            Assert.False(doc.RootElement.TryGetProperty("verdict", out _));
        }
    }

    // ── EDGE-004: the emission is safe under the concurrency --parallel's site operates in ──

    /// <summary>
    /// A smoke check that the producer's own output survives being posted concurrently into one
    /// <see cref="LiveEventPump"/>. <strong>The EDGE-004 evidence is the reading in the remarks
    /// below, not this test</strong> — what runs here is eight tasks against one pump, which is
    /// <c>LiveEventPumpTests</c>' subject, and nothing shared with the emission site is under test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EDGE-004 asks whether the emission is safe under the concurrency <c>--parallel</c> already
    /// operates in, and that is established by READING the fan-out and the pump rather than by
    /// reasoning from the flag's name. <c>--parallel</c> fans out through
    /// <c>ParallelSuiteRunner</c> into <c>RunScenarioOwningTopologyAsync</c> — a site plain
    /// <c>run</c> does NOT reach, since <c>RunCommand</c>'s dispatch sends the flagless path to
    /// <c>RunSuiteAsync</c> instead. <c>ParallelSuiteRunner.RunParallelCoreAsync</c> opens a single
    /// pump and passes that same instance to every slot task (bounded by a <c>SemaphoreSlim</c>, joined by
    /// <c>Task.WhenAll</c>), so the transport emission at that site runs on N threads against one
    /// conduit. <c>LiveEventPump</c> is built for exactly that: its channel is created with
    /// <c>SingleWriter = false</c>, every write goes through the thread-safe, non-blocking
    /// <c>Writer.TryWrite</c>, and the drop counter is bumped with <c>Interlocked.Increment</c>.
    /// The per-scenario <c>buffer</c> the same site appends to is a local list owned by one task
    /// and shared with nothing, so it needs no synchronisation and none is claimed for it here.
    /// </para>
    /// <para>
    /// Volume is kept well under <c>LiveEventPump.DefaultCapacity</c> so what this exercises is
    /// concurrency, not the overflow policy (which <c>LiveEventPumpTests</c> owns).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentEmission_IntoOneSharedPump_LosesNothing()
    {
        const int Scenarios = 8;
        var path = Path.Combine(
            Path.GetTempPath(), "vouchfx-transport-notice-" + Guid.NewGuid().ToString("n") + ".jsonl");

        try
        {
            await using (var pump = new LiveEventPump(path))
            {
                await Task.WhenAll(Enumerable.Range(0, Scenarios).Select(i => Task.Run(() =>
                {
                    // The shared half of what the site does. Its per-scenario buffer is a local
                    // owned by one task, so reproducing it here would test nothing.
                    var lines = TransportNoticeEvents.ToLines(
                        new[] { new EndpointSelectionNotice($"svc-{i}", "http", "https") },
                        new[] { new EndpointTrustNotice($"svc-{i}", "https") },
                        $"run-{i}",
                        At,
                        replayed: false);
                    pump.PostRange(lines);
                })));
            }

            var written = (await File.ReadAllLinesAsync(path))
                .Where(l => l.Length > 0)
                .ToList();

            Assert.Equal(Scenarios * 2, written.Count);

            // Disposed per line, for the reason given at the two-services test above.
            var runIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in written)
            {
                using var doc = JsonDocument.Parse(line);
                runIds.Add(doc.RootElement.GetProperty("runId").GetString()!);
            }

            Assert.Equal(
                Enumerable.Range(0, Scenarios).Select(i => $"run-{i}").ToHashSet(StringComparer.Ordinal),
                runIds);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// EDGE-008: a run with no <c>--events-stream</c> destination has no pump at all, and the
    /// emission must be a no-op rather than a null-reference — a healthy advisory must never crash
    /// a run.
    /// </summary>
    /// <remarks>
    /// The runtime half is trivial (a null-conditional post), so the load-bearing half is the
    /// source assertion in <see cref="EveryPrintSite_AlsoEmitsTheRecord"/> that every
    /// transport post in the runner really is null-conditional. This pins the behaviour the sites
    /// depend on: <c>PostRange</c> on a null pump is a no-op, and the lines still reach the buffer
    /// that feeds the <c>--events</c> archive.
    /// </remarks>
    [Fact]
    public void NoEventsDestination_EmitsNoStream_AndDoesNotThrow()
    {
        LiveEventPump? pump = null;
        var buffer = new List<string>();

        var lines = TransportNoticeEvents.ToLines(
            new[] { new EndpointSelectionNotice("svc", "http", "https") },
            Array.Empty<EndpointTrustNotice>(),
            Run,
            At,
            replayed: false);
        buffer.AddRange(lines);
        pump?.PostRange(lines);

        Assert.Single(buffer);
    }

    // ── The routing gate: every print site emits, through the one producer ───────────────────

    /// <summary>
    /// Every method in <c>Vouchfx.Engine.Runtime</c> or <c>Vouchfx.Cli</c> that reads
    /// <c>EndpointSelectionNotices</c> / <c>EndpointTrustNotices</c> to PRINT them must also call
    /// <c>TransportNoticeEvents.ToLines</c> to EMIT them — and must pass the pump result through a
    /// null-conditional post.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the tests above are the trap they are meant to close: they prove the PRODUCER
    /// maps correctly, and would stay green if a fourth print site were added tomorrow with no
    /// emission, or if one of the three lost its call in a refactor. Three sites are three ways to
    /// forget one — the defect #450 and #453 exist to report was precisely a print with no record.
    /// </para>
    /// <para>
    /// <strong>Two roots, because the collections are public and Runtime does not own them.</strong>
    /// Every site that reads the two collections in order to PRINT them is in Runtime today, so the
    /// CLI root adds no assertion now — it closes a future hole. It is also a hole the CLI can
    /// actually fall into: <c>Vouchfx.Engine.Runtime.csproj</c> grants
    /// <c>InternalsVisibleTo("vouchfx")</c> and the CLI's assembly name IS <c>vouchfx</c>, so a
    /// print site there genuinely could call the internal producer — without that, the CLI root
    /// would be open to the same "demands the impossible" objection that excludes Orchestration.
    /// <c>SuiteTopology.EndpointSelectionNotices</c> and <c>EndpointTrustNotices</c> are PUBLIC
    /// members of Orchestration, so a print site added in <c>Vouchfx.Cli</c> — a <c>--dry-run</c>
    /// summary echoing the advisories, say — would print, emit nothing, and be caught by nothing.
    /// The CLI is also where the hand-rolled-record assertion below most needs to reach: it is the
    /// assembly most likely to want to build a <c>TransportNoticeEvent</c> of its own.
    /// </para>
    /// <para>
    /// <strong>Orchestration is deliberately NOT a third root, and the decisive reason is the
    /// dependency direction rather than any of the softer ones.</strong> Orchestration sits BELOW
    /// Runtime and <c>TransportNoticeEvents</c> is internal to Runtime, so a call from there is a
    /// reference cycle — impossible, not merely awkward. The softer reasons are true too (no event
    /// destination, no run id, no opinion about replay), but stating only those invites an
    /// objection they do not survive: <c>EnvironmentErrorEvents</c> is a §14 record with a
    /// <c>ToLine</c> serialiser and a runId parameter, and it lives in Orchestration. That is not a
    /// counter-example — every one of its <c>ToLine</c> calls is in <c>ScenarioRunner</c>, with the
    /// runId passed in — but a reader who finds it should meet the cycle argument first.
    /// </para>
    /// <para>
    /// Asserts the PROPERTY (print ⇒ emit, in the same method) rather than a count of sites, so the
    /// gate survives a site being added or moved. Reads the production source in the shape
    /// <c>SecretObservationLeakPenetrationTests.EveryEnvironmentErrorEmission_InRuntime_GoesThroughTheScrubbingChokepoint</c>
    /// already uses.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryPrintSite_AlsoEmitsTheRecord()
    {
        var repositoryRoot = RepositoryRoot();
        var runtimeRoot = Path.Combine(repositoryRoot, "src", "Engine", "Vouchfx.Engine.Runtime");
        var roots = new[]
        {
            runtimeRoot,
            Path.Combine(repositoryRoot, "src", "Cli", "Vouchfx.Cli"),
        };

        var sep = Path.DirectorySeparatorChar;
        var producerPath = Path.Combine(runtimeRoot, "TransportNoticeEvents.cs");

        var sources = roots
            .SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                     && !p.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .ToList();

        var printingMethods = 0;
        var emittingMethods = new List<string>();

        foreach (var file in sources)
        {
            var source = File.ReadAllText(file);

            // The producer itself is the one file exempt from the print ⇒ emit implication, for
            // the obvious reason: it never prints. It names both collections in its own parameter
            // documentation, and this scan reads comments as well as code — deliberately, since a
            // gate that fails closed on a stray mention is louder and cheaper than one that misses
            // a real site. Skipped here rather than special-cased below so the exemption is one
            // decision in one place.
            //
            // MATCHED ON THE FULL PATH, NOT THE FILE NAME, and the second root is exactly why.
            // While Runtime was the only root, a name match was safe because only one file could
            // carry it. With the CLI scanned too, `src/Cli/Vouchfx.Cli/TransportNoticeEvents.cs`
            // would be exempted in full — free to print without emitting AND to hand-build the
            // record — and that name is not a contrived one: it is precisely what a second,
            // CLI-side producer would be called, which is the shape this widening exists to catch.
            if (string.Equals(file, producerPath, StringComparison.Ordinal))
            {
                continue;
            }

            // Nothing outside the producer may build the wire record by hand — that would bypass
            // its `replayed` policy and its rejected-endpoint pairing just as effectively as a
            // missing call, and the attribution loop below would not see it.
            //
            // Asserted on the TYPE NAME, not on a `new` spelling: `new TransportNoticeEvent` is
            // only one of the ways to construct one, and the target-typed
            // `EventStreamJson.ToLine<TransportNoticeEvent>(new() { … })` contains no `new
            // TransportNoticeEvent` anywhere. Naming the type is unavoidable on every route to a
            // hand-built record — a generic argument, a declared local, a cast — so the name is
            // the property to pin. The word boundaries are what keep the two longer identifiers
            // the runner legitimately names, `TransportNoticeEvents` (this producer) and
            // `TransportNoticeEventEmissionTests` (this file), out of it.
            var handRolled = Regex.Match(source, @"\bTransportNoticeEvent\b");
            Assert.False(
                handRolled.Success,
                $"'{Path.GetFileName(file)}' names the wire record TransportNoticeEvent directly "
                + $"(offset {handRolled.Index}). Only TransportNoticeEvents may construct it — a "
                + "hand-built record bypasses the `replayed` policy and the rejected-endpoint "
                + "pairing just as effectively as a missing call, and the attribution loop below "
                + "would not see it.");

            // Declarations at class-member indentation, in file order, so each occurrence can be
            // attributed to the method containing it.
            var declarations = Regex.Matches(
                    source,
                    @"^    (?:private|internal|public)[^\r\n=]*?\b(\w+)\(",
                    RegexOptions.Multiline)
                .Select(m => (m.Index, Name: m.Groups[1].Value))
                .ToList();

            string MethodAt(int index) =>
                declarations.LastOrDefault(d => d.Index < index).Name ?? "(file scope)";

            // Group every occurrence — of a print read and of an emission — by containing method.
            var reads = Regex.Matches(source, @"\.Endpoint(?:Selection|Trust)Notices\b")
                .Select(m => MethodAt(m.Index))
                .ToHashSet(StringComparer.Ordinal);
            var emissions = Regex.Matches(source, @"TransportNoticeEvents\.ToLines\(")
                .Select(m => MethodAt(m.Index))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var method in reads)
            {
                printingMethods++;
                Assert.True(
                    emissions.Contains(method),
                    $"'{Path.GetFileName(file)}'.{method} reads EndpointSelectionNotices / "
                    + "EndpointTrustNotices but never calls TransportNoticeEvents.ToLines. Every "
                    + "path that PRINTS a transport advisory must also EMIT its §14 record "
                    + "(#450 / #453) — a path that prints without emitting reproduces the defect "
                    + "those issues closed, in a narrower place that is harder to find.");
            }

            emittingMethods.AddRange(emissions);
        }

        // Not vacuous: the sites really do exist, and each one that emits also prints.
        Assert.True(printingMethods > 0, "No transport-advisory print site was found at all.");
        Assert.NotEmpty(emittingMethods);

        // Every post of a transport line is null-conditional (EDGE-008): a run with no
        // --events-stream destination has no pump, and the advisory must not crash it.
        var runner = File.ReadAllText(Path.Combine(runtimeRoot, "ScenarioRunner.cs"));
        Assert.DoesNotContain(
            "livePump.PostRange(transportNoticeLines)", runner, StringComparison.Ordinal);
        Assert.Contains(
            "livePump?.PostRange(transportNoticeLines)", runner, StringComparison.Ordinal);

        // THE DELIVERY, not just the call. Everything above pins that a printing method CALLS the
        // producer; nothing yet pins that it keeps the result. Deleting a single
        // `buffer.AddRange(transportNoticeLines)` / `allBuffers.AddRange(transportNoticeLines)`
        // leaves every assertion above green while the record silently vanishes from the --events
        // archive — and the orphaned local does not even redden the build, since IDE0059 sits at
        // suggestion severity and .editorconfig promotes only IDE0055. So: every line this file
        // produces is added to an event buffer, and every live post has a buffered sibling (the
        // buffer is the wider destination — the watch site buffers with no pump at all).
        var produced = Regex.Count(runner, @"TransportNoticeEvents\.ToLines\(");
        var buffered = Regex.Count(
            runner, @"AddRange\((?:transportNoticeLines\)|TransportNoticeEvents\.ToLines\()");
        var posted = Regex.Count(runner, @"PostRange\(transportNoticeLines\)");

        Assert.True(
            produced > 0 && buffered == produced,
            $"ScenarioRunner.cs calls TransportNoticeEvents.ToLines {produced} time(s) but adds "
            + $"the result to an event buffer {buffered} time(s). A produced-but-unbuffered line "
            + "is dropped from the --events archive with no other test and no compiler warning to "
            + "say so.");
        Assert.True(
            posted > 0 && posted <= buffered,
            $"{posted} live post(s) of transport lines against {buffered} buffered. Every post "
            + "must have a buffered sibling: --events is written from the buffer, and a line that "
            + "reaches only the live stream is absent from the end-of-run archive.");
    }

    /// <summary>
    /// The wire path must NOT be sanitised: <c>DisplaySanitiser.SanitiseForDisplay</c> may not be
    /// applied to anything the producer puts on the stream.
    /// </summary>
    /// <remarks>
    /// Sanitising at the producer would make the wire <c>service</c> differ from the author's YAML
    /// key — breaking the only correlation a consumer has back to the suite — and would bake a
    /// render-time concern into a frozen contract. <c>DisplaySanitiser</c>'s own remarks state the
    /// <c>--events</c> path needs no equivalent, because <c>System.Text.Json</c> always
    /// <c>\u</c>-escapes control characters. Asserted structurally: the producer file names the
    /// sanitiser nowhere but in the comment explaining why it is absent, so a call would show up as
    /// an invocation.
    /// </remarks>
    [Fact]
    public void Producer_DoesNotSanitiseForDisplay()
    {
        var producer = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Engine", "Vouchfx.Engine.Runtime", "TransportNoticeEvents.cs"));

        Assert.DoesNotContain("SanitiseForDisplay(", producer, StringComparison.Ordinal);
        Assert.DoesNotContain(".Scrub(", producer, StringComparison.Ordinal);
    }

    /// <summary>
    /// A control character in a service name survives to the wire as a JSON escape, unaltered —
    /// which is what makes producer-side sanitisation unnecessary rather than merely undesirable.
    /// </summary>
    [Fact]
    public void ControlCharacterInAServiceName_IsJsonEscaped_NotStripped()
    {
        var line = TransportNoticeEvents.ToLines(
            new[] { new EndpointSelectionNotice("svc\u001b[31m", "http", "https") },
            Array.Empty<EndpointTrustNotice>(),
            Run,
            At,
            replayed: false)[0];

        // No literal ESC byte on the wire …
        Assert.DoesNotContain("\u001b", line, StringComparison.Ordinal);

        // … and the value round-trips to exactly what the author declared.
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("svc\u001b[31m", doc.RootElement.GetProperty("service").GetString());
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
