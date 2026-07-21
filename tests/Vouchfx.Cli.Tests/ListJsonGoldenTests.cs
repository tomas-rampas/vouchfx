// Vouchfx.Cli.Tests — `list --json` wire-shape freeze (#260).
//
// The `list --json` document is a FROZEN contract, exactly like `validate --json` (see
// ValidateJsonGoldenTests' header for the full rationale). This test serialises a FIXED,
// hand-built ListJsonDocument (never the live registry / a real engineVersion) through the
// SAME CliJsonContract.Options ListCommand uses, and asserts identity against
// Golden/list-json-document.v1.json — compared with line endings and any trailing newline
// normalised, so the check is stable across Windows and Git checkouts.
//
// REGENERATION:
//   VOUCHFX_REGEN_LIST_JSON=1 dotnet test tests/Vouchfx.Cli.Tests \
//     --filter "FullyQualifiedName~ListJsonGoldenTests"
//   Rewrites Golden/list-json-document.v1.json from the freshly-serialised fixture.
//   Review the diff, then commit.

using System.Text.Json;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ListJsonGoldenTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_LIST_JSON";

    private static ListJsonDocument BuildFixture() => new(
        SchemaVersion: 1,
        EngineVersion: "1.2.3-golden",
        StepTypes: new[]
        {
            new ListJsonStepType("db-assert.postgres", "db-assert", "postgres"),
            new ListJsonStepType("http.rest", "http", "rest"),
            new ListJsonStepType("mq-publish.kafka", "mq-publish", "kafka"),
        });

    [Fact]
    public void ListJsonDocument_MatchesGolden_ByteForByte()
    {
        var actual = JsonSerializer.Serialize(BuildFixture(), CliJsonContract.Options);

        if (IsRegenRequested())
        {
            var repoRoot = FindRepoRoot();
            var goldenPath = Path.Combine(
                repoRoot, "tests", "Vouchfx.Cli.Tests", "Golden", "list-json-document.v1.json");
            File.WriteAllText(goldenPath, actual);
            return;
        }

        var golden = ReadGolden("list-json-document.v1.json");

        Assert.Equal(Normalise(golden), Normalise(actual));
    }

    // ── Regen + golden-read helpers (mirror ValidateJsonGoldenTests) ─────────────

    private static bool IsRegenRequested()
    {
        var value = Environment.GetEnvironmentVariable(RegenEnvVar);
        return !string.IsNullOrEmpty(value)
            && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').");
    }

    private static string ReadGolden(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Golden", fileName);

        Assert.True(
            File.Exists(path),
            $"Golden file not found at '{path}'. The freeze gate requires "
            + $"Golden/{fileName} to be committed and copied to output.");

        return File.ReadAllText(path);
    }

    private static string Normalise(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
}
