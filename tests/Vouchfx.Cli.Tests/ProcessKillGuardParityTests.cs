// Vouchfx.Cli.Tests — the two copies of KillTreeQuietly carry the SAME catch filter (#481).
//
// WHY THERE ARE TWO COPIES AT ALL
// ───────────────────────────────
// `Vouchfx.TestSupport.ChildProcess.KillTreeQuietly` is the one guarded tree-kill every test
// assembly's child-process launch site calls from its `finally` (#378, #475). Vouchfx.TestSupport
// is `IsPackable=false` and is referenced only by test projects, so PRODUCT code cannot call it —
// and #481 gave `Vouchfx.Cli.Selection.SystemProcessRunner` a child of its own to reclaim when its
// time budget expires. The copy is forced by the assembly graph, not chosen.
//
// WHY THAT NEEDS A GATE
// ─────────────────────
// The original's own header states the risk precisely, and it is the reason this file exists:
//
//     "Two copies of a guard whose whole value is that its catch filter is exhaustive is how the
//      filter drifts: the round-1 filters in #378 already missed AggregateException once, and a
//      divergent copy is that miss made permanent in one lane only."
//
// The failure mode is silent in BOTH directions. A filter that is too narrow lets a teardown
// exception escape a `finally` and replace the real failure with a misattributed one — the exact
// misattribution #378 closed. A filter that is too wide swallows a genuine defect. Neither shows
// up as a failing test in the lane that drifted, because the offending exception is raised only by
// a race that a green run does not take.
//
// WHAT THIS GATE DOES, AND WHAT IT DELIBERATELY DOES NOT
// ─────────────────────────────────────────────────────
// It compares the two ACTUAL SOURCES. It does not assert a hardcoded list of four type names in
// one place and call that parity: a hardcoded list is a third copy, and a third copy of an
// exhaustive filter is one more thing that can drift. The only claim made here is "these two are
// the same", which is the claim that has teeth, and it is made by parsing both files.
//
// It does NOT check that either filter is CORRECT — that judgement lives in the remarks on both
// methods, which name each of the four exceptions `Process.Kill(bool)` documents in the .NET 8
// reference XML, including the fact that the ended-between-check-and-kill race is Win32Exception
// and NOT InvalidOperationException on this runtime. A gate cannot read the reference XML; it can
// only stop the two copies from disagreeing about what was read.
//
// ROSLYN, NOT A REGEX — the house idiom (see ChildProcessKillCallSiteCensusTests and
// AsciiRuntimeOutputCensusTests). Telling a type name in a catch filter from the same words in the
// prose above is exactly what a regex gets wrong, and this file names all four many times over.
// The mechanism is node kind: only a `CatchClauseSyntax`'s declared type and the type-bearing
// PATTERN nodes inside its `when` filter are read. A comment is trivia and a string literal is
// neither of those, so the four names spelled in the prose above are invisible to the gate that
// compares them.
//
// IT READS BOTH FILES AS TEXT, so assembly boundaries are irrelevant — this project needs nothing
// more than the two paths, and Microsoft.CodeAnalysis.CSharp is already referenced here for the
// ASCII census.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>
/// Pins the product and test-support copies of <c>KillTreeQuietly</c> to one catch filter (#481).
/// </summary>
public sealed class ProcessKillGuardParityTests
{
    /// <summary>The method whose catch filter is pinned, spelled once.</summary>
    /// <remarks>
    /// The same identifier <c>ChildProcessKillCallSiteCensusTests.KillMethod</c> matches when it
    /// decides whether a child-process launch sits in a member with a killing <c>finally</c>. The
    /// name is therefore load-bearing in two gates at once: renaming either copy fails this one,
    /// and renaming the product copy additionally makes its launch site read as unguarded.
    /// </remarks>
    private const string KillMethod = "KillTreeQuietly";

    /// <summary>The test-support original.</summary>
    private static readonly string[] s_testSupportCopy =
    {
        "tests", "Vouchfx.TestSupport", "ChildProcess.cs",
    };

    /// <summary>The product copy, forced by Vouchfx.TestSupport being unreferenceable from src.</summary>
    private static readonly string[] s_productCopy =
    {
        "src", "Cli", "Vouchfx.Cli", "Selection", "SystemProcessRunner.cs",
    };

