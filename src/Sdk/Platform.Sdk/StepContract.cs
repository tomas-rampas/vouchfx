// Platform.Sdk — provider-authoring contract surface (§13).
// This file declares the five provider interfaces, the model marker, and the
// discovery attribute.  The v1 interface contract is frozen for the v1.x
// engine series (§13.8.1).
using YamlDotNet.RepresentationModel;

namespace Platform.Sdk;

/// <summary>
/// Marker interface that every provider's step model record must implement.
/// Models must be strongly-typed records; <c>Dictionary&lt;string,object&gt;</c>
/// is explicitly prohibited (§13).
/// </summary>
/// <remarks>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// </remarks>
public interface IStepModel { }

/// <summary>
/// Core identity contract for a step provider.  Every provider class must
/// implement this interface and be decorated with
/// <see cref="StepProviderAttribute"/> so the <c>StepKindRegistry</c> can
/// discover it at startup.
/// </summary>
/// <remarks>
/// <para>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// </para>
/// <para>
/// Providers are compile-time, source-level plugins — no runtime dynamic
/// loading or sandboxing (§13).
/// </para>
/// </remarks>
public interface IStepProvider
{
    /// <summary>
    /// Gets the unique <see cref="StepKindId"/> that identifies the step
    /// kind implemented by this provider (e.g.
    /// <c>Family="db-assert", Provider="postgres"</c>).
    /// </summary>
    StepKindId Kind { get; }

    /// <summary>
    /// Gets the descriptive <see cref="ProviderMetadata"/> for this provider,
    /// including version and licence information.
    /// </summary>
    ProviderMetadata Metadata { get; }
}

/// <summary>
/// Deserialises a <see cref="YamlNode"/> into a strongly-typed step model
/// and supplies the JSON Schema fragment used for YAML validation and IDE
/// completions (§8, §13.3).
/// </summary>
/// <remarks>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// </remarks>
/// <typeparam name="TModel">
/// The strongly-typed record that represents the provider's step model.
/// Must implement <see cref="IStepModel"/>.
/// </typeparam>
public interface IStepBinder<TModel> where TModel : IStepModel
{
    /// <summary>
    /// Gets the JSON Schema fragment that describes the fields of
    /// <typeparamref name="TModel"/> in the <c>.e2e.yaml</c> DSL.
    /// </summary>
    JsonSchemaFragment SchemaFragment { get; }

    /// <summary>
    /// Deserialises <paramref name="node"/> into a <typeparamref name="TModel"/>
    /// instance.
    /// </summary>
    /// <param name="node">
    /// The <see cref="YamlNode"/> rooted at the step's YAML mapping that
    /// corresponds to this provider.
    /// </param>
    /// <param name="ctx">
    /// The binding context supplying shared configuration and diagnostic
    /// services.
    /// </param>
    /// <returns>
    /// A fully populated <typeparamref name="TModel"/> record.
    /// </returns>
    TModel Bind(YamlNode node, IBindingContext ctx);
}

/// <summary>
/// Validates a bound step model, producing human-readable error messages
/// for the test author when the model is invalid.
/// </summary>
/// <remarks>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// </remarks>
/// <typeparam name="TModel">
/// The strongly-typed record that represents the provider's step model.
/// Must implement <see cref="IStepModel"/>.
/// </typeparam>
public interface IStepValidator<TModel> where TModel : IStepModel
{
    /// <summary>
    /// Validates <paramref name="model"/> against provider-specific rules
    /// and project-level constraints.
    /// </summary>
    /// <param name="model">
    /// The step model produced by the binding stage.
    /// </param>
    /// <param name="ctx">
    /// The project context supplying path resolution, configuration, and
    /// cross-step dependency information.
    /// </param>
    /// <returns>
    /// A <see cref="ValidationResult"/> indicating whether the model is
    /// valid and listing any errors.
    /// </returns>
    ValidationResult Validate(TModel model, IProjectContext ctx);
}

