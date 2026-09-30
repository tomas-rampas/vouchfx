// S08-F-02 (T3 — Freeze the v1 event wire contract).
//
// The public event records under Vouchfx.Engine.Abstractions.Events ARE the v1
// JSON-Lines wire contract (§14): one schema-versioned event stream feeds every
// renderer (terminal, HTML, JUnit XML, dashboard) and the Healer.  Any change to a
// property name, type, or [JsonPropertyName] wire name changes what every consumer
// parses.  This golden-snapshot test freezes that surface: it reflects over the
// snapshotted event records and asserts the canonical signature is byte-for-byte
// (newline-normalised) identical to Golden/event-stream-wire-contract.v1.txt.
//
// Snapshotted records (the §14.4 event payloads + envelope + the NESTED value
// records reachable from them on the wire):
//   EventEnvelope, ScenarioStartedEvent, StepStartedEvent, StepAttemptEvent,
//   StepCompletedEvent, ScenarioCompletedEvent, ReproducibilityEnvelopeEvent,
//   TransportNoticeEvent, VerdictCounts,
//   CapturedVar, SubstitutionRef          (nested in StepCompletedEvent),
//   SecretReferenceDigest, FixtureDigest   (nested in the reproducibility envelope;
//                                           these two live in a DIFFERENT namespace —
//                                           Vouchfx.Engine.Abstractions.Reproducibility
//                                           — but ARE part of the envelope wire contract,
//                                           so they are listed explicitly below).
// The set is an EXPLICIT, hand-maintained list (not a namespace scan) so that
// ADDING a record to the namespace is itself a deliberate act: the new record must
// be added to this list AND to the golden, both reviewed.  A separate completeness
// guard (SnapshotSet_CoversEveryPublicEventRecord) reflects over the Events namespace
// and fails if a new public Events record is NOT added here, so the surface cannot
// silently grow unfrozen.
//
// WHY freeze the NESTED records' FIELDS, not just their names: the top-level events
// reference these value records only by TYPE NAME (e.g. IReadOnlyList<CapturedVar>).
// Without snapshotting the nested records themselves, renaming CapturedVar.Matched to
// .Hit — or changing its [JsonPropertyName] — would pass the gate while silently
// breaking the JSON-Lines wire contract every renderer parses.
//
// Canonicalization / inclusion rule (mirror this when regenerating the golden):
//   • Records are emitted in declared list order (the order below), each as a
//     "record <Name>" header followed by its public instance properties.
//   • Properties: public instance properties DECLARED on the record, sorted
//     ordinally by property NAME so reflection order never matters.  Each line is:
//       "property <CLR-type> <PropertyName> [wire=<json-name>] [required] [init|get-only]"
//     where <json-name> is the [JsonPropertyName] value (the WIRE contract) and the
//     CLR type is rendered by a deterministic formatter (generics as Name<Arg>,
//     nullable reference / value types preserved).  The synthesised record
//     value-equality surface (Equals/GetHashCode/ToString/Deconstruct/Clone/
//     EqualityContract/op_*) is not emitted — it adds no wire information.
//
// DELIBERATE DECISION ENCODED HERE (T1):
//   The step events — StepStartedEvent / StepAttemptEvent / StepCompletedEvent —
//   carry RunId + StepId but DO NOT carry ScenarioId.  The renderer disambiguates
//   aggregated streams by the (runId, stepId) pair because each scenario has a
//   distinct runId.  The golden encodes this absence, so re-adding ScenarioId to a
//   step event later is a CONSCIOUS, REVIEWED change (it will fail this gate).  A
//   dedicated assertion below also pins the decision independently of the golden.
//
// CENSUS (#578) — what the golden CANNOT show: the golden above renders each
// frozen record's PUBLIC INSTANCE PROPERTIES only (Type.GetProperties(Public |
// Instance)). System.Text.Json maps more than that: a public field carrying
// [JsonInclude], and a non-public property or field carrying [JsonInclude], are
// both wire members to STJ but invisible to the property-only scan above — such a
// member could be added to a frozen record and change the wire shape without
// moving the golden, leaving this gate green.
// EventWireContract_Census_MatchesStjMappedMembers (below) closes that gap:
// for every record in s_eventRecords it computes the STJ-mapped member set
// from EventStreamJson.Options.GetTypeInfo(type).Properties (the same
// contract FromLine<T> itself already reflects over) and asserts it is
// IDENTICAL — by (wire name, CLR type, required, extension-data, and — #586 —
// converter, number handling, property order, object-creation handling and
// conditional-ignore; see the MEMBER-LEVEL REPRESENTATION CENSUS note below) — to
// the rendered set from the same public-property enumeration the golden uses.
// A [JsonInclude] field or non-public [JsonInclude] member therefore fails
// THIS gate with the member named, even though it cannot appear in the
// golden text itself. STJ lists an unconditional [JsonIgnore] member with neither
// getter nor setter; the census drops those from the mapped set, so [JsonIgnore]
// on a rendered property is reported as rendered-but-unmapped (a conditional
// ignore keeps both accessors and stays a wire member; see #586). A NEW
// unconditional [JsonIgnore] public property on a frozen record therefore fails
// this census permanently, by design: frozen records carry wire members only —
// put helpers in extension methods, not on the record.
//
// MEMBER-LEVEL REPRESENTATION CENSUS (#586) — the #578 census closed the "member
// present or not" gap but did NOT pin HOW a mapped member is represented: a
// property-level [JsonConverter], [JsonNumberHandling], [JsonPropertyOrder] (an
// EXPLICIT [JsonPropertyOrder] attribute is pinned because its presence is itself a
// deliberate, reviewable signal that a member's wire position was overridden — NOT
// because this gate pins wire member order in general (#586): the golden's OWN
// property list is sorted ALPHABETICALLY by CLR name (BuildSignature/FormatProperty),
// so a reordered DECLARATION, or a reordered set of members inherited from a base
// record, changes nothing this gate can see; #600 tracks a proposed byte-level golden
// that WOULD see it. [JsonPropertyOrder]'s byte-visibility is NOT because of the
// telemetry backend's parity fixtures — measured: those fixtures pin
// Vouchfx.Engine.Telemetry.TelemetryEvent, an unrelated type this gate never
// freezes), [JsonObjectCreationHandling], or a CONDITIONAL [JsonIgnore]
// (Condition = WhenWritingDefault / WhenWritingNull / Never — as opposed to the
// unconditional Condition = Always the #578 census already drops from the mapped
// set) on a frozen member changes the wire while moving neither the golden text nor
// the #578 census. MemberSignature (below) now carries more fields —
// ConverterTypeName, NumberHandling, Order, ObjectCreationHandling,
// ConditionalIgnore — each populated two ways and asserted equal:
//   • RENDERED side (GetDeclaredJsonConverterType / GetJsonNumberHandling /
//     GetJsonPropertyOrder / GetJsonObjectCreationHandling / GetJsonIgnoreCondition):
//     reads the .NET attribute directly off the PropertyInfo, mirroring what
//     FormatProperty renders into the golden (shared helpers — the golden and the
//     rendered-signature side cannot independently drift on what counts as present).
//     The converter is the one field whose golden text and census value differ by design:
//     both read the same declared attribute, but the golden shows the declared type while
//     the census resolves it the way System.Text.Json does (see the CONVERTER NAMES note).
//   • STJ side (GetStjMappedMemberSignatures, extended): reads the SAME fact off
//     the shared EventStreamJson.Options' own JsonPropertyInfo — p.CustomConverter,
//     p.NumberHandling, p.Order, p.ObjectCreationHandling, p.ShouldSerialize.
// [JsonRequired] needs NO new field: it was ALREADY caught by the existing Required
// comparison the day #578 shipped it — GetJsonPropertyName/IsRequiredMember's
// "Required" only tests RequiredMemberAttribute (the C# `required` keyword), while
// STJ's p.IsRequired is true for EITHER `required` or a bare [JsonRequired]; a member
// carrying only [JsonRequired] therefore already renders unrequired but census-maps
// required, a signature mismatch the existing intersection loop already flags.
// (Proven by CensusProbeRecord.Tightened, pre-dating this task.)
//
// MEASURED (STJ 8.0.0.0, the in-box net8.0 reflection resolver this project actually
// runs against — see EventStreamJsonOptions_RunsAgainstMeasuredSystemTextJsonVersion;
// neither this test project nor Vouchfx.Engine.Abstractions references a System.Text.Json
// package, so no Directory.Packages.props pin applies to either):
//   • p.CustomConverter is populated ONLY by a member-level [JsonConverter]; a
//     converter reached via the GLOBALLY REGISTERED EventStreamJson.Options.Converters
//     list (as VerdictJsonConverter is, for the Verdict-typed members below) leaves
//     p.CustomConverter null. So today's Verdict/Verdict? members correctly compare
//     ConverterTypeName=null on both sides — global registration is invisible to, and
//     therefore cannot be confused with, this per-member pin.
//   • p.ShouldSerialize is POPULATED ONLY by a member's OWN [JsonIgnore(Condition=…)]
//     attribute (WhenWritingDefault, WhenWritingNull, or Never) — NOT by the shared
//     Options' global DefaultIgnoreCondition = WhenWritingNull. Measured directly: a
//     nullable member with no per-member attribute, under a global
//     DefaultIgnoreCondition = WhenWritingNull, reports ShouldSerialize = null; only a
//     member carrying its own [JsonIgnore(Condition=…)] — even one that merely repeats
//     the global setting — reports a non-null ShouldSerialize. This is what makes
//     ConditionalIgnore a safe per-member signal: it does not fire for the (many)
//     ordinary nullable members that rely solely on the shared global default.
//   • Condition = Always is excluded from ConditionalIgnore on the rendered side (and
//     never reaches the STJ side at all: an Always-ignored member has both p.Get and
//     p.Set null, so the pre-existing #578 "no accessors" skip drops it from the STJ
//     map before any new field is compared) — it stays covered exactly as #578 left
//     it, via the rendered-but-unmapped path in DescribeMemberSetDifferences.
//   • [JsonObjectCreationHandling(Populate)] on a member throws NotSupportedException
//     from JsonTypeInfo.Configure when the declaring type binds through a parameterized
//     constructor (a positional record — CapturedVar, SubstitutionRef,
//     SecretReferenceDigest, FixtureDigest); it is supported on a property-syntax
//     record (every other frozen record). This is a real STJ 8 constraint on a future
//     member-level Populate attribute on one of the positional records above, not a gap
//     in this gate — the golden+census comparison for such a member would never get
//     as far as being compared, because the process fails to construct the JsonTypeInfo
//     at all (documented; CensusProbeRecord.Populated below deliberately uses a
//     property-syntax host — CensusProbeRecord itself — to stay constructible).
//   • [JsonConstructor] and [JsonInclude] are member-affecting but out of THIS census's
//     new scope: [JsonInclude] is already the #578 census's reason for existing (a
//     mapped-but-unrendered member); [JsonConstructor] selects a BINDING constructor —
//     it changes how a member is READ during deserialisation (interacting with the
//     required-reference-member guard in EventStreamJson.FromLine{T}), not what
//     JsonPropertyInfo reports for that member's wire shape, so it is not a
//     representation-affecting attribute in the sense this file freezes.
//
// TYPE-LEVEL REPRESENTATION CENSUS (#586) — the same gap exists one level up: a
// type-level [JsonConverter] (replaces the WHOLE per-property contract),
// [JsonNumberHandling], [JsonUnmappedMemberHandling], [JsonPolymorphic] and/or
// [JsonDerivedType] (polymorphism — see the POLYMORPHISM note below), or
// [JsonObjectCreationHandling] on a frozen RECORD TYPE itself changes the wire while
// moving neither the golden nor either census above, both of which are scoped to
// MEMBERS. TypeSignature (below) is compared the same two ways:
//   • RENDERED side (GetRenderedTypeSignature): reads the attribute directly off the
//     record Type WITH inherit: false (see the INHERITANCE note below), and is
//     rendered onto the SAME "record <Name>" golden header line — present only when
//     at least one is — via the same helper FormatRecordHeader shares with the
//     renderer, so the golden and the rendered signature cannot drift.
//   • STJ side (GetStjTypeSignature): reads EventStreamJson.Options.GetTypeInfo(type)'s
//     own type-level facts — NumberHandling, UnmappedMemberHandling,
//     PolymorphismOptions, PreferredPropertyObjectCreationHandling — plus Converter,
//     gated on Kind == JsonTypeInfoKind.None.
// MEASURED: EVERY reflected Object-shaped type — including every frozen record, and
// even the polymorphic TypePolymorphicBaseProbeRecord below — reports a NON-NULL
// ti.Converter (STJ's own internal ObjectDefaultConverter<T> or
// SmallObjectWithParameterizedConstructorConverter<...>, chosen per whether the type
// binds through a parameterless or parameterized constructor); only a type carrying an
// explicit type-level [JsonConverter] reports ti.Kind == JsonTypeInfoKind.None (with
// ti.Properties left EMPTY — the converter owns the whole contract, so there is nothing
// left to enumerate). Gating the STJ-side ConverterTypeName on Kind == None is what
// lets ti.Converter's near-universal non-nullness be ignored everywhere else; no
// frozen record shows Kind == None today.
//
// INHERITANCE (#586 — measured, STJ 8.0.0.0): every type-level attribute read in this
// file passes inherit: false. STJ 8 ignores a base type's attributes when resolving a
// DERIVED type's contract, regardless of what C# reflection's own inherit parameter
// would otherwise find (the default, inherit: true, would pick up an attribute that
// STJ itself never applies to the derived type, reporting a false representation
// marker that STJ does not honour).
//
// POLYMORPHISM (#586 — measured, STJ 8.0.0.0): STJ treats [JsonDerivedType] ALONE,
// with NO [JsonPolymorphic] attribute present at all, as making a type polymorphic —
// JsonTypeInfo.PolymorphismOptions is populated IDENTICALLY whether [JsonPolymorphic]
// is present or not, including its settings beyond the derived-type list:
// TypeDiscriminatorPropertyName, UnknownDerivedTypeHandling,
// IgnoreUnrecognizedTypeDiscriminators. GetDeclaredDerivedTypeTokens therefore gates on
// [JsonDerivedType] PRESENCE, not [JsonPolymorphic] presence, and
// GetDeclaredPolymorphicSettings defaults each of the other settings to STJ's OWN
// measured default when reading them off an absent or under-specified [JsonPolymorphic]
// attribute — critically, "$type" for TypeDiscriminatorPropertyName, NOT the bare
// attribute class's own null default (measured divergence: see
// GetDeclaredPolymorphicSettings' remarks). A frozen record's own declared derived
// types must themselves be frozen records too — see
// EventWireContract_DerivedTypesAreThemselvesFrozen — otherwise a derived type's OWN
// members are invisible to every gate here.
//
// CONVERTER NAMES, FACTORIES AND NULLABLE-CONVERTER UNWRAPPING (#586): every converter-name
// rendering site (member and type level, both the attribute-Type and the STJ-instance
// side) goes through the shared FormatType formatter, not Type.FullName directly —
// Type.FullName on a GENERIC converter type embeds assembly-qualified generic
// arguments (Version=…, Culture=…, PublicKeyToken=…), which FormatType strips,
// matching how every other rendered CLR type name in this file is already formatted.
// The GOLDEN renders the converter type the [JsonConverter] attribute DECLARES, never the
// converter System.Text.Json creates from it (FormatDeclaredConverterTypeName). Two
// different factories can create converters of the SAME type that write different bytes —
// measured: on a Nullable<TEnum> member, JsonStringEnumConverter and a subclass whose
// constructor passes JsonNamingPolicy.CamelCase both create the internal EnumConverter<TEnum>,
// yet for the same value one writes "B" and the other "b" (the bytes
// Golden_RendersDeclaredFactory_SoAFactorySwapIsVisible asserts on CensusProbeEnum.B) — so only
// the declared type makes such a swap visible (ConverterFactorySwapProbeRecord). The census's RENDERED side, by contrast, mirrors
// System.Text.Json's own [JsonConverter] resolution, so that it compares like with like
// against what JsonPropertyInfo.CustomConverter and JsonTypeInfo.Converter actually hold.
// MEASURED (STJ 8.0.0.0), one bullet per branch ResolveMemberConverterTypeName and
// ResolveTypeConverterTypeName reproduce:
//   • member, declared converter's CanConvert(property type) is true: CustomConverter is the
//     declared converter itself — a JsonConverterFactory UNEXPANDED (e.g.
//     JsonStringEnumConverter on a plain enum member reports JsonStringEnumConverter);
//   • member of type Nullable<T>, declared converter cannot convert T? but can convert T: STJ
//     expands a factory against T first, then wraps the result (or the declared non-factory
//     converter, e.g. a JsonConverter<int> on an int? member) in its internal
//     NullableConverter<T>. CustomConverter reports the WRAPPER, whose wrapped converter is
//     reachable only via its PRIVATE _elementConverter field (see UnwrapNullableConverter);
//     unwrapped, it is the factory's CREATED converter (e.g. the internal EnumConverter<TEnum>)
//     or the declared non-factory converter;
//   • type level: JsonTypeInfo.Converter is a factory's CREATED converter, or the declared
//     converter for a non-factory (Kind == None either way).
//
// OPTIONS-LEVEL PINS (#586) — EventStreamJsonOptions_PinnedSettings_MatchMeasuredExpectations
// (below) adds one NAMED assertion per JsonSerializerOptions setting that affects what
// EventStreamJson.Options writes or parses, so a future edit to CreateOptions() that
// changes one of these (rather than a member/type attribute) is caught the same way.
// Named, not reflective: a reflective golden over JsonSerializerOptions would not
// survive an STJ version bump (STJ 9 adds AllowOutOfOrderMetadataProperties, STJ 10 adds
// AllowDuplicateProperties — neither exists on the STJ 8 this project runs against;
// measured: referencing either by name here is a compile error under STJ 8).
//
// REGENERATION (when the event wire contract legitimately changes — additive only
// for v1.x, e.g. a namespace-qualified CLR type name changes but no wire name does):
//   VOUCHFX_REGEN_EVENT_CONTRACT=1 dotnet test tests/Vouchfx.Engine.Abstractions.Tests \
//     --filter "FullyQualifiedName~EventContractFreezeTests"
//   This rewrites Golden/event-stream-wire-contract.v1.txt from the freshly-reflected
//   signature.  Review the diff (the [wire=...] JSON property names must NOT change),
//   then commit.  Mirror of SchemaFreezeTests.IsRegenRequested / VOUCHFX_REGEN_SCHEMA.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