    /// <summary>
    /// The two <c>KillTreeQuietly</c> guards catch exactly the same set of exception types.
    /// </summary>
    [Fact]
    public void BothKillTreeQuietlyCopies_CatchTheSameExceptionTypes()
    {
        var repoRoot = RepositoryRoot();
        var testSupportPath = Path.Combine(repoRoot, Path.Combine(s_testSupportCopy));
        var productPath = Path.Combine(repoRoot, Path.Combine(s_productCopy));

        var testSupport = CaughtExceptionTypes(testSupportPath, repoRoot);
        var product = CaughtExceptionTypes(productPath, repoRoot);

        Assert.True(
            testSupport.SetEquals(product),
            "The two copies of the guarded tree-kill no longer catch the same exceptions.\n\n"
            + $"  {Path.GetRelativePath(repoRoot, testSupportPath)}: {Describe(testSupport)}\n"
            + $"  {Path.GetRelativePath(repoRoot, productPath)}: {Describe(product)}\n\n"
            + $"  only in the test-support copy: {Describe(Except(testSupport, product))}\n"
            + $"  only in the product copy:      {Describe(Except(product, testSupport))}\n\n"
            + "Both guards run inside a `finally`, where ANY escaping exception replaces the real "
            + "failure with a teardown one — the misattribution issue #378 closed — while anything "
            + "swallowed too widely hides a genuine defect. A divergent filter is that miss made "
            + "permanent in ONE LANE ONLY: the drifted copy stays green because the exception it "
            + "no longer handles is raised by a race a passing run does not take, and the round-1 "
            + "filters in #378 already dropped AggregateException once exactly this way.\n\n"
            + "There are two copies because Vouchfx.TestSupport is IsPackable=false and cannot be "
            + "referenced from src/, so the duplication is forced and cannot be refactored away. "
            + "If the filter genuinely needs to change, change BOTH and update the remarks on both "
            + "methods, which document each type against the .NET 8 reference XML for "
            + "Process.Kill(bool).");
    }

    /// <summary>
    /// Every exception type named by the <c>catch</c> clauses of <see cref="KillMethod"/> in one
    /// file: the declared type plus every type tested by the <c>when</c> filter's patterns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are collected because the guard's real filter is the pair. Writing
    /// <c>catch (Exception ex) when (ex is A or B)</c> and <c>catch (A) catch (B)</c> would
    /// produce different sets here, which is correct: they are different filters even though they
    /// catch the same things today, and a gate whose job is "these two files agree" should not be
    /// the place that decides two spellings are equivalent.
    /// </para>
    /// <para>
    /// <strong>The declared half and the filtered half are counted SEPARATELY before the union is
    /// returned.</strong> Not decoration: the first draft of this file read only
    /// <c>TypePatternSyntax</c> and, run against the real sources, found ZERO filter types — it
    /// reported exactly one type per file, the declared <c>Exception</c>. A bare type name in an
    /// <c>is A or B</c> chain does not necessarily parse to a type pattern: without semantics the
    /// parser cannot know whether <c>A</c> names a type or a constant, so it can hand back a
    /// <see cref="ConstantPatternSyntax"/> instead. A union-level floor of two did catch that
    /// draft, but only because each guard happens to be written as ONE <c>catch</c> clause: rewrite
    /// either as two clauses and the same unread filter clears a union floor with two declared
    /// types, leaving the gate green over a filter it never read. Counting the halves apart is what
    /// makes "the filter was actually read" a checked claim rather than a lucky one.
    /// </para>
    /// </remarks>
    private static SortedSet<string> CaughtExceptionTypes(string path, string repoRoot)
    {
        var relative = Path.GetRelativePath(repoRoot, path);

        Assert.True(
            File.Exists(path),
            $"This parity gate is configured to read '{relative}', which does not exist. It "
            + "locates both copies by PATH, so a file move takes one of them out from under it — "
            + "and a gate that cannot find a file it is comparing must fail loudly rather than "
            + "compare nothing against nothing.");

        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);

        // Collected into a list and counted rather than SingleOrDefault'd: a file that declared the
        // method TWICE would make SingleOrDefault throw a bare InvalidOperationException naming no
        // file, jumping straight past the explanatory assertion below.
        var methods = tree
            .GetRoot()
            .DescendantNodes(descendIntoTrivia: false)
            .OfType<MethodDeclarationSyntax>()
            .Where(candidate => candidate.Identifier.ValueText == KillMethod)
            .ToList();

        Assert.True(
            methods.Count == 1,
            $"'{relative}' declares {methods.Count} methods named `{KillMethod}`; this gate reads "
            + "exactly one. Either it was renamed, split or overloaded, or the guard moved "
            + "elsewhere; either way this gate is no longer comparing what it claims to compare, "
            + "and a rename is also what makes a product launch site read as unguarded to "
            + "ChildProcessKillCallSiteCensusTests.");

        var method = methods[0];

