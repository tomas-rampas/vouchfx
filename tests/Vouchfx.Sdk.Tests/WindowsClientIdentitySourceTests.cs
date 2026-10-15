using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Sdk.Tests;

public sealed class WindowsClientIdentitySourceTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("probe")]
    public void IdentityContext_WindowsSelectsExplicitLeafAndOtherPlatformsKeepTheContext(string consumer)
    {
        AssertIdentitySelection(Source(consumer));
    }

    [Theory]
    [InlineData("http", "context")]
    [InlineData("probe", "context")]
    [InlineData("http", "callback")]
    [InlineData("probe", "callback")]
    [InlineData("http", "collection")]
    [InlineData("probe", "collection")]
    [InlineData("http", "other-platform")]
    [InlineData("probe", "other-platform")]
    public void IdentitySelection_GuardRejectsContextCacheAliasAndDroppedIdentityControls(
        string consumer, string mutation)
    {
        var source = Source(consumer);
        var prefix = consumer == "http" ? "handler.SslOptions" : "options";
        var invalid = mutation switch
        {
            "context" => source,
            "callback" => source.Replace(
                "=> clientContext.TargetCertificate", "=> null", StringComparison.Ordinal),
            "collection" => source.Replace(
                prefix + ".ClientCertificates =", prefix + ".ClientCertificateContext =", StringComparison.Ordinal),
            "other-platform" => source.Replace(
                prefix + ".ClientCertificateContext = clientContext;",
                prefix + ".ClientCertificateContext = null;", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        if (mutation == "context")
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            var windows = Assert.Single(root.DescendantNodes().OfType<IfStatementSyntax>(),
                node => node.Condition.ToString() == "System.OperatingSystem.IsWindows()");
            var block = Assert.IsType<BlockSyntax>(windows.Statement);
            invalid = source.Insert(block.OpenBraceToken.Span.End,
                "\n" + prefix + ".ClientCertificateContext = clientContext;");
        }

        Assert.NotEqual(source, invalid);
        Assert.ThrowsAny<Exception>(() => AssertIdentitySelection(invalid));
    }

    private static void AssertIdentitySelection(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var windows = Assert.Single(root.DescendantNodes().OfType<IfStatementSyntax>(),
            node => node.Condition.ToString() == "System.OperatingSystem.IsWindows()");
        var assignments = windows.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>().ToArray();
        Assert.DoesNotContain(assignments, node => node.Left.ToString().EndsWith(
            ".ClientCertificateContext", StringComparison.Ordinal));
        var collection = Assert.Single(assignments, node => node.Left.ToString().EndsWith(
            ".ClientCertificates", StringComparison.Ordinal));
        Assert.Contains(collection.Right.DescendantNodes().OfType<MemberAccessExpressionSyntax>(),
            node => node.ToString() == "clientContext.TargetCertificate");
        var callback = Assert.Single(assignments, node => node.Left.ToString().EndsWith(
            ".LocalCertificateSelectionCallback", StringComparison.Ordinal));
        var lambda = Assert.IsType<ParenthesizedLambdaExpressionSyntax>(callback.Right);
        Assert.Equal("clientContext.TargetCertificate", lambda.Body.ToString());
        var alternative = Assert.IsType<BlockSyntax>(windows.Else!.Statement);
        var context = Assert.Single(alternative.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            node => node.Left.ToString().EndsWith(".ClientCertificateContext", StringComparison.Ordinal));
        Assert.Equal("clientContext", context.Right.ToString());
    }

    private static string Source(string consumer)
    {
        if (consumer == "http")
        {
            return HttpTlsHelper.Source;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "vouchfx.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName,
            "src", "Engine", "Vouchfx.Engine.Orchestration", "SecuredEndpointProbe.cs"));
    }
}
