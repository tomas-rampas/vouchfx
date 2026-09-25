// Tests for #573: EventStreamJson.FromLine<T> must refuse a typed record whose OWN
// `required` reference-type member deserialised to null.
//
// Background: #571 taught the UNTYPED EventStreamJson.FromLine(string) to refuse a null
// `runId`/`type` on the envelope, because STJ's `required` enforces presence only, not
// non-null (Abstractions carries no STJ package reference, so it compiles against the
// in-box STJ 8, which has no `RespectNullableAnnotations` opt-in). The generic
// FromLine<T> overload was left unguarded: every typed payload record (ScenarioStartedEvent,
// StepCompletedEvent, ...) declares its OWN `required` reference-type members
// (scenarioId, stepId, counts, ...) that the untyped guard cannot see. #573 (see the
// issue's "counts": null comment) closes that gap via per-closed-T reflection rather than
// `RespectNullableAnnotations`, per the maintainer's decision recorded on the issue.
//
// This file exercises, per typed record, EVERY `required` reference-type member set to
// null (theories below); the cross-cutting cases the production remarks call out
// (absent-vs-null are distinct failure paths; empty string is legal; a nullable optional
// member accepts null; a required member that is ITSELF nullable-annotated accepts null);
// a census that cross-checks the production reflection cache against the frozen
// EventContractFreezeTests golden for EVERY record it lists; and three hardenings for a
// hosting scenario this repo does not itself exercise (Abstractions ships as a published
// package): a host with NullabilityInfoContextSupport=false where NullabilityInfoContext
// .Create throws (NOT a default trimmed/AOT publish, which fails earlier — see the
// production remarks), a type from a collectible AssemblyLoadContext that must never be
// pinned in the static cache, and a required member with a throwing getter.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

