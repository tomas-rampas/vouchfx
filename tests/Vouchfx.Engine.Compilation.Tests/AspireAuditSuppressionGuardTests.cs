using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Vouchfx.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Fails when the NuGet audit suppression for GHSA-f7vh-h5g3-7jrh in the root
/// <c>Directory.Build.props</c> outlives its reason, when it is joined by a second
/// suppression, or when the premise that justifies it stops being true.
/// </summary>
/// <remarks>
/// <para>
/// This is a bounded source and text check, not proof that the advisory is unreachable.
/// It reads every <c>*.props</c>, <c>*.targets</c>, <c>*.csproj</c>, <c>*.proj</c> and
/// <c>nuget.config</c> file under the repository root as XML, reads every <c>*.rsp</c> file and
/// every workflow under <c>.github/workflows</c> as plain text, and parses every
/// <c>src/**/*.cs</c> file with Roslyn twice, once with the Release and once with the Debug
/// preprocessor symbols of the <c>net8.0</c> build. The walk skips hidden directories (any name
/// starting with a dot) and <c>bin</c>, <c>obj</c> and <c>node_modules</c>, so a local SDK or
/// package cache is never mistaken for repository configuration. The workflows are read by
/// explicit path for that reason.
/// </para>
/// <para>
/// It does not run NuGet audit, resolve transitive Aspire packages, or start a topology. Code
/// under an <c>#if</c> symbol that neither build defines is not scanned. The suppression is
/// justified by the engine building its only Aspire topology with <c>DisableDashboard = true</c>
/// and by nothing referencing YARP. Both are inferences from the code, and these tests only
/// confirm the inferred facts still hold. A file that is not tracked but sits in the working
/// tree is scanned too, which can only make the guard stricter. MSBuild element and attribute
/// names are matched without regard to case, as MSBuild itself does.
/// </para>
/// </remarks>
public sealed class AspireAuditSuppressionGuardTests
{
    private const string AdvisoryUrl = "https://github.com/advisories/GHSA-f7vh-h5g3-7jrh";

    private const string AdvisoryId = "GHSA-f7vh-h5g3-7jrh";

    private const string HostingPackageId = "Aspire.Hosting";

    private const string RootPropsName = "Directory.Build.props";

    private const string PackagesPropsName = "Directory.Packages.props";

    private const string WorkflowsRelativePath = ".github/workflows";

    private const string TopologyRelativePath = "src/Engine/Vouchfx.Engine.Orchestration/HeadlessTopology.cs";

    private const string DismissalHint =
        "If this is deliberate, get a maintainer decision first and change this test in the same change.";

    /// <summary>The only Aspire release the advisory names as patched.</summary>
    private static readonly Version FirstPatchedVersion = new(13, 6, 5);

    private static readonly string[] SkippedDirectoryNames = { "bin", "obj", "node_modules" };

    private static readonly string[] AuditSettingElements = { "NuGetAudit", "NuGetAuditLevel", "NuGetAuditMode" };

    private static readonly string[] HostingPackageIds = { "Aspire.Hosting", "Aspire.Hosting.AppHost" };

    private static readonly string[] PackageElements = { "PackageVersion", "PackageReference", "GlobalPackageReference" };

    /// <summary>The construction sites <see cref="TopologyRelativePath"/> is expected to contain, sorted ordinally.</summary>
    private static readonly string[] ExpectedTopologySites = { "DistributedApplication.CreateBuilder", "new DistributedApplicationOptions" };

    private static readonly string[] SharedPreprocessorSymbols =
    {
        "TRACE", "NET", "NET8_0", "NETCOREAPP", "NETCOREAPP3_1_OR_GREATER",
        "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER",
    };

