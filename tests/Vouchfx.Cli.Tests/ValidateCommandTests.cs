// Vouchfx.Cli.Tests — `validate` Execute orchestration tests (#260). No Docker.
//
// Exercises ValidateCommand.Execute directly (bypassing System.CommandLine parsing)
// against real temp *.e2e.yaml files and the real sealed Core registry. Mirrors
// RunPathRootExecuteTests' fixture style (a per-test temp directory). Covers the exit
// code table: 0 (all valid / nothing discovered), 2 (bad path), 4 (one or more invalid) —
// deliberately never 1 (TestFailure is reserved for a genuine run Fail). Never starts a
// topology: ScenarioValidator's pipeline stops before SuiteTopology.StartAsync.
//
// Code-review MINOR-2 (--json stdout purity) and MINOR-3 (discovery I/O errors losing
// their message) are each covered by a dedicated test below.
//
// Peer-review MAJOR (validate-vs-run base-directory drift): a recursive multi-directory
// scan MUST resolve every scenario's compile-time relative file-path fields (e.g.
// script.csharp's `file:`) against the SAME single base directory RunCommand.ExecuteAsync
// uses — Path.GetDirectoryName of the FIRST discovery-clean scenario — never a
// per-scenario directory. Two tests below prove both directions of the fix: a scenario
// several directories away from the first one now compiles when `run` would let it
// compile (helper file lives in the FIRST scenario's directory), and it now correctly
// FAILS when `run` would also fail (helper file lives only in the referencing scenario's
// OWN directory, which is not the shared base). Before the fix, both cases disagreed
// with `run` (the exact drift the peer review flagged).

