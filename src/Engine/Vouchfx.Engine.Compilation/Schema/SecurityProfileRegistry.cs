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
//     (the schema forbids the block outright on every other dependency
//     kind, and WiredTargetKindsAtV1 below is the registry's own statement of the same set).
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
// NOTHING IN THIS FILE IS PUBLISHED. Vouchfx.Engine.Compilation
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
/// A single security-profile wiring: the mechanism name it implements (the
/// <c>profile</c>, e.g. <c>"tls"</c>) and which declared target kinds it is actually wired
/// for.
/// </summary>
/// <remarks>
/// <para>
/// A "target kind" is either one of the <c>environment.dependencies.&lt;name&gt;.type</c>
/// values (e.g. <c>"kafka"</c>, <c>"redis"</c>), or the fixed sentinel
/// <see cref="SecurityProfileRegistry.ServiceTargetKind"/> for any <c>environment.services.&lt;name&gt;</c>
/// entry — services carry no per-kind discriminator of their own, and the schema
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
/// <c>internal</c>, like every other type in this file, so the
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
    /// per-kind discriminator of their own (unlike a dependency's <c>type</c>), and the
    /// schema narrowing never restricts a service, so every wiring that intends to cover
    /// services checks for this literal value.
    /// </summary>
    /// <remarks>
    /// this is a STRINGLY-TYPED sentinel sharing one value space
    /// with the dependency <c>type</c> values. A future dependency kind literally
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
    /// of target kind — the "is this profile name known at all" check.
    /// </summary>
    public bool TryGet(string profile, out RegisteredSecurityProfileWiring? wiring) =>
        _wirings.TryGetValue(profile, out wiring);

    /// <summary>
    /// Comma-joined, ordinally-sorted display list of every profile name currently
    /// registered — the "registered: ..." tail of <see cref="DescribeUnknownProfile"/>, and
    /// the same list consumed by <c>DocumentValidator.CollectUnknownSecurityProfileErrors</c>.
    /// </summary>
    public string RegisteredProfilesDisplayList =>
        string.Join(", ", All.Select(w => w.Profile).OrderBy(p => p, StringComparer.Ordinal));

    // Internal correction history (not XML documentation):
    // This method previously claimed BYTE-IDENTICAL wording
    // across TWO call sites — DocumentValidator.CollectUnknownSecurityProfileErrors and
    // SchemaErrorCollector.FormatConstError's REQ-021 branch — and
    // the claim was false above 200 characters, because only the FormatConstError path
    // passed the value through that class's own TruncateForDisplay bound. Both halves of
    // that problem are gone: the schema tightening removed the REQ-021 const clause
    // entirely (no profile is legal on a non-kafka dependency at 1.0, so there is nothing left
    // for a const to pin), which deleted the second call site, and the truncation now
    // lives HERE so the single remaining call site cannot lose it.
    // The bound is not cosmetic: profile is an open string pattern with no length limit
    // of its own, this text is serialised verbatim into vouchfx validate --json's
    // golden-pinned ValidateJsonDiagnostic(Stage, Message) document (and written to
    // stdout), and the same author-controlled-value reasoning that produced
    // SchemaErrorCollector.MaxOffendingValueChars applies unchanged. A later correction
    // changed the PREMISE only, never the conclusion: this text does NOT
    // reach the §14 JSON Lines event stream. Traced: for a schema-invalid suite
    // ScenarioRunner emits only ScenarioStartedEvent/ScenarioCompletedEvent
    // and assigns the message to its own earlyMessage local, which reaches
    // output.WriteLineAsync and nothing else — no event record carries it. The bound
    // still stands: validate --json is System.Text.Json-serialised, so the
    // unbounded-inflation and lone-surrogate arguments hold against that surface unchanged. The constant is
    // mirrored rather than shared because SchemaErrorCollector's copy is private to a
    // class this one must not depend on; the two are pinned equal by
    // SecurityProfileRegistryTests.UnknownProfileMessage_TruncatesAtTheSameBoundAsSchemaErrorCollector.
    /// <summary>
    /// Builds the exact "unknown security profile" message text for <paramref name="profileValue"/>,
    /// naming the unknown profile and listing every registered one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Truncation lives here so every caller receives the same bounded message. A profile is
    /// an open string pattern with no length limit; centralising the bound prevents caller-specific
    /// formatting from producing different messages for the same value.
    /// </para>
    /// <para>
    /// This text is serialised into <c>vouchfx validate --json</c> as
    /// <c>ValidateJsonDiagnostic(Stage, Message)</c> and written to stdout. It does not enter the
    /// JSON Lines event records: schema-invalid scenarios emit start/completion records without
    /// this message, while the runner writes its early message separately.
    /// The <c>System.Text.Json</c> surface still requires bounded inflation and valid UTF-16.
    /// The constant mirrors <c>SchemaErrorCollector.MaxOffendingValueChars</c> instead of coupling
    /// this registry to that collector.
    /// <c>SecurityProfileRegistryTests.UnknownProfileMessage_TruncatesAtTheSameBoundAsSchemaErrorCollector</c>
    /// pins the two bounds equal.
    /// </para>
    /// </remarks>
    public string DescribeUnknownProfile(string profileValue) =>
        $"unknown security profile '{TruncateForDisplay(profileValue)}' - not a registered " +
        $"profile (registered: {RegisteredProfilesDisplayList}).";

    /// <summary>
    /// The maximum number of characters of an author-supplied profile value
    /// <see cref="DescribeUnknownProfile"/> renders inline — mirrors
    /// <c>SchemaErrorCollector.MaxOffendingValueChars</c> (see that constant's own remarks for
    /// the security finding that introduced the bound).
    /// </summary>
    internal const int MaxOffendingValueChars = 200;

    // Internal correction history (not XML documentation):
    // The cut index is a CHAR index, so a naive
    // value[..MaxOffendingValueChars] slice can land between a high surrogate and its
    // low-surrogate partner, leaving invalid UTF-16 that throws or renders as U+FFFD when this
    // text is later serialised. The defect was pre-existing in
    // SchemaErrorCollector.TruncateForDisplay and was faithfully MIRRORED here; both
    // copies are fixed together, because a known correctness bug duplicated across two files is
    // how one becomes permanent. Same back-off idiom as
    // SuiteSetLoader.TruncateParseErrorMessage.
    // Internal, not
    // private, so SecurityProfileWiringValidator (Vouchfx.Engine.Runtime,
    // reached through this project's own InternalsVisibleTo) bounds its own
    // author-controlled profile interpolation through THIS helper rather than growing a
    // third copy. Widening the accessibility publishes nothing: every type in this file is
    // internal, and the assembly's InternalsVisibleTo list is the reachable set.
    /// <summary>
    /// Truncates <paramref name="value"/> to <see cref="MaxOffendingValueChars"/> with a
    /// "… (N chars total)" tail when it exceeds that bound, returning it unchanged otherwise —
    /// character-for-character the same rendering <c>SchemaErrorCollector.TruncateForDisplay</c>
    /// produces, so an author never sees two spellings of the same truncated value. Never splits
    /// a UTF-16 surrogate pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cut index counts UTF-16 code units. A naive
    /// <c>value[..MaxOffendingValueChars]</c> can leave a lone high surrogate, which can throw
    /// or render as U+FFFD when serialised. Backing off preserves complete surrogate pairs,
    /// using the same idiom as <c>SuiteSetLoader.TruncateParseErrorMessage</c> and
    /// <c>SchemaErrorCollector.TruncateForDisplay</c>.
    /// </para>
    /// <para>
    /// This helper is <see langword="internal"/> so
    /// <c>SecurityProfileWiringValidator</c> in <c>Vouchfx.Engine.Runtime</c> can reuse it through
    /// <c>InternalsVisibleTo</c> instead of maintaining a third truncation implementation.
    /// All types in this file are internal; the friend-assembly list defines the reachable set.
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
        return $"{value[..cut]}... ({totalChars} chars total)";
    }

    /// <summary>
    /// Attempts to resolve the <c>(profile, targetKind)</c> pair to a registered wiring that
    /// is actually wired for that kind — the invariant.
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
    /// the reachable set of contributing assemblies is exactly
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
    /// simulate a wiring's absence ("removing a wiring must make a
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

