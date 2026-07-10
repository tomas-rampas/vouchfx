// Throwaway reference provider for S02-F-01 lifecycle tests.
// Consolidated: one [StepProvider] class implements all five provider interfaces.
// Must NOT be moved to src/ — it is fixture-only.
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Sdk.Tests.Providers;

/// <summary>
/// Reference implementation of all five provider interfaces for the
/// <c>noop.echo</c> step kind.  Exists solely to prove the provider
/// contract is implementable and to exercise the full lifecycle
/// resolve→bind→validate→plan→emit end-to-end in unit tests.
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
///       <see cref="CsxFragment.RequiredHelpers"/> entries are full
///       <c>static class</c> definitions whose names are prefixed with
///       the provider id (<c>NoopEcho_</c>).
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="CsxFragment.StatementBlock"/> is built with a C# 11
///       double-dollar raw string (<c>$$"""…"""</c>): a single
///       <c>{</c>/<c>}</c> is a literal brace in the emitted CSX (so the
///       step's own block passes through verbatim) and <c>{{hole}}</c> is
///       an interpolation hole filled at emit time.
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
[StepProvider]
public sealed class NoopEchoProvider
    : IStepProvider,
      IStepBinder<NoopEchoModel>,
      IStepValidator<NoopEchoModel>,
      IStepCompiler<NoopEchoModel>,
      IResourceContributor<NoopEchoModel>
{
    private static readonly string[] s_authors = { "vouchfx-contributors" };

    private static readonly IReadOnlyList<string> s_usings =
        new[] { "System", "System.Collections.Generic" };

    /// <summary>
    /// The helper class definition injected into the compiled CSX submission.
    /// The name is provider-id-prefixed (<c>NoopEcho_Helpers</c>) to avoid
    /// collisions when multiple providers contribute helpers to the same
    /// submission (§13.3.1).
    /// </summary>
    private static readonly IReadOnlyList<string> s_helpers = new[]
    {
        "static class NoopEcho_Helpers\n{\n    public static int Echo(int length) => length;\n}",
    };

    // ── IStepProvider ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("noop", "echo");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: s_authors);

    // ── IStepBinder<NoopEchoModel> ────────────────────────────────────────────

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

    // ── IStepValidator<NoopEchoModel> ─────────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult Validate(NoopEchoModel model, IProjectContext ctx)
    {
        if (string.IsNullOrWhiteSpace(model.Message))
            return ValidationResult.Failure("noop.echo: 'message' must not be empty.");

        return ValidationResult.Success;
    }

    // ── IStepCompiler<NoopEchoModel> ──────────────────────────────────────────

    /// <inheritdoc />
    public CsxFragment Emit(NoopEchoModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

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

    // ── IResourceContributor<NoopEchoModel> ───────────────────────────────────

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
/// Test-only <see cref="ICompileContext"/> that carries a specific step
/// identifier for verifying id sanitisation behaviour and end-to-end
/// compilation.
/// </summary>
public sealed class CompileContextWithStepId : ICompileContext
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

    /// <inheritdoc />
    public string SuiteNamespace => "Generated";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Captures { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
        new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
}
