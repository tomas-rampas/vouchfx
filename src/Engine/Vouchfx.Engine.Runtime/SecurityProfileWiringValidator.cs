// Vouchfx.Engine.Runtime — SecurityProfileWiringValidator (authenticated-infrastructure-mtls,
// slice C — REQ-022).
//
// The invariant REQ-021's schema-level narrowing alone cannot enforce: for every declared
// `security` block, the (profile, target-kind) pair must resolve to a REGISTERED WIRING
// (Vouchfx.Engine.Compilation.Schema.SecurityProfileRegistry), checked at validation time —
// not merely "this profile name is syntactically legal on this kind", which is all
// $defs/dependency's own const-conditional narrowing (REQ-021) can express.
//
// Why this matters (see the spec's own REQ-022 rationale): REQ-005's confirmation probe is
// engine-side and generic — it can only confirm the resolved endpoint SPEAKS TLS. The actual
// client connection is provider-emitted, per step. A schema narrowing that later drifts from
// what a provider has actually wired (a profile or a target kind added on one side without the
// other) would let a suite validate a security profile with NO real client-side implementation
// behind it — a false assurance the probe alone cannot catch, arriving through a different
// door. This validator closes that gap by checking the SAME registry REQ-019's unknown-profile
// cross-check consults (DocumentValidator.CollectUnknownSecurityProfileErrors), just with the
// target kind added — the one extra fact ONLY this pipeline stage has (a dependency's own
// `type`, a service's fixed `service` sentinel), not the schema-validation stage.
//
// Called from ProviderPipeline.Compile — the SAME pre-topology stage EnvironmentSecurityValidator
// already runs in, immediately after it — so a declared (profile, kind) pair with no registered
// wiring is caught at `vouchfx validate` / pre-topology `vouchfx run` time, never surfaced later
// as an opaque connection failure with no diagnosable cause.
//
// Deliberately a separate file/class from EnvironmentSecurityValidator, mirroring that file's
// own header note: each concern gets a dedicated static class so it is tested in isolation.
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;
using Vouchfx.Engine.Compilation.Schema;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Validates that every declared <c>security</c> block's <c>(profile, target-kind)</c> pair
/// resolves to a registered <see cref="ISecurityProfileWiring"/> in a
/// <see cref="SecurityProfileRegistry"/> (REQ-022).
/// </summary>
internal static class SecurityProfileWiringValidator
{
    /// <summary>
    /// Validates every declared <c>security</c> block's <c>(profile, target-kind)</c> pair
    /// across <paramref name="ast"/>'s <c>environment.services</c> and
    /// <c>environment.dependencies</c> against <paramref name="registry"/>.
    /// </summary>
    /// <param name="ast">The normalised scenario AST.</param>
    /// <param name="registry">
    /// The security-profile registry to resolve against — <see cref="SecurityProfileRegistry.BuiltIn"/>
    /// in production; a caller-supplied, reduced registry in a test proving a removed wiring
    /// fails closed (REQ-022's own acceptance).
    /// </param>
    /// <returns>
    /// The first unresolved pair encountered (services checked before dependencies; within
    /// each, map iteration order), naming the owning service/dependency, the declared profile,
    /// and the target kind — or <see langword="null"/> when every declared pair resolves.
    /// </returns>
    internal static ValidationFailure? Validate(ScenarioAst ast, SecurityProfileRegistry registry)
    {
        var services = ast.Environment?.Services;
        if (services is not null)
        {
            foreach (var (name, spec) in services)
            {
                var failure = ValidateOne(
                    spec.Security, "services", name, SecurityProfileRegistry.ServiceTargetKind, registry);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        var dependencies = ast.Environment?.Dependencies;
        if (dependencies is not null)
        {
            foreach (var (name, spec) in dependencies)
            {
                var failure = ValidateOne(spec.Security, "dependencies", name, spec.Type, registry);
                if (failure is not null)
                {
                    return failure;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Validates one service's or dependency's declared <see cref="SecuritySpec"/> (when
    /// present) against <paramref name="registry"/>.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> <paramref name="security"/> block, or one whose
    /// <see cref="SecuritySpec.Profile"/> is itself <see langword="null"/>, is not this
    /// method's concern: the former means no security was declared at all; the latter is a
    /// malformed document the schema's own <c>required</c> rule (REQ-001) already rejects
    /// earlier in the pipeline (Stage 1, schema — see <c>ScenarioValidator</c>) whenever this
    /// method is reached THROUGH that path. This method is also reachable directly (e.g. a
    /// test driving <c>ProviderPipeline.Compile</c> without a prior schema pass), so it stays
    /// defensive rather than assuming a non-null <c>Profile</c>.
    /// </remarks>
    private static ValidationFailure? ValidateOne(
        SecuritySpec? security,
        string ownerKindPlural,
        string ownerName,
        string targetKind,
        SecurityProfileRegistry registry)
    {
        if (security?.Profile is not { } profile)
        {
            return null;
        }

        if (registry.TryResolve(profile, targetKind, out _))
        {
            return null;
        }

        return new ValidationFailure(
            $"environment.{ownerKindPlural}.{ownerName}.security: profile '{profile}' has no " +
            $"registered wiring for target kind '{targetKind}'.")
        {
            IsSecurityPreflight = true,
        };
    }
}
