// Vouchfx.Sdk — DeclaredServiceInfo (authenticated-infrastructure-mtls, slice C — carried
// forward from PR #349's review, nit n1).
//
// IProjectContext.DeclaredServices used to map a declared service's name directly to
// IReadOnlyList<string> (its Aspire endpoint names). Two problems compounded under that bare
// value type: an EXTERNAL target (a service vouchfx does not itself start — the deferred
// capability the readiness audit identified as the most likely enterprise blocker) has no
// Aspire endpoint names at all, and an empty list is ALREADY overloaded to mean "project-form,
// endpoints auto-discovered" (see IProjectContext.DeclaredServices' own remarks). A bare
// IReadOnlyList<string> gives a future addition (e.g. an external-target flag) nowhere to live
// without reusing the empty-list shape for a second, unrelated meaning.

namespace Vouchfx.Sdk;

/// <summary>
/// The declared shape of a single entry in <see cref="IProjectContext.DeclaredServices"/>.
/// </summary>
/// <param name="EndpointNames">
/// The Aspire endpoint names this entry exposes — see
/// <see cref="IProjectContext.DeclaredServices"/>'s own remarks for what populates this per
/// source. Empty for a project-form service, whose endpoints Aspire auto-discovers from the
/// project's own launch profile rather than this engine naming them.
/// </param>
/// <remarks>
/// A record (not a bare <see cref="IReadOnlyList{T}"/>) so a later addition — e.g. a flag
/// distinguishing an external, engine-unmanaged target from one the engine starts — is a
/// purely additive, init-only property, never a breaking change to this already-shipped shape.
/// Future additions to this shape MUST be init-only properties, never new positional
/// constructor parameters — the same rule this codebase already applies to
/// <c>Vouchfx.Engine.Authoring.Model.ServiceSpec</c>/<c>HealthCheckSpec</c> and
/// <c>DependencySpec.Image</c>: <c>Vouchfx.Sdk</c> is a packable assembly, and inserting a new
/// positional parameter would change this record's primary constructor's parameter
/// order/arity and its compiler-generated <c>Deconstruct</c> — a binary-breaking change for any
/// already-compiled caller. An init-only property is purely additive.
/// </remarks>
public sealed record DeclaredServiceInfo(IReadOnlyList<string> EndpointNames);
