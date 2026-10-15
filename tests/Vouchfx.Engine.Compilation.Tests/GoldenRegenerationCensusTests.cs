using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class GoldenRegenerationCensusTests
{
    [Fact]
    public void AllRegenerationLeaves_DelegateToTheCiRefusalGuard()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Vouchfx.Engine.Compilation.Tests/CatalogueJsonGoldenTests.cs"] = "VOUCHFX_REGEN_CATALOGUE_JSON",
            ["Vouchfx.Engine.Compilation.Tests/LanguageReferenceGoldenTests.cs"] = "VOUCHFX_REGEN_LANGUAGE_REFERENCE",
            ["Vouchfx.Engine.Compilation.Tests/SchemaFreezeTests.cs"] = "VOUCHFX_REGEN_SCHEMA",
            ["Vouchfx.Engine.Compilation.Tests/VsCodeShippedSchemaSyncTests.cs"] = "VOUCHFX_REGEN_SCHEMA",
            ["Vouchfx.Engine.Abstractions.Tests/Events/EventContractFreezeTests.cs"] = "VOUCHFX_REGEN_EVENT_CONTRACT",
            ["Vouchfx.Engine.Planning.Tests/PlanReportContractFreezeTests.cs"] = "VOUCHFX_REGEN_PLAN_CONTRACT",
            ["Vouchfx.Sdk.Tests/SdkContractFreezeTests.cs"] = "VOUCHFX_REGEN_SDK_CONTRACT",
            ["Vouchfx.Sdk.Testing.Tests/SdkTestingContractFreezeTests.cs"] = "VOUCHFX_REGEN_SDK_TESTING_CONTRACT",
            ["Vouchfx.Cli.Tests/ValidateJsonGoldenTests.cs"] = "VOUCHFX_REGEN_VALIDATE_JSON",
            ["Vouchfx.Cli.Tests/ListJsonGoldenTests.cs"] = "VOUCHFX_REGEN_LIST_JSON",
            ["Vouchfx.Cli.Tests/CliPublicApiContractTests.cs"] = "VOUCHFX_REGEN_CLI_CONTRACT",
            ["Vouchfx.Engine.Abstractions.Tests/AbstractionsContractFreezeTests.cs"] = "VOUCHFX_REGEN_ABSTRACTIONS_CONTRACT",
            ["Vouchfx.Engine.Authoring.Tests/AuthoringPublicApiContractTests.cs"] = "VOUCHFX_REGEN_AUTHORING_CONTRACT",
            ["Vouchfx.Engine.Compilation.Tests/CompilationPublicApiContractTests.cs"] = "VOUCHFX_REGEN_COMPILATION_CONTRACT",
            ["Vouchfx.Engine.Orchestration.Tests/OrchestrationEventContractFreezeTests.cs"] = "VOUCHFX_REGEN_ORCHESTRATION_EVENT_CONTRACT",
            ["Vouchfx.Engine.Planning.Tests/PlanningPublicApiContractTests.cs"] = "VOUCHFX_REGEN_PLANNING_CONTRACT",
        };
        var testsRoot = Path.Combine(RepoRoot.Resolve(), "tests");
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        var declaredSwitches = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(testsRoot, path).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
            foreach (var field in syntax.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    if (variable.Initializer?.Value is LiteralExpressionSyntax literal
                        && literal.Token.ValueText.StartsWith("VOUCHFX_REGEN_", StringComparison.Ordinal))
                    {
                        switches.Add(literal.Token.ValueText);
                        declaredSwitches.Add(relative, literal.Token.ValueText);
                    }
                }
            }

            foreach (var method in syntax.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == "IsRegenRequested"))
            {
                var field = method.Parent!.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Single(variable => variable.Identifier.ValueText == "RegenEnvVar");
                var literal = Assert.IsType<LiteralExpressionSyntax>(field.Initializer!.Value);
                actual.Add(relative, literal.Token.ValueText);
                var statement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(method.Body!.Statements));
                var call = Assert.IsType<InvocationExpressionSyntax>(statement.Expression);
                Assert.Equal("Vouchfx.TestSupport.GoldenRegeneration.IsRequested", call.Expression.ToString());
                Assert.Equal("RegenEnvVar", Assert.Single(call.ArgumentList.Arguments).Expression.ToString());
            }
        }

        Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
        Assert.Equal(expected.OrderBy(pair => pair.Key), declaredSwitches.OrderBy(pair => pair.Key));
        Assert.Equal(expected.Values.Distinct().OrderBy(value => value), switches.OrderBy(value => value));
    }
}