    private static readonly Regex AuditWarningCode = new(@"\bNU190\d\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex AuditTextPattern = new(@"NuGetAudit|\bNU190\d\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Lazy<List<(string File, XDocument Document)>> MsBuildDocuments = new(LoadMsBuildDocuments);

    private static readonly Lazy<List<ParsedConfiguration>> SourceConfigurations = new(ParseSourceFiles);

    private readonly ITestOutputHelper _output;

    public AspireAuditSuppressionGuardTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GivenMsBuildFiles_WhenAuditSuppressionsAreListed_ThenOnlyTheAspireAdvisoryInRootPropsIsSuppressed()
    {
        var found = ReadSuppressions();

        _output.WriteLine("NuGetAuditSuppress entries: " + string.Join(", ", found.Select(s => $"{s.File}={s.Url}")));

        var outside = found.Where(s => !IsRootProps(s.File)).ToList();
        Assert.True(
            outside.Count == 0,
            $"NuGetAuditSuppress is only allowed in the root {RootPropsName}. Found elsewhere: "
            + $"[{string.Join(", ", outside.Select(s => $"{s.File}={s.Url}"))}]. Move the item there if it is "
            + $"the approved {AdvisoryUrl}, otherwise delete it. {DismissalHint}");

        var urls = found.Select(s => s.Url).ToList();
        Assert.True(
            urls.Count == 0 || urls.SequenceEqual(new[] { AdvisoryUrl }, StringComparer.Ordinal),
            $"{RootPropsName} must suppress at most the single approved advisory {AdvisoryUrl}. "
            + $"Found: [{string.Join(", ", urls)}]. Delete the extra entries. A second suppression needs "
            + $"a maintainer approval and a deliberate change to this test.");
    }

    [Fact]
    public void GivenMsBuildFiles_WhenAuditSettingsAreScanned_ThenNoAuditSettingIsChanged()
    {
        var violations = new List<string>();
        foreach (var (file, document) in MsBuildDocuments.Value)
        {
            foreach (var element in document.Descendants())
            {
                var name = element.Name.LocalName;
                if (AuditSettingElements.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add($"{file}: <{name}> element");
                }

                var texts = element.Attributes().Select(a => a.Value).ToList();
                if (!element.HasElements)
                {
                    texts.Add(element.Value);
                }

                foreach (var text in texts)
                {
                    var match = AuditWarningCode.Match(text);
                    if (match.Success)
                    {
                        violations.Add($"{file}: <{name}> mentions {match.Value}");
                    }
                }
            }
        }

        _output.WriteLine("Audit setting findings: " + violations.Count);
        Assert.True(
            violations.Count == 0,
            "NuGet audit settings must not be changed anywhere in the MSBuild files. A weaker setting would hide "
            + $"advisories other than {AdvisoryId}, and any change, including a stricter one, needs a deliberate "
            + "edit of this test. Remove these (use a NuGetAuditSuppress item for one advisory only, and only with "
            + $"maintainer approval): [{string.Join("; ", violations)}]. {DismissalHint}");
    }

    [Fact]
    public void GivenResponseFilesAndWorkflows_WhenTextIsScanned_ThenNoAuditSettingIsChanged()
    {
        var root = RepoRoot.Resolve();
        var files = EnumerateFiles(root, p => p.EndsWith(".rsp", StringComparison.OrdinalIgnoreCase));
        var workflows = Path.Combine(root, WorkflowsRelativePath);
        if (Directory.Exists(workflows))
        {
            files.AddRange(Directory
                .EnumerateFiles(workflows)
                .Where(p => p.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.Ordinal));
        }

        var violations = new List<string>();
        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var match = AuditTextPattern.Match(lines[index]);
                if (match.Success)
                {
                    violations.Add($"{Relative(root, path)}:{index + 1}: {match.Value}");
                }
            }
        }