using System.Text.Json;
using Vouchfx.Cli;
using Vouchfx.Engine.Runtime;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ValidateCommandTests : IDisposable
{
    private readonly string _root;

    public ValidateCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vouchfx-validate-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    private const string ValidScenario =
        "steps:\n" +
        "  - id: check\n" +
        "    type: http.rest\n" +
        "    target: svc\n" +
        "    method: GET\n" +
        "    path: /health\n";

    // Schema-invalid: http.rest requires 'method', which is omitted here.
    private const string SchemaInvalidScenario =
        "steps:\n" +
        "  - id: bad\n" +
        "    type: http.rest\n" +
        "    target: svc\n" +
        "    path: /health\n";

    // Genuinely fails ScenarioDiscovery.ParseFile's own YAML parse (unterminated flow
    // sequence) — Ast stays null and ParseError is set to "Parse / AST error: ...". This
    // is the deterministic, cross-platform way to produce a Failed DiscoveredScenario
    // (MINOR-3): simulating a true unreadable-file I/O error portably across Windows/Linux
    // CI is not reliable, but discovery's Failed/ParseError code path is IDENTICAL for
    // both causes (see ScenarioDiscovery.ParseFile), so this exercises the same fix.
    private const string DiscoveryParseFailureScenario = "steps: [\n  - id: bad\n";

    /// <summary>
    /// Calls <see cref="ValidateCommand.Execute"/> with a discarded error-output sink, for
    /// tests that only care about stdout (<paramref name="output"/>).
    /// </summary>
    private static int Execute(string path, bool json, TextWriter output) =>
        ValidateCommand.Execute(path, json, output, TextWriter.Null);

    [Fact]
    public void Execute_MissingRoot_ReturnsUsageError_AndWritesNotFoundMessage()
    {
        var sw = new StringWriter();
        var missing = Path.Combine(_root, "does-not-exist");

        var exitCode = Execute(missing, json: false, sw);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("does not exist", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_WrongExtensionFileRoot_ReturnsUsageError_AndWritesSuffixMessage()
    {
        var sw = new StringWriter();
        var wrong = Path.Combine(_root, "scenario.txt");
        File.WriteAllText(wrong, ValidScenario);

        var exitCode = Execute(wrong, json: false, sw);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains(".e2e.yaml", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_EmptyDirectory_ReturnsSuccess_AndWritesNothingFoundMessage()
    {
        var sw = new StringWriter();

        var exitCode = Execute(_root, json: false, sw);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("No *.e2e.yaml scenarios found", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_EmptyDirectory_Json_ReturnsVacuouslyValidDocument()
    {
        var sw = new StringWriter();

        var exitCode = Execute(_root, json: true, sw);

        Assert.Equal(ExitCodes.Success, exitCode);
        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);
        Assert.True(document!.Valid);
        Assert.Empty(document.Scenarios);
    }

    [Fact]
    public void Execute_ValidFile_ReturnsSuccess_AndWritesPassLine()
    {
        var sw = new StringWriter();
        var file = Path.Combine(_root, "valid.e2e.yaml");
        File.WriteAllText(file, ValidScenario);

        var exitCode = Execute(file, json: false, sw);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("PASS", sw.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 valid, 0 invalid (of 1 discovered).", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_InvalidFile_ReturnsInconclusiveExitCode_AndWritesFailLine()
    {
        var sw = new StringWriter();
        var file = Path.Combine(_root, "invalid.e2e.yaml");
        File.WriteAllText(file, SchemaInvalidScenario);

        var exitCode = Execute(file, json: false, sw);

        // 4 (Inconclusive), never 1 (TestFailure) — the scenario never ran.
        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("FAIL", sw.ToString(), StringComparison.Ordinal);
        Assert.Contains("[Schema]", sw.ToString(), StringComparison.Ordinal);
        Assert.Contains("0 valid, 1 invalid (of 1 discovered).", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_DirectoryWithValidAndInvalidFiles_Json_ReportsBoth()
    {
        File.WriteAllText(Path.Combine(_root, "a-valid.e2e.yaml"), ValidScenario);
        File.WriteAllText(Path.Combine(_root, "b-invalid.e2e.yaml"), SchemaInvalidScenario);

        var sw = new StringWriter();
        var exitCode = Execute(_root, json: true, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);

        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);
        Assert.False(document!.Valid);
        Assert.Equal(2, document.Scenarios.Count);

        var validEntry = document.Scenarios.Single(s => s.Path.Contains("a-valid", StringComparison.Ordinal));
        Assert.True(validEntry.Valid);
        Assert.Empty(validEntry.Diagnostics);

        var invalidEntry = document.Scenarios.Single(s => s.Path.Contains("b-invalid", StringComparison.Ordinal));
        Assert.False(invalidEntry.Valid);
        Assert.NotEmpty(invalidEntry.Diagnostics);
        Assert.Equal(ValidationStage.Schema, invalidEntry.Diagnostics[0].Stage);
    }

    [Fact]
    public void Execute_Json_SchemaVersionAndEngineVersionArePresent()
    {
        var file = Path.Combine(_root, "valid.e2e.yaml");
        File.WriteAllText(file, ValidScenario);

        var sw = new StringWriter();
        Execute(file, json: true, sw);

        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);
        Assert.Equal(1, document!.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(document.EngineVersion));
    }

    // ── MINOR-2: --json stdout purity (usage-error diagnostics move to stderr) ─────────

    [Fact]
    public void Execute_MissingRoot_Json_StdoutIsEmpty_MessageGoesToStderr()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var missing = Path.Combine(_root, "does-not-exist");

        var exitCode = ValidateCommand.Execute(missing, json: true, stdout, stderr);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        // stdout must carry NOTHING on a usage error in --json mode — a tool parsing it as
        // one JSON document must never choke on a stray text line.
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("does not exist", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_WrongExtensionFileRoot_Json_StdoutIsEmpty_MessageGoesToStderr()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var wrong = Path.Combine(_root, "scenario.txt");
        File.WriteAllText(wrong, ValidScenario);

        var exitCode = ValidateCommand.Execute(wrong, json: true, stdout, stderr);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains(".e2e.yaml", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_MissingRoot_NonJson_MessageStillGoesToOutput()
    {
        // Non-JSON mode is UNCHANGED: the diagnostic still goes to the human report's own
        // stream (stdout), not stderr.
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var missing = Path.Combine(_root, "does-not-exist");

        var exitCode = ValidateCommand.Execute(missing, json: false, stdout, stderr);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("does not exist", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    // ── MINOR-3: a Failed discovery surfaces its OWN ParseError, not a re-validation ───

    [Fact]
    public void Execute_FileFailsDiscoveryParse_SurfacesOriginalParseErrorAtParseStage()
    {
        var sw = new StringWriter();
        var file = Path.Combine(_root, "unparseable.e2e.yaml");
        File.WriteAllText(file, DiscoveryParseFailureScenario);

        var exitCode = Execute(file, json: false, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        var text = sw.ToString();
        Assert.Contains("FAIL", text, StringComparison.Ordinal);
        Assert.Contains("[Parse]", text, StringComparison.Ordinal);
        // The ORIGINAL discovery message ("Parse / AST error: ...") must be surfaced
        // verbatim — never a generic re-validation diagnostic (e.g. a schema "document is
        // empty" complaint) derived from re-running the (possibly unrepresentative) text
        // back through ScenarioValidator.
        Assert.Contains("Parse / AST error", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_FileFailsDiscoveryParse_Json_ReportsParseStageWithOriginalMessage()
    {
        var file = Path.Combine(_root, "unparseable.e2e.yaml");
        File.WriteAllText(file, DiscoveryParseFailureScenario);

        var sw = new StringWriter();
        var exitCode = Execute(file, json: true, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);
        var entry = Assert.Single(document!.Scenarios);
        Assert.False(entry.Valid);
        var diagnostic = Assert.Single(entry.Diagnostics);
        Assert.Equal(ValidationStage.Parse, diagnostic.Stage);
        Assert.Contains("Parse / AST error", diagnostic.Message, StringComparison.Ordinal);
    }

    // ── Peer-review MAJOR: validate must resolve compile-time relative paths using the
    //    SAME single suite-wide base directory `run` uses, not a per-scenario directory ──

    // A trivially-valid, always-compiling scenario used purely to occupy the FIRST
    // ordinal-sorted discovery slot (its directory becomes the shared suite base).
    private const string AnchorScenario =
        "steps:\n" +
        "  - id: check\n" +
        "    type: http.rest\n" +
        "    target: svc\n" +
        "    method: GET\n" +
        "    path: /health\n";

    // References a bare 'helper.csx' filename — deliberately no path segments, so its
    // resolution is entirely determined by WHICH directory is chosen as the base, which
    // is exactly the ambiguity this test exploits. script.csharp splices trivially-valid
    // C# (a comment only) so that once the file resolves, the scenario compiles cleanly.
    private const string ScriptWithFileReference =
        "steps:\n" +
        "  - id: run-helper\n" +
        "    type: script.csharp\n" +
        "    file: helper.csx\n";

    private const string HelperCsxContents = "// no-op\n";

    /// <summary>
    /// The helper file lives in the FIRST discovered scenario's directory ('a'), NOT in
    /// the referencing scenario's own directory ('b'). <c>run</c> resolves EVERY
    /// scenario's relative file-path fields against the first scenario's directory
    /// (RunCommand.ExecuteAsync's <c>suiteBaseDirectory</c>), so 'y' compiles under a real
    /// run. Before the fix, <c>validate</c> resolved 'y' against its OWN directory ('b') —
    /// where 'helper.csx' does NOT exist — wrongly reporting 'y' invalid (a Pipeline
    /// "file not found" diagnostic) even though `run` would accept it. This test FAILS
    /// against that per-scenario code and PASSES once validate matches run's single
    /// shared base directory.
    /// </summary>
    [Fact]
    public void Execute_MultiDirectoryScan_FileReferenceResolvesAgainstFirstScenariosDirectory_MatchingRun()
    {
        var dirA = Path.Combine(_root, "a");
        var dirB = Path.Combine(_root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        // 'a' sorts before 'b' ordinally, so ScenarioDiscovery.Discover's ordinal-sorted
        // walk makes x.e2e.yaml (in 'a') the FIRST discovery-clean scenario — its
        // directory becomes the shared suite base, exactly as RunCommand computes it.
        File.WriteAllText(Path.Combine(dirA, "x.e2e.yaml"), AnchorScenario);
        File.WriteAllText(Path.Combine(dirA, "helper.csx"), HelperCsxContents);
        File.WriteAllText(Path.Combine(dirB, "y.e2e.yaml"), ScriptWithFileReference);

        var sw = new StringWriter();
        var exitCode = Execute(_root, json: true, sw);

        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);
        Assert.Equal(2, document!.Scenarios.Count);

        var yEntry = document.Scenarios.Single(
            s => s.Path.Contains("y.e2e.yaml", StringComparison.Ordinal));
        Assert.True(
            yEntry.Valid,
            "y.e2e.yaml's 'file: helper.csx' must resolve against the FIRST scenario's "
            + $"directory ('a', where helper.csx exists) — matching what `run` would do — "
            + $"not against y's own directory ('b'). Diagnostics: "
            + string.Join("; ", yEntry.Diagnostics.Select(d => $"[{d.Stage}] {d.Message}")));

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    /// <summary>
    /// The reversed case: the helper file lives ONLY in the referencing scenario's OWN
    /// directory ('b'), NOT in the first scenario's directory ('a') — the shared base
    /// <c>run</c> would actually use. A real <c>run</c> would therefore FAIL to resolve
    /// 'y's <c>file:</c> reference (it never looks in 'b'). Before the fix, per-scenario
    /// <c>validate</c> resolved 'y' against its own directory ('b') and found the file —
    /// wrongly reporting 'y' VALID, exactly the "validate passes what run rejects" defect
    /// the peer review flagged. After the fix, validate must agree with run and report 'y'
    /// INVALID too — proving the fix is a faithful match, not merely "always succeeds".
    /// </summary>
    [Fact]
    public void Execute_MultiDirectoryScan_ReversedFileLocation_CorrectlyFailsMatchingRun()
    {
        var dirA = Path.Combine(_root, "a");
        var dirB = Path.Combine(_root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        File.WriteAllText(Path.Combine(dirA, "x.e2e.yaml"), AnchorScenario);
        // No helper.csx in 'a' (the shared suite base) this time.
        File.WriteAllText(Path.Combine(dirB, "y.e2e.yaml"), ScriptWithFileReference);
        File.WriteAllText(Path.Combine(dirB, "helper.csx"), HelperCsxContents);

        var sw = new StringWriter();
        var exitCode = Execute(_root, json: true, sw);

        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);

        var yEntry = document!.Scenarios.Single(
            s => s.Path.Contains("y.e2e.yaml", StringComparison.Ordinal));
        Assert.False(
            yEntry.Valid,
            "y.e2e.yaml's 'file: helper.csx' must resolve against the FIRST scenario's "
            + "directory ('a', where helper.csx does NOT exist) — matching what `run` "
            + "would do — not against y's own directory ('b'), where it happens to exist.");
        Assert.Contains(
            yEntry.Diagnostics,
            d => d.Stage == ValidationStage.Pipeline
                && d.Message.Contains("helper.csx", StringComparison.Ordinal)
                && d.Message.Contains("not found", StringComparison.Ordinal));

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }
}
