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
// IDENTICAL — by (wire name, CLR type, required, extension-data) — to the
// rendered set from the same public-property enumeration the golden uses.
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
using System.Text.Json.Serialization;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Abstractions.Reproducibility;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

/// <summary>
/// S08-F-02: the frozen-v1-event-wire-contract golden-snapshot gate.
/// </summary>
public sealed class EventContractFreezeTests
{
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
    /// unfrozen.  (The two Reproducibility nested records — SecretReferenceDigest,
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
            sb.Append("record ");
            sb.Append(record.Name);
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

        return $"property {FormatType(prop.PropertyType)} {prop.Name}"
            + $"{wirePart}{required}{setterKind}";
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
    /// A member's wire-relevant signature, comparable between the golden's rendered
    /// (public-property-only) view and System.Text.Json's own mapped-member view
    /// under the shared <see cref="EventStreamJson.Options"/>. Two members with an
    /// equal <see cref="MemberSignature"/> are indistinguishable on the wire.
    /// </summary>
    private readonly record struct MemberSignature(
        string Wire, Type ClrType, bool Required, bool ExtensionData);

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
                    ExtensionData: p.GetCustomAttribute<JsonExtensionDataAttribute>() is not null),
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
            var signature = new MemberSignature(p.Name, p.PropertyType, p.IsRequired, p.IsExtensionData);

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
    /// (e.g. <c>[JsonIgnore]</c>) — plus any member present in both whose signature
    /// (wire name, CLR type, required, extension-data) disagrees between the two
    /// views. Returns an empty list when the two sets are identical.
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
            "The v1 event-wire census (#578) found a member System.Text.Json maps that the "
            + "golden's public-property-only render cannot show (or vice versa), or a member "
            + "whose wire name, CLR type, required-ness or extension-data flag differs between "
            + "the two views. This means a [JsonInclude] field, a non-public [JsonInclude] "
            + "member, a [JsonIgnore] on a rendered property or a required-flag change could "
            + "change the wire shape of a frozen record WITHOUT moving "
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
