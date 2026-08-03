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
//     final allOf clause) and this registry's built-in wirings are deliberately kept in sync
//     (both permit 'mtls' only for a kafka dependency or any service), but the registry is the
//     one that is actually CHECKED at validation time — closing the gap REQ-021's narrowing
//     alone cannot: REQ-005's probe is engine-side and generic (it can only confirm an endpoint
//     SPEAKS TLS), while the actual client connection is provider-emitted, so a schema-only
//     narrowing that later drifts from what is actually wired would let a suite validate a
//     security profile with no real client-side implementation behind it — a false assurance a
//     probe alone cannot catch.
//
// The built-in 'tls'/'mtls' profiles are implemented THROUGH this registry (TlsProfileWiring /
// MtlsProfileWiring below, both discovered via the SAME [SecurityProfileWiring] reflective scan
// an out-of-tree profile would use), not beside it — exercising the seam rather than merely
// reserving it. No public SDK extension interface is published yet (see the spec's own
// Out-of-scope section): ISecurityProfileWiring and [SecurityProfileWiringAttribute] are not
// part of the frozen Vouchfx.Sdk provider contract, deliberately, since one implementation is
// not enough to design a frozen abstraction from — they are `public` on THIS engine assembly
// only because Vouchfx.Engine.Runtime (a separate assembly, SecurityProfileWiringValidator)
// and this assembly's own DocumentValidator both need to consult the registry, mirroring the
// same public-but-not-SDK-frozen convention DocumentValidator/ScenarioValidator/SchemaComposer
// already use for cross-assembly engine collaboration. TlsProfileWiring/MtlsProfileWiring
// themselves stay internal: nothing outside this file needs to name the CONCRETE wiring types,
// only RegisteredSecurityProfileWiring's own Profile/Instance (a test proving REQ-022's
// "removing a wiring fails closed" filters SecurityProfileRegistry.BuiltIn.All by Profile and
// re-freezes the remainder, never referencing a concrete wiring type by name).
using System.Collections.ObjectModel;
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
/// Not part of the frozen v1 provider contract (<c>Vouchfx.Sdk</c>) — this interface lives in
/// the engine assembly and is not published for out-of-tree implementation yet (see this
/// file's own header remarks).
/// </para>
/// </remarks>
public interface ISecurityProfileWiring
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
[AttributeUsage(AttributeTargets.Class)]
internal sealed class SecurityProfileWiringAttribute : Attribute
{
}

/// <summary>
/// Raised when two wirings resolve to the same <see cref="ISecurityProfileWiring.Profile"/>
/// key during registry construction — mirrors
/// <see cref="Vouchfx.Sdk.DuplicateStepKindException"/>.
/// </summary>
public sealed class DuplicateSecurityProfileException : Exception
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
public sealed record RegisteredSecurityProfileWiring(string Profile, ISecurityProfileWiring Instance);

/// <summary>
/// Reflective registry of every known security-profile wiring, frozen at startup — mirrors
/// <see cref="Vouchfx.Sdk.StepKindRegistry"/>'s own discovery pattern and freeze semantics
/// (this file's own header remarks explain why the two are kept structurally parallel).
/// </summary>
/// <remarks>
/// Post-freeze mutation is structurally impossible: no public Add/Remove/Clear/Set* method
/// exists, and <see cref="All"/> is a snapshot the caller cannot mutate the registry through.
/// </remarks>
public sealed class SecurityProfileRegistry
{
    /// <summary>
    /// The fixed sentinel <see cref="ISecurityProfileWiring.AppliesTo(string)"/> target-kind
    /// value for any <c>environment.services.&lt;name&gt;</c> entry — services carry no
    /// per-kind discriminator of their own (unlike a dependency's <c>type</c>), and REQ-021's
    /// schema narrowing never restricts a service, so every wiring that intends to cover
    /// services checks for this literal value.
    /// </summary>
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
    /// Builds the exact "unknown security profile" message text for <paramref name="profileValue"/>
    /// — shared by <c>DocumentValidator.CollectUnknownSecurityProfileErrors</c>'s own REQ-019
    /// registry cross-check and <c>SchemaErrorCollector.FormatConstError</c>'s REQ-021 branch
    /// (G-MAJOR-1), so an author sees BYTE-IDENTICAL wording naming the unknown profile and
    /// listing every registered one, regardless of which of the two call sites the composed
    /// schema happened to route the failure through for a given dependency kind.
    /// </summary>
    /// <remarks>
    /// Before this fix, an unregistered profile on a NON-KAFKA dependency kind never reached
    /// DocumentValidator's own registry cross-check at all: REQ-021's per-kind narrowing
    /// (<c>$defs/dependency</c>'s final <c>allOf</c> clause) occupies the SAME instance
    /// location with its own <c>[const]</c> error for every value other than <c>'tls'</c>, so
    /// DocumentValidator's pointer-keyed deferral (avoiding a genuine double-report for a
    /// wrong-cased <c>[pattern]</c> value, the mirror of issue #265) always yielded to that
    /// <c>[const]</c> finding instead — which, unguarded, claimed the profile WAS a recognised
    /// mechanism merely unwired for that kind, actively misdirecting an author chasing a typo
    /// or a genuinely unknown name toward a kind that could never support it. Consulting this
    /// SAME registry from <c>FormatConstError</c> (both live in this Compilation assembly)
    /// closes the gap: an unregistered profile now renders THIS message everywhere, never the
    /// "only 'x' is wired for this kind" one, which stays reserved for a profile that IS
    /// registered but genuinely unwired for the declared kind (e.g. 'mtls' on redis).
    /// </remarks>
    public string DescribeUnknownProfile(string profileValue) =>
        $"unknown security profile '{profileValue}' — not a registered profile " +
        $"(registered: {RegisteredProfilesDisplayList}).";

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
/// The built-in <c>tls</c> profile: server-side TLS only, no client identity presented.
/// Legal on every target kind (every dependency kind and every service) — REQ-021's schema
/// narrowing never restricts it, and this wiring's own <see cref="AppliesTo"/> agrees by
/// construction, deliberately, rather than by coincidence.
/// </summary>
[SecurityProfileWiring]
internal sealed class TlsProfileWiring : ISecurityProfileWiring
{
    public string Profile => "tls";

    public bool AppliesTo(string targetKind) => true;
}

/// <summary>
/// The built-in <c>mtls</c> profile: mutual TLS, presenting a client certificate. Wired only
/// for a <c>kafka</c> dependency and any declared service (REQ-021's schema narrowing pins
/// every OTHER dependency kind's <c>security.profile</c> to <c>'tls'</c> via
/// <c>$defs/dependency</c>'s own final <c>allOf</c> clause — this wiring's own
/// <see cref="AppliesTo"/> agrees by construction, deliberately, rather than by coincidence;
/// see the spec's Out-of-scope section for why client certificates for any OTHER managed
/// dependency kind are deferred).
/// </summary>
[SecurityProfileWiring]
internal sealed class MtlsProfileWiring : ISecurityProfileWiring
{
    public string Profile => "mtls";

    public bool AppliesTo(string targetKind) =>
        string.Equals(targetKind, "kafka", StringComparison.Ordinal) ||
        string.Equals(targetKind, SecurityProfileRegistry.ServiceTargetKind, StringComparison.Ordinal);
}
