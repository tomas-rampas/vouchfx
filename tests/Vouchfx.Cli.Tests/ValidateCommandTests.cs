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
// Issue #268 (validate-vs-run base-directory drift, superseding the #260 peer-review
// MAJOR finding below): a recursive multi-directory scan MUST resolve EACH scenario's
// compile-time relative file-path fields (e.g. script.csharp's `file:`) against THAT
// scenario's OWN directory — never a single suite-wide directory shared by every
// scenario (the #260 behaviour these tests originally pinned, and which #268 replaces).
// `RunCommand.ExecuteAsync` compiles each scenario against its own directory too
// (`scenarioBaseDirectories`), so this is what keeps `validate` predicting `run`
// faithfully. Two tests below prove both directions of the #268 fix: a helper file
// beside the REFERENCING scenario's own directory now lets it compile (matching a real
// run), and a helper file that exists only in ANOTHER scenario's directory no longer
// leaks across scenarios — `validate` now correctly reports the reference unresolved,
// matching what `run` would also fail on. Before #268 (the #260 behaviour), both cases
// disagreed with `run` whenever a non-first scenario's own directory differed from the
// first scenario's.

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a locked or read-only file must not fail the test.
            // UnauthorizedAccessException (not just IOException) is possible here too — e.g.
            // a read-only file on some platforms — so both are swallowed (Copilot review
            // finding, #260 follow-up).
        }
    }

    // 'svc' must be a DECLARED service (services-generalisation spec, REQ-012 as narrowed by
    // M1: http.rest's target must name an entry under environment.services, and a target
    // naming a declared dependency is rejected at validation time — a dependency stages a
    // connection string, not a service endpoint, so it could never have worked at run time)
    // — this fixture predates that target-reconciliation check, when http.rest accepted any
    // target string.
    private const string ValidScenario =
        "environment:\n" +
        "  services:\n" +
        "    svc:\n" +
        "      image: myorg/svc:1.0\n" +
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

    // ── Copilot review finding #1: ScenarioDiscovery.Discover's OWN Path.GetFullPath /
    //    Directory.EnumerateFiles calls can throw exceptions beyond DirectoryNotFoundException
    //    / ScenarioDiscoveryException (ArgumentException, NotSupportedException,
    //    PathTooLongException, UnauthorizedAccessException, IOException,
    //    System.Security.SecurityException) on a malformed/inaccessible path. These MUST map
    //    to the same clean usage error (exit 2), never an unhandled-exception crash. A NUL
    //    character is the deterministic, cross-platform trigger used here: Path.GetFullPath
    //    unconditionally rejects it (ArgumentException: "Null character in path.") on every
    //    .NET platform/OS — confirmed by direct inspection — unlike most other "invalid path
    //    character" checks, which are platform-dependent or have been relaxed on .NET
    //    Core/.NET 5+. This exercises the SAME broadened catch RunCommand.ExecuteAsync now
    //    shares (kept in lock-step, #260 follow-up).

    [Fact]
    public void Execute_PathContainsNullCharacter_ReturnsUsageError_WithoutUnhandledException()
    {
        var sw = new StringWriter();
        var badPath = "bad\0path";

        var exitCode = Execute(badPath, json: false, sw);

        Assert.Equal(ExitCodes.UsageError, exitCode);
    }

    [Fact]
    public void Execute_PathContainsNullCharacter_Json_StdoutIsEmpty_MessageGoesToStderr()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var badPath = "bad\0path";

        var exitCode = ValidateCommand.Execute(badPath, json: true, stdout, stderr);

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.NotEmpty(stderr.ToString());
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

    // -- Issue #266, Item 4: display sanitisation of an embedded control character --

    // A script.csharp 'file:' reference that does not exist produces a Pipeline diagnostic
    // that echoes the field's raw value verbatim ("file '{model.File}' not found ...") - a
    // realistic, schema-legal vector for a hostile field value (unlike 'id', which the
    // schema restricts to [A-Za-z_][A-Za-z0-9_-]*, 'file' is an unconstrained string).
    //
    // The embedded control character is FORM FEED (0x0C), not ESC (0x1B): DocumentValidator's
    // schema-validation stage bridges the whole YAML document through YamlDotNet's
    // JsonCompatible serialiser before it ever reaches Pipeline, and that serialiser emits
    // several control characters -- including ESC -- as a non-JSON short escape (e.g. "\e"),
    // which then fails re-parsing as JSON at the SCHEMA stage with a generic "Failed to parse
    // YAML" message that never echoes the field's actual content at all (confirmed by direct
    // probing while writing this test: 0x01/0x02/0x07/0x0B/0x1F/0x7F/0x9F all fail the same
    // way; only 0x08 (backspace) and 0x0C (form feed) round-trip, because JSON has native
    // single-character escapes for them). This is a pre-existing YamlDotNet limitation
    // unrelated to issue #266 (out of scope to fix here), so FF is used as the control
    // character that both (a) survives to reach a Pipeline-stage diagnostic that echoes it
    // back, and (b) is exactly the kind of byte DisplaySanitiser strips (every C0 control
    // character except \t/\n).
    private const string HostileFieldValueScenario =
        "steps:\n" +
        "  - id: run-helper\n" +
        "    type: script.csharp\n" +
        "    file: \"before\\u000cafter.csx\"\n";

    [Fact]
    public void Execute_DiagnosticWithEmbeddedControlCharacter_HumanReport_RendersInert()
    {
        var file = Path.Combine(_root, "hostile.e2e.yaml");
        File.WriteAllText(file, HostileFieldValueScenario);

        var sw = new StringWriter();
        var exitCode = Execute(file, json: false, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        var text = sw.ToString();
        Assert.Contains("[Pipeline]", text, StringComparison.Ordinal);
        // The surrounding text survives sanitisation intact...
        Assert.Contains("beforeafter.csx", text, StringComparison.Ordinal);
        // ...but the embedded control character itself does not.
        Assert.DoesNotContain((char)0x0C, text);
    }

    [Fact]
    public void Execute_DiagnosticWithEmbeddedControlCharacter_Json_StaysByteClean()
    {
        var file = Path.Combine(_root, "hostile.e2e.yaml");
        File.WriteAllText(file, HostileFieldValueScenario);

        var sw = new StringWriter();
        var exitCode = Execute(file, json: true, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        var jsonText = sw.ToString();

        // --json needs no DisplaySanitiser treatment: System.Text.Json always escapes
        // control characters inside a JSON string (mandated by the JSON spec itself), so a
        // raw control byte can never appear literally -- confirmed here rather than merely
        // asserted in a comment.
        Assert.DoesNotContain((char)0x0C, jsonText);
        Assert.Contains("\\f", jsonText, StringComparison.Ordinal);

        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(jsonText, CliJsonContract.Options);
        Assert.NotNull(document);
        var entry = Assert.Single(document!.Scenarios);
        Assert.False(entry.Valid);
        // Deserialising round-trips the JSON escape back to a real control character in the
        // in-memory string -- proving the document carries the ORIGINAL text faithfully
        // (--json is a tooling contract, so it must never silently mutate diagnostic
        // content the way the human report's DisplaySanitiser deliberately does).
        Assert.Contains(entry.Diagnostics, d => d.Message.Contains((char)0x0C));
    }

    // ── Issue #266: document-size cap at the ScenarioDiscovery.ParseFile read seam ──

    /// <summary>
    /// A <c>.e2e.yaml</c> file exceeding <see cref="ScenarioDiscovery.MaxDocumentSizeBytes"/>
    /// (1 MiB) is rejected as a Parse-stage diagnostic — the SAME code path an unreadable
    /// file already used (<see cref="ScenarioDiscovery.ParseFile"/>) — never read into
    /// memory in full, and never reaching schema/pipeline/Roslyn.
    /// </summary>
    [Fact]
    public void Execute_OversizedDocument_ReturnsInconclusive_WithParseStageSizeDiagnostic()
    {
        // Comfortably over the 1 MiB limit; content is irrelevant — the size check runs
        // before any read/parse.
        var oversized = "# " + new string('a', 1_100_000) + "\n";
        var file = Path.Combine(_root, "oversized.e2e.yaml");
        File.WriteAllText(file, oversized);

        var sw = new StringWriter();
        var exitCode = Execute(file, json: false, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        var text = sw.ToString();
        Assert.Contains("[Parse]", text, StringComparison.Ordinal);
        Assert.Contains("exceeds", text, StringComparison.Ordinal);
        Assert.Contains("1 MiB", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_OversizedDocument_Json_ReportsParseStageWithSizeDiagnostic()
    {
        var oversized = "# " + new string('a', 1_100_000) + "\n";
        var file = Path.Combine(_root, "oversized.e2e.yaml");
        File.WriteAllText(file, oversized);

        var sw = new StringWriter();
        var exitCode = Execute(file, json: true, sw);

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);
        var entry = Assert.Single(document!.Scenarios);
        Assert.False(entry.Valid);
        var diagnostic = Assert.Single(entry.Diagnostics);
        Assert.Equal(ValidationStage.Parse, diagnostic.Stage);
        Assert.Contains("exceeds", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("1 MiB", diagnostic.Message, StringComparison.Ordinal);
    }

    // ── Issue #268: validate must resolve compile-time relative paths using EACH
    //    scenario's OWN directory, never a single directory shared across scenarios ──

    // A trivially-valid, always-compiling scenario used purely to occupy the FIRST
    // ordinal-sorted discovery slot alongside the referencing scenario in dir 'b'.
    // 'svc' must be a DECLARED service (services-generalisation spec, REQ-012) — see
    // ValidScenario's own remark above for why.
    private const string AnchorScenario =
        "environment:\n" +
        "  services:\n" +
        "    svc:\n" +
        "      image: myorg/svc:1.0\n" +
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
    /// The helper file lives beside the REFERENCING scenario, in its OWN directory ('b'),
    /// NOT in the first-discovered scenario's directory ('a'). Issue #268: <c>run</c>
    /// compiles each scenario against its OWN directory (<c>RunCommand.ExecuteAsync</c>'s
    /// per-scenario <c>scenarioBaseDirectories</c>), so 'y' compiles under a real run — its
    /// <c>file: helper.csx</c> resolves right beside it. Before #268 (the #260 behaviour),
    /// <c>validate</c> resolved EVERY scenario's relative paths against the FIRST
    /// discovery-clean scenario's directory ('a') — where 'helper.csx' does NOT exist here —
    /// wrongly reporting 'y' invalid even though a real <c>run</c> would accept it. This
    /// test FAILS against that pre-#268 suite-wide-base code and PASSES once each scenario
    /// resolves against its own directory.
    /// </summary>
    [Fact]
    public void Execute_MultiDirectoryScan_FileReferenceResolvesAgainstOwnDirectory_MatchingRun()
    {
        var dirA = Path.Combine(_root, "a");
        var dirB = Path.Combine(_root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        // 'a' sorts before 'b' ordinally, so ScenarioDiscovery.Discover's ordinal-sorted
        // walk makes x.e2e.yaml (in 'a') the FIRST discovery-clean scenario. Under #268
        // this ordering carries NO significance for 'y's file: resolution — only 'y's OWN
        // directory ('b') does.
        File.WriteAllText(Path.Combine(dirA, "x.e2e.yaml"), AnchorScenario);
        // No helper.csx in 'a' — proves 'y' does NOT fall back to the first scenario's dir.
        File.WriteAllText(Path.Combine(dirB, "y.e2e.yaml"), ScriptWithFileReference);
        File.WriteAllText(Path.Combine(dirB, "helper.csx"), HelperCsxContents);

        var sw = new StringWriter();
        var exitCode = Execute(_root, json: true, sw);

        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);
        Assert.Equal(2, document!.Scenarios.Count);

        var yEntry = document.Scenarios.Single(
            s => s.Path.Contains("y.e2e.yaml", StringComparison.Ordinal));
        Assert.True(
            yEntry.Valid,
            "y.e2e.yaml's 'file: helper.csx' must resolve against y's OWN directory ('b', "
            + $"where helper.csx exists) — matching what `run` would do (issue #268) — not "
            + $"against the first-discovered scenario's directory ('a'). Diagnostics: "
            + string.Join("; ", yEntry.Diagnostics.Select(d => $"[{d.Stage}] {d.Message}")));

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    /// <summary>
    /// The reversed case: the helper file lives ONLY in the FIRST-discovered scenario's
    /// directory ('a'), NOT in the referencing scenario's own directory ('b'). A real
    /// <c>run</c> compiles 'y' against ITS OWN directory (#268) and would therefore FAIL to
    /// resolve 'y's <c>file:</c> reference (it never looks in 'a'). Before #268 (the #260
    /// behaviour), suite-wide <c>validate</c> resolved every scenario — including 'y' —
    /// against the first scenario's directory ('a') and found the file there, wrongly
    /// reporting 'y' VALID even though a per-scenario <c>run</c> would reject it. After
    /// #268, validate must agree with run and report 'y' INVALID too — proving the fix does
    /// not simply broadcast a DIFFERENT shared directory, but genuinely resolves per scenario.
    /// </summary>
    [Fact]
    public void Execute_MultiDirectoryScan_FileReferenceDoesNotResolveAgainstOtherScenariosDirectory_MatchingRun()
    {
        var dirA = Path.Combine(_root, "a");
        var dirB = Path.Combine(_root, "b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        File.WriteAllText(Path.Combine(dirA, "x.e2e.yaml"), AnchorScenario);
        File.WriteAllText(Path.Combine(dirA, "helper.csx"), HelperCsxContents);
        // No helper.csx in 'b' (y's own directory) this time.
        File.WriteAllText(Path.Combine(dirB, "y.e2e.yaml"), ScriptWithFileReference);

        var sw = new StringWriter();
        var exitCode = Execute(_root, json: true, sw);

        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);

        var yEntry = document!.Scenarios.Single(
            s => s.Path.Contains("y.e2e.yaml", StringComparison.Ordinal));
        Assert.False(
            yEntry.Valid,
            "y.e2e.yaml's 'file: helper.csx' must resolve against y's OWN directory ('b', "
            + "where helper.csx does NOT exist) — matching what `run` would do (issue #268) "
            + "— not against the first-discovered scenario's directory ('a'), where it "
            + "happens to exist.");
        Assert.Contains(
            yEntry.Diagnostics,
            d => d.Stage == ValidationStage.Pipeline
                && d.Message.Contains("helper.csx", StringComparison.Ordinal)
                && d.Message.Contains("not found", StringComparison.Ordinal));

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
    }

    // ── #387: `${secret:}` on a security field, through the CLI's own validate path ──
    //
    // The two halves of the fix, measured where the issue measured the bug — `vouchfx validate`.
    // Both must keep exit code 4 (Inconclusive): the document is invalid, the scenario never ran,
    // and neither shape is a product Fail.

    /// <summary>
    /// REQ-011. A <c>${secret:}</c> in the path-valued <c>clientKey</c> is refused as a misuse of
    /// reference syntax, with NO filesystem-path message and no garbled resolved path — the exact
    /// output #387 recorded (<c>file '${secret:env/CLIENT_KEY}' not found (resolved to
    /// '…\${secret:env\CLIENT_KEY}')</c>) is gone, not merely accompanied.
    /// </summary>
    [Fact]
    public void Execute_SecretReferenceInClientKey_ReturnsInconclusive_WithNoFilesystemMessage()
    {
        // clientCert is validated BEFORE clientKey, so it must be a real file for the clientKey
        // diagnostic to be the one reported.
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        var file = Path.Combine(_root, "secret-key.e2e.yaml");
        File.WriteAllText(
            file,
            "environment:\n" +
            "  services:\n" +
            "    api:\n" +
            "      image: myorg/api:1.0\n" +
            "      security:\n" +
            "        profile: mtls\n" +
            "        endpoint: 8443\n" +
            "        clientCert: ./client.pem\n" +
            "        clientKey: \"${secret:env/CLIENT_KEY}\"\n" +
            "steps:\n" +
            "  - id: call\n" +
            "    type: http.rest\n" +
            "    target: api\n" +
            "    method: GET\n" +
            "    path: /health\n");

        var sw = new StringWriter();
        var exitCode = Execute(file, json: false, sw);
        var rendered = sw.ToString();

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("[Pipeline]", rendered, StringComparison.Ordinal);
        Assert.Contains(
            "environment.services.api.security.clientKey:", rendered, StringComparison.Ordinal);
        Assert.Contains("is a secret reference", rendered, StringComparison.Ordinal);
        Assert.Contains("clientKeyPassword", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("resolved to", rendered, StringComparison.OrdinalIgnoreCase);

        // #387's second defect: the '/' inside the token was eaten by path normalisation, so the
        // echoed value was split across a directory boundary. The token is echoed VERBATIM now.
        Assert.Contains("${secret:env/CLIENT_KEY}", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// EDGE-003. An unknown source in the reference-valued <c>clientKeyPassword</c> is diagnosed
    /// exactly as the same token in a step is — the asymmetry #387 measured, closed.
    /// </summary>
    [Fact]
    public void Execute_UnknownSecretSourceInClientKeyPassword_ReturnsInconclusive_NamingTheSource()
    {
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var file = Path.Combine(_root, "bad-source.e2e.yaml");
        File.WriteAllText(
            file,
            "environment:\n" +
            "  services:\n" +
            "    api:\n" +
            "      image: myorg/api:1.0\n" +
            "      security:\n" +
            "        profile: mtls\n" +
            "        endpoint: 8443\n" +
            "        clientCert: ./client.pem\n" +
            "        clientKey: ./client.key\n" +
            "        clientKeyPassword: \"${secret:nosuchsource/KEY_PASS}\"\n" +
            "steps:\n" +
            "  - id: call\n" +
            "    type: http.rest\n" +
            "    target: api\n" +
            "    method: GET\n" +
            "    path: /health\n");

        var sw = new StringWriter();
        var exitCode = Execute(file, json: false, sw);
        var rendered = sw.ToString();

        Assert.Equal(ExitCodes.Inconclusive, exitCode);
        Assert.Contains("[Pipeline]", rendered, StringComparison.Ordinal);
        Assert.Contains(
            "environment.services.api.security.clientKeyPassword:", rendered, StringComparison.Ordinal);
        Assert.Contains("names an unknown source 'nosuchsource'", rendered, StringComparison.Ordinal);
        Assert.Contains("known sources are:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("step '", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The disclosure regression, pinned at CLI level because that is the only level at which it
    /// was ever caught: two rounds of unit-level reasoning produced a guard that looked right and
    /// leaked, and only driving the real CLI showed it. A nested <c>${secret:</c> inside the PATH
    /// is schema-valid — the pattern's <c>[^}]+</c> swallows it — so it reaches the secret-
    /// reference pass from an ordinary suite and its whole declared value used to be interpolated
    /// into the diagnostic.
    /// <para>
    /// Asserted on <c>--json</c> specifically: that document is what a CI job archives, so a leak
    /// there outlives the console it was printed to.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("${secret:env/PASS${secret:MARKER-ZQ7X}", "MARKER-ZQ7X")]
    [InlineData("${secret:nosuchsource/PASS${secret:MARKER-ZQ7X}", "MARKER-ZQ7X")]
    public void Execute_NestedSigilInClientKeyPassword_Json_NeverDisclosesTheDeclaredValue(
        string declared, string marker)
    {
        File.WriteAllText(Path.Combine(_root, "client.pem"), "placeholder");
        File.WriteAllText(Path.Combine(_root, "client.key"), "placeholder");
        var file = Path.Combine(_root, "nested-sigil.e2e.yaml");
        File.WriteAllText(
            file,
            "environment:\n" +
            "  services:\n" +
            "    api:\n" +
            "      image: myorg/api:1.0\n" +
            "      security:\n" +
            "        profile: mtls\n" +
            "        endpoint: 8443\n" +
            "        clientCert: ./client.pem\n" +
            "        clientKey: ./client.key\n" +
            $"        clientKeyPassword: \"{declared}\"\n" +
            "steps:\n" +
            "  - id: call\n" +
            "    type: http.rest\n" +
            "    target: api\n" +
            "    method: GET\n" +
            "    path: /health\n");

        var sw = new StringWriter();
        var exitCode = Execute(file, json: true, sw);
        var json = sw.ToString();

        Assert.Equal(ExitCodes.Inconclusive, exitCode);

        // Not merely absent from the rendered message — absent from the whole archived document,
        // in any escaping. System.Text.Json \u-escapes control characters but not letters, so a
        // plain marker would appear verbatim if it were present at all.
        Assert.DoesNotContain(marker, json, StringComparison.OrdinalIgnoreCase);

        // …and no PREFIX of it either. A whole-marker assertion alone would pass a diagnostic that
        // echoed a truncated head of the declared value, which is still disclosure. Four characters
        // is the shortest prefix that cannot collide with ordinary message text.
        for (var length = marker.Length; length >= 4; length--)
        {
            Assert.DoesNotContain(marker[..length], json, StringComparison.OrdinalIgnoreCase);
        }

        var document = JsonSerializer.Deserialize<ValidateJsonDocument>(json, CliJsonContract.Options);
        var scenario = Assert.Single(document!.Scenarios);
        var diagnostic = Assert.Single(scenario.Diagnostics);
        Assert.Equal(ValidationStage.Pipeline, diagnostic.Stage);
        Assert.Contains(
            "environment.services.api.security.clientKeyPassword:",
            diagnostic.Message,
            StringComparison.Ordinal);
        Assert.Contains("not a single, whole secret reference", diagnostic.Message, StringComparison.Ordinal);
    }
}
