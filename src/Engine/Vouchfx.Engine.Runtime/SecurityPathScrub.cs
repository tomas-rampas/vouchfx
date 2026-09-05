// Vouchfx.Engine.Runtime — SecurityPathScrub (#473).
//
// THE ONE MEMBER THE CLI NEEDS FROM SecurityPathDisclosureLedger, RE-EXPOSED AT THE LAYER THE CLI
// IS ALREADY TRUSTED WITH.
//
// #473 lifted SecurityPathDisclosureLedger down from this assembly into
// Vouchfx.Engine.Orchestration, because that is where its new recording sites live. The type is
// public and its parameterless constructor is public, so `vouchfx` (the CLI) can still hold one and
// pass it along — but `Scrub` is INTERNAL, deliberately, and WatchRunner calls it at every
// post-probe terminal sink. Something has to bridge that.
//
// THE TWO WAYS, AND WHY THIS ONE. The alternative was an InternalsVisibleTo grant from
// Vouchfx.Engine.Orchestration to `vouchfx`. Measured, that grant has never existed on that csproj
// (`git log -S` over its history returns zero commits), so it would have been a NEW licence, and
// its price is the whole of Orchestration's internal surface — SeedApplier, SeedFixtures,
// ServerArtifactInjection, EnvironmentMapper's internals, HeadlessTopology — handed to the CLI in
// order to reach ONE method. This assembly already holds both halves: `vouchfx` sees Runtime's
// internals (granted for ScenarioRunner.Elevate), and Runtime sees Orchestration's. Forwarding
// through here keeps the seam at one method instead of one assembly, which is exactly the argument
// the Vouchfx.Cli.Tests grant's own comment makes about staying narrow.
//
// WHAT WAS NOT DONE, and why. Widening `Scrub` to public would remove the problem and give away
// the property that shape exists to keep: the ledger is an OPAQUE TOKEN to anyone outside the
// engine's friends — an embedder can hold one and pass it along and can do nothing else with it.
// A public Scrub invites an embedder to run the engine's substitution over text of their own,
// against a table whose contents are an engine implementation detail.
using Vouchfx.Engine.Orchestration;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Forwards the run's security-path substitution to callers that may see this assembly's internals
/// but not <c>Vouchfx.Engine.Orchestration</c>'s (issue #473).
/// </summary>
internal static class SecurityPathScrub
{
    /// <summary>
    /// Applies <paramref name="ledger"/>'s substitution to <paramref name="text"/>, or returns it
    /// unchanged when there is no ledger.
    /// </summary>
    /// <param name="ledger">The run's or session's ledger, or <see langword="null"/>.</param>
    /// <param name="text">The free-form diagnostic text. May be <see langword="null"/>.</param>
    /// <returns>
    /// The substituted text, the original reference when nothing applied, or
    /// <see langword="null"/> for a <see langword="null"/> input.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A pure forwarder, and it must stay one. It does NOT compose the secret scrub, because the
    /// secrets-first ordering is decided at <c>ScenarioRunner.ScrubDiagnostic</c> and argued there
    /// at length; a second place that also knew the order is a second place it can drift. Callers
    /// run the value scrub themselves and then call this, in that order.
    /// </para>
    /// <para>
    /// The <see langword="null"/> check lives here rather than at the call site so the one
    /// remaining CLI sink reads as a single expression, and so a future second caller cannot get
    /// the null case subtly different.
    /// </para>
    /// </remarks>
    internal static string? Apply(SecurityPathDisclosureLedger? ledger, string? text)
        => ledger is null ? text : ledger.Scrub(text);
}
