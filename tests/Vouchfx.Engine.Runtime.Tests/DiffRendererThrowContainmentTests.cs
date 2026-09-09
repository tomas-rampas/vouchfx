// Vouchfx.Engine.Runtime.Tests — issue #485: a throwing IStepDiffRenderer must cost one diff
// line, never a report artefact. No Docker.
//
// WHAT WAS UNPINNED. `ScenarioRunner.BuildDiffLookup` called BOTH members of the optional
// provider-implemented `IStepDiffRenderer` surface UNGUARDED. Neither consumer's own catch covers
// a general throw:
//   • `TerminalRenderer.Render`'s per-envelope catch filters `JsonException or
//     InvalidOperationException`. Anything else — NullReferenceException, FormatException,
//     ArgumentException, KeyNotFoundException — escaped it, and the terminal render runs BEFORE
//     the HTML / JUnit / `--events` writes in `ParallelSuiteRunner.RenderAndAggregate`, so ALL
//     THREE artefacts were lost with it.
//   • `HtmlRenderer`'s own diff-site catch filters `InvalidOperationException or JsonException`
//     over a `FileReportWriter` catch that adds only the IO/path family, so the same throw
//     arriving through the HTML renderer killed the HTML artefact too.
// The verdict was already computed when this ran, so nothing was falsified — the loss was the
// evidence, after the answer, which is why this is an ARTEFACT-LOSS bug and not a taxonomy one.
//
// WHY THE SEAM AND NOT A CONTAINERISED RUN. The exposure is entirely in the reporting tail: a
// buffered event stream plus the diff-lookup closure, consumed by the renderers. Driving it from
// a real topology would add containers to prove nothing this seam does not already prove, and
// the closure under test is the REAL one — `ScenarioRunner.BuildParallelDiffLookup`, which both
// run paths build (the sequential path calls the same private `BuildDiffLookup` behind it), so a
// guard that existed on only one path could not pass these tests.
//
// THE STUBS ARE DECLARED HERE and are file-scoped: seven provider kinds that exist only to make
// a diff renderer throw (or not). They are discovered by the SAME assembly scan the registry
// does for real providers.

using System.IO;
using System.Text.Json;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Abstractions.Events;
using Vouchfx.Engine.Reporting;
using Vouchfx.Sdk;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Tests;

// ── File-scoped stub providers ────────────────────────────────────────────────

file sealed record DiffStubModel(string Tag) : IStepModel;

/// <summary>
/// Common plumbing for the seven stubs declared below — six diff renderers and one control that
/// implements no renderer at all.  None of them is ever compiled.
/// </summary>
file abstract class DiffStubProviderBase
    : IStepProvider,
      IStepBinder<DiffStubModel>,
      IStepValidator<DiffStubModel>,
      IStepCompiler<DiffStubModel>
{
    private static readonly string[] s_authors = { "test" };

    public abstract StepKindId Kind { get; }

    public ProviderMetadata Metadata => new(
        Version: "0.1.0",
        MinEngineVersion: "0.1.0",
        License: "Apache-2.0",
        Authors: s_authors);

    public JsonSchemaFragment SchemaFragment => new("""{"type":"object"}""");

    public DiffStubModel Bind(YamlNode node, IBindingContext ctx) => new(Tag: "diff-stub");

    public ValidationResult Validate(DiffStubModel model, IProjectContext ctx) => ValidationResult.Success;

    public CsxFragment Emit(DiffStubModel model, ICompileContext ctx) =>
        new(
            RequiredUsings: System.Array.Empty<string>(),
            RequiredHelpers: System.Array.Empty<string>(),
            StatementBlock: $"{{ /* diff stub: {CsxFragment.SanitiseId(ctx.StepId)} */ }}");
}