/// <summary>
/// Emits the <see cref="CsxFragment"/> that represents a single step
/// instance in the assembled Roslyn script (§5, §13.3).
/// </summary>
/// <remarks>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// </remarks>
/// <typeparam name="TModel">
/// The strongly-typed record that represents the provider's step model.
/// Must implement <see cref="IStepModel"/>.
/// </typeparam>
public interface IStepCompiler<TModel> where TModel : IStepModel
{
    /// <summary>
    /// Produces the <see cref="CsxFragment"/> for a single step instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations must observe all CsxFragment composition rules
    /// (§13.3.1): bare namespace strings in
    /// <see cref="CsxFragment.RequiredUsings"/>, provider-id-prefixed
    /// nested static class <em>definitions</em> (full source) in
    /// <see cref="CsxFragment.RequiredHelpers"/>, and a single
    /// brace-enclosed <see cref="CsxFragment.StatementBlock"/>.
    /// </para>
    /// <para>
    /// Statement bodies must be constructed with C# 11 double-dollar raw
    /// strings (<c>$$"""…"""</c>).  <c>using var</c> is illegal in a
    /// Roslyn script body.
    /// </para>
    /// </remarks>
    /// <param name="model">
    /// The validated step model to compile.
    /// </param>
    /// <param name="ctx">
    /// The compile context supplying the step identifier and suite-level
    /// namespace.
    /// </param>
    /// <returns>
    /// A well-formed <see cref="CsxFragment"/> for this step instance.
    /// </returns>
    CsxFragment Emit(TModel model, ICompileContext ctx);
}

/// <summary>
/// Declares the infrastructure resources required by a step instance so
/// that the engine can build the correct Aspire topology (§4, §13.4).
/// </summary>
/// <remarks>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// </remarks>
/// <typeparam name="TModel">
/// The strongly-typed record that represents the provider's step model.
/// Must implement <see cref="IStepModel"/>.
/// </typeparam>
public interface IResourceContributor<TModel> where TModel : IStepModel
{
    /// <summary>
    /// Returns the set of infrastructure resources required by the given
    /// step model.
    /// </summary>
    /// <param name="model">
    /// The validated step model whose resource requirements are to be
    /// declared.
    /// </param>
    /// <returns>
    /// Zero or more <see cref="ResourceRequirement"/> instances describing
    /// each required resource.
    /// </returns>
    IEnumerable<ResourceRequirement> Resources(TModel model);
}

/// <summary>
/// Allows a provider to register runtime services with the engine's internal
/// DI container.  This interface is intentionally non-generic because service
/// registration is independent of any particular step model (§13).
/// </summary>
/// <remarks>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// </remarks>
public interface IRuntimeServiceContributor
{
    /// <summary>
    /// Returns the set of service descriptors that this provider wishes to
    /// register with the engine's DI container.
    /// </summary>
    /// <returns>
    /// Zero or more <see cref="ProviderServiceDescriptor"/> instances describing
    /// the services to register.
    /// </returns>
    IEnumerable<ProviderServiceDescriptor> Services();
}

/// <summary>
/// Optional discovery surface for assemblies that register multiple providers
/// through a single entry point.  An assembly may expose an
/// <see cref="IProviderModule"/> implementation in addition to, or instead of,
/// individual classes decorated with <see cref="StepProviderAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>StepKindRegistry</c> discovers provider modules at startup by
/// scanning loaded assemblies for types implementing this interface, alongside
/// its scan for <see cref="StepProviderAttribute"/>-decorated types (§13).
/// Both discovery paths are equivalent; using a module is optional and is
/// intended for provider packages that bundle several step kinds together.
/// </para>
/// <para>
/// The members of this interface are frozen for the v1.x engine series;
/// evolution is additive only, via new optional interfaces.
/// </para>
/// </remarks>
public interface IProviderModule
{
    /// <summary>
    /// Returns every <see cref="IStepProvider"/> this module contributes to
    /// the engine's <c>StepKindRegistry</c>.
    /// </summary>
    /// <returns>
    /// A non-null sequence of <see cref="IStepProvider"/> instances.
    /// The sequence may be empty but must not be <see langword="null"/>.
    /// </returns>
    IEnumerable<IStepProvider> Providers();
}