        var declared = new SortedSet<string>(StringComparer.Ordinal);
        var filtered = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var clause in method.DescendantNodes(descendIntoTrivia: false).OfType<CatchClauseSyntax>())
        {
            if (clause.Declaration?.Type is { } caught)
            {
                declared.Add(RightmostName(caught));
            }

            if (clause.Filter is null)
            {
                continue;
            }

            foreach (var pattern in clause.Filter.DescendantNodes(descendIntoTrivia: false).OfType<PatternSyntax>())
            {
                if (PatternTypeName(pattern) is { } name)
                {
                    filtered.Add(name);
                }
            }
        }

        // Vacuity guards, one per half. "The two sets are equal" is also what two EMPTY sets
        // report, and — worse, because it looks healthy — what two sets holding nothing but the
        // declared `Exception` report. Both halves must be non-trivially populated before the
        // comparison below means anything.
        Assert.True(
            declared.Count >= 1,
            $"This gate found no declared catch type in `{KillMethod}` in '{relative}'. A bare "
            + "`catch { }` catches everything unconditionally, which is a different guard from the "
            + "one both copies document, and it leaves this gate comparing empty sets that pass "
            + "for free.");

        Assert.True(
            filtered.Count >= 2,
            $"This gate read {filtered.Count} type(s) out of the `when` filter of `{KillMethod}` "
            + $"in '{relative}' ({Describe(filtered)}). The documented shape is an "
            + "`ex is A or B or ...` chain of at least two, so a lower count means the filter was "
            + "rewritten into a shape PatternTypeName does not read — and an unread filter still "
            + "yields two equal, plausible-looking sets of declared types, which is precisely how "
            + "this gate could report itself green while pinning nothing.");

        var union = new SortedSet<string>(declared, StringComparer.Ordinal);
        union.UnionWith(filtered);
        return union;
    }

    /// <summary>
    /// The exception type one pattern tests, or <see langword="null"/> when it tests no type.
    /// </summary>
    /// <remarks>
    /// Every shape a bare type name can take in a pattern is read, because which one the parser
    /// produces is a SYNTACTIC accident this gate must not depend on: without semantics the parser
    /// cannot tell a type from a constant, so <c>ex is Foo</c> can arrive as
    /// <see cref="ConstantPatternSyntax"/> as readily as <see cref="TypePatternSyntax"/>. The
    /// composite patterns (<c>or</c>, <c>and</c>, <c>not</c>) return <see langword="null"/> here
    /// and contribute through their operands, which the descendant walk visits in their own right.
    /// </remarks>
    private static string? PatternTypeName(PatternSyntax pattern) => pattern switch
    {
        TypePatternSyntax type => RightmostName(type.Type),
        DeclarationPatternSyntax declaration => RightmostName(declaration.Type),
        RecursivePatternSyntax { Type: { } recursive } => RightmostName(recursive),
        ConstantPatternSyntax constant => RightmostExpressionName(constant.Expression),
        _ => null,
    };

    /// <summary>
    /// The rightmost identifier of a type name, so <c>Win32Exception</c> and
    /// <c>System.ComponentModel.Win32Exception</c> are the same type to this gate.
    /// </summary>
    /// <remarks>
    /// Qualification is a local style choice — neither file imports
    /// <c>System.ComponentModel</c>, so both must qualify <c>Win32Exception</c> today, but a gate
    /// that treated qualification as a difference would fail the first time one of them added the
    /// using directive.
    /// </remarks>
    private static string RightmostName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => RightmostName(qualified.Right),
        AliasQualifiedNameSyntax aliased => RightmostName(aliased.Name),
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => type.ToString(),
    };

    /// <summary>
    /// The rightmost identifier of a name written as an EXPRESSION, which is how a type reaches
    /// this gate whenever the parser reads it as a constant pattern.
    /// </summary>
    private static string? RightmostExpressionName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        IdentifierNameSyntax name => name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>Renders a set for the failure message, naming emptiness rather than showing it.</summary>
    private static string Describe(IEnumerable<string> types)
    {
        var names = types.ToList();
        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }

    /// <summary>The members of <paramref name="left"/> that <paramref name="right"/> lacks.</summary>
    private static IEnumerable<string> Except(SortedSet<string> left, SortedSet<string> right) =>
        left.Where(name => !right.Contains(name));

    /// <summary>
    /// Walks up from the test assembly's output directory to the solution file — the same shape
    /// <c>AsciiRuntimeOutputCensusTests</c> uses to read source directly.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "vouchfx.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            "Walked from " + AppContext.BaseDirectory + " to the filesystem root without finding "
            + "vouchfx.sln, so this gate cannot locate either source file. That is an environment "
            + "failure, not a parity failure.");

        return directory!.FullName;
    }
}