/// <summary><c>RenderDiff</c> throws a type outside every renderer's filtered catch.</summary>
[StepProvider]
file sealed class StubThrowingRenderDiffProvider : DiffStubProviderBase, IStepDiffRenderer
{
    public override StepKindId Kind => new("stub", "throwing-renderdiff");

    public bool CanRender(JsonElement observation) => true;

    /// <summary>
    /// Produces a REAL <see cref="System.NullReferenceException"/> the way a defective renderer
    /// produces one — by dereferencing something the observation did not carry — rather than
    /// throwing the type directly, which CA2201 forbids (the runtime reserves it).  The TYPE is
    /// load-bearing rather than incidental: it sits outside <c>TerminalRenderer</c>'s
    /// <c>JsonException or InvalidOperationException</c> filter, outside <c>HtmlRenderer</c>'s
    /// identical one, and outside <c>FileReportWriter</c>'s IO/path family — which is exactly the
    /// escape route issue #485 closes.
    /// </summary>
    public string? RenderDiff(JsonElement observation) => AbsentField(observation)!.ToString();

    /// <summary>Always <see langword="null"/> — the "field the provider assumed was there".</summary>
    private static object? AbsentField(JsonElement observation) => null;
}

/// <summary><c>CanRender</c> throws — a renderer broken for every payload, not just one.</summary>
[StepProvider]
file sealed class StubThrowingCanRenderProvider : DiffStubProviderBase, IStepDiffRenderer
{
    internal const string Message = "CanRender always throws";

    public override StepKindId Kind => new("stub", "throwing-canrender");

    public bool CanRender(JsonElement observation) => throw new System.FormatException(Message);

    public string? RenderDiff(JsonElement observation) => "unreachable";
}

/// <summary>
/// A well-behaved renderer, so the guard can be shown to change nothing for one — and a kind
/// whose provider implements NO diff renderer at all is covered by
/// <see cref="StubNoRendererProvider"/>.
/// </summary>
[StepProvider]
file sealed class StubHealthyRendererProvider : DiffStubProviderBase, IStepDiffRenderer
{
    internal const string Diff = "HEALTHY-DIFF-MARKER";

    public override StepKindId Kind => new("stub", "healthy-renderer");

    public bool CanRender(JsonElement observation) => true;

    public string? RenderDiff(JsonElement observation) => Diff;
}

/// <summary>The control: a provider with no <see cref="IStepDiffRenderer"/> at all.</summary>
[StepProvider]
file sealed class StubNoRendererProvider : DiffStubProviderBase
{
    public override StepKindId Kind => new("stub", "no-renderer");
}

/// <summary>
/// A renderer whose exception message carries raw ANSI/VT100 bytes — provider-authored text on a
/// human-facing writer, which is the §17 / issue #266 Item 4 category.
/// </summary>
[StepProvider]
file sealed class StubEscapeInMessageProvider : DiffStubProviderBase, IStepDiffRenderer
{
    /// <summary>
    /// A raw ESC (0x1B) as a one-character string.  Built from its code point rather than written
    /// as a literal so that no control byte lands in this source file, where a formatter, a diff
    /// viewer or a terminal `cat` would each mangle it differently.
    /// </summary>
    internal static readonly string Esc = new((char)0x1B, 1);

    /// <summary>A raw BEL (0x07), for the same reason.</summary>
    internal static readonly string Bel = new((char)0x07, 1);

    /// <summary>ESC [ 2 J (clear screen) followed by a bell, embedded in the message.</summary>
    internal static readonly string Message = "boom " + Esc + "[2J" + Bel + " cleared";

    public override StepKindId Kind => new("stub", "escaping-renderdiff");

    public bool CanRender(JsonElement observation) => true;

    public string? RenderDiff(JsonElement observation) => throw new System.InvalidTimeZoneException(Message);
}

