// The standing guard on WHERE the orphan-host sweep can be reached from - issue #378.
//
// WHY THIS FILE EXISTS, AND WHY A BEHAVIOURAL TEST WOULD NOT DO
// ────────────────────────────────────────────────────────────
// DrillHostSweep.SweepLiveProcesses walks the live process table and KILLS what it finds under
// this repository's CLI build output. It is correct in the docker drill lane, where the fixture
// that calls it belongs to a collection every member of which is docker-gated. It is a hazard
// anywhere else: the fast `requires!=docker` lane is the one every contributor and the blocking CI
// job run, and a kill there can land on a process the developer is deliberately holding.
//
// THAT BOUNDARY HAS BEEN CROSSED TWICE, both times by accident and neither time by the kill logic:
//
//   1. KafkaSecurityConfirmationDrillDockerTests joined the drill collection while three of its
//      rows carried no requires=docker trait. A collection fixture is built before its first test
//      on WHATEVER lane runs it, so the sweep constructed - and killed - during `requires!=docker`.
//      Fixed by splitting the class (KafkaSecurityConfirmationPreflightTests).
//
//   2. DrillHostSweepFixture.Dispose called SweepLiveProcesses directly with no injection seam,
//      and the guard's OWN drills - untraited, therefore fast-lane - called Dispose. MEASURED: a
//      process planted holding the Debug CLI dll was killed by a `requires!=docker` run, exit 0.
//      Fixed by the Func<SweepReport> seam on the fixture's test-seam constructor.
//
// Two crossings through two unrelated doors is the signal that the property needs a gate rather
// than another careful author. No behavioural test can cover the door nobody has opened yet, so
// this is a SOURCE census in the house idiom (see Vouchfx.Cli.Tests/AsciiRuntimeOutputCensusTests):
// it parses the C# with Roslyn and pins every syntactic call site of SweepLiveProcesses.
//
// ROSLYN, NOT A REGEX, for the same reason that file gives: the distinction between a real call
// and the name appearing in a comment or a doc reference is exactly the one a regex gets wrong,
// and this file's own prose above names the method six times.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

/// <summary>
/// Pins the call sites of <see cref="DrillHostSweep.SweepLiveProcesses"/> so the process-killing
/// sweep cannot be reached from the fast <c>requires!=docker</c> lane.
/// </summary>
public sealed class DrillHostSweepCallSiteCensusTests
{
    /// <summary>The method whose reachability this census constrains.</summary>
    private const string SweepMethod = "SweepLiveProcesses";

    /// <summary>
    /// The fewest <c>.cs</c> files this project can plausibly hold. Below it, the census is
    /// assumed to have failed to find the source tree rather than to have found a small one.
    /// </summary>
    /// <remarks>
    /// A floor rather than an exact count, because an exact count is a second thing to maintain
    /// and would redden on every unrelated file added. The project held around sixty files when
    /// this was written; twenty is far enough below that to never be reached by deletion, and far
    /// enough above zero to catch a directory that resolved wrongly.
    /// </remarks>
    private const int MinimumCensusFiles = 20;

    /// <summary>
    /// The ONLY members permitted to name it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SweepUnlessDisabled</c> is the production sweep. It is reached from the public
    /// parameterless constructor - the only one xUnit calls - and from the default of the seam that
    /// <c>Dispose</c> runs. <c>SweepLiveProcesses</c> itself is permitted so the method's own body
    /// is not an offender against its own name.
    /// </para>
    /// <para>
    /// Note what is NOT on this list: <c>Dispose</c>. It must reach the live sweep through the
    /// injected delegate, never directly, because its callers include the guard's own untraited
    /// drills. Reverting it to a hard call reddens this census.
    /// </para>
    /// </remarks>
    private static readonly string[] s_permittedCallers =
    {
        "SweepUnlessDisabled",
        SweepMethod,
    };

    /// <summary>Build output, which holds generated sources this census has no business reading.</summary>
    private static readonly string[] s_excludedDirectories = { "bin", "obj" };

