using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Telemetry;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class TelemetryDisclosureTests
{
    private static readonly IReadOnlyDictionary<string, string> ApprovedGroups = new Dictionary<string, string>
    {
        ["schemaVersion"] = "tool/engine/.NET versions and telemetry schema version",
        ["toolVersion"] = "tool/engine/.NET versions and telemetry schema version",
        ["engineVersion"] = "tool/engine/.NET versions and telemetry schema version",
        ["dotnetVersion"] = "tool/engine/.NET versions and telemetry schema version",
        ["timestamp"] = "a UTC timestamp",
        ["installId"] = "a random install identifier",
        ["runCount"] = "run, scenario and per-verdict step + scenario counts",
        ["scenarioCount"] = "run, scenario and per-verdict step + scenario counts",
        ["stepVerdicts"] = "run, scenario and per-verdict step + scenario counts",
        ["scenarioVerdicts"] = "run, scenario and per-verdict step + scenario counts",
        ["stepFamilies"] = "which built-in step kinds ran",
        ["stepProviders"] = "which built-in step kinds ran",
        ["startupMs"] = "startup timings",
        ["timeToFirstTestMs"] = "startup timings",
        ["skippedEventLines"] = "a count of unreadable event-stream lines",
    };

    [Fact]
    public void ActualSerializedProperties_AllHaveApprovedFirstRunDisclosure()
    {
        var actual = typeof(TelemetryEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(15, ApprovedGroups.Count);
        Assert.Equal(ApprovedGroups.Keys.Order(StringComparer.Ordinal), actual);

        var notice = NormaliseWrapping(TelemetryConsentStore.FirstRunNoticeText);
        foreach (var group in ApprovedGroups.Values.Distinct(StringComparer.Ordinal))
        {
            Assert.Contains(group, notice, StringComparison.Ordinal);
        }
    }


    [Fact]
    public void BothConsentNotices_ContainEveryApprovedDisclosureGroup()
    {
        foreach (var text in new[] { TelemetryConsentStore.FirstRunNoticeText, TelemetryCommand.EnableNoticeText })
        {
            var notice = NormaliseWrapping(text);
            foreach (var group in ApprovedGroups.Values.Distinct(StringComparer.Ordinal))
            {
                Assert.Contains(group, notice, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void PublicNoticeCopies_MatchActualNotices_NormalisingWrappingOnly()
    {
        var document = NormaliseWrapping(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "telemetry.md")));
        Assert.Contains(NormaliseWrapping(TelemetryConsentStore.FirstRunNoticeText), document, StringComparison.Ordinal);
        Assert.Contains(NormaliseWrapping(TelemetryCommand.EnableNoticeText), document, StringComparison.Ordinal);
    }

    [Fact]
    public void EnableAction_ActuallyPrintsTheCheckedNotice_WithoutInvokingHostConsent()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Cli", "Vouchfx.Cli", "TelemetryCommand.cs"));
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
        var method = Assert.Single(tree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
, m => m.Identifier.ValueText == "BuildEnable");
        var print = Assert.Single(method.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
, i => i.Expression.ToString() == "Console.Out.WriteLine"
                && i.ArgumentList.Arguments.Count == 1
                && i.ArgumentList.Arguments[0].Expression.ToString() == "EnableNoticeText");
        Assert.Equal("EnableNoticeText", print.ArgumentList.Arguments[0].Expression.ToString());
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "vouchfx.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository source is required for disclosure parity tests.");
    }

    private static string NormaliseWrapping(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