/// <summary>
/// A renderer whose exception message carries LINE SEPARATORS rather than control bytes — a
/// SEPARATE claim from <see cref="StubEscapeInMessageProvider"/>'s and given its own stub so that
/// a failure names which of the two broke.  That one is about scrubbing control characters out of
/// provider-authored text; this one is about the fault diagnostic staying on ONE line, which is
/// the shape the CHANGELOG publishes for it ("one line per (kind, member, exception type) per
/// run") and which <c>DisplaySanitiser</c> cannot deliver on its own: it PRESERVES <c>\n</c> by
/// design, and U+2028 / U+2029 are outside both control ranges it strips.
/// </summary>
[StepProvider]
file sealed class StubMultiLineMessageProvider : DiffStubProviderBase, IStepDiffRenderer
{
    /// <summary>
    /// U+2028 LINE SEPARATOR, built from its code point for the same reason
    /// <see cref="StubEscapeInMessageProvider.Esc"/> is.
    /// </summary>
    internal static readonly string LineSeparator = new((char)0x2028, 1);

    /// <summary>U+2029 PARAGRAPH SEPARATOR, for the same reasons.</summary>
    internal static readonly string ParagraphSeparator = new((char)0x2029, 1);

    /// <summary>U+0085 NEXT LINE — a C1 control, so the sanitiser strips it independently.</summary>
    internal static readonly string NextLine = new((char)0x85, 1);

    /// <summary>
    /// The DISTINCT words the message is composed from, in order.  The flatten row asserts on
    /// each of them individually, so it proves the message SURVIVES flattening rather than merely
    /// that it was truncated at the first separator — and it reads this array rather than
    /// re-listing the words, so a word added here cannot leave a stale expectation behind.
    /// </summary>
    internal static readonly string[] Words =
        { "boom", "alpha", "bravo", "charlie", "delta", "echo", "foxtrot" };

    /// <summary>
    /// One separator between each consecutive pair of <see cref="Words"/> — every family that
    /// could split the composed line: CRLF (which must collapse as ONE unit, not two), a bare LF,
    /// a bare CR, NEL, LS and PS.
    /// </summary>
    private static readonly string[] s_separators =
        { "\r\n", "\n", "\r", NextLine, LineSeparator, ParagraphSeparator };

    /// <summary>
    /// <see cref="Words"/> joined by <see cref="s_separators"/>, in order — one message carrying
    /// every separator family at once.
    /// </summary>
    internal static readonly string Message = string.Concat(
        Words.Select((word, index) => index == 0 ? word : s_separators[index - 1] + word));

    public override StepKindId Kind => new("stub", "multiline-message");

    public bool CanRender(JsonElement observation) => true;

    public string? RenderDiff(JsonElement observation) => throw new System.InvalidTimeZoneException(Message);
}

/// <summary>
/// The REAL fault mode, not a synthetic throw: <c>CanRender</c> reads a string value OUT of the
/// observation, which is what <c>DbAssertPostgresProvider.CanRender</c> does through its
/// <c>TryReadColumnDiff</c> helper (<c>column = columnEl.GetString()</c>).  Handed an observation
/// whose <c>column</c> carries a lone / unpaired UTF-16 surrogate — which a SYSTEM UNDER TEST can
/// produce, and which <c>JsonDocument.Parse</c> accepts — <c>GetString()</c> throws
/// <see cref="System.InvalidOperationException"/>.  So this stub is a CORRECT renderer that throws
/// on ONE payload, which is the case <c>ScenarioRunner.ReportDiffRendererFault</c>'s two-armed
/// attribution exists for and which no deliberately-throwing stub can exercise.
/// </summary>
[StepProvider]
file sealed class StubColumnReadingCanRenderProvider : DiffStubProviderBase, IStepDiffRenderer
{
    public override StepKindId Kind => new("stub", "column-reading-canrender");

    public bool CanRender(JsonElement observation) =>
        observation.ValueKind == JsonValueKind.Object
        && observation.TryGetProperty("column", out var column)
        && column.ValueKind == JsonValueKind.String
        && column.GetString() is not null;

    public string? RenderDiff(JsonElement observation) => "unreachable";
}

// ── Tests ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Non-docker tests for issue #485: a throwing <see cref="IStepDiffRenderer"/> is contained at
/// <c>ScenarioRunner.BuildDiffLookup</c> — the single bridge between the decoupled reporting layer
/// and the SDK type — so every report artefact still lands and only that step's diff is absent.
/// </summary>
public sealed class DiffRendererThrowContainmentTests
{
    private static readonly System.Reflection.Assembly[] ProviderAssemblies =
        { typeof(DiffRendererThrowContainmentTests).Assembly };

    private const string StepId = "assert-order";

    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    private static JsonElement Observation()
    {
        using var doc = JsonDocument.Parse("""{"column":"status","expected":"SHIPPED","actual":"PENDING"}""");
        return doc.RootElement.Clone();
    }

    private const string ScenarioId = "order-flow";

