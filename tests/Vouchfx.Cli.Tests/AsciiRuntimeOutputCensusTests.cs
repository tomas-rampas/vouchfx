// Vouchfx.Cli.Tests - issue #379: the CLI's and the engine's RUNTIME OUTPUT strings are ASCII.
//
// THE DEFECT, measured differentially inside one trx during the slice-F verification. The same
// security-confirmation line, written from one source string, came out two ways:
//
//     in-process row : client identity resolved — the broker answered...
//     CLI subprocess : client identity resolved - the broker answered...
//
// The first row carries a REAL em dash and the second a real hyphen - this comment is
// trivia, so the gate below cannot see it, and writing both rows in ASCII (as the first
// draft of this file did) left two byte-identical lines labelled as differing.
//
// The child process degrades non-ASCII through Console.OutputEncoding, which on Windows defaults to
// the active codepage rather than UTF-8. An em dash with no representation there best-fit-maps to a
// hyphen - not to the usual '?', which is what makes it invisible.
//
// IMPACT TODAY: none. IMPACT LATENT: a mystifying assertion failure. Any future assertion on CLI
// output that happens to span an em dash fails on such a host with a diff between two strings that
// render identically in a terminal, and the cause is nowhere near the assertion. Separately, this is
// USER-VISIBLE output: a run piped to a CI log keeps the mangling in the artefact.
//
// THE APPROVED FIX IS OPTION 2 of the issue: keep the strings ASCII, rather than setting
// Console.OutputEncoding (option 1), which trades one mangling for another on a terminal that
// cannot render UTF-8 and has to be reasoned about separately for redirected output. The em dashes
// were stylistic; a tool whose output lands in CI logs of unknown encoding should not depend on
// them.
//
// WHY A SOURCE CENSUS. The defect is a property of every output string, present and future, and no
// behavioural test can cover strings nobody has written yet. This file is the gate that keeps the
// class closed: it parses the boundary's C# with Roslyn and asserts that no literal token carries a
// non-ASCII character.
//
// ROSLYN, NOT A REGEX. The distinction this gate has to make - literal versus comment - is exactly
// the one a regex cannot make reliably over verbatim strings, raw strings, interpolated holes and
// `//` inside a URL. Roslyn's lexer answers it exactly: comments and XML documentation are TRIVIA
// and never appear as tokens, so the boundary between "prose, which keeps its em dashes deliberately"
// and "output, which may not" is drawn by the compiler rather than by this file's cleverness.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Issue #379: no string or character literal in the CLI or engine runtime-output surface contains
/// a non-ASCII character, so nothing this tool prints can be degraded by a host console codepage.
/// </summary>
public sealed class AsciiRuntimeOutputCensusTests
{
    /// <summary>
    /// THE BOUNDARY, and it is a decision rather than a discovery - stated here because a census
    /// whose scope is implicit stops meaning anything the first time someone widens the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>IN: the CLI and every engine assembly.</strong> These are where the strings a user
    /// reads come from - <c>--help</c> text, validation and schema diagnostics, orchestration and
    /// security errors, the scenario-level causes that reach the §14 event stream and every
    /// renderer. All of it is written by the engine, all of it can end up on a console or in a CI
    /// log, and none of it needs a typographic character to say what it says.
    /// </para>
    /// <para>
    /// <strong>OUT: <c>src/Providers</c> and <c>src/Sdk</c>, and the reason is measured, not
    /// squeamish.</strong> A provider's literals are dominated by EMITTED CSX: the
    /// <c>RequiredHelpers</c> arrays and the <c>$$"""..."""</c> step bodies are generated C# SOURCE,
    /// one string literal per line, comments included. MEASURED on the pre-fix tree: of 523
    /// offending literal tokens across <c>src/</c>, 396 were in those two trees; those 396 tokens
    /// span 498 offending LINES (a multi-line raw string is one token over many lines), and 412 of
    /// those lines are comment lines within generated source, which no console ever prints.
    /// Including the trees would mean teaching this gate to tell a generated comment
    /// from generated code - the same regex-shaped judgement the Roslyn approach exists to avoid -
    /// and it would move the SDK helper <c>Source</c> constants, whose values are hash-pinned by
    /// <c>SdkContractFreezeTests</c>.
    /// </para>
    /// <para>
    /// <strong>What that exclusion leaves open is stated STRUCTURALLY, and deliberately so:</strong>
    /// everything under <c>src/Providers</c> and <c>src/Sdk</c> is outside this gate, and some of
    /// it is genuinely runtime-reachable and degrades on exactly the hosts #379 measured. The full
    /// inventory lives in issue #472 and is maintained there, NOT here.
    /// </para>
    /// <para>
    /// <strong>Why the boundary is stated as a rule rather than as a list.</strong> The first
    /// version of this remark enumerated two classes it had seen — the diff-renderer tables and the
    /// schema <c>description</c> strings — and the review found a third it had not (provider
    /// validator and observation literals, which travel the identical
    /// <c>ValidationFailure</c>-to-scenario-cause channel as the engine strings this gate now
    /// covers). An enumeration in a comment is a claim of completeness that nothing checks and that
    /// decays the moment a provider adds a literal; a structural statement cannot be incomplete.
    /// A reader who needs the list should open #472, where a wrong list is a visible defect rather
    /// than a quiet one.
    /// </para>
    /// <para>
    /// <strong>OUT: test projects</strong>, which assert on Unicode deliberately - astral-plane
    /// surrogate fixtures, hostile-input strings, and the assertion-failure prose that is written
    /// for a human reading a test report rather than for a console.
    /// </para>
    /// </remarks>
    private static readonly string[] s_boundary =
    {
        Path.Combine("src", "Cli"),
        Path.Combine("src", "Engine"),
    };

