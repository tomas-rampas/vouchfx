// Vouchfx.Engine.Authoring — SecuredTargets (authenticated-infrastructure-mtls, slice E —
// m5, peer review fix round three).
//
// ONE spelling of "which declared targets carry a `security` block, and in what order".
//
// It had grown to three, in three assemblies: `SuiteTopology.DeclaresSecurity` (the
// no-accessor guard), `EnvironmentSecurityValidator.DeclaresSecurity` (the cheap
// does-this-suite-claim-anything question), and `SecuredEndpointProbe.EnumerateSecuredTargets`
// (the walk the probe actually confirms). All three read the same two dictionaries in the same
// services-then-dependencies order, and the probe's own remarks and the guard's own remarks
// each assert — in prose — that they agree with the others. This branch's own
// `SecurityArtifactPath` header argues exactly the opposite discipline for exactly this shape:
// "two spellings of one security rule is how the two drift". Three is worse.
//
// `Vouchfx.Engine.Authoring` is the same home that argument chose for `IsContainedWithin`, for
// the same reason: Orchestration and Runtime both reference it and neither references the other,
// and it is where `SecuritySpec`/`EnvironmentSpec` already live.
namespace Vouchfx.Engine.Authoring.Model;

/// <summary>
/// One declared target carrying a <c>security</c> block.
/// </summary>
/// <param name="Name">The declared service or dependency name.</param>
/// <param name="Kind">
/// <c>"service"</c> for a declared service, or the dependency's declared <c>type</c>
/// (e.g. <c>"kafka"</c>) — the same discriminator the security-profile registry's
/// <c>AppliesTo</c> takes.
/// </param>
/// <param name="Security">The declared block itself, never <see langword="null"/>.</param>
public readonly record struct SecuredTarget(string Name, string Kind, SecuritySpec Security);

/// <summary>
/// Enumerates the declared targets that carry a <c>security</c> block.
/// </summary>
public static class SecuredTargets
{
    /// <summary>
    /// The target kind reported for a declared service, as opposed to a dependency's own
    /// declared <c>type</c>.
    /// </summary>
    public const string ServiceKind = "service";

    /// <summary>
    /// The document segment naming the services map in a field path:
    /// <c>environment.<strong>services</strong>.&lt;name&gt;.security.…</c>.
    /// </summary>
    public const string ServicesFieldSegment = "services";

    /// <summary>
    /// The document segment naming the dependencies map in a field path:
    /// <c>environment.<strong>dependencies</strong>.&lt;name&gt;.security.…</c>.
    /// </summary>
    public const string DependenciesFieldSegment = "dependencies";

    /// <summary>
    /// The field-path segment for the map <paramref name="target"/> was declared in:
    /// <see cref="ServicesFieldSegment"/> for a service, <see cref="DependenciesFieldSegment"/>
    /// otherwise.
    /// </summary>
    /// <param name="target">A target yielded by <see cref="Enumerate"/>.</param>
    /// <remarks>
    /// <para>
    /// Every diagnostic naming a <c>security</c> field spells the same path
    /// (<c>environment.&lt;plural&gt;.&lt;name&gt;.security.&lt;field&gt;</c>). This member exists
    /// so the DERIVATION of the plural from a <see cref="SecuredTarget"/> is written once:
    /// <c>ScenarioRunner</c>'s secret-reference scan is its consumer, and
    /// <c>EnvironmentSecurityValidator</c>'s own walk uses the two segment constants above.
    /// </para>
    /// <para>
    /// It is NOT yet the single home of the plural across the engine. Several producers that walk
    /// services and dependencies themselves still pass the literals —
    /// <c>SecurityProfileWiringValidator</c>, <c>SecurityConfigurationAccessor</c> and
    /// <c>EnvironmentMapper</c> among them — and routing those is filed separately rather than
    /// done here. Claiming otherwise would be the kind of universal this file's own header warns
    /// about, asserted in prose and false in the tree.
    /// </para>
    /// <para>
    /// <see cref="SecuredTarget.Kind"/> is the fixed <see cref="ServiceKind"/> sentinel for a
    /// service and the dependency's own declared <c>type</c> otherwise, so a dependency typed
    /// literally <c>"service"</c> would map to the services segment. That is unreachable from
    /// YAML twice over: <c>$defs/dependency</c>'s <c>type</c> is a closed enum with no
    /// <c>service</c> member, and a <c>security</c> block is forbidden on any dependency whose
    /// type is not <c>kafka</c>.
    /// </para>
    /// </remarks>
    public static string PluralFor(SecuredTarget target) =>
        target.Kind == ServiceKind ? ServicesFieldSegment : DependenciesFieldSegment;

    /// <summary>
    /// Yields every declared target carrying a <c>security</c> block: <strong>services first,
    /// then dependencies</strong>, each in declaration order.
    /// </summary>
    /// <param name="environment">The suite's environment declaration, or <see langword="null"/>.</param>
    /// <returns>The secured targets, in the fixed order above; empty when none is declared.</returns>
    /// <remarks>
    /// The order is part of the contract, not an implementation detail: the pre-topology
    /// validator and the post-topology probe both walk it, so a suite with two faults reports
    /// the same one at both stages.
    /// </remarks>
    public static IEnumerable<SecuredTarget> Enumerate(EnvironmentSpec? environment)
    {
        if (environment?.Services is { } services)
        {
            foreach (var (name, spec) in services)
            {
                if (spec.Security is { } declared)
                {
                    yield return new SecuredTarget(name, ServiceKind, declared);
                }
            }
        }

        if (environment?.Dependencies is { } dependencies)
        {
            foreach (var (name, spec) in dependencies)
            {
                if (spec.Security is { } declared)
                {
                    yield return new SecuredTarget(name, spec.Type, declared);
                }
            }
        }
    }

    /// <summary>
    /// True when any declared service or dependency carries a <c>security</c> block.
    /// </summary>
    /// <param name="environment">The suite's environment declaration, or <see langword="null"/>.</param>
    /// <remarks>
    /// The cheap question "does this suite claim any security at all", asked by callers that
    /// must apply a security-only rule without paying for the full validation walk. Defined in
    /// terms of <see cref="Enumerate"/> rather than beside it, so the two cannot answer
    /// differently.
    /// </remarks>
    public static bool Any(EnvironmentSpec? environment) => Enumerate(environment).Any();
}
