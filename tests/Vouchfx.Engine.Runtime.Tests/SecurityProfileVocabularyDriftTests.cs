// m3 (authenticated-infrastructure-mtls, slice E — peer review fix round three).
//
// The security-profile REGISTRY is the open, extensible list of `security.profile` values the
// engine wires (REQ-019..022). Two of the three components that dispatch on a profile were
// already pinned to it: both emitted helpers, by
// SecurityProfileRegistryTests.EveryWiredProfile_IsNamedInTheEmittedHelpersProfileSwitch. The
// third — SecuredEndpointProbe — compared a bare "mtls" literal inline, and it is the component
// whose whole job is preventing a false pass.
//
// The failure mode a bare literal leaves open: a future registered profile that DOES present a
// client identity (a SASL mechanism, a bearer token, Kerberos) takes the probe's no-identity
// path. No certificate is resolved, the anonymous-client differential never runs, and the
// confirmation line claims exactly what it observed — nothing about an identity. It degrades
// safely, which is precisely why nothing would notice.
//
// WHY THIS FILE LIVES IN Vouchfx.Engine.Runtime.Tests. It needs internals of TWO assemblies at
// once — SecurityProfileRegistry (Vouchfx.Engine.Compilation) and SecuredEndpointProbe
// (Vouchfx.Engine.Orchestration) — and no test project could see both. This project already held
// the registry grant (it drives SecurityProfileWiringValidator with reduced registries for
// REQ-022's red-first acceptance), so granting it Orchestration's internals was the smaller of
// the two widenings; see Vouchfx.Engine.Orchestration.csproj's own comment.
using System;
using System.Linq;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Pins <c>SecuredEndpointProbe</c>'s profile vocabulary to the security-profile registry, so a
/// registered profile the probe has never heard of turns a build red rather than being handled
/// silently by the wrong branch.
/// </summary>
public sealed class SecurityProfileVocabularyDriftTests
{
    /// <summary>
    /// Every registered profile must appear in the probe's own map, and the probe must name no
    /// profile the registry does not wire.
    /// </summary>
    /// <remarks>
    /// The probe's map is EXHAUSTIVE (profile → does it present a client identity) rather than a
    /// set of identity-presenting profiles, precisely so this assertion can be an equality: a set
    /// cannot distinguish "this profile presents nothing" from "this probe has never heard of
    /// this profile", and only the second is drift.
    /// </remarks>
    [Fact]
    public void EveryRegisteredProfile_IsNamedInTheProbesOwnVocabulary()
    {
        var registered = SecurityProfileRegistry.BuiltIn.All
            .Select(wiring => wiring.Profile)
            .OrderBy(profile => profile, StringComparer.Ordinal)
            .ToList();

        var known = SecuredEndpointProbe.ProfilesPresentingAClientIdentity.Keys
            .OrderBy(profile => profile, StringComparer.Ordinal)
            .ToList();

        // Guards the guard: an empty registry would compare equal to an empty vocabulary.
        Assert.NotEmpty(registered);
        Assert.Equal(registered, known);
    }

    /// <summary>
    /// The vocabulary's VALUES are what decide whether the anonymous-client differential runs, so
    /// they are pinned too — an entry present but flagged wrongly would satisfy the equality above
    /// while still skipping the control.
    /// </summary>
    [Fact]
    public void OnlyMutualTls_PresentsAClientIdentity()
    {
        Assert.True(SecuredEndpointProbe.ProfilesPresentingAClientIdentity["mtls"]);
        Assert.False(SecuredEndpointProbe.ProfilesPresentingAClientIdentity["tls"]);
    }
}
