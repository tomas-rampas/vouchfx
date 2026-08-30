// Vouchfx.Cli.Tests — FakeKeptTopology (#364). No Docker, by construction.
//
// WHAT THIS IS EVIDENCE ABOUT, AND WHAT IT IS NOT.
//
// #364's finding is that the kept-topology entry point took a CONCRETE SuiteTopology, whose
// constructor is private and whose only factory needs Docker — so none of the behaviour downstream
// of it was assertable at unit speed, and all three defects the issue records were found by a human
// running a container drill. This double closes that: it implements the narrow IKeptTopology seam
// and records what the runner asks it for.
//
// THE LIMIT, STATED HERE RATHER THAN DISCOVERED LATER. SuiteTopology.SecurityConfirmations carries
// an invariant this type cannot: reaching that property on the real class means every declared
// security block was CONFIRMED, because a probe failure aborts StartAsync before any SuiteTopology
// exists. This double returns whatever a test hands it. So a green test here is evidence about what
// the RUNNER does with confirmations — that it renders them, on every re-run, with the replay
// qualifier — and NEVER about what the probe decided. Any claim of the second kind belongs to
// Vouchfx.Engine.Runtime.Tests/WatchProbeSecurityWiringTests, which executes the real composition.
// (WatchProbeSecurityWiringTests states its own reciprocal limit in the same way.)
//
// That this project is deliberately NOT an Aspire host (see its csproj) is itself part of the
// evidence: a green test here could not have run through DCP even by accident.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vouchfx.Engine.Orchestration;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// A recording <see cref="IKeptTopology"/> double: it answers the seven members the runner reads
/// and logs, in order, which of them it was asked for (#364).
/// </summary>
internal sealed class FakeKeptTopology : IKeptTopology
{
    private readonly List<string> _calls = new();

    /// <summary>Gets the ordered log of members the runner touched on this instance.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>Gets the number of times <see cref="ReseedAsync"/> was called.</summary>
    public int ReseedCount { get; private set; }

    /// <summary>Gets the number of times <see cref="DisposeAsync"/> was called.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>Gets or sets the confirmations the runner replays ahead of a re-run.</summary>
    public IReadOnlyList<SecurityConfirmation> Confirmations { get; set; } =
        Array.Empty<SecurityConfirmation>();

    /// <summary>Gets or sets the endpoint-selection advisories the runner replays.</summary>
    public IReadOnlyList<EndpointSelectionNotice> SelectionNotices { get; set; } =
        Array.Empty<EndpointSelectionNotice>();

    /// <summary>Gets or sets the endpoint-trust advisories the runner replays.</summary>
    public IReadOnlyList<EndpointTrustNotice> TrustNotices { get; set; } =
        Array.Empty<EndpointTrustNotice>();

    /// <summary>Gets the staged endpoints/connection strings this double reports.</summary>
    public Dictionary<string, object> Services { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<SecurityConfirmation> SecurityConfirmations
    {
        get
        {
            _calls.Add(nameof(SecurityConfirmations));
            return Confirmations;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EndpointSelectionNotice> EndpointSelectionNotices
    {
        get
        {
            _calls.Add(nameof(EndpointSelectionNotices));
            return SelectionNotices;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EndpointTrustNotice> EndpointTrustNotices
    {
        get
        {
            _calls.Add(nameof(EndpointTrustNotices));
            return TrustNotices;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> DiscoveredServices => Services;

    /// <inheritdoc />
    public IReadOnlyList<string> DependencyNames { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> DependencyTypes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task ReseedAsync(CancellationToken cancellationToken)
    {
        _calls.Add(nameof(ReseedAsync));
        ReseedCount++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _calls.Add(nameof(DisposeAsync));
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
