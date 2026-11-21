using System.Text.RegularExpressions;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Fails when the <c>OUT_OF_SOLUTION_PROJECTS</c> list in the <c>vuln-scan</c> job of the build
/// workflow and the projects outside <c>vouchfx.sln</c> that have package references drift apart.
/// </summary>
/// <remarks>
/// The job's solution scan cannot see such a project, so an advisory against its packages would
/// not surface. The check is textual: it reads the solution's <c>Project(...)</c> entries (solution
/// folders excluded), the lines of the <c>OUT_OF_SOLUTION_PROJECTS: &gt;-</c> block (a mention in a
/// YAML comment elsewhere does not count), and every <c>*.csproj</c>, <c>*.fsproj</c>,
/// <c>*.vbproj</c> and <c>*.proj</c> under the repository root, skipping hidden directories,
/// <c>bin</c>, <c>obj</c> and <c>node_modules</c>. A project has package references when its text
/// contains <c>&lt;PackageReference</c>, or imports a file under its own directory that does.
/// The two sets must be equal.
/// <para>
/// Deliberately not covered, so the limit is stated rather than silent: packages that arrive
/// through an ancestor <c>Directory.Build.props</c> or <c>Directory.Build.targets</c>, through
/// <c>GlobalPackageReference</c>, or through an import that is not a literal path (properties,
/// wildcards) or sits more than one level away; MSBuild is not evaluated. Project types other than
/// the four above, and files inside hidden directories, are not enumerated.
/// </para>
/// </remarks>
public sealed class OutOfSolutionProjectAuditGuardTests
{
    private const string SolutionFolderTypeGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    private static readonly string[] ProjectExtensions = { ".csproj", ".fsproj", ".vbproj", ".proj" };
    private static readonly string[] SkippedDirectoryNames = { "bin", "obj", "node_modules" };

    [Fact]
    public void GivenProjectsOutsideSolutionWithPackageReferences_WhenVulnScanListIsRead_ThenTheListMatchesThem()
    {
        var root = RepoRoot.Resolve();
        var solution = File.ReadAllText(Path.Combine(root, "vouchfx.sln"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));

        var inSolution = ReadSolutionProjects(solution);
        var computed = EnumerateProjects(root)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(relative => !inSolution.Contains(relative))
            .Where(relative => HasPackageReferences(Path.Combine(root, relative)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listed = ReadOutOfSolutionList(workflow).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = computed.Except(listed, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var surplus = listed.Except(computed, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0 && surplus.Count == 0,
            "The OUT_OF_SOLUTION_PROJECTS list in the vuln-scan job of .github/workflows/build.yml does not match the "
            + "projects that have package references and are not in vouchfx.sln. "
            + $"Missing from the workflow: [{string.Join("; ", missing)}]. "
            + $"Listed but not existing, in vouchfx.sln, or without package references: [{string.Join("; ", surplus)}]. "
            + "Name each missing project in that list, and add its directory to the nuget entry in "
            + ".github/dependabot.yml if it pins its own package versions.");
    }

    private static HashSet<string> ReadSolutionProjects(string solution)
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = Regex.Matches(
            solution,
            "^Project\\(\"\\{(?<type>[^}]+)\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"(?<path>[^\"]+)\"",
            RegexOptions.Multiline);
        foreach (Match entry in entries)
        {
            if (!string.Equals(entry.Groups["type"].Value, SolutionFolderTypeGuid, StringComparison.OrdinalIgnoreCase))
            {
                projects.Add(entry.Groups["path"].Value.Replace('\\', '/'));
            }
        }

        return projects;
    }

    private static List<string> ReadOutOfSolutionList(string workflow)
    {
        var job = Regex.Match(workflow, @"^  vuln-scan:\r?\n(?:(?!^  [A-Za-z0-9_-]+:\s*$).*\r?\n?)*", RegexOptions.Multiline);
        Assert.True(job.Success, "The vuln-scan job was not found in build.yml.");

        var lines = job.Value.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*OUT_OF_SOLUTION_PROJECTS:\s*>-\s*$"));
        Assert.True(start >= 0, "The vuln-scan job has no 'OUT_OF_SOLUTION_PROJECTS: >-' block.");

        var keyIndent = lines[start].Length - lines[start].TrimStart().Length;
        var entries = new List<string>();
        foreach (var line in lines.Skip(start + 1))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (line.Length - line.TrimStart().Length <= keyIndent)
            {
                break;
            }

            entries.Add(line.Trim());
        }

        return entries;
    }

    private static bool HasPackageReferences(string projectPath)
    {
        var text = File.ReadAllText(projectPath);
        if (text.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(projectPath)!;
        foreach (Match import in Regex.Matches(text, "<Import\\s[^>]*?Project\\s*=\\s*\"(?<file>[^\"$*]+)\"", RegexOptions.IgnoreCase))
        {
            var imported = Path.GetFullPath(Path.Combine(directory, import.Groups["file"].Value.Replace('\\', '/')));
            if (imported.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && File.Exists(imported)
                && File.ReadAllText(imported).Contains("<PackageReference", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSkippedDirectory(string name) =>
        name.StartsWith('.') || SkippedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static List<string> EnumerateProjects(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var files = new List<string>();
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                files.AddRange(Directory.EnumerateFiles(current)
                    .Where(f => ProjectExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)));
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if (!IsSkippedDirectory(Path.GetFileName(child)))
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // An inaccessible or vanished directory is skipped rather than failing the guard.
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }
}
