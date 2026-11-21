using System.Text.RegularExpressions;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Fails when a project file with a <c>PackageReference</c> sits outside <c>vouchfx.sln</c> and
/// the <c>vuln-scan</c> job in the build workflow does not name it.
/// </summary>
/// <remarks>
/// The job's solution scan cannot see such a project, so an advisory against its packages would
/// not surface. The check is textual: it reads the solution and the workflow as text and every
/// <c>*.csproj</c>, <c>*.fsproj</c> and <c>*.vbproj</c> under the repository root, skipping
/// hidden directories, <c>bin</c>, <c>obj</c> and <c>node_modules</c>.
/// </remarks>
public sealed class OutOfSolutionProjectAuditGuardTests
{
    private static readonly string[] ProjectPatterns = { "*.csproj", "*.fsproj", "*.vbproj" };

    [Fact]
    public void GivenProjectOutsideSolutionWithPackageReference_WhenVulnScanIsRead_ThenItIsNamedThere()
    {
        var root = RepoRoot.Resolve();
        var solution = File.ReadAllText(Path.Combine(root, "vouchfx.sln")).Replace('\\', '/');
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        var job = Regex.Match(workflow, @"^  vuln-scan:\r?\n(?:(?!^  [A-Za-z0-9_-]+:\s*$).*\r?\n?)*", RegexOptions.Multiline);
        Assert.True(job.Success, "The vuln-scan job was not found in build.yml.");

        var missing = new List<string>();
        foreach (var path in ProjectPatterns.SelectMany(p => Directory.EnumerateFiles(root, p, SearchOption.AllDirectories)))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Split('/').Any(s => s.StartsWith('.') || s is "bin" or "obj" or "node_modules")
                || solution.Contains(relative, StringComparison.OrdinalIgnoreCase)
                || !File.ReadAllText(path).Contains("<PackageReference", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!job.Value.Contains(relative, StringComparison.Ordinal))
            {
                missing.Add(relative);
            }
        }

        Assert.True(
            missing.Count == 0,
            "These projects have package references, are not in vouchfx.sln and are not named in the vuln-scan job "
            + $"of .github/workflows/build.yml, so their advisories would not surface: [{string.Join("; ", missing)}]. "
            + "Name each one in that job's OUT_OF_SOLUTION_PROJECTS list, and add its directory to the nuget entry "
            + "in .github/dependabot.yml if it sets a package version itself.");
    }
}
