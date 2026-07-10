// Vouchfx.Sdk.Testing — TestBindingContext.
//
// A public, ready-to-use implementation of the frozen Vouchfx.Sdk IBindingContext,
// supplied to a provider's Bind stage when driving it through the harness.  It
// replaces the hand-written file-scoped stub a provider author would otherwise copy
// into their own test project (cf. the example fixture's FixtureBindingContext).
using Vouchfx.Sdk;

namespace Vouchfx.Sdk.Testing.Contexts;

/// <summary>
/// A public, reusable implementation of <see cref="IBindingContext"/> for driving a
/// provider's binding stage in a test harness.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IBindingContext"/> is an engine-supplied, provider-consumed marker
/// interface (CLAUDE.md §13): a provider receives an instance and reads its members
/// but never implements it.  In production the engine's provider pipeline constructs
/// the binding context; the harness supplies this stand-in so a provider can be driven
/// in isolation, exactly as the engine would.
/// </para>
/// <para>
/// The type carries no state because the interface declares no members in the v1
/// contract.  Should the frozen contract gain a member additively (a non-breaking
/// change for providers), this stub will be extended to satisfy it.
/// </para>
/// </remarks>
public sealed class TestBindingContext : IBindingContext
{
}