/// <summary>
/// #573: <see cref="EventStreamJson.FromLine{T}"/> null-guards every typed record's own
/// <see langword="required"/> reference-type members.
/// </summary>
public sealed class EventStreamJsonRequiredReferenceNullTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Serialises <paramref name="validPayload"/>, then rewrites its <paramref name="wireFieldName"/>
    /// wire field to an explicit JSON <see langword="null"/> (adding the key if it was
    /// previously omitted, e.g. an optional field that <c>WhenWritingNull</c> suppressed) —
    /// this is the "otherwise satisfies <c>required</c>" wire shape #573 is about.
    /// </summary>
    private static string LineWithNullField<T>(T validPayload, string wireFieldName)
    {
        var line = EventStreamJson.ToLine(validPayload);
        var node = JsonNode.Parse(line)!.AsObject();
        node[wireFieldName] = null;
        return node.ToJsonString();
    }

    private static void AssertNullRequiredReferenceMemberThrows<T>(
        T validPayload, string wireFieldName, string recordTypeName)
    {
        var line = LineWithNullField(validPayload, wireFieldName);

        var ex = Assert.Throws<InvalidOperationException>(() => EventStreamJson.FromLine<T>(line));

        Assert.Equal(
            $"Event-stream line has a null {wireFieldName}; the {recordTypeName} field is required.",
            ex.Message);
    }

    // =========================================================================
    // ScenarioStartedEvent — required reference members: runId, scenarioId
    // =========================================================================

    [Theory]
    [InlineData("runId")]
    [InlineData("scenarioId")]
    public void ScenarioStartedEvent_NullRequiredReferenceMember_Throws(string wireFieldName)
    {
        var valid = new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "scenario-1" };

        AssertNullRequiredReferenceMemberThrows(valid, wireFieldName, nameof(ScenarioStartedEvent));
    }

    // =========================================================================
    // StepStartedEvent — required reference members: runId, stepId
    // =========================================================================

    [Theory]
    [InlineData("runId")]
    [InlineData("stepId")]
    public void StepStartedEvent_NullRequiredReferenceMember_Throws(string wireFieldName)
    {
        var valid = new StepStartedEvent { RunId = "run-1", StepId = "step-1" };

        AssertNullRequiredReferenceMemberThrows(valid, wireFieldName, nameof(StepStartedEvent));
    }

    // =========================================================================
    // StepAttemptEvent — required reference members: runId, stepId
    // =========================================================================

    [Theory]
    [InlineData("runId")]
    [InlineData("stepId")]
    public void StepAttemptEvent_NullRequiredReferenceMember_Throws(string wireFieldName)
    {
        var valid = new StepAttemptEvent { RunId = "run-1", StepId = "step-1", Attempt = 1, TMs = 100L };

        AssertNullRequiredReferenceMemberThrows(valid, wireFieldName, nameof(StepAttemptEvent));
    }

    // =========================================================================
    // StepCompletedEvent — required reference members: runId, stepId
    // (Verdict and DurationMs are required too, but are VALUE types — excluded)
    // =========================================================================

    [Theory]
    [InlineData("runId")]
    [InlineData("stepId")]
    public void StepCompletedEvent_NullRequiredReferenceMember_Throws(string wireFieldName)
    {
        var valid = new StepCompletedEvent
        {
            RunId = "run-1",
            StepId = "step-1",
            Verdict = Verdict.Pass,
            DurationMs = 100L,
        };

        AssertNullRequiredReferenceMemberThrows(valid, wireFieldName, nameof(StepCompletedEvent));
    }

    // =========================================================================
    // ScenarioCompletedEvent — required reference members: runId, scenarioId, counts
    // (Verdict is required too, but is a VALUE type — excluded)
    // =========================================================================

    [Theory]
    [InlineData("runId")]
    [InlineData("scenarioId")]
    [InlineData("counts")]
    public void ScenarioCompletedEvent_NullRequiredReferenceMember_Throws(string wireFieldName)
    {
        // This is the exact #573 shape: a `"counts": null` line, which — pre-fix — deserialised
        // to a ScenarioCompletedEvent with a null Counts, so a consumer's `scenario.Counts.Pass`
        // (TelemetryEventBuilder.AccumulateScenarioCompleted) threw NullReferenceException.
        var valid = new ScenarioCompletedEvent
        {
            RunId = "run-1",
            ScenarioId = "scenario-1",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        };

        AssertNullRequiredReferenceMemberThrows(valid, wireFieldName, nameof(ScenarioCompletedEvent));
    }

    // =========================================================================
    // ReproducibilityEnvelopeEvent — required reference members: runId, scenarioId,
    // envSchemaVersion, secretReferences, fixtures
    // =========================================================================

    [Theory]
    [InlineData("runId")]
    [InlineData("scenarioId")]
    [InlineData("envSchemaVersion")]
    [InlineData("secretReferences")]
    [InlineData("fixtures")]
    public void ReproducibilityEnvelopeEvent_NullRequiredReferenceMember_Throws(string wireFieldName)
    {
        var valid = new ReproducibilityEnvelopeEvent
        {
            RunId = "run-1",
            ScenarioId = "scenario-1",
            EnvSchemaVersion = "v1",
            SecretReferences = Array.Empty<SecretReferenceDigest>(),
            Fixtures = Array.Empty<FixtureDigest>(),
        };

        AssertNullRequiredReferenceMemberThrows(valid, wireFieldName, nameof(ReproducibilityEnvelopeEvent));
    }

    // =========================================================================
    // TransportNoticeEvent — required reference members: runId, kind, service,
    // selectedEndpoint
    // =========================================================================

    [Theory]
    [InlineData("runId")]
    [InlineData("kind")]
    [InlineData("service")]
    [InlineData("selectedEndpoint")]
    public void TransportNoticeEvent_NullRequiredReferenceMember_Throws(string wireFieldName)
    {
        var valid = new TransportNoticeEvent
        {
            RunId = "run-1",
            Kind = TransportNoticeKinds.NoEngineTrust,
            Service = "checkout-api",
            SelectedEndpoint = "https",
        };

        AssertNullRequiredReferenceMemberThrows(valid, wireFieldName, nameof(TransportNoticeEvent));
    }

    // =========================================================================
    // Cross-cutting: absent vs. null are distinct failure paths
    // =========================================================================

    [Fact]
    public void AbsentRequiredReferenceMember_ThrowsJsonException_NotInvalidOperationException()
    {
        // scenarioId is missing entirely (not present as a key at all), unlike the
        // null-field theories above where the key is present with a JSON null value.
        // STJ's own `required`-member enforcement throws JsonException for an absent
        // field before FromLine<T>'s null guard ever runs — the two paths stay distinct,
        // mirroring the untyped FromLine(string)'s documented behaviour.
        const string line = """
            {"v":1,"schemaVersion":"v1","type":"scenario-started","ts":"2026-01-01T00:00:00Z","runId":"run-1"}
            """;

        Assert.Throws<JsonException>(() => EventStreamJson.FromLine<ScenarioStartedEvent>(line));
    }

    // =========================================================================
    // Cross-cutting: an empty string is legal, not malformed
    // =========================================================================

    [Fact]
    public void EmptyStringRequiredReferenceMember_IsAccepted()
    {
        var evt = new ScenarioStartedEvent { RunId = "run-1", ScenarioId = string.Empty };

        var line = EventStreamJson.ToLine(evt);
        var restored = EventStreamJson.FromLine<ScenarioStartedEvent>(line);

        Assert.Equal(string.Empty, restored.ScenarioId);
    }

    // =========================================================================
    // Cross-cutting: a nullable OPTIONAL member accepts an explicit null
    // =========================================================================

    [Fact]
    public void NullableOptionalMember_File_SetToNull_IsAccepted()
    {
        var valid = new ScenarioStartedEvent
        {
            RunId = "run-1",
            ScenarioId = "scenario-1",
            File = "some.e2e.yaml",
        };

        var line = LineWithNullField(valid, "file");
        var restored = EventStreamJson.FromLine<ScenarioStartedEvent>(line);

        Assert.Null(restored.File);
    }

    [Fact]
    public void NullableOptionalMember_CorrelationIds_SetToNull_IsAccepted()
    {
        var valid = new StepStartedEvent { RunId = "run-1", StepId = "step-1" };

        // CorrelationIds is null by default and therefore omitted from the wire
        // (DefaultIgnoreCondition.WhenWritingNull) — forcing it onto the wire as an
        // explicit JSON null proves the NULLABLE path is accepted, not merely its absence.
        var line = LineWithNullField(valid, "correlationIds");
        var restored = EventStreamJson.FromLine<StepStartedEvent>(line);

        Assert.Null(restored.CorrelationIds);
    }

    // =========================================================================
    // Census: the reflection cache cannot silently drift from the golden's [required] set
    // =========================================================================

    /// <summary>
    /// For EVERY record declared in the frozen <c>Golden/event-stream-wire-contract.v1.txt</c>
    /// — not just the four typed records the engine calls <see cref="EventStreamJson.FromLine{T}"/>
    /// with today (<c>ScenarioStartedEvent</c> and <c>StepCompletedEvent</c> via
    /// <c>EventHistoryReader</c>; <c>ScenarioCompletedEvent</c> and <c>StepStartedEvent</c> via
    /// <c>TelemetryEventBuilder</c>) — <c>FromLine{T}</c>'s guard itself applies to any record,
    /// including the three currently unused ones and <c>EventEnvelope</c> — the set of wire
    /// names <see cref="EventStreamJson.GetRequiredReferenceMemberWireNamesForTests"/> reports
    /// must equal that record's <c>[required]</c> properties in the golden that are ALSO
    /// reference types (checked by reflecting the live property's
    /// <see cref="Type.IsValueType"/> — independent of the production cache under test). Each
    /// record's type is resolved BY NAME from the same assembly
    /// <c>EventContractFreezeTests.SnapshotSet_CoversEveryPublicEventRecord</c> reflects over,
    /// so a record renamed in one place but not the other fails loudly here rather than being
    /// silently skipped. A record with no <c>[required]</c> reference member (e.g.
    /// <c>VerdictCounts</c>, <c>CapturedVar</c>) compares an empty expected list to an empty
    /// actual list — the test is not vacuous for those, it pins that the cache reports NOTHING
    /// for them, which is itself part of the contract (a corrupt <c>counts</c> line's fields
    /// are never individually null-guarded, only the parent <c>ScenarioCompletedEvent.Counts</c>
    /// reference is). <c>EventEnvelope</c> is the one record where both listed members
    /// (<c>runId</c> AND <c>type</c>) are expected: of its own two required fields, only
    /// <c>runId</c> is also guarded by the typed overload on every payload record (each
    /// separately marks its own <c>RunId</c> <see langword="required"/>); <c>type</c> is
    /// required only here, and <c>EventEnvelope</c> is the one record that marks both
    /// <see langword="required"/>.
    /// </summary>
    /// <remarks>
    /// This does NOT mean a future required reference member is guaranteed to fail here until
    /// a theory row is added above: both sides of the comparison are derived automatically
    /// (golden regeneration here; reflection on the production side), so a genuinely NEW
    /// required reference member on an EXISTING record — added correctly and with the golden
    /// regenerated — makes this census pass, not fail; it is the FromLine{T} theories above,
    /// not this census, that a human must remember to extend for it. What this census actually
    /// catches is disagreement BETWEEN the golden and the live reflection cache — e.g. the
    /// production nullability-annotation filter mis-classifying a member the golden's
    /// `[required]` marker says is reference-typed, or a record renamed in one file but not
    /// the other. One BLIND SPOT is worth knowing: the golden's CLR-type column is written from
    /// <see cref="Type.FullName"/> via <c>EventContractFreezeTests.FormatType</c>, which cannot
    /// see a REFERENCE type's nullable-annotation (`string` and `string?` both print
    /// <c>System.String</c> — reflection has no metadata for reference-type nullability short of
    /// <see cref="System.Reflection.NullabilityInfoContext"/> itself). So if a future
    /// <c>required string?</c> member were ever added to one of these records, THIS census
    /// would compute it as expected (it looks reference-typed from the golden text) while
    /// production correctly excludes it (nullability annotation is <c>Nullable</c>-annotated
    /// reference, not <see cref="System.Reflection.NullabilityState.NotNull"/>) — a MISLEADING
    /// failure here that would need a human to recognise production is right and this census's
    /// golden-text-only view is what is blind, not the other way round.
    /// </remarks>
    [Fact]
    public void RequiredReferenceMemberCache_MatchesGolden_ForEveryRecordInTheGolden()
    {
        var golden = ReadGolden();
        var goldenRequiredProperties = ParseRequiredProperties(golden);
        var assembly = typeof(EventEnvelope).Assembly;

        Assert.NotEmpty(goldenRequiredProperties);

        foreach (var (recordName, requiredProps) in goldenRequiredProperties)
        {
            var type = assembly.GetTypes().SingleOrDefault(t => t.Name == recordName);
            Assert.True(
                type is not null,
                $"'{recordName}' is listed in the frozen wire-contract golden but no type "
                + $"named '{recordName}' was found in {assembly.GetName().Name} — the golden "
                + "and the assembly have drifted.");

            var expectedWireNames = requiredProps
                .Where(p => !type!.GetProperty(p.PropertyName)!.PropertyType.IsValueType)
                .Select(p => p.WireName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            var actualWireNames = EventStreamJson.GetRequiredReferenceMemberWireNamesForTests(type!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            Assert.True(
                expectedWireNames.SequenceEqual(actualWireNames, StringComparer.Ordinal),
                $"{recordName}: the frozen golden's [required] reference-type properties "
                + "["
                + string.Join(", ", expectedWireNames)
                + "] disagree with what EventStreamJson's reflection cache reports guarded "
                + "["
                + string.Join(", ", actualWireNames)
                + "]. Either the golden and the live type have drifted, or (see this test's "
                + "remarks) a required reference-typed-in-the-golden member is a nullable "
                + "reference annotation the golden's CLR-type column cannot show — in which "
                + "case production, not this census, is correct.");
        }
    }

    // =========================================================================
    // m2: the nullability-annotation branch — required-and-nullable vs required-and-not
    // =========================================================================

    /// <summary>
    /// A record local to this test file, not part of the wire contract: exercises the
    /// nullable-annotation branch directly (<c>Maybe</c> is <see langword="required"/> but
    /// nullable-annotated; <c>Must</c> is <see langword="required"/> and not). Production's
    /// null-guard cannot be exercised against this branch using any real event record, because
    /// none of them declares a nullable-annotated <see langword="required"/> member today.
    /// </summary>
    private sealed record NullableRequiredMemberProbe
    {
        public required string Must { get; init; }

        public required string? Maybe { get; init; }
    }

    [Fact]
    public void RequiredNullableAnnotatedMember_SetToNull_IsAccepted()
    {
        var line = """{"Must":"present","Maybe":null}""";

        var restored = EventStreamJson.FromLine<NullableRequiredMemberProbe>(line);

        Assert.Null(restored.Maybe);
    }

    [Fact]
    public void RequiredNonNullableAnnotatedMember_SetToNull_Throws()
    {
        var line = """{"Must":null,"Maybe":"present"}""";

        var ex = Assert.Throws<InvalidOperationException>(
            () => EventStreamJson.FromLine<NullableRequiredMemberProbe>(line));

        Assert.Equal(
            "Event-stream line has a null Must; the NullableRequiredMemberProbe field is required.",
            ex.Message);
    }

    // =========================================================================
    // LOW-1: a host with NullabilityInfoContext.IsSupported=false must over-guard, not
    // silently skip every line
    // =========================================================================

    private sealed record TrimmedHostProbe
    {
        public required string RunId { get; init; }
    }

    [Fact]
    public void NullabilityReadThatThrows_TreatsTheMemberAsGuarded()
    {
        // Simulates NullabilityInfoContext.Create throwing InvalidOperationException, exactly
        // as it does when a host has NullabilityInfoContext.IsSupported=false (the
        // NullabilityInfoContextSupport=false MSBuild property — NOT a default trimmed/AOT
        // publish, which fails earlier: PublishTrimmed/PublishAot default
        // JsonSerializerIsReflectionEnabledByDefault to false, so EventStreamJson's own static
        // initialiser throws TypeInitializationException before this guard ever runs). The
        // real AppContext.SetSwitch for that flag cannot be pinned by a test in
        // this shared process: the NullabilityInfoContext support switch is a static read
        // made once per process on the type's first touch, and — measured via a throwaway
        // console probe — an AppContext.SetSwitch call made AFTER that first touch (which
        // other tests in this same assembly have already caused) has no effect. The
        // internal seam below bypasses that entirely by injecting the throwing reader directly.
        static NullabilityState ThrowingReader(MemberInfo member) =>
            throw new InvalidOperationException(
                "NullabilityInfoContext is not supported in the current application because "
                + "'System.Reflection.NullabilityInfoContext.IsSupported' is set to false.");

        var wireNames = EventStreamJson.ComputeRequiredReferenceMemberWireNamesForTests(
            typeof(TrimmedHostProbe), ThrowingReader);

        // RunId is `required` and a reference type; with the nullability read throwing, it
        // must still be reported as guarded (over-guarded, not silently dropped).
        Assert.Contains("RunId", wireNames);
    }

    // =========================================================================
    // LOW-2: a type from a collectible AssemblyLoadContext is never cached
    // =========================================================================

    [Fact]
    public void CollectibleType_IsNeverCached_UnlikeAnOrdinaryRecord()
    {
        // An ordinary in-tree record gets cached on first use.
        _ = EventStreamJson.GetRequiredReferenceMemberWireNamesForTests(typeof(ScenarioStartedEvent));
        Assert.True(EventStreamJson.IsCachedForTests(typeof(ScenarioStartedEvent)));

        // A type from a collectible dynamic assembly (AssemblyBuilderAccess.RunAndCollect;
        // Type.IsCollectible is true for it, confirmed via a throwaway probe) must not be.
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("EventStreamJsonCollectibleProbe"), AssemblyBuilderAccess.RunAndCollect);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule("Main");
        var typeBuilder = moduleBuilder.DefineType("CollectibleProbe", TypeAttributes.Public);
        var collectibleType = typeBuilder.CreateType();

        Assert.True(collectibleType.IsCollectible);

        _ = EventStreamJson.GetRequiredReferenceMemberWireNamesForTests(collectibleType);

        Assert.False(EventStreamJson.IsCachedForTests(collectibleType));
    }

    // =========================================================================
    // INFO-3: a required member whose getter throws surfaces InvalidOperationException,
    // not TargetInvocationException
    // =========================================================================

    private sealed record ThrowingGetterProbe
    {
        [System.Text.Json.Serialization.JsonPropertyName("boom")]
        public required string Boom
        {
            get => throw new FormatException($"getter deliberately throws ({GetType().Name})");
            init { }
        }
    }

    [Fact]
    public void RequiredMemberWithThrowingGetter_ThrowsInvalidOperationException_NamingWireName()
    {
        var line = """{"boom":"anything"}""";

        var ex = Assert.Throws<InvalidOperationException>(
            () => EventStreamJson.FromLine<ThrowingGetterProbe>(line));

        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
        // The getter's own text travels only as the inner exception: a guard message is
        // fixed text plus the wire and record names, never a value the line could carry.
        Assert.DoesNotContain("getter deliberately throws", ex.Message, StringComparison.Ordinal);
        var inner = Assert.IsType<FormatException>(ex.InnerException);
        Assert.Contains("getter deliberately throws", inner.Message, StringComparison.Ordinal);
    }

    // =========================================================================
    // PR #575 review (Copilot): field path
    // =========================================================================

    private sealed record RequiredFieldProbe
    {
        public required string RunId { get; init; }

        /// <summary>
        /// A required, non-nullable, [JsonInclude] field with no explicit
        /// [JsonPropertyName] — its wire name falls back to the declared member name,
        /// "Must" (EventStreamJson.Options applies no naming policy).
        /// </summary>
        [System.Text.Json.Serialization.JsonInclude]
        public required string Must = null!;

        /// <summary>
        /// A required, NULLABLE-annotated, [JsonInclude] field — the guard must exclude
        /// it, exactly as for the property case.
        /// </summary>
        [System.Text.Json.Serialization.JsonInclude]
        public required string? Maybe = null;

        /// <summary>
        /// A required, non-nullable, [JsonInclude] field with an explicit
        /// [JsonPropertyName] rename — pins that the guard reads the attribute, not the
        /// declared field name, for the wire name.
        /// </summary>
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonPropertyName("renamedField")]
        public required string Renamed = null!;

        /// <summary>
        /// A required, non-nullable field WITHOUT [JsonInclude]. With no IncludeFields
        /// opt-in on EventStreamJson.Options, System.Text.Json never maps this field to or
        /// from the wire at all — it must therefore be entirely absent from the guard's
        /// cached member list, never checked, never enforced as present.
        /// </summary>
        public required string Unmapped = null!;
    }

    private static RequiredFieldProbe ValidRequiredFieldProbe() => new()
    {
        RunId = "run-1",
        Must = "must",
        Maybe = "maybe",
        Renamed = "renamed",
        Unmapped = "unmapped",
    };

    [Fact]
    public void Field_RequiredNonNullableJsonIncludeField_Null_ThrowsNamingWireName()
    {
        AssertNullRequiredReferenceMemberThrows(
            ValidRequiredFieldProbe(), "Must", nameof(RequiredFieldProbe));
    }

    [Fact]
    public void Field_RequiredNullableAnnotatedJsonIncludeField_Null_IsAccepted()
    {
        var line = LineWithNullField(ValidRequiredFieldProbe(), "Maybe");

        var restored = EventStreamJson.FromLine<RequiredFieldProbe>(line);

        Assert.Null(restored.Maybe);
    }

    [Fact]
    public void Field_RenamedRequiredJsonIncludeField_Null_ThrowsNamingRenamedWireName()
    {
        AssertNullRequiredReferenceMemberThrows(
            ValidRequiredFieldProbe(), "renamedField", nameof(RequiredFieldProbe));
    }

    [Fact]
    public void Field_RequiredJsonIncludeField_Absent_ThrowsJsonException()
    {
        // "Must" omitted entirely — measured: STJ's `required`-member presence
        // enforcement applies uniformly to a [JsonInclude] field, exactly as it does to
        // a property, so an absent required field fails the same way (JsonException),
        // never reaching the null-value guard.
        const string line = """{"RunId":"r-1","Maybe":"m","renamedField":"r"}""";

        Assert.Throws<JsonException>(() => EventStreamJson.FromLine<RequiredFieldProbe>(line));
    }

    [Fact]
    public void Field_RequiredFieldWithoutJsonInclude_IsNeverInTheCachedMemberList()
    {
        // "Unmapped" is `required` (it carries RequiredMemberAttribute) but STJ never
        // binds it because it has no [JsonInclude] and EventStreamJson.Options sets no
        // IncludeFields — the guard must never enumerate it.
        var wireNames = EventStreamJson.GetRequiredReferenceMemberWireNamesForTests(
            typeof(RequiredFieldProbe));

        Assert.DoesNotContain("Unmapped", wireNames);
        Assert.Contains("Must", wireNames);
        Assert.Contains("renamedField", wireNames);
        Assert.DoesNotContain("Maybe", wireNames);
        Assert.DoesNotContain("Renamed", wireNames);
    }

    // =========================================================================
    // MINOR-1: a write-only required property under a throwing nullability read must
    // never be listed — it cannot be read to null-check it
    // =========================================================================

    private sealed record WriteOnlyRequiredMemberProbe
    {
        private string? _x;

        public required string X { init => _x = value; }
    }

    [Fact]
    public void NullabilityReadThatThrows_WriteOnlyRequiredMember_IsNeverListed()
    {
        static NullabilityState ThrowingReader(MemberInfo member) =>
            throw new InvalidOperationException(
                "NullabilityInfoContext is not supported in the current application because "
                + "'System.Reflection.NullabilityInfoContext.IsSupported' is set to false.");

        var wireNames = EventStreamJson.ComputeRequiredReferenceMemberWireNamesForTests(
            typeof(WriteOnlyRequiredMemberProbe), ThrowingReader);

        Assert.DoesNotContain("X", wireNames);
    }

    // =========================================================================
    // MINOR-2: a genuinely collectible ordinary type (not a Reflection.Emit stub with no
    // properties) is never cached, and the guard still fires through it
    // =========================================================================

    [Fact]
    public void CollectibleType_WithRealRequiredMember_IsNeverCached_AndGuardStillFires()
    {
        var alc = new AssemblyLoadContext(
            "EventStreamJsonCollectibleRealTypeProbe", isCollectible: true);

        var loadedAssembly = alc.LoadFromAssemblyPath(
            typeof(NullableRequiredMemberProbe).Assembly.Location);
        var loadedType = loadedAssembly.GetType(typeof(NullableRequiredMemberProbe).FullName!)!;

        Assert.True(loadedType.IsCollectible);

        var wireNames = EventStreamJson.GetRequiredReferenceMemberWireNamesForTests(loadedType);
        var expectedWireNames = new List<string> { "Must" };
        Assert.Equal(expectedWireNames, wireNames);
        Assert.False(EventStreamJson.IsCachedForTests(loadedType));

        var fromLineParameterTypes = new[] { typeof(string) };
        var fromLineMethod = typeof(EventStreamJson)
            .GetMethod(nameof(EventStreamJson.FromLine), 1, fromLineParameterTypes)!
            .MakeGenericMethod(loadedType);

        const string line = """{"Must":null,"Maybe":"present"}""";

        var invokeArgs = new object[] { line };
        var ex = Assert.Throws<TargetInvocationException>(
            () => fromLineMethod.Invoke(null, invokeArgs));

        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal(
            "Event-stream line has a null Must; the NullableRequiredMemberProbe field is required.",
            inner.Message);

        // The reflective FromLine<T> call above must not have pinned the collectible type
        // either — repeat the assertion post-call, not just post-GetRequiredReferenceMembers.
        Assert.False(EventStreamJson.IsCachedForTests(loadedType));

        // Deliberately no Unload()/collection assertion (see FromLine{T}'s remarks: a
        // collectible T reaching this method is already pinned by the shared STJ
        // serialiser's own reflection cache, so unloadability is not this test's claim).
    }

    // =========================================================================
    // PR #575 review (code-review-gatekeeper, MAJOR): STJ maps a NON-public member carrying [JsonInclude]
    // (property or field, any visibility, declared or inherited) and presence-checks it when
    // `required`; the hand-rolled GetProperties(Public)/GetFields(Public) scan this file used
    // to exercise could never see one. The production fix replaces that scan with STJ's own
    // JsonTypeInfo.Properties contract (Options.GetTypeInfo(type)), which starts from the
    // members STJ itself maps. Measured (pre-fix, via a throwaway probe reproducing this
    // shape): `internal sealed record P { [JsonInclude] internal required string X { get; init; } }`
    // deserialises `{"X":null}` with `X == null` and passed the old guard silently, while an
    // absent `X` still threw STJ's own "missing required properties" JsonException.
    //
    // A base-class PRIVATE `[JsonInclude] private required` member (case 2 of the finding) does
    // not compile: `private required string X { get; init; }` on any type, base or not, fails
    // CS9032 ("Required member 'X' cannot be less visible ... than the containing type") the
    // moment the member's own accessibility is narrower than its DECLARING type's — regardless
    // of inheritance. Measured via a throwaway scratch build:
    //   error CS9032: Required member 'Base.X' cannot be less visible or have a setter less
    //   visible than the containing type 'Base'.
    // The only way to satisfy a base class + narrower-than-public visibility together is to make
    // the DECLARING type itself no more visible than the member — which is exactly the
    // `InternalRequiredMemberProbe` shape below (a `private` nested record whose members are
    // `internal`, i.e. no LESS visible than a `private` container). That shape composes with
    // inheritance: a `private` NON-SEALED base record declaring the member and a `private
    // sealed` derived record with no members of its own both compile, exercised later in this
    // file as InternalBaseWithInheritedMember / InternalDerivedWithInheritedMember. Only a
    // member LESS visible than its OWN declaring type hits CS9032 — inheritance itself is not
    // the obstacle.
    // =========================================================================

    private sealed record InternalRequiredMemberProbe
    {
        public required string RunId { get; init; }

        /// <summary>
        /// A required, non-nullable, INTERNAL (non-public) [JsonInclude] property — the shape
        /// the old GetProperties(BindingFlags.Public | BindingFlags.Instance) scan could never
        /// see, so a null "Prop" silently satisfied `required` before the fix.
        /// </summary>
        [System.Text.Json.Serialization.JsonInclude]
        internal required string Prop { get; init; }

        /// <summary>
        /// A required, non-nullable, INTERNAL (non-public) [JsonInclude] field — the same gap
        /// for the old GetFields(BindingFlags.Public | BindingFlags.Instance) scan.
        /// </summary>
        [System.Text.Json.Serialization.JsonInclude]
        internal required string Fld = null!;
    }

    private static InternalRequiredMemberProbe ValidInternalRequiredMemberProbe() => new()
    {
        RunId = "run-1",
        Prop = "prop",
        Fld = "fld",
    };

    [Theory]
    [InlineData("Prop")]
    [InlineData("Fld")]
    public void InternalJsonIncludeMember_Null_ThrowsNamingWireName(string wireFieldName)
    {
        AssertNullRequiredReferenceMemberThrows(
            ValidInternalRequiredMemberProbe(), wireFieldName, nameof(InternalRequiredMemberProbe));
    }

    [Fact]
    public void InternalJsonIncludeMember_Prop_Absent_ThrowsJsonException()
    {
        // "Prop" omitted entirely — STJ's own `required`-member presence enforcement applies
        // uniformly regardless of member visibility, so this still fails before the null guard
        // ever runs, exactly like the public-member absent case above.
        const string line = """{"RunId":"run-1","Fld":"fld"}""";

        Assert.Throws<JsonException>(() => EventStreamJson.FromLine<InternalRequiredMemberProbe>(line));
    }

    [Fact]
    public void InternalJsonIncludeMembers_AreInTheCachedMemberList()
    {
        var wireNames = EventStreamJson.GetRequiredReferenceMemberWireNamesForTests(
            typeof(InternalRequiredMemberProbe));

        Assert.Contains("Prop", wireNames);
        Assert.Contains("Fld", wireNames);
    }

    private sealed record RenamedInternalRequiredMemberProbe
    {
        public required string RunId { get; init; }

        /// <summary>
        /// An INTERNAL (non-public) required [JsonInclude] property carrying an explicit
        /// [JsonPropertyName] rename — pins that the STJ-driven scan reports STJ's own wire
        /// name (the JsonTypeInfo.Properties entry's Name), not the declared member name, for
        /// a non-public member too.
        /// </summary>
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonPropertyName("renamed")]
        internal required string Prop { get; init; }
    }

    [Fact]
    public void RenamedInternalJsonIncludeMember_Null_ThrowsNamingRenamedWireName()
    {
        var valid = new RenamedInternalRequiredMemberProbe { RunId = "run-1", Prop = "prop" };

        AssertNullRequiredReferenceMemberThrows(
            valid, "renamed", nameof(RenamedInternalRequiredMemberProbe));
    }

    // =========================================================================
    // security LOW-2a: a member mapped through a [SetsRequiredMembers] constructor still
    // reports IsRequired == false from STJ, so the guard must test the MemberInfo's own
    // RequiredMemberAttribute directly rather than trust JsonPropertyInfo.IsRequired alone.
    // =========================================================================

    private sealed record SetsRequiredMembersProbe
    {
        [SetsRequiredMembers]
        public SetsRequiredMembersProbe()
        {
            RunId = string.Empty;
            Name = string.Empty;
        }

        public required string RunId { get; init; }

        public required string Name { get; init; }
    }

    [Fact]
    public void SetsRequiredMembersConstructor_NullMember_Throws()
    {
        const string line = """{"RunId":"r","Name":null}""";

        var ex = Assert.Throws<InvalidOperationException>(
            () => EventStreamJson.FromLine<SetsRequiredMembersProbe>(line));

        Assert.Equal(
            "Event-stream line has a null Name; the SetsRequiredMembersProbe field is required.",
            ex.Message);
    }

    [Fact]
    public void SetsRequiredMembersConstructor_AbsentMember_IsAccepted_UsingConstructorValue()
    {
        // STJ does not enforce presence for a member set by a [SetsRequiredMembers]
        // constructor, so an absent "Name" is legal wire content — unlike the ordinary
        // absent-required-member case elsewhere in this file, which throws JsonException.
        // The constructor's own value ("") must survive untouched.
        const string line = """{"RunId":"r"}""";

        var restored = EventStreamJson.FromLine<SetsRequiredMembersProbe>(line);

        Assert.Equal(string.Empty, restored.Name);
    }

    // =========================================================================
    // m2 (code-review-gatekeeper, measured): a [JsonIgnore] member still appears in
    // JsonTypeInfo.Properties with both Get and Set null, so it is never bound to or from the
    // wire at all. Before the fix, the guard's RequiredMemberAttribute fallback still admitted
    // it (the C# `required` modifier is on the member, not on the JSON mapping) despite
    // IsRequired == false, and PropertyInfo.GetValue then read whatever the backing field held
    // — null, since the [SetsRequiredMembers] constructor never assigns X — refusing EVERY line
    // for a member the wire never carries. The fix skips a member whose JsonPropertyInfo.Set is
    // null before that fallback test runs.
    // =========================================================================

    private sealed record JsonIgnoredSetsRequiredMembersProbe
    {
        [SetsRequiredMembers]
        public JsonIgnoredSetsRequiredMembersProbe()
        {
            RunId = string.Empty;
        }

        public required string RunId { get; init; }

        [JsonIgnore]
        public required string X { get; init; } = null!;
    }

    [Fact]
    public void JsonIgnoredMember_UnderSetsRequiredMembers_LeftNullByConstructor_LineIsAccepted()
    {
        // Pre-fix, this threw InvalidOperationException: "Event-stream line has a null X; the
        // JsonIgnoredSetsRequiredMembersProbe field is required." — measured by running this
        // test against the pre-m2 code (no p.Set null check).
        const string line = """{"RunId":"r"}""";

        var restored = EventStreamJson.FromLine<JsonIgnoredSetsRequiredMembersProbe>(line);

        Assert.Equal("r", restored.RunId);
        Assert.Null(restored.X);
    }

    [Fact]
    public void JsonIgnoredMember_UnderSetsRequiredMembers_IsNeverInTheCachedMemberList()
    {
        var wireNames = EventStreamJson.GetRequiredReferenceMemberWireNamesForTests(
            typeof(JsonIgnoredSetsRequiredMembersProbe));

        Assert.DoesNotContain("X", wireNames);
        Assert.Contains("RunId", wireNames);
    }

    // =========================================================================
    // gate m3: a polymorphic T's contract is the DERIVED type's, not typeof(T)'s — a required
    // member declared only on the derived type must still be guarded.
    // =========================================================================

    [JsonPolymorphic]
    [JsonDerivedType(typeof(PolyDerived), "d")]
    private abstract record PolyBase
    {
        public required string RunId { get; init; }
    }

    private sealed record PolyDerived : PolyBase
    {
        public required string D { get; init; }
    }

    [Fact]
    public void PolymorphicPayload_NullDerivedOnlyRequiredMember_Throws_NamingDerivedType()
    {
        const string line = """{"$type":"d","RunId":"r","D":null}""";

        var ex = Assert.Throws<InvalidOperationException>(
            () => EventStreamJson.FromLine<PolyBase>(line));

        Assert.Equal(
            "Event-stream line has a null D; the PolyDerived field is required.",
            ex.Message);
    }

    // =========================================================================
    // gate M1: [JsonRequired] alone (no C# `required` modifier) is already guarded via
    // JsonPropertyInfo.IsRequired — pinned here, not a new code path.
    // =========================================================================

    private sealed record JsonRequiredAttributeProbe
    {
        [JsonRequired]
        public string Name { get; init; } = string.Empty;
    }

    private sealed record JsonRequiredAttributeNullableProbe
    {
        [JsonRequired]
        public string? Name { get; init; }
    }

    [Fact]
    public void JsonRequiredAttribute_NonNullableMember_Null_Throws()
    {
        const string line = """{"Name":null}""";

        var ex = Assert.Throws<InvalidOperationException>(
            () => EventStreamJson.FromLine<JsonRequiredAttributeProbe>(line));

        Assert.Equal(
            "Event-stream line has a null Name; the JsonRequiredAttributeProbe field is required.",
            ex.Message);
    }

    [Fact]
    public void JsonRequiredAttribute_NullableMember_Null_IsAccepted()
    {
        const string line = """{"Name":null}""";

        var restored = EventStreamJson.FromLine<JsonRequiredAttributeNullableProbe>(line);

        Assert.Null(restored.Name);
    }

    // =========================================================================
    // security LOW-2b: a type-level custom JsonConverter exposes no properties to STJ's
    // contract at all (JsonTypeInfo.Kind == None) — the converter owns null handling, and
    // there is nothing for this guard to scan.
    // =========================================================================

    private sealed class TypeLevelConverterProbeConverter : JsonConverter<TypeLevelConverterProbe>
    {
        public override TypeLevelConverterProbe Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString() ?? string.Empty);

        public override void Write(
            Utf8JsonWriter writer, TypeLevelConverterProbe value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    [JsonConverter(typeof(TypeLevelConverterProbeConverter))]
    private sealed record TypeLevelConverterProbe(string Value);

    [Fact]
    public void TypeLevelJsonConverter_ExposesNoMembersToTheGuard()
    {
        var wireNames = EventStreamJson.GetRequiredReferenceMemberWireNamesForTests(
            typeof(TypeLevelConverterProbe));

        Assert.Empty(wireNames);
    }

    // =========================================================================
    // m1 (code-review-gatekeeper, measured): a type-level JsonConverter on T whose Read
    // legitimately constructs and returns an instance of a DIFFERENT type than T. Keyed on
    // payload.GetType() unconditionally, the guard would read the RETURNED type's OWN default
    // reflection contract (ConvDerived has no converter of its own, so it gets a plain
    // property-based contract with `X` required) and refuse a null the converter itself
    // already accepted. The guard reads the runtime type only when STJ's OWN polymorphism
    // resolution (JsonTypeInfo.PolymorphismOptions) selected it for T; ConvBase carries a plain
    // converter and no polymorphism configuration, so the contract stays ConvBase's own — which the
    // converter attribute empties of properties (JsonTypeInfo.Kind == None), exactly like
    // TypeLevelConverterProbe above — and the guard never sees ConvDerived's `X` at all.
    // =========================================================================

    private sealed class ConvBaseConverter : JsonConverter<ConvBase>
    {
        public override ConvBase Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var x = doc.RootElement.TryGetProperty("X", out var xProperty)
                && xProperty.ValueKind != JsonValueKind.Null
                    ? xProperty.GetString()
                    : null;
            return new ConvDerived { X = x! };
        }

        public override void Write(
            Utf8JsonWriter writer, ConvBase value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("X", ((ConvDerived)value).X);
            writer.WriteEndObject();
        }
    }

    [JsonConverter(typeof(ConvBaseConverter))]
    private record ConvBase;

    private sealed record ConvDerived : ConvBase
    {
        public required string X { get; init; }
    }

    [Fact]
    public void TypeLevelJsonConverter_ReturningADifferentType_NullOnTheReturnedType_IsAccepted()
    {
        // Pins that the converter-returned type's contract is not consulted: keyed on
        // payload.GetType() unconditionally, this line is refused with "Event-stream line has a
        // null X; the ConvDerived field is required." (measured).
        const string line = """{"X":null}""";

        var restored = (ConvDerived)EventStreamJson.FromLine<ConvBase>(line);

        Assert.Null(restored.X);
    }

    // =========================================================================
    // gate m1: an INHERITED non-public required member — declared on a non-sealed base record,
    // read through a sealed derived record with no members of its own. This shape compiles:
    // CS9032 only fires when a member is less visible than its OWN declaring type, and
    // `internal` on a `private` base is not less visible.
    // =========================================================================

    private record InternalBaseWithInheritedMember
    {
        public required string RunId { get; init; }

        [System.Text.Json.Serialization.JsonInclude]
        internal required string X { get; init; }
    }

    private sealed record InternalDerivedWithInheritedMember : InternalBaseWithInheritedMember;

    private static InternalDerivedWithInheritedMember ValidInternalDerivedWithInheritedMember() => new()
    {
        RunId = "run-1",
        X = "x",
    };

    [Fact]
    public void InheritedInternalJsonIncludeMember_Null_ThrowsNamingDerivedType()
    {
        AssertNullRequiredReferenceMemberThrows(
            ValidInternalDerivedWithInheritedMember(), "X", nameof(InternalDerivedWithInheritedMember));
    }

    private sealed record GoldenRequiredProperty(string PropertyName, string WireName);

    /// <summary>
    /// Parses <c>record &lt;Name&gt;</c> / <c>property ... [required] ...</c> lines out of
    /// the frozen golden text, matching <c>EventContractFreezeTests.FormatProperty</c>'s
    /// output shape: <c>property {CLR-type} {PropertyName} [wire={json}] [required] [init|get-only|set]</c>.
    /// </summary>
    private static Dictionary<string, List<GoldenRequiredProperty>> ParseRequiredProperties(string golden)
    {
        var byRecord = new Dictionary<string, List<GoldenRequiredProperty>>(StringComparer.Ordinal);
        string? currentRecord = null;

        foreach (var rawLine in golden.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("record ", StringComparison.Ordinal))
            {
                currentRecord = line["record ".Length..].Trim();
                byRecord[currentRecord] = new List<GoldenRequiredProperty>();
                continue;
            }

            if (currentRecord is null || !line.TrimStart().StartsWith("property ", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.Contains("[required]", StringComparison.Ordinal))
            {
                continue;
            }

            // "property {type} {name} [wire={json}] [required] [init]" — split on spaces;
            // token[0] = "property", token[1] = CLR type, token[2] = property name.
            var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var propertyName = tokens[2];

            var wireToken = tokens.FirstOrDefault(t => t.StartsWith("[wire=", StringComparison.Ordinal));
            var wireName = wireToken is null
                ? propertyName
                : wireToken["[wire=".Length..^1];

            byRecord[currentRecord].Add(new GoldenRequiredProperty(propertyName, wireName));
        }

        return byRecord;
    }

    private static string ReadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", "event-stream-wire-contract.v1.txt");

        Assert.True(
            File.Exists(path),
            $"Golden v1 event-wire contract not found at '{path}'. This census reuses the "
            + "same committed golden EventContractFreezeTests gates.");

        return File.ReadAllText(path);
    }
}