    /// <summary>
    /// A COMPLETE one-scenario buffer whose <paramref name="failedStepKinds"/> each fail with a
    /// structured observation — complete because the HTML and JUnit renderers build a scenario
    /// model, so a buffer of bare step events produces artefacts that render nothing and would
    /// make "the artefact survived" a vacuous assertion.
    /// </summary>
    private static string[] FailingScenario(params string[] failedStepKinds)
    {
        const string RunId = "run-1";

        var lines = new List<string>
        {
            Line(new ScenarioStartedEvent
            {
                RunId = RunId,
                ScenarioId = ScenarioId,
                File = "order-flow.e2e.yaml",
            }),
        };

        for (var i = 0; i < failedStepKinds.Length; i++)
        {
            var stepId = failedStepKinds.Length == 1 ? StepId : $"step-{i}";
            lines.Add(Line(new StepStartedEvent { RunId = RunId, StepId = stepId, Kind = failedStepKinds[i] }));
            lines.Add(Line(new StepCompletedEvent
            {
                RunId = RunId,
                StepId = stepId,
                Verdict = Verdict.Fail,
                DurationMs = 12,
                Observation = Observation(),
            }));
        }

        lines.Add(Line(new ScenarioCompletedEvent
        {
            RunId = RunId,
            ScenarioId = ScenarioId,
            Verdict = Verdict.Fail,
            Counts = new VerdictCounts { Fail = failedStepKinds.Length },
        }));

        return lines.ToArray();
    }

    /// <summary>One failed step of <paramref name="kind"/>, in a complete scenario.</summary>
    private static string[] FailedStep(string kind) => FailingScenario(kind);

    /// <summary>
    /// The placeholder written into the observation's <c>column</c> value, replaced in the
    /// SERIALISED lines by the JSON escape for a lone high surrogate.
    /// </summary>
    private const string SurrogatePlaceholder = "LONESURROGATEHERE";

