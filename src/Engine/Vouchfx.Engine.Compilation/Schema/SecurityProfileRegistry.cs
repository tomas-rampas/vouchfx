// Vouchfx.Engine.Compilation.Schema — SecurityProfileRegistry (authenticated-infrastructure-
// mtls, slice C — REQ-019/REQ-022).
//
// A compile-time, reflective, frozen-at-startup registry for the security 'profile'
// discriminator (REQ-019's open mechanism axis, the same role StepKindRegistry plays for a
// step's own <family>.<provider> 'type'), mirroring StepKindRegistry's own discovery pattern
// and freeze semantics (Vouchfx.Sdk.StepKindRegistry) deliberately closely — read that class
// first. Two consumers:
//   • DocumentValidator (REQ-019) — is a declared 'profile' NAME registered at all, regardless
//     of the target's kind? The schema's own 'profile' field carries no 'enum' (an open string
//     pattern only), so an unrecognised name is rejected here, at validation time, exactly like
//     an unregistered step 'type' is rejected by DocumentValidator.CollectUnknownStepTypeErrors
//     rather than by the composed schema.
//   • SecurityProfileWiringValidator (REQ-022) — does a declared (profile, target-kind) PAIR
//     resolve to an actual wiring? REQ-021's schema-level narrowing ($defs/dependency's own
//     final allOf clause) and this registry's built-in wirings are deliberately kept in sync:
//     BOTH now permit a security block only on a kafka dependency or on any declared service
//     (M1, fix round 2 — the schema forbids the block outright on the other twelve dependency
//     kinds, and WiredTargetKindsAtV1 below is the registry's own statement of the same set).
//     The registry is the one that is actually CHECKED at validation time — closing the gap
//     REQ-021's narrowing alone cannot: REQ-005's probe is engine-side and generic (it can only
//     confirm an endpoint SPEAKS TLS), while the actual client connection is provider-emitted,
//     so a schema-only narrowing that later drifts from what is actually wired would let a suite
//     validate a security profile with no real client-side implementation behind it — a false
//     assurance a probe alone cannot catch.
//
// The built-in 'tls'/'mtls' profiles are implemented THROUGH this registry (TlsProfileWiring /
// MtlsProfileWiring below, both discovered via the SAME [SecurityProfileWiring] reflective scan
// an out-of-tree profile would use), not beside it — exercising the seam rather than merely
// reserving it.
//
// NOTHING IN THIS FILE IS PUBLISHED (M2, peer review — fix round 2). Vouchfx.Engine.Compilation
// is a PACKABLE assembly with GenerateDocumentationFile=true, so a `public` type here ships in
// the NuGet package with its XML docs attached. A previous round left ISecurityProfileWiring,
// SecurityProfileRegistry, RegisteredSecurityProfileWiring and DuplicateSecurityProfileException
// public while their own XML summaries told a consumer the interface "is not published for
// out-of-tree implementation yet" — prose a consumer would read OUT OF THE PACKAGE THAT
// PUBLISHED IT. Every type in this file is therefore `internal`, and the decision the spec's own
// Out-of-scope section records ("no extension interface until a second profile exists to design a
// frozen abstraction from") is now enforced by the compiler rather than asserted by a comment
// that does not ship. Cross-assembly access is granted narrowly, via InternalsVisibleTo on this
// project (see Vouchfx.Engine.Compilation.csproj): Vouchfx.Engine.Runtime (a non-packable
// assembly — SecurityProfileWiringValidator, ProviderPipeline.Compile, both themselves internal)
// plus the two test assemblies. Widening any of this back to `public` is a publication decision,
// not a visibility tidy-up.
//
// TlsProfileWiring/MtlsProfileWiring are additionally file-local in spirit: nothing outside this
// file names the CONCRETE wiring types, only RegisteredSecurityProfileWiring's own
// Profile/Instance (a test proving REQ-022's "removing a wiring fails closed" filters
// SecurityProfileRegistry.BuiltIn.All by Profile and re-freezes the remainder, never referencing
// a concrete wiring type by name).
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// A single security-profile wiring: the mechanism name it implements (REQ-019's
/// <c>profile</c>, e.g. <c>"tls"</c>) and which declared target kinds it is actually wired
/// for.
/// </summary>
/// <remarks>
/// <para>
/// A "target kind" is either one of the thirteen <c>environment.dependencies.&lt;name&gt;.type</c>
/// values (e.g. <c>"kafka"</c>, <c>"redis"</c>), or the fixed sentinel
/// <see cref="SecurityProfileRegistry.ServiceTargetKind"/> for any <c>environment.services.&lt;name&gt;</c>
/// entry — services carry no per-kind discriminator of their own, and REQ-021's schema
/// narrowing does not restrict them, so every registered wiring is expected to recognise this
/// sentinel.
/// </para>
/// <para>
/// <c>internal</c>, and not part of the frozen v1 provider contract (<c>Vouchfx.Sdk</c>): this
/// interface is not published for out-of-tree implementation, in any package — see this file's
/// own header remarks for why that is enforced by visibility rather than by prose.
/// </para>
/// </remarks>
internal interface ISecurityProfileWiring
{
    /// <summary>The security-profile mechanism name this wiring implements, e.g. <c>"tls"</c>.</summary>
    string Profile { get; }

