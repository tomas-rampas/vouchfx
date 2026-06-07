// Throwaway reference provider for S01-F-01 contract tests.
// Implements all five provider interfaces over NoopEchoModel.
using Platform.Sdk;
using YamlDotNet.RepresentationModel;

namespace Platform.Sdk.Tests.Providers;

/// <summary>
/// Reference implementation of <see cref="IStepProvider"/> for the
/// <c>noop.echo</c> step kind.  Exists solely to prove the provider
/// contract is implementable and to exercise it in unit tests.
/// </summary>
[StepProvider]
public sealed class NoopEchoProvider : IStepProvider
{
    private static readonly string[] s_authors = { "vouchfx-contributors" };

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("noop", "echo");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: s_authors);
}

/// <summary>
/// Reference implementation of <see cref="IStepBinder{TModel}"/> for
/// <see cref="NoopEchoModel"/>.
/// </summary>
public sealed class NoopEchoBinder : IStepBinder<NoopEchoModel>
{
    /// <inheritdoc />
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "properties": {
            "message": { "type": "string" }
          },
          "required": ["message"]
        }
        """);

    /// <inheritdoc />
    public NoopEchoModel Bind(YamlNode node, IBindingContext ctx)
    {
        if (node is not YamlMappingNode mapping)
            return new NoopEchoModel(string.Empty);

        var message = mapping.Children.TryGetValue(
            new YamlScalarNode("message"), out var msgNode)
            && msgNode is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty
            : string.Empty;

        return new NoopEchoModel(message);
    }
}

/// <summary>
/// Reference implementation of <see cref="IStepValidator{TModel}"/> for
/// <see cref="NoopEchoModel"/>.
/// </summary>
public sealed class NoopEchoValidator : IStepValidator<NoopEchoModel>
{
    /// <inheritdoc />
    public ValidationResult Validate(NoopEchoModel model, IProjectContext ctx)
    {
        if (string.IsNullOrWhiteSpace(model.Message))
            return ValidationResult.Failure("noop.echo: 'message' must not be empty.");

        return ValidationResult.Success;
    }
}

/// <summary>
/// Reference implementation of <see cref="IStepCompiler{TModel}"/> for
/// <see cref="NoopEchoModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// Demonstrates the mandatory CSX fragment composition rules (§13.3.1):
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="CsxFragment.RequiredUsings"/> contains bare namespace
///       strings — no <c>using</c> keyword, no semicolons.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="CsxFragment.RequiredHelpers"/> entries are prefixed with
///       the provider id (<c>NoopEcho_</c>).
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="CsxFragment.StatementBlock"/> is built with a C# 11
///       double-dollar raw string (<c>$$"""…"""</c>): with two <c>$</c>, a
///       single <c>{</c>/<c>}</c> is a literal brace in the emitted CSX (so the
///       step's own <c>{ … }</c> block passes through verbatim) and
///       <c>{{hole}}</c> is an interpolation hole the emitter fills.
///     </description>
///   </item>
///   <item>
///     <description>
///       The step identifier is sanitised via
///       <see cref="CsxFragment.SanitiseId"/> before use as a variable-name
///       suffix; hyphens are replaced by underscores.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class NoopEchoCompiler : IStepCompiler<NoopEchoModel>
{
    private static readonly IReadOnlyList<string> s_usings =
        new[] { "System", "System.Collections.Generic" };

    private static readonly IReadOnlyList<string> s_helpers =
        new[] { "NoopEcho_Helpers" };

    /// <inheritdoc />
    public CsxFragment Emit(NoopEchoModel model, ICompileContext ctx)
    {
        var stepId = ctx is IStepIdAccessor accessor
            ? accessor.StepId
            : "default_step";

        var safeId = CsxFragment.SanitiseId(stepId);

        // Statement block built with a C# 11 $$"""…""" double-dollar raw string:
        //   {  }      →  literal brace in the emitted CSX (the block's own braces)
        //   {{expr}}  →  interpolation hole, filled here at emit time
        var block = $$"""
            {
                var result_{{safeId}} = NoopEcho_Helpers.Echo({{model.Message.Length}});
                Vars["echo_{{safeId}}"] = result_{{safeId}};
            }
            """;

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: s_helpers,
            StatementBlock: block);
    }
}

/// <summary>
/// Reference implementation of <see cref="IResourceContributor{TModel}"/> for
/// <see cref="NoopEchoModel"/>.
/// </summary>
public sealed class NoopEchoResourceContributor : IResourceContributor<NoopEchoModel>
{
    /// <inheritdoc />
    public IEnumerable<ResourceRequirement> Resources(NoopEchoModel model)
    {
        yield return new ResourceRequirement(
            Family: "noop",
            Name: "echo-service",
            Image: null);
    }
}

/// <summary>
/// Extends <see cref="ICompileContext"/> with access to the current step
/// identifier.  Used by tests to inject a known step id when exercising
/// <see cref="NoopEchoCompiler"/>.
/// </summary>
public interface IStepIdAccessor : ICompileContext
{
    /// <summary>
    /// Gets the step identifier for the current compilation pass.
    /// </summary>
    string StepId { get; }
}

/// <summary>
/// Test-only <see cref="ICompileContext"/> that carries a specific step
/// identifier for verifying id sanitisation behaviour.
/// </summary>
public sealed class CompileContextWithStepId : IStepIdAccessor
{
    /// <summary>
    /// Initialises a new instance with the given step identifier.
    /// </summary>
    /// <param name="stepId">The step identifier to expose.</param>
    public CompileContextWithStepId(string stepId)
    {
        StepId = stepId;
    }

    /// <inheritdoc />
    public string StepId { get; }
}
