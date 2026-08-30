// Vouchfx.Engine.Orchestration — IKeptTopology (#364).
//
// The Docker-free construction seam for "a topology someone else built and owns the lifetime of".
//
// WHY IT EXISTS. #364 records three defects found on the `--watch` path in two review rounds, and
// names the reason CI could never have caught any of them: the kept-topology entry point took a
// CONCRETE SuiteTopology, whose constructor is private and whose only factory needs Docker. So
// nothing downstream of that parameter — confirmation rendering, transport-notice replay,
// reset/reseed ordering, anything added later — was assertable at unit speed. Every one of the
// three was found by a human running a container drill.
//
// WHY IT IS EXACTLY THESE SEVEN MEMBERS AND NOT THE WHOLE TYPE. `SuiteTopology.Application`
// returns Aspire's DistributedApplication. An interface carrying it would drag Aspire into every
// fake and reinstate the DCP dependency this seam exists to remove. Measured: the kept-topology
// path and its callees touch SecurityConfirmations, EndpointSelectionNotices,
// EndpointTrustNotices, DiscoveredServices, DependencyNames, DependencyTypes, ReseedAsync and
// DisposeAsync — and nothing else. `Application` is the only member left out, and leaving it out
// is what makes the seam viable.
//
// WHAT A FAKE PROVES, AND WHAT IT DOES NOT. SuiteTopology.SecurityConfirmations documents an
// invariant a fake cannot carry: reaching that property at all means every declared security block
// was confirmed, because a probe failure aborts StartAsync before any SuiteTopology exists. A
// double can return confirmations without that ever having happened. So a fake is evidence about
// what the RUNNER does with confirmations, never about what the PROBE decided. Fakes in this repo
// state that limit in their own headers.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vouchfx.Engine.Orchestration;

/// <summary>
/// The narrow, Aspire-free view of an already-started topology that a caller keeps alive across
/// several scenario runs (<c>--watch</c>) or across a whole suite (#364).
/// </summary>
/// <remarks>
/// <para>
/// Implemented by <see cref="SuiteTopology"/> with no member-body changes — every signature here
/// already matched. The interface adds a SEAM, not a behaviour: production code runs against the
/// one implementation, and a test substitutes a double so the run path's own execution core is
/// exercisable without DCP metadata or a container.
/// </para>
/// <para>
/// <strong>Deliberately excludes <see cref="SuiteTopology.Application"/>.</strong> That is the only
/// Aspire-typed member on the class, and the whole value of this seam is that no implementor needs
/// the Aspire hosting stack.
/// </para>
/// </remarks>
public interface IKeptTopology : IAsyncDisposable
{
    /// <summary>
    /// Gets the declared-versus-observed security confirmations REQ-005's probe produced when this
    /// topology was built. See <see cref="SuiteTopology.SecurityConfirmations"/> for the invariant
    /// the real implementation carries and a double does not.
    /// </summary>
    IReadOnlyList<SecurityConfirmation> SecurityConfirmations { get; }

    /// <summary>
    /// Gets the endpoint-selection advisories the mapper raised while staging this topology (#348).
    /// </summary>
    IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices { get; }

    /// <summary>
    /// Gets the endpoint-trust advisories raised for staged https addresses the engine contributed
    /// no client trust material for.
    /// </summary>
    IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices { get; }

    /// <summary>
    /// Gets the flat map of discovered service endpoints and managed-dependency connection strings,
    /// keyed by declared resource name.
    /// </summary>
    IReadOnlyDictionary<string, object> DiscoveredServices { get; }

    /// <summary>Gets the declared names of the managed dependencies in this topology.</summary>
    IReadOnlyList<string> DependencyNames { get; }

    /// <summary>Gets the declared dependency name → <c>type</c> map for this topology.</summary>
    IReadOnlyDictionary<string, string> DependencyTypes { get; }

    /// <summary>
    /// Re-applies the scenario's <c>environment.seed</c> against this already-started topology,
    /// restoring the freshly-built baseline before a re-run.
    /// </summary>
    /// <param name="cancellationToken">Propagated to every reset/seed operation.</param>
    /// <remarks>
    /// Declared WITHOUT a default so an implementor cannot satisfy it by accident with a
    /// zero-argument overload; the implementation is free to keep one (<see cref="SuiteTopology"/>
    /// does), and every call site passes the token explicitly.
    /// </remarks>
    Task ReseedAsync(CancellationToken cancellationToken);
}