    /// <summary>
    /// <see langword="true"/> when this wiring is actually wired for <paramref name="targetKind"/> —
    /// a dependency <c>type</c> value, or <see cref="SecurityProfileRegistry.ServiceTargetKind"/>
    /// for any declared service.
    /// </summary>
    bool AppliesTo(string targetKind);
}

/// <summary>
/// Marks a concrete class as a discoverable <see cref="ISecurityProfileWiring"/>
/// implementation, mirroring <see cref="Vouchfx.Sdk.StepProviderAttribute"/>'s role for
/// <see cref="Vouchfx.Sdk.IStepProvider"/>.
/// </summary>
/// <remarks>
/// m3 (peer review, fix round 2): <c>internal</c>, like every other type in this file, so the
/// set of assemblies that can apply it is exactly the set this project grants
/// <c>InternalsVisibleTo</c> — this engine assembly and the two test assemblies. That makes
/// <see cref="SecurityProfileRegistry.BuildAndFreeze(IEnumerable{Assembly})"/>'s multi-assembly
/// parameter genuinely exercisable (a test assembly declares its own decorated wiring and the
/// scan finds it — see <c>SecurityProfileRegistryTests</c>), rather than a shape no caller could
/// ever satisfy. An out-of-tree assembly still cannot apply it, deliberately: that is the
/// publication decision this file's header records, not an oversight in this attribute.
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class SecurityProfileWiringAttribute : Attribute
{
}

/// <summary>
/// Raised when two wirings resolve to the same <see cref="ISecurityProfileWiring.Profile"/>
/// key during registry construction — mirrors
/// <see cref="Vouchfx.Sdk.DuplicateStepKindException"/>.
/// </summary>
internal sealed class DuplicateSecurityProfileException : Exception
{
    /// <summary>
    /// Initialises a new instance with a message naming the duplicated profile and both
    /// conflicting wiring type names.
    /// </summary>
    public DuplicateSecurityProfileException(string profile, string existingTypeName, string conflictingTypeName)
        : base(
            $"Duplicate security profile '{profile}': already registered by '{existingTypeName}', " +
            $"conflicting registration from '{conflictingTypeName}'. Each profile may only be " +
            "registered once.")
    {
    }
}

/// <summary>
/// An immutable snapshot of a single wiring discovered by <see cref="SecurityProfileRegistry"/>.
/// </summary>
internal sealed record RegisteredSecurityProfileWiring(string Profile, ISecurityProfileWiring Instance);

