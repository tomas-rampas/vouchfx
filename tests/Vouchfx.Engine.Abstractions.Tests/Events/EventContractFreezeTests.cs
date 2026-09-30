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
//   • A header line is "record <Name>", followed — ONLY when present, in this order —
//     by the type-level markers
//       [converter=<declared converter type>] [numberHandling=<value>]
//       [unmappedMemberHandling=<value>] [objectCreationHandling=<value>]
//       [polymorphicDerivedTypes=<type>:<discriminator>;…] [polymorphicDiscriminator=<name>]
//       [polymorphicUnknownHandling=<value>] [polymorphicIgnoreUnrecognized=<bool>]
//     (the four polymorphic markers appear together, whenever the record declares a
//     derived type; a string discriminator renders quoted, an int discriminator bare).
//   • Properties: public instance properties (Type.GetProperties(Public | Instance)),
//     sorted ordinally by property NAME so reflection order never matters.  Each line is:
//       "property <CLR-type> <PropertyName> [wire=<json-name>] [required] [init|get-only|set]"
//     followed — ONLY when present, in this order — by the member-level markers
//       [converter=<declared converter type>] [numberHandling=<value>] [order=<n>]
//       [objectCreationHandling=<value>] [ignoreCondition=<value>]
//     where [wire=<json-name>] is the [JsonPropertyName] value (the WIRE contract; absent
//     when the property carries no such attribute), [required] marks the C# `required`
//     modifier, and every CLR type — property and converter alike — is rendered by the
//     deterministic FormatType formatter (generics as Name<Arg>, Nullable<T> as T?, no
//     assembly qualification; reference-type nullability is not rendered).  The
//     synthesised record value-equality surface (Equals/GetHashCode/ToString/Deconstruct/
//     Clone/EqualityContract/op_*) is not emitted — it adds no wire information.
//
// DELIBERATE DECISION ENCODED HERE (T1):
//   The step events — StepStartedEvent / StepAttemptEvent / StepCompletedEvent —
//   carry RunId + StepId but DO NOT carry ScenarioId.  The renderer disambiguates
//   aggregated streams by the (runId, stepId) pair because each scenario has a
//   distinct runId.  The golden encodes this absence, so re-adding ScenarioId to a
//   step event later is a CONSCIOUS, REVIEWED change (it will fail this gate).  A
//   dedicated assertion below also pins the decision independently of the golden.
//
// CENSUS (#578) — what the golden CANNOT show: the golden renders each frozen record's
// PUBLIC INSTANCE PROPERTIES only. System.Text.Json maps more than that: a public field
// carrying [JsonInclude], and a non-public property or field carrying [JsonInclude], are
// both wire members to STJ but invisible to the property-only scan — such a member could
// be added to a frozen record and change the wire without moving the golden.
// EventWireContract_Census_MatchesStjMappedMembers closes that gap: for every record in
// s_eventRecords it computes the STJ-mapped member set from
// EventStreamJson.Options.GetTypeInfo(type).Properties (the same contract FromLine<T>
// reflects over) and asserts it is IDENTICAL — by wire name, CLR type, required,
// extension-data and the MEMBER-LEVEL REPRESENTATION fields below — to the rendered set
// from the same public-property enumeration the golden uses. A [JsonInclude] field or
// non-public [JsonInclude] member therefore fails THIS test with the member named. An
// unconditional [JsonIgnore] property is reported as rendered-but-unmapped (MEASURED (e)),
// so a NEW one on a frozen record fails this census permanently, by design: frozen
// records carry wire members only — put helpers in extension methods, not on the record.
// So does every property of a frozen record that gains a type-level [JsonConverter]:
// STJ then maps no members at all (MEASURED (g)).
//
// MEMBER-LEVEL REPRESENTATION CENSUS (#586) — the #578 census pins whether a member is
// mapped, not HOW it is represented: a member-level [JsonConverter], [JsonNumberHandling],
// [JsonPropertyOrder], [JsonObjectCreationHandling], or a CONDITIONAL [JsonIgnore]
// (Condition = WhenWritingDefault / WhenWritingNull / Never) changes the wire without
// changing a member's name, type or wire name. Each renders as a marker on the golden
// line (see the canonicalisation rule above) and is compared by the census through a
// MemberSignature field — HasCustomConverter, NumberHandling, Order,
// ObjectCreationHandling, ConditionalIgnore — populated two ways and asserted equal:
//   • RENDERED side (GetDeclaredJsonConverterType / GetJsonNumberHandling /
//     GetJsonPropertyOrder / GetJsonObjectCreationHandling / GetJsonIgnoreCondition):
//     reads the attribute directly off the PropertyInfo with inherit: false (MEASURED
//     (h)). The same helpers feed FormatProperty, so the golden and the census cannot
//     drift on what counts as present.
//   • STJ side (GetStjMappedMemberSignatures): reads the same fact off the shared
//     Options' own JsonPropertyInfo — p.CustomConverter != null (MEASURED (a)),
//     p.NumberHandling, p.Order, p.ObjectCreationHandling, p.ShouldSerialize != null
//     (MEASURED (d)).
// [JsonPropertyOrder] moves where STJ writes a member. The golden's own property list is
// sorted alphabetically by CLR name, so this pins the attribute, not wire member order in
// general: a reordered declaration changes nothing here (#600 proposes a byte-level golden
// that would see it).
// [JsonRequired] needs no field of its own: STJ's p.IsRequired is true for EITHER the C#
// `required` modifier or a bare [JsonRequired], while the rendered side's Required tests
// only the modifier, so a bare [JsonRequired] is already a Required mismatch
// (CensusProbeRecord.Tightened).
// [JsonConstructor] is out of scope: it selects the BINDING constructor, which changes how
// members are READ (see the required-reference-member guard in EventStreamJson.FromLine{T}),
// not what JsonPropertyInfo reports for a member's wire shape.
//
// CONVERTERS (#586): the golden renders the converter type a [JsonConverter] attribute
// DECLARES, never what System.Text.Json creates from it, and the census compares PRESENCE
// only. Presence is enough because STJ instantiates the declared type whenever it is
// non-null (MEASURED (b)): the declared type names exactly the converter, or factory, STJ
// uses, so replacing it moves the golden line. Two factories that create converters of the
// same type still write different bytes (MEASURED (c)), which is why the golden shows the
// declared factory rather than the created converter
// (Golden_RendersDeclaredFactory_SoAFactorySwapIsVisible). The one attribute shape with no
// declared type — a JsonConverterAttribute subclass whose ConverterType is null and which
// supplies its converter from CreateConverter — renders no marker, and the presence
// comparison reports it
// (Census_Detects_JsonConverterAttributeSubclass_ConverterTypeNull_AsGenuineMismatch).
//
// EXTENSION DATA: the census's ExtensionData field compares the [JsonExtensionData]
// attribute with STJ's reading of that same attribute, so the two sides cannot disagree,
// and the golden renders no marker for it. The set of extension-data members is therefore
// pinned by name: EventWireContract_ExtensionDataMembers_AreExactlyEnvelopeExtra.
//
// TYPE-LEVEL REPRESENTATION CENSUS (#586) — the same gap one level up: a type-level
// [JsonConverter] (which replaces the whole per-property contract), [JsonNumberHandling],
// [JsonUnmappedMemberHandling], [JsonPolymorphic]/[JsonDerivedType], or
// [JsonObjectCreationHandling] on a frozen RECORD TYPE changes the wire while moving
// neither the golden's member lines nor the member census. Each renders as a marker on the
// "record <Name>" header line and is compared through a TypeSignature field:
//   • RENDERED side (GetRenderedTypeSignature): reads the attributes off the record type
//     with inherit: false (MEASURED (h)), through the same helpers FormatRecordHeader uses.
//     Polymorphism is keyed on [JsonDerivedType] presence and the settings default to
//     STJ's own resolved defaults (MEASURED (i)).
//   • STJ side (GetStjTypeSignature): reads EventStreamJson.Options.GetTypeInfo(type)'s
//     NumberHandling, UnmappedMemberHandling, PolymorphismOptions and
//     PreferredPropertyObjectCreationHandling, and HasCustomConverter as
//     Kind == JsonTypeInfoKind.None (MEASURED (g)).
// A frozen record's declared derived types must themselves be frozen records
// (EventWireContract_DerivedTypesAreThemselvesFrozen); otherwise a derived type's OWN
// members are invisible to every test here.
//
// MEASURED — System.Text.Json 8.0.0.0, the in-box net8.0 assembly THIS TEST PROCESS loads
// (EventStreamJsonOptions_RunsAgainstMeasuredSystemTextJsonVersion pins it; the version
// the CLI ships is pinned separately there too). Each fact is stated once here; comments
// elsewhere refer to it by letter.
//   (a) p.CustomConverter is non-null ONLY for a member carrying its own [JsonConverter]. A
//       converter in the Options' global Converters list (VerdictJsonConverter) leaves it
//       null, and a member attribute takes precedence over that list: a Verdict member with
//       [JsonConverter(typeof(JsonStringEnumConverter))] writes "Pass" where an unattributed
//       Verdict member writes "PASS".
//   (b) When a [JsonConverter] attribute's ConverterType is non-null, STJ instantiates that
//       type: a JsonConverterAttribute subclass that sets ConverterType AND overrides
//       CreateConverter has its override ignored. Only a null ConverterType routes through
//       CreateConverter.
//   (c) On a Nullable<TEnum> member, JsonStringEnumConverter and a subclass whose
//       constructor passes JsonNamingPolicy.CamelCase both create the internal
//       EnumConverter<TEnum>, yet write "B" and "b" for the same value
//       (ConverterFactorySwapProbeRecord).
//   (d) p.ShouldSerialize is non-null ONLY for a member carrying its own
//       [JsonIgnore(Condition = …)] — WhenWritingDefault, WhenWritingNull or Never, even
//       one that repeats the global setting — and stays null under the Options' global
//       DefaultIgnoreCondition = WhenWritingNull alone, so ConditionalIgnore does not fire
//       for the ordinary nullable members.
//   (e) A [JsonIgnore] (Condition = Always) member is still listed in
//       JsonTypeInfo.Properties, with both Get and Set null; the census drops such members
//       from the STJ-mapped set.
//   (f) [JsonObjectCreationHandling(Populate)] on a member throws NotSupportedException
//       from JsonTypeInfo.Configure when the declaring type binds through a parameterized
//       constructor (the positional records CapturedVar, SubstitutionRef,
//       SecretReferenceDigest, FixtureDigest); it is supported on a property-syntax record.
//       Such a member on a positional record fails every test that builds that type's
//       JsonTypeInfo, before any comparison.
//   (g) JsonTypeInfo.Converter is non-null for every reflected type (STJ's internal
//       ObjectDefaultConverter<T> or SmallObjectWithParameterizedConstructorConverter<…>),
//       so it is not compared directly. Only a type carrying a type-level [JsonConverter]
//       reports Kind == JsonTypeInfoKind.None, with Properties EMPTY; no frozen record does.
//   (h) STJ reads representation attributes without inheritance: a base type's type-level
//       attributes do not apply to a derived type, and a base property's member-level
//       attributes do not apply to its override (a [JsonNumberHandling] on a virtual base
//       property is absent from the override's JsonPropertyInfo), although the default
//       GetCustomAttribute<T>(member) finds the base's. Every attribute read in this file
//       therefore passes inherit: false.
//   (i) [JsonDerivedType] ALONE, with no [JsonPolymorphic], makes a type polymorphic, and
//       PolymorphismOptions is identical either way. TypeDiscriminatorPropertyName defaults
//       to "$type" although the bare JsonPolymorphicAttribute's own property is null;
//       UnknownDerivedTypeHandling (FailSerialization) and
//       IgnoreUnrecognizedTypeDiscriminators (false) match the attribute's own defaults.
//       [JsonPolymorphic] with no [JsonDerivedType] makes GetTypeInfo throw
//       InvalidOperationException.
//   (j) A discriminator is an int or a string, and the two write differently:
//       [JsonDerivedType(typeof(D), 1)] writes "$type":1 and
//       [JsonDerivedType(typeof(D), "1")] writes "$type":"1".
//   (k) Neither PropertyNamingPolicy nor DictionaryKeyPolicy changes the keys STJ writes
//       from EventEnvelope.Extra, the [JsonExtensionData] member.
//
// OPTIONS-LEVEL PINS (#586) — EventStreamJsonOptions_PinnedSettings_MatchMeasuredExpectations
// adds one NAMED assertion per JsonSerializerOptions setting that affects what
// EventStreamJson.Options writes or parses, so an edit to CreateOptions() that changes one
// is caught the same way. Named, not reflective: a reflective snapshot of every settable
// option would not survive an STJ version bump (STJ 9 adds
// AllowOutOfOrderMetadataProperties and STJ 10 adds AllowDuplicateProperties; measured:
// referencing either by name here is a compile error under STJ 8).
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
using System.Globalization;
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
            + "[JsonPropertyName] wire name change, or a changed representation marker "
            + "([converter=…], [numberHandling=…], [order=…], [objectCreationHandling=…], "
            + "[ignoreCondition=…] on a property; the type-level and polymorphic markers on a "
            + "record header), breaks every renderer and the Healer. "
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

        // #586: representation-affecting member attributes, rendered ONLY when present (no
        // frozen record carries any today — see the file header's MEMBER-LEVEL
        // REPRESENTATION CENSUS note). Each getter is shared with GetRenderedMemberSignatures
        // so the golden and the census cannot drift on what counts as present. The converter
        // marker is the DECLARED type, so replacing one converter or factory with another
        // changes this line (see the file header's CONVERTERS note).
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
    /// appending #586's type-level representation markers ONLY when present (no frozen
    /// record carries any today — see the file header's
    /// TYPE-LEVEL REPRESENTATION CENSUS note). Shared with
    /// <see cref="GetRenderedTypeSignature"/> so the golden and the type-level census
    /// cannot independently drift on what counts as present.
    /// </summary>
    private static string FormatRecordHeader(Type record)
    {
        // #586: attribute reads use inherit: false (MEASURED (h) in the file header) and the
        // converter marker is the DECLARED type (see the file header's CONVERTERS note).
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

        // #586: the settings a [JsonPolymorphic] attribute can carry alongside its
        // derived-type list — rendered whenever the record is polymorphic at all (derivedTypes
        // is non-null), not only when a [JsonPolymorphic] attribute is present (MEASURED (i)).
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
    /// The sorted derived-type tokens (<see cref="FormatDerivedTypeToken"/>) declared via
    /// <c>[JsonDerivedType]</c> on <paramref name="record"/> (read with <c>inherit: false</c>),
    /// or <see langword="null"/> when <paramref name="record"/> declares none.
    /// </summary>
    /// <remarks>
    /// Keyed on <c>[JsonDerivedType]</c> PRESENCE rather than <c>[JsonPolymorphic]</c>
    /// presence, because <c>[JsonDerivedType]</c> alone makes the type polymorphic (MEASURED
    /// (i) in the file header): keying on <c>[JsonPolymorphic]</c> would leave a record carrying
    /// only <c>[JsonDerivedType]</c> with no marker and no type-level comparison.
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
            .Select(a => FormatDerivedTypeToken(a.DerivedType, a.TypeDiscriminator))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// #586: one <c>"&lt;derived type&gt;:&lt;discriminator&gt;"</c> token, shared by the
    /// rendered side (<see cref="GetDeclaredDerivedTypeTokens"/>) and the STJ side
    /// (<see cref="GetStjTypeSignature"/>). The derived type goes through
    /// <see cref="FormatType"/>, so a generic derived type carries no assembly-qualified
    /// arguments. The discriminator's KIND is rendered, because an int and a string
    /// discriminator write differently (MEASURED (j) in the file header): a string renders
    /// quoted (<c>"1"</c>), an int bare (<c>1</c>), and an absent discriminator as
    /// <c>(none)</c>.
    /// </summary>
    private static string FormatDerivedTypeToken(Type derivedType, object? discriminator)
    {
        var renderedDiscriminator = discriminator switch
        {
            null => "(none)",
            string s => $"\"{s}\"",
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => $"({FormatType(discriminator.GetType())}){discriminator}",
        };

        return $"{FormatType(derivedType)}:{renderedDiscriminator}";
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
    /// The defaults are STJ's resolved ones (MEASURED (i) in the file header) — in particular
    /// <c>"$type"</c>, where the bare attribute's own <c>TypeDiscriminatorPropertyName</c> is
    /// <see langword="null"/>; defaulting to <see langword="null"/> would make every polymorphic
    /// record that does not override the discriminator disagree with the STJ side. Only called
    /// when <see cref="GetDeclaredDerivedTypeTokens"/> returned non-null: the settings mean
    /// nothing without a declared derived type.
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
    /// Never expanded, even for a <see cref="JsonConverterFactory"/>: STJ instantiates exactly
    /// the declared type (MEASURED (b) in the file header), and the declared type is what
    /// distinguishes two factories that create converters of the SAME type but write different
    /// bytes (MEASURED (c); <see cref="Golden_RendersDeclaredFactory_SoAFactorySwapIsVisible"/>).
    /// Rendering the created converter instead would let such a swap change the wire with the
    /// golden unchanged.
    /// </remarks>
    private static string? FormatDeclaredConverterTypeName(Type? converterType) =>
        converterType is null ? null : FormatType(converterType);

    /// <summary>
    /// #586: the member-level <c>[JsonConverter]</c> attribute's declared converter type (read
    /// with <c>inherit: false</c>, MEASURED (h)), or <see langword="null"/> when the attribute
    /// is absent OR declares no type (a subclass supplying its converter from
    /// <c>CreateConverter</c>). Shared by <see cref="FormatProperty"/>, which renders it, and
    /// <see cref="GetRenderedMemberSignatures"/>, whose HasCustomConverter is its presence — so
    /// the census holds exactly when the golden line carries a converter marker if and only if
    /// System.Text.Json uses a member converter. A converter in the Options' global
    /// <c>Converters</c> list never sets it, matching STJ's own
    /// <c>JsonPropertyInfo.CustomConverter</c> (MEASURED (a)).
    /// </summary>
    private static Type? GetDeclaredJsonConverterType(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonConverterAttribute>(inherit: false)?.ConverterType;

    /// <summary>
    /// #586: the type-level <c>[JsonConverter]</c> attribute's declared converter type (read
    /// with <c>inherit: false</c>, MEASURED (h)), or <see langword="null"/> when the attribute
    /// is absent or declares no type. Shared by <see cref="FormatRecordHeader"/>, which renders
    /// it, and <see cref="GetRenderedTypeSignature"/>, which compares its presence.
    /// </summary>
    private static Type? GetDeclaredTypeConverterType(Type record) =>
        record.GetCustomAttribute<JsonConverterAttribute>(inherit: false)?.ConverterType;

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
        prop.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: false)?.Name;

    /// <summary>
    /// <see langword="true"/> when <paramref name="prop"/> carries the C#
    /// <see langword="required"/> modifier (<see cref="RequiredMemberAttribute"/>).
    /// Shared by <see cref="FormatProperty"/> and <see cref="GetRenderedMemberSignatures"/>.
    /// </summary>
    private static bool IsRequiredMember(PropertyInfo prop) =>
        prop.GetCustomAttribute<RequiredMemberAttribute>(inherit: false) is not null;

    /// <summary>
    /// #586: the member-level <c>[JsonNumberHandling]</c> value, or <see langword="null"/>
    /// when absent. Shared by <see cref="FormatProperty"/> and
    /// <see cref="GetRenderedMemberSignatures"/>.
    /// </summary>
    private static JsonNumberHandling? GetJsonNumberHandling(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonNumberHandlingAttribute>(inherit: false)?.Handling;

    /// <summary>
    /// #586: the member-level <c>[JsonPropertyOrder]</c> value, defaulted to <c>0</c> when
    /// absent — the default STJ's own <c>JsonPropertyInfo.Order</c> reports for an
    /// unattributed member (measured), so the two sides compare the EFFECTIVE order; an
    /// explicit <c>[JsonPropertyOrder(0)]</c> is the default and changes nothing on the wire.
    /// Shared by <see cref="FormatProperty"/>, which renders the marker only when this is
    /// non-zero, and <see cref="GetRenderedMemberSignatures"/>.
    /// </summary>
    private static int GetJsonPropertyOrder(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonPropertyOrderAttribute>(inherit: false)?.Order ?? 0;

    /// <summary>
    /// #586: the member-level <c>[JsonObjectCreationHandling]</c> value, or
    /// <see langword="null"/> when absent. Shared by <see cref="FormatProperty"/> and
    /// <see cref="GetRenderedMemberSignatures"/>. On a positional frozen record,
    /// <c>Populate</c> fails before any comparison (MEASURED (f) in the file header).
    /// </summary>
    private static JsonObjectCreationHandling? GetJsonObjectCreationHandling(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonObjectCreationHandlingAttribute>(inherit: false)?.Handling;

    /// <summary>
    /// #586: <paramref name="prop"/>'s own <c>[JsonIgnore(Condition=…)]</c> condition when
    /// it keeps the member mapped (<c>WhenWritingDefault</c>, <c>WhenWritingNull</c> or
    /// <c>Never</c>), or <see langword="null"/> when the attribute is absent or its condition
    /// is <c>Always</c>. Shared by <see cref="FormatProperty"/> and
    /// <see cref="GetRenderedMemberSignatures"/>; compared against STJ's
    /// <c>JsonPropertyInfo.ShouldSerialize != null</c> (MEASURED (d) in the file header).
    /// </summary>
    /// <remarks>
    /// <c>Always</c> is excluded because such a member is not a wire member at all (MEASURED
    /// (e)): the census reports it as rendered-but-unmapped. Excluding it here is what keeps
    /// an <c>[ignoreCondition=Always]</c> marker off its golden line and keeps its rendered
    /// ConditionalIgnore false — <see cref="CensusProbeRecord.Dropped"/> is such a member.
    /// </remarks>
    private static JsonIgnoreCondition? GetJsonIgnoreCondition(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<JsonIgnoreAttribute>(inherit: false);
        return attr is null || attr.Condition == JsonIgnoreCondition.Always ? null : attr.Condition;
    }

    /// <summary>
    /// A member's wire-relevant signature, comparable between the golden's rendered
    /// (public-property-only) view and System.Text.Json's own mapped-member view
    /// under the shared <see cref="EventStreamJson.Options"/>.
    /// #586 adds five representation fields (HasCustomConverter, NumberHandling, Order,
    /// ObjectCreationHandling, ConditionalIgnore) alongside the #578 fields; see the
    /// file header for what each pins. HasCustomConverter compares PRESENCE only; the golden
    /// line's declared-type marker pins which converter (see the file header's CONVERTERS
    /// note).
    /// </summary>
    private readonly record struct MemberSignature(
        string Wire,
        Type ClrType,
        bool Required,
        bool ExtensionData,
        bool HasCustomConverter,
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
                    ExtensionData: p.GetCustomAttribute<JsonExtensionDataAttribute>(inherit: false) is not null,
                    HasCustomConverter: GetDeclaredJsonConverterType(p) is not null,
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
            // (MEASURED (e) in the file header; the same observation as
            // EventStreamJson.ComputeRequiredReferenceMembers). STJ never reads or writes
            // it, so it is not a wire member; keeping it would let [JsonIgnore] on a frozen
            // property drop it from the wire with this census still passing.
            if (p.Get is null && p.Set is null)
            {
                continue;
            }

            // Every JsonPropertyInfo under the shared reflection-based Options carries
            // the declaring PropertyInfo/FieldInfo as its AttributeProvider (mirrors the
            // same observation EventStreamJson.ComputeRequiredReferenceMembers relies on).
            var memberName = (p.AttributeProvider as MemberInfo)?.Name ?? p.Name;

            // #586: STJ's own per-member representation facts under the shared Options, each
            // compared with what GetRenderedMemberSignatures reads off the same member's
            // attributes: p.CustomConverter is non-null only for a member-level [JsonConverter]
            // (MEASURED (a) in the file header), p.ShouldSerialize only for a member's own
            // [JsonIgnore(Condition = …)] (MEASURED (d)).
            var signature = new MemberSignature(
                p.Name,
                p.PropertyType,
                p.IsRequired,
                p.IsExtensionData,
                HasCustomConverter: p.CustomConverter is not null,
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
                + "the wire under the shared Options — e.g. [JsonIgnore] on the property, or a "
                + "type-level [JsonConverter] on the record, under which System.Text.Json maps no "
                + "members at all.");
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
            + "required-ness, extension-data (#578), or custom-converter presence, number handling, "
            + "property order, object-creation handling, conditional-ignore (#586). This means a "
            + "[JsonInclude] field, "
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
    /// Extension-data pin (#586): across <see cref="s_eventRecords"/>, the members
    /// System.Text.Json treats as extension data (<c>JsonPropertyInfo.IsExtensionData</c>, its
    /// own resolution) are exactly <c>EventEnvelope.Extra</c>. The golden renders no marker for
    /// <c>[JsonExtensionData]</c>, and the census's ExtensionData field compares the attribute
    /// with STJ's reading of that same attribute, so neither can see the attribute removed
    /// from Extra or added to another member; Extra is where every consumer reads the fields
    /// it does not know (§14).
    /// </summary>
    [Fact]
    public void EventWireContract_ExtensionDataMembers_AreExactlyEnvelopeExtra()
    {
        var extensionDataMembers = s_eventRecords
            .SelectMany(record => EventStreamJson.Options.GetTypeInfo(record).Properties
                .Where(p => p.IsExtensionData)
                .Select(p => $"{record.Name}.{(p.AttributeProvider as MemberInfo)?.Name ?? p.Name}"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var expected = new[] { $"{nameof(EventEnvelope)}.{nameof(EventEnvelope.Extra)}" };
        Assert.True(
            extensionDataMembers.SequenceEqual(expected, StringComparer.Ordinal),
            "The v1 event-wire extension-data members (#586) must be exactly EventEnvelope.Extra: "
            + "the attribute decides where an EventEnvelope reads and writes the fields it does not "
            + "know, and neither removing it from Extra nor adding it to another member moves the "
            + "golden. Found: ["
            + string.Join(", ", extensionDataMembers)
            + "].");
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
    /// <see cref="EventStreamJson.Options"/>, and that the two sides agree — plus a converter
    /// factory on a plain and on a nullable member, proven by
    /// <see cref="Census_Detects_ConverterFactoryPresence_OnPlainAndNullableMembers"/>.
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

        // #586: a JsonConverterFactory on a plain enum member and on a nullable one — the two
        // shapes whose JsonPropertyInfo.CustomConverter differs (the factory itself, and an
        // internal wrapper around the converter it creates); presence must agree on both.
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public CensusProbeEnum PlainFactoryConverted { get; init; }

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
        Assert.True(rendered["Converted"].HasCustomConverter);
        Assert.Equal(rendered["Converted"].HasCustomConverter, stj["Converted"].HasCustomConverter);
        Assert.False(stj["RunId"].HasCustomConverter);
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
        // which is the outcome the census depends on for every frozen record.
        var differences = DescribeMemberSetDifferences(nameof(CensusProbeRecord), rendered, stj);
        Assert.DoesNotContain(differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.Converted", StringComparison.Ordinal));
        Assert.DoesNotContain(differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.NumberHandled", StringComparison.Ordinal));
        Assert.DoesNotContain(differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.Ordered", StringComparison.Ordinal));
        Assert.DoesNotContain(differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.Populated", StringComparison.Ordinal));
        Assert.DoesNotContain(
            differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.ConditionallyIgnored", StringComparison.Ordinal));
    }

    /// <summary>
    /// Probe (#586): a <see cref="JsonConverterFactory"/> on a plain enum member
    /// (<see cref="CensusProbeRecord.PlainFactoryConverted"/>) and on a nullable one
    /// (<see cref="CensusProbeRecord.NullableFactoryConverted"/>). System.Text.Json reports a
    /// different <c>CustomConverter</c> for each (the factory itself; an internal wrapper around
    /// the converter it creates), and presence must agree on both. The golden line shows the
    /// DECLARED factory on both.
    /// </summary>
    [Fact]
    public void Census_Detects_ConverterFactoryPresence_OnPlainAndNullableMembers()
    {
        var rendered = GetRenderedMemberSignatures(typeof(CensusProbeRecord));
        var stj = GetStjMappedMemberSignatures(typeof(CensusProbeRecord));
        var differences = DescribeMemberSetDifferences(nameof(CensusProbeRecord), rendered, stj);
        var declaredFactoryMarker = $"[converter={typeof(JsonStringEnumConverter).FullName}]";

        foreach (var member in new[]
        {
            nameof(CensusProbeRecord.PlainFactoryConverted),
            nameof(CensusProbeRecord.NullableFactoryConverted),
        })
        {
            Assert.True(stj[member].HasCustomConverter);
            Assert.True(rendered[member].HasCustomConverter);
            Assert.DoesNotContain(
                differences, d => d.StartsWith($"{nameof(CensusProbeRecord)}.{member}", StringComparison.Ordinal));
            Assert.Contains(
                declaredFactoryMarker,
                FormatProperty(typeof(CensusProbeRecord).GetProperty(member)!),
                StringComparison.Ordinal);
        }
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
    /// that create converters of the same type (MEASURED (c) in the file header) — a swap
    /// neither presence nor the created converter's type can see.
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
    /// Pins that each line names its own declared factory and never the created converter,
    /// whose type is the same for both members.
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

        // Both census sides agree on both members: the census cannot tell them apart.
        var rendered = GetRenderedMemberSignatures(typeof(ConverterFactorySwapProbeRecord));
        var stj = GetStjMappedMemberSignatures(typeof(ConverterFactorySwapProbeRecord));
        Assert.Equal(stj["Standard"] with { Wire = "CamelCase" }, stj["CamelCase"]);
        Assert.Empty(DescribeMemberSetDifferences(nameof(ConverterFactorySwapProbeRecord), rendered, stj));

        // So only the golden can tell them apart: each line names its own DECLARED factory,
        // never the internal EnumConverter<TEnum> both factories create.
        var standardLine = FormatProperty(standardProp);
        var camelCaseLine = FormatProperty(camelCaseProp);
        Assert.Contains($"[converter={typeof(JsonStringEnumConverter).FullName}]", standardLine, StringComparison.Ordinal);
        Assert.Contains(
            $"[converter={typeof(CensusProbeCamelCaseEnumConverter).FullName}]", camelCaseLine, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumConverter<", standardLine, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumConverter<", camelCaseLine, StringComparison.Ordinal);
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
    /// A <see cref="JsonConverterAttribute"/> SUBCLASS that declares a converter type through
    /// the base constructor AND overrides <see cref="JsonConverterAttribute.CreateConverter(Type)"/>
    /// to return a different one. Used ONLY by
    /// <see cref="Census_Detects_JsonConverterAttributeSubclass_ConverterTypeNull_AsGenuineMismatch"/>
    /// to pin MEASURED (b) in the file header: System.Text.Json instantiates the declared type
    /// and ignores the override.
    /// </summary>
    private sealed class DeclaredTypeWithOverrideAttribute : JsonConverterAttribute
    {
        public DeclaredTypeWithOverrideAttribute()
            : base(typeof(CensusProbeUpperCaseConverter))
        {
        }

        public override JsonConverter? CreateConverter(Type typeToConvert) => new CensusProbeOverrideConverter();
    }

    /// <summary>
    /// The converter <see cref="DeclaredTypeWithOverrideAttribute"/>'s ignored override returns.
    /// </summary>
    private sealed class CensusProbeOverrideConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() ?? string.Empty;

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    /// <summary>
    /// Probe record (#586): <see cref="Passthrough"/> carries
    /// <see cref="NullConverterTypePassthroughAttribute"/> (no declared type), and
    /// <see cref="DeclaredWins"/> carries <see cref="DeclaredTypeWithOverrideAttribute"/>.
    /// </summary>
    private sealed record ConverterAttributeNullTypeProbeRecord
    {
        [NullConverterTypePassthrough]
        public string Passthrough { get; init; } = string.Empty;

        [DeclaredTypeWithOverride]
        public string DeclaredWins { get; init; } = string.Empty;
    }

    /// <summary>
    /// Probe (#586): the two premises the presence-only converter comparison rests on.
    /// A member whose attribute declares NO converter type renders no golden marker while
    /// System.Text.Json still uses a converter, so the census reports a genuine mismatch for it
    /// — the STJ side must read <c>JsonPropertyInfo.CustomConverter</c>, not the attribute, or
    /// both sides would read "absent" and agree. And a member whose attribute declares a type
    /// is converted by exactly that type, even when the attribute overrides
    /// <c>CreateConverter</c>, so the declared type the golden renders is the converter in use.
    /// </summary>
    [Fact]
    public void Census_Detects_JsonConverterAttributeSubclass_ConverterTypeNull_AsGenuineMismatch()
    {
        var prop = typeof(ConverterAttributeNullTypeProbeRecord)
            .GetProperty(nameof(ConverterAttributeNullTypeProbeRecord.Passthrough))!;
        var attr = prop.GetCustomAttribute<JsonConverterAttribute>(inherit: false);
        Assert.NotNull(attr);
        Assert.Null(attr!.ConverterType);

        var rendered = GetRenderedMemberSignatures(typeof(ConverterAttributeNullTypeProbeRecord));
        var stj = GetStjMappedMemberSignatures(typeof(ConverterAttributeNullTypeProbeRecord));

        Assert.False(rendered["Passthrough"].HasCustomConverter);
        Assert.True(stj["Passthrough"].HasCustomConverter);
        Assert.DoesNotContain("[converter=", FormatProperty(prop), StringComparison.Ordinal);

        var differences = DescribeMemberSetDifferences(
            nameof(ConverterAttributeNullTypeProbeRecord), rendered, stj);

        Assert.Contains(
            differences,
            d => d.StartsWith($"{nameof(ConverterAttributeNullTypeProbeRecord)}.Passthrough", StringComparison.Ordinal)
                && d.Contains("does not match", StringComparison.Ordinal));

        // The declared type wins over the attribute's CreateConverter override.
        var declaredWins = EventStreamJson.Options.GetTypeInfo(typeof(ConverterAttributeNullTypeProbeRecord))
            .Properties.Single(p => p.Name == nameof(ConverterAttributeNullTypeProbeRecord.DeclaredWins));
        Assert.IsType<CensusProbeUpperCaseConverter>(declaredWins.CustomConverter);
        Assert.DoesNotContain(
            differences,
            d => d.StartsWith($"{nameof(ConverterAttributeNullTypeProbeRecord)}.DeclaredWins", StringComparison.Ordinal));
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
    /// <see cref="MemberSignature"/>, which is per-MEMBER. <see cref="HasCustomConverter"/>
    /// compares presence only, as at member level; the golden header's declared-type marker
    /// pins which converter. <see cref="PolymorphicDerivedTypes"/> is the same <c>"Type:tag;Type:tag"</c> joined,
    /// sorted string <see cref="FormatRecordHeader"/> renders — not a list — because
    /// <c>record struct</c> equality on a reference-type member (a <see cref="List{T}"/> or
    /// similar) would compare by REFERENCE, not content, making two independently-built
    /// signatures with identical derived-type sets compare unequal. The other
    /// <c>Polymorphic*</c> fields beyond the derived-type list (#586) are value
    /// types (a nullable string, a nullable enum, a nullable bool), so they need no such
    /// joining — <c>record struct</c> equality on them is exact by construction.
    /// </summary>
    private readonly record struct TypeSignature(
        bool HasCustomConverter,
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
    /// <paramref name="record"/>'s own attributes (<c>inherit: false</c>, MEASURED (h) in the
    /// file header). Shared getters keep the golden header and this signature from
    /// independently drifting on what counts as present.
    /// </summary>
    private static TypeSignature GetRenderedTypeSignature(Type record)
    {
        var derived = GetDeclaredDerivedTypeTokens(record);
        (string Discriminator, JsonUnknownDerivedTypeHandling UnknownHandling, bool IgnoreUnrecognized)? polySettings =
            derived is null ? null : GetDeclaredPolymorphicSettings(record);

        return new TypeSignature(
            HasCustomConverter: GetDeclaredTypeConverterType(record) is not null,
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
    /// HasCustomConverter is <c>Kind == JsonTypeInfoKind.None</c>, not
    /// <c>Converter != null</c>: every reflected type has a non-null converter, and only a
    /// custom one leaves the type with no object contract (MEASURED (g) in the file header). A
    /// converter for a record type added to the Options' global <c>Converters</c> list would
    /// report the same, with no attribute on the rendered side — a mismatch this comparison
    /// then reports.
    /// </remarks>
    private static TypeSignature GetStjTypeSignature(Type record)
    {
        var typeInfo = EventStreamJson.Options.GetTypeInfo(record);

        var poly = typeInfo.PolymorphismOptions;
        var polymorphicDerivedTypes = poly is null
            ? null
            : string.Join(
                ";",
                poly.DerivedTypes
                    .Select(dt => FormatDerivedTypeToken(dt.DerivedType, dt.TypeDiscriminator))
                    .OrderBy(s => s, StringComparer.Ordinal));

        return new TypeSignature(
            HasCustomConverter: typeInfo.Kind == JsonTypeInfoKind.None,
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
    /// #586: the comparison is plain <c>record struct</c> equality over every
    /// <see cref="TypeSignature"/> field. Every frozen record agrees today, so the census test
    /// alone cannot show that this method reports anything;
    /// <see cref="DescribeTypeSignatureDifferences_ReportsAGenuineMismatch"/> pins that it
    /// reports a difference in a single field.
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
    /// <paramref name="frozenSet"/>, or its own members are invisible to every test in this
    /// file: the type-level census pins the LIST of derived types and discriminators
    /// (<c>PolymorphicDerivedTypes</c> in <see cref="TypeSignature"/>), but nothing walks INTO a
    /// derived type that is not itself in <c>s_eventRecords</c>, so a wire-name rename on one of
    /// its own properties would move neither the golden nor either census. Returns one message per unfrozen derived type, or an empty list when
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
    /// wire-name rename on one of them changes the wire with every other test in this file
    /// passing, because the derived type was never separately added to <see cref="s_eventRecords"/>.
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
    /// record. No frozen record is polymorphic today, so this probe is what pins that the
    /// <c>!frozenSet.Contains(...)</c> check reports a missing derived type at all.
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

    // #586 type-level probe types — test-only, used only by
    // Census_Detects_TypeLevelRepresentationAttributes and
    // Census_Detects_DiscriminatorKind_IntVersusString below.

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

    [JsonDerivedType(typeof(IntDiscriminatorProbeDerived), 1)]
    private abstract record IntDiscriminatorProbeBase(string A);

    private sealed record IntDiscriminatorProbeDerived(string A) : IntDiscriminatorProbeBase(A);

    [JsonDerivedType(typeof(StringDiscriminatorProbeDerived), "1")]
    private abstract record StringDiscriminatorProbeBase(string A);

    private sealed record StringDiscriminatorProbeDerived(string A) : StringDiscriminatorProbeBase(A);

    /// <summary>
    /// Probe (#586): an int discriminator and a string discriminator with the same text write
    /// differently (MEASURED (j) in the file header), so both the golden header and the
    /// type-level census must render the discriminator's KIND — the string quoted, the int bare
    /// — and each side must agree with System.Text.Json.
    /// </summary>
    [Fact]
    public void Census_Detects_DiscriminatorKind_IntVersusString()
    {
        Assert.Equal(
            "{\"$type\":1,\"A\":\"a\"}",
            JsonSerializer.Serialize<IntDiscriminatorProbeBase>(new IntDiscriminatorProbeDerived("a"), EventStreamJson.Options));
        Assert.Equal(
            "{\"$type\":\"1\",\"A\":\"a\"}",
            JsonSerializer.Serialize<StringDiscriminatorProbeBase>(
                new StringDiscriminatorProbeDerived("a"), EventStreamJson.Options));

        var intRendered = GetRenderedTypeSignature(typeof(IntDiscriminatorProbeBase));
        var intStj = GetStjTypeSignature(typeof(IntDiscriminatorProbeBase));
        var stringRendered = GetRenderedTypeSignature(typeof(StringDiscriminatorProbeBase));
        var stringStj = GetStjTypeSignature(typeof(StringDiscriminatorProbeBase));

        var intToken = $"{typeof(IntDiscriminatorProbeDerived).FullName}:1";
        var stringToken = $"{typeof(StringDiscriminatorProbeDerived).FullName}:\"1\"";
        Assert.Equal(intToken, intStj.PolymorphicDerivedTypes);
        Assert.Equal(stringToken, stringStj.PolymorphicDerivedTypes);
        Assert.Empty(DescribeTypeSignatureDifferences("probe", intRendered, intStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", stringRendered, stringStj));

        Assert.Contains(
            $"[polymorphicDerivedTypes={intToken}]",
            FormatRecordHeader(typeof(IntDiscriminatorProbeBase)),
            StringComparison.Ordinal);
        Assert.Contains(
            $"[polymorphicDerivedTypes={stringToken}]",
            FormatRecordHeader(typeof(StringDiscriminatorProbeBase)),
            StringComparison.Ordinal);
    }

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
        Assert.True(converterStj.HasCustomConverter);
        Assert.Equal(converterRendered.HasCustomConverter, converterStj.HasCustomConverter);
        Assert.False(GetStjTypeSignature(typeof(TypeNumberHandlingProbeRecord)).HasCustomConverter);
        Assert.Contains(
            $"[converter={typeof(TypeLevelProbeConverter).FullName}]",
            FormatRecordHeader(typeof(TypeConverterProbeRecord)),
            StringComparison.Ordinal);

        // A type-level FACTORY: present on both sides, and the golden header shows the DECLARED
        // factory, not the converter it creates.
        var factoryRendered = GetRenderedTypeSignature(typeof(TypeFactoryConverterProbeRecord));
        var factoryStj = GetStjTypeSignature(typeof(TypeFactoryConverterProbeRecord));
        Assert.True(factoryStj.HasCustomConverter);
        Assert.Equal(factoryStj.HasCustomConverter, factoryRendered.HasCustomConverter);
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
        var expectedDerived = $"{typeof(TypePolymorphicDerivedProbeRecord).FullName}:\"derived\"";
        Assert.Equal(expectedDerived, polyRendered.PolymorphicDerivedTypes);
        Assert.Equal(polyRendered.PolymorphicDerivedTypes, polyStj.PolymorphicDerivedTypes);
        Assert.Contains(
            $"[polymorphicDerivedTypes={expectedDerived}]",
            FormatRecordHeader(typeof(TypePolymorphicBaseProbeRecord)),
            StringComparison.Ordinal);

        // Both sides agree on every one of these — the outcome
        // EventWireContract_Census_MatchesStjTypeLevelSettings depends on for every frozen record.
        Assert.Empty(DescribeTypeSignatureDifferences("probe", converterRendered, converterStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", factoryRendered, factoryStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", numberHandlingRendered, numberHandlingStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", unmappedRendered, unmappedStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", creationRendered, creationStj));
        Assert.Empty(DescribeTypeSignatureDifferences("probe", polyRendered, polyStj));

        // #586: the settings beyond the derived-type list agree too, using System.Text.Json's
        // own resolved defaults (MEASURED (i) in the file header).
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
    /// Probe (#586): <see cref="JsonDerivedTypeAloneProbeBase"/> carries ONLY
    /// <c>[JsonDerivedType]</c>; both the rendered and the STJ views must recognise it as
    /// polymorphic and agree on every polymorphism setting (MEASURED (i) in the file header),
    /// which pins that <see cref="GetDeclaredDerivedTypeTokens"/> keys on
    /// <c>[JsonDerivedType]</c> presence.
    /// </summary>
    [Fact]
    public void Census_Detects_DerivedTypeAloneAsPolymorphic_WithoutJsonPolymorphicAttribute()
    {
        var rendered = GetRenderedTypeSignature(typeof(JsonDerivedTypeAloneProbeBase));
        var stj = GetStjTypeSignature(typeof(JsonDerivedTypeAloneProbeBase));

        var expectedDerived = $"{typeof(JsonDerivedTypeAloneProbeDerived).FullName}:\"tag\"";
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
    /// Probe (#586): pins that <see cref="DescribeTypeSignatureDifferences"/> reports a
    /// difference when two signatures differ in a single field. Every frozen record agrees
    /// today, so without this probe a comparator that never reports anything would leave the
    /// type-level census passing. Takes a real STJ-reflected <see cref="TypeSignature"/> (from
    /// <see cref="JsonDerivedTypeAloneProbeBase"/>, whose polymorphism fields are populated) and
    /// changes only its discriminator property name.
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
    /// Pins the System.Text.Json assembly THIS TEST PROCESS loads — the version the file
    /// header's MEASURED facts were taken on — at major version 8, printing its version and
    /// location either way. It is the in-box net8.0 assembly (8.0.0.0), because neither this
    /// test project nor <c>Vouchfx.Engine.Abstractions</c> references a <c>System.Text.Json</c>
    /// package; its assembly version does not move with net8.0 servicing, so this fails a test
    /// only if the test project starts referencing the package or moves target framework.
    /// It is NOT the version the CLI ships: that is the central <c>PackageVersion</c>, pinned by
    /// <see cref="CentralSystemTextJsonPackageVersion_IsTheMajorTheNotesWereCheckedAgainst"/>.
    /// </summary>
    /// <remarks>
    /// The MEASURED facts are claims about one version, not eternal STJ behaviour, and nothing
    /// else makes anyone re-check them when the version moves; the failure message says which
    /// notes to re-check.
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
            $"The System.Text.Json this test process loads moved to major version {version.Major} "
            + "(8.0.0.0 when this file's MEASURED facts were taken). Re-measure MEASURED (a)-(k) in the "
            + "file header on the new version and update every note and probe that relies on them, and "
            + "check whether AllowOutOfOrderMetadataProperties (STJ 9+) and AllowDuplicateProperties "
            + "(STJ 10+) now compile here and need named pins in "
            + "EventStreamJsonOptions_PinnedSettings_MatchMeasuredExpectations.");
    }

    /// <summary>
    /// Pins the MAJOR version of the central <c>System.Text.Json</c> <c>PackageVersion</c> in
    /// <c>Directory.Packages.props</c> — the version the CLI ships, through central transitive
    /// pinning — at 10, the major the file header's MEASURED facts were re-checked against
    /// (running this file with the package referenced from the test project). A bump of that
    /// line to a new major fails this test, so the notes are re-measured on the version that
    /// actually ships rather than left describing an older one.
    /// </summary>
    [Fact]
    public void CentralSystemTextJsonPackageVersion_IsTheMajorTheNotesWereCheckedAgainst()
    {
        var propsPath = Path.Combine(FindRepoRoot(), "Directory.Packages.props");
        var document = System.Xml.Linq.XDocument.Load(propsPath);
        var versions = document
            .Descendants("PackageVersion")
            .Where(e => string.Equals((string?)e.Attribute("Include"), "System.Text.Json", StringComparison.Ordinal))
            .Select(e => (string?)e.Attribute("Version"))
            .ToList();

        var versionText = Assert.Single(versions);
        _output.WriteLine($"Central System.Text.Json PackageVersion: {versionText}");
        Assert.True(
            Version.TryParse(versionText, out var version),
            $"Directory.Packages.props: the System.Text.Json PackageVersion '{versionText}' is not a plain version.");
        Assert.True(
            version!.Major == 10,
            $"The central System.Text.Json PackageVersion moved to {versionText}; the CLI ships that "
            + "version. Re-measure MEASURED (a)-(k) in EventContractFreezeTests' file header on it "
            + "(reference the package from this test project in a scratch copy and run this file), "
            + "update any note that no longer holds, then update this pin.");
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
        // as CorrelationIds. Neither policy touches Extra's extension-data keys (MEASURED (k)
        // in the file header).
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
        // init-only property's own default-value expression already constructed. A member- or
        // type-level [JsonObjectCreationHandling] overrides this per property or per type, and
        // the censuses pin those attributes; this assertion pins the global default they
        // override.
        Assert.Equal(JsonObjectCreationHandling.Replace, options.PreferredObjectCreationHandling);

        // #586: decides what STJ produces when it parses a value into an `object`-typed member
        // — a JsonElement (STJ's default) or a boxed CLR primitive. No frozen record has an
        // `object`-typed member today (Observation and Extra are typed JsonElement, so nothing
        // relies on this); it is pinned because every setting that affects parsing is pinned.
        Assert.Equal(JsonUnknownTypeHandling.JsonElement, options.UnknownTypeHandling);

        // DefaultBufferSize is NOT pinned: it only tunes the internal buffer STJ grows from
        // while writing (measured default 16384 bytes) and has no effect on what bytes end up
        // on the wire — changing it cannot break a consumer, so it is out of this census's scope
        // by definition (representation, not performance).

        // Exactly one converter, and it is VerdictJsonConverter — not, e.g., a default enum
        // string converter that would emit "Pass" instead of the canonical "PASS" token. Order
        // matters if a second converter for an overlapping type were ever added (STJ tries
        // converters in list order), so the count is pinned alongside the identity. A
        // member-level [JsonConverter] takes precedence over this list (MEASURED (a) in the file
        // header), and the member census pins those.
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
        //     Modifiers.IsReadOnly == false after the JsonSerializerOptions that references it
        //     has been through MakeReadOnly(), UNTIL ITS FIRST USE: a modifier appended in that
        //     window took effect on the first Serialize call (renaming the wire property "Value"
        //     to "renamed"), after which Modifiers.IsReadOnly is true and Modifiers.Add throws
        //     InvalidOperationException.
        // Assert.Same below therefore proves CreateOptions installed a resolver no code can
        // append a modifier to, rather than one that carries no modifiers now but would accept
        // one from any caller that reaches Options.TypeInfoResolver before the first
        // (de)serialisation.
        Assert.Same(JsonSerializerOptions.Default.TypeInfoResolver, options.TypeInfoResolver);

        // No modifier rewrites any type's contract: the resolver's Modifiers list is empty.
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
