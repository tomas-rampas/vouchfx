// Vouchfx.Sdk — provider-authoring contract surface (§13).
// This file declares the provider context interfaces passed to each provider stage.
// IBindingContext remains a marker interface. IProjectContext carries
// DeclaredDependencies (Sprint 4) and SuiteDirectory. ICompileContext carries
// the step-identity surface (StepId + SuiteNamespace, Sprint 2), Captures/
// CaptureExprs (Sprint 4/7), and SuiteDirectory.
namespace Vouchfx.Sdk;

/// <summary>
/// Provides contextual services available to a provider's binding stage,
/// such as resolution of shared configuration or diagnostic sinks.
/// </summary>
/// <remarks>
/// <para>
/// This context is <strong>engine-supplied and provider-consumed</strong>:
/// providers receive an instance and read its members but never implement the
/// interface. Adding a member is therefore non-breaking for providers — only the
/// engine (the single in-tree implementor) must satisfy it. The <em>frozen v1
/// contract</em> (CLAUDE.md §13) governs the provider-<em>implemented</em> surface
/// (<c>IStepProvider</c>, <c>IStepBinder&lt;T&gt;</c>, …), which evolves solely via
/// new optional interfaces (e.g. <see cref="ICompileReferenceContributor"/>) and
/// freezes at the M1.5 milestone (end of Phase 2).
/// </para>
/// <para>Sprint-1 surface: marker only.</para>
/// </remarks>
public interface IBindingContext { }

/// <summary>
/// Provides contextual services available to a provider's validation stage,
/// such as access to the project-level configuration, path resolution, and
/// cross-step dependency information.
/// </summary>
/// <remarks>
/// <para>
/// This context is <strong>engine-supplied and provider-consumed</strong>:
/// providers receive an instance and read its members but never implement the
/// interface. Adding a member is therefore non-breaking for providers — the ENGINE
/// is the only PRODUCTION implementor that must satisfy it. (n5 fix, fix round 2:
/// this remark previously claimed the engine as "the single in-tree implementor",
/// which the services-generalisation spec's own <see cref="DeclaredServices"/>
/// addition below made visibly false — adding that member required updating ~22
/// test-only stand-in implementations across this repository's own test suites,
/// which also implement this interface in-tree, just never ship. The claim that
/// matters for provider authors is unaffected either way: no PROVIDER implements
/// this interface, so no provider needs to change when a member is added.) The
/// <em>frozen v1 contract</em> (CLAUDE.md §13) governs the provider-<em>implemented</em>
/// surface (<c>IStepProvider</c>, <c>IStepBinder&lt;T&gt;</c>, …), which evolves solely
/// via new optional interfaces (e.g. <see cref="ICompileReferenceContributor"/>) and
/// freezes at the M1.5 milestone (end of Phase 2).
/// </para>
/// <para>
/// Sprint-4 addition: <see cref="DeclaredDependencies"/>.
/// Later addition: <see cref="SuiteDirectory"/>, added so providers whose
/// fields reference an external file (e.g. <c>script.csharp</c>'s <c>file</c>
/// field) can validate the file exists relative to the scenario's own
/// directory, without inventing a second path convention alongside
/// <c>environment.seed</c>'s existing <c>seedBaseDirectory</c>.
/// </para>
/// </remarks>
public interface IProjectContext
{
    /// <summary>
    /// Gets the map of dependency names to their type identifiers as declared
    /// under <c>environment.dependencies</c> in the scenario file.
    /// </summary>
    /// <remarks>
    /// Keys are the logical dependency names (e.g. <c>"orders-db"</c>);
    /// values are the type identifiers (e.g. <c>"postgres"</c>, <c>"kafka"</c>).
    /// The map is empty when the scenario file omits the
    /// <c>environment.dependencies</c> section.
    /// Providers use this map to reconcile step targets against declared
    /// infrastructure (dependency reconciliation, §13).
    /// </remarks>
    IReadOnlyDictionary<string, string> DeclaredDependencies { get; }