/// <summary>
/// Reflective registry of every known security-profile wiring, frozen at startup — mirrors
/// <see cref="Vouchfx.Sdk.StepKindRegistry"/>'s own discovery pattern and freeze semantics
/// (this file's own header remarks explain why the two are kept structurally parallel).
/// </summary>
/// <remarks>
/// Post-freeze mutation is structurally impossible: no public Add/Remove/Clear/Set* method
/// exists, and <see cref="All"/> is a snapshot the caller cannot mutate the registry through.
/// </remarks>
internal sealed class SecurityProfileRegistry
{
    /// <summary>
    /// The fixed sentinel <see cref="ISecurityProfileWiring.AppliesTo(string)"/> target-kind
    /// value for any <c>environment.services.&lt;name&gt;</c> entry — services carry no
    /// per-kind discriminator of their own (unlike a dependency's <c>type</c>), and REQ-021's
    /// schema narrowing never restricts a service, so every wiring that intends to cover
    /// services checks for this literal value.
    /// </summary>
    /// <remarks>
    /// n2 (peer review, fix round 2): this is a STRINGLY-TYPED sentinel sharing one value space
    /// with the thirteen dependency <c>type</c> values. A fourteenth dependency kind literally
    /// named <c>service</c> would alias this sentinel silently — every service-scoped wiring
    /// would start claiming that dependency kind, and the enum-derived guard in
    /// <c>SecurityProfileRegistryTests.DependencyKindsEnumerated_MatchesSchemasOwnTypeEnum</c>
    /// compares SETS of kind names and so would not notice. The collision is guarded explicitly
    /// instead, by <c>SecurityProfileRegistryTests.ServiceTargetKind_DoesNotCollideWithAnyDependencyKind</c>,
    /// which asserts the live composed schema's own <c>$defs/dependency.properties.type.enum</c>
    /// does not contain this value. Replacing the sentinel with a discriminated target-kind type
    /// is the structural fix, deferred: it would cross the (frozen) validator/registry seam for a
    /// collision no released schema can currently produce.
    /// </remarks>
    public const string ServiceTargetKind = "service";

    private readonly IReadOnlyDictionary<string, RegisteredSecurityProfileWiring> _wirings;

    private SecurityProfileRegistry(IReadOnlyDictionary<string, RegisteredSecurityProfileWiring> wirings)
    {
        _wirings = wirings;
        All = new ReadOnlyCollection<RegisteredSecurityProfileWiring>(wirings.Values.ToList());
    }

    /// <summary>Gets an immutable snapshot of every wiring registered in this registry.</summary>
    public IReadOnlyCollection<RegisteredSecurityProfileWiring> All { get; }

    /// <summary>
    /// The frozen, built-in registry (<c>tls</c> + <c>mtls</c>), discovered reflectively from
    /// this engine assembly's own <see cref="SecurityProfileWiringAttribute"/>-decorated
    /// types — the seam a future out-of-tree profile would be discovered through too (see this
    /// file's own header remarks).
    /// </summary>
    public static SecurityProfileRegistry BuiltIn { get; } =
        BuildAndFreeze(new[] { typeof(SecurityProfileRegistry).Assembly });

    /// <summary>
    /// Attempts to retrieve ANY wiring registered under <paramref name="profile"/>, regardless
    /// of target kind — REQ-019's "is this profile name known at all" check.
    /// </summary>
    public bool TryGet(string profile, out RegisteredSecurityProfileWiring? wiring) =>
        _wirings.TryGetValue(profile, out wiring);

    /// <summary>
    /// Comma-joined, ordinally-sorted display list of every profile name currently
    /// registered — the "registered: ..." tail of <see cref="DescribeUnknownProfile"/>, and
    /// the same list <c>DocumentValidator.CollectUnknownSecurityProfileErrors</c> used to build
    /// inline before G-MAJOR-1 centralised it here.
    /// </summary>
    public string RegisteredProfilesDisplayList =>
        string.Join(", ", All.Select(w => w.Profile).OrderBy(p => p, StringComparer.Ordinal));