    /// <summary>
    /// The same complete one-step scenario <see cref="FailedStep"/> builds, except that the
    /// observation's <c>column</c> value carries a lone / unpaired UTF-16 surrogate — the shape
    /// a system under test can put into an observation.
    /// </summary>
    /// <remarks>
    /// The escape is substituted into the SERIALISED line rather than into the
    /// <see cref="JsonElement"/> beforehand, so the row does not depend on how
    /// <c>System.Text.Json</c> chooses to re-encode an already-parsed element.  What the
    /// renderers are handed is a line of JSON text carrying the six characters
    /// <c>\ud800</c> — what an events file from such a run holds:
    /// <c>JsonDocument.Parse</c> accepts it, the value lands as an element of kind
    /// <see cref="JsonValueKind.String"/>, and only <see cref="JsonElement.GetString"/>
    /// throws on it.
    /// </remarks>
    private static string[] FailedStepWithUnreadableObservation(string kind)
    {
        const string RunId = "run-1";

        using var doc = JsonDocument.Parse(
            "{\"column\":\"" + SurrogatePlaceholder
            + "\",\"expected\":\"SHIPPED\",\"actual\":\"PENDING\"}");
        var observation = doc.RootElement.Clone();

        var lines = new[]
        {
            Line(new ScenarioStartedEvent
            {
                RunId = RunId,
                ScenarioId = ScenarioId,
                File = "order-flow.e2e.yaml",
            }),
            Line(new StepStartedEvent { RunId = RunId, StepId = StepId, Kind = kind }),
            Line(new StepCompletedEvent
            {
                RunId = RunId,
                StepId = StepId,
                Verdict = Verdict.Fail,
                DurationMs = 12,
                Observation = observation,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = RunId,
                ScenarioId = ScenarioId,
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
            }),
        };

        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].Replace(
                SurrogatePlaceholder, "\\ud800", System.StringComparison.Ordinal);
        }

        return lines;
    }

    /// <summary>
    /// Renders <paramref name="lines"/> through the REAL closure and writes all three file
    /// artefacts, returning the terminal output, the diagnostics text and the artefact contents.
    /// </summary>
    private static (string Terminal, string Diagnostics, string Html, string Junit, string Events)
        RenderEverything(string[] lines)
    {
        var directory = Directory.CreateTempSubdirectory("vouchfx-diff-throw-");
        try
        {
            var htmlPath = Path.Combine(directory.FullName, "report.html");
            var junitPath = Path.Combine(directory.FullName, "results.xml");
            var eventsPath = Path.Combine(directory.FullName, "events.jsonl");

            var registry = StepKindRegistry.BuildAndFreeze(ProviderAssemblies);

            // The diagnostics sink is kept SEPARATE from the terminal writer here only so the two
            // can be asserted apart. Production passes the one `output` writer for both, which is
            // the point of the closure taking a sink at all.
            using var terminal = new StringWriter();
            using var diagnostics = new StringWriter();

            var diffLookup = ScenarioRunner.BuildParallelDiffLookup(registry, diagnostics);

            // Neither call may throw. That is the whole issue: the terminal render sits AHEAD of
            // the file-report writes, so a throw here used to take all three artefacts with it.
            TerminalRenderer.Render(lines, terminal, diffLookup);
            FileReportWriter.WriteFileReports(
                lines, diffLookup, htmlPath, junitPath, diagnostics, eventsPath: eventsPath);

            return (
                terminal.ToString(),
                diagnostics.ToString(),
                File.ReadAllText(htmlPath),
                File.ReadAllText(junitPath),
                File.ReadAllText(eventsPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ── RenderDiff throws ─────────────────────────────────────────────────────

    /// <summary>
    /// A <c>RenderDiff</c> that throws <see cref="System.NullReferenceException"/> — outside both
    /// renderers' filtered catches — still leaves the terminal output and all three file
    /// artefacts intact; only the diff is missing.
    /// </summary>
    [Fact]
    public void RenderDiffThrows_TerminalAndEveryArtefactStillWritten()
    {
        var (terminal, diagnostics, html, junit, events) =
            RenderEverything(FailedStep("stub.throwing-renderdiff"));

        Assert.Contains(StepId, terminal, System.StringComparison.Ordinal);
        Assert.Contains("FAIL", terminal, System.StringComparison.Ordinal);

        // The artefacts exist AND are complete: the HTML renders the step and closes its
        // document, the JUnit XML records the scenario as a failing test case, and the events
        // stream carries every buffered line back out.
        Assert.Contains(StepId, html, System.StringComparison.Ordinal);
        Assert.Contains("</html>", html, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ScenarioId, junit, System.StringComparison.Ordinal);
        Assert.Contains("</testsuites>", junit, System.StringComparison.Ordinal);
        Assert.Equal(4, events.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length);

        // Only the diff is absent — the HTML carries no diff fragment for this step.
        Assert.DoesNotContain("class=\"diff\"", html, System.StringComparison.Ordinal);

        // The fault is NAMED, and named where a non-verdict-affecting diagnostic belongs.
        Assert.Contains("stub.throwing-renderdiff", diagnostics, System.StringComparison.Ordinal);
        Assert.Contains(nameof(IStepDiffRenderer.RenderDiff), diagnostics, System.StringComparison.Ordinal);
        Assert.Contains(nameof(System.NullReferenceException), diagnostics, System.StringComparison.Ordinal);

        // The provider is named by Type.FullName, not by the bare type name — an author with two
        // same-named types in different namespaces must be able to tell them apart.
        Assert.Contains(
            typeof(DiffRendererThrowContainmentTests).Namespace!,
            diagnostics,
            System.StringComparison.Ordinal);
    }

    // ── CanRender throws ──────────────────────────────────────────────────────

    /// <summary>
    /// The same containment for <c>CanRender</c>, guarded separately because it is a different
    /// signal: a renderer broken for every payload rather than for one.
    /// </summary>
    [Fact]
    public void CanRenderThrows_TerminalAndEveryArtefactStillWritten()
    {
        var (terminal, diagnostics, html, junit, events) =
            RenderEverything(FailedStep("stub.throwing-canrender"));

        Assert.Contains(StepId, terminal, System.StringComparison.Ordinal);
        Assert.Contains("FAIL", terminal, System.StringComparison.Ordinal);
        Assert.Contains(StepId, html, System.StringComparison.Ordinal);
        Assert.Contains("</html>", html, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</testsuites>", junit, System.StringComparison.Ordinal);
        Assert.Equal(4, events.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("class=\"diff\"", html, System.StringComparison.Ordinal);

        // The member named is CanRender, NOT RenderDiff — the two are reported apart, and
        // `RenderDiff` must not appear at all (this stub's RenderDiff is never reached).
        Assert.Contains("stub.throwing-canrender", diagnostics, System.StringComparison.Ordinal);
        Assert.Contains(nameof(IStepDiffRenderer.CanRender), diagnostics, System.StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(IStepDiffRenderer.RenderDiff), diagnostics, System.StringComparison.Ordinal);
        Assert.Contains(nameof(System.FormatException), diagnostics, System.StringComparison.Ordinal);
        Assert.Contains(StubThrowingCanRenderProvider.Message, diagnostics, System.StringComparison.Ordinal);
    }

    // ── CanRender throws on an UNREADABLE observation ──────────────────────

    /// <summary>
    /// The fault mode the two-armed attribution exists for, driven the way it actually arises
    /// rather than by a stub that throws on purpose: a CORRECT <c>CanRender</c> reads the
    /// observation's <c>column</c> with <see cref="JsonElement.GetString"/> — as
    /// <c>DbAssertPostgresProvider</c> does — and the value carries a lone UTF-16 surrogate,
    /// which a system under test can produce.  The diagnostic must NOT blame the provider
    /// outright; it must say the seam cannot tell provider defect from unreadable data apart.
    /// </summary>
    [Fact]
    public void CanRenderThrowsReadingAnUnreadableObservation_AttributionNamesBothPossibilities()
    {
        var (terminal, diagnostics, html, junit, events) =
            RenderEverything(FailedStepWithUnreadableObservation("stub.column-reading-canrender"));

        // Contained exactly as a synthetic throw is: every artefact lands, only the diff is gone.
        Assert.Contains(StepId, terminal, System.StringComparison.Ordinal);
        Assert.Contains(StepId, html, System.StringComparison.Ordinal);
        Assert.Contains("</html>", html, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</testsuites>", junit, System.StringComparison.Ordinal);
        Assert.Equal(
            4, events.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("class=\"diff\"", html, System.StringComparison.Ordinal);

        // The fault is named, and the exception is the one GetString() raises on a lone surrogate.
        Assert.Contains("stub.column-reading-canrender", diagnostics, System.StringComparison.Ordinal);
        Assert.Contains(nameof(IStepDiffRenderer.CanRender), diagnostics, System.StringComparison.Ordinal);
        Assert.Contains(
            nameof(System.InvalidOperationException), diagnostics, System.StringComparison.Ordinal);

        // THE POINT OF THE ROW: the two-armed wording, and not the unconditional one.
        Assert.Contains(
            "this seam cannot tell the two apart", diagnostics, System.StringComparison.Ordinal);
        Assert.Contains(
            "either a defect in the provider", diagnostics, System.StringComparison.Ordinal);
        Assert.DoesNotContain(
            "This is a defect in the provider", diagnostics, System.StringComparison.Ordinal);
    }

    // ── Deduplication ─────────────────────────────────────────────────────────

    /// <summary>
    /// A renderer that throws on every failed step reports ONCE per run, not once per step: the
    /// first line carries everything an author needs, and N copies would bury the report the
    /// guard exists to preserve.
    /// </summary>
    [Fact]
    public void RenderDiffThrowsOnEveryStep_ReportsExactlyOneFaultLine()
    {
        const int StepCount = 7;

        var kinds = new string[StepCount];
        System.Array.Fill(kinds, "stub.throwing-renderdiff");

        var (terminal, diagnostics, _, _, _) = RenderEverything(FailingScenario(kinds));

        // Every step still rendered…
        for (var i = 0; i < StepCount; i++)
        {
            Assert.Contains($"step-{i}", terminal, System.StringComparison.Ordinal);
        }

        // …and exactly one fault line was emitted, even though the closure threw 14 times
        // (7 steps × the terminal render + the HTML render).
        Assert.Equal(1, CountOccurrences(diagnostics, nameof(System.NullReferenceException)));
    }

    /// <summary>
    /// The dedup key includes the MEMBER and the exception type, so two genuinely different
    /// faults are both reported — deduplication must not collapse distinct defects.
    /// </summary>
    [Fact]
    public void TwoDifferentFaults_AreBothReported()
    {
        var (_, diagnostics, _, _, _) = RenderEverything(
            FailingScenario("stub.throwing-renderdiff", "stub.throwing-canrender"));

        Assert.Equal(1, CountOccurrences(diagnostics, nameof(System.NullReferenceException)));
        Assert.Equal(1, CountOccurrences(diagnostics, nameof(System.FormatException)));
    }

    // ── No regression for a well-behaved renderer ─────────────────────────────

    /// <summary>
    /// A renderer that does not throw renders exactly as it did before the guard, and writes no
    /// diagnostic — the guard must be invisible on the healthy path.
    /// </summary>
    [Fact]
    public void HealthyRenderer_StillRendersItsDiffAndReportsNothing()
    {
        var (terminal, diagnostics, html, _, _) = RenderEverything(FailedStep("stub.healthy-renderer"));

        Assert.Contains(StubHealthyRendererProvider.Diff, terminal, System.StringComparison.Ordinal);
        Assert.Contains(StubHealthyRendererProvider.Diff, html, System.StringComparison.Ordinal);
        Assert.Contains("class=\"diff\"", html, System.StringComparison.Ordinal);
        Assert.Equal(string.Empty, diagnostics);
    }

    /// <summary>
    /// A provider with no <see cref="IStepDiffRenderer"/> at all is unchanged too: no diff, and no
    /// diagnostic — the guard reports a THROW, never an absence.
    /// </summary>
    [Fact]
    public void ProviderWithoutADiffRenderer_ReportsNothing()
    {
        var (terminal, diagnostics, html, _, _) = RenderEverything(FailedStep("stub.no-renderer"));

        Assert.Contains(StepId, terminal, System.StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"diff\"", html, System.StringComparison.Ordinal);
        Assert.Equal(string.Empty, diagnostics);
    }

    // ── The rendered report is otherwise IDENTICAL ────────────────────────────

    /// <summary>
    /// "Only that step's diff is absent", asserted as an equality rather than as a collection of
    /// <c>Contains</c>: the terminal output and the HTML produced with a THROWING renderer are
    /// byte-identical to those produced for a provider that implements no renderer at all.
    /// Nothing about the run's reported outcome moves — which is the property #485 is about, the
    /// verdict having been fixed long before this code runs.
    /// </summary>
    [Fact]
    public void ThrowingRenderer_ProducesTheSameReportAsNoRendererAtAll()
    {
        // Same step id and run id in both, so the only difference is the resolved provider.
        var throwing = RenderEverything(FailedStep("stub.throwing-renderdiff"));
        var absent = RenderEverything(FailedStep("stub.no-renderer"));

        // Neither renderer echoes the step KIND, so these compare equal outright.
        Assert.Equal(absent.Terminal, throwing.Terminal);
        Assert.Equal(absent.Html, throwing.Html);
        Assert.Equal(absent.Junit, throwing.Junit);

        // The `--events` stream is the input buffer echoed verbatim, so it differs by exactly the
        // step-started `kind` the two scenarios were built with and by nothing else — asserted as
        // a line count plus that difference rather than as equality, which would be a false
        // failure rather than evidence of a lost artefact.
        Assert.Equal(
            absent.Events.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length,
            throwing.Events.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(
            absent.Events.Replace("stub.no-renderer", "stub.throwing-renderdiff", System.StringComparison.Ordinal),
            throwing.Events);

        // The ONLY difference is the diagnostic, and it is on the diagnostics channel.
        Assert.Equal(string.Empty, absent.Diagnostics);
        Assert.NotEqual(string.Empty, throwing.Diagnostics);
    }

    // ── §17 / issue #266 Item 4: the provider-authored message is display-sanitised ──

    /// <summary>
    /// The exception MESSAGE is provider-authored text written straight to a human-facing writer —
    /// it bypasses <c>TerminalRenderer</c>'s <c>GetStr</c> choke — so the composed line passes
    /// through <see cref="DisplaySanitiser.SanitiseForDisplay"/> at the write site.  A message
    /// carrying a raw ESC/CSI sequence must not reach the terminal able to clear the screen.
    /// </summary>
    [Fact]
    public void ProviderExceptionMessage_IsDisplaySanitisedAtTheWriteSite()
    {
        var (_, diagnostics, _, _, _) = RenderEverything(FailedStep("stub.escaping-renderdiff"));

        // The fault is still reported and still readable…
        Assert.Contains("stub.escaping-renderdiff", diagnostics, System.StringComparison.Ordinal);
        Assert.Contains("boom", diagnostics, System.StringComparison.Ordinal);
        Assert.Contains("cleared", diagnostics, System.StringComparison.Ordinal);

        // …with no raw ESC and no raw BEL byte anywhere in it.
        Assert.DoesNotContain(StubEscapeInMessageProvider.Esc, diagnostics, System.StringComparison.Ordinal);
        Assert.DoesNotContain(StubEscapeInMessageProvider.Bel, diagnostics, System.StringComparison.Ordinal);
    }

    // ── The fault line is ONE line, whatever the provider's message contains ──

    /// <summary>
    /// The published shape of this diagnostic is one line per <c>(kind, member, exception type)</c>
    /// per run, and <c>ex.Message</c> is provider-authored text that can carry line separators.  So
    /// the message is flattened at the write site before the composed line is sanitised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SIBLING OF <see cref="ProviderExceptionMessage_IsDisplaySanitisedAtTheWriteSite"/> rather
    /// than an extra assertion inside it, because the two pin different claims and a reader must be
    /// able to tell from the failure alone which one broke: that row is control-character
    /// SCRUBBING, this row is line FLATTENING.  Nothing else in the file covers this one —
    /// <c>DisplaySanitiser</c> preserves <c>\n</c> on purpose, so the dedup rows' single fault line
    /// would still be single if the message split it across five.
    /// </para>
    /// <para>
    /// TWO ASSERTIONS, because one separator family is invisible to the other.  The line count
    /// catches LF and CRLF, which is what a reader, a terminal and every <c>Split('\n')</c>
    /// downstream mean by "one line".  U+2028 / U+2029 cannot move that count, and the sanitiser
    /// cannot reach them either (they are outside <c>0x00-0x1F</c> and <c>0x7F-0x9F</c>), so their
    /// absence is asserted directly — it is the only evidence that the flatten covered them.
    /// </para>
    /// </remarks>
    [Fact]
    public void ProviderExceptionMessageWithLineSeparators_IsFlattenedToOneLine()
    {
        var (_, diagnostics, _, _, _) = RenderEverything(FailedStep("stub.multiline-message"));

        var lines = diagnostics.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

        Assert.True(
            lines.Length == 1,
            $"The fault diagnostic came out as {lines.Length} lines, not one. A provider's "
            + "exception message carried line separators and reached the writer unflattened, so "
            + "the once-per-fault line this method publishes is no longer one line: it interleaves "
            + "with the report the renderer is streaming, and anything downstream that reads the "
            + "diagnostics sink a line at a time now sees fragments. DisplaySanitiser cannot fix "
            + "this - it PRESERVES \\n deliberately - so the flatten at "
            + "ScenarioRunner.ReportDiffRendererFault is the only thing that does. The lines "
            + "produced were: "
            + string.Join(" | ", lines));

        // The message still says what it said - flattening is not truncation. The expectation is
        // READ FROM the stub's own word array rather than re-listed here, so adding a separator
        // and a word to the stub cannot weaken this row silently. It is not circular: the words
        // are compared against the RENDERED diagnostics, which the production flatten and
        // sanitise produced, not against the stub's own composed message.
        foreach (var fragment in StubMultiLineMessageProvider.Words)
        {
            Assert.Contains(fragment, diagnostics, System.StringComparison.Ordinal);
        }

        // The two separators no line count can see and no sanitiser strips. These two are named
        // rather than swept from the stub's separator array, because the array's CR / LF / CRLF
        // entries CANNOT be asserted absent: `diagnostics` ends with the WriteLine's own line
        // terminator, so a blanket absence assertion over that array would be false on a correct
        // implementation.
        Assert.DoesNotContain(
            StubMultiLineMessageProvider.LineSeparator, diagnostics, System.StringComparison.Ordinal);
        Assert.DoesNotContain(
            StubMultiLineMessageProvider.ParagraphSeparator, diagnostics, System.StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
