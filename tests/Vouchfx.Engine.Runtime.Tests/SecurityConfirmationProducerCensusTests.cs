using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class SecurityConfirmationProducerCensusTests
{
    [Fact]
    public void EveryConfirmationPrintSite_ArchivesTheSameCollection_AndFreshSitesPostToTheLiveStream()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "vouchfx.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "src", "Engine", "Vouchfx.Engine.Runtime", "ScenarioRunner.cs"));
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var printLoops = root.DescendantNodes().OfType<ForEachStatementSyntax>()
            .Where(loop => loop.Expression.ToString().EndsWith(".SecurityConfirmations", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, printLoops.Length);
        foreach (var loop in printLoops)
        {
            var method = loop.Ancestors().OfType<MethodDeclarationSyntax>().First();
            var producer = Assert.Single(method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
                call => call.Expression.ToString() == "SecurityConfirmationEvents.ToLines");
            Assert.Equal(loop.Expression.ToString(), producer.ArgumentList.Arguments[0].Expression.ToString());
            var replayed = producer.ArgumentList.Arguments[3].Expression.IsKind(SyntaxKind.TrueLiteralExpression);
            Assert.Equal(loop.Expression.ToString().StartsWith("topology.", StringComparison.Ordinal), replayed);
            if (replayed)
            {
                var append = Assert.IsType<InvocationExpressionSyntax>(producer.Parent!.Parent!.Parent);
                Assert.Equal("buffer.AddRange", append.Expression.ToString());
            }
            else
            {
                var variable = producer.Ancestors().OfType<VariableDeclaratorSyntax>().First().Identifier.ValueText;
                var destinations = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(call => call.ArgumentList.Arguments.Count == 1
                        && call.ArgumentList.Arguments[0].Expression.ToString() == variable)
                    .Select(call => call.Expression.ToString()).ToArray();
                Assert.Contains(destinations, expression => expression.EndsWith(".AddRange", StringComparison.Ordinal));
                Assert.Contains(destinations, expression => expression.EndsWith(".PostRange", StringComparison.Ordinal));
            }
        }
    }
}
