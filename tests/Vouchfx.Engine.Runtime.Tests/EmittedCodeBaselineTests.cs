// EmittedCodeBaselineTests - the emitted-code baseline gate.
//
// WHAT IT PINS. For every `.e2e.yaml` file in the repository (examples, test fixtures, corpora), the C#
// the engine emits for it, or - for a suite the engine refuses - the stage and message of the refusal.
// One golden per suite lives under Golden/EmittedCode/, mirroring the suite's repository-relative path
// with a `.txt` suffix. Each golden records, per step, the fragment the provider returned (required
// usings, helper class names with SHA-256, statement block) and then the full assembled script, so a
// difference is attributable to one step and one provider. See EmittedCodeBaseline for the format.
//
// WHY IT EXISTS. Changing how a provider reads its YAML (the provider binder input type) must not change
// a single byte of generated code. This gate is the proof: it runs the real front end over every suite
// in the repository and compares the result with a committed record.
//
// NOTHING IS RUN. The gate stops at emitted text: no topology, container or network, and the script is
// neither compiled, loaded nor executed. A suite with a `<name>.setup.(sh|ps1)` script runs it first,
// exactly as ExamplesCompileTests does, because its declared files must exist for the pipeline to
// accept it (the same prerequisite, the same git-ignored output).
//
// REGENERATION (deliberate, local, never in CI - GoldenRegeneration refuses it when CI is set):
//   VOUCHFX_REGEN_EMITTED_CODE=1 dotnet test tests/Vouchfx.Engine.Runtime.Tests \
//     --filter "FullyQualifiedName~EmittedCodeBaselineTests"
//   Rewrites every golden and deletes goldens whose suite no longer exists. Review the diff, then commit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Gate: the emitted code (or refusal) of every repository suite equals its committed golden.
/// </summary>
public sealed class EmittedCodeBaselineTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_EMITTED_CODE";

    private const string GoldenSuffix = ".txt";

    private const string RegenHint =
        "To accept a deliberate change run: VOUCHFX_REGEN_EMITTED_CODE=1 dotnet test "
        + "tests/Vouchfx.Engine.Runtime.Tests --filter \"FullyQualifiedName~EmittedCodeBaselineTests\", "
        + "then review and commit the golden diff. A change you did not intend to make to emitted code "
        + "is a defect, not a golden update.";

    /// <summary>
    /// Suites deliberately left out of the baseline, each with the reason. A suite listed here has no
    /// golden, and the gate fails if the entry outlives its suite or if a golden is left behind for it.
    /// </summary>
    private static readonly Dictionary<string, string> s_exclusions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string GoldenRoot =>
        Path.Combine(RepoRoot.Resolve(), "tests", "Vouchfx.Engine.Runtime.Tests", "Golden", "EmittedCode");

    /// <summary>
    /// Every suite the gate covers: the discovered suites minus the listed exclusions, as
    /// repository-relative forward-slash paths in ordinal order.
    /// </summary>
    public static IEnumerable<object[]> Suites() =>
        DiscoverSuites().Where(path => !s_exclusions.ContainsKey(path)).Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(Suites))]
    public void EmittedCode_MatchesGolden(string suite)
    {
        var absolute = Path.Combine(RepoRoot.Resolve(), suite.Replace('/', Path.DirectorySeparatorChar));
        ExamplesCompileTests.EnsureFixtureMaterial(absolute);

        var actual = EmittedCodeBaseline.Render(suite, absolute, ExamplesCompileTests.CoreRegistry);
        var goldenPath = GoldenPathFor(suite);

        if (IsRegenRequested())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, actual, s_utf8NoBom);
            return;
        }

        Assert.True(
            File.Exists(goldenPath),
            $"{suite}: no emitted-code golden at '{Path.GetRelativePath(RepoRoot.Resolve(), goldenPath)}', and the "
            + $"suite is not listed as an exclusion. {RegenHint}");

        var expected = File.ReadAllText(goldenPath, s_utf8NoBom);
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        Assert.Fail(
            $"{suite}: the emitted code changed.{Environment.NewLine}{Describe(expected, actual)}{Environment.NewLine}{RegenHint}");
    }

    [Fact]
    public void Goldens_HaveNoOrphans()
    {
        var suites = new HashSet<string>(
            DiscoverSuites().Where(path => !s_exclusions.ContainsKey(path)), StringComparer.Ordinal);
        var root = GoldenRoot;
        var orphans = new List<string>();
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var suite = relative.EndsWith(GoldenSuffix, StringComparison.Ordinal)
                    ? relative[..^GoldenSuffix.Length]
                    : relative;
                if (!suites.Contains(suite))
                {
                    orphans.Add(file);
                }
            }
        }

        if (orphans.Count > 0 && IsRegenRequested())
        {
            foreach (var orphan in orphans)
            {
                File.Delete(orphan);
            }

            return;
        }

        Assert.True(
            orphans.Count == 0,
            "Emitted-code goldens exist for suites that no longer exist (or are now excluded): "
            + string.Join(", ", orphans.Select(o => Path.GetRelativePath(RepoRoot.Resolve(), o).Replace('\\', '/')))
            + $". {RegenHint}");
    }

    [Fact]
    public void Exclusions_AreCurrentAndJustified()
    {
        var discovered = new HashSet<string>(DiscoverSuites(), StringComparer.Ordinal);
        foreach (var (suite, reason) in s_exclusions)
        {
            Assert.True(discovered.Contains(suite), $"The exclusion '{suite}' names a suite that no longer exists; remove it.");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"The exclusion '{suite}' has no reason.");
        }
    }

    [Fact]
    public void Discovery_FindsSuites_AndTheRegistryIsTheFullCoreSet()
    {
        Assert.NotEmpty(DiscoverSuites());
        Assert.True(
            ExamplesCompileTests.CoreRegistry.All.Count >= 25,
            "The baseline must run over every Core provider; the registry holds fewer than 25.");
    }

    /// <summary>
    /// Finds every <c>*.e2e.yaml</c> under the repository root, skipping build output, dependency
    /// folders and dot-directories (<c>.git</c> and tool state). Reads the file system only; it does not
    /// ask git, so an untracked suite fails the gate until it has a golden, which is the safe direction.
    /// </summary>
    internal static IReadOnlyList<string> DiscoverSuites()
    {
        var root = RepoRoot.Resolve();
        var found = new List<string>();
        Walk(root, root, found);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void Walk(string root, string directory, List<string> found)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.e2e.yaml"))
        {
            found.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name is "bin" or "obj" or "node_modules" || name.StartsWith('.'))
            {
                continue;
            }

            Walk(root, child, found);
        }
    }

    private static string GoldenPathFor(string suite) =>
        Path.Combine(GoldenRoot, suite.Replace('/', Path.DirectorySeparatorChar) + GoldenSuffix);

    /// <summary>
    /// Names the sections that differ, and for a step section the step id and provider, so a failure
    /// says which step of which suite changed rather than only that something did.
    /// </summary>
    private static string Describe(string expected, string actual)
    {
        var before = Sections(expected);
        var after = Sections(actual);
        var report = new StringBuilder();
        var shown = 0;

        var headers = before.Select(s => s.Header).Union(after.Select(s => s.Header)).ToList();
        foreach (var header in headers)
        {
            var was = before.FirstOrDefault(s => s.Header == header);
            var now = after.FirstOrDefault(s => s.Header == header);
            if (was is not null && now is not null && was.Body == now.Body)
            {
                continue;
            }

            if (++shown > 10)
            {
                report.Append("  ... further differences omitted.\n");
                break;
            }

            var identity = (now ?? was)!.Identity;
            report.Append("  section '").Append(header).Append('\'');
            if (identity.Length > 0)
            {
                report.Append(" (").Append(identity).Append(')');
            }

            if (was is null)
            {
                report.Append(" is new.\n");
            }
            else if (now is null)
            {
                report.Append(" is gone.\n");
            }
            else
            {
                var oldLines = was.Body.Split('\n');
                var newLines = now.Body.Split('\n');
                var at = 0;
                while (at < oldLines.Length && at < newLines.Length && oldLines[at] == newLines[at])
                {
                    at++;
                }

                report.Append(" differs at its line ").Append(at + 1).Append(":\n");
                report.Append("    golden: ").Append(at < oldLines.Length ? oldLines[at] : "(end)").Append('\n');
                report.Append("    actual: ").Append(at < newLines.Length ? newLines[at] : "(end)").Append('\n');
            }
        }

        return report.ToString().TrimEnd('\n');
    }

    private static List<Section> Sections(string text)
    {
        var sections = new List<Section>();
        var identity = string.Empty;
        Section? current = null;
        var body = new StringBuilder();
        var stepLines = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith(EmittedCodeBaseline.Marker, StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections.Add(Close(current, body.ToString(), identity));
                }

                body.Clear();
                var header = line[EmittedCodeBaseline.Marker.Length..];
                if (!header.StartsWith("step ", StringComparison.Ordinal)
                    || header.Contains(" of ", StringComparison.Ordinal))
                {
                    stepLines.Clear();
                    identity = string.Empty;
                }

                current = new Section(header, identity, string.Empty);
                continue;
            }

            if (current is not null)
            {
                body.Append(line).Append('\n');
                if (current.Header.Contains(" of ", StringComparison.Ordinal)
                    && (line.StartsWith("id: ", StringComparison.Ordinal) || line.StartsWith("provider: ", StringComparison.Ordinal)))
                {
                    stepLines.Add(line);
                    identity = string.Join(", ", stepLines);
                }
            }
        }

        if (current is not null)
        {
            sections.Add(Close(current, body.ToString(), identity));
        }

        return sections;
    }

    private static Section Close(Section section, string body, string identity) =>
        section with
        {
            Body = body,
            Identity = section.Header.Contains(" of ", StringComparison.Ordinal) ? identity : section.Identity,
        };

    private sealed record Section(string Header, string Identity, string Body);

    private static bool IsRegenRequested()
    {
        return Vouchfx.TestSupport.GoldenRegeneration.IsRequested(RegenEnvVar);
    }
}
