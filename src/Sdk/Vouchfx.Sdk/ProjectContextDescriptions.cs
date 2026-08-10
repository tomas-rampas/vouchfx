// Vouchfx.Sdk — ProjectContextDescriptions (m9 fix, fix round 2 — PR #349 follow-up).
//
// DescribeDeclaredSurfaces was declared byte-identically five times, once per provider
// that reconciles a step's 'target' against IProjectContext (HttpRestProvider,
// HttpSoapProvider, MetricsAssertPrometheusProvider, MqExpectKafkaProvider,
// MqPublishKafkaProvider) — a private static method every provider re-implemented rather
// than shared, on the (since-revisited) grounds that the frozen v1 provider contract left
// no shared seam for it. A PUBLIC STATIC helper on Vouchfx.Sdk is purely additive to the
// SdkContractFreezeTests golden (a new type + member, never a change to an existing one),
// so there is no contract-freeze reason to keep the duplication. Moving it here gives every
// current and future provider ONE implementation to depend on instead of five (soon six)
// copy-pasted ones.
namespace Vouchfx.Sdk;

/// <summary>
/// Shared formatting helpers for messages that describe an <see cref="IProjectContext"/>'s
/// declared infrastructure surface (services and dependencies).
/// </summary>
public static class ProjectContextDescriptions
{
    /// <summary>
    /// Formats the "Declared dependencies: ...; Declared services: ..." tail a provider
    /// appends to an unresolved-target validation message — names are sorted ordinally for
    /// deterministic output, and an empty surface reads <c>"(none)"</c> rather than an empty
    /// list.
    /// </summary>
    /// <param name="ctx">
    /// The project context whose <see cref="IProjectContext.DeclaredDependencies"/> and
    /// <see cref="IProjectContext.DeclaredServices"/> maps are described.
    /// </param>
    public static string DescribeDeclaredSurfaces(IProjectContext ctx)
    {
        // G-C (gatekeeper, fix round 3): this is a PUBLIC API on a contract frozen for the
        // whole v1.x engine series (§13) — unlike the five private copies it replaced, callers
        // are not limited to this file's own five in-tree call sites, so a null ctx must fail
        // fast with a located, actionable exception rather than an NRE surfacing from whichever
        // line below happens to dereference it first.
        ArgumentNullException.ThrowIfNull(ctx);

        var deps = ctx.DeclaredDependencies.Count == 0
            ? "(none)"
            : string.Join(", ", ctx.DeclaredDependencies.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var services = ctx.DeclaredServices.Count == 0
            ? "(none)"
            : string.Join(", ", ctx.DeclaredServices.Keys.OrderBy(k => k, StringComparer.Ordinal));

        return $"Declared dependencies: {deps}. Declared services: {services}.";
    }
}
