using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Pins the Core-provider methods that recursively walk author-supplied YAML.
/// A new recursive walk must be reviewed for both depth and expanded-node limits.
/// </summary>
/// <remarks>
/// This is a syntactic accident guard, not call-graph or data-flow analysis. It detects direct
/// self-calls and two-method cycles within one type when both methods declare a YamlNode,
/// YamlMappingNode or YamlSequenceNode parameter. It does not see mutual recursion through a
/// third method, calls delegated to another assembly, aliases of the YAML types, or indirect
/// calls. The emitted CSX ToJsonNode helper in mq-expect.kafka walks runtime Avro payloads,
/// not author YAML, and is outside this source-level census.
/// </remarks>
public sealed class AuthorYamlRecursionCensusTests
{
    private const string ProvidersRoot = "src/Providers";
    private const string KnownWalk = "Core/Vouchfx.Steps.Core.HttpRest/HttpRestProvider.cs:HttpRestProvider.YamlToJsonElement";

    [Fact]
    public void AuthorYamlRecursiveWalks_AreExactlyTheReviewedBoundedMethods()
    {
        var root = Path.Combine(RepoRoot.Resolve(), "src", "Providers");
        Assert.True(Directory.Exists(root), $"Missing census root: {root}");

        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .ToArray();
        Assert.True(files.Length >= 25, $"{ProvidersRoot} has only {files.Length} source files; the census may have lost its root.");

        var walks = files.SelectMany(path => FindRecursiveYamlMethods(
                File.ReadAllText(path),
                Path.GetRelativePath(root, path).Replace('\\', '/')))
            .ToArray();

        Assert.Equal(new[] { KnownWalk }, walks.Select(walk => walk.Identity).OrderBy(name => name));
        foreach (var walk in walks)
        {
            Assert.True(HasBoundParameters(walk.Method), $"{walk.Identity} must carry int depth and ref int budget parameters.");
        }

        // Pin the existing backstop's use of the counters as well as its signature. These
        // assertions are deliberately specific to the sole reviewed walk, not a generic claim
        // that a similarly named counter in an unknown provider is actually enforced.
        var body = Assert.Single(walks).Method.Body!.ToString();
        Assert.Contains("depth > MaxBodyDepth", body, StringComparison.Ordinal);
        Assert.Contains("--budget < 0", body, StringComparison.Ordinal);
        Assert.Contains("depth + 1", body, StringComparison.Ordinal);
        Assert.Contains("ref budget", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class P { void Walk(YamlNode node) { Walk(node); } }", 1)]
    [InlineData("class P { void Walk(YamlNode? node) { Walk(node); } }", 1)]
    [InlineData("class P { void Walk(global::YamlDotNet.RepresentationModel.YamlMappingNode? node) { Walk(node); } }", 1)]
    [InlineData("class P { void A(YamlNode node) { B(node); } void B(YamlMappingNode node) { A(node); } }", 2)]
    [InlineData("class P { void A(YamlNode node) { B(node); } void B(YamlNode node) { } }", 0)]
    [InlineData("class P { void Walk(JsonNode node) { Walk(node); } }", 0)]
    public void Detection_FindsYamlCyclesWithoutCountingOrdinaryCalls(string source, int expected)
    {
        var walks = FindRecursiveYamlMethods(source, "fixture.cs").ToArray();
        Assert.Equal(expected, walks.Length);
        if (expected > 0)
        {
            Assert.All(walks, walk => Assert.False(HasBoundParameters(walk.Method)));
        }
    }

    private static bool HasBoundParameters(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Any(parameter =>
            parameter.Identifier.ValueText == "depth" && parameter.Type?.ToString() == "int")
        && method.ParameterList.Parameters.Any(parameter =>
            parameter.Identifier.ValueText == "budget" && parameter.Type?.ToString() == "int"
            && parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.RefKeyword)));

    private static IEnumerable<YamlMethod> FindRecursiveYamlMethods(string source, string path)
    {
        var syntax = CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach (var type in syntax.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var methods = type.Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.ParameterList.Parameters.Any(parameter => IsYamlNode(parameter.Type)))
                .ToArray();
            foreach (var method in methods)
            {
                var called = CalledSiblingNames(method, type.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
                var name = method.Identifier.ValueText;
                var direct = called.Contains(name);
                var paired = methods.Any(sibling => sibling != method
                    && called.Contains(sibling.Identifier.ValueText)
                    && CalledSiblingNames(sibling, type.Identifier.ValueText).Contains(name));
                if (direct || paired)
                {
                    yield return new YamlMethod($"{path}:{type.Identifier.ValueText}.{name}", method);
                }
            }
        }
    }

    private static bool IsYamlNode(TypeSyntax? type) => type switch
    {
        NullableTypeSyntax nullable => IsYamlNode(nullable.ElementType),
        QualifiedNameSyntax qualified => IsYamlNode(qualified.Right),
        AliasQualifiedNameSyntax aliased => IsYamlNode(aliased.Name),
        IdentifierNameSyntax name => name.Identifier.ValueText is "YamlNode" or "YamlMappingNode" or "YamlSequenceNode",
        _ => false,
    };

    private static IEnumerable<string> CalledSiblingNames(MethodDeclarationSyntax method, string typeName) =>
        method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => call.Expression switch
            {
                IdentifierNameSyntax name => name.Identifier.ValueText,
                MemberAccessExpressionSyntax member when member.Expression.ToString() is "this" or "base"
                    || member.Expression.ToString() == typeName => member.Name.Identifier.ValueText,
                _ => null,
            })
            .OfType<string>();

    private sealed record YamlMethod(string Identity, MethodDeclarationSyntax Method);
}
