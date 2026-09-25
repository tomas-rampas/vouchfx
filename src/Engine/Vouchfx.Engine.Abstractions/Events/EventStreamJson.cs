// Shared JSON serialisation helpers for the vouchfx structured event stream.
//
// Design constraints:
//   • System.Text.Json only — no third-party serialisers (§5.7, library
//     invariants).  STJ ships in-box with net8.0.
//   • WriteIndented = false: JSON Lines requires exactly one compact object per
//     line; embedded newlines would break every line-oriented consumer.
//   • DefaultIgnoreCondition = WhenWritingNull: null optional fields (e.g.
//     CorrelationIds) are omitted from the wire, matching the §14.4 examples.
//   • No naming policy is applied.  The EventEnvelope properties that need
//     non-default wire names carry explicit [JsonPropertyName] attributes.
//     Applying a CamelCase policy would re-rename those properties a second time
//     (e.g. RunId → "runId" before the attribute, then the attribute wins, but
//     the Extra bag would also be affected), so the safest and most explicit
//     approach is no policy at all.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

// VerdictJsonConverter lives in the parent namespace; bring it in without
// polluting the public API surface of this file.
using Vouchfx.Engine.Abstractions;

namespace Vouchfx.Engine.Abstractions.Events;

/// <summary>
/// Static helper that owns the single shared <see cref="JsonSerializerOptions"/>
/// instance and provides <see cref="ToLine"/> / <see cref="FromLine"/> for the
/// structured JSON Lines event stream (§14.4).
/// </summary>
/// <remarks>
/// All event emission and all renderer ingestion MUST go through these helpers
/// so that the wire format is controlled from one place.  Creating ad-hoc
/// <see cref="JsonSerializerOptions"/> instances in caller code is prohibited —
/// it risks inconsistent serialisation (e.g. accidentally writing indented JSON
/// that violates the JSON Lines contract).
/// </remarks>
public static class EventStreamJson
{
    /// <summary>
    /// The single shared <see cref="JsonSerializerOptions"/> instance used for
    /// all event-stream serialisation and deserialisation.
    /// </summary>
    /// <remarks>
    /// Options selected:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>WriteIndented = false</c> — JSON Lines mandate: one compact object
    ///     per line; embedded newlines would corrupt line-oriented consumers.
    ///   </description></item>
    ///   <item><description>
    ///     <c>DefaultIgnoreCondition = WhenWritingNull</c> — null optional fields
    ///     are omitted from the wire, consistent with the §14.4 examples.
    ///   </description></item>
    ///   <item><description>
    ///     No <c>PropertyNamingPolicy</c> — wire names are controlled exclusively
    ///     via <c>[JsonPropertyName]</c> attributes on the record properties, which
    ///     is safer than relying on a naming transformation that could interact
    ///     unexpectedly with extension-data keys.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The instance is frozen via <see cref="JsonSerializerOptions.MakeReadOnly()"/>
    /// at construction time so that callers cannot mutate the shared options after
    /// first use, which would silently corrupt serialisation across the process.
    /// </para>
    /// </remarks>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new VerdictJsonConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// Serialises <paramref name="envelope"/> to a compact single-line JSON
    /// string suitable for appending to a JSON Lines stream.
    /// </summary>
    /// <param name="envelope">The event envelope to serialise.</param>
    /// <returns>
    /// A compact JSON object string with no embedded newline characters.
    /// </returns>
    public static string ToLine(EventEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    /// <summary>
    /// Deserialises a single JSON Lines record into an <see cref="EventEnvelope"/>.
    /// Unknown fields are captured in <see cref="EventEnvelope.Extra"/> and are
    /// preserved on re-serialisation (§14 forward-compatibility guarantee).
    /// </summary>
    /// <param name="line">
    /// A single JSON object string, as produced by <see cref="ToLine"/> or
    /// emitted by any compatible engine version.
    /// </param>
    /// <returns>The deserialised envelope.</returns>
    /// <exception cref="JsonException">
    /// Thrown if <paramref name="line"/> is not valid JSON, does not represent an
    /// object, a required field (<c>type</c>, <c>runId</c>) is absent, or a mapped
    /// field has the wrong JSON type (e.g. a numeric <c>runId</c>).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if <paramref name="line"/> is the JSON literal <c>null</c>, or a
    /// required field (<c>type</c>, <c>runId</c>) is present but explicitly
    /// <see langword="null"/> on the wire (e.g. <c>"runId": null</c>).
    /// </exception>
    /// <remarks>
    /// An empty string (e.g. <c>"runId": ""</c>) is legal wire content and is
    /// accepted, not rejected.
    /// </remarks>
    public static EventEnvelope FromLine(string line)
    {
        var envelope = JsonSerializer.Deserialize<EventEnvelope>(line, Options)
            ?? throw new InvalidOperationException(
                "Deserialisation of event-stream line produced a null result; " +
                "the input was not a JSON object.");

        // STJ's `required` enforces presence only, not non-null. `RespectNullableAnnotations`
        // (an STJ 9+ opt-in, off by default) is not used here because Abstractions resolves
        // no STJ package (the 10.0.8 pin in Directory.Packages.props is a transitive security
        // pin that reaches only other projects' graphs), so it compiles against the in-box
        // STJ 8. A wire line carrying "runId": null or "type": null
        // therefore satisfies `required` and deserialises the property to null despite its
        // non-nullable `string` type.
        // An ABSENT required field is a different, pre-existing failure mode: STJ's
        // own `required`-member enforcement throws JsonException for that case,
        // never reaching the null checks below — the two paths stay distinct.
        // An EMPTY string, by contrast, is deliberately ACCEPTED here: "" is a
        // legal (if unusual) value for a non-nullable `string` field, satisfies
        // `required`, and is pinned as legal wire content by
        // TerminalRendererTests.Render_ScenarioStarted_EmptyRunId_StillDerivesTotal
        // — only the JSON-null case is malformed. Every in-tree consumer of this method
        // (the three renderers, EventHistoryReader, TelemetryEventBuilder) already
        // treats InvalidOperationException as "skip this line" per §14's per-line
        // tolerance, so failing fast here — instead of handing a contract-violating
        // envelope on to the caller — makes a null required field malformed
        // everywhere uniformly, rather than one renderer (HtmlRenderer, whose
        // _lastScenarioByRun dictionary key rejects a null RunId) aborting the
        // whole document while the others render a scenario from data that was
        // never valid.
        if (envelope.RunId is null)
        {
            throw new InvalidOperationException(
                "Event-stream line has a null runId; the envelope field is required.");
        }

        if (envelope.Type is null)
        {
            throw new InvalidOperationException(
                "Event-stream line has a null type; the envelope field is required.");
        }

        return envelope;
    }

    /// <summary>
    /// Serialises <paramref name="payload"/> to a compact single-line JSON
    /// string suitable for appending to a JSON Lines stream.
    /// </summary>
    /// <typeparam name="T">
    /// The typed event-payload record (e.g. <see cref="StepCompletedEvent"/>).
    /// </typeparam>
    /// <param name="payload">The payload record to serialise.</param>
    /// <returns>
    /// A compact JSON object string with no embedded newline characters.
    /// </returns>
    public static string ToLine<T>(T payload) =>
        JsonSerializer.Serialize(payload, Options);

    /// <summary>
    /// Deserialises a single JSON Lines record into the specified typed
    /// event-payload record.
    /// </summary>
    /// <typeparam name="T">
    /// The typed event-payload record (e.g. <see cref="StepCompletedEvent"/>).
    /// </typeparam>
    /// <param name="line">
    /// A single JSON object string, as produced by <see cref="ToLine{T}"/> or
    /// emitted by any compatible engine version.
    /// </param>
    /// <returns>The deserialised payload record.</returns>
    /// <exception cref="JsonException">
    /// Thrown if <paramref name="line"/> is not valid JSON or cannot be
    /// deserialised as <typeparamref name="T"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if deserialisation produces a <see langword="null"/> result, or if a
    /// non-nullable-annotated reference-type member that the guard treats as required
    /// (System.Text.Json's own required set, plus a <see langword="required"/> member behind a
    /// <see cref="SetsRequiredMembersAttribute"/> constructor; see the remarks) deserialised to
    /// null (#573), or if reading such a member's value itself threw (see the third bullet
    /// below).
    /// </exception>
    /// <remarks>
    /// <para>
    /// The guard refuses a null in a reference-type member that System.Text.Json's contract for
    /// the type that bound the line — the derived type, for a polymorphic
    /// <typeparamref name="T"/> — treats as required: the C# <see langword="required"/> modifier
    /// or <see cref="JsonRequiredAttribute"/>, on a property or a
    /// <see cref="JsonIncludeAttribute"/> field STJ maps, public or not, declared or inherited. A
    /// member stays guarded when the constructor STJ deserialises through carries
    /// <see cref="SetsRequiredMembersAttribute"/>, even though STJ then no longer enforces the
    /// member's presence; a type handled by a type-level custom <see cref="JsonConverter"/>
    /// exposes no members to that contract at all — whatever instance the converter's
    /// <c>Read</c> returns — and is therefore never guarded, since the converter owns null
    /// handling for it.
    /// </para>
    /// <para>
    /// STJ's <c>required</c> enforces presence only, not non-null, so a wire line carrying e.g.
    /// <c>"scenarioId": null</c> would otherwise satisfy <c>required string ScenarioId</c> and
    /// hand the caller a record whose non-nullable member is null. Value types are excluded
    /// outright: a non-nullable one (<c>Verdict</c>, <c>long</c>) cannot hold null at all, so
    /// guarding it is unnecessary, and a nullable one (<c>Nullable&lt;T&gt;</c>, e.g. a future
    /// <c>required long?</c>) is excluded for the same reason a nullable REFERENCE type is (next
    /// sentence) — it is itself nullable-annotated and legitimately allows null — even though the
    /// check below treats every value type as one case for simplicity. A
    /// <see langword="required"/> reference-type member that is itself nullable-annotated (e.g. a
    /// future <c>required string?</c>) is excluded too, for that same reason.
    /// </para>
    /// <para>
    /// <c>runId</c> is guarded here because every typed record declares it
    /// <see langword="required"/>; <c>type</c> is not (no typed record marks it
    /// <see langword="required"/>), so a <c>"type": null</c> line yields a null
    /// <c>Type</c> unless <see cref="FromLine(string)"/> accepts the line first, as the
    /// engine's own readers do.
    /// </para>
    /// <para>
    /// A member's nullable ANNOTATION — not merely its CLR type — is what excludes it, read via
    /// <see cref="NullabilityInfoContext"/>: a member is guarded only when that read state is
    /// <see cref="NullabilityState.NotNull"/> (or, see below, when the read itself throws). A
    /// type compiled WITHOUT a nullable context (no <c>#nullable enable</c> and no
    /// <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c>) reports
    /// <see cref="NullabilityState.Unknown"/> for every reference-type member and therefore gets
    /// NO guard at all — every in-tree record compiles under this project's repo-wide
    /// <c>&lt;Nullable&gt;enable&lt;/Nullable&gt;</c>, so this only matters for a third-party
    /// <typeparamref name="T"/> built without it.
    /// </para>
    /// <para>
    /// Three further hardenings (Abstractions ships as a published NuGet package a host may
    /// run under conditions this solution never builds with):
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <strong>Nullability metadata unavailable.</strong> If the host disables
    ///     <c>NullabilityInfoContext.IsSupported</c> (the
    ///     <c>NullabilityInfoContextSupport=false</c> MSBuild property),
    ///     <c>NullabilityInfoContext.Create</c> throws
    ///     <see cref="InvalidOperationException"/> for EVERY scanned member. Left uncaught, that would
    ///     make <see cref="FromLine{T}"/> refuse every valid line for every typed record in that
    ///     host, silently: the Planner would count every line skipped and the telemetry builder
    ///     would tally zero scenarios, with no signal anything was wrong. A member is instead
    ///     treated as guarded (as if <see cref="NullabilityState.NotNull"/>) when the
    ///     nullability read itself throws — over-guarding a hypothetical nullable
    ///     <see langword="required"/> member there is the safer failure than silently
    ///     discarding every event line. A default <c>PublishTrimmed</c>/<c>PublishAot</c> publish
    ///     never reaches this code: it disables reflection-based System.Text.Json, so
    ///     <see cref="EventStreamJson"/>'s static initialiser throws first. On System.Text.Json 9
    ///     or later, STJ's own reflection resolver reads <see cref="NullabilityInfoContext"/> and
    ///     throws <see cref="InvalidOperationException"/> for every type with a reference-typed
    ///     member in such a host — which includes every type this guard has anything to check —
    ///     before this guard runs, so this hardening changes behaviour only on System.Text.Json 8.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>Collectible types.</strong> A <typeparamref name="T"/> loaded into a
    ///     collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> (e.g. a type from
    ///     an assembly a host loads into a collectible context, such as one passed through
    ///     <c>RoslynScriptCompiler</c>'s <c>collectibleProbingPaths</c>) is never added to the
    ///     static per-type cache below: caching it there
    ///     would pin the type — and so its ALC — alive for the process's remaining lifetime,
    ///     defeating the whole point of a collectible context. This closes no leak that exists
    ///     today (the shared <see cref="Options"/> serialiser already resolves and pins types
    ///     through STJ's own reflection cache, so a collectible <typeparamref name="T"/>
    ///     reaching this method at all is already not fully unloadable) — it only avoids a
    ///     SECOND, independent cache adding to that.
    ///   </description></item>
    ///   <item><description>
    ///     <strong>A throwing getter.</strong> A third-party record's required property
    ///     could compute its value and throw. Reading it via
    ///     <see cref="PropertyInfo.GetValue(object?)"/> wraps such a throw in
    ///     <see cref="System.Reflection.TargetInvocationException"/> — outside this method's
    ///     documented exception surface and outside every in-tree consumer's
    ///     <c>JsonException or InvalidOperationException</c> catch filter — so it is unwrapped
    ///     and re-thrown as <see cref="InvalidOperationException"/> naming the wire name,
    ///     keeping the failure inside the type every consumer already handles.
    ///   </description></item>
    /// </list>
    /// </remarks>
    // No path the CLI engine takes reaches any of the three hardenings today: no project here
    // sets PublishTrimmed, PublishAot or NullabilityInfoContextSupport, and the only typed
    // FromLine<T> calls under src/ (two in EventHistoryReader, two in TelemetryEventBuilder)
    // pass non-collectible Abstractions records whose required members are auto-properties.
    // The collectible branch is reachable only through a host that loads its own assembly into
    // a collectible context (e.g. via RoslynScriptCompiler's collectibleProbingPaths, which
    // ScenarioRunner never passes) and calls FromLine<T> with a type from it.
    public static T FromLine<T>(string line)
    {
        var payload = JsonSerializer.Deserialize<T>(line, Options)
            ?? throw new InvalidOperationException(
                $"Deserialisation of event-stream line as {typeof(T).Name} produced a null result; " +
                "the input was not a JSON object.");

        // The runtime type is used only when STJ's OWN polymorphism resolution
        // (JsonTypeInfo.PolymorphismOptions, driven by [JsonPolymorphic] or [JsonDerivedType])
        // chose it for this T:
        // for a polymorphic T, STJ deserialises through the DERIVED type's contract, and that
        // derived type is where a `required` reference-type member actually lives and was
        // presence-checked during binding — keying the lookup on typeof(T) instead would
        // silently miss a derived-only required member. For every other T — including one bound
        // through a type-level custom JsonConverter, whose Read may legitimately construct and
        // return an instance of a different type than T — the contract stays T's own:
        // payload.GetType() there would read the RETURNED type's default reflection contract,
        // a contract the converter's binding never went through, and could refuse a null the
        // converter already accepted (or the reverse).
        var contractType = !typeof(T).IsValueType
            && Options.GetTypeInfo(typeof(T)).PolymorphismOptions is not null
            ? payload.GetType()
            : typeof(T);
        var requiredMembers = GetRequiredReferenceMembers(contractType);
        for (var i = 0; i < requiredMembers.Length; i++)
        {
            var member = requiredMembers[i];
            object? value;
            if (member.Member is PropertyInfo property)
            {
                try
                {
                    value = property.GetValue(payload);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    // A throwing getter (unreachable for every in-tree record today) would
                    // otherwise surface as TargetInvocationException — outside this method's
                    // documented exceptions and every in-tree catch filter. Unwrap and re-throw
                    // as the type every consumer already treats as "skip this line". The inner
                    // exception travels as InnerException only: its message may carry a value
                    // from the line, and every guard message stays fixed text plus the wire and
                    // record names.
                    throw new InvalidOperationException(
                        $"Event-stream line's {member.WireName} member threw while being read for "
                        + $"the required-reference-member guard on {contractType.Name}.",
                        ex.InnerException);
                }
            }
            else
            {
                // A [JsonInclude] field has no getter to throw — FieldInfo.GetValue never
                // wraps in TargetInvocationException, so no unwrap is needed on this path.
                value = ((FieldInfo)member.Member).GetValue(payload);
            }

            if (value is null)
            {
                throw new InvalidOperationException(
                    $"Event-stream line has a null {member.WireName}; the {contractType.Name} field is required.");
            }
        }

        return payload;
    }

    /// <summary>
    /// Cache of <see cref="ComputeRequiredReferenceMembers"/>'s result, keyed by the contract
    /// type <see cref="FromLine{T}"/> resolved for the call — the closed generic type it was
    /// called with, or the runtime type when STJ's own polymorphism resolution selected it (see
    /// <see cref="FromLine{T}"/>'s body). A type from
    /// a collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/> is deliberately
    /// never added here (see <see cref="FromLine{T}"/>'s remarks) — its members are computed
    /// fresh on every call instead.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, RequiredReferenceMember[]>
        s_requiredReferenceMembersByType = new();

    /// <summary>
    /// One of a typed record's own <see langword="required"/> reference-type members that
    /// <see cref="FromLine{T}"/> null-checks: the reflected member (a <see cref="PropertyInfo"/>,
    /// or a <see cref="FieldInfo"/> carrying <see cref="JsonIncludeAttribute"/> — used to read
    /// the deserialised value) paired with its wire name (used in the exception message).
    /// </summary>
    private readonly record struct RequiredReferenceMember(MemberInfo Member, string WireName);

    private static RequiredReferenceMember[] GetRequiredReferenceMembers(Type type)
    {
        if (type.IsCollectible)
        {
            return ComputeRequiredReferenceMembers(type, ReadNullabilityState);
        }

        return s_requiredReferenceMembersByType.GetOrAdd(
            type, static t => ComputeRequiredReferenceMembers(t, ReadNullabilityState));
    }

    /// <summary>
    /// Reflects, via <see cref="Options"/>'s own <c>JsonTypeInfo.Properties</c> contract, every
    /// member STJ maps for <paramref name="type"/> and treats as <see langword="required"/> — or
    /// carries <see cref="RequiredMemberAttribute"/> even when STJ itself reports
    /// <c>IsRequired == false</c>, which it does for a member when the constructor STJ
    /// deserialises through carries
    /// <see cref="SetsRequiredMembersAttribute"/> (STJ then no longer enforces the member's
    /// presence, but the guard still treats it as required) — whatever its visibility, declared
    /// or inherited, with the wire name STJ itself uses, unlike a hand-rolled
    /// <see cref="Type.GetProperties()"/> / <see cref="Type.GetFields()"/> scan (a non-public
    /// member carrying <see cref="JsonIncludeAttribute"/>, including one declared on a base
    /// class, is mapped by STJ and therefore scanned here too). A <c>[JsonIgnore]</c> member,
    /// which STJ still lists here with a null <c>Set</c>, is skipped
    /// outright: STJ never binds it to or from the wire, so a required-but-unassigned one must
    /// never refuse every line. Of those, the
    /// ones that are also a non-nullable reference type are returned, using
    /// <paramref name="readNullabilityState"/> to obtain each candidate member's nullability
    /// read state (normally <see cref="ReadNullabilityState"/>; overridable only from tests, so
    /// the "nullability read throws" hardening can be exercised deterministically without a
    /// host built with <c>NullabilityInfoContextSupport=false</c>). A property with no getter is
    /// skipped outright, before the nullability read: it cannot be read to null-check it, so
    /// reaching <see cref="PropertyInfo.GetValue(object?)"/> in <see cref="FromLine{T}"/> would
    /// otherwise throw <see cref="ArgumentException"/> instead of the documented exception
    /// surface, for a member a throwing nullability read had (incorrectly) treated as guarded.
    /// </summary>
    private static RequiredReferenceMember[] ComputeRequiredReferenceMembers(
        Type type, Func<MemberInfo, NullabilityState> readNullabilityState)
    {
        var members = new List<RequiredReferenceMember>();
        var typeInfo = Options.GetTypeInfo(type);

        foreach (var p in typeInfo.Properties)
        {
            if (p.PropertyType.IsValueType)
            {
                continue;
            }

            if (p.AttributeProvider is not MemberInfo member)
            {
                // Every JsonPropertyInfo under the shared reflection-based Options carries the
                // declaring PropertyInfo/FieldInfo as its AttributeProvider; a resolver without
                // one cannot occur here.
                continue;
            }

            // A [JsonIgnore] member still appears in JsonTypeInfo.Properties — with both Get and
            // Set null — and STJ itself never reads or writes it. Skip it here too, BEFORE the
            // required test below: it would otherwise still carry RequiredMemberAttribute (the
            // C# `required` modifier is on the member, not on the JSON mapping) and fall through
            // to PropertyInfo.GetValue in FromLine{T}, which returns whatever the backing field
            // holds — null when a [SetsRequiredMembers] constructor never assigns it — refusing
            // every line for a member the wire never carries at all. Measured safe: every member
            // that must stay guarded here has p.Set != null; a [JsonRequired] get-only property
            // bound through a constructor is refused by STJ during contract resolution and never
            // reaches this method at all.
            if (p.Set is null)
            {
                continue;
            }

            // STJ reports IsRequired == false for a member mapped through a constructor
            // carrying [SetsRequiredMembers], even though the member itself still carries the
            // C# `required` modifier (RequiredMemberAttribute) — test the MemberInfo directly
            // so a required member behind a [SetsRequiredMembers] constructor stays guarded.
            if (!p.IsRequired && !member.IsDefined(typeof(RequiredMemberAttribute), inherit: false))
            {
                continue;
            }

            if (member is PropertyInfo { GetMethod: null })
            {
                continue;
            }

            if (!IsNonNullableReference(member, readNullabilityState))
            {
                continue;
            }

            members.Add(new RequiredReferenceMember(member, p.Name));
        }

        // An array, not IReadOnlyList<T>: FromLine{T} iterates this on every call with a `for`
        // loop over .Length rather than `foreach` over an interface, avoiding the boxed
        // enumerator allocation a `foreach` over IReadOnlyList<T> would otherwise cost per line.
        return members.ToArray();
    }

    /// <summary>
    /// Reads <paramref name="member"/>'s nullability state via <paramref name="readNullabilityState"/>
    /// and reports whether it is guarded (<see cref="NullabilityState.NotNull"/>, or the read
    /// itself throwing — see <see cref="FromLine{T}"/>'s remarks).
    /// </summary>
    private static bool IsNonNullableReference(
        MemberInfo member, Func<MemberInfo, NullabilityState> readNullabilityState)
    {
        NullabilityState nullabilityState;
        try
        {
            nullabilityState = readNullabilityState(member);
        }
        catch (InvalidOperationException)
        {
            // A host built with NullabilityInfoContextSupport=false makes Create throw for
            // every member. See FromLine{T}'s remarks for the full rationale: treat the
            // member as guarded rather than let every consumer silently discard every line
            // for every typed record.
            nullabilityState = NullabilityState.NotNull;
        }

        return nullabilityState == NullabilityState.NotNull;
    }

    private static NullabilityState ReadNullabilityState(MemberInfo member) => member switch
    {
        PropertyInfo property => new NullabilityInfoContext().Create(property).ReadState,
        FieldInfo field => new NullabilityInfoContext().Create(field).ReadState,
        _ => throw new ArgumentOutOfRangeException(
            nameof(member), member, "Expected a PropertyInfo or FieldInfo."),
    };

    /// <summary>
    /// Test-only accessor (visible to <c>Vouchfx.Engine.Abstractions.Tests</c> via
    /// <c>InternalsVisibleTo</c>) for the wire names of <paramref name="type"/>'s own
    /// null-guarded required reference members. Takes a <see cref="Type"/> directly (rather
    /// than being reached generically through reflection from the test) so the census in
    /// <c>EventStreamJsonRequiredReferenceNullTests</c> can call it for any record resolved by
    /// name from the golden, not just a compile-time-known <c>T</c>.
    /// </summary>
    internal static IReadOnlyList<string> GetRequiredReferenceMemberWireNamesForTests(Type type) =>
        GetRequiredReferenceMembers(type).Select(m => m.WireName).ToList();

    /// <summary>
    /// Test-only accessor: <see langword="true"/> when <paramref name="type"/>'s required
    /// reference members are currently held in the static cache. Used to pin that a
    /// collectible type's members are computed fresh every call rather than cached.
    /// </summary>
    internal static bool IsCachedForTests(Type type) => s_requiredReferenceMembersByType.ContainsKey(type);

    /// <summary>
    /// Test-only accessor: computes <paramref name="type"/>'s required reference-type members
    /// using <paramref name="nullabilityReader"/> in place of the real
    /// <see cref="NullabilityInfoContext"/>-backed reader, bypassing the cache entirely. Lets a
    /// test simulate <c>NullabilityInfoContext.Create</c> throwing (as it does under a host
    /// built with <c>NullabilityInfoContextSupport=false</c>) deterministically, without
    /// needing that host — <c>IsSupported</c> is read once via a static field on the type's
    /// first use in the process, so flipping the real
    /// <c>System.Reflection.NullabilityInfoContext.IsSupported</c> <see cref="AppContext"/>
    /// switch mid test-run has no effect once anything else in the process has already
    /// touched <see cref="NullabilityInfoContext"/> (measured: confirmed via a throwaway probe
    /// that an early <c>AppContext.SetSwitch</c> only takes effect before the type's first
    /// touch in the process).
    /// </summary>
    internal static IReadOnlyList<string> ComputeRequiredReferenceMemberWireNamesForTests(
        Type type, Func<MemberInfo, NullabilityState> nullabilityReader) =>
        ComputeRequiredReferenceMembers(type, nullabilityReader).Select(m => m.WireName).ToList();
}