// Internal correction history (not XML documentation):
// The set of target kinds 1.0 wires a security profile for, both delivered (slices D and E):
// a kafka dependency, or any declared service.
// Both built-in wirings share this ONE predicate, so the two
// cannot drift apart by editing only one of them. The set is deliberately NOT "every kind the
// schema happens to accept": a service gets its TLS/mTLS client material through
// REQ-023/REQ-024 (slice D, landed), a kafka dependency through REQ-015 (slice E, landed), and
// NOTHING ELSE is planned for 1.0.
// This paragraph previously called the set "measured
// against the slices that do the wiring". It was not measured then — it was a FORECAST, and at
// that commit NEITHER member was wired: REQ-024 recorded that all three HTTP-family providers
// emitted a bare HttpClientHandler, and REQ-015 that both Kafka providers emitted a bare
// ProducerConfig.
// Status, updated by slice E (REQ-015/REQ-023-as-amended) — both members are now
// WIRED and MEASURED, and the forecast is discharged:
//
//     service — WIRED, measured. A service declaring security
//     is staged as its secured endpoint (REQ-023), in the form its own consumer uses — an
//     https URL where the HTTP family addresses it, a bare host:port bootstrap
//     authority where the Kafka families do — and http.rest/http.soap/
//     metrics-assert.prometheus configure their emitted HttpClientHandler from
//     the resolved configuration (REQ-024). Evidenced by execution, not by validation:
//     HttpsClientCertificateTests runs the compiled CSX against a mutual-TLS listener
//     and passes only when the declared client certificate actually arrives at the server.
//
//
//     kafka — WIRED (slice E, REQ-015). Both Kafka providers emit
//     KafkaSecurity_Helpers.ConfigureClient onto their
//     ProducerConfig/ConsumerConfig,
//     setting SecurityProtocol, SslCaLocation and — under mtls — the
//     client certificate/key pair from the resolved configuration for the step's target.
//     Pinned by KafkaSecurityWiringTests in both provider test projects, and by this
//     file's own EveryWiredProfile_IsNamedInTheEmittedHelpersProfileSwitch.
//
// So issue #354's release gate below is SATISFIED as of slice E: nothing here needs removing
// before 1.0. The paragraphs that follow are kept for the discipline they state, not because a
// member is outstanding.
// The distinction is not pedantry, because the two directions carry
// asymmetric risk. Rejecting the excluded dependency kinds is safe even if the forecast
// is wrong, since widening a validation-time gate after the freeze is permitted. ACCEPTING
// {kafka, service} is NOT safe if the forecast is wrong: shrinking after 1.0 would
// invalidate suites that already validated — the forbidden direction, and precisely the failure
// REQ-021 exists to prevent, reproduced one level up inside the requirement itself. That is why
// this set may only ever GROW before the freeze, and why a member that turns out unwired must
// be removed BEFORE 1.0 rather than after.
// The gate that enforces it is issue #354: v1.0 must not ship unless slices D and E have
// actually wired every member named here, and the set must shrink before the freeze if either
// slips. Both have now landed and both members are wired, so the gate is discharged — it stays
// documented here because the RULE it encodes outlives it: any member added to this set before
// the freeze must be wired before 1.0 ships, or removed.
// Why tls is in here rather than universally wired: ISecurityProfileWiring.AppliesTo
// is consulted at VALIDATION time (SecurityProfileWiringValidator, run from
// ProviderPipeline.Compile), so it gates WHICH SUITES VALIDATE — identical semantics to
// REQ-021's schema narrowing, which this series declared freeze-critical for exactly that
// reason. A tls wiring returning true unconditionally would accept, at
// 1.0, a profile: tls on (say) a postgres dependency for which nothing stages a TLS
// connection at all: the engine-side REQ-005 probe would confirm the endpoint SPEAKS TLS while
// the step's own provider-emitted client connected in plaintext — precisely the false assurance
// REQ-022 exists to close. Tightening that in 1.1 would then reject suites that validated at
// 1.0. Rejecting more now and widening later is the only safe direction across a freeze.
// What the set costs an author, stated against what a provider actually does at step-execution
// time rather than against what the schema accepts. A REST or SOAP API
// under test is a declared service, and http.rest/http.soap/
// metrics-assert.prometheus resolve target exclusively through
// IProjectContext.DeclaredServices, so the service member costs that author nothing. A
// Kafka broker is covered by the kafka dependency member, and — since REQ-023's
// amendment — by the service member too: a broker authored under environment.services is
// staged as the bootstrap authority its clients consume and the two Kafka providers emit the
// svc:: key for it, so "reachable as a declared service" is now true of a working suite
// and not of validation alone. For the excluded dependency kinds the cost is
// real and total — every one of them is served by providers that resolve target only
// through DeclaredDependencies, so there is no re-declaration that buys transport
// security back, and that is a deliberate 1.0 position, not an oversight to paper over with
// remediation advice that does not work. Widening happens in 1.1 as REQ-013 lands server-side
// TLS for the remaining dependency kinds; at that point the two wirings will very likely stop
// sharing one predicate (a kind may gain tls before it gains mtls), and splitting
// them is the expected shape of that change, not a regression.
/// <summary>
/// The target kinds supported by both built-in transport-security profiles:
/// a <c>kafka</c> dependency or any declared service.
/// </summary>
/// <remarks>
/// <para>
/// Both wirings share one predicate so their accepted target sets cannot drift apart.
/// A secured service is staged as the endpoint its consumer uses: an <c>https</c> URL
/// for HTTP-family providers or a <c>host:port</c> bootstrap authority for Kafka.
/// <c>http.rest</c>, <c>http.soap</c> and <c>metrics-assert.prometheus</c> configure
/// <c>HttpClientHandler</c> from the resolved security configuration.
/// <c>HttpsClientCertificateTests</c> verifies that the client identity reaches a mutual-TLS server.
/// </para>
/// <para>
/// Both Kafka providers splice <c>KafkaSecurity_Helpers.ConfigureClient</c> into their
/// <c>ProducerConfig</c>/<c>ConsumerConfig</c>, setting <c>SecurityProtocol</c>,
/// <c>SslCaLocation</c> and, for <c>mtls</c>, the client certificate/key pair.
/// <c>KafkaSecurityWiringTests</c> and
/// <c>EveryWiredProfile_IsNamedInTheEmittedHelpersProfileSwitch</c> pin that wiring.
/// </para>
/// <para>
/// Validation must never accept a target kind whose provider still connects in plaintext:
/// a successful engine-side TLS probe alone cannot establish client transport security.
/// Tightening the accepted set after a contract freeze would invalidate existing suites.
/// Widen only when the complete server/client path is implemented, and wire or remove an
/// announced member before freezing the contract.
/// </para>
/// <para>
/// REST and SOAP services and Kafka dependencies are covered. A Kafka broker declared
/// under <c>environment.services</c> is also covered through its <c>svc::</c> authority.
/// Other dependency kinds are deliberately excluded: their providers resolve only
/// <c>DeclaredDependencies</c>, so re-declaring them as services does not restore transport
/// security. Future dependency support may require separate <c>tls</c>/<c>mtls</c> predicates
/// when a kind gains server authentication before mutual authentication.
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
/// on a <c>kafka</c> dependency and on any declared service — both halves wired and measured
/// (see
/// <see cref="WiredTargetKindsAtV1"/> for the evidence behind each and for why the set is
/// narrower than "every kind the schema once accepted"). The schema
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
/// <see cref="TlsProfileWiring"/> covers, both members wired and measured
/// (see <see cref="WiredTargetKindsAtV1"/>); the spec's own
/// Out-of-scope section records why client certificates for any OTHER managed dependency kind
/// are deferred to 1.1.
/// </summary>
[SecurityProfileWiring]
internal sealed class MtlsProfileWiring : ISecurityProfileWiring
{
    public string Profile => "mtls";

    public bool AppliesTo(string targetKind) => WiredTargetKindsAtV1.Contains(targetKind);
}
