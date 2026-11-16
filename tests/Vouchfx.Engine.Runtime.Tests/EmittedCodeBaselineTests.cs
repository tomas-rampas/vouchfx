// EmittedCodeBaselineTests - the emitted-code baseline gate.
//
// WHAT IT PINS. For every `.e2e.yaml` file in the repository (examples, test fixtures, corpora, editor
// samples, and the binder-coverage corpus under EmittedCodeCorpus/), what each step binds to and the C#
// the engine emits for it, or - for a suite the engine refuses - the stage and numbered diagnostics of
// the refusal and, when the suite reached the AST, what each step binds to all the same. One golden per
// suite lives under Golden/EmittedCode/, mirroring the suite's repository-relative path with a `.txt`
// suffix. Each golden records, per step, the bound model as JSON and the host-resource requirements (or
// the bind failure), then, when the suite emits, the fragment the provider returned (required usings,
// helper class names with SHA-256, statement block), and finally the full assembled script, so a
// difference is attributable to one section of one step and one provider. See EmittedCodeBaseline for
// the format and EmittedCodeBaselineModels for how a model is written.
//
// WHY IT EXISTS. Changing how a provider reads its YAML (the provider binder input type) must not change
// a single byte of what a binder produces or of generated code. This gate is the proof: it runs the
// real front end over every suite in the repository and compares the result with a committed record.
// The EmittedCodeCorpus/ suites exist only for this gate: between them they set every step field of
// the 25 Core providers and the YAML forms a binder input type must reproduce (quoted and plain typed
// scalars, null tokens, block scalars, flow collections, anchors, aliases and merge keys).
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
using System.Text.RegularExpressions;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Gate: the bound models and emitted code (or refusal) of every repository suite equal its committed golden.
/// </summary>
public sealed class EmittedCodeBaselineTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_EMITTED_CODE";

    private const string GoldenSuffix = ".txt";

    private const string RegenHint =
        "To accept a deliberate change run: VOUCHFX_REGEN_EMITTED_CODE=1 dotnet test "
        + "tests/Vouchfx.Engine.Runtime.Tests --filter \"FullyQualifiedName~EmittedCodeBaselineTests\", "
        + "then review and commit the golden diff. A change you did not intend to make to a bound model or "
        + "to emitted code is a defect, not a golden update.";

    /// <summary>
    /// Suites deliberately left out of the baseline, each with the reason. A suite listed here has no
    /// golden, and the gate fails if the entry outlives its suite or if a golden is left behind for it.
    /// </summary>
    private static readonly Dictionary<string, string> s_exclusions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A step section header: "step N of M" (the step's header) or "step N &lt;part&gt;".</summary>
    private static readonly Regex s_stepHeader = new(
        @"^step (\d+) (?:of (\d+)$)?",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Directory names discovery never enters: build output, package caches and the state folders of
    /// tools and editors. Each is named exactly; a dot-directory not listed here (for example
    /// <c>.github</c>) is searched, because a tracked suite could live there.
    /// </summary>
    private static readonly HashSet<string> s_skippedDirectories = new(StringComparer.Ordinal)
    {
        "bin", "obj", "node_modules", "TestResults",
        ".git", ".vs", ".idea", ".vscode-test", ".dotnet", ".nuget", ".venv", ".cache",
        ".pytest_cache", ".mypy_cache", ".ruff_cache", ".serena", ".code-review-graph",
    };

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
            $"{suite}: what the suite binds to or compiles to changed.{Environment.NewLine}{Describe(expected, actual)}{Environment.NewLine}{RegenHint}");
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
    public void Describe_NamesTheSectionForEveryKindOfDifference()
    {
        const string golden =
            "vouchfx emitted-code baseline v2\nsuite: s.e2e.yaml\noutcome: refused\nstage: schema\n"
            + "@@@@ diagnostic 1 of 2\n/a\tfirst\n@@@@ diagnostic 2 of 2\n/b\tsecond\n@@@@ end\n";

        var secondDiagnostic = Describe(golden, golden.Replace("second", "changed", StringComparison.Ordinal));
        Assert.Contains("section 'diagnostic 2 of 2' differs", secondDiagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic 1 of 2", secondDiagnostic, StringComparison.Ordinal);

        var preamble = Describe(golden, golden.Replace("stage: schema", "stage: parse", StringComparison.Ordinal));
        Assert.Contains("section 'preamble' differs at its line 4", preamble, StringComparison.Ordinal);

        const string steps =
            "v\n@@@@ step 1 of 1\nid: a\ntype: t\nprovider: P\n@@@@ step 1 model\n{\n  \"X\": 1\n}\n@@@@ end\n";
        var model = Describe(steps, steps.Replace("\"X\": 1", "\"X\": 2", StringComparison.Ordinal));
        Assert.Contains("section 'step 1 model' (id: a, provider: P) differs", model, StringComparison.Ordinal);
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
    /// Finds every <c>*.e2e.yaml</c> under the repository root. It skips the directories named in
    /// <see cref="s_skippedDirectories"/>, any directory holding its own <c>.git</c> entry (a nested
    /// checkout or worktree, such as an agent worktree under <c>.claude/</c>, is another copy of the
    /// repository rather than part of it) and symbolic links to directories (git records the link,
    /// not what it points at). Other dot-directories, <c>.github</c> among them, are searched. Reads the
    /// file system only; it does not ask git, so an untracked suite fails the gate until it has a
    /// golden, which is the safe direction.
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
            if (s_skippedDirectories.Contains(Path.GetFileName(child))
                || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0
                || File.Exists(Path.Combine(child, ".git"))
                || Directory.Exists(Path.Combine(child, ".git")))
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
    /// says which step of which suite changed rather than only that something did. Every golden line
    /// belongs to exactly one section (the lines before the first marker form the 'preamble'), and
    /// section headers are unique within a golden, so any difference names at least one section.
    /// </summary>
    internal static string Describe(string expected, string actual)
    {
        var before = Sections(expected);
        var after = Sections(actual);
        var report = new StringBuilder();
        var shown = 0;

        var headers = before.Keys.Union(after.Keys).ToList();
        foreach (var header in headers)
        {
            before.TryGetValue(header, out var was);
            after.TryGetValue(header, out var now);
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

        if (shown == 0)
        {
            // Unreachable while every line belongs to a uniquely named section; kept so a golden that
            // breaks that rule still produces a description instead of an empty one.
            var oldLines = expected.Split('\n');
            var newLines = actual.Split('\n');
            var at = 0;
            while (at < oldLines.Length && at < newLines.Length && oldLines[at] == newLines[at])
            {
                at++;
            }

            report.Append("  the goldens differ at line ").Append(at + 1).Append(" but no section could be named:\n");
            report.Append("    golden: ").Append(at < oldLines.Length ? oldLines[at] : "(end)").Append('\n');
            report.Append("    actual: ").Append(at < newLines.Length ? newLines[at] : "(end)").Append('\n');
        }

        return report.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Splits a golden into its sections, keyed by header in document order. The lines before the
    /// first marker form the 'preamble' section. A repeated header (which the renderer refuses to
    /// write) is kept apart by an occurrence suffix, so it can never hide a difference.
    /// </summary>
    private static Dictionary<string, Section> Sections(string text)
    {
        var raw = new List<(string Header, string Body)>();
        var header = "preamble";
        var body = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith(EmittedCodeBaseline.Marker, StringComparison.Ordinal))
            {
                raw.Add((header, body.ToString()));
                body.Clear();
                header = line[EmittedCodeBaseline.Marker.Length..];
                continue;
            }

            body.Append(line).Append('\n');
        }

        raw.Add((header, body.ToString()));

        // A step's identity (its id and provider) comes from its "step N of M" header section and is
        // attached to every "step N ..." section.
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (sectionHeader, sectionBody) in raw)
        {
            var match = s_stepHeader.Match(sectionHeader);
            if (match.Success && match.Groups[2].Success)
            {
                identities[match.Groups[1].Value] = string.Join(
                    ", ",
                    sectionBody.Split('\n').Where(l =>
                        l.StartsWith("id: ", StringComparison.Ordinal) || l.StartsWith("provider: ", StringComparison.Ordinal)));
            }
        }

        var sections = new Dictionary<string, Section>(StringComparer.Ordinal);
        foreach (var (sectionHeader, sectionBody) in raw)
        {
            var key = sectionHeader;
            for (var occurrence = 2; sections.ContainsKey(key); occurrence++)
            {
                key = sectionHeader + " (occurrence " + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            }

            var match = s_stepHeader.Match(sectionHeader);
            var identity = match.Success && identities.TryGetValue(match.Groups[1].Value, out var found) ? found : string.Empty;
            sections.Add(key, new Section(key, identity, sectionBody));
        }

        return sections;
    }

    private sealed record Section(string Header, string Identity, string Body);

    private static bool IsRegenRequested()
    {
        return Vouchfx.TestSupport.GoldenRegeneration.IsRequested(RegenEnvVar);
    }
}