/// <summary>
/// S08-F-02: the frozen-v1-event-wire-contract golden-snapshot gate.
/// </summary>
public sealed class EventContractFreezeTests
{
    private readonly ITestOutputHelper _output;

    public EventContractFreezeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The snapshotted event records, in the fixed order they appear in the golden.
    /// Listing them by <see cref="Type"/> (not a namespace scan) makes a renamed or
    /// removed record a compile error here, and makes adding a record a deliberate,
    /// reviewed two-step act (this list + the golden).
    /// </summary>
    private static readonly Type[] s_eventRecords =
    {
        typeof(EventEnvelope),
        typeof(ScenarioStartedEvent),
        typeof(StepStartedEvent),
        typeof(StepAttemptEvent),
        typeof(StepCompletedEvent),
        typeof(ScenarioCompletedEvent),
        typeof(ReproducibilityEnvelopeEvent),
        typeof(TransportNoticeEvent), // #450/#453; added here AND to the golden, one reviewed act.
        typeof(VerdictCounts),

        // Nested value records reachable on the wire from the events above. Their
        // FIELDS (name + CLR type + [JsonPropertyName] wire name + required/init) are
        // part of the JSON-Lines contract and must be frozen too — freezing only the
        // top-level events leaves these unguarded (a renamed CapturedVar.Matched would
        // pass the gate while breaking the wire).
        typeof(CapturedVar),     // nested in StepCompletedEvent (captured[])
        typeof(SubstitutionRef), // nested in StepCompletedEvent (substitutions[])

        // The next two live in the Vouchfx.Engine.Abstractions.Reproducibility
        // namespace (NOT Events), but are nested in the reproducibility envelope event
        // (ReproducibilityEnvelopeEvent.SecretReferences[] / .Fixtures[]) and so are
        // part of the envelope wire contract. Listed explicitly because the Events
        // completeness guard cannot reach a different namespace.
        typeof(SecretReferenceDigest), // nested in ReproducibilityEnvelopeEvent (secretReferences[])
        typeof(FixtureDigest),         // nested in ReproducibilityEnvelopeEvent (fixtures[])
    };

    /// <summary>
    /// The reflected wire signature of the v1 event records must be byte-for-byte
    /// (newline-normalised) identical to the committed golden.  If this fails, the
    /// JSON-Lines wire contract (§14) has drifted.
    /// </summary>
    [Fact]
    public void EventWireContract_MatchesGolden_ByteForByte()
    {
        var actual = BuildSignature(s_eventRecords);

        // Regeneration mode: rewrite the committed golden from the freshly-reflected
        // wire signature and pass.  Mirror of SchemaFreezeTests / SdkContractFreezeTests.
        if (IsRegenRequested())
        {
            var repoRoot = FindRepoRoot();
            var goldenPath = Path.Combine(
                repoRoot, "tests", "Vouchfx.Engine.Abstractions.Tests",
                "Golden", "event-stream-wire-contract.v1.txt");
            File.WriteAllText(goldenPath, actual);
            return;
        }

        var golden = ReadGolden();

        var actualNormalised = Normalise(actual);
        var goldenNormalised = Normalise(golden);

        Assert.True(
            string.Equals(actualNormalised, goldenNormalised, StringComparison.Ordinal),
            "The v1 event-stream wire contract (§14 JSON Lines) has DRIFTED. The event "
            + "records are FROZEN for the v1.x engine series — a property name, type, or "
            + "[JsonPropertyName] wire name change breaks every renderer and the Healer. "
            + "If this change is intentional, regenerate "
            + "Golden/event-stream-wire-contract.v1.txt with VOUCHFX_REGEN_EVENT_CONTRACT=1 "
            + "and get it reviewed."
            + Environment.NewLine
            + FirstDifference(goldenNormalised, actualNormalised));
    }

    /// <summary>
    /// Pins the deliberate T1 decision INDEPENDENTLY of the golden text: the three
    /// step events carry RunId + StepId but NOT ScenarioId.  Re-adding ScenarioId to
    /// a step event must be a conscious, reviewed change — this assertion (plus the
    /// golden) makes that so.
    /// </summary>
    [Fact]
    public void StepEvents_CarryRunIdAndStepId_ButNotScenarioId()
    {
        Type[] stepEvents =
        {
            typeof(StepStartedEvent),
            typeof(StepAttemptEvent),
            typeof(StepCompletedEvent),
        };

        foreach (var evt in stepEvents)
        {
            var propNames = evt
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(
                propNames.Contains("RunId"),
                $"{evt.Name} must carry RunId (the renderer keys aggregated streams by runId).");
            Assert.True(
                propNames.Contains("StepId"),
                $"{evt.Name} must carry StepId.");
            Assert.False(
                propNames.Contains("ScenarioId"),
                $"{evt.Name} must NOT carry ScenarioId (T1 decision): the (runId, stepId) pair "
                + "disambiguates aggregated streams because each scenario has a distinct runId. "
                + "Re-adding ScenarioId is a conscious, reviewed change.");
        }
    }

    /// <summary>
    /// Public record types in the <c>Vouchfx.Engine.Abstractions.Events</c> namespace
    /// that are deliberately NOT frozen by this gate.  This list MUST stay empty unless
    /// a record is genuinely outside the §14 wire contract — and then only with a
    /// reviewed comment justifying the exclusion.  Its purpose is to force a conscious
    /// edit: a new record is either added to <see cref="s_eventRecords"/> (frozen) or
    /// added here (explicitly excused), never silently left unguarded.
    /// </summary>
    /// <remarks>
    /// SCOPE LIMIT worth knowing before trusting this gate: the completeness guard
    /// filters on <c>IsRecord</c>, so a static class in the Events namespace is outside
    /// it, and the golden freezes property NAMES and TYPES, never string VALUES.
    /// <c>TransportNoticeKinds</c> is both — its two <c>kind</c> tokens are part of the
    /// wire contract (a consumer branches on them) yet renaming one produces no diff
    /// here. Their value-freeze lives in
    /// <c>TransportNoticeEventTests.Kinds_AreTheTwoPinnedTokens</c>, which asserts the
    /// literals. A reviewer seeing that test change alongside a constant is looking at a
    /// wire break, not at a test kept in step.
    /// </remarks>
    private static readonly HashSet<Type> s_eventNamespaceExclusions = new();

