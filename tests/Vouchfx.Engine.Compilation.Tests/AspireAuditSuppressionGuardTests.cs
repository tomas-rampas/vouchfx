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
/// This is a bounded source and text check, not proof that the advisory is unreachable, and it does
/// not evaluate MSBuild. It reads every <c>*.props</c>, <c>*.targets</c>, <c>*.csproj</c>,
/// <c>*.proj</c> and <c>nuget.config</c> file under the repository root as XML, and reads these as
/// plain text: every <c>*.rsp</c> file, every YAML file under <c>.github/workflows</c>, every YAML,
/// <c>.sh</c> and <c>.ps1</c> file under <c>.github/actions</c>, <c>ci</c> and <c>installer</c>, and
/// every <c>.sh</c> and <c>.ps1</c> file under <c>scripts</c> and <c>examples</c>. It parses every
/// <c>src/**/*.cs</c> file with Roslyn four times, with the Release and the Debug preprocessor
/// symbols of both the <c>net10.0</c> and the <c>net8.0</c> build. Test sources are parsed the same
/// way for the <c>DisableDashboard</c> check only, and only when the file names
/// <c>DisableDashboard</c> or <c>DistributedApplicationOptions</c>. The walk skips hidden
/// directories (any name starting with a dot) below its starting directory, and <c>bin</c>,
/// <c>obj</c> and <c>node_modules</c>, so a local SDK or package cache is never mistaken for
/// repository configuration. The workflows and actions are read by explicit path for that reason.
/// A directory or file that disappears during the walk is treated as absent.
/// </para>
/// <para>
/// Imports are checked rather than followed. Every <c>&lt;Import Project&gt;</c> must end in
/// <c>.props</c> or <c>.targets</c>, must contain no wildcard (<c>*</c> or <c>?</c>), and must have
/// no path segment that starts with a dot or is named <c>bin</c>, <c>obj</c> or
/// <c>node_modules</c> (compared without regard to case, after property expansions are removed from
/// the segment), which are the directories the walk skips. A relative import of a file inside the
/// repository is therefore a file this guard reads. The one property function accepted is
/// <c>GetPathOfFileAbove</c>: its file argument gets the same checks, and its start-directory
/// argument gets the segment checks. Any other target that ends in a property expansion fails.
/// </para>
/// <para>
/// Text and XML values are decoded (<c>%XX</c>, up to four times) before the audit patterns run,
/// which is stricter than MSBuild's single unescape for these patterns. A
/// <c>TreatWarningsAsErrors</c> value is the exception and is decoded once, as MSBuild does, so
/// <c>%2574rue</c> is not accepted as true. In XML its value must be <c>true</c>, and the root
/// <c>Directory.Build.props</c> must set it without a condition. In text, <c>TreatWarningsAsErrors</c>
/// followed by <c>=</c> or <c>:</c> and any value other than <c>true</c> fails (an empty value,
/// <c>off</c>, <c>no</c> or a variable included), as does <c>-warnaserror-</c>, because a value this
/// guard cannot read as true may turn NU190x back into a warning.
/// </para>
/// <para>
/// It does not run NuGet audit, resolve transitive Aspire packages, or start a topology. Code
/// under an <c>#if</c> symbol that neither build defines is not scanned. The suppression is
/// justified by the engine building its only Aspire topology with <c>DisableDashboard = true</c>
/// and by nothing referencing YARP. Both are inferences from the code, and these tests only
/// confirm the inferred facts still hold. A file that is not tracked but sits in the working
/// tree is scanned too, which can only make the guard stricter. MSBuild element and attribute
/// names are matched without regard to case, as MSBuild itself does. Package ids are decoded
/// before they are compared. Using aliases and <c>using static</c> directives are collected across
/// all parsed files, because a global using applies to every file, and an alias name maps to every
/// type any file aliases it to, so an alias in one file can only make the construction-site check
/// stricter in another. A <c>&lt;Using&gt;</c> item that names a <c>DistributedApplication</c> type
/// is refused for the same reason. In test sources <c>DisableDashboard</c> may be assigned only the
/// literal <c>true</c>, and options may be created without setting it. The check that no Aspire
/// dashboard package is referenced, and the check that every Aspire host imports the shared DCP
/// targets, back up NuGet audit: with warnings as errors, restoring a dashboard package that
/// carries an advisory is expected to fail in NuGet audit first, and these checks name the cause.
/// </para>
/// <para>
/// Accepted limits. The <c>DisableDashboard</c> check matches assignments in syntax, so a name built
/// by reflection or a string is not seen, and an options object created by a type this guard cannot
/// name is not either. Text of other kinds is not scanned (<c>*.py</c>, <c>*.bat</c>, <c>*.cmd</c>,
/// <c>*.wxs</c>, Dockerfiles, and YAML under <c>scripts</c> and <c>examples</c>). YAML <c>\x</c> and
/// <c>\u</c> escapes and shell quote concatenation are not decoded, so a setting spelled with them is
/// not seen. An import is not proved to stay in the repository: a target that starts with a property
/// other than <c>$(MSBuildThisFileDirectory)</c> or <c>$(MSBuildProjectDirectory)</c> (a property set
/// elsewhere, <c>$(NuGetPackageRoot)</c>), an absolute path, a <c>..</c> path that leaves the
/// repository root, and an <c>Sdk=</c> import are all allowed, because SDK imports need them, and a
/// file such an import reaches is not read. The agreement of <c>VouchfxDcpVersion</c>, the central
/// <c>Aspire.Hosting.AppHost</c> version and each <c>Aspire.AppHost.Sdk</c> version is checked by
/// <c>AspireVersionLockstepGuardTests</c>, not here. Code ownership of the files of this contract is
/// closed by the entries in <c>.github/CODEOWNERS</c> and has no test, because ownership rules are a
/// repository setting that a file in the tree cannot prove. Host platforms outside the four that
/// <c>Vouchfx.Dcp.targets</c> covers are documented there and are not this guard's concern.
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

    private const string ActionsRelativePath = ".github/actions";

    private const string ScriptsRelativePath = "scripts";

    private const string CiRelativePath = "ci";

    private const string InstallerRelativePath = "installer";

    private const string ExamplesRelativePath = "examples";

    private const string CliProjectRelativePath = "src/Cli/Vouchfx.Cli/Vouchfx.Cli.csproj";

    private const string DcpTargetsName = "Vouchfx.Dcp.targets";

    private const string AppHostSdkName = "Aspire.AppHost.Sdk";

    private const string TopologyRelativePath = "src/Engine/Vouchfx.Engine.Orchestration/HeadlessTopology.cs";

    private const string DismissalHint =
        "If this is deliberate, get a maintainer decision first and change this test in the same change.";

    /// <summary>The only Aspire release the advisory names as patched.</summary>
    private static readonly Version FirstPatchedVersion = new(13, 6, 5);

    /// <summary>The directories read as plain text, and the file extensions read in each, besides the <c>*.rsp</c> files under the root.</summary>
    private static readonly (string Directory, string[] Extensions)[] TextScans =
    {
        (WorkflowsRelativePath, new[] { ".yml", ".yaml" }),
        (ActionsRelativePath, new[] { ".yml", ".yaml", ".sh", ".ps1" }),
        (CiRelativePath, new[] { ".yml", ".yaml", ".sh", ".ps1" }),
        (InstallerRelativePath, new[] { ".yml", ".yaml", ".sh", ".ps1" }),
        (ScriptsRelativePath, new[] { ".sh", ".ps1" }),
        (ExamplesRelativePath, new[] { ".sh", ".ps1" }),
    };

    private static readonly string[] SkippedDirectoryNames = { "bin", "obj", "node_modules" };

    private static readonly string[] AuditSettingElements = { "NuGetAudit", "NuGetAuditLevel", "NuGetAuditMode" };

    private static readonly string[] HostingPackageIds = { "Aspire.Hosting", "Aspire.Hosting.AppHost" };

    private static readonly string[] PackageElements = { "PackageVersion", "PackageReference", "GlobalPackageReference" };

    /// <summary>The construction sites <see cref="TopologyRelativePath"/> is expected to contain, sorted ordinally.</summary>
    private static readonly string[] ExpectedTopologySites = { "DistributedApplication.CreateBuilder", "new DistributedApplicationOptions" };

    /// <summary>Type names whose construction, or a using alias of which, is an Aspire construction site.</summary>
    private static readonly string[] AspireTypeNames =
    {
        "DistributedApplication", "DistributedApplicationOptions", "DistributedApplicationBuilder", "DistributedApplicationTestingBuilder",
    };

    private static readonly string[] SharedPreprocessorSymbols =
    {
        "TRACE", "NET", "NETCOREAPP",
        "NETCOREAPP1_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER", "NETCOREAPP2_0_OR_GREATER",
        "NETCOREAPP2_1_OR_GREATER", "NETCOREAPP2_2_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER",
        "NET5_0_OR_GREATER", "NET6_0_OR_GREATER", "NET7_0_OR_GREATER", "NET8_0_OR_GREATER",
    };

    /// <summary>
    /// The framework-specific symbols of each target framework under <c>src</c>: net10.0 for the
    /// engine, providers and CLI, and net8.0 for the provider-contract libraries. Every file is
    /// parsed under both, so code that one framework's <c>#if</c> hides is still seen.
    /// </summary>
    private static readonly (string Name, string[] Symbols)[] FrameworkPreprocessorSymbols =
    {
        ("net8.0", new[] { "NET8_0" }),
        ("net10.0", new[] { "NET10_0", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER" }),
    };

    private static readonly Regex PercentEscape = new("%([0-9A-Fa-f]{2})", RegexOptions.CultureInvariant);

    /// <summary>MSBuild's <c>GetPathOfFileAbove</c>: the first argument names the imported file, the second a start directory.</summary>
    private static readonly Regex GetPathOfFileAbove = new(
        @"^\$\(\[MSBuild\]::GetPathOfFileAbove\(\s*'(?<file>[^']*)'\s*(?:,\s*'(?<start>[^']*)'\s*)?\)\)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A property expansion such as <c>$(MSBuildThisFileDirectory)</c>, one level of nested parentheses allowed.</summary>
    private static readonly Regex PropertyExpansion = new(@"\$\((?:[^()]|\([^()]*\))*\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Text that turns warnings-as-errors off from a command line, a script or an action:
    /// <c>TreatWarningsAsErrors</c> followed by <c>=</c> or <c>:</c> and anything but <c>true</c>
    /// (an empty value included), or <c>-warnaserror-</c>. The atomic group stops the optional
    /// spaces from backing off to make <c>: true</c> look like a value that is not <c>true</c>.
    /// </summary>
    private static readonly Regex WarningsAsErrorsOffText = new(
        @"TreatWarningsAsErrors(?>[""']?\s*[=:]\s*[""']?)(?!true\b)|(?<![\w])[-/]warnaserror-(?![:\w])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex AuditWarningCode = new(@"(?<![0-9])NU190\d(?![0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex AuditTextPattern = new(@"NuGetAudit|(?<![0-9])NU190\d(?![0-9])", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Lazy<List<(string File, XDocument Document)>> MsBuildDocuments = new(LoadMsBuildDocuments);

    private static readonly Lazy<List<ParsedConfiguration>> SourceConfigurations = new(ParseSourceFiles);

    private static readonly Lazy<List<ParsedConfiguration>> TestConfigurations = new(ParseTestFiles);

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

    [Theory]
    [InlineData("$(NoWarn);NU1903")]
    [InlineData("-nowarn:NU1903")]
    [InlineData("/nowarn:NU1903,NU1904")]
    [InlineData("CS1591%3BNU1903")]
    [InlineData("CS1591%3bNU1903")]
    [InlineData("-p:NoWarn=\"NU1603%3BNU1903\"")]
    [InlineData("nu1903")]
    [InlineData("CS1591;%20NU1903")]
    [InlineData("CS1591%3B%20NU1904")]
    [InlineData("N%55190%33")]
    [InlineData("%4eU1903")]
    [InlineData("%254eU1903")]
    public void GivenAuditWarningCodeText_WhenPatternsAreMatched_ThenBothPatternsMatch(string text)
    {
        Assert.True(FindAuditWarningCode(text).Success, $"The warning-code pattern missed '{text}'.");
        Assert.True(FindAuditText(text).Success, $"The text pattern missed '{text}'.");
    }

    [Theory]
    [InlineData("-p:NuGetAudit=false")]
    [InlineData("-p:NuGet%41udit=false")]
    [InlineData("%4EuGetAuditLevel")]
    public void GivenNuGetAuditSwitch_WhenTextPatternIsMatched_ThenItMatchesButTheWarningCodePatternDoesNot(string text)
    {
        Assert.True(FindAuditText(text).Success, $"The text pattern missed '{text}'.");
        Assert.False(FindAuditWarningCode(text).Success, $"The warning-code pattern matched '{text}'.");
    }

    [Theory]
    [InlineData("NU19034")]
    [InlineData("NU1903%30")]
    [InlineData("1NU1903")]
    public void GivenLongerNumericCode_WhenPatternsAreMatched_ThenNeitherPatternMatches(string text)
    {
        Assert.False(FindAuditWarningCode(text).Success, $"The warning-code pattern matched '{text}'.");
        Assert.False(FindAuditText(text).Success, $"The text pattern matched '{text}'.");
    }

    [Theory]
    [InlineData("-p:TreatWarningsAsErrors=false", true)]
    [InlineData("/p:TreatWarningsAsErrors=\"False\"", true)]
    [InlineData("-p:TreatWarningsAsErrors=%66alse", true)]
    [InlineData("export TREATWARNINGSASERRORS: false", true)]
    [InlineData("dotnet build -warnaserror-", true)]
    [InlineData("dotnet build /warnaserror- --no-restore", true)]
    [InlineData("-p:TreatWarningsAsErrors=off", true)]
    [InlineData("-p:TreatWarningsAsErrors=no", true)]
    [InlineData("-p:TreatWarningsAsErrors=", true)]
    [InlineData("-p:TreatWarningsAsErrors= -c Release", true)]
    [InlineData("-p:TreatWarningsAsErrors=$STRICT", true)]
    [InlineData("-p:TreatWarningsAsErrors=trueish", true)]
    [InlineData("-p:TreatWarningsAsErrors=%2574rue", true)]
    [InlineData("TreatWarningsAsErrors:", true)]
    [InlineData("-p:TreatWarningsAsErrors=true", false)]
    [InlineData("-p:TreatWarningsAsErrors=\"True\"", false)]
    [InlineData("-p:TreatWarningsAsErrors=%74rue", false)]
    [InlineData("TreatWarningsAsErrors: true", false)]
    [InlineData("dotnet build -warnaserror", false)]
    [InlineData("dotnet build -warnaserror+", false)]
    [InlineData("TreatWarningsAsErrors is true, not false", false)]
    [InlineData("# TreatWarningsAsErrors stays true", false)]
    public void GivenWarningsAsErrorsText_WhenPatternIsMatched_ThenOnlyTurningItOffMatches(string text, bool expected)
    {
        Assert.Equal(expected, FindWarningsAsErrorsOff(text).Success);
    }

    [Theory]
    [InlineData("Vouchfx.Dcp.targets", null)]
    [InlineData("..\\..\\src\\Cli\\Vouchfx.Cli\\Vouchfx.Dcp.targets", null)]
    [InlineData("$(MSBuildThisFileDirectory)../build/shared.props", null)]
    [InlineData("$(MSBuildThisFileDirectory)../build/Common.props", null)]
    [InlineData("$(MSBuildToolsPath)\\x.targets", null)]
    [InlineData("$(MSBuildProjectDirectory)/build/x.targets", null)]
    [InlineData("$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))", null)]
    [InlineData("build/*.TARGETS", "wildcard")]
    [InlineData("build/**/*.props", "wildcard")]
    [InlineData("build/x?.props", "wildcard")]
    [InlineData("obj/audit.props", "skipped directory")]
    [InlineData("tools/bin/audit.props", "skipped directory")]
    [InlineData("tools\\OBJ\\audit.props", "skipped directory")]
    [InlineData("node_modules/x/y.targets", "skipped directory")]
    [InlineData("$(MSBuildThisFileDirectory)obj/audit.props", "skipped directory")]
    [InlineData("$(MSBuildThisFileDirectory).cfg/x.props", "hidden path segment")]
    [InlineData("../../../../outside.props", null)]
    [InlineData("$(NuGetPackageRoot)evil/1.0.0/build/evil.props", null)]
    [InlineData("$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory).cfg/sub/'))", "hidden path segment")]
    [InlineData("$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)obj/'))", "skipped directory")]
    [InlineData("$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', 'tools/bin/'))", "skipped directory")]
    [InlineData("$([MSBuild]::GetPathOfFileAbove('build/*.props'))", "wildcard")]
    [InlineData("build/.hidden/x.props", "hidden path segment")]
    [InlineData("build/%2Ehidden/x.props", "hidden path segment")]
    [InlineData("..\\.hidden\\x.targets", "hidden path segment")]
    [InlineData(".x.props", "hidden path segment")]
    [InlineData("build/x.txt", "does not end in .props or .targets")]
    [InlineData("build/x.props.txt", "does not end in .props or .targets")]
    [InlineData("$(SomeProperty)", "does not end in .props or .targets")]
    [InlineData("$([MSBuild]::GetPathOfFileAbove('x.txt'))", "does not end in .props or .targets")]
    [InlineData("$([MSBuild]::GetPathOfFileAbove('.cfg/x.props'))", "hidden path segment")]
    [InlineData("", "no Project")]
    [InlineData(null, "no Project")]
    public void GivenImportTarget_WhenItIsChecked_ThenOnlyVisibleProjectFilesPass(string? project, string? expectedProblem)
    {
        var problem = ImportProblem(project);

        if (expectedProblem is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.NotNull(problem);
            Assert.Contains(expectedProblem, problem, StringComparison.Ordinal);
        }
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
                    var match = FindAuditWarningCode(text);
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
    public void GivenTextFiles_WhenTheyAreScanned_ThenNoAuditSettingIsChangedAndWarningsStayErrors()
    {
        var root = RepoRoot.Resolve();
        var files = TextScanFiles(root);

        var violations = new List<string>();
        var warningViolations = new List<string>();
        foreach (var path in files)
        {
            var lines = TryReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var match = FindAuditText(lines[index]);
                if (match.Success)
                {
                    violations.Add($"{Relative(root, path)}:{index + 1}: {match.Value}");
                }

                var off = FindWarningsAsErrorsOff(lines[index]);
                if (off.Success)
                {
                    warningViolations.Add($"{Relative(root, path)}:{index + 1}: {off.Value}");
                }
            }
        }

        _output.WriteLine($"Scanned {files.Count} response, workflow, action and script files; findings: {violations.Count + warningViolations.Count}");
        Assert.True(
            violations.Count == 0,
            "NuGet audit settings must not be changed from a response file (*.rsp), a workflow, a composite action, "
            + "a CI definition under ci or installer, or a script (*.sh, *.ps1 under scripts, examples, installer or ci), for example with "
            + "-p:NuGetAudit=false, /nowarn:NU1903 or a NuGetAudit environment variable, because that would hide "
            + $"advisories other than {AdvisoryId} where this guard's MSBuild checks cannot see it. Any change, "
            + "including a stricter one, needs a deliberate edit of this test. Remove these: "
            + $"[{string.Join("; ", violations)}]. {DismissalHint}");
        Assert.True(
            warningViolations.Count == 0,
            "Warnings must stay errors. Giving TreatWarningsAsErrors (after = or :) any value other than true, or passing "
            + "-warnaserror-, from a response file, workflow, action, CI definition or script would turn NU190x audit "
            + "warnings back into warnings. Remove these: "
            + $"[{string.Join("; ", warningViolations)}]. {DismissalHint}");
    }

    [Fact]
    public void GivenARepositoryLayout_WhenTextScanFilesAreListed_ThenActionsAndScriptsAreIncludedAndHiddenOnesAreNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "vouchfx-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var relative in new[]
            {
                ".github/workflows/build.yml", ".github/workflows/notes.txt", ".github/actions/setup/action.yml",
                ".github/actions/setup/nested/run.sh", ".github/actions/setup/.hidden/skipped.sh", "scripts/a.sh",
                "scripts/ci/b.ps1", "scripts/ci/c.py", "scripts/node_modules/d.sh", "scripts/.cache/e.ps1", "tools/f.rsp",
                "tools/g.sh", "installer/windows/h.ps1", "installer/windows/i.wxs", "ci/gitlab/j.yml", "ci/gitlab/k.py",
                "examples/l.sh", "examples/m.ps1", "examples/n.e2e.yaml", "examples/sub/obj/o.sh", "scripts/p.yml",
            })
            {
                var path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "# fixture\n");
            }

            var found = TextScanFiles(root).Select(p => Relative(root, p)).ToList();

            Assert.Equal(
                new List<string>
                {
                    ".github/actions/setup/action.yml", ".github/actions/setup/nested/run.sh", ".github/workflows/build.yml",
                    "ci/gitlab/j.yml", "examples/l.sh", "examples/m.ps1", "installer/windows/h.ps1",
                    "scripts/a.sh", "scripts/ci/b.ps1", "tools/f.rsp",
                },
                found.OrderBy(p => p, StringComparer.Ordinal).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GivenTheRepository_WhenTextScanFilesAreListed_ThenEveryScannedDirectoryThatHoldsFilesContributesSome()
    {
        var root = RepoRoot.Resolve();
        var found = TextScanFiles(root).Select(p => Relative(root, p)).ToList();

        Assert.NotEmpty(found);
        foreach (var scan in TextScans)
        {
            var directory = Path.Combine(root, scan.Directory);
            var holdsFiles = Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Any(p => scan.Extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
            if (holdsFiles)
            {
                Assert.True(
                    found.Any(p => p.StartsWith(scan.Directory + "/", StringComparison.Ordinal)),
                    $"{scan.Directory} holds files of a scanned kind but the text scan found none of them, so the search is probably broken.");
            }
        }
    }

    [Fact]
    public void GivenADirectoryRemovedDuringTheWalk_WhenFilesAreEnumerated_ThenTheWalkSkipsItAndKeepsTheRest()
    {
        var root = Path.Combine(Path.GetTempPath(), "vouchfx-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var name in new[] { "a", "b" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                File.WriteAllText(Path.Combine(root, name, "f.txt"), "x");
            }

            // Both children are queued before either is walked. The first file seen deletes the other
            // directory, which is what a parallel run clearing a build directory does.
            var deleted = false;
            var found = EnumerateFiles(root, path =>
            {
                if (!deleted)
                {
                    deleted = true;
                    var other = string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "a", StringComparison.Ordinal) ? "b" : "a";
                    Directory.Delete(Path.Combine(root, other), recursive: true);
                }

                return true;
            });

            Assert.Single(found);
            Assert.Empty(EnumerateFiles(Path.Combine(root, "missing"), _ => true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GivenMsBuildFiles_WhenImportsAreScanned_ThenEveryImportIsAVisibleProjectFile()
    {
        var violations = new List<string>();
        var imports = 0;
        foreach (var (file, document) in MsBuildDocuments.Value)
        {
            foreach (var element in document.Descendants().Where(e => NameIs(e, "Import")))
            {
                imports++;
                var project = AttributeValue(element, "Project");
                var problem = ImportProblem(project);
                if (problem is not null)
                {
                    violations.Add($"{file}: <Import Project=\"{project}\"> {problem}");
                }
            }
        }

        _output.WriteLine($"Imports scanned: {imports}; findings: {violations.Count}");
        Assert.True(imports > 0, "No <Import> element was found in any MSBuild file, so the search is probably broken.");
        Assert.True(
            violations.Count == 0,
            "Every <Import Project> must end in .props or .targets, contain no wildcard, and have no path segment that "
            + "starts with a dot or is named bin, obj or node_modules, so that a relative import of a file inside the "
            + "repository is one the audit guard reads: an audit override in a file of another extension, or in a "
            + "directory the guard skips, would not be seen. Accepted limit: a target that starts with a property other "
            + "than $(MSBuildThisFileDirectory) or $(MSBuildProjectDirectory), an absolute path, a .. path that leaves the "
            + "repository root, and an Sdk= import are not rejected, and a file they reach is not read. "
            + $"Found: [{string.Join("; ", violations)}]. {DismissalHint}");
    }

    [Fact]
    public void GivenMsBuildFiles_WhenTreatWarningsAsErrorsIsScanned_ThenNoFileTurnsItOffAndTheRootEnablesIt()
    {
        var off = new List<string>();
        foreach (var (file, document) in MsBuildDocuments.Value)
        {
            off.AddRange(TreatWarningsAsErrorsProblems(document).Select(p => $"{file}: {p}"));
        }

        Assert.True(
            off.Count == 0,
            "TreatWarningsAsErrors must only ever be the literal true (decoded once, as MSBuild does). A project setting "
            + "it to false, or to a value this guard cannot read, would turn NU190x audit warnings back into warnings. "
            + $"Found: [{string.Join("; ", off)}]. "
            + DismissalHint);

        var rootProps = Assert.Single(MsBuildDocuments.Value, d => IsRootProps(d.File)).Document;
        Assert.Contains(
            rootProps.Descendants().Where(e => NameIs(e, "TreatWarningsAsErrors")),
            e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)
                && !e.AncestorsAndSelf().Any(a => AttributeValue(a, "Condition") is not null));
    }

    [Theory]
    [InlineData("<PackageReference Include=\"Yarp.ReverseProxy\" />", true)]
    [InlineData("<PackageReference Include=\"%59arp.ReverseProxy\" />", true)]
    [InlineData("<PackageVersion Include=\"Aspire.Hosting.%59arp\" Version=\"1.0.0\" />", true)]
    [InlineData("<PackageReference Include=\"Newtonsoft.Json\" />", false)]
    public void GivenPackageElement_WhenYarpIdsAreListed_ThenEscapedIdsAreFound(string element, bool expected)
    {
        var document = XDocument.Parse($"<Project><ItemGroup>{element}</ItemGroup></Project>");

        Assert.Equal(expected, PackageIds(document, "Yarp").Count == 1);
    }

    [Theory]
    [InlineData("<PackageReference Include=\"Aspire.Hosting.AppHost\" VersionOverride=\"13.6.5\" />", true)]
    [InlineData("<PackageReference Include=\"Aspire.Hosting%2EAppHost\" VersionOverride=\"13.6.5\" />", true)]
    [InlineData("<PackageReference Include=\"Aspire.Hosting\"><VersionOverride>13.6.5</VersionOverride></PackageReference>", true)]
    [InlineData("<PackageReference Include=\"Aspire.Hosting\" />", false)]
    [InlineData("<PackageReference Include=\"Newtonsoft.Json\" VersionOverride=\"13.0.1\" />", false)]
    public void GivenPackageReference_WhenHostingOverridesAreListed_ThenEscapedIdsAreFound(string element, bool expected)
    {
        var document = XDocument.Parse($"<Project><ItemGroup>{element}</ItemGroup></Project>");

        Assert.Equal(expected, HostingOverrides(document).Count == 1);
    }

    [Theory]
    [InlineData("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", false)]
    [InlineData("<TreatWarningsAsErrors> True </TreatWarningsAsErrors>", false)]
    [InlineData("<TreatWarningsAsErrors>%74rue</TreatWarningsAsErrors>", false)]
    [InlineData("<TreatWarningsAsErrors>%2574rue</TreatWarningsAsErrors>", true)]
    [InlineData("<TreatWarningsAsErrors>false</TreatWarningsAsErrors>", true)]
    [InlineData("<TreatWarningsAsErrors></TreatWarningsAsErrors>", true)]
    [InlineData("<TreatWarningsAsErrors>$(Strict)</TreatWarningsAsErrors>", true)]
    public void GivenTreatWarningsAsErrorsElement_WhenItIsChecked_ThenOnlyAOnceDecodedTrueIsAccepted(string element, bool expectedProblem)
    {
        var document = XDocument.Parse($"<Project><PropertyGroup>{element}</PropertyGroup></Project>");

        Assert.Equal(expectedProblem, TreatWarningsAsErrorsProblems(document).Count == 1);
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

        Assert.Fail(
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
            overrides.AddRange(HostingOverrides(document).Select(o => $"{file}: {o}"));
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
            yarp.AddRange(PackageIds(document, "Yarp").Select(id => $"{file}: {id}"));
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
            var sites = FindConstructionSites(configuration.Files);

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
            var (badAssignments, optionsWithoutTrue, topologyTrueAssignments) = InspectDashboardAssignments(configuration.Files);

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

    [Fact]
    public void GivenSuppressionIsPresent_WhenTestSourceIsParsed_ThenDisableDashboardIsOnlyEverAssignedTrue()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        foreach (var configuration in TestConfigurations.Value)
        {
            var (badAssignments, _, _) = InspectDashboardAssignments(configuration.Files);

            Assert.True(
                badAssignments.Count == 0,
                $"The {AdvisoryId} suppression is justified by DisableDashboard only ever being set to the literal true, "
                + $"in test sources too. Found other assignments under tests in the {configuration.Name} build: "
                + $"[{string.Join("; ", badAssignments)}]. Restore the literal true, or re-assess the advisory path and get "
                + "a maintainer decision.");
        }
    }

    [Theory]
    [InlineData("class C { object M() { var o = new DistributedApplicationOptions(); o.DisableDashboard = true; return o; } }", 0)]
    [InlineData("class C { void M() { Assert.False(new DistributedApplicationOptions().DisableDashboard); } }", 0)]
    [InlineData("class C { object M() => new DistributedApplicationOptions { DisableDashboard = true }; }", 0)]
    [InlineData("class C { object M() => new DistributedApplicationOptions { DisableDashboard = false }; }", 1)]
    [InlineData("class C { void M(DistributedApplicationOptions o) { o.DisableDashboard = false; } }", 1)]
    [InlineData("class C { void M(DistributedApplicationOptions o) { o.DisableDashboard |= false; } }", 1)]
    public void GivenTestSource_WhenDashboardAssignmentsAreInspected_ThenOnlyAssignmentsOtherThanTrueAreBad(string code, int expectedBad)
    {
        var files = new[] { ("tests/Case.cs", CSharpSyntaxTree.ParseText(code).GetRoot()) }.Cast<(string File, SyntaxNode Root)>().ToList();

        var (badAssignments, _, _) = InspectDashboardAssignments(files);

        Assert.Equal(expectedBad, badAssignments.Count);
    }

    [Fact]
    public void GivenSuppressionIsPresent_WhenPackagesAreScanned_ThenNoAspireDashboardPackageIsReferenced()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        var dashboard = new List<string>();
        foreach (var (file, document) in MsBuildDocuments.Value)
        {
            dashboard.AddRange(PackageIds(document, "Aspire.Dashboard", prefix: true).Select(id => $"{file}: {id}"));
        }

        Assert.True(
            dashboard.Count == 0,
            $"The {AdvisoryId} suppression assumes no Aspire.Dashboard.* package is restored: Vouchfx.Dcp.targets skips the "
            + "SDK's default references so no dashboard package enters the restore graph. This check backs up NuGet audit. "
            + $"Found: [{string.Join("; ", dashboard)}]. "
            + "Remove the reference, or re-assess the advisory path and get a maintainer decision.");
    }

    [Fact]
    public void GivenSuppressionIsPresent_WhenProjectsAreScanned_ThenEveryAspireHostImportsTheDcpTargets()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        var hosts = new List<string>();
        var missing = new List<string>();
        foreach (var (file, document) in MsBuildDocuments.Value.Where(d => d.File.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (!IsAspireHost(document))
            {
                continue;
            }

            hosts.Add(file);
            var importsTargets = document
                .Descendants()
                .Where(e => NameIs(e, "Import"))
                .Where(e => !e.AncestorsAndSelf().Any(a => AttributeValue(a, "Condition") is not null))
                .Any(e => string.Equals(
                    DecodeEscapes(AttributeValue(e, "Project") ?? string.Empty).Trim().Split('/', '\\')[^1],
                    DcpTargetsName,
                    StringComparison.OrdinalIgnoreCase));
            if (!importsTargets)
            {
                missing.Add(file);
            }
        }

        _output.WriteLine("Aspire hosts: " + string.Join(", ", hosts));
        Assert.True(
            hosts.Contains(CliProjectRelativePath, StringComparer.Ordinal),
            $"{CliProjectRelativePath} was not found among the Aspire host projects [{string.Join(", ", hosts)}]. The search is "
            + "probably broken, or the CLI no longer hosts Aspire, so this guard would check nothing.");
        Assert.True(
            missing.Count == 0,
            $"Every project that uses {AppHostSdkName} or sets IsAspireHost must import {DcpTargetsName} unconditionally. "
            + "Without it the SDK's default references restore Aspire.Dashboard.Sdk, which the "
            + $"{AdvisoryId} suppression assumes is never restored. This check backs up NuGet audit. "
            + $"Missing in: [{string.Join("; ", missing)}].");
    }

    [Fact]
    public void GivenSuppressionIsPresent_WhenMsBuildItemsAreScanned_ThenNoUsingItemNamesAnAspireApplicationType()
    {
        if (!RootSuppressionIsPresent())
        {
            return;
        }

        var usings = new List<string>();
        foreach (var (file, document) in MsBuildDocuments.Value)
        {
            foreach (var element in document.Descendants().Where(e => NameIs(e, "Using")))
            {
                var include = DecodeEscapes(AttributeValue(element, "Include") ?? string.Empty);
                if (AspireTypeNames.Any(n => include.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    usings.Add($"{file}: <Using Include=\"{include}\">");
                }
            }
        }

        Assert.True(
            usings.Count == 0,
            "A <Using> item would import a DistributedApplication type into every file without a using directive this "
            + $"guard's syntax checks can see. Found: [{string.Join("; ", usings)}]. Remove it, or re-assess the advisory "
            + "path and get a maintainer decision.");
    }

    [Fact]
    public void GivenSourceWithAliasesAndStaticUsings_WhenConstructionSitesAreFound_ThenEveryFormIsRecognised()
    {
        var cases = new (string Name, string[] Sources, string Expected)[]
        {
            ("bare CreateBuilder after using static",
                new[] { "using static Aspire.Hosting.DistributedApplication;\nclass C { void M(string[] args) { var b = CreateBuilder(args); } }" },
                "DistributedApplication.CreateBuilder"),
            ("bare CreateSlimBuilder after using static",
                new[] { "using static Aspire.Hosting.DistributedApplication;\nclass C { void M(string[] args) { CreateSlimBuilder(args); } }" },
                "DistributedApplication.CreateSlimBuilder"),
            ("global using static in another file",
                new[] { "global using static Aspire.Hosting.DistributedApplication;", "class C { void M() { CreateBuilder(); } }" },
                "DistributedApplication.CreateBuilder"),
            ("using alias of DistributedApplication",
                new[] { "using X = Aspire.Hosting.DistributedApplication;\nclass C { void M(string[] a) { X.CreateBuilder(a); } }" },
                "DistributedApplication.CreateBuilder"),
            ("using alias of the options type",
                new[] { "using O = Aspire.Hosting.DistributedApplicationOptions;\nclass C { object M() => new O(); }" },
                "new DistributedApplicationOptions"),
            ("using alias of the options type, target-typed",
                new[] { "using O = Aspire.Hosting.DistributedApplicationOptions;\nclass C { O M() { return new(); } }" },
                "new DistributedApplicationOptions"),
            ("using alias of the builder type",
                new[] { "using B = Aspire.Hosting.DistributedApplicationBuilder;\nclass C { object M() => new B(null); }" },
                "new DistributedApplicationBuilder"),
            ("using alias of the testing builder",
                new[] { "using T = Aspire.Hosting.Testing.DistributedApplicationTestingBuilder;\nclass C { void M() { T.CreateAsync<object>(); } }" },
                "DistributedApplicationTestingBuilder"),
            ("alias names re-used for other types in a later file",
                new[]
                {
                    "using Y = Aspire.Hosting.DistributedApplicationBuilder;\nusing O = Aspire.Hosting.DistributedApplicationOptions;\nclass A { void M() { new Y(new O()).Build(); } }",
                    "using O = Aspire.Hosting.DistributedApplication;\nusing Y = Aspire.Hosting.DistributedApplication;\nclass Z { }",
                },
                "new DistributedApplicationBuilder"),
            ("options alias re-used for another type in a later file",
                new[]
                {
                    "using O = Aspire.Hosting.DistributedApplicationOptions;\nclass A { object M() => new O(); }",
                    "using O = Aspire.Hosting.DistributedApplication;\nclass Z { }",
                },
                "new DistributedApplicationOptions"),
            ("qualified call",
                new[] { "class C { void M() { Aspire.Hosting.DistributedApplication.CreateBuilder(); } }" },
                "DistributedApplication.CreateBuilder"),
            ("code under a legacy NETCOREAPP symbol",
                new[] { "#if NETCOREAPP2_1_OR_GREATER\nclass C { void M() { DistributedApplication.CreateBuilder(); } }\n#endif" },
                "DistributedApplication.CreateBuilder"),
        };

        foreach (var (name, sources, expected) in cases)
        {
            foreach (var (configuration, options) in ParseConfigurations())
            {
                var files = sources
                    .Select((code, index) => ($"case{index}.cs", CSharpSyntaxTree.ParseText(code, options).GetRoot()))
                    .Cast<(string File, SyntaxNode Root)>()
                    .ToList();
                var sites = FindConstructionSites(files).Select(s => s.Site).ToList();

                Assert.True(sites.Contains(expected, StringComparer.Ordinal), $"'{name}' was not recognised as '{expected}' in the {configuration} build. Sites: [{string.Join(", ", sites)}].");
            }
        }

        // Names that merely look alike are not construction sites.
        var unrelated = new[]
        {
            "using static System.Console;\nclass C { void M() { CreateBuilder(); } }",
            "class C { void M() { WebApplication.CreateBuilder(); Host.CreateDefaultBuilder(); } }",
        };
        foreach (var code in unrelated)
        {
            var files = new[] { ("unrelated.cs", CSharpSyntaxTree.ParseText(code).GetRoot()) }.Cast<(string File, SyntaxNode Root)>().ToList();
            Assert.Empty(FindConstructionSites(files));
        }
    }

    private static List<(string File, int Line, string Site)> FindConstructionSites(IReadOnlyList<(string File, SyntaxNode Root)> files)
    {
        var imports = ImportedNames.From(files.Select(f => f.Root));
        var sites = new List<(string File, int Line, string Site)>();
        foreach (var (file, root) in files)
        {
            foreach (var node in root.DescendantNodes())
            {
                var site = DescribeConstructionSite(node, imports);
                if (site is not null)
                {
                    sites.Add((file, LineOf(node), site));
                }
            }
        }

        return sites;
    }

    private static (List<string> BadAssignments, List<string> OptionsWithoutTrue, int TopologyTrueAssignments) InspectDashboardAssignments(
        IReadOnlyList<(string File, SyntaxNode Root)> files)
    {
        var imports = ImportedNames.From(files.Select(f => f.Root));
        var badAssignments = new List<string>();
        var optionsWithoutTrue = new List<string>();
        var topologyTrueAssignments = 0;

        foreach (var (file, root) in files)
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
                if (!IsOptionsCreation(creation, imports))
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

        return (badAssignments, optionsWithoutTrue, topologyTrueAssignments);
    }

    private static string? DescribeConstructionSite(SyntaxNode node, ImportedNames imports)
    {
        switch (node)
        {
            case ObjectCreationExpressionSyntax creation:
                return CreationSite(imports.Names(SimpleName(creation.Type)));
            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return CreationSite(imports.Names(ImplicitTargetTypeName(implicitCreation, imports)));
            case InvocationExpressionSyntax invocation when DescribeBuilderInvocation(invocation, imports) is { } description:
                return description;
            case IdentifierNameSyntax identifier when imports.Names(identifier.Identifier.ValueText).Contains("DistributedApplicationTestingBuilder"):
                return "DistributedApplicationTestingBuilder";
            default:
                return null;
        }
    }

    private static string? DescribeBuilderInvocation(InvocationExpressionSyntax invocation, ImportedNames imports)
    {
        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateBuilder" or "CreateSlimBuilder" } access
                when imports.Names(SimpleName(access.Expression)).Contains("DistributedApplication"):
                return "DistributedApplication." + access.Name.Identifier.ValueText;
            case IdentifierNameSyntax { Identifier.ValueText: "CreateBuilder" or "CreateSlimBuilder" } bare
                when imports.StaticApplication:
                return "DistributedApplication." + bare.Identifier.ValueText;
            default:
                return null;
        }
    }

    /// <summary>
    /// Names the type a target-typed <c>new()</c> creates, when the syntax states it: a local or
    /// field declaration, a property initialiser, a parameter default, a return from a member
    /// with a declared type, or an argument to <c>DistributedApplication.CreateBuilder</c>.
    /// </summary>
    private static string? ImplicitTargetTypeName(ImplicitObjectCreationExpressionSyntax creation, ImportedNames imports)
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
                when DescribeBuilderInvocation(invocation, imports) is not null:
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

    private static bool IsOptionsCreation(BaseObjectCreationExpressionSyntax creation, ImportedNames imports) =>
        creation switch
        {
            ObjectCreationExpressionSyntax typed => imports.Names(SimpleName(typed.Type)).Contains("DistributedApplicationOptions"),
            ImplicitObjectCreationExpressionSyntax implicitCreation =>
                imports.Names(ImplicitTargetTypeName(implicitCreation, imports)).Contains("DistributedApplicationOptions"),
            _ => false,
        };

    /// <summary>
    /// Decodes <c>%XX</c> escapes up to four times, which is stricter than MSBuild's single unescape:
    /// an escape can itself be escaped, and a stricter reading can only find more.
    /// </summary>
    internal static string DecodeEscapes(string text)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var decoded = DecodeOnce(text);
            if (string.Equals(decoded, text, StringComparison.Ordinal))
            {
                break;
            }

            text = decoded;
        }

        return text;
    }

    /// <summary>Decodes <c>%XX</c> escapes once, as MSBuild does when it reads a value.</summary>
    private static string DecodeOnce(string text) =>
        PercentEscape.Replace(text, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    private static Match FindAuditWarningCode(string text) => AuditWarningCode.Match(DecodeEscapes(text));

    private static Match FindAuditText(string text) => AuditTextPattern.Match(DecodeEscapes(text));

    private static Match FindWarningsAsErrorsOff(string text) => WarningsAsErrorsOffText.Match(DecodeOnce(text));

    /// <summary>Why an <c>&lt;Import Project&gt;</c> value would hide a file from this guard, or null when it would not.</summary>
    private static string? ImportProblem(string? project)
    {
        var target = DecodeEscapes(project ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            return "has no Project attribute";
        }

        var function = GetPathOfFileAbove.Match(target);
        if (function.Success)
        {
            target = function.Groups["file"].Value;
            if (function.Groups["start"].Success && SegmentProblem(function.Groups["start"].Value) is { } startProblem)
            {
                return "has a start directory with " + startProblem;
            }
        }

        if (target.Contains('*') || target.Contains('?'))
        {
            return "has a wildcard, which could reach a file this guard does not read";
        }

        if (!target.EndsWith(".props", StringComparison.OrdinalIgnoreCase) && !target.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
        {
            return "does not end in .props or .targets";
        }

        return SegmentProblem(target) is { } problem ? "has " + problem : null;
    }

    /// <summary>
    /// Why a path has a segment this guard's walk would not enter: a hidden segment (starting with a
    /// dot, other than <c>.</c> and <c>..</c>) or one named <c>bin</c>, <c>obj</c> or
    /// <c>node_modules</c>. Property expansions are removed from a segment first, so
    /// <c>$(MSBuildThisFileDirectory).cfg</c> is the hidden segment <c>.cfg</c>.
    /// </summary>
    private static string? SegmentProblem(string path)
    {
        foreach (var raw in path.Split('/', '\\'))
        {
            var segment = PropertyExpansion.Replace(raw, string.Empty).Trim();
            if (segment.StartsWith('.') && segment is not "." and not "..")
            {
                return $"the hidden path segment '{segment}'";
            }

            if (SkippedDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return $"the path segment '{segment}', a skipped directory name";
            }
        }

        return null;
    }

    /// <summary>True when the project uses the Aspire AppHost SDK, in any of its spellings, or sets <c>IsAspireHost</c>.</summary>
    private static bool IsAspireHost(XDocument document)
    {
        var sdkNames = document.Descendants()
            .Where(e => NameIs(e, "Sdk") || NameIs(e, "Import"))
            .Select(e => AttributeValue(e, NameIs(e, "Sdk") ? "Name" : "Sdk"))
            .Concat((document.Root is { } project ? AttributeValue(project, "Sdk") : null)?.Split(';') ?? Array.Empty<string>())
            .Select(sdk => (sdk ?? string.Empty).Trim().Split('/')[0]);
        var setsHostFlag = document.Descendants()
            .Where(e => NameIs(e, "IsAspireHost"))
            .Any(e => !string.Equals(DecodeOnce(e.Value).Trim(), "false", StringComparison.OrdinalIgnoreCase));

        return setsHostFlag || sdkNames.Any(n => string.Equals(n, AppHostSdkName, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] TryReadAllLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Every file this guard reads as text: response files under the root, then the directories of
    /// <see cref="TextScans"/>: workflows (YAML), composite actions, <c>ci</c> and <c>installer</c>
    /// (YAML, <c>.sh</c>, <c>.ps1</c>), and <c>scripts</c> and <c>examples</c> (<c>.sh</c>, <c>.ps1</c>).
    /// </summary>
    private static List<string> TextScanFiles(string root)
    {
        var files = EnumerateFiles(root, p => p.EndsWith(".rsp", StringComparison.OrdinalIgnoreCase));
        foreach (var (directory, extensions) in TextScans)
        {
            files.AddRange(EnumerateFiles(Path.Combine(root, directory), p => extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))));
        }

        return files.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The package element ids of a document that contain, or start with, <paramref name="needle"/>.</summary>
    private static List<string> PackageIds(XDocument document, string needle, bool prefix = false) =>
        document.Descendants()
            .Where(IsPackageElement)
            .Select(PackageId)
            .Where(id => prefix
                ? id.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                : id.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>The <c>VersionOverride</c> of every <c>PackageReference</c> to an Aspire hosting package, as <c>id VersionOverride=value</c>.</summary>
    private static List<string> HostingOverrides(XDocument document) =>
        document.Descendants()
            .Where(e => NameIs(e, "PackageReference"))
            .Select(e => (Id: PackageId(e), Override: Metadata(e, "VersionOverride")))
            .Where(p => p.Override is not null && HostingPackageIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
            .Select(p => $"{p.Id} VersionOverride={p.Override}")
            .ToList();

    /// <summary>Every <c>TreatWarningsAsErrors</c> property whose value, decoded once, is not <c>true</c>.</summary>
    private static List<string> TreatWarningsAsErrorsProblems(XDocument document) =>
        document.Descendants()
            .Where(e => NameIs(e, "TreatWarningsAsErrors"))
            .Select(e => DecodeOnce(e.Value).Trim())
            .Where(value => !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            .Select(value => $"<TreatWarningsAsErrors> is '{value}'")
            .ToList();

    /// <summary>The using aliases and static usings of a set of parsed files that name an Aspire application type.</summary>
    private sealed record ImportedNames(IReadOnlyDictionary<string, SortedSet<string>> Aliases, bool StaticApplication)
    {
        public static ImportedNames From(IEnumerable<SyntaxNode> roots)
        {
            var aliases = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var staticApplication = false;
            foreach (var directive in roots.SelectMany(r => r.DescendantNodes().OfType<UsingDirectiveSyntax>()))
            {
                var name = SimpleName(directive.NamespaceOrType);
                if (name is null || !AspireTypeNames.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                if (directive.Alias is { } alias)
                {
                    var aliasName = alias.Name.Identifier.ValueText;
                    if (!aliases.TryGetValue(aliasName, out var targets))
                    {
                        aliases[aliasName] = targets = new SortedSet<string>(StringComparer.Ordinal);
                    }

                    targets.Add(name);
                }
                else if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) && name == "DistributedApplication")
                {
                    staticApplication = true;
                }
            }

            return new ImportedNames(aliases, staticApplication);
        }

        /// <summary>
        /// Every Aspire type name <paramref name="name"/> can stand for: the name itself, plus each type
        /// any parsed file aliases it to. One alias name can be declared differently in different files.
        /// </summary>
        public IReadOnlyCollection<string> Names(string? name)
        {
            if (name is null)
            {
                return Array.Empty<string>();
            }

            return Aliases.TryGetValue(name, out var targets) ? targets.Append(name).ToList() : new[] { name };
        }
    }

    /// <summary>The construction site for the first of <paramref name="names"/> that is an Aspire builder or options type.</summary>
    private static string? CreationSite(IReadOnlyCollection<string> names) =>
        names.Order(StringComparer.Ordinal).FirstOrDefault(n => n is "DistributedApplicationOptions" or "DistributedApplicationBuilder") is { } type
            ? "new " + type
            : null;

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

    internal static bool NameIs(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    internal static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>An item's metadata, written as an attribute or as a child element, or null when it is absent.</summary>
    internal static string? Metadata(XElement element, string name) =>
        AttributeValue(element, name) ?? element.Elements().FirstOrDefault(c => NameIs(c, name))?.Value;

    /// <summary>The package id an item names in <c>Include</c> or <c>Update</c>, with <c>%XX</c> escapes decoded and trimmed.</summary>
    internal static string PackageId(XElement element) =>
        DecodeEscapes(AttributeValue(element, "Include") ?? AttributeValue(element, "Update") ?? string.Empty).Trim();

    /// <summary>Every MSBuild document the guard reads, as the repository-relative file and its XML.</summary>
    internal static IReadOnlyList<(string File, XDocument Document)> LoadedMsBuildDocuments() => MsBuildDocuments.Value;

    /// <summary>The package element names that declare or reference a package.</summary>
    internal static bool IsPackageElement(XElement element) =>
        PackageElements.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase);

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
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted between the walk and the read, as another local run may do: no longer part of the tree.
            }
        }

        return documents;
    }

    private static List<ParsedConfiguration> ParseSourceFiles() => ParseCSharp("src", filter: null);

    /// <summary>
    /// Parses test sources for the <c>DisableDashboard</c> check. Only files that name
    /// <c>DisableDashboard</c> or <c>DistributedApplicationOptions</c> are parsed, which keeps the walk fast.
    /// </summary>
    private static List<ParsedConfiguration> ParseTestFiles() =>
        ParseCSharp(
            "tests",
            text => text.Contains("DisableDashboard", StringComparison.Ordinal) || text.Contains("DistributedApplicationOptions", StringComparison.Ordinal));

    private static List<ParsedConfiguration> ParseCSharp(string directory, Func<string, bool>? filter)
    {
        var root = RepoRoot.Resolve();
        var paths = EnumerateFiles(Path.Combine(root, directory), p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var texts = new List<(string File, string Path, string Text)>();
        foreach (var path in paths)
        {
            try
            {
                var text = File.ReadAllText(path);
                if (filter is null || filter(text))
                {
                    texts.Add((Relative(root, path), path, text));
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted between the walk and the read: no longer part of the tree.
            }
        }

        // Each build's own symbols: code that #if leaves out of a build is skipped trivia in that
        // build's tree, so it never counts there, and every check runs against both trees.
        return ParseConfigurations()
            .Select(c => new ParsedConfiguration(
                c.Name,
                texts.Select(t => (t.File, CSharpSyntaxTree.ParseText(t.Text, c.Options, path: t.Path).GetRoot())).ToList()))
            .ToList();
    }

    private static IEnumerable<(string Name, CSharpParseOptions Options)> ParseConfigurations()
    {
        foreach (var (name, buildSymbol) in new[] { ("Release", "RELEASE"), ("Debug", "DEBUG") })
        {
            foreach (var (framework, frameworkSymbols) in FrameworkPreprocessorSymbols)
            {
                yield return (
                    $"{name} {framework}",
                    CSharpParseOptions.Default.WithPreprocessorSymbols(SharedPreprocessorSymbols.Concat(frameworkSymbols).Append(buildSymbol)));
            }
        }
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

    internal static List<string> EnumerateFiles(string directory, Func<string, bool> include)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        var files = new List<string>();
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(current).Where(include));
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if (!IsSkippedDirectory(Path.GetFileName(child)))
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // Removed mid-walk, as a parallel local run does to a build directory: no longer part of the tree.
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
