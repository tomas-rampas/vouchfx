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
    /// The files that may mention it at all: the guard itself and the guard's own drills.
    /// </summary>
    /// <remarks>
    /// Both are named rather than globbed. The drills file is IN scope precisely because it is the
    /// file that broke the property last time - a census that watched only the production source
    /// would have passed while the fast lane was killing processes.
    /// </remarks>
    private static readonly string[] s_censusFiles =
    {
        "DrillHostHygiene.cs",
        "DrillHostSweepTests.cs",
    };

    /// <summary>
    /// The ONLY members permitted to call it, both inside <c>DrillHostSweepFixture</c> and both on
    /// the production path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SweepUnlessDisabled</c> is the production sweep. It is reached from the public
    /// parameterless constructor - the only one xUnit calls - and from the default of the seam that
    /// <c>Dispose</c> runs.
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
    };

    /// <summary>
    /// Every syntactic call to the live sweep sits in a member this census names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Vacuity-guarded twice.</strong> "No offending call site" is also what a census that
    /// found no FILES reports, and what one that found no CALLS AT ALL reports - the second being
    /// reachable by renaming the method, which would leave this gate passing over a sweep it no
    /// longer watches. Both are asserted non-zero before the real check.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLiveSweep_IsCalledOnlyFromTheFixturesProductionPath()
    {
        var files = CensusFiles();

        Assert.Equal(s_censusFiles.Length, files.Count);

        var callSites = files.SelectMany(FindCallSites).ToList();

        Assert.True(
            callSites.Count > 0,
            $"This census found no call to '{SweepMethod}' in "
            + string.Join(" or ", s_censusFiles)
            + ". Either the method was renamed - in which case rename it here too, because this "
            + "gate is now watching nothing - or the sweep was removed and this file should go "
            + "with it.");

        var offenders = callSites
            .Where(site => !s_permittedCallers.Contains(site.Member, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"'{SweepMethod}' walks the live process table and KILLS what it finds under this "
            + "repository's CLI build output. It may only be called from "
            + string.Join(" or ", s_permittedCallers)
            + ", so that the fast `requires!=docker` lane - which the guard's own untraited drills "
            + "run in - cannot reach it. New call site(s):\n"
            + string.Join("\n", offenders.Select(site => $"  {site.File}({site.Line}): {site.Member}"))
            + "\n\nIf a drill needs an exit sweep, inject a stub through the fixture's `sweep` "
            + "parameter. If production code needs one, add the member here and say why it can "
            + "never run outside the docker lane.");
    }

    /// <summary>One syntactic call to the swept method, and the member it sits in.</summary>
    private sealed record CallSite(string File, int Line, string Member);

    private static List<string> CensusFiles()
    {
        var directory = Path.GetDirectoryName(ThisFile())!;

        return s_censusFiles
            .Select(name => Path.Combine(directory, name))
            .Where(File.Exists)
            .ToList();
    }

    /// <summary>
    /// This test file's own directory, resolved from the compiled assembly rather than from the
    /// working directory, which <c>dotnet test</c> does not guarantee.
    /// </summary>
    private static string ThisFile()
    {
        var assemblyDirectory = Path.GetDirectoryName(
            typeof(DrillHostSweepCallSiteCensusTests).Assembly.Location)!;

        // bin/<cfg>/net8.0 -> the project directory.
        return Path.GetFullPath(Path.Combine(
            assemblyDirectory, "..", "..", "..", nameof(DrillHostSweepCallSiteCensusTests) + ".cs"));
    }

    private static IEnumerable<CallSite> FindCallSites(string path)
    {
        var text = File.ReadAllText(path);
        var tree = CSharpSyntaxTree.ParseText(text, path: path);
        var root = tree.GetRoot();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (NameOf(invocation.Expression) != SweepMethod)
            {
                continue;
            }

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            yield return new CallSite(Path.GetFileName(path), line, EnclosingMember(invocation));
        }
    }

    /// <summary>The invoked name, whether written bare or qualified.</summary>
    private static string? NameOf(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

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
