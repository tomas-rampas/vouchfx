// Vouchfx.Cli.Tests — `validate --json` wire-shape freeze (#260).
//
// The `validate --json` document is a FROZEN contract: a downstream consumer (e.g. a
// future vouchfx-mcp validate worker) can parse it without depending on any
// Vouchfx.Cli-internal type. This test serialises a FIXED, hand-built ValidateJsonDocument
// (never the real engineVersion / a real registry) through the SAME CliJsonContract.Options
// ValidateCommand uses, and asserts the result is byte-for-byte identical to the committed
// golden Golden/validate-json-document.v1.json. Using fixed sample data (not a live
// AssemblyInformationalVersion or a live registry) keeps the golden reproducible across
// builds and machines — only the WIRE SHAPE (property names, order, nesting, enum tokens)
// is under test here, not any particular build's version string.
//
// REGENERATION (when the validate --json shape legitimately, additively changes):
//   VOUCHFX_REGEN_VALIDATE_JSON=1 dotnet test tests/Vouchfx.Cli.Tests \
//     --filter "FullyQualifiedName~ValidateJsonGoldenTests"
//   This rewrites Golden/validate-json-document.v1.json from the freshly-serialised
//   fixture. Review the diff, then commit. Mirrors EventContractFreezeTests /
//   SchemaFreezeTests' VOUCHFX_REGEN_* convention.

using System.Text.Json;
using Vouchfx.Cli;
using Vouchfx.Engine.Runtime;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ValidateJsonGoldenTests
{
    private const string RegenEnvVar = "VOUCHFX_REGEN_VALIDATE_JSON";

    /// <summary>
    /// A fixed, deterministic <c>validate --json</c> document exercising every field: a
    /// passing scenario (empty diagnostics) and a failing scenario carrying one diagnostic
    /// per <see cref="ValidationStage"/> member — schema, parse, pipeline, AND roslyn — so
    /// every enum wire token is pinned by this ONE golden. Renaming (or re-casing) any of
    /// the four tokens must break this test: a fixture that only exercised two of the four
    /// stages would leave the other two spellings frozen by nothing.
    /// </summary>
    private static ValidateJsonDocument BuildFixture() => new(
        SchemaVersion: 1,
        EngineVersion: "1.2.3-golden",
        Scenarios: new[]
        {
            new ValidateJsonScenario(
                Path: "scenarios/valid.e2e.yaml",
                Valid: true,
                Diagnostics: Array.Empty<ValidateJsonDiagnostic>()),
            new ValidateJsonScenario(
                Path: "scenarios/invalid.e2e.yaml",
                Valid: false,
                Diagnostics: new[]
                {
                    new ValidateJsonDiagnostic(ValidationStage.Schema, "(line 3) 'method' is a required property."),
                    new ValidateJsonDiagnostic(ValidationStage.Parse, "Parse / AST error: unexpected end of stream."),
                    new ValidateJsonDiagnostic(ValidationStage.Pipeline, "step 'bad-path': http.rest: 'path' must be a rooted relative path (start with '/'); absolute URLs and protocol-relative paths are not allowed."),
                    new ValidateJsonDiagnostic(ValidationStage.Roslyn, "CSX compilation failed: (1,9): error CS1525: Invalid expression term ';'"),
                }),
        },
        Valid: false);

    [Fact]
    public void ValidateJsonDocument_MatchesGolden_ByteForByte()
    {
        var actual = JsonSerializer.Serialize(BuildFixture(), CliJsonContract.Options);

        if (IsRegenRequested())
        {
            var repoRoot = FindRepoRoot();
            var goldenPath = Path.Combine(
                repoRoot, "tests", "Vouchfx.Cli.Tests", "Golden", "validate-json-document.v1.json");
            File.WriteAllText(goldenPath, actual);
            return;
        }

        var golden = ReadGolden("validate-json-document.v1.json");

        Assert.Equal(Normalise(golden), Normalise(actual));
    }

    // ── Regen + golden-read helpers (mirror SchemaFreezeTests / EventContractFreezeTests) ──

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