/// <summary>
/// Optional.  A provider implements this to declare the assemblies its emitted CSX
/// references at compile time.
/// </summary>
/// <remarks>
/// <para>
/// These assemblies become Roslyn <c>MetadataReference</c>s only and <strong>must</strong>
/// already be resolvable in the Default <see cref="System.Runtime.Loader.AssemblyLoadContext"/>
/// at runtime (the provider assembly itself references them transitively), so they are
/// <strong>never</strong> loaded into the collectible ALC.  This preserves the §5 memory-model
/// invariant.
/// </para>
/// <para>
/// This is a frozen-contract-compatible optional extension interface (§13.8.1).  Providers
/// that do not need additional compile references simply omit this interface; the engine
/// handles its absence tolerantly.
/// </para>
/// </remarks>
public interface ICompileReferenceContributor
{
    /// <summary>
    /// Gets the assemblies whose locations should be added to Roslyn's
    /// <c>MetadataReference</c> list for the compiled scenario script.
    /// </summary>
    /// <remarks>
    /// Each returned assembly must already be loaded in the Default ALC.  The engine
    /// de-duplicates locations across all contributing providers before passing them to
    /// the Roslyn compiler.
    /// </remarks>
    System.Collections.Generic.IEnumerable<System.Reflection.Assembly> CompileReferenceAssemblies { get; }
}

/// <summary>
/// Optional.  A provider implements this to render a faithful expected-vs-observed
/// diff for its own data model from a step's structured observation, at <em>render
/// time</em> (§14.5, §13.8.1).
/// </summary>
/// <remarks>
/// <para>
/// The diff is computed by the renderer when it formats a failed step, never by the
/// engine when it records the event.  This keeps the schema-versioned JSON Lines event
/// stream pure structured data: a renderer that does not understand a provider's
/// observation simply omits the diff (§14 invariant — one stream feeds every renderer;
/// renderers tolerate unknown fields).  No rendered diff text is ever baked into the
/// stream.
/// </para>
/// <para>
/// This is a frozen-contract-compatible optional extension interface (§13.8.1).
/// Providers that do not implement it are skipped — the renderer falls back to its
/// plain verdict line — so adding this interface does not change the v1 provider
/// contract, which remains frozen for the v1.x engine series.  The implementation runs
/// in the Default <see cref="System.Runtime.Loader.AssemblyLoadContext"/> (the provider
/// instance is held by the frozen <c>StepKindRegistry</c>), so it raises no §5
/// memory-model concern.
/// </para>
/// </remarks>
public interface IStepDiffRenderer
{
    /// <summary>
    /// Returns <see langword="true"/> when this renderer recognises the shape of the
    /// supplied structured step <paramref name="observation"/> and can render a diff
    /// for it.
    /// </summary>
    /// <param name="observation">
    /// The provider-supplied structured observation recorded on the step's
    /// <c>step-completed</c> event (e.g. a failed column-value or row-count assertion).
    /// </param>
    /// <returns>
    /// <see langword="true"/> when <see cref="RenderDiff"/> would return a non-null
    /// diff for <paramref name="observation"/>; <see langword="false"/> otherwise
    /// (e.g. a pass-shaped observation or an unrecognised shape).
    /// </returns>
    bool CanRender(System.Text.Json.JsonElement observation);

    /// <summary>
    /// Renders a faithful expected-vs-observed diff for this provider's data model
    /// from the structured step <paramref name="observation"/>.
    /// </summary>
    /// <param name="observation">
    /// The provider-supplied structured observation recorded on the step's
    /// <c>step-completed</c> event.
    /// </param>
    /// <returns>
    /// The rendered diff text (plain ASCII / box-drawing, ready to splice under the
    /// step line), or <see langword="null"/> when this renderer cannot render the
    /// supplied observation.
    /// </returns>
    string? RenderDiff(System.Text.Json.JsonElement observation);
}

/// <summary>
/// Applied to a class that implements one or more provider interfaces to mark
/// it as discoverable by the engine's <c>StepKindRegistry</c> at startup.
/// </summary>
/// <remarks>
/// <para>
/// The registry performs a one-time reflective scan of all loaded assemblies
/// at application startup and freezes the discovered providers into an
/// immutable map.  Providers not decorated with this attribute will not be
/// discovered.
/// </para>
/// <para>
/// Only one <see cref="StepProviderAttribute"/> may be applied per class
/// (<c>AllowMultiple = false</c>).  The attribute is not inherited
/// (<c>Inherited = false</c>) so that accidental sub-classing does not
/// register unintended step kinds.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class StepProviderAttribute : Attribute { }