    /// <summary>
    /// Gets the map of service names to their declared shape, as declared under
    /// <c>environment.services</c> in the scenario file (services-generalisation spec,
    /// REQ-010).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keys are the logical service names (e.g. <c>"kafka-broker"</c>), mirroring
    /// <see cref="DeclaredDependencies"/>'s own shape; each value's
    /// <see cref="DeclaredServiceInfo.EndpointNames"/> carries the Aspire endpoint names
    /// that service's declared shape produces (e.g. <c>["http"]</c> for the implicit
    /// default HTTP endpoint, or <c>["tcp-9093"]</c> for a declared <c>ports: [9093]</c>
    /// entry) — empty for a project-form service, whose endpoints Aspire auto-discovers
    /// from the project's own launch profile rather than this engine modelling them. See
    /// <see cref="DeclaredServiceInfo"/>'s own remarks for why the value is a record rather
    /// than a bare endpoint-name list.
    /// </para>
    /// <para>
    /// The map is NOT limited to <c>environment.services</c> entries, and a scenario that
    /// omits that section entirely can still produce a non-empty map. It carries every name
    /// the engine stages under a <c>svc::</c> key, from three sources:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <c>environment.services</c> entries, as above.
    ///   </description></item>
    ///   <item><description>
    ///   Host resources contributed by a step's own
    ///   <see cref="IHostResourceContributor{TModel}"/> — a <c>webhook-listen.http</c>
    ///   listener or a <c>trace-expect.otlp</c> receiver, for example. These are declared BY
    ///   a step rather than under <c>environment.services</c>, and a step may legitimately
    ///   target one declared by a LATER step, which is why they are collected up front.
    ///   </description></item>
    ///   <item><description>
    ///   Sidecar endpoints of managed dependencies, which the engine stages under a suffixed
    ///   name rather than the dependency's own: a <c>kafka</c> dependency declaring
    ///   <c>schemaRegistry: true</c> exposes its registry REST API at
    ///   <c>&lt;name&gt;-sr</c>, and a <c>mailpit</c> dependency exposes SMTP at
    ///   <c>&lt;name&gt;-smtp</c>. The dependency's own name stays in
    ///   <see cref="DeclaredDependencies"/> and is staged under <c>conn::</c>, not here.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The practical consequence for a provider reconciling a <c>target</c>: membership of
    /// this map means "the engine stages a <c>svc::</c> value under this name", not "the
    /// author wrote this under <c>environment.services</c>". It says nothing about which
    /// protocol that endpoint speaks.
    /// </para>
    /// <para>
    /// Providers use this map the same way they use <see cref="DeclaredDependencies"/>: to
    /// reconcile a step's <c>target</c> against declared infrastructure (dependency/service
    /// reconciliation, §13) — e.g. <c>mq-publish.kafka</c> accepting a <c>target</c> naming
    /// either a declared <c>kafka</c> dependency or a declared service exposing a
    /// Kafka-compatible endpoint.
    /// </para>
    /// <para>
    /// This is an ADDITIVE member on a provider-CONSUMED (never provider-implemented)
    /// interface (see this interface's own remarks) — non-breaking for providers; only the
    /// engine (and any test harness implementing this interface as a stand-in) must satisfy
    /// it.
    /// </para>
    /// </remarks>
    IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; }

    /// <summary>
    /// Gets the directory that relative file paths in step fields (e.g.
    /// <c>script.csharp</c>'s <c>file</c> field) are resolved against.
    /// </summary>
    /// <remarks>
    /// This is the same base directory the engine already uses to resolve
    /// <c>environment.seed</c> fixture paths (<c>seedBaseDirectory</c>) — the
    /// scenario's own <c>.e2e.yaml</c> directory when known, falling back to
    /// the process's current directory otherwise.
    /// </remarks>
    string SuiteDirectory { get; }
}

/// <summary>
/// Provides contextual services available to a provider's compilation stage,
/// such as the step identifier, the suite-level namespace, and access to
/// shared helper registrations.
/// </summary>
/// <remarks>
/// <para>
/// This context is <strong>engine-supplied and provider-consumed</strong>:
/// providers receive an instance and read its members but never implement the
/// interface. Adding a member is therefore non-breaking for providers — only the
/// engine (the single in-tree implementor) must satisfy it. The <em>frozen v1
/// contract</em> (CLAUDE.md §13) governs the provider-<em>implemented</em> surface
/// (<c>IStepProvider</c>, <c>IStepBinder&lt;T&gt;</c>, …), which evolves solely via
/// new optional interfaces (e.g. <see cref="ICompileReferenceContributor"/>) and
/// freezes at the M1.5 milestone (end of Phase 2).
/// </para>
/// <para>
/// Sprint-2 additions: <see cref="StepId"/> and <see cref="SuiteNamespace"/>.
/// Sprint-4 addition: <see cref="Captures"/> (S04-B-02).
/// Sprint-7 addition: <see cref="CaptureExprs"/> (S07-B-01a) — the
/// format-aware view that supersedes <see cref="Captures"/>.
/// Later addition: <see cref="SuiteDirectory"/>, mirroring the identically
/// named member on <see cref="IProjectContext"/> so a provider's <c>Emit</c>
/// stage can resolve the same external file it validated at bind/validate
/// time (e.g. <c>script.csharp</c>'s <c>file</c> field).
/// authenticated-infrastructure-mtls addition: <see cref="DeclaredServices"/>,
/// mirroring <see cref="IProjectContext.DeclaredServices"/> for the same reason —
/// a provider whose <c>target</c> may name EITHER a dependency or a service must
/// emit the corresponding <c>Vars</c> key, and which key that is can only be
/// decided at compile time.
/// </para>
/// </remarks>
public interface ICompileContext
{
    /// <summary>
    /// The <see cref="DeclaredServices"/> default: the empty map, meaning "this context knows of
    /// no declared services", which is what every pre-existing implementation was already saying
    /// by not having the member at all.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, DeclaredServiceInfo> NoDeclaredServices =
        new Dictionary<string, DeclaredServiceInfo>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the map of service names to their declared shape — the same map
    /// <see cref="IProjectContext.DeclaredServices"/> exposes at validate time, exposed here
    /// too because <c>Emit</c> runs in a separate stage and must reach the same fact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// See <see cref="IProjectContext.DeclaredServices"/> for what the map contains (it is not
    /// limited to <c>environment.services</c> entries: it carries every name the engine stages
    /// under a <c>svc::</c> key). The engine's own context exposes the identical instance, so a
    /// provider that reconciled a <c>target</c> against it in <c>Validate</c> reads the same
    /// membership in <c>Emit</c>.
    /// </para>
    /// <para>
    /// <strong>What it is for.</strong> A provider whose <c>target</c> may name either kind —
    /// <c>mq-publish.kafka</c> and <c>mq-expect.kafka</c> accept a <c>kafka</c> dependency OR a
    /// declared service (REQ-011) — must emit <c>VarKeys.Connection(target)</c> for the first
    /// and <c>VarKeys.Service(target)</c> for the second. Emitting one key and hoping, or
    /// emitting both and letting the helper try each in turn, is the "provider guessing" that
    /// REQ-023 forbids; membership of this map is the answer, and it is known at compile time.
    /// </para>
    /// <para>
    /// <strong>Why a DEFAULT implementation rather than a plain new member.</strong> This
    /// interface's own remarks say adding a member is non-breaking because the engine is its
    /// single in-tree implementor — which is true of PRODUCTION code and false of test doubles:
    /// this repository alone carries some eighty <c>StubCompileContext</c> stand-ins, and every
    /// provider outside it is free to carry more. A default returning the empty map keeps all of
    /// them compiling and behaving EXACTLY as before (an unknown name is not a service, so a
    /// provider emits the <c>conn::</c> key it emitted before this member existed), while the
    /// engine's own context overrides it with the real map. Adding a member with a default is
    /// additive in the strongest available sense; adding one without would have been a
    /// source-breaking change dressed as an additive one.
    /// </para>
    /// </remarks>
    IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices => NoDeclaredServices;