    /// <summary>
    /// Completeness guard (SF-2): every public record declared in the
    /// <c>Vouchfx.Engine.Abstractions.Events</c> namespace must be covered by the
    /// freeze — either present in <see cref="s_eventRecords"/> or explicitly excused in
    /// <see cref="s_eventNamespaceExclusions"/>.  This is the analogue of T2's
    /// SchemaFreezeTests "a missing provider is a compile error": adding a new public
    /// Events record (e.g. a future <c>SuiteStartedEvent</c>) FAILS here until it is
    /// consciously added to the frozen set or excused, so the wire surface cannot grow
    /// unfrozen.  (The Reproducibility nested records — SecretReferenceDigest,
    /// FixtureDigest — live in a different namespace and are covered by being listed
    /// explicitly in <see cref="s_eventRecords"/>; this guard scopes to the Events
    /// namespace only.)
    /// </summary>
    [Fact]
    public void SnapshotSet_CoversEveryPublicEventRecord()
    {
        const string eventsNamespace = "Vouchfx.Engine.Abstractions.Events";

        var declaredRecords = typeof(EventEnvelope).Assembly
            .GetTypes()
            .Where(t => t.Namespace == eventsNamespace)
            .Where(t => t.IsPublic)
            .Where(IsRecord)
            .ToHashSet();

        Assert.NotEmpty(declaredRecords);

        var covered = new HashSet<Type>(s_eventRecords);
        covered.UnionWith(s_eventNamespaceExclusions);

        var uncovered = declaredRecords
            .Where(t => !covered.Contains(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            uncovered.Count == 0,
            "A public record in the Vouchfx.Engine.Abstractions.Events namespace is NOT "
            + "covered by the v1 wire-contract freeze. Every public Events record is part "
            + "of the §14 JSON-Lines contract: add it to s_eventRecords (and regenerate the "
            + "golden), or — only if it is genuinely outside the wire contract — add it to "
            + "s_eventNamespaceExclusions with a reviewed comment. Uncovered: "
            + string.Join(", ", uncovered.Select(t => t.Name)));
    }

    /// <summary>
    /// True when <paramref name="type"/> is a C# <c>record</c>.  The compiler
    /// synthesises an <c>EqualityContract</c> property and a <c>&lt;Clone&gt;$</c>
    /// method on every record (and on no ordinary class), so their joint presence is a
    /// reliable, framework-agnostic record marker.
    /// </summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null
        && type.GetProperty(
            "EqualityContract",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance) is not null;

    // ── Signature builder ────────────────────────────────────────────────────

    /// <summary>
    /// Produces the deterministic, newline-joined wire signature of the supplied
    /// event records.  See the inclusion rule documented at the top of this file.
    /// </summary>
    private static string BuildSignature(Type[] records)
    {
        var sb = new StringBuilder();
        sb.Append("# Vouchfx.Engine.Abstractions.Events v1 JSON-Lines wire contract — FROZEN for v1.x.\n");
        sb.Append("# Generated by EventContractFreezeTests; do not hand-edit. Regenerate + review on intentional change.\n");

        foreach (var record in records)
        {
            sb.Append('\n');
            sb.Append(FormatRecordHeader(record));
            sb.Append('\n');

            var props = record
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(p => p.Name, StringComparer.Ordinal);

            foreach (var prop in props)
            {
                sb.Append("  ");
                sb.Append(FormatProperty(prop));
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string FormatProperty(PropertyInfo prop)
    {
        var wire = GetJsonPropertyName(prop);
        var wirePart = wire is null ? string.Empty : $" [wire={wire}]";

        var required = IsRequiredMember(prop) ? " [required]" : string.Empty;

        var setterKind = prop.SetMethod switch
        {
            null => " [get-only]",
            { } setter when IsInitOnly(setter) => " [init]",
            _ => " [set]",
        };

        // #586: representation-affecting member attributes, rendered ONLY when present
        // (measured: no frozen record carries any of these today, so the
        // golden is unaffected — see the file header's MEMBER-LEVEL REPRESENTATION
        // CENSUS note). Each getter is shared with GetRenderedMemberSignatures so the
        // golden and the census cannot independently drift on what counts as present. The
        // converter marker is the DECLARED type, unexpanded, so swapping one converter or
        // factory for another always changes this line (see the file header's CONVERTER
        // NAMES note).
        var converter = FormatDeclaredConverterTypeName(GetDeclaredJsonConverterType(prop));
        var converterPart = converter is null ? string.Empty : $" [converter={converter}]";

        var numberHandling = GetJsonNumberHandling(prop);
        var numberHandlingPart = numberHandling is null ? string.Empty : $" [numberHandling={numberHandling}]";

        var order = GetJsonPropertyOrder(prop);
        var orderPart = order == 0 ? string.Empty : $" [order={order}]";

        var objectCreationHandling = GetJsonObjectCreationHandling(prop);
        var objectCreationHandlingPart = objectCreationHandling is null
            ? string.Empty
            : $" [objectCreationHandling={objectCreationHandling}]";

        var conditionalIgnore = GetJsonIgnoreCondition(prop);
        var conditionalIgnorePart = conditionalIgnore is null
            ? string.Empty
            : $" [ignoreCondition={conditionalIgnore}]";

        return $"property {FormatType(prop.PropertyType)} {prop.Name}"
            + $"{wirePart}{required}{setterKind}"
            + $"{converterPart}{numberHandlingPart}{orderPart}{objectCreationHandlingPart}{conditionalIgnorePart}";
    }

    /// <summary>
    /// Renders the <c>"record &lt;Name&gt;"</c> golden header line for <paramref name="record"/>,
    /// appending #586's type-level representation markers ONLY when present (measured:
    /// no frozen record carries any today — see the file header's
    /// TYPE-LEVEL REPRESENTATION CENSUS note). Shared with
    /// <see cref="GetRenderedTypeSignature"/> so the golden and the type-level census
    /// cannot independently drift on what counts as present.
    /// </summary>
    private static string FormatRecordHeader(Type record)
    {
        // #586: attribute reads use inherit: false (mirrors STJ 8 — see the file header's
        // INHERITANCE note) and the converter marker is the DECLARED type through the shared
        // FormatType formatter, never expanded and never the raw, assembly-qualified
        // Type.FullName (see the file header's CONVERTER NAMES note).
        var converter = FormatDeclaredConverterTypeName(GetDeclaredTypeConverterType(record));
        var converterPart = converter is null ? string.Empty : $" [converter={converter}]";

        var numberHandling = record.GetCustomAttribute<JsonNumberHandlingAttribute>(inherit: false)?.Handling;
        var numberHandlingPart = numberHandling is null ? string.Empty : $" [numberHandling={numberHandling}]";

        var unmapped =
            record.GetCustomAttribute<JsonUnmappedMemberHandlingAttribute>(inherit: false)?.UnmappedMemberHandling;
        var unmappedPart = unmapped is null ? string.Empty : $" [unmappedMemberHandling={unmapped}]";

        var objectCreationHandling =
            record.GetCustomAttribute<JsonObjectCreationHandlingAttribute>(inherit: false)?.Handling;
        var objectCreationHandlingPart = objectCreationHandling is null
            ? string.Empty
            : $" [objectCreationHandling={objectCreationHandling}]";

        var derivedTypes = GetDeclaredDerivedTypeTokens(record);
        var polymorphicPart = derivedTypes is null
            ? string.Empty
            : $" [polymorphicDerivedTypes={string.Join(";", derivedTypes)}]";

        // #586: the settings a [JsonPolymorphic] attribute can carry
        // alongside its derived-type list — rendered whenever the record is polymorphic at
        // all (derivedTypes is non-null), not only when a [JsonPolymorphic] attribute is
        // literally present, because [JsonDerivedType] alone already makes the type
        // polymorphic under System.Text.Json (see GetDeclaredDerivedTypeTokens).
        var polymorphicSettingsPart = derivedTypes is null
            ? string.Empty
            : FormatPolymorphicSettingsPart(GetDeclaredPolymorphicSettings(record));

        return "record " + record.Name
            + $"{converterPart}{numberHandlingPart}{unmappedPart}{objectCreationHandlingPart}"
            + $"{polymorphicPart}{polymorphicSettingsPart}";
    }

    private static string FormatPolymorphicSettingsPart(
        (string Discriminator, JsonUnknownDerivedTypeHandling UnknownHandling, bool IgnoreUnrecognized) settings) =>
        $" [polymorphicDiscriminator={settings.Discriminator}]"
        + $" [polymorphicUnknownHandling={settings.UnknownHandling}]"
        + $" [polymorphicIgnoreUnrecognized={settings.IgnoreUnrecognized}]";

    /// <summary>
    /// The sorted <c>"&lt;DerivedTypeFullName&gt;:&lt;discriminator&gt;"</c> tokens declared via
    /// <c>[JsonDerivedType]</c> on <paramref name="record"/> (read with <c>inherit: false</c> —
    /// see the file header's INHERITANCE note), or <see langword="null"/> when
    /// <paramref name="record"/> declares none.
    /// </summary>
    /// <remarks>
    /// MEASURED (STJ 8.0.0.0): System.Text.Json treats <c>[JsonDerivedType]</c> ALONE — with NO
    /// <c>[JsonPolymorphic]</c> attribute present at all — as making the type polymorphic;
    /// <c>JsonTypeInfo.PolymorphismOptions</c> is populated IDENTICALLY (same discriminator
    /// property name, same unknown-derived-type handling, same ignore-unrecognized flag, same
    /// derived-type list) whether <c>[JsonPolymorphic]</c> is present or not.
    /// <c>EventStreamJson.FromLine{T}</c>'s own remarks already document the same fact
    /// ("driven by [JsonPolymorphic] or [JsonDerivedType]"). Gating on <c>[JsonDerivedType]</c>
    /// PRESENCE — mirroring System.Text.Json, rather than gating on <c>[JsonPolymorphic]</c>'s
    /// presence — is what lets a frozen record that carries ONLY <c>[JsonDerivedType]</c> render
    /// a golden marker and participate in the type-level census: gating on
    /// <c>[JsonPolymorphic]</c> instead would leave such a record silently unrenderable and
    /// unpinnable, because a regenerated golden could never catch up to what the record actually
    /// does on the wire.
    /// Shared by <see cref="FormatRecordHeader"/> and <see cref="GetRenderedTypeSignature"/>.
    /// </remarks>
    private static List<string>? GetDeclaredDerivedTypeTokens(Type record)
    {
        var derivedTypeAttrs = record.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).ToList();
        if (derivedTypeAttrs.Count == 0)
        {
            return null;
        }

        return derivedTypeAttrs
            .Select(a => $"{a.DerivedType.FullName}:{a.TypeDiscriminator}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// <paramref name="record"/>'s polymorphism settings BEYOND the derived-type list —
    /// the discriminator property name, the unknown-derived-type handling, and the
    /// ignore-unrecognized-discriminator flag (#586) — read from an OPTIONAL
    /// <c>[JsonPolymorphic]</c> attribute (<c>inherit: false</c>), each defaulted to
    /// System.Text.Json's OWN measured default for a polymorphic type, not to the attribute
    /// CLASS's own property defaults.
    /// </summary>
    /// <remarks>
    /// MEASURED (STJ 8.0.0.0) divergence this guards against: a freshly-constructed
    /// <c>JsonPolymorphicAttribute</c>'s own <c>TypeDiscriminatorPropertyName</c> property is
    /// <see langword="null"/>, but System.Text.Json's RESOLVED
    /// <c>JsonPolymorphismOptions.TypeDiscriminatorPropertyName</c> — for every polymorphic
    /// type that does not explicitly override it, including one with no
    /// <c>[JsonPolymorphic]</c> attribute at all — is the literal string <c>"$type"</c>.
    /// Defaulting to the attribute's own <see langword="null"/> here, instead of to the
    /// measured <c>"$type"</c>, would make this signature permanently disagree with the STJ
    /// side for every polymorphic record that does not override the discriminator — including
    /// every polymorphism probe below. <c>UnknownDerivedTypeHandling</c>
    /// (<c>FailSerialization</c>) and <c>IgnoreUnrecognizedTypeDiscriminators</c>
    /// (<see langword="false"/>) do not have this gap: the attribute class's own defaults
    /// already match System.Text.Json's resolved defaults for both (measured).
    /// Only called when <see cref="GetDeclaredDerivedTypeTokens"/> already returned non-null —
    /// these settings are meaningless without at least one declared derived type, and
    /// System.Text.Json itself refuses to build a <c>JsonTypeInfo</c> at all for a type
    /// carrying <c>[JsonPolymorphic]</c> with ZERO <c>[JsonDerivedType]</c> attributes
    /// (measured: throws <see cref="InvalidOperationException"/>, "should specify at least one
    /// derived type").
    /// </remarks>
    private static (string Discriminator, JsonUnknownDerivedTypeHandling UnknownHandling, bool IgnoreUnrecognized)
        GetDeclaredPolymorphicSettings(Type record)
    {
        var polyAttr = record.GetCustomAttribute<JsonPolymorphicAttribute>(inherit: false);
        return (
            polyAttr?.TypeDiscriminatorPropertyName ?? "$type",
            polyAttr?.UnknownDerivedTypeHandling ?? JsonUnknownDerivedTypeHandling.FailSerialization,
            polyAttr?.IgnoreUnrecognizedTypeDiscriminators ?? false);
    }

    /// <summary>
    /// #586: the GOLDEN's converter marker — the converter <see cref="Type"/> a
    /// <c>[JsonConverter]</c> attribute DECLARES, formatted through the shared
    /// <see cref="FormatType"/> formatter rather than <see cref="Type.FullName"/> directly (a
    /// GENERIC converter type would otherwise render with assembly-qualified generic arguments:
    /// <c>Version=…, Culture=…, PublicKeyToken=…</c>), or <see langword="null"/> when the
    /// attribute is absent. Shared by both golden rendering sites
    /// (<see cref="FormatProperty"/>, <see cref="FormatRecordHeader"/>).
    /// </summary>
    /// <remarks>
    /// Deliberately NEVER expanded, even for a <see cref="JsonConverterFactory"/>: the
    /// declared type is the author's intent, and it is what distinguishes two factories that
    /// create converters of the SAME type but write different bytes (measured — see the file
    /// header's CONVERTER NAMES note and <see cref="Golden_RendersDeclaredFactory_SoAFactorySwapIsVisible"/>).
    /// Rendering the created converter here instead would let such a swap change the wire with
    /// the golden unchanged. The census compares the RESOLVED converter instead
    /// (<see cref="ResolveMemberConverterTypeName"/>, <see cref="ResolveTypeConverterTypeName"/>).
    /// </remarks>
    private static string? FormatDeclaredConverterTypeName(Type? converterType) =>
        converterType is null ? null : FormatType(converterType);

    /// <summary>
    /// #586: the census's RENDERED-side converter name for a member whose
    /// <c>[JsonConverter]</c> declares <paramref name="converterType"/> — mirroring System.Text.Json's
    /// own member-level attribute resolution, so it equals what
    /// <c>JsonPropertyInfo.CustomConverter</c> holds once <see cref="UnwrapNullableConverter"/>
    /// has removed any <c>NullableConverter&lt;T&gt;</c> wrapper. <see langword="null"/> when the
    /// attribute is absent.
    /// </summary>
    /// <remarks>
    /// MEASURED (STJ 8.0.0.0), one branch each:
    /// <list type="bullet">
    /// <item>The declared converter's <c>CanConvert(propertyType)</c> is true: System.Text.Json
    /// keeps the declared instance as-is — a <see cref="JsonConverterFactory"/> UNEXPANDED
    /// (<see cref="JsonStringEnumConverter"/> on a plain enum member reports
    /// <see cref="JsonStringEnumConverter"/>) — so the DECLARED type is returned.</item>
    /// <item>Otherwise, when <paramref name="propertyType"/> is <c>Nullable&lt;T&gt;</c> and the
    /// declared converter can convert <c>T</c>: a factory is expanded against <c>T</c> with
    /// <c>CreateConverter(T, EventStreamJson.Options)</c> and the CREATED converter's type is
    /// returned (<see cref="JsonStringEnumConverter"/> on a nullable enum member reports the
    /// internal <c>EnumConverter&lt;TEnum&gt;</c>); a non-factory's DECLARED type is returned.
    /// System.Text.Json then wraps either in <c>NullableConverter&lt;T&gt;</c>, which the STJ side
    /// unwraps.</item>
    /// <item>Otherwise System.Text.Json refuses the attribute (it throws while building the
    /// <c>JsonTypeInfo</c>), so the census never compares this value; a descriptive
    /// placeholder is returned rather than throwing here.</item>
    /// </list>
    /// The declared type is instantiated through its public parameterless constructor, as
    /// System.Text.Json itself instantiates a <c>[JsonConverter]</c>-declared type. A factory
    /// that returns <see langword="null"/> is reported as such, so a mismatch stays visible
    /// rather than silently falling back to the factory's own name.
    /// </remarks>
    private static string? ResolveMemberConverterTypeName(Type? converterType, Type propertyType)
    {
        if (converterType is null)
        {
            return null;
        }

        if (Activator.CreateInstance(converterType) is not JsonConverter converter)
        {
            return $"(declared type {FormatType(converterType)} is not a JsonConverter)";
        }

        if (converter.CanConvert(propertyType))
        {
            return FormatType(converterType);
        }

        if (Nullable.GetUnderlyingType(propertyType) is { } underlying && converter.CanConvert(underlying))
        {
            return converter is JsonConverterFactory factory
                ? FormatCreatedConverterTypeName(factory, underlying)
                : FormatType(converterType);
        }

        return $"(declared converter {FormatType(converterType)} cannot convert {FormatType(propertyType)})";
    }

    /// <summary>
    /// #586: the census's RENDERED-side converter name for a record whose type-level
    /// <c>[JsonConverter]</c> declares <paramref name="converterType"/> — mirroring what
    /// System.Text.Json reports as <c>JsonTypeInfo.Converter</c> for such a type.
    /// <see langword="null"/> when the attribute is absent.
    /// </summary>
    /// <remarks>
    /// MEASURED (STJ 8.0.0.0): unlike a plain MEMBER, a type-level factory IS expanded —
    /// <c>JsonTypeInfo.Converter</c> reports the factory's CREATED converter (with
    /// <c>Kind == None</c>), so a factory is expanded here with
    /// <c>CreateConverter(record, EventStreamJson.Options)</c>; a non-factory's DECLARED type is
    /// returned unchanged. Proven by <see cref="Census_Detects_TypeLevelRepresentationAttributes"/>.
    /// </remarks>
    private static string? ResolveTypeConverterTypeName(Type? converterType, Type record)
    {
        if (converterType is null)
        {
            return null;
        }

        return Activator.CreateInstance(converterType) switch
        {
            JsonConverterFactory factory => FormatCreatedConverterTypeName(factory, record),
            JsonConverter => FormatType(converterType),
            _ => $"(declared type {FormatType(converterType)} is not a JsonConverter)",
        };
    }

    /// <summary>
    /// #586: the formatted type of the converter <paramref name="factory"/> creates for
    /// <paramref name="typeToConvert"/> under the shared <see cref="EventStreamJson.Options"/>,
    /// or a descriptive placeholder when the factory declines (returns <see langword="null"/>).
    /// </summary>
    private static string FormatCreatedConverterTypeName(JsonConverterFactory factory, Type typeToConvert) =>
        factory.CreateConverter(typeToConvert, EventStreamJson.Options) is { } created
            ? FormatType(created.GetType())
            : $"(factory {FormatType(factory.GetType())} declined to convert {FormatType(typeToConvert)})";

    /// <summary>
    /// #586: the member-level <c>[JsonConverter]</c> attribute's declared converter type, or
    /// <see langword="null"/> when <paramref name="prop"/> carries none. Read ONCE, by both
    /// <see cref="FormatProperty"/> (the golden, which renders it as declared) and
    /// <see cref="GetRenderedMemberSignatures"/> (the census, which resolves it through
    /// <see cref="ResolveMemberConverterTypeName"/>), so the two cannot drift on whether a
    /// converter is present. Deliberately distinct from a converter reached through the shared
    /// <see cref="EventStreamJson.Options"/>' GLOBALLY registered <c>Converters</c> list (e.g.
    /// <c>VerdictJsonConverter</c>) — that path never sets this attribute and is invisible here
    /// by construction, matching STJ's own <c>JsonPropertyInfo.CustomConverter</c> (measured
    /// null for a globally-registered converter; see the file header).
    /// </summary>
    private static Type? GetDeclaredJsonConverterType(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType;

    /// <summary>
    /// #586: the type-level <c>[JsonConverter]</c> attribute's declared converter type (read
    /// with <c>inherit: false</c> — see the file header's INHERITANCE note), or
    /// <see langword="null"/> when <paramref name="record"/> carries none. Shared by
    /// <see cref="FormatRecordHeader"/> and <see cref="GetRenderedTypeSignature"/>.
    /// </summary>
    private static Type? GetDeclaredTypeConverterType(Type record) =>
        record.GetCustomAttribute<JsonConverterAttribute>(inherit: false)?.ConverterType;

    /// <summary>
    /// #586: the STJ-side counterpart of <see cref="FormatDeclaredConverterTypeName"/> —
    /// formats an actual converter INSTANCE's runtime type the same way, for the same reason.
    /// Shared by <see cref="GetStjMappedMemberSignatures"/> and <see cref="GetStjTypeSignature"/>.
    /// </summary>
    private static string? FormatConverterInstanceTypeName(object? converterInstance) =>
        converterInstance is null ? null : FormatType(converterInstance.GetType());

    /// <summary>
    /// #586: unwraps System.Text.Json's own internal <c>NullableConverter&lt;T&gt;</c> wrapper.
    /// </summary>
    /// <remarks>
    /// MEASURED (STJ 8.0.0.0): when a member-level <c>[JsonConverter]</c> names a converter for
    /// a value type's NON-nullable underlying type (e.g. a <c>JsonConverter&lt;int&gt;</c>, or a
    /// <see cref="JsonConverterFactory"/> such as <see cref="JsonStringEnumConverter"/>, which
    /// System.Text.Json expands to its created converter BEFORE this wrapping) but the member
    /// itself is <c>Nullable&lt;T&gt;</c> (<c>int?</c>), System.Text.Json wraps it:
    /// <c>JsonPropertyInfo.CustomConverter</c> reports
    /// <c>System.Text.Json.Serialization.Converters.NullableConverter&lt;Int32&gt;</c> — NOT the
    /// attribute-named (or factory-created) converter — so comparing that wrapper's type name
    /// against the rendered side (<see cref="ResolveMemberConverterTypeName"/>, which names the
    /// declared or factory-created converter, never the wrapper) could never agree for any
    /// nullable-value-typed member using this pattern. The
    /// wrapped converter is reachable only through System.Text.Json's PRIVATE instance field
    /// <c>_elementConverter</c> (measured by reflecting over the wrapper's own field list; there
    /// is no public API for this). If a future STJ version renames or removes that field, the
    /// lookup degrades to returning the WRAPPER unchanged rather than throwing, so a version
    /// drift surfaces as a renewed (investigable) mismatch rather than a crashed test run.
    /// </remarks>
    private static object? UnwrapNullableConverter(object? converter)
    {
        if (converter is null)
        {
            return null;
        }

        var converterType = converter.GetType();
        if (!converterType.IsGenericType
            || converterType.Namespace != "System.Text.Json.Serialization.Converters"
            || converterType.Name != "NullableConverter`1")
        {
            return converter;
        }

        var elementConverterField =
            converterType.GetField("_elementConverter", BindingFlags.Instance | BindingFlags.NonPublic);
        return elementConverterField?.GetValue(converter) ?? converter;
    }

    // ── Census (#578): STJ-mapped vs. golden-rendered member sets ────────────

    /// <summary>
    /// The explicit <c>[JsonPropertyName]</c> value on <paramref name="prop"/>, or
    /// <see langword="null"/> when the property carries none (its wire name is then
    /// its CLR name, since <see cref="EventStreamJson.Options"/> applies no naming
    /// policy). Shared by <see cref="FormatProperty"/> (the golden renderer) and
    /// <see cref="GetRenderedMemberSignatures"/> (the census) so the two cannot
    /// independently drift on what counts as a property's wire name.
    /// </summary>
    private static string? GetJsonPropertyName(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;

    /// <summary>
    /// <see langword="true"/> when <paramref name="prop"/> carries the C#
    /// <see langword="required"/> modifier (<see cref="RequiredMemberAttribute"/>).
    /// Shared by <see cref="FormatProperty"/> and <see cref="GetRenderedMemberSignatures"/>.
    /// </summary>
    private static bool IsRequiredMember(PropertyInfo prop) =>
        prop.GetCustomAttribute<RequiredMemberAttribute>() is not null;

    /// <summary>
    /// #586: the member-level <c>[JsonNumberHandling]</c> value, or <see langword="null"/>
    /// when absent. Shared by <see cref="FormatProperty"/> and
    /// <see cref="GetRenderedMemberSignatures"/>.
    /// </summary>
    private static JsonNumberHandling? GetJsonNumberHandling(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonNumberHandlingAttribute>()?.Handling;

    /// <summary>
    /// #586: the member-level <c>[JsonPropertyOrder]</c> value, defaulted to <c>0</c> when
    /// absent — the same default STJ's own <c>JsonPropertyInfo.Order</c> reports for an
    /// unattributed member (measured), so the two sides compare on the EFFECTIVE order
    /// rather than on attribute presence (an explicit <c>[JsonPropertyOrder(0)]</c> is
    /// indistinguishable from no attribute at the wire level, since 0 is already the
    /// default). Shared by <see cref="FormatProperty"/> and
    /// <see cref="GetRenderedMemberSignatures"/>; the golden itself renders the marker
    /// only when this is non-zero.
    /// </summary>
    private static int GetJsonPropertyOrder(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0;

    /// <summary>
    /// #586: the member-level <c>[JsonObjectCreationHandling]</c> value, or
    /// <see langword="null"/> when absent. Shared by <see cref="FormatProperty"/> and
    /// <see cref="GetRenderedMemberSignatures"/>. Measured: STJ 8 refuses (throws
    /// <see cref="NotSupportedException"/> while building the <c>JsonTypeInfo</c>) this
    /// attribute's <c>Populate</c> value on a member of a type bound through a
    /// parameterized constructor — the positional nested records among the frozen types
    /// (CapturedVar, SubstitutionRef, SecretReferenceDigest, FixtureDigest) —
    /// so such a member never reaches this comparison at all; see the file header.
    /// </summary>
    private static JsonObjectCreationHandling? GetJsonObjectCreationHandling(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonObjectCreationHandlingAttribute>()?.Handling;

    /// <summary>
    /// #586: <paramref name="prop"/>'s own <c>[JsonIgnore(Condition=…)]</c> condition, when
    /// it is one that keeps the member mapped (<c>WhenWritingDefault</c>,
    /// <c>WhenWritingNull</c>, or <c>Never</c>) — or <see langword="null"/> when the
    /// attribute is absent, OR when it is present with <c>Condition = Always</c> (that
    /// case drops both <c>Get</c> and <c>Set</c> from STJ's own map and is already covered
    /// by the pre-existing #578 "no accessors" skip — see the file header). Shared by
    /// <see cref="FormatProperty"/> and <see cref="GetRenderedMemberSignatures"/>; compared
    /// against STJ's own <c>JsonPropertyInfo.ShouldSerialize != null</c> signal (see
    /// <see cref="GetStjMappedMemberSignatures"/>), which — measured — reflects ONLY a
    /// member's own attribute and never the shared Options' global
    /// <c>DefaultIgnoreCondition</c>; see the file header for the full measurement.
    /// </summary>
    private static JsonIgnoreCondition? GetJsonIgnoreCondition(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<JsonIgnoreAttribute>();

        // The `attr.Condition == Always` half of this check is BELT-AND-BRACES, not
        // load-bearing on its own (measured: a mutant that drops
        // it entirely is behaviourally equivalent today, because the ONLY member on any
        // probe or frozen record actually carrying Condition = Always is
        // CensusProbeRecord.Dropped, whose Get and Set are BOTH already null — the
        // pre-existing #578 "no accessors" skip in GetStjMappedMemberSignatures excludes
        // it from the STJ-mapped set before this method is ever consulted for it, so
        // removing the check changes no test's outcome). Kept anyway: it documents the
        // Always case explicitly at the one call site that decides "is this member
        // conditionally ignored", rather than relying on a reader to independently
        // rediscover, from the #578 skip alone, that Always could otherwise slip through
        // as a false "conditionally ignored" positive if a FUTURE Always-ignored member
        // ever gained a getter or setter STJ could map.
        return attr is null || attr.Condition == JsonIgnoreCondition.Always ? null : attr.Condition;
    }

    /// <summary>
    /// A member's wire-relevant signature, comparable between the golden's rendered
    /// (public-property-only) view and System.Text.Json's own mapped-member view
    /// under the shared <see cref="EventStreamJson.Options"/>.
    /// #586 adds five representation fields (ConverterTypeName, NumberHandling, Order,
    /// ObjectCreationHandling, ConditionalIgnore) alongside the #578 fields; see the
    /// file header for what each pins and how it was measured. ConverterTypeName names the
    /// resolved converter's TYPE, not its configuration: two factories creating converters of
    /// the same type are equal here, and the golden's declared-type marker is what tells them
    /// apart (see the file header's CONVERTER NAMES note).
    /// </summary>
    private readonly record struct MemberSignature(
        string Wire,
        Type ClrType,
        bool Required,
        bool ExtensionData,
        string? ConverterTypeName,
        JsonNumberHandling? NumberHandling,
        int Order,
        JsonObjectCreationHandling? ObjectCreationHandling,
        bool ConditionalIgnore);

    /// <summary>
    /// The rendered member set for <paramref name="record"/>: exactly the properties
    /// the golden renders (<c>GetProperties(Public | Instance)</c>), keyed by the
    /// DECLARING CLR MEMBER NAME (not the wire name) so it can be diffed against
    /// <see cref="GetStjMappedMemberSignatures"/>'s keys, which are also declaring
    /// member names: wire = <c>[JsonPropertyName] ?? property
    /// name</c>; required = <see cref="RequiredMemberAttribute"/> present;
    /// extensionData = <see cref="JsonExtensionDataAttribute"/> present.
    /// </summary>
    private static Dictionary<string, MemberSignature> GetRenderedMemberSignatures(Type record) =>
        record
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(
                p => p.Name,
                p => new MemberSignature(
                    Wire: GetJsonPropertyName(p) ?? p.Name,
                    ClrType: p.PropertyType,
                    Required: IsRequiredMember(p),
                    ExtensionData: p.GetCustomAttribute<JsonExtensionDataAttribute>() is not null,
                    ConverterTypeName: ResolveMemberConverterTypeName(GetDeclaredJsonConverterType(p), p.PropertyType),
                    NumberHandling: GetJsonNumberHandling(p),
                    Order: GetJsonPropertyOrder(p),
                    ObjectCreationHandling: GetJsonObjectCreationHandling(p),
                    ConditionalIgnore: GetJsonIgnoreCondition(p) is not null),
                StringComparer.Ordinal);

    /// <summary>
    /// The STJ-mapped member set for <paramref name="record"/>, reflected from
    /// <see cref="EventStreamJson.Options"/>'s own <c>JsonTypeInfo.Properties</c> —
    /// the same contract <c>EventStreamJson.FromLine{T}</c> itself reflects over —
    /// keyed by the DECLARING CLR MEMBER NAME (via each <c>JsonPropertyInfo</c>'s
    /// <c>AttributeProvider</c>, which is the backing <see cref="PropertyInfo"/> or,
    /// for a <c>[JsonInclude]</c> field, the backing <see cref="FieldInfo"/>) so a
    /// mapped field or non-public <c>[JsonInclude]</c> member is keyed the same way
    /// the golden-rendered set is, even though neither can ever appear IN the
    /// golden-rendered set: wire = <c>JsonPropertyInfo.Name</c>; clrType =
    /// <c>JsonPropertyInfo.PropertyType</c>; required = <c>JsonPropertyInfo.IsRequired</c>;
    /// extensionData = <c>JsonPropertyInfo.IsExtensionData</c>.
    /// </summary>
    private static Dictionary<string, MemberSignature> GetStjMappedMemberSignatures(Type record)
    {
        var typeInfo = EventStreamJson.Options.GetTypeInfo(record);
        var signatures = new Dictionary<string, MemberSignature>(StringComparer.Ordinal);

        foreach (var p in typeInfo.Properties)
        {
            // A [JsonIgnore] member is still listed here with both Get and Set null
            // (measured, STJ 8; the same observation as
            // EventStreamJson.ComputeRequiredReferenceMembers). STJ never reads or
            // writes it, so it is not a wire member; keeping it would let
            // [JsonIgnore] on a frozen property drop it from the wire with both
            // gates green.
            if (p.Get is null && p.Set is null)
            {
                continue;
            }

            // Every JsonPropertyInfo under the shared reflection-based Options carries
            // the declaring PropertyInfo/FieldInfo as its AttributeProvider (mirrors the
            // same observation EventStreamJson.ComputeRequiredReferenceMembers relies on).
            var memberName = (p.AttributeProvider as MemberInfo)?.Name ?? p.Name;

            // #586: p.CustomConverter/.NumberHandling/.Order/.ObjectCreationHandling are
            // STJ's own per-member representation facts under the shared Options; p.ShouldSerialize
            // is non-null ONLY when the member carries its own [JsonIgnore(Condition=…)] (measured
            // NOT to fire for the shared Options' global DefaultIgnoreCondition alone — see the
            // file header). Each mirrors what GetRenderedMemberSignatures reads off the same
            // member's .NET attributes. ConverterTypeName goes through UnwrapNullableConverter
            // then the shared FormatType formatter; whether it is the declared converter or a
            // factory's created one is System.Text.Json's own decision, which
            // ResolveMemberConverterTypeName mirrors on the rendered side — see the file
            // header's CONVERTER NAMES note.
            var signature = new MemberSignature(
                p.Name,
                p.PropertyType,
                p.IsRequired,
                p.IsExtensionData,
                ConverterTypeName: FormatConverterInstanceTypeName(UnwrapNullableConverter(p.CustomConverter)),
                NumberHandling: p.NumberHandling,
                Order: p.Order,
                ObjectCreationHandling: p.ObjectCreationHandling,
                ConditionalIgnore: p.ShouldSerialize is not null);

            if (!signatures.TryAdd(memberName, signature))
            {
                throw new InvalidOperationException(
                    $"{record.Name}: System.Text.Json maps two members named {memberName} (wire "
                    + $"{signatures[memberName].Wire} and {p.Name}); the census keys by CLR member name.");
            }
        }

        return signatures;
    }

    /// <summary>
    /// Describes every difference between <paramref name="rendered"/> (the golden's
    /// public-property-only view) and <paramref name="stj"/> (System.Text.Json's own
    /// mapped-member view) for <paramref name="recordName"/>, naming each member
    /// present in one set but not the other and stating why: a member STJ maps that
    /// the golden's public-property scan cannot show (a <c>[JsonInclude]</c> field or
    /// non-public <c>[JsonInclude]</c> member), or a rendered public property STJ does not map to the wire
    /// (e.g. <c>[JsonIgnore]</c>) — plus any member present in both whose <see cref="MemberSignature"/>
    /// disagrees between the two views on ANY field: wire name, CLR type, required, extension-data
    /// (#578), or converter, number handling, property order, object-creation handling, and
    /// conditional-ignore (#586). Returns an empty list when the two sets are identical.
    /// </summary>
    private static List<string> DescribeMemberSetDifferences(
        string recordName,
        IReadOnlyDictionary<string, MemberSignature> rendered,
        IReadOnlyDictionary<string, MemberSignature> stj)
    {
        var differences = new List<string>();

        foreach (var memberName in stj.Keys.Except(rendered.Keys, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal))
        {
            differences.Add(
                $"{recordName}.{memberName}: System.Text.Json maps this member to the wire "
                + $"(wire={stj[memberName].Wire}) but the golden's public-property scan cannot "
                + "show it — a [JsonInclude] field or a non-public [JsonInclude] member.");
        }

        foreach (var memberName in rendered.Keys.Except(stj.Keys, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal))
        {
            differences.Add(
                $"{recordName}.{memberName}: the golden renders this public property "
                + $"(wire={rendered[memberName].Wire}) but System.Text.Json does not map it to "
                + "the wire under the shared Options — e.g. [JsonIgnore].");
        }

        foreach (var memberName in rendered.Keys.Intersect(stj.Keys, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal))
        {
            var renderedSignature = rendered[memberName];
            var stjSignature = stj[memberName];
            if (!renderedSignature.Equals(stjSignature))
            {
                differences.Add(
                    $"{recordName}.{memberName}: golden-rendered {renderedSignature} does not "
                    + $"match System.Text.Json's mapped {stjSignature}.");
            }
        }

        return differences;
    }

    /// <summary>
    /// Census gate (#578): every frozen record's STJ-mapped member set (per
    /// <see cref="GetStjMappedMemberSignatures"/>) must equal its golden-rendered
    /// member set (per <see cref="GetRenderedMemberSignatures"/>). The golden text
    /// above freezes only PUBLIC INSTANCE PROPERTIES; this test closes the gap for
    /// members System.Text.Json ALSO maps but the golden cannot show — a public field
    /// carrying <c>[JsonInclude]</c>, or a non-public property or field carrying
    /// <c>[JsonInclude]</c> — so such a member added to a frozen record fails HERE,
    /// named, even though it moves no golden text.
    /// </summary>
    [Fact]
    public void EventWireContract_Census_MatchesStjMappedMembers()
    {
        var differences = new List<string>();

        foreach (var record in s_eventRecords)
        {
            var rendered = GetRenderedMemberSignatures(record);
            var stj = GetStjMappedMemberSignatures(record);
            differences.AddRange(DescribeMemberSetDifferences(record.Name, rendered, stj));
        }

        Assert.True(
            differences.Count == 0,
            "The v1 event-wire census (#578/#586) found a member System.Text.Json maps that the "
            + "golden's public-property-only render cannot show (or vice versa), or a member whose "
            + "MemberSignature differs between the two views on ANY field — wire name, CLR type, "
            + "required-ness, extension-data (#578), or converter, number handling, property order, "
            + "object-creation handling, conditional-ignore (#586). This means a [JsonInclude] field, "
            + "a non-public [JsonInclude] member, a [JsonIgnore] on a rendered property, a "
            + "required-flag change, or a representation-affecting attribute (converter/number "
            + "handling/order/object-creation handling/conditional ignore) could change the wire "
            + "shape of a frozen record WITHOUT moving "
            + "Golden/event-stream-wire-contract.v1.txt — the property-only golden gate would "
            + "stay green while the wire contract drifted."
            + Environment.NewLine
            + string.Join(Environment.NewLine, differences));
    }

    /// <summary>
    /// Private record used ONLY to prove <see cref="DescribeMemberSetDifferences"/> can
    /// see what the golden's public-property scan structurally cannot (#578): a
    /// <c>[JsonInclude]</c> field (<see cref="Hidden"/>) and an <c>internal</c>
    /// <c>[JsonInclude]</c> property (<see cref="Secret"/>) are both members System.Text.Json
    /// maps to the wire, and neither is a public instance property, so
    /// <see cref="GetRenderedMemberSignatures"/> (mirroring the golden renderer) cannot
    /// see either one while <see cref="GetStjMappedMemberSignatures"/> sees both. Two more
    /// members cover the other branches: <see cref="Dropped"/> (<c>[JsonIgnore]</c>) is a
    /// public property the golden renders but STJ does not map, and <see cref="Tightened"/>
    /// (<c>[JsonRequired]</c> without the C# <see langword="required"/> modifier) is mapped on
    /// both sides with a differing required flag.
    /// </summary>
    /// <remarks>
    /// #586 adds one member per new representation branch — see
    /// <see cref="Census_Detects_MemberLevelRepresentationAttributes"/>, which proves each is
    /// rendered AND independently detected by System.Text.Json under the shared
    /// <see cref="EventStreamJson.Options"/>, and that the two sides agree — plus one member per
    /// converter-resolution shape, proven by <see cref="Census_Detects_NullableConverters"/> and
    /// <see cref="Census_Detects_ConverterFactories_OnPlainAndNullableMembers"/>.
    /// </remarks>
    private sealed record CensusProbeRecord
    {
        public required string RunId { get; init; }

        [JsonInclude]
        public required string Hidden = string.Empty;

        [JsonInclude]
        internal string Secret { get; init; } = string.Empty;

        [JsonIgnore]
        public string? Dropped { get; init; }

        [JsonRequired]
        public string? Tightened { get; init; }

        [JsonConverter(typeof(CensusProbeUpperCaseConverter))]
        public string Converted { get; init; } = string.Empty;

        [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
        public int NumberHandled { get; init; }

        [JsonPropertyOrder(7)]
        public string Ordered { get; init; } = string.Empty;

        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public List<string> Populated { get; init; } = new();

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ConditionallyIgnored { get; init; }

        // #586: a NON-factory converter declared for the member's own NULLABLE VALUE TYPE
        // directly (JsonConverter<int?>, not JsonConverter<int>) — measured (file header,
        // CONVERTER NAMES note) to need NO unwrapping: STJ reports it on
        // JsonPropertyInfo.CustomConverter unwrapped, because the converter already matches the
        // member's own type. Proves ResolveMemberConverterTypeName/UnwrapNullableConverter
        // agree for the COMMON case, distinct
        // from NullableUnderlyingTypeConverted below (a converter for the UNDERLYING type,
        // which DOES get wrapped).
        [JsonConverter(typeof(CensusProbeNullableIntConverter))]
        public int? NullableConverted { get; init; }

        // #586: a NON-factory converter declared for the member's NON-nullable UNDERLYING type
        // (JsonConverter<int>) applied to a Nullable<int> member — measured (file header,
        // CONVERTER NAMES note) to be WRAPPED by STJ in
        // NullableConverter<Int32>, requiring UnwrapNullableConverter to agree with the
        // rendered side.
        [JsonConverter(typeof(CensusProbeNonNullableIntConverter))]
        public int? NullableUnderlyingTypeConverted { get; init; }

        // #586: a JsonConverterFactory (JsonStringEnumConverter) applied to a PLAIN enum member —
        // measured (file header, CONVERTER NAMES note) to be kept UNEXPANDED by STJ, because the
        // factory's CanConvert(CensusProbeEnum) is already true: CustomConverter is the factory
        // itself, so ResolveMemberConverterTypeName must NOT expand it.
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CensusProbeEnum PlainFactoryConverted { get; init; }

        // #586: the same factory applied to a NULLABLE enum member — measured (file header,
        // CONVERTER NAMES note) to be EXPANDED by STJ against the underlying enum to its created
        // converter (the internal EnumConverter<TEnum>), then wrapped in NullableConverter<T>, so
        // ResolveMemberConverterTypeName MUST expand it to agree with the unwrapped STJ side.
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CensusProbeEnum? NullableFactoryConverted { get; init; }
    }

    /// <summary>
    /// Test-only enum, used by the converter-factory probes (<see cref="CensusProbeRecord"/>,
    /// <see cref="ConverterFactorySwapProbeRecord"/>).
    /// </summary>
    private enum CensusProbeEnum
    {
        A,
        B,
    }

    /// <summary>
    /// Trivial pass-through converter, used only to give <see cref="CensusProbeRecord.Converted"/>
    /// a real member-level <c>[JsonConverter]</c> to detect (#586). Its actual read/write
    /// behaviour is irrelevant — this file only reflects over <c>JsonPropertyInfo</c>, never
    /// serialises a <see cref="CensusProbeRecord"/>.
    /// </summary>
    private sealed class CensusProbeUpperCaseConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() ?? string.Empty;

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    /// <summary>
    /// A converter FOR <see cref="Nullable{T}"/> (<c>int?</c>) directly — the NON-wrapped case;
    /// see <see cref="CensusProbeRecord.NullableConverted"/>.
    /// </summary>
    private sealed class CensusProbeNullableIntConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : reader.GetInt32();

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteNumberValue(value.Value);
            }
        }
    }

    /// <summary>
    /// A converter for <c>int</c> (the NON-nullable underlying type) applied to a
    /// <see cref="Nullable{T}"/> member — the WRAPPED case; see
    /// <see cref="CensusProbeRecord.NullableUnderlyingTypeConverted"/>.
    /// </summary>
    private sealed class CensusProbeNonNullableIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetInt32();

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
    }

    /// <summary>
    /// Probe test (#586): proves the new #586 <see cref="MemberSignature"/> fields are both
    /// rendered into the golden (<see cref="FormatProperty"/>) and independently detected by
    /// System.Text.Json under the shared <see cref="EventStreamJson.Options"/>
    /// (<see cref="GetStjMappedMemberSignatures"/>), and that the two sides AGREE — the same
    /// comparison <see cref="EventWireContract_Census_MatchesStjMappedMembers"/> makes for
    /// every frozen record every day. Unlike the #578 probes above, each of these new fields is
    /// attribute-driven on BOTH sides (the rendered side reads the .NET attribute directly; the
    /// STJ side reads the fact STJ derived from the same attribute), so agreement — not a
    /// reported difference — is the expected, asserted outcome.
    /// </summary>
    [Fact]
    public void Census_Detects_MemberLevelRepresentationAttributes()
    {
        var rendered = GetRenderedMemberSignatures(typeof(CensusProbeRecord));
        var stj = GetStjMappedMemberSignatures(typeof(CensusProbeRecord));

        var converterProp = typeof(CensusProbeRecord).GetProperty(nameof(CensusProbeRecord.Converted))!;
        Assert.Equal(typeof(CensusProbeUpperCaseConverter).FullName, rendered["Converted"].ConverterTypeName);
        Assert.Equal(rendered["Converted"].ConverterTypeName, stj["Converted"].ConverterTypeName);
        Assert.Contains(
            $"[converter={typeof(CensusProbeUpperCaseConverter).FullName}]",
            FormatProperty(converterProp),
            StringComparison.Ordinal);

        var numberHandledProp = typeof(CensusProbeRecord).GetProperty(nameof(CensusProbeRecord.NumberHandled))!;
        Assert.Equal(JsonNumberHandling.WriteAsString, rendered["NumberHandled"].NumberHandling);
        Assert.Equal(rendered["NumberHandled"].NumberHandling, stj["NumberHandled"].NumberHandling);
        Assert.Contains("[numberHandling=WriteAsString]", FormatProperty(numberHandledProp), StringComparison.Ordinal);

        var orderedProp = typeof(CensusProbeRecord).GetProperty(nameof(CensusProbeRecord.Ordered))!;
        Assert.Equal(7, rendered["Ordered"].Order);
        Assert.Equal(rendered["Ordered"].Order, stj["Ordered"].Order);
        Assert.Contains("[order=7]", FormatProperty(orderedProp), StringComparison.Ordinal);

        var populatedProp = typeof(CensusProbeRecord).GetProperty(nameof(CensusProbeRecord.Populated))!;
        Assert.Equal(JsonObjectCreationHandling.Populate, rendered["Populated"].ObjectCreationHandling);
        Assert.Equal(rendered["Populated"].ObjectCreationHandling, stj["Populated"].ObjectCreationHandling);
        Assert.Contains("[objectCreationHandling=Populate]", FormatProperty(populatedProp), StringComparison.Ordinal);

        var conditionallyIgnoredProp =
            typeof(CensusProbeRecord).GetProperty(nameof(CensusProbeRecord.ConditionallyIgnored))!;
        Assert.True(rendered["ConditionallyIgnored"].ConditionalIgnore);
        Assert.Equal(rendered["ConditionallyIgnored"].ConditionalIgnore, stj["ConditionallyIgnored"].ConditionalIgnore);
        Assert.Contains("[ignoreCondition=WhenWritingNull]", FormatProperty(conditionallyIgnoredProp), StringComparison.Ordinal);

        // Neither side sees ANY of these as a rendered-vs-stj mismatch — both sides agree,
        // which is the outcome the real gate depends on for every frozen record.
        var differences = DescribeMemberSetDifferences(nameof(CensusProbeRecord), rendered, stj);
        Assert.DoesNotContain(differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.Converted", StringComparison.Ordinal));
        Assert.DoesNotContain(differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.NumberHandled", StringComparison.Ordinal));
        Assert.DoesNotContain(differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.Ordered", StringComparison.Ordinal));
        Assert.DoesNotContain(differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.Populated", StringComparison.Ordinal));
        Assert.DoesNotContain(
            differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.ConditionallyIgnored", StringComparison.Ordinal));
    }

    /// <summary>
    /// Probe (#586): proves the census agrees on both non-factory nullable-converter shapes —
    /// <see cref="CensusProbeRecord.NullableConverted"/> (a converter FOR the nullable type,
    /// never wrapped) and <see cref="CensusProbeRecord.NullableUnderlyingTypeConverted"/> (a
    /// converter for the underlying type, WRAPPED by System.Text.Json in
    /// <c>NullableConverter&lt;Int32&gt;</c> — needs <see cref="UnwrapNullableConverter"/> to
    /// agree, so disabling the unwrap turns this probe red). The factory shapes are proven by
    /// <see cref="Census_Detects_ConverterFactories_OnPlainAndNullableMembers"/>.
    /// </summary>
    [Fact]
    public void Census_Detects_NullableConverters()
    {
        var rendered = GetRenderedMemberSignatures(typeof(CensusProbeRecord));
        var stj = GetStjMappedMemberSignatures(typeof(CensusProbeRecord));
        var differences = DescribeMemberSetDifferences(nameof(CensusProbeRecord), rendered, stj);

        // Non-factory converter FOR the nullable type itself — never wrapped.
        Assert.Equal(
            typeof(CensusProbeNullableIntConverter).FullName, rendered["NullableConverted"].ConverterTypeName);
        Assert.Equal(rendered["NullableConverted"].ConverterTypeName, stj["NullableConverted"].ConverterTypeName);
        Assert.DoesNotContain(
            differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.NullableConverted", StringComparison.Ordinal));

        // Non-factory converter for the UNDERLYING type — WRAPPED by STJ; needs unwrapping to agree.
        Assert.Equal(
            typeof(CensusProbeNonNullableIntConverter).FullName,
            rendered["NullableUnderlyingTypeConverted"].ConverterTypeName);
        Assert.Equal(
            rendered["NullableUnderlyingTypeConverted"].ConverterTypeName,
            stj["NullableUnderlyingTypeConverted"].ConverterTypeName);
        Assert.DoesNotContain(
            differences,
            d => d.StartsWith($"{nameof(CensusProbeRecord)}.NullableUnderlyingTypeConverted", StringComparison.Ordinal));
    }

    /// <summary>
    /// The formatted name of System.Text.Json's internal <c>EnumConverter&lt;CensusProbeEnum&gt;</c>
    /// — the converter <see cref="JsonStringEnumConverter"/> (and any subclass of it) creates for
    /// <see cref="CensusProbeEnum"/>. Spelled out rather than reflected, because the type is
    /// internal to System.Text.Json: measured (STJ 8.0.0.0) as
    /// <c>System.Text.Json.Serialization.Converters.EnumConverter`1[TEnum]</c>.
    /// </summary>
    private static readonly string s_createdEnumConverterName =
        $"System.Text.Json.Serialization.Converters.EnumConverter<{typeof(CensusProbeEnum).FullName}>";

    /// <summary>
    /// Probe (#586): proves the census mirrors System.Text.Json's own resolution of a
    /// <see cref="JsonConverterFactory"/> on BOTH member shapes, and that the golden line shows
    /// the DECLARED factory on both:
    /// <see cref="CensusProbeRecord.PlainFactoryConverted"/> (plain enum member — System.Text.Json
    /// keeps the factory UNEXPANDED, so a rendered side that always expands turns this red) and
    /// <see cref="CensusProbeRecord.NullableFactoryConverted"/> (nullable enum member —
    /// System.Text.Json expands the factory, then wraps; a rendered side that never expands, or
    /// an STJ side that does not unwrap, turns this red).
    /// </summary>
    [Fact]
    public void Census_Detects_ConverterFactories_OnPlainAndNullableMembers()
    {
        var rendered = GetRenderedMemberSignatures(typeof(CensusProbeRecord));
        var stj = GetStjMappedMemberSignatures(typeof(CensusProbeRecord));
        var differences = DescribeMemberSetDifferences(nameof(CensusProbeRecord), rendered, stj);
        var declaredFactoryMarker = $"[converter={typeof(JsonStringEnumConverter).FullName}]";

        // Plain member: the factory itself, on both sides.
        Assert.Equal(typeof(JsonStringEnumConverter).FullName, stj["PlainFactoryConverted"].ConverterTypeName);
        Assert.Equal(stj["PlainFactoryConverted"].ConverterTypeName, rendered["PlainFactoryConverted"].ConverterTypeName);
        Assert.DoesNotContain(
            differences,
            d => d.StartsWith($"{nameof(CensusProbeRecord)}.PlainFactoryConverted", StringComparison.Ordinal));
        Assert.Contains(
            declaredFactoryMarker,
            FormatProperty(typeof(CensusProbeRecord).GetProperty(nameof(CensusProbeRecord.PlainFactoryConverted))!),
            StringComparison.Ordinal);

        // Nullable member: the factory's created converter, on both sides (the STJ side after
        // unwrapping NullableConverter<T>) — yet the golden line still shows the declared factory.
        Assert.Equal(s_createdEnumConverterName, stj["NullableFactoryConverted"].ConverterTypeName);
        Assert.Equal(
            stj["NullableFactoryConverted"].ConverterTypeName,
            rendered["NullableFactoryConverted"].ConverterTypeName);
        Assert.DoesNotContain(
            differences,
            d => d.StartsWith($"{nameof(CensusProbeRecord)}.NullableFactoryConverted", StringComparison.Ordinal));
        Assert.Contains(
            declaredFactoryMarker,
            FormatProperty(typeof(CensusProbeRecord).GetProperty(nameof(CensusProbeRecord.NullableFactoryConverted))!),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A <see cref="JsonStringEnumConverter"/> subclass that differs only in its naming policy.
    /// On a nullable enum member it creates the SAME internal converter type as
    /// <see cref="JsonStringEnumConverter"/> itself, but writes camel-cased names — used only by
    /// <see cref="ConverterFactorySwapProbeRecord"/>.
    /// </summary>
    private sealed class CensusProbeCamelCaseEnumConverter : JsonStringEnumConverter
    {
        public CensusProbeCamelCaseEnumConverter()
            : base(JsonNamingPolicy.CamelCase)
        {
        }
    }

    /// <summary>
    /// Probe record (#586): the same nullable enum member shape under two different factories
    /// that create converters of the same type — the swap a census comparing resolved converter
    /// types alone cannot see.
    /// </summary>
    private sealed record ConverterFactorySwapProbeRecord
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CensusProbeEnum? Standard { get; init; }

        [JsonConverter(typeof(CensusProbeCamelCaseEnumConverter))]
        public CensusProbeEnum? CamelCase { get; init; }
    }

    /// <summary>
    /// Probe (#586): swapping one converter factory for another that creates a converter of the
    /// SAME type changes the bytes on the wire while leaving both census sides unchanged, so the
    /// golden line — which renders the DECLARED factory — must be what makes the swap visible.
    /// A golden that rendered the created converter instead would show identical markers for
    /// both members, and this probe turns red.
    /// </summary>
    [Fact]
    public void Golden_RendersDeclaredFactory_SoAFactorySwapIsVisible()
    {
        var standardProp = typeof(ConverterFactorySwapProbeRecord)
            .GetProperty(nameof(ConverterFactorySwapProbeRecord.Standard))!;
        var camelCaseProp = typeof(ConverterFactorySwapProbeRecord)
            .GetProperty(nameof(ConverterFactorySwapProbeRecord.CamelCase))!;

        // The swap is real on the wire (measured bytes, not an assumption).
        var line = JsonSerializer.Serialize(
            new ConverterFactorySwapProbeRecord { Standard = CensusProbeEnum.B, CamelCase = CensusProbeEnum.B },
            EventStreamJson.Options);
        Assert.Equal("{\"Standard\":\"B\",\"CamelCase\":\"b\"}", line);

        // Both census sides report the same created converter type for both members, and agree.
        var rendered = GetRenderedMemberSignatures(typeof(ConverterFactorySwapProbeRecord));
        var stj = GetStjMappedMemberSignatures(typeof(ConverterFactorySwapProbeRecord));
        Assert.Equal(s_createdEnumConverterName, stj["Standard"].ConverterTypeName);
        Assert.Equal(s_createdEnumConverterName, stj["CamelCase"].ConverterTypeName);
        Assert.Empty(DescribeMemberSetDifferences(nameof(ConverterFactorySwapProbeRecord), rendered, stj));

        // So only the golden can tell them apart: each line names its own DECLARED factory.
        var standardLine = FormatProperty(standardProp);
        var camelCaseLine = FormatProperty(camelCaseProp);
        Assert.Contains($"[converter={typeof(JsonStringEnumConverter).FullName}]", standardLine, StringComparison.Ordinal);
        Assert.Contains(
            $"[converter={typeof(CensusProbeCamelCaseEnumConverter).FullName}]", camelCaseLine, StringComparison.Ordinal);
        Assert.DoesNotContain(s_createdEnumConverterName, standardLine, StringComparison.Ordinal);
        Assert.DoesNotContain(s_createdEnumConverterName, camelCaseLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <see cref="JsonConverterAttribute"/> SUBCLASS that overrides
    /// <see cref="JsonConverterAttribute.CreateConverter(Type)"/> instead of calling the base
    /// constructor with a converter <see cref="Type"/> — a legitimate, documented STJ pattern
    /// (e.g. for a converter chosen at runtime). MEASURED (STJ 8.0.0.0): such a subclass
    /// instance's own <see cref="JsonConverterAttribute.ConverterType"/> property is
    /// <see langword="null"/> — there is no <see cref="Type"/> to read off the ATTRIBUTE — while
    /// System.Text.Json itself calls <see cref="JsonConverterAttribute.CreateConverter(Type)"/>
    /// polymorphically and gets a REAL converter. Used ONLY by
    /// <see cref="Census_Detects_JsonConverterAttributeSubclass_ConverterTypeNull_AsGenuineMismatch"/>.
    /// </summary>
    private sealed class NullConverterTypePassthroughAttribute : JsonConverterAttribute
    {
        public override JsonConverter? CreateConverter(Type typeToConvert) => new CensusProbeUpperCaseConverter();
    }

    /// <summary>
    /// Probe (#586): a member carrying <see cref="NullConverterTypePassthroughAttribute"/>
    /// (above). Proves <see cref="DescribeMemberSetDifferences"/> reports a GENUINE, NATURALLY
    /// OCCURRING mismatch — no synthetic perturbation needed — and catches a mutant that reads
    /// the STJ side's converter name back off the same attribute instead of off System.Text.Json's
    /// own resolved <c>JsonPropertyInfo.CustomConverter</c>, which would make BOTH sides read
    /// the same null and never disagree.
    /// </summary>
    private sealed record ConverterAttributeNullTypeProbeRecord
    {
        [NullConverterTypePassthrough]
        public string Passthrough { get; init; } = string.Empty;
    }

    [Fact]
    public void Census_Detects_JsonConverterAttributeSubclass_ConverterTypeNull_AsGenuineMismatch()
    {
        var prop = typeof(ConverterAttributeNullTypeProbeRecord)
            .GetProperty(nameof(ConverterAttributeNullTypeProbeRecord.Passthrough))!;
        var attr = prop.GetCustomAttribute<JsonConverterAttribute>();
        Assert.NotNull(attr);
        Assert.Null(attr!.ConverterType);

        var rendered = GetRenderedMemberSignatures(typeof(ConverterAttributeNullTypeProbeRecord));
        var stj = GetStjMappedMemberSignatures(typeof(ConverterAttributeNullTypeProbeRecord));

        Assert.Null(rendered["Passthrough"].ConverterTypeName);
        Assert.NotNull(stj["Passthrough"].ConverterTypeName);

        var differences = DescribeMemberSetDifferences(
            nameof(ConverterAttributeNullTypeProbeRecord), rendered, stj);

        Assert.Contains(
            differences,
            d => d.StartsWith($"{nameof(ConverterAttributeNullTypeProbeRecord)}.Passthrough", StringComparison.Ordinal)
                && d.Contains("does not match", StringComparison.Ordinal));
    }

    /// <summary>
    /// Negative test (#578): proves the census in
    /// <see cref="EventWireContract_Census_MatchesStjMappedMembers"/> is load-bearing —
    /// it can see a member the golden's public-property scan structurally cannot. Both
    /// <see cref="CensusProbeRecord.Hidden"/> (a <c>[JsonInclude]</c> field) and
    /// <see cref="CensusProbeRecord.Secret"/> (a non-public <c>[JsonInclude]</c>
    /// property) must be named as STJ-mapped-but-unrendered differences;
    /// <see cref="CensusProbeRecord.Dropped"/> (<c>[JsonIgnore]</c>) as rendered-but-unmapped;
    /// <see cref="CensusProbeRecord.Tightened"/> (<c>[JsonRequired]</c>) as a signature
    /// mismatch; and <c>RunId</c>, identical on both sides, must not be reported.
    /// </summary>
    [Fact]
    public void Census_Detects_JsonIncludeFieldAndNonPublicMember_GoldenCannotShow()
    {
        var rendered = GetRenderedMemberSignatures(typeof(CensusProbeRecord));
        var stj = GetStjMappedMemberSignatures(typeof(CensusProbeRecord));

        var differences = DescribeMemberSetDifferences(nameof(CensusProbeRecord), rendered, stj);

        Assert.Contains(
            differences,
            d => d.Contains($"{nameof(CensusProbeRecord)}.Hidden", StringComparison.Ordinal)
                && d.Contains("[JsonInclude] field", StringComparison.Ordinal));
        Assert.Contains(
            differences,
            d => d.Contains($"{nameof(CensusProbeRecord)}.Secret", StringComparison.Ordinal)
                && d.Contains("non-public [JsonInclude] member", StringComparison.Ordinal));
        Assert.Contains(
            differences,
            d => d.Contains($"{nameof(CensusProbeRecord)}.Dropped", StringComparison.Ordinal)
                && d.Contains("does not map it to the wire", StringComparison.Ordinal));
        Assert.Contains(
            differences,
            d => d.Contains($"{nameof(CensusProbeRecord)}.Tightened", StringComparison.Ordinal)
                && d.Contains("does not match", StringComparison.Ordinal));

        // RunId is mapped identically on both sides (public required property on both
        // views) — it must NOT be reported as a difference. Guards against a helper
        // that reports every member as differing rather than only the true gaps.
        Assert.DoesNotContain(differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.RunId", StringComparison.Ordinal));
    }

    // ── Type-level census (#586): STJ-mapped vs. golden-rendered TYPE signature ──

    /// <summary>
    /// A record TYPE's wire-relevant representation signature — as opposed to
    /// <see cref="MemberSignature"/>, which is per-MEMBER. Like
    /// <see cref="MemberSignature.ConverterTypeName"/>, <see cref="ConverterTypeName"/> names the
    /// resolved converter's TYPE, not its configuration; the golden header's declared-type
    /// marker is what distinguishes two factories creating converters of the same type.
    /// <see cref="PolymorphicDerivedTypes"/> is the same <c>"Type:tag;Type:tag"</c> joined,
    /// sorted string <see cref="FormatRecordHeader"/> renders — not a list — because
    /// <c>record struct</c> equality on a reference-type member (a <see cref="List{T}"/> or
    /// similar) would compare by REFERENCE, not content, making two independently-built
    /// signatures with identical derived-type sets compare unequal. The other
    /// <c>Polymorphic*</c> fields beyond the derived-type list (#586) are value
    /// types (a nullable string, a nullable enum, a nullable bool), so they need no such
    /// joining — <c>record struct</c> equality on them is exact by construction.
    /// </summary>
    private readonly record struct TypeSignature(
        string? ConverterTypeName,
        JsonNumberHandling? NumberHandling,
        JsonUnmappedMemberHandling? UnmappedMemberHandling,
        JsonObjectCreationHandling? ObjectCreationHandling,
        string? PolymorphicDerivedTypes,
        string? PolymorphicTypeDiscriminatorPropertyName,
        JsonUnknownDerivedTypeHandling? PolymorphicUnknownDerivedTypeHandling,
        bool? PolymorphicIgnoreUnrecognizedTypeDiscriminators);

    /// <summary>
    /// The rendered type-level signature for <paramref name="record"/>: reads the same
    /// type-level attributes <see cref="FormatRecordHeader"/> renders, directly off
    /// <paramref name="record"/>'s own attributes (<c>inherit: false</c> — #586;
    /// see the file header's INHERITANCE note). Shared getters keep the golden
    /// header and this signature from independently drifting on what counts as present.
    /// </summary>
    private static TypeSignature GetRenderedTypeSignature(Type record)
    {
        var derived = GetDeclaredDerivedTypeTokens(record);
        (string Discriminator, JsonUnknownDerivedTypeHandling UnknownHandling, bool IgnoreUnrecognized)? polySettings =
            derived is null ? null : GetDeclaredPolymorphicSettings(record);

        return new TypeSignature(
            ConverterTypeName: ResolveTypeConverterTypeName(GetDeclaredTypeConverterType(record), record),
            NumberHandling: record.GetCustomAttribute<JsonNumberHandlingAttribute>(inherit: false)?.Handling,
            UnmappedMemberHandling:
                record.GetCustomAttribute<JsonUnmappedMemberHandlingAttribute>(inherit: false)?.UnmappedMemberHandling,
            ObjectCreationHandling:
                record.GetCustomAttribute<JsonObjectCreationHandlingAttribute>(inherit: false)?.Handling,
            PolymorphicDerivedTypes: derived is null ? null : string.Join(";", derived),
            PolymorphicTypeDiscriminatorPropertyName: polySettings?.Discriminator,
            PolymorphicUnknownDerivedTypeHandling: polySettings?.UnknownHandling,
            PolymorphicIgnoreUnrecognizedTypeDiscriminators: polySettings?.IgnoreUnrecognized);
    }

    /// <summary>
    /// The STJ-mapped type-level signature for <paramref name="record"/>, reflected from
    /// <see cref="EventStreamJson.Options"/>'s own <c>JsonTypeInfo</c>.
    /// </summary>
    /// <remarks>
    /// MEASURED (STJ 8.0.0.0): <c>JsonTypeInfo.Converter</c> is NON-NULL for every reflected
    /// type — including every frozen record, which reports STJ's own internal
    /// <c>ObjectDefaultConverter&lt;T&gt;</c> or
    /// <c>SmallObjectWithParameterizedConstructorConverter&lt;…&gt;</c> depending on whether the
    /// type binds through a parameterless or parameterized constructor — so it cannot be
    /// compared unconditionally without reporting a false positive on every frozen record. Only
    /// a type carrying an explicit type-level <c>[JsonConverter]</c> (or an equally-scoped
    /// GLOBAL converter registration, though none of <see cref="EventStreamJson.Options"/>'
    /// <c>Converters</c> targets a record type today) reports
    /// <c>Kind == JsonTypeInfoKind.None</c>, with <c>Properties</c> left EMPTY — the converter
    /// owns the whole contract. Gating on <c>Kind == None</c> is what makes the comparison safe:
    /// no frozen record reports it today.
    /// </remarks>
    private static TypeSignature GetStjTypeSignature(Type record)
    {
        var typeInfo = EventStreamJson.Options.GetTypeInfo(record);

        var converterTypeName = typeInfo.Kind == JsonTypeInfoKind.None
            ? FormatConverterInstanceTypeName(typeInfo.Converter)
            : null;

        var poly = typeInfo.PolymorphismOptions;
        var polymorphicDerivedTypes = poly is null
            ? null
            : string.Join(
                ";",
                poly.DerivedTypes
                    .Select(dt => $"{dt.DerivedType.FullName}:{dt.TypeDiscriminator}")
                    .OrderBy(s => s, StringComparer.Ordinal));

        return new TypeSignature(
            ConverterTypeName: converterTypeName,
            NumberHandling: typeInfo.NumberHandling,
            UnmappedMemberHandling: typeInfo.UnmappedMemberHandling,
            ObjectCreationHandling: typeInfo.PreferredPropertyObjectCreationHandling,
            PolymorphicDerivedTypes: polymorphicDerivedTypes,
            PolymorphicTypeDiscriminatorPropertyName: poly?.TypeDiscriminatorPropertyName,
            PolymorphicUnknownDerivedTypeHandling: poly?.UnknownDerivedTypeHandling,
            PolymorphicIgnoreUnrecognizedTypeDiscriminators: poly?.IgnoreUnrecognizedTypeDiscriminators);
    }

    /// <summary>
    /// Describes the difference between <paramref name="rendered"/> and <paramref name="stj"/>
    /// for <paramref name="recordName"/>'s type-level signature, or an empty list when they
    /// agree. Mirrors <see cref="DescribeMemberSetDifferences"/> at the type level.
    /// </summary>
    /// <remarks>
    /// #586: the comparison is a plain
    /// <c>record struct</c> equality check (<c>!rendered.Equals(stj)</c>) — not an
    /// always-true or always-false expression. A mutation that widens this condition with a
    /// tautological <c>|| rendered != stj</c> (making it unconditionally
    /// <see langword="true"/> and this method unconditionally return an EMPTY list) is exactly
    /// the class of defect <see cref="DescribeTypeSignatureDifferences_ReportsAGenuineMismatch"/>
    /// below exists to catch: that probe fails red under it.
    /// </remarks>
    private static List<string> DescribeTypeSignatureDifferences(
        string recordName, TypeSignature rendered, TypeSignature stj)
    {
        if (!rendered.Equals(stj))
        {
            return new List<string>
            {
                $"{recordName}: golden-rendered type-level signature {rendered} does not match "
                    + $"System.Text.Json's mapped type-level signature {stj}.",
            };
        }

        return new List<string>();
    }

    /// <summary>
    /// Type-level census gate (#586): every frozen record's STJ-mapped TYPE signature (per
    /// <see cref="GetStjTypeSignature"/>) must equal its golden-rendered type signature (per
    /// <see cref="GetRenderedTypeSignature"/>). A type-level <c>[JsonConverter]</c> (which
    /// REPLACES the whole per-property contract), <c>[JsonNumberHandling]</c>,
    /// <c>[JsonUnmappedMemberHandling]</c>, <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c>, or
    /// <c>[JsonObjectCreationHandling]</c> added to a frozen RECORD TYPE fails HERE, since
    /// neither the golden nor the per-member census
    /// (<see cref="EventWireContract_Census_MatchesStjMappedMembers"/>) is scoped to see it.
    /// </summary>
    [Fact]
    public void EventWireContract_Census_MatchesStjTypeLevelSettings()
    {
        var differences = new List<string>();

        foreach (var record in s_eventRecords)
        {
            var rendered = GetRenderedTypeSignature(record);
            var stj = GetStjTypeSignature(record);
            differences.AddRange(DescribeTypeSignatureDifferences(record.Name, rendered, stj));
        }

        Assert.True(
            differences.Count == 0,
            "The v1 event-wire TYPE-LEVEL census (#586) found a frozen record whose type-level "
            + "representation disagrees between the golden-rendered attribute view and System.Text.Json's "
            + "own JsonTypeInfo under the shared EventStreamJson.Options — a type-level [JsonConverter], "
            + "[JsonNumberHandling], [JsonUnmappedMemberHandling], [JsonPolymorphic]/[JsonDerivedType], or "
            + "[JsonObjectCreationHandling] added to a frozen RECORD TYPE changes the wire without moving "
            + "the golden or the per-member census."
            + Environment.NewLine
            + string.Join(Environment.NewLine, differences));
    }

    /// <summary>
    /// #586: every derived type <paramref name="record"/> declares via
    /// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> must ITSELF be a member of
    /// <paramref name="frozenSet"/>, or its own members are invisible to every gate in this
    /// file. MEASURED: making a frozen record polymorphic freezes the LIST of derived-type
    /// names and discriminators (via <c>PolymorphicDerivedTypes</c> in
    /// <see cref="TypeSignature"/>) but nothing here separately walks INTO a derived type and
    /// freezes ITS OWN members — renaming a wire name on a derived type's own property passes
    /// every existing gate, because the derived type was never added to <c>s_eventRecords</c> in
    /// the first place. Returns one message per unfrozen derived type, or an empty list when
    /// <paramref name="record"/> is not polymorphic or every derived type is already frozen.
    /// </summary>
    private static List<string> DescribeUnfrozenDerivedTypes(Type record, HashSet<Type> frozenSet)
    {
        var typeInfo = EventStreamJson.Options.GetTypeInfo(record);
        if (typeInfo.PolymorphismOptions is null)
        {
            return new List<string>();
        }

        var unfrozen = new List<string>();
        foreach (var derivedType in typeInfo.PolymorphismOptions.DerivedTypes)
        {
            if (!frozenSet.Contains(derivedType.DerivedType))
            {
                unfrozen.Add(
                    $"{record.Name} declares derived type {derivedType.DerivedType.Name} "
                    + $"(discriminator \"{derivedType.TypeDiscriminator}\"), which is NOT itself listed in "
                    + "s_eventRecords — its own members are frozen by NEITHER the golden NOR either census.");
            }
        }

        return unfrozen;
    }

    /// <summary>
    /// Completeness guard (#586): pins that every derived type a frozen
    /// record declares is itself frozen. Without this, a polymorphic frozen record's own
    /// derived-type LIST is pinned (by <see cref="EventWireContract_Census_MatchesStjTypeLevelSettings"/>'s
    /// <c>PolymorphicDerivedTypes</c> field), but the derived type's OWN members are not: a
    /// wire-name rename on one of them changes the wire with every gate in this file staying
    /// green, because the derived type was never separately added to <see cref="s_eventRecords"/>.
    /// </summary>
    [Fact]
    public void EventWireContract_DerivedTypesAreThemselvesFrozen()
    {
        var frozenSet = new HashSet<Type>(s_eventRecords);
        var unfrozen = new List<string>();

        foreach (var record in s_eventRecords)
        {
            unfrozen.AddRange(DescribeUnfrozenDerivedTypes(record, frozenSet));
        }

        Assert.True(
            unfrozen.Count == 0,
            "A frozen record declares a derived type (#586) that is not itself frozen: add the "
            + "derived record to s_eventRecords AND regenerate the golden "
            + "(VOUCHFX_REGEN_EVENT_CONTRACT=1), then get the new golden lines reviewed — a "
            + "polymorphic record's derived-type LIST being frozen does not freeze the derived "
            + "type's OWN members."
            + Environment.NewLine
            + string.Join(Environment.NewLine, unfrozen));
    }

    // #586 probe types — a synthetic polymorphic record whose derived type
    // is deliberately NOT added to s_eventRecords, simulating exactly the vulnerability
    // EventWireContract_DerivedTypesAreThemselvesFrozen exists to catch.
    [JsonPolymorphic]
    [JsonDerivedType(typeof(UnfrozenDerivedProbeDerived), "d")]
    private abstract record UnfrozenDerivedProbeBase(string A);

    private sealed record UnfrozenDerivedProbeDerived(string A, string B) : UnfrozenDerivedProbeBase(A);

    /// <summary>
    /// Probe (#586): proves <see cref="DescribeUnfrozenDerivedTypes"/> reports a derived type
    /// missing from the frozen set, and reports nothing once it is added — the same comparison
    /// <see cref="EventWireContract_DerivedTypesAreThemselvesFrozen"/> makes for every frozen
    /// record every day. If the <c>!frozenSet.Contains(...)</c> check in
    /// <see cref="DescribeUnfrozenDerivedTypes"/> is removed, this probe's first assertion goes
    /// red.
    /// </summary>
    [Fact]
    public void DescribeUnfrozenDerivedTypes_ReportsDerivedTypeMissingFromFrozenSet()
    {
        var incompleteFrozenSet = new HashSet<Type> { typeof(UnfrozenDerivedProbeBase) };
        var unfrozen = DescribeUnfrozenDerivedTypes(typeof(UnfrozenDerivedProbeBase), incompleteFrozenSet);

        Assert.NotEmpty(unfrozen);
        Assert.Contains(unfrozen, d => d.Contains(nameof(UnfrozenDerivedProbeDerived), StringComparison.Ordinal));

        var completeFrozenSet = new HashSet<Type>
        {
            typeof(UnfrozenDerivedProbeBase),
            typeof(UnfrozenDerivedProbeDerived),
        };
        Assert.Empty(DescribeUnfrozenDerivedTypes(typeof(UnfrozenDerivedProbeBase), completeFrozenSet));
    }

    // #586 type-level probe types — test-only, never produced by production code, exercised
    // solely to prove Census_Detects_TypeLevelRepresentationAttributes below is load-bearing.

    [JsonConverter(typeof(TypeLevelProbeConverter))]
    private sealed record TypeConverterProbeRecord(string A);

    private sealed class TypeLevelProbeConverter : JsonConverter<TypeConverterProbeRecord>
    {
        public override TypeConverterProbeRecord Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(string.Empty);

        public override void Write(
            Utf8JsonWriter writer, TypeConverterProbeRecord value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.A);
    }

    [JsonConverter(typeof(TypeLevelProbeConverterFactory))]
    private sealed record TypeFactoryConverterProbeRecord(string A);

    private sealed class TypeLevelProbeConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(TypeFactoryConverterProbeRecord);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            new TypeLevelProbeFactoryCreatedConverter();
    }

    private sealed class TypeLevelProbeFactoryCreatedConverter : JsonConverter<TypeFactoryConverterProbeRecord>
    {
        public override TypeFactoryConverterProbeRecord Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(string.Empty);

        public override void Write(
            Utf8JsonWriter writer, TypeFactoryConverterProbeRecord value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.A);
    }

    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    private sealed record TypeNumberHandlingProbeRecord(int N);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record TypeUnmappedMemberHandlingProbeRecord(string A);

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    private sealed record TypeObjectCreationHandlingProbeRecord
    {
        public List<string> Items { get; init; } = new();
    }

    [JsonPolymorphic]
    [JsonDerivedType(typeof(TypePolymorphicDerivedProbeRecord), "derived")]
    private abstract record TypePolymorphicBaseProbeRecord(string A);

    private sealed record TypePolymorphicDerivedProbeRecord(string A, string B)
        : TypePolymorphicBaseProbeRecord(A);

    /// <summary>
    /// Probe test (#586): proves each <see cref="TypeSignature"/> field is both
    /// rendered onto the golden's <c>"record &lt;Name&gt;"</c> header
    /// (<see cref="FormatRecordHeader"/>) and independently detected by System.Text.Json under
    /// the shared <see cref="EventStreamJson.Options"/> (<see cref="GetStjTypeSignature"/>), and
    /// that the two sides AGREE — the same comparison
    /// <see cref="EventWireContract_Census_MatchesStjTypeLevelSettings"/> makes for every frozen
    /// record every day.
    /// </summary>
    [Fact]
    public void Census_Detects_TypeLevelRepresentationAttributes()
    {
        var converterRendered = GetRenderedTypeSignature(typeof(TypeConverterProbeRecord));
        var converterStj = GetStjTypeSignature(typeof(TypeConverterProbeRecord));
        Assert.Equal(typeof(TypeLevelProbeConverter).FullName, converterRendered.ConverterTypeName);
        Assert.Equal(converterRendered.ConverterTypeName, converterStj.ConverterTypeName);
        Assert.Contains(
            $"[converter={typeof(TypeLevelProbeConverter).FullName}]",
            FormatRecordHeader(typeof(TypeConverterProbeRecord)),
            StringComparison.Ordinal);

        // A type-level FACTORY: System.Text.Json reports its CREATED converter (measured), so the
        // census expands it — while the golden header still shows the DECLARED factory.
        var factoryRendered = GetRenderedTypeSignature(typeof(TypeFactoryConverterProbeRecord));
        var factoryStj = GetStjTypeSignature(typeof(TypeFactoryConverterProbeRecord));
        Assert.Equal(typeof(TypeLevelProbeFactoryCreatedConverter).FullName, factoryStj.ConverterTypeName);
        Assert.Equal(factoryStj.ConverterTypeName, factoryRendered.ConverterTypeName);
        var factoryHeader = FormatRecordHeader(typeof(TypeFactoryConverterProbeRecord));
        Assert.Contains(
            $"[converter={typeof(TypeLevelProbeConverterFactory).FullName}]", factoryHeader, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(TypeLevelProbeFactoryCreatedConverter).FullName!, factoryHeader, StringComparison.Ordinal);

        var numberHandlingRendered = GetRenderedTypeSignature(typeof(TypeNumberHandlingProbeRecord));
        var numberHandlingStj = GetStjTypeSignature(typeof(TypeNumberHandlingProbeRecord));
        Assert.Equal(JsonNumberHandling.WriteAsString, numberHandlingRendered.NumberHandling);
        Assert.Equal(numberHandlingRendered.NumberHandling, numberHandlingStj.NumberHandling);
        Assert.Contains(
            "[numberHandling=WriteAsString]",
            FormatRecordHeader(typeof(TypeNumberHandlingProbeRecord)),
            StringComparison.Ordinal);

        var unmappedRendered = GetRenderedTypeSignature(typeof(TypeUnmappedMemberHandlingProbeRecord));
        var unmappedStj = GetStjTypeSignature(typeof(TypeUnmappedMemberHandlingProbeRecord));
        Assert.Equal(JsonUnmappedMemberHandling.Disallow, unmappedRendered.UnmappedMemberHandling);
        Assert.Equal(unmappedRendered.UnmappedMemberHandling, unmappedStj.UnmappedMemberHandling);
        Assert.Contains(
            "[unmappedMemberHandling=Disallow]",
            FormatRecordHeader(typeof(TypeUnmappedMemberHandlingProbeRecord)),
            StringComparison.Ordinal);

        var creationRendered = GetRenderedTypeSignature(typeof(TypeObjectCreationHandlingProbeRecord));
        var creationStj = GetStjTypeSignature(typeof(TypeObjectCreationHandlingProbeRecord));
        Assert.Equal(JsonObjectCreationHandling.Populate, creationRendered.ObjectCreationHandling);
        Assert.Equal(creationRendered.ObjectCreationHandling, creationStj.ObjectCreationHandling);
        Assert.Contains(
            "[objectCreationHandling=Populate]",
            FormatRecordHeader(typeof(TypeObjectCreationHandlingProbeRecord)),
            StringComparison.Ordinal);

        var polyRendered = GetRenderedTypeSignature(typeof(TypePolymorphicBaseProbeRecord));
        var polyStj = GetStjTypeSignature(typeof(TypePolymorphicBaseProbeRecord));
        var expectedDerived = $"{typeof(TypePolymorphicDerivedProbeRecord).FullName}:derived";
        Assert.Equal(expectedDerived, polyRendered.PolymorphicDerivedTypes);
        Assert.Equal(polyRendered.PolymorphicDerivedTypes, polyStj.PolymorphicDerivedTypes);
        Assert.Contains(
            $"[polymorphicDerivedTypes={expectedDerived}]",
            FormatRecordHeader(typeof(TypePolymorphicBaseProbeRecord)),
            StringComparison.Ordinal);

        // Both sides agree on every one of these — the outcome the real gate
        // (EventWireContract_Census_MatchesStjTypeLevelSettings) depends on for every
        // frozen record every day.
        Assert.Empty(DescribeTypeSignatureDifferences("probe", converterRendered, converterStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", factoryRendered, factoryStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", numberHandlingRendered, numberHandlingStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", unmappedRendered, unmappedStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", creationRendered, creationStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", polyRendered, polyStj));

        // #586: the settings beyond the derived-type list agree too, using
        // System.Text.Json's OWN measured defaults ("$type" / FailSerialization / false) rather
        // than the bare [JsonPolymorphic] attribute's own (different) property defaults — see
        // GetDeclaredPolymorphicSettings' remarks for the measured discrepancy this guards.
        Assert.Equal("$type", polyRendered.PolymorphicTypeDiscriminatorPropertyName);
        Assert.Equal(polyRendered.PolymorphicTypeDiscriminatorPropertyName, polyStj.PolymorphicTypeDiscriminatorPropertyName);
        Assert.Equal(JsonUnknownDerivedTypeHandling.FailSerialization, polyRendered.PolymorphicUnknownDerivedTypeHandling);
        Assert.Equal(polyRendered.PolymorphicUnknownDerivedTypeHandling, polyStj.PolymorphicUnknownDerivedTypeHandling);
        Assert.False(polyRendered.PolymorphicIgnoreUnrecognizedTypeDiscriminators);
        Assert.Equal(
            polyRendered.PolymorphicIgnoreUnrecognizedTypeDiscriminators,
            polyStj.PolymorphicIgnoreUnrecognizedTypeDiscriminators);
        Assert.Contains(
            "[polymorphicDiscriminator=$type] [polymorphicUnknownHandling=FailSerialization] "
                + "[polymorphicIgnoreUnrecognized=False]",
            FormatRecordHeader(typeof(TypePolymorphicBaseProbeRecord)),
            StringComparison.Ordinal);
    }

    // #586 — an unsealed record carrying ONLY [JsonDerivedType]
    // (no [JsonPolymorphic] attribute at all).
    [JsonDerivedType(typeof(JsonDerivedTypeAloneProbeDerived), "tag")]
    private abstract record JsonDerivedTypeAloneProbeBase(string A);

    private sealed record JsonDerivedTypeAloneProbeDerived(string A, string B)
        : JsonDerivedTypeAloneProbeBase(A);

    /// <summary>
    /// Probe (#586): proves <see cref="GetDeclaredDerivedTypeTokens"/>'s
    /// — <c>[JsonDerivedType]</c> PRESENCE, not <c>[JsonPolymorphic]</c> PRESENCE — is correct:
    /// <see cref="JsonDerivedTypeAloneProbeBase"/> carries ONLY <c>[JsonDerivedType]</c>, and both
    /// the rendered and the STJ views must still recognise it as polymorphic and agree on every
    /// polymorphism setting, using System.Text.Json's OWN measured defaults for the settings an
    /// explicit <see cref="JsonPolymorphicAttribute"/> would otherwise carry.
    /// </summary>
    [Fact]
    public void Census_Detects_DerivedTypeAloneAsPolymorphic_WithoutJsonPolymorphicAttribute()
    {
        var rendered = GetRenderedTypeSignature(typeof(JsonDerivedTypeAloneProbeBase));
        var stj = GetStjTypeSignature(typeof(JsonDerivedTypeAloneProbeBase));

        var expectedDerived = $"{typeof(JsonDerivedTypeAloneProbeDerived).FullName}:tag";
        Assert.Equal(expectedDerived, rendered.PolymorphicDerivedTypes);
        Assert.Equal("$type", rendered.PolymorphicTypeDiscriminatorPropertyName);
        Assert.Equal(JsonUnknownDerivedTypeHandling.FailSerialization, rendered.PolymorphicUnknownDerivedTypeHandling);
        Assert.False(rendered.PolymorphicIgnoreUnrecognizedTypeDiscriminators);
        Assert.Contains(
            $"[polymorphicDerivedTypes={expectedDerived}]",
            FormatRecordHeader(typeof(JsonDerivedTypeAloneProbeBase)),
            StringComparison.Ordinal);

        Assert.Empty(DescribeTypeSignatureDifferences("probe", rendered, stj));
    }

    /// <summary>
    /// Probe (#586): proves <see cref="DescribeTypeSignatureDifferences"/> DOES
    /// report a difference for genuinely different inputs, guarding against a mutant that widens
    /// the equality check to an always-<see langword="true"/> tautology — such a mutant would
    /// report no difference for anything, ever, no matter how many test records pass through it.
    /// Takes a REAL STJ-reflected
    /// <see cref="TypeSignature"/> (from <see cref="JsonDerivedTypeAloneProbeBase"/> above, already
    /// proven non-trivial by <see cref="Census_Detects_DerivedTypeAloneAsPolymorphic_WithoutJsonPolymorphicAttribute"/>)
    /// and perturbs ONE field, so the "rendered" and "stj" arguments are two concretely different,
    /// independently meaningful signatures — not a synthetic default-vs-default comparison a
    /// vacuous-in-a-different-way comparator could also pass by accident.
    /// </summary>
    [Fact]
    public void DescribeTypeSignatureDifferences_ReportsAGenuineMismatch()
    {
        var stj = GetStjTypeSignature(typeof(JsonDerivedTypeAloneProbeBase));
        var perturbedRendered = stj with { PolymorphicTypeDiscriminatorPropertyName = "kind" };

        var differences = DescribeTypeSignatureDifferences(
            nameof(JsonDerivedTypeAloneProbeBase), perturbedRendered, stj);

        Assert.NotEmpty(differences);
        Assert.Contains(differences, d => d.StartsWith(nameof(JsonDerivedTypeAloneProbeBase), StringComparison.Ordinal));
    }

    // ── Options-level pins (#586) ─────────────────────────────────────────────

    /// <summary>
    /// A RE-MEASURE TRIPWIRE, not a silent observation: this DOES fail the build when the
    /// System.Text.Json version moves off the one every other assertion in this file was
    /// measured against, printing the version via <see cref="ITestOutputHelper"/> either way so
    /// it is visible in test output too. MEASURED: 8.0.0.0 — the in-box net8.0 reflection
    /// resolver, because neither this test project nor <c>Vouchfx.Engine.Abstractions</c>
    /// references a <c>System.Text.Json</c> package (confirmed: no <c>PackageReference</c> to
    /// it anywhere in the solution), so the <c>Directory.Packages.props</c> 10.0.8 security pin
    /// — which applies only to a project graph that actually references the package — never
    /// reaches either assembly.
    /// </summary>
    /// <remarks>
    /// A version bump is not ITSELF a wire-contract change — measured: running under STJ 10.0.8
    /// (the version <c>Vouchfx.Cli</c> resolves), every OTHER assertion in this file still held;
    /// only this one pin failed. It fails on purpose anyway, rather than only logging: this
    /// file's own MEASURED comments
    /// (member <c>CustomConverter</c>/<c>ShouldSerialize</c> behaviour, where a converter factory
    /// is and is not expanded, the "$type" polymorphic default, the private
    /// <c>_elementConverter</c> field name, the Options members that STJ 9+
    /// (<c>AllowOutOfOrderMetadataProperties</c>) and STJ 10+ (<c>AllowDuplicateProperties</c>)
    /// add) are claims about THIS version, not eternal STJ facts, and nothing else forces a
    /// human to re-verify them when the version moves. The failure message names exactly which
    /// notes to re-check, so a future STJ bump is a deliberate re-measurement, never a silent
    /// drift.
    /// </remarks>
    [Fact]
    public void EventStreamJsonOptions_RunsAgainstMeasuredSystemTextJsonVersion()
    {
        var version = typeof(JsonSerializer).Assembly.GetName().Version;
        _output.WriteLine($"System.Text.Json assembly version: {version}");
        _output.WriteLine($"System.Text.Json assembly location: {typeof(JsonSerializer).Assembly.Location}");

        Assert.NotNull(version);
        Assert.True(
            version!.Major == 8,
            $"System.Text.Json moved to major version {version.Major} (was 8.0.0.0 when this file's "
            + "MEASURED comments were written). Re-measure and update, in this file's header and "
            + "inline comments, every claim tied to that version: (1) p.CustomConverter is null for a "
            + "GLOBALLY-registered converter; (2) p.ShouldSerialize is non-null ONLY for a member's own "
            + "[JsonIgnore(Condition=…)], never for the shared Options' DefaultIgnoreCondition alone; "
            + "(3) [JsonObjectCreationHandling(Populate)] throws NotSupportedException on a "
            + "parameterized-constructor type; (4) a type-level [JsonConverter] reports "
            + "JsonTypeInfoKind.None with empty Properties, while every other reflected type reports a "
            + "non-null ti.Converter; (5) [JsonDerivedType] alone (no [JsonPolymorphic]) makes STJ treat "
            + "a type as polymorphic, defaulting TypeDiscriminatorPropertyName to the literal \"$type\" "
            + "(NOT the bare attribute's own null default); (6) STJ 8 ignores base-type attributes on a "
            + "derived type regardless of the inherit: parameter passed; (7) NullableConverter<T>'s "
            + "wrapped converter is reachable only via the PRIVATE instance field _elementConverter — "
            + "confirm that field still exists and is still named that; (8) AllowOutOfOrderMetadataProperties "
            + "(STJ 9+) and AllowDuplicateProperties (STJ 10+) remain absent here — confirm they still do "
            + "not compile against this version, or add named assertions for them if they now do; "
            + "(9) a member-level JsonConverterFactory is reported UNEXPANDED on a member it can convert "
            + "directly, but expanded against T and then NullableConverter<T>-wrapped on a Nullable<T> "
            + "member, while a type-level factory is reported as its CREATED converter.");
    }

    /// <summary>
    /// Options-level pins (#586): one NAMED assertion per <see cref="JsonSerializerOptions"/>
    /// setting on <see cref="EventStreamJson.Options"/> that affects what it writes or parses.
    /// Named rather than a reflective golden, because a reflective snapshot of every settable
    /// option would not survive an STJ version bump — STJ 9 adds
    /// <c>AllowOutOfOrderMetadataProperties</c> and STJ 10 adds <c>AllowDuplicateProperties</c>
    /// (measured: <c>AllowDuplicateProperties</c> is absent from the STJ 9.0.4 package),
    /// neither of which exists on the STJ 8 this project runs against (measured: referencing
    /// either by name is a compile error here).
    /// Each assertion below states, in its own comment, why that setting is wire-relevant.
    /// </summary>
    [Fact]
    public void EventStreamJsonOptions_PinnedSettings_MatchMeasuredExpectations()
    {
        var options = EventStreamJson.Options;

        // Read-only by the time THIS test touches the shared instance. This alone does NOT pin
        // that CONSTRUCTION is what froze it — see
        // EventStreamJsonOptions_CreateOptions_FreezesOnConstruction below for the
        // test-order-independent proof: JsonSerializerOptions ALSO becomes
        // read-only the first time ANY caller (in or out of this file) serialises through it,
        // so this assertion alone would stay green even if CreateOptions' own explicit
        // MakeReadOnly call were removed, as long as some earlier test already exercised
        // ToLine/FromLine. A caller mutating a property below would throw
        // InvalidOperationException immediately once read-only, not silently succeed.
        Assert.True(options.IsReadOnly);

        // PropertyNamingPolicy is null: every wire name already comes from an explicit
        // [JsonPropertyName], and a naming policy only ever transforms a property lacking
        // one — measured: applying JsonNamingPolicy.CamelCase here
        // produces byte-identical output to today's, so this pin is about a FUTURE
        // property added without an explicit wire name, not about anything at risk today.
        // DictionaryKeyPolicy is null too: unlike PropertyNamingPolicy, one WOULD matter —
        // measured to rewrite the KEYS of a Dictionary<string,TValue>-typed property such
        // as CorrelationIds — but measured NOT to touch Extra's own extension-data keys,
        // which STJ exempts from it. See EventStreamJson.cs's own remarks for the same
        // measurement.
        Assert.Null(options.PropertyNamingPolicy);
        Assert.Null(options.DictionaryKeyPolicy);

        // Omits a null optional field from the wire (e.g. CorrelationIds) rather than writing
        // "field":null — the §14.4 examples never show a null-valued key.
        Assert.Equal(JsonIgnoreCondition.WhenWritingNull, options.DefaultIgnoreCondition);

        // Every wire member on every frozen record is an init-only auto-PROPERTY, never a
        // get-only computed one (measured: no frozen record declares a raw field at all, with
        // or without [JsonInclude] — CensusProbeRecord.Hidden, a [JsonInclude] field in
        // this file, is TEST-ONLY, used solely to prove the #578 census can see what the golden
        // cannot). Neither setting has anything to act on among frozen records today; pinned so
        // a change is deliberate rather than a side effect of an unrelated edit.
        Assert.False(options.IgnoreReadOnlyProperties);
        Assert.False(options.IgnoreReadOnlyFields);

        // No frozen record maps a FIELD at all (measured, see above); true here would map every
        // public field of every frozen record, silently widening the wire the first time one
        // gains a plain (non-property) field.
        Assert.False(options.IncludeFields);

        // Numbers are written as JSON numbers, not strings — Strict is STJ's default; pinned so
        // a global NumberHandling change (which would alter every numeric field's wire
        // representation at once) is deliberate.
        Assert.Equal(JsonNumberHandling.Strict, options.NumberHandling);

        // An unrecognised wire property is silently skipped during deserialisation rather than
        // throwing — this is what makes EventEnvelope.Extra's [JsonExtensionData] the actual
        // forward-compatibility mechanism (Skip is also STJ's default; an EventEnvelope reads
        // an unknown field into Extra regardless, since Extra is itself the extension-data
        // member — this assertion pins that no OTHER frozen record, which carries no
        // [JsonExtensionData] member, is made to THROW on an unknown field instead of
        // tolerating it per §14).
        Assert.Equal(JsonUnmappedMemberHandling.Skip, options.UnmappedMemberHandling);

        // Reading is case-SENSITIVE: "runId" and "RunId" are different wire names. Loosening
        // this would let a producer's typo silently bind to the wrong property instead of
        // falling through to Extra.
        Assert.False(options.PropertyNameCaseInsensitive);

        // JSON Lines is not JSON-with-comments; a `//`-prefixed line would otherwise be
        // ambiguous with a line-delimited stream.
        Assert.Equal(JsonCommentHandling.Disallow, options.ReadCommentHandling);
        Assert.False(options.AllowTrailingCommas);

        // 0 means "use STJ's built-in default (64)" — not "unlimited" and not "reject
        // everything". Pinned as the explicit STJ default rather than a project-chosen depth.
        Assert.Equal(0, options.MaxDepth);

        // WriteIndented = false is JSON Lines' own mandate (file header, CreateOptions'
        // remarks): one compact object per line; an embedded newline from indentation would
        // corrupt every line-oriented consumer.
        Assert.False(options.WriteIndented);

        // No custom escaping encoder — STJ's default (conservative HTML-safe) escaping applies,
        // and no code path here opts into UnsafeRelaxedJsonEscaping.
        Assert.Null(options.Encoder);

        // No $id/$ref preserved-reference handling — every frozen record is a flat or
        // list-of-value-record shape with no cyclic or shared-reference graphs to preserve.
        Assert.Null(options.ReferenceHandler);

        // Replace (STJ's own default) is what makes every frozen collection-typed member
        // (Captured, Substitutions, SecretReferences, Fixtures, …) get a FRESH collection
        // instance on deserialisation rather than being populated into (Populate) whatever the
        // init-only property's own default-value expression already constructed — relevant
        // because #586 measured that a member/type [JsonObjectCreationHandling(Populate)]
        // attribute changes this per-property/per-type, which is exactly what the new census
        // fields above pin; this assertion pins the GLOBAL default the census fields diff
        // against.
        Assert.Equal(JsonObjectCreationHandling.Replace, options.PreferredObjectCreationHandling);

        // #586: an `object`-typed value (e.g. a member declared as `object`,
        // which no frozen record has today) deserialises to a JsonElement rather than a boxed
        // CLR primitive — JsonElement is STJ's own default and is what the rest of this file
        // already assumes JsonElement-typed members (StepAttemptEvent.Observation,
        // StepCompletedEvent.Observation) receive; pinned so a change here — which would start
        // boxing `object` members as CLR primitives instead — is deliberate.
        Assert.Equal(JsonUnknownTypeHandling.JsonElement, options.UnknownTypeHandling);

        // DefaultBufferSize is NOT pinned: it only tunes the internal buffer STJ grows from
        // while writing (measured default 16384 bytes) and has no effect on what bytes end up
        // on the wire — changing it cannot break a consumer, so it is out of this census's scope
        // by definition (representation, not performance).

        // Exactly one converter, and it is VerdictJsonConverter — not, e.g., a default enum
        // string converter that would emit "Pass" instead of the canonical "PASS" token. Order
        // matters if a second converter for an overlapping type were ever added (STJ tries
        // converters in list order), so the count is pinned alongside the identity.
        Assert.Single(options.Converters);
        Assert.IsType<VerdictJsonConverter>(options.Converters[0]);

        // The default reflection-based resolver, not a source-generated one — required for the
        // per-member/per-type attribute reflection every assertion in this file depends on
        // (a source-generated JsonTypeInfo would not expose CustomConverter/ShouldSerialize/etc.
        // the same way).
        var resolver = Assert.IsType<DefaultJsonTypeInfoResolver>(options.TypeInfoResolver);

        // #586: pins that this is System.Text.Json's own SHARED default resolver instance —
        // JsonSerializerOptions.Default's own TypeInfoResolver, not merely the same TYPE — not a
        // private `new DefaultJsonTypeInfoResolver()` that CreateOptions could install
        // instead.
        //
        // MEASURED (STJ 8.0.0.0), and the two are NOT symmetric:
        //   • JsonSerializerOptions.Default.TypeInfoResolver is IMMUTABLE BY CONSTRUCTION.
        //     Its Modifiers.IsReadOnly is already true, and Modifiers.Add throws
        //     InvalidOperationException ("Cannot add callbacks to the 'Modifiers' property
        //     after the resolver has been used for the first time") the first time anything
        //     touches it — measured in a fresh process, before this file's own
        //     EventStreamJson.Options has done anything else with it. No modifier can EVER be
        //     appended to this specific instance, by this file or by anything else in the
        //     process.
        //   • A PRIVATE `new DefaultJsonTypeInfoResolver()`, by contrast, keeps
        //     Modifiers.IsReadOnly == false EVEN AFTER the JsonSerializerOptions that references
        //     it has itself been through MakeReadOnly() — measured: a modifier appended to such
        //     a resolver AFTER MakeReadOnly() still took effect on the NEXT Serialize call,
        //     renaming a property from the wire ("Value") to "renamed" in the output.
        // Assert.Same below is therefore what makes this pin meaningful: it proves CreateOptions
        // installed the resolver that cannot be mutated, not one that only happens to carry no
        // modifiers RIGHT NOW but remains just as mutable, after MakeReadOnly, as the renaming
        // example above.
        Assert.Same(JsonSerializerOptions.Default.TypeInfoResolver, options.TypeInfoResolver);

        // Belt-and-braces, now implied by Assert.Same above (the shared default resolver is
        // never observed with a non-empty Modifiers list, since nothing can append to it) —
        // kept because it fails with a more specific message ("Modifiers was not empty") if a
        // future STJ version ever changes that behaviour.
        Assert.Empty(resolver.Modifiers);
    }

    /// <summary>
    /// #586: pins that CONSTRUCTION itself freezes
    /// <see cref="EventStreamJson.Options"/> — not merely that the shared static field happens
    /// to read <c>IsReadOnly == true</c> by the time some test touches it.
    /// </summary>
    /// <remarks>
    /// MEASURED: a <see cref="JsonSerializerOptions"/> ALSO becomes implicitly read-only the
    /// FIRST time it is used for any (de)serialisation, independent of an explicit
    /// <c>MakeReadOnly</c> call. That means asserting <c>IsReadOnly</c> on the SHARED
    /// <see cref="EventStreamJson.Options"/> field, after other tests in this assembly have
    /// already exercised <c>ToLine</c>/<c>FromLine</c> against it, would stay GREEN even if
    /// <c>CreateOptions</c>' own explicit <c>MakeReadOnly</c> call were removed entirely — an
    /// earlier test's incidental first use would have frozen it anyway, masking the regression
    /// depending on test execution order (xUnit does not guarantee order across test classes).
    /// Invoking <see cref="EventStreamJson"/>'s PRIVATE static <c>CreateOptions</c> method
    /// AFRESH, via reflection, and asserting <c>IsReadOnly</c> on THAT brand-new instance —
    /// before anything else could possibly have touched it — is what actually pins "CreateOptions
    /// itself freezes what it returns", independent of test order.
    /// </remarks>
    [Fact]
    public void EventStreamJsonOptions_CreateOptions_FreezesOnConstruction()
    {
        var createOptionsMethod = typeof(EventStreamJson).GetMethod(
            "CreateOptions", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(createOptionsMethod);

        var freshlyConstructed = (JsonSerializerOptions)createOptionsMethod!.Invoke(obj: null, parameters: null)!;

        Assert.True(
            freshlyConstructed.IsReadOnly,
            "EventStreamJson.CreateOptions() must return an instance that is ALREADY "
            + "read-only — MakeReadOnly called inside CreateOptions itself — not one that merely "
            + "becomes read-only the first time some caller happens to serialise through it.");
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    // ── Deterministic type-name formatter ────────────────────────────────────

    private static string FormatType(Type type)
    {
        if (type.IsArray)
        {
            return $"{FormatType(type.GetElementType()!)}[]";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return $"{FormatType(underlying)}?";
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var baseName = StripArity(def.FullName ?? def.Name);
            var args = type.GetGenericArguments().Select(FormatType);
            return $"{baseName}<{string.Join(",", args)}>";
        }

        return type.FullName ?? type.Name;
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    // ── Golden + normalisation (mirror SchemaFreezeTests) ────────────────────

    /// <summary>
    /// Set <c>VOUCHFX_REGEN_EVENT_CONTRACT=1</c> to make the gate REWRITE the
    /// committed golden <c>Golden/event-stream-wire-contract.v1.txt</c> from the
    /// freshly-reflected wire signature instead of asserting against it.  Mirror of
    /// <c>SchemaFreezeTests.RegenEnvVar</c> / <c>VOUCHFX_REGEN_SCHEMA</c>.
    /// </summary>
    private const string RegenEnvVar = "VOUCHFX_REGEN_EVENT_CONTRACT";

    private static bool IsRegenRequested()
    {
        var value = Environment.GetEnvironmentVariable(RegenEnvVar);
        return !string.IsNullOrEmpty(value)
            && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the directory
    /// containing <c>vouchfx.sln</c> — the repo root.  Mirrors
    /// <c>SchemaFreezeTests.FindRepoRoot</c>.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').  This gate must locate "
            + "the source-tree golden to rewrite it.");
    }

    private static string Normalise(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

    private static string ReadGolden()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Golden", "event-stream-wire-contract.v1.txt");

        Assert.True(
            File.Exists(path),
            $"Golden v1 event-wire contract not found at '{path}'. The freeze gate "
            + "requires Golden/event-stream-wire-contract.v1.txt to be committed and "
            + "copied to output.");

        return File.ReadAllText(path);
    }

    private static string FirstDifference(string golden, string actual)
    {
        var goldenLines = golden.Split('\n');
        var actualLines = actual.Split('\n');
        var max = Math.Max(goldenLines.Length, actualLines.Length);

        for (var i = 0; i < max; i++)
        {
            var g = i < goldenLines.Length ? goldenLines[i] : "<EOF>";
            var a = i < actualLines.Length ? actualLines[i] : "<EOF>";
            if (!string.Equals(g, a, StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:"
                    + $"{Environment.NewLine}  golden: {g}"
                    + $"{Environment.NewLine}  actual: {a}";
            }
        }

        return "(no line-level difference detected; check trailing whitespace or length)";
    }
}