    /// <summary>
    /// Every syntactic reference to the live sweep sits in a member this census names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>REFERENCES, not just invocations, and the difference is the whole point.</strong>
    /// The fixture's seam takes a <c>Func&lt;SweepReport&gt;</c>, so
    /// <c>sweep: DrillHostSweep.SweepLiveProcesses</c> - a method group, never syntactically an
    /// invocation - hands a drill the live killer just as effectively as calling it. A census that
    /// matched only <c>InvocationExpression</c> would watch the door that was used last time while
    /// leaving the adjacent one open. Any identifier naming the method counts.
    /// </para>
    /// <para>
    /// <strong>The scope is EVERY .cs file in this project, not a whitelist.</strong> The first
    /// version named two files, which encoded an assumption the gate exists to disprove: that the
    /// next crossing will happen where the last one did. Both crossings so far came through doors
    /// nobody had listed. Parsing the whole project costs well under a second and cannot be
    /// out-of-date.
    /// </para>
    /// <para>
    /// <strong>Vacuity-guarded twice.</strong> "No offending reference" is also what a census that
    /// found no FILES reports, and what one that found no REFERENCES AT ALL reports - the second
    /// being reachable by renaming the method, which would leave this gate passing over a sweep it
    /// no longer watches. Both are asserted before the real check.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLiveSweep_IsNamedOnlyByTheFixturesProductionPath()
    {
        var files = CensusFiles();

        Assert.True(
            files.Count > MinimumCensusFiles,
            $"This census found only {files.Count} .cs file(s) under '{ProjectDirectory()}', which "
            + $"is below the floor of {MinimumCensusFiles}. It has almost certainly resolved the "
            + "wrong directory rather than found a small project - and a census over no files "
            + "passes for free.");

        var references = files.SelectMany(FindReferences).ToList();

        Assert.True(
            references.Count > 0,
            $"This census found no reference to '{SweepMethod}' anywhere in the project. Either "
            + "the method was renamed - in which case rename it here too, because this gate is now "
            + "watching nothing - or the sweep was removed and this file should go with it.");

        var offenders = references
            .Where(site => !s_permittedCallers.Contains(site.Member, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"'{SweepMethod}' walks the live process table and KILLS what it finds under this "
            + "repository's CLI build output. It may only be NAMED - called, or passed as a method "
            + "group - from "
            + string.Join(" or ", s_permittedCallers)
            + ", so that the fast `requires!=docker` lane, which the guard's own untraited drills "
            + "run in, cannot reach it. New reference(s):\n"
            + string.Join("\n", offenders.Select(site => $"  {site.File}({site.Line}): {site.Member}"))
            + "\n\nIf a drill needs an exit sweep, inject a stub through the fixture's `sweep` "
            + "parameter. If production code needs one, add the member here and say why it can "
            + "never run outside the docker lane.");
    }

    /// <summary>One syntactic reference to the swept method, and the member it sits in.</summary>
    private sealed record CallSite(string File, int Line, string Member);

    /// <summary>Every <c>.cs</c> file in this test project, excluding build output.</summary>
    private static List<string> CensusFiles() =>
        Directory
            .EnumerateFiles(ProjectDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsUnderExcludedDirectory(path))
            .ToList();

    private static bool IsUnderExcludedDirectory(string path)
    {
        var relative = Path.GetRelativePath(ProjectDirectory(), path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return segments.Any(segment => s_excludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// This test project's directory, resolved from the compiled assembly rather than from the
    /// working directory, which <c>dotnet test</c> does not guarantee.
    /// </summary>
    private static string ProjectDirectory()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            typeof(DrillHostSweepCallSiteCensusTests).Assembly.Location)!;

        // bin/<cfg>/net8.0 -> the project directory.
        return Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", ".."));
    }

    /// <summary>
    /// Every identifier in one file that names the swept method, invoked or not.
    /// </summary>
    /// <remarks>
    /// <c>DescendantNodes</c> does not descend into trivia, so a <c>&lt;see cref="..."/&gt;</c> in
    /// documentation and the method's name in a comment are invisible here by construction - which
    /// is the reason this is Roslyn rather than a regex, given how often this file's own prose
    /// names the method.
    /// </remarks>
    private static IEnumerable<CallSite> FindReferences(string path)
    {
        var text = File.ReadAllText(path);
        var tree = CSharpSyntaxTree.ParseText(text, path: path);
        var root = tree.GetRoot();

        foreach (var node in root.DescendantNodes(descendIntoTrivia: false))
        {
            if (!NamesTheSweep(node))
            {
                continue;
            }

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            yield return new CallSite(Path.GetFileName(path), line, EnclosingMember(node));
        }
    }

    /// <summary>
    /// Whether this node names the swept method, counting a qualified reference exactly once.
    /// </summary>
    /// <remarks>
    /// <c>DrillHostSweep.SweepLiveProcesses</c> is a <see cref="MemberAccessExpressionSyntax"/>
    /// whose <c>Name</c> is itself an <see cref="IdentifierNameSyntax"/> carrying the same text, so
    /// a naive match reports one reference twice. The identifier is therefore skipped when it is
    /// the name half of a member access that has already been counted.
    /// </remarks>
    private static bool NamesTheSweep(SyntaxNode node)
    {
        switch (node)
        {
            case MemberAccessExpressionSyntax member:
                return member.Name.Identifier.ValueText == SweepMethod;

            case IdentifierNameSyntax identifier:
                if (identifier.Identifier.ValueText != SweepMethod)
                {
                    return false;
                }

                // The name half of a qualified reference; the member access above counted it.
                var isNameOfAMemberAccess =
                    identifier.Parent is MemberAccessExpressionSyntax parent
                    && parent.Name == identifier;

                return !isNameOfAMemberAccess;

            default:
                return false;
        }
    }

    /// <summary>
    /// The method, constructor or property the call sits in - what the permitted list names.
    /// </summary>
    /// <remarks>
    /// A call in a field initialiser or outside any member yields a sentinel rather than nothing,
    /// so it can never be silently permitted: a field initialiser runs on type load, which is
    /// exactly the uncontrolled timing this census exists to prevent.
    /// </remarks>
    private static string EnclosingMember(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.ValueText;
                case ConstructorDeclarationSyntax constructor:
                    return constructor.Identifier.ValueText;
                case PropertyDeclarationSyntax property:
                    return property.Identifier.ValueText;
                case FieldDeclarationSyntax:
                    return "<field initialiser>";
            }
        }

        return "<file scope>";
    }
}