    /// <summary>
    /// Gets the identifier of the step currently being compiled.
    /// Providers must sanitise this value via <see cref="CsxFragment.SanitiseId"/>
    /// before embedding it in emitted variable names.
    /// </summary>
    string StepId { get; }

    /// <summary>
    /// Gets the C# namespace into which the compiled suite is emitted.
    /// Providers use this to qualify helper-class names when name collisions
    /// across suites must be avoided.
    /// </summary>
    string SuiteNamespace { get; }

    /// <summary>
    /// Gets the directory that relative file paths in step fields (e.g.
    /// <c>script.csharp</c>'s <c>file</c> field) are resolved against.
    /// </summary>
    /// <remarks>
    /// See <see cref="IProjectContext.SuiteDirectory"/> — same base directory,
    /// exposed here too because <c>Emit</c> runs in a separate stage from
    /// <c>Validate</c> and needs to resolve the same path again to read the
    /// file's content for splicing.
    /// </remarks>
    string SuiteDirectory { get; }

    /// <summary>
    /// Gets the map of variable names to capture <em>expression strings</em>
    /// declared in the step's <c>capture</c> block (DSL §6.1, S04-B-02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keys are the author-supplied variable names (e.g. <c>"orderId"</c>);
    /// values are the raw extractor strings (e.g. <c>"$.id"</c>) evaluated
    /// against the step's response body at execution time.
    /// </para>
    /// <para>
    /// This is the <strong>format-agnostic, back-compatible view</strong>: it
    /// exposes only the expression text and is retained so that a provider that
    /// only ever evaluates JSONPath (the pre-S07 behaviour) compiles and behaves
    /// unchanged.  A provider that needs to dispatch on the extractor language
    /// (JSONPath vs XPath) must read <see cref="CaptureExprs"/> instead, whose
    /// <see cref="CaptureExpr.Expression"/> values are identical to these entries
    /// in the same order.
    /// </para>
    /// <para>
    /// The map is never <see langword="null"/>; an empty dictionary is used when
    /// the YAML step omits the <c>capture</c> section.
    /// </para>
    /// <para>
    /// When an expression yields no match the step outcome is set to
    /// <c>Verdict.Inconclusive</c> with reason
    /// <c>upstream-capture-unmet</c> (§12.1).
    /// </para>
    /// </remarks>
    IReadOnlyDictionary<string, string> Captures { get; }

    /// <summary>
    /// Gets the map of variable names to typed <see cref="CaptureExpr"/> values
    /// declared in the step's <c>capture</c> block (DSL §6.1, S07-B-01a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each value carries both the extractor <see cref="CaptureExpr.Format"/>
    /// (<see cref="CaptureFormat.JsonPath"/> or <see cref="CaptureFormat.XPath"/>)
    /// and the raw <see cref="CaptureExpr.Expression"/> string.  This is the
    /// authoritative, format-aware view: a provider that supports more than one
    /// extractor language dispatches on <see cref="CaptureExpr.Format"/>.
    /// </para>
    /// <para>
    /// Iteration order and keys match <see cref="Captures"/> exactly; the two
    /// views are projections of the same underlying capture map.  The map is
    /// never <see langword="null"/>; an empty dictionary is used when the YAML
    /// step omits the <c>capture</c> section.
    /// </para>
    /// </remarks>
    IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; }
}