    /// <summary>
    /// Builds the exact "unknown security profile" message text for <paramref name="profileValue"/>,
    /// naming the unknown profile and listing every registered one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// n1 + M1 (peer review, fix round 2). This method previously claimed BYTE-IDENTICAL wording
    /// across TWO call sites — <c>DocumentValidator.CollectUnknownSecurityProfileErrors</c> and
    /// <c>SchemaErrorCollector.FormatConstError</c>'s REQ-021 branch (the G-MAJOR-1 fix) — and
    /// the claim was false above 200 characters, because only the <c>FormatConstError</c> path
    /// passed the value through that class's own <c>TruncateForDisplay</c> bound. Both halves of
    /// that problem are gone: M1's schema tightening removed the REQ-021 <c>const</c> clause
    /// entirely (no profile is legal on a non-kafka dependency at 1.0, so there is nothing left
    /// for a <c>const</c> to pin), which deleted the second call site, and the truncation now
    /// lives HERE so the single remaining call site cannot lose it.
    /// </para>
    /// <para>
    /// The bound is not cosmetic: <c>profile</c> is an open string pattern with no length limit
    /// of its own, this text is serialised verbatim into <c>vouchfx validate --json</c>'s
    /// golden-pinned <c>ValidateJsonDiagnostic(Stage, Message)</c> document (and written to
    /// stdout), and the same author-controlled-value reasoning that produced
    /// <c>SchemaErrorCollector.MaxOffendingValueChars</c> applies unchanged. MINOR-1 (peer
    /// review, fix round 4) corrected the PREMISE only, never the conclusion: this text does NOT
    /// reach the §14 JSON Lines event stream. Traced: for a schema-invalid suite
    /// <c>ScenarioRunner</c> emits only <c>ScenarioStartedEvent</c>/<c>ScenarioCompletedEvent</c>
    /// and assigns the message to its own <c>earlyMessage</c> local, which reaches
    /// <c>output.WriteLineAsync</c> and nothing else — no event record carries it. The bound
    /// still stands: <c>validate --json</c> is <c>System.Text.Json</c>-serialised, so the
    /// unbounded-inflation and lone-surrogate arguments hold against that surface unchanged. The constant is
    /// mirrored rather than shared because <c>SchemaErrorCollector</c>'s copy is private to a
    /// class this one must not depend on; the two are pinned equal by
    /// <c>SecurityProfileRegistryTests.UnknownProfileMessage_TruncatesAtTheSameBoundAsSchemaErrorCollector</c>.
    /// </para>
    /// </remarks>
    public string DescribeUnknownProfile(string profileValue) =>
        $"unknown security profile '{TruncateForDisplay(profileValue)}' — not a registered " +
        $"profile (registered: {RegisteredProfilesDisplayList}).";

    /// <summary>
    /// The maximum number of characters of an author-supplied profile value
    /// <see cref="DescribeUnknownProfile"/> renders inline — mirrors
    /// <c>SchemaErrorCollector.MaxOffendingValueChars</c> (see that constant's own remarks for
    /// the security finding that introduced the bound).
    /// </summary>
    internal const int MaxOffendingValueChars = 200;

