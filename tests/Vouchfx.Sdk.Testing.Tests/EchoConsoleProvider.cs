// Vouchfx.Sdk.Testing.Tests — an in-test worked-example provider (echo.console).
//
// A tiny, dependency-free provider used to prove the ProviderTestHarness drives ANY
// provider correctly, type-erased.  It is structurally like Example.Steps.Hello: it
// emits a message and asserts it equals an expectation (message == expect → Pass).
//
// §5.6 ASSEMBLY-GRAPH HYGIENE: this provider lives in the NON-reserved namespace
// `Harness.Steps.Echo`.  `Vouchfx.Steps.*` and `Vouchfx.Engine.*` are RESERVED for the
// engine and its Core providers — a customer DLL declaring them is refused at startup.
//
// §5 MEMORY MODEL: this provider takes NO reference to Vouchfx.Engine.Abstractions.  The
// emitted CSX reaches the run environment ONLY through the engine-injected `Vars` global
// and refers to engine types (StepOutcome, Verdict, VarKeys) by fully-qualified name —
// the engine already references Vouchfx.Engine.Abstractions when it compiles the
// assembled script, so no static handle from this provider bridges the collectible
// AssemblyLoadContext boundary.
using System.Text.Json;
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Harness.Steps.Echo;

/// <summary>
/// Strongly-typed model for the in-test <c>echo.console</c> step kind (a record
/// implementing <see cref="IStepModel"/>, never a dictionary — §13).
/// </summary>
/// <param name="Message">The message the step emits at execution time.</param>
/// <param name="Expected">
/// The constant the emitted <paramref name="Message"/> is asserted to equal; defaults to
/// <paramref name="Message"/> when the step omits <c>expect</c>.
/// </param>
public sealed record EchoConsoleModel(string Message, string Expected) : IStepModel;

/// <summary>
/// In-test worked-example provider for the <c>echo.console</c> step kind — a trivial,
/// dependency-free step that emits a message and asserts it equals a constant.  Used to
/// validate <see cref="Vouchfx.Sdk.Testing.ProviderTestHarness"/> against a provider
/// whose model type the harness does not know at compile time.
/// </summary>
[StepProvider]
public sealed class EchoConsoleProvider
    : IStepProvider,
      IStepBinder<EchoConsoleModel>,
      IStepValidator<EchoConsoleModel>,
      IStepCompiler<EchoConsoleModel>
{
    private static readonly IReadOnlyList<string> s_usings = new[]
    {
        "System",
        "System.Diagnostics",
        "Vouchfx.Engine.Abstractions",
    };

    /// <inheritdoc />
    public StepKindId Kind { get; } = new StepKindId("echo", "console");

    /// <inheritdoc />
    public ProviderMetadata Metadata { get; } = new ProviderMetadata(
        Version: "1.0.0",
        MinEngineVersion: "1.0.0",
        License: "Apache-2.0",
        Authors: new[] { "vouchfx-contributors" });

    /// <summary>
    /// Gets the JSON Schema fragment describing the <c>echo.console</c> provider's own
    /// fields (<c>message</c> required, <c>expect</c> optional).
    /// </summary>
    public JsonSchemaFragment SchemaFragment { get; } = new JsonSchemaFragment(
        """
        {
          "type": "object",
          "required": ["message"],
          "properties": {
            "message": {
              "description": "The message the step emits at execution time.",
              "type": "string"
            },
            "expect": {
              "description": "The constant the emitted message is asserted to equal. Defaults to 'message' when omitted.",
              "type": "string"
            }
          },
          "additionalProperties": true
        }
        """);

    /// <inheritdoc />
    public EchoConsoleModel Bind(YamlNode node, IBindingContext ctx)
    {
        // Bind only SHAPES YAML into the model; rejection stays in Validate (§13).
        if (node is not YamlMappingNode mapping)
            return new EchoConsoleModel(Message: string.Empty, Expected: string.Empty);

        var message = ReadScalar(mapping, "message") ?? string.Empty;
        var expected = ReadScalar(mapping, "expect") ?? message;

        return new EchoConsoleModel(Message: message, Expected: expected);
    }

    private static string? ReadScalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value)
        && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    /// <inheritdoc />
    public ValidationResult Validate(EchoConsoleModel model, IProjectContext ctx)
    {
        if (string.IsNullOrWhiteSpace(model.Message))
        {
            return ValidationResult.Failure(
                "echo.console: 'message' must not be empty or whitespace.");
        }

        return ValidationResult.Success;
    }

    /// <inheritdoc />
    public CsxFragment Emit(EchoConsoleModel model, ICompileContext ctx)
    {
        var safeId = CsxFragment.SanitiseId(ctx.StepId);

        // Emit author text as JSON-escaped C# string literals (defence against quotes /
        // braces / backslashes corrupting the emitted source).
        var messageLiteral = JsonSerializer.Serialize(model.Message);
        var expectedLiteral = JsonSerializer.Serialize(model.Expected);

        // The structured observation is built at emit time and spliced in as one safe
        // string literal, so the emitted CSX needs no JSON reference at runtime.
        var observationJson =
            "{\"message\":" + messageLiteral + ",\"expected\":" + expectedLiteral + "}";
        var observationLiteral = JsonSerializer.Serialize(observationJson);

        // Provider-id-prefixed nested static helper (§13.3.1); de-duplicated by class
        // name, so every echo.console step in a suite must emit byte-identical source.
        const string helper = """
            static class EchoConsole_Helpers
            {
                public static bool Check(string message, string expected)
                    => string.Equals(message, expected, System.StringComparison.Ordinal);
            }
            """;

        // One brace-enclosed block, C# 11 double-dollar raw string: a single { / } is a
        // LITERAL brace in the emitted CSX and {{hole}} is an interpolation hole.
        var block =
            $$"""
            {
                var __sw_{{safeId}} = System.Diagnostics.Stopwatch.StartNew();
                var __message_{{safeId}} = {{messageLiteral}};
                var __expected_{{safeId}} = {{expectedLiteral}};

                Vars["echo::{{safeId}}"] = __message_{{safeId}};

                var __pass_{{safeId}} =
                    EchoConsole_Helpers.Check(__message_{{safeId}}, __expected_{{safeId}});
                __sw_{{safeId}}.Stop();

                var __verdict_{{safeId}} = __pass_{{safeId}}
                    ? Vouchfx.Engine.Abstractions.Verdict.Pass
                    : Vouchfx.Engine.Abstractions.Verdict.Fail;

                Vars[Vouchfx.Engine.Abstractions.VarKeys.Outcome("{{safeId}}")] =
                    new Vouchfx.Engine.Abstractions.StepOutcome(
                        __verdict_{{safeId}},
                        __sw_{{safeId}}.ElapsedMilliseconds,
                        {{observationLiteral}});
            }
            """;

        return new CsxFragment(
            RequiredUsings: s_usings,
            RequiredHelpers: new[] { helper },
            StatementBlock: block);
    }
}