    /// <summary>
    /// Every token kind that carries author-written character data. Comments and XML documentation
    /// are trivia in Roslyn's model and are therefore absent from this list by construction, not by
    /// filtering - which is the whole reason the gate is written this way.
    /// </summary>
    private static readonly SyntaxKind[] s_literalKinds =
    {
        SyntaxKind.StringLiteralToken,
        SyntaxKind.Utf8StringLiteralToken,
        SyntaxKind.SingleLineRawStringLiteralToken,
        SyntaxKind.MultiLineRawStringLiteralToken,
        SyntaxKind.Utf8SingleLineRawStringLiteralToken,
        SyntaxKind.Utf8MultiLineRawStringLiteralToken,
        SyntaxKind.InterpolatedStringTextToken,
        SyntaxKind.CharacterLiteralToken,
    };

    /// <summary>
    /// The ASCII replacements this repository uses, quoted in the failure message so the fix is
    /// stated rather than left to be guessed at differently each time.
    /// </summary>
    private const string ReplacementGuidance =
        "  em dash / en dash  ->  '-'  (the surrounding spaces already read as a dash)\n"
        + "  ellipsis           ->  '...'\n"
        + "  right arrow        ->  '->'\n"
        + "  section sign       ->  'section ' (e.g. '(section 12.1)', '(DSL section 3.2)')\n"
        + "  curly quotes       ->  the straight ASCII quote\n";