    /// <summary>
    /// Truncates <paramref name="value"/> to <see cref="MaxOffendingValueChars"/> with a
    /// "… (N chars total)" tail when it exceeds that bound, returning it unchanged otherwise —
    /// character-for-character the same rendering <c>SchemaErrorCollector.TruncateForDisplay</c>
    /// produces, so an author never sees two spellings of the same truncated value. Never splits
    /// a UTF-16 surrogate pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SEC-2 (peer review, fix round 3): the cut index is a CHAR index, so a naive
    /// <c>value[..MaxOffendingValueChars]</c> slice can land between a high surrogate and its
    /// low-surrogate partner, leaving invalid UTF-16 that throws or renders as U+FFFD when this
    /// text is later serialised. The defect was pre-existing in
    /// <c>SchemaErrorCollector.TruncateForDisplay</c> and was faithfully MIRRORED here; both
    /// copies are fixed together, because a known correctness bug duplicated across two files is
    /// how one becomes permanent. Same back-off idiom as
    /// <c>SuiteSetLoader.TruncateParseErrorMessage</c>.
    /// </para>
    /// <para>
    /// SEC-1 (peer review, fix round 3): <see langword="internal"/>, not
    /// <see langword="private"/>, so <c>SecurityProfileWiringValidator</c> (Vouchfx.Engine.Runtime,
    /// reached through this project's own <c>InternalsVisibleTo</c>) bounds its own
    /// author-controlled <c>profile</c> interpolation through THIS helper rather than growing a
    /// third copy. Widening the accessibility publishes nothing: every type in this file is
    /// internal, and the assembly's InternalsVisibleTo list is the reachable set.
    /// </para>
    /// </remarks>
    internal static string TruncateForDisplay(string value)
    {
        if (value.Length <= MaxOffendingValueChars)
        {
            return value;
        }

        var cut = MaxOffendingValueChars;
        while (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        var totalChars = value.Length.ToString(CultureInfo.InvariantCulture);
        return $"{value[..cut]}… ({totalChars} chars total)";
    }

    /// <summary>
    /// Attempts to resolve the <c>(profile, targetKind)</c> pair to a registered wiring that
    /// is actually wired for that kind — REQ-022's invariant.
    /// </summary>
    public bool TryResolve(string profile, string targetKind, out RegisteredSecurityProfileWiring? wiring)
    {
        if (_wirings.TryGetValue(profile, out var candidate) && candidate.Instance.AppliesTo(targetKind))
        {
            wiring = candidate;
            return true;
        }

        wiring = null;
        return false;
    }

    /// <summary>
    /// Scans each of the supplied <paramref name="assemblies"/> for concrete classes
    /// decorated with <see cref="SecurityProfileWiringAttribute"/>, then freezes the result
    /// into an immutable registry.
    /// </summary>
    /// <remarks>
    /// m3 (peer review, fix round 2): the reachable set of contributing assemblies is exactly
    /// the set granted <c>InternalsVisibleTo</c> by this project, since
    /// <see cref="SecurityProfileWiringAttribute"/> is <c>internal</c> — this engine assembly
    /// (the <see cref="BuiltIn"/> scan) and the two test assemblies. That is a real capability,
    /// not a decorative parameter: <c>SecurityProfileRegistryTests</c> declares its own decorated
    /// wiring type and proves this overload discovers it across the assembly boundary. An
    /// out-of-tree assembly cannot contribute, by design (see this file's header).
    /// </remarks>
    public static SecurityProfileRegistry BuildAndFreeze(IEnumerable<Assembly> assemblies)
    {
        var collected = new List<ISecurityProfileWiring>();
        var seenTypes = new HashSet<Type>();

        foreach (var assembly in assemblies)
        {
            IEnumerable<Type> assemblyTypes;
            try
            {
                assemblyTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                assemblyTypes = ex.Types.OfType<Type>();
            }

            foreach (var type in assemblyTypes)
            {
                if (!type.IsClass || type.IsAbstract)
                {
                    continue;
                }

                if (Attribute.IsDefined(type, typeof(SecurityProfileWiringAttribute), inherit: false) &&
                    typeof(ISecurityProfileWiring).IsAssignableFrom(type) &&
                    seenTypes.Add(type))
                {
                    collected.Add(InstantiateWiring(type));
                }
            }
        }

        return BuildAndFreeze(collected);
    }

    /// <summary>
    /// Indexes the supplied <paramref name="wirings"/> by their own
    /// <see cref="ISecurityProfileWiring.Profile"/> key and freezes the result into an
    /// immutable registry. The explicit-instance overload used directly by tests that need to
    /// simulate a wiring's ABSENCE (REQ-022's own acceptance: "removing a wiring must make a
    /// suite declaring that pair fail").
    /// </summary>
    public static SecurityProfileRegistry BuildAndFreeze(IReadOnlyCollection<ISecurityProfileWiring> wirings)
    {
        var map = new Dictionary<string, RegisteredSecurityProfileWiring>(StringComparer.Ordinal);

        foreach (var wiring in wirings)
        {
            if (map.TryGetValue(wiring.Profile, out var existing))
            {
                throw new DuplicateSecurityProfileException(
                    wiring.Profile,
                    existing.Instance.GetType().FullName ?? existing.Instance.GetType().Name,
                    wiring.GetType().FullName ?? wiring.GetType().Name);
            }

            map[wiring.Profile] = new RegisteredSecurityProfileWiring(wiring.Profile, wiring);
        }

        return new SecurityProfileRegistry(map);
    }

    private static ISecurityProfileWiring InstantiateWiring(Type type)
    {
        try
        {
            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException(
                    $"Activator.CreateInstance returned null for security-profile wiring type '{type.FullName}'.");

            return (ISecurityProfileWiring)instance;
        }
        catch (MissingMemberException ex)
        {
            throw new InvalidOperationException(
                $"Security-profile wiring type '{type.FullName}' decorated with " +
                "[SecurityProfileWiring] must have a public parameterless constructor.", ex);
        }
        catch (InvalidCastException ex)
        {
            throw new InvalidOperationException(
                $"Type '{type.FullName}' is decorated with [SecurityProfileWiring] but does not " +
                "implement ISecurityProfileWiring.", ex);
        }
    }
}

/// <summary>
/// The set of target kinds 1.0 will wire a security profile for, once slices D and E land:
/// a <c>kafka</c> dependency, or any declared service.
/// </summary>
/// <remarks>
/// <para>
/// M1 (peer review, fix round 2). Both built-in wirings share this ONE predicate, so the two
/// cannot drift apart by editing only one of them. The set is deliberately NOT "every kind the
/// schema happens to accept": a service is to get its TLS/mTLS client material through
/// REQ-023/REQ-024 (slice D), a kafka dependency through REQ-015 (slice E), and NOTHING ELSE is
/// planned for 1.0.
/// </para>
/// <para>
/// MAJOR-2 (peer review, fix round 4): this paragraph previously called the set "measured
/// against the slices that do the wiring". It is not measured — it is a FORECAST, and at this
/// commit NEITHER member is wired: REQ-024 records that all three HTTP-family providers emit a
/// bare <c>HttpClientHandler</c>, and REQ-015 that both Kafka providers emit a bare
/// <c>ProducerConfig</c>. The distinction is not pedantry, because the two directions carry
/// asymmetric risk. Rejecting the twelve excluded dependency kinds is safe even if the forecast
/// is wrong, since widening a validation-time gate after the freeze is permitted. ACCEPTING
/// <c>{kafka, service}</c> is NOT safe if the forecast is wrong: shrinking after 1.0 would
/// invalidate suites that already validated — the forbidden direction, and precisely the failure
/// REQ-021 exists to prevent, reproduced one level up inside the requirement itself. That is why
/// this set may only ever GROW before the freeze, and why a member that turns out unwired must
/// be removed BEFORE 1.0 rather than after.
/// </para>
/// <para>
/// The gate that enforces it is issue #354: v1.0 must not ship unless slices D and E have
/// actually wired every member named here, and the set must shrink before the freeze if either
/// slips. Do not weaken the narrowing to compensate for the forecast — the set is the right
/// forecast; only the earlier claim about its provenance was wrong.
/// </para>
/// <para>
/// Why <c>tls</c> is in here rather than universally wired: <see cref="ISecurityProfileWiring.AppliesTo"/>
/// is consulted at VALIDATION time (<c>SecurityProfileWiringValidator</c>, run from
/// <c>ProviderPipeline.Compile</c>), so it gates WHICH SUITES VALIDATE — identical semantics to
/// REQ-021's schema narrowing, which this series declared freeze-critical for exactly that
/// reason. A <c>tls</c> wiring returning <see langword="true"/> unconditionally would accept, at
/// 1.0, a <c>profile: tls</c> on (say) a postgres dependency for which nothing stages a TLS
/// connection at all: the engine-side REQ-005 probe would confirm the endpoint SPEAKS TLS while
/// the step's own provider-emitted client connected in plaintext — precisely the false assurance
/// REQ-022 exists to close. Tightening that in 1.1 would then reject suites that validated at
/// 1.0. Rejecting more now and widening later is the only safe direction across a freeze.
/// </para>
/// <para>
/// What the set costs an author, stated against what a provider actually does at step-execution
/// time rather than against what the schema accepts (MAJOR-1, fix round 4). A REST or SOAP API
/// under test is a declared service, and <c>http.rest</c>/<c>http.soap</c>/
/// <c>metrics-assert.prometheus</c> resolve <c>target</c> exclusively through
/// <c>IProjectContext.DeclaredServices</c>, so the service member costs that author nothing. A
/// Kafka broker is covered by the <c>kafka</c> dependency member; the two Kafka providers do
/// ALSO accept a service target, but that path stages no connection string yet and fails closed
/// at run time as an EnvironmentError, so "reachable as a declared service" is true of
/// validation only, not of a working suite. For the twelve excluded dependency kinds the cost is
/// real and total — every one of them is served by providers that resolve <c>target</c> only
/// through <c>DeclaredDependencies</c>, so there is no re-declaration that buys transport
/// security back, and that is a deliberate 1.0 position, not an oversight to paper over with
/// remediation advice that does not work. Widening happens in 1.1 as REQ-013 lands server-side
/// TLS for the remaining dependency kinds; at that point the two wirings will very likely stop
/// sharing one predicate (a kind may gain <c>tls</c> before it gains <c>mtls</c>), and splitting
/// them is the expected shape of that change, not a regression.
/// </para>
/// </remarks>
internal static class WiredTargetKindsAtV1
{
    internal static bool Contains(string targetKind) =>
        string.Equals(targetKind, "kafka", StringComparison.Ordinal) ||
        string.Equals(targetKind, SecurityProfileRegistry.ServiceTargetKind, StringComparison.Ordinal);
}

/// <summary>
/// The built-in <c>tls</c> profile: server-side TLS only, no client identity presented. Accepted
/// on a <c>kafka</c> dependency and on any declared service — the set 1.0 will wire once slices D
/// and E land, NOT a set measured against wiring that exists today (see
/// <see cref="WiredTargetKindsAtV1"/> for the forecast, its asymmetry and the #354 release gate,
/// and for why it is narrower than "every kind the schema once accepted"). REQ-021's schema
/// narrowing agrees by construction, deliberately rather than
/// by coincidence: <c>$defs/dependency</c>'s own final <c>allOf</c> clause now forbids the
/// <c>security</c> block outright on every non-kafka dependency kind.
/// </summary>
[SecurityProfileWiring]
internal sealed class TlsProfileWiring : ISecurityProfileWiring
{
    public string Profile => "tls";

    public bool AppliesTo(string targetKind) => WiredTargetKindsAtV1.Contains(targetKind);
}

/// <summary>
/// The built-in <c>mtls</c> profile: mutual TLS, presenting a client certificate. Accepted on a
/// <c>kafka</c> dependency and on any declared service — the same set
/// <see cref="TlsProfileWiring"/> covers, and on the same forecast-not-measurement footing (see
/// <see cref="WiredTargetKindsAtV1"/>); the spec's own
/// Out-of-scope section records why client certificates for any OTHER managed dependency kind
/// are deferred to 1.1.
/// </summary>
[SecurityProfileWiring]
internal sealed class MtlsProfileWiring : ISecurityProfileWiring
{
    public string Profile => "mtls";

    public bool AppliesTo(string targetKind) => WiredTargetKindsAtV1.Contains(targetKind);
}
