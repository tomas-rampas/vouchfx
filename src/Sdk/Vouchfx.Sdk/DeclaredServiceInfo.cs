// Vouchfx.Sdk — DeclaredServiceInfo (authenticated-infrastructure-mtls, slice C — carried
// forward from PR #349's review).
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

// Internal correction history (not XML documentation):
// The parameter documentation above used to forward to
// IProjectContext.DeclaredServices's own remarks "for what populates this per
// source", and those remarks do not answer it for one of the three sources they enumerate. For
// a HOST-RESOURCE entry — a webhook-listen.http listener, a trace-expect.otlp
// receiver — this list holds the contributing requirement's
// HostResourceRequirement.Kind (e.g. ["webhook-listener"]), which is a
// resource-kind discriminator, NOT an Aspire endpoint name. The forward is therefore replaced
// by the concrete examples above, and the exception stated here rather than left to a reader to
// discover by inspecting ProviderPipeline's own service-map construction.
// The paragraph below used to be a SECOND, consecutive
// <remarks> element on this same member. Vouchfx.Sdk is packable with
// GenerateDocumentationFile=true, and documentation renderers show only the FIRST
// <remarks> — so the block that silently disappeared from the emitted
// Vouchfx.Sdk.xml was the one carrying the binary-compatibility rule, on a type inside
// the frozen v1 contract. Merged into one element with two <para> blocks, the
// shape every other file in this change already uses.
/// <summary>
/// The declared shape of a single entry in <see cref="IProjectContext.DeclaredServices"/>.
/// </summary>
/// <param name="EndpointNames">
/// The Aspire endpoint names this entry exposes — e.g. <c>["http"]</c> for the implicit default
/// HTTP endpoint of an <c>image:</c>-form service, or <c>["tcp-9093"]</c> for a declared
/// <c>ports: [9093]</c> entry. Empty for a project-form service, whose endpoints Aspire
/// auto-discovers from the project's own launch profile rather than this engine naming them.
/// </param>
/// <remarks>
/// <para>
/// Host-resource entries are an exception to the endpoint-name shape: a
/// <c>webhook-listen.http</c> listener or <c>trace-expect.otlp</c> receiver contributes its
/// <see cref="HostResourceRequirement.Kind"/> (for example, <c>["webhook-listener"]</c>).
/// That value is a resource-kind discriminator, not an Aspire endpoint name.
/// </para>
/// <para>
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
/// </para>
/// </remarks>
public sealed record DeclaredServiceInfo(IReadOnlyList<string> EndpointNames);