    /// <summary>
    /// No literal in the boundary carries a non-ASCII character.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reports EVERY offender, not the first.</strong> A gate that fails on one site at a
    /// time turns a sweep into as many edit-build-run cycles as there are strings, which is how a
    /// gate comes to be suppressed rather than satisfied.
    /// </para>
    /// <para>
    /// <strong>The file count is asserted non-zero first</strong>, because "no offenders" is also
    /// what a census that found no FILES reports - the failure mode every source-census test has,
    /// reached here by a directory rename. A census matching nothing passes for free, and this is
    /// the check that stops it.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoRuntimeOutputLiteral_ContainsANonAsciiCharacter()
    {
        var repoRoot = RepositoryRoot();
        var files = BoundaryFiles(repoRoot).ToList();

        Assert.True(
            files.Count > 0,
            "This census found no .cs files under " + string.Join(" or ", s_boundary)
            + ". Most likely a project moved. Point the boundary at where it went rather than "
            + "leaving it matching nothing: a census that reads no files reports no offenders and "
            + "passes for free, which is the one way this file can stop guarding anything without "
            + "saying so.");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(
                SourceText.From(text),
                new CSharpParseOptions(LanguageVersion.Preview),
                path: file);

            foreach (var token in tree.GetRoot().DescendantTokens())
            {
                if (Array.IndexOf(s_literalKinds, token.Kind()) < 0)
                {
                    continue;
                }

                // token.Text (the SOURCE SPELLING) and not token.ValueText (the decoded value),
                // and the trade-off is exact rather than incidental.
                //
                // WHAT THIS COSTS: a literal written as an ESCAPE - "\u2014" - is invisible to this
                // gate and still emits an em dash at run time. That hole is real and is accepted.
                //
                // WHAT IT BUYS: ValueText would redden SecurityConfigurationAccessor's own
                // sanitiser constants, which name control characters as '\u0080' / '\u009f'
                // precisely because that file's rule is that a control character is written as an
                // escape and never as a raw byte. Those are correct code doing the right thing, and
                // a gate that fails them teaches the next author to suppress the gate.
                //
                // The defect #379 records is a TYPED character - somebody wrote an em dash because
                // it reads nicely - and that is what a source-spelling check catches. An escape is a
                // deliberate act by an author who already knows what the character is; this gate
                // does not try to stop it, and saying so here is what stops the omission being read
                // as coverage.
                var found = token.Text.Where(c => c > 0x7F).Distinct().ToArray();
                if (found.Length == 0)
                {
                    continue;
                }

                var line = tree.GetLineSpan(token.Span).StartLinePosition.Line + 1;
                var codes = string.Join(
                    ", ",
                    found.Select(c =>
                        "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture)
                        + " '" + c + "'"));

                offenders.Add(
                    $"  {Path.GetRelativePath(repoRoot, file)}:{line}  {codes}\n"
                    + $"      {Excerpt(token.Text)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} literal(s) in the CLI / engine runtime-output surface contain "
            + "non-ASCII characters (issue #379).\n"
            + "\n"
            + "These strings reach a console, a CI log, the section 14 event stream or a rendered report. "
            + "On a Windows host with a legacy console codepage the child process degrades them "
            + "through Console.OutputEncoding - an em dash best-fit-maps to a hyphen, silently - so "
            + "an assertion spanning one fails with a diff between two strings that look identical. "
            + "Use the ASCII spelling:\n"
            + "\n"
            + ReplacementGuidance
            + "\n"
            + "COMMENTS AND XML DOCUMENTATION ARE NOT AFFECTED and must not be changed to satisfy "
            + "this gate: they are Roslyn trivia, never tokens, so nothing below is one. This "
            + "repository writes British English prose with typographic punctuation deliberately.\n"
            + "\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// One line of context for a failure message, with newlines made visible so a multi-line raw
    /// string does not reformat the report around itself.
    /// </summary>
    private static string Excerpt(string raw)
    {
        var flattened = raw.Replace("\r", "\\r", StringComparison.Ordinal)
                           .Replace("\n", "\\n", StringComparison.Ordinal);

        return flattened.Length <= 160 ? flattened : flattened[..160] + "...";
    }

    /// <summary>
    /// Every <c>.cs</c> file inside the boundary, excluding build output.
    /// </summary>
    /// <remarks>
    /// <c>obj</c> and <c>bin</c> are excluded because they hold GENERATED sources - the
    /// <c>AssemblyAttribute</c> file this repository's csproj files emit among them - which no
    /// author can edit and which this gate has no business failing on.
    /// </remarks>
    private static IEnumerable<string> BoundaryFiles(string repoRoot)
    {
        var obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        var bin = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;

        foreach (var relative in s_boundary)
        {
            var root = Path.Combine(repoRoot, relative);

            // PER-ENTRY, not aggregate. The caller's non-empty check is over the WHOLE census, so
            // with `src/Engine` present and `src/Cli` moved it still sees hundreds of files and
            // passes - and the CLI, which is where issue #379 was actually measured, would have
            // silently left the gate. A skipped root is the one failure a census cannot report by
            // finding nothing.
            Assert.True(
                Directory.Exists(root),
                $"Boundary root '{relative}' does not exist under '{repoRoot}'. This census reads "
                + "the tree directly, so a moved or renamed project must move this boundary with "
                + "it. Continuing past a missing root would drop that project out of the gate "
                + "while the remaining roots kept the test green.");

            var found = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (!file.Contains(obj, StringComparison.Ordinal)
                    && !file.Contains(bin, StringComparison.Ordinal))
                {
                    found++;
                    yield return file;
                }
            }

            Assert.True(
                found > 0,
                $"Boundary root '{relative}' exists but contains no .cs file outside obj/ and "
                + "bin/. An empty root contributes nothing and reports nothing; treat it as a "
                + "moved project rather than as a clean one.");
        }
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the solution file - the same shape
    /// <c>ScenarioCoreDeclarationCensusTests</c> uses to read engine source directly.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
