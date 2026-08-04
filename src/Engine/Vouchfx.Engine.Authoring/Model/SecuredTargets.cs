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
