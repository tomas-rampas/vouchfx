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
//   VerdictCounts,
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
        var wire = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
        var wirePart = wire is null ? string.Empty : $" [wire={wire}]";

        var required = prop.GetCustomAttribute<RequiredMemberAttribute>() is not null
            ? " [required]"
            : string.Empty;

        var setterKind = prop.SetMethod switch
        {
            null => " [get-only]",
            { } setter when IsInitOnly(setter) => " [init]",
            _ => " [set]",
        };

        return $"property {FormatType(prop.PropertyType)} {prop.Name}"
            + $"{wirePart}{required}{setterKind}";
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