        _output.WriteLine($"Scanned {files.Count} response and workflow files; findings: {violations.Count}");
        Assert.True(
            violations.Count == 0,
            "NuGet audit settings must not be changed from a response file (*.rsp) or a workflow, for example with "
            + "-p:NuGetAudit=false, /nowarn:NU1903 or a NuGetAudit environment variable, because that would hide "
            + $"advisories other than {AdvisoryId} where this guard's MSBuild checks cannot see it. Any change, "
            + "including a stricter one, needs a deliberate edit of this test. Remove these: "
            + $"[{string.Join("; ", violations)}]. {DismissalHint}");
    }

    [Fact]
    public void GivenAspireHostingPin_WhenItReachesTheFirstPatchedRelease_ThenTheSuppressionIsAbsent()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        var version = ReadAspireHostingVersion();
        _output.WriteLine($"Central {HostingPackageId} PackageVersion: {version}");

        if (!IsAtLeast(version, FirstPatchedVersion))
        {
            return;
        }

        Assert.False(
            RootSuppressionIsPresent(),
            $"{PackagesPropsName} pins {HostingPackageId} {version}, which is {FirstPatchedVersion} or "
            + $"later and therefore patched for {AdvisoryId}. Delete the NuGetAuditSuppress item for "
            + $"{AdvisoryUrl} from {RootPropsName}, and lift the Aspire.* hold in "
            + ".github/dependabot.yml.");
    }

    [Fact]
    public void GivenSuppressionIsPresent_WhenProjectsAreScanned_ThenNoProjectOverridesAnAspireHostingVersion()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        var overrides = new List<string>();
        foreach (var (file, document) in MsBuildDocuments.Value)
        {
            foreach (var element in document.Descendants().Where(e => NameIs(e, "PackageReference")))
            {
                var id = PackageId(element);
                var versionOverride = AttributeValue(element, "VersionOverride");
                if (versionOverride is not null && HostingPackageIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    overrides.Add($"{file}: {id} VersionOverride={versionOverride}");
                }
            }
        }

        Assert.True(
            overrides.Count == 0,
            $"The {AdvisoryId} suppression is only valid while the central Aspire.Hosting pin is the version in "
            + "force. A VersionOverride on Aspire.Hosting or Aspire.Hosting.AppHost bypasses the removal check. "
            + $"Remove the override, or move the central pin and then delete the suppression. Found: [{string.Join("; ", overrides)}].");
    }

    [Fact]
    public void GivenSuppressionIsPresent_WhenPackagesAreScanned_ThenNothingReferencesYarp()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        var yarp = new List<string>();
        foreach (var (file, document) in MsBuildDocuments.Value)
        {
            foreach (var element in document.Descendants().Where(e => PackageElements.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
            {
                var id = PackageId(element);
                if (id.Contains("Yarp", StringComparison.OrdinalIgnoreCase))
                {
                    yarp.Add($"{file}: {id}");
                }
            }
        }

        Assert.True(
            yarp.Count == 0,
            $"The {AdvisoryId} suppression assumes nothing in this repository references YARP (Yarp.* or "
            + "Aspire.Hosting.Yarp), because the advisory is triggered through a YARP link in the Aspire dashboard. "
            + $"Found: [{string.Join("; ", yarp)}]. Remove the package, or re-assess reachability and get a "
            + "maintainer decision before keeping the suppression.");
    }

    [Fact]
    public void GivenSuppressionIsPresent_WhenSourceIsParsed_ThenNothingCallsAddYarp()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        var calls = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var configuration in SourceConfigurations.Value)
        {
            foreach (var (file, root) in configuration.Files)
            {
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = invocation.Expression switch
                    {
                        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                        SimpleNameSyntax simple => simple.Identifier.ValueText,
                        _ => null,
                    };
                    if (string.Equals(name, "AddYarp", StringComparison.Ordinal))
                    {
                        calls.Add($"{file}:{LineOf(invocation)} ({configuration.Name})");
                    }
                }
            }
        }

        Assert.True(
            calls.Count == 0,
            $"The {AdvisoryId} suppression assumes nothing in this repository uses YARP (Yarp.* or "
            + "Aspire.Hosting.Yarp), because the advisory is triggered through a YARP link in the Aspire dashboard. "
            + $"Found AddYarp calls: [{string.Join("; ", calls)}]. Remove them, or re-assess reachability and get a "
            + "maintainer decision before keeping the suppression.");
    }

    [Fact]
    public void GivenSuppressionIsPresent_WhenSourceIsParsed_ThenHeadlessTopologyIsTheOnlyAspireConstructionSite()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        foreach (var configuration in SourceConfigurations.Value)
        {
            var sites = new List<(string File, int Line, string Site)>();
            foreach (var (file, root) in configuration.Files)
            {
                foreach (var node in root.DescendantNodes())
                {
                    var site = DescribeConstructionSite(node);
                    if (site is not null)
                    {
                        sites.Add((file, LineOf(node), site));
                    }
                }
            }

            _output.WriteLine($"Aspire construction sites ({configuration.Name}): "
                + string.Join("; ", sites.Select(s => $"{s.File}:{s.Line}: {s.Site}")));

            var outside = sites
                .Where(s => !string.Equals(s.File, TopologyRelativePath, StringComparison.Ordinal))
                .Select(s => $"{s.File}:{s.Line}: {s.Site}")
                .ToList();
            Assert.True(
                outside.Count == 0,
                $"The {AdvisoryId} suppression is justified by {TopologyRelativePath} being the only place that builds "
                + $"an Aspire application, with the dashboard disabled. Another construction site was found in the "
                + $"{configuration.Name} build: [{string.Join("; ", outside)}]. Route it through HeadlessTopology, or "
                + "re-assess the advisory path and get a maintainer decision.");

            var topologySites = sites
                .Where(s => string.Equals(s.File, TopologyRelativePath, StringComparison.Ordinal))
                .Select(s => s.Site)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            Assert.True(
                topologySites.SequenceEqual(ExpectedTopologySites, StringComparer.Ordinal),
                $"{TopologyRelativePath} must contain exactly these Aspire construction sites in the "
                + $"{configuration.Name} build: [{string.Join(", ", ExpectedTopologySites)}]. Found: "
                + $"[{string.Join(", ", topologySites)}]. If the topology moved, update this guard to follow it. If a "
                + "construction site was added, make sure it disables the dashboard, then get a maintainer decision "
                + "and update the expected list in this test.");
        }
    }

    [Fact]
    public void GivenSuppressionIsPresent_WhenSourceIsParsed_ThenDisableDashboardIsOnlyEverAssignedTrue()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        foreach (var configuration in SourceConfigurations.Value)
        {
            var badAssignments = new List<string>();
            var topologyTrueAssignments = 0;
            var optionsWithoutTrue = new List<string>();

            foreach (var (file, root) in configuration.Files)
            {
                foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    if (!IsDisableDashboardTarget(assignment.Left))
                    {
                        continue;
                    }

                    if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                        && assignment.Right.IsKind(SyntaxKind.TrueLiteralExpression))
                    {
                        if (string.Equals(file, TopologyRelativePath, StringComparison.Ordinal))
                        {
                            topologyTrueAssignments++;
                        }

                        continue;
                    }

                    badAssignments.Add($"{file}:{LineOf(assignment)}: {assignment.ToString().Replace('\n', ' ').Trim()}");
                }

                foreach (var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
                {
                    if (!IsOptionsCreation(creation))
                    {
                        continue;
                    }

                    var setsTrue = creation.Initializer?.Expressions
                        .OfType<AssignmentExpressionSyntax>()
                        .Any(a => IsDisableDashboardTarget(a.Left) && a.Right.IsKind(SyntaxKind.TrueLiteralExpression)) ?? false;
                    if (!setsTrue)
                    {
                        optionsWithoutTrue.Add($"{file}:{LineOf(creation)}");
                    }
                }
            }

            Assert.True(
                badAssignments.Count == 0,
                $"The {AdvisoryId} suppression is justified by DisableDashboard only ever being set to the literal true. "
                + $"Found other assignments in the {configuration.Name} build: [{string.Join("; ", badAssignments)}]. "
                + "Restore the literal true, or re-assess the advisory path and get a maintainer decision.");
            Assert.True(
                optionsWithoutTrue.Count == 0,
                $"Every DistributedApplicationOptions must set DisableDashboard = true in its initialiser, because the "
                + $"{AdvisoryId} suppression depends on it. Missing in the {configuration.Name} build at: "
                + $"[{string.Join("; ", optionsWithoutTrue)}].");
            Assert.True(
                topologyTrueAssignments > 0,
                $"{TopologyRelativePath} no longer contains a DisableDashboard = true assignment that compiles in the "
                + $"{configuration.Name} build (comments, strings and code that #if leaves out of that build do not "
                + "count). Re-assess whether the advisory path is still unreachable, then either restore the setting "
                + "in both the Debug and Release builds or remove the suppression.");
        }
    }

    private static string? DescribeConstructionSite(SyntaxNode node)
    {
        switch (node)
        {
            case ObjectCreationExpressionSyntax creation:
                var typeName = SimpleName(creation.Type);
                return typeName is "DistributedApplicationOptions" or "DistributedApplicationBuilder"
                    ? "new " + typeName
                    : null;
            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                var targetName = ImplicitTargetTypeName(implicitCreation);
                return targetName is "DistributedApplicationOptions" or "DistributedApplicationBuilder"
                    ? "new " + targetName
                    : null;
            case InvocationExpressionSyntax invocation when DescribeBuilderInvocation(invocation) is { } description:
                return description;
            case IdentifierNameSyntax { Identifier.ValueText: "DistributedApplicationTestingBuilder" }:
                return "DistributedApplicationTestingBuilder";
            default:
                return null;
        }
    }

    private static string? DescribeBuilderInvocation(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateBuilder" or "CreateSlimBuilder" } access
        && SimpleName(access.Expression) == "DistributedApplication"
            ? "DistributedApplication." + access.Name.Identifier.ValueText
            : null;

    /// <summary>
    /// Names the type a target-typed <c>new()</c> creates, when the syntax states it: a local or
    /// field declaration, a property initialiser, a parameter default, a return from a member
    /// with a declared type, or an argument to <c>DistributedApplication.CreateBuilder</c>.
    /// </summary>
    private static string? ImplicitTargetTypeName(ImplicitObjectCreationExpressionSyntax creation)
    {
        switch (creation.Parent)
        {
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } }:
                return SimpleName(declaration.Type);
            case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax property }:
                return SimpleName(property.Type);
            case EqualsValueClauseSyntax { Parent: ParameterSyntax { Type: { } parameterType } }:
                return SimpleName(parameterType);
            case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } }
                when DescribeBuilderInvocation(invocation) is not null:
                return "DistributedApplicationOptions";
            case ReturnStatementSyntax or ArrowExpressionClauseSyntax:
                var member = creation.Ancestors().FirstOrDefault(a =>
                    a is MethodDeclarationSyntax or PropertyDeclarationSyntax or LocalFunctionStatementSyntax
                        or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax);
                return member switch
                {
                    MethodDeclarationSyntax method => SimpleName(method.ReturnType),
                    LocalFunctionStatementSyntax local => SimpleName(local.ReturnType),
                    PropertyDeclarationSyntax property => SimpleName(property.Type),
                    AccessorDeclarationSyntax { Parent.Parent: PropertyDeclarationSyntax property } => SimpleName(property.Type),
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static bool IsOptionsCreation(BaseObjectCreationExpressionSyntax creation) =>
        creation switch
        {
            ObjectCreationExpressionSyntax typed => SimpleName(typed.Type) == "DistributedApplicationOptions",
            ImplicitObjectCreationExpressionSyntax implicitCreation => ImplicitTargetTypeName(implicitCreation) == "DistributedApplicationOptions",
            _ => false,
        };

    private static bool IsDisableDashboardTarget(ExpressionSyntax left) =>
        left switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == "DisableDashboard",
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == "DisableDashboard",
            _ => false,
        };

    private static string? SimpleName(SyntaxNode node) =>
        node switch
        {
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            QualifiedNameSyntax qualified => SimpleName(qualified.Right),
            AliasQualifiedNameSyntax alias => SimpleName(alias.Name),
            NullableTypeSyntax nullable => SimpleName(nullable.ElementType),
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        };

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static bool NameIs(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string PackageId(XElement element) =>
        (AttributeValue(element, "Include") ?? AttributeValue(element, "Update") ?? string.Empty).Trim();

    private static bool IsRootProps(string relativePath) =>
        string.Equals(relativePath, RootPropsName, StringComparison.Ordinal);

    private static bool RootSuppressionIsPresent() =>
        ReadSuppressions().Any(s => IsRootProps(s.File) && string.Equals(s.Url, AdvisoryUrl, StringComparison.Ordinal));

    private static List<(string File, string Url)> ReadSuppressions()
    {
        var result = new List<(string File, string Url)>();
        foreach (var (file, document) in MsBuildDocuments.Value)
        {
            foreach (var element in document.Descendants().Where(e => NameIs(e, "NuGetAuditSuppress")))
            {
                result.Add((file, (AttributeValue(element, "Include") ?? string.Empty).Trim()));
            }
        }

        return result;
    }

    private static List<(string File, XDocument Document)> LoadMsBuildDocuments()
    {
        var root = RepoRoot.Resolve();
        var documents = new List<(string, XDocument)>();
        foreach (var path in EnumerateFiles(root, IsMsBuildFile))
        {
            var relative = Relative(root, path);
            try
            {
                documents.Add((relative, XDocument.Load(path)));
            }
            catch (System.Xml.XmlException exception)
            {
                Assert.Fail($"{relative} is not well-formed XML, so the audit guard cannot read it: {exception.Message}");
            }
        }

        return documents;
    }

    private static List<ParsedConfiguration> ParseSourceFiles()
    {
        var root = RepoRoot.Resolve();
        var paths = EnumerateFiles(Path.Combine(root, "src"), p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var texts = paths.Select(p => (File: Relative(root, p), Path: p, Text: File.ReadAllText(p))).ToList();

        // Each build's own symbols: code that #if leaves out of a build is skipped trivia in that
        // build's tree, so it never counts there, and every check runs against both trees.
        var configurations = new List<ParsedConfiguration>();
        foreach (var (name, buildSymbol) in new[] { ("Release", "RELEASE"), ("Debug", "DEBUG") })
        {
            var options = CSharpParseOptions.Default.WithPreprocessorSymbols(SharedPreprocessorSymbols.Append(buildSymbol));
            var files = texts
                .Select(t => (t.File, CSharpSyntaxTree.ParseText(t.Text, options, path: t.Path).GetRoot()))
                .ToList();
            configurations.Add(new ParsedConfiguration(name, files));
        }

        return configurations;
    }

    private static bool IsMsBuildFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".proj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "nuget.config", StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool IsSkippedDirectory(string name) =>
        name.StartsWith('.') || SkippedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static List<string> EnumerateFiles(string directory, Func<string, bool> include)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        var files = new List<string>();
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            files.AddRange(Directory.EnumerateFiles(current).Where(include));
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (!IsSkippedDirectory(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static Version ReadAspireHostingVersion()
    {
        var document = Assert.Single(MsBuildDocuments.Value, d => string.Equals(d.File, PackagesPropsName, StringComparison.Ordinal)).Document;
        var versions = document
            .Descendants()
            .Where(element => NameIs(element, "PackageVersion"))
            .Where(element => string.Equals(AttributeValue(element, "Include")?.Trim(), HostingPackageId, StringComparison.OrdinalIgnoreCase))
            .Select(element => AttributeValue(element, "Version"))
            .ToList();

        var versionText = Assert.Single(versions)?.Trim();
        if (!Version.TryParse(versionText, out var parsed))
        {
            var kind = versionText is not null && (versionText.Contains('-', StringComparison.Ordinal) || versionText.Contains('+', StringComparison.Ordinal))
                ? "a prerelease or build-metadata version"
                : "not a plain numeric version";
            Assert.Fail(
                $"{PackagesPropsName} {HostingPackageId} Version '{versionText}' is {kind}, so this guard cannot "
                + $"tell whether it has reached {FirstPatchedVersion} and fails closed. Pin a stable release, or "
                + "update this guard to compare prerelease versions deliberately.");
        }

        return parsed!;
    }

    /// <summary>
    /// Compares major, minor and build. <see cref="Version"/> stores an omitted build as -1,
    /// so that component is treated as 0. Revision is not compared.
    /// </summary>
    private static bool IsAtLeast(Version actual, Version minimum)
    {
        var normalised = new Version(actual.Major, actual.Minor, Math.Max(actual.Build, 0));
        return normalised >= minimum;
    }

    private sealed record ParsedConfiguration(string Name, IReadOnlyList<(string File, SyntaxNode Root)> Files);
}
