// Issue #473 — the ledger's THIRD hop, which no existing mechanism covered.
//
// THE HOP, AND WHY IT WAS INVISIBLE. Getting SecurityPathDisclosureLedger from the run to the
// recording site takes three passes of the same value, and each one is a separate chance to drop
// it. Two were already guarded and the middle one was not:
//
//   1. ScenarioRunner / WatchRunner  -> TopologyRequest.StartAsync   REQUIRED parameter (compiler)
//   2. TopologyRequest.StartAsync    -> SuiteTopology.StartAsync     SuiteProtocolTargetsTests
//   3. SuiteTopology.StartAsync      -> EnvironmentMapper.Map        *** THIS FILE ***
//   4. EnvironmentMapper.Map         -> ServerArtifactInjection.Plan REQUIRED parameter (compiler)
//
// Hop 3 was passed POSITIONALLY into an optional parameter, and three separate things conspired to
// hide it. SuiteProtocolTargetsTests excludes SuiteTopology.cs from its scan BY FILENAME (it is the
// declaring file for the call that census greps). That census only ever looks for
// `SuiteTopology.StartAsync(`, which is a different symbol. And the two arms proving the ledger
// reaches Plan — Map_ThreadsTheLedgerToTheArtefactsOfAService and its dependency twin — call
// EnvironmentMapper.Map DIRECTLY, so they exercise hop 4 and say nothing about hop 3.
//
// Measured before this file existed: deleting `, pathDisclosures` from that call compiled clean and
// left 828 / 590 / 589 green while `security.serverArtifacts[].source` recording was dead on BOTH
// production paths. That is the #364 defect shape — an optional argument dropped from one of
// several hand-maintained hops — one frame below where the branch had just finished documenting it
// as closed.
//
// WHY A CENSUS HERE RATHER THAN A REQUIRED PARAMETER, which is what closed hops 1 and 4. Measured:
// EnvironmentMapper.Map has ONE production call site and 205 in tests. Making its parameter
// required would force every one of those to pass `null` to assert something their own inputs
// already state, which is ceremony that gets copied without being read — the same argument that
// keeps SuiteTopology.StartAsync's parameter optional against ~60 Docker call sites. Where the
// call-site count is small the compiler is the better gate and was used: ServerArtifactInjection.
// Plan (four call sites) is a REQUIRED parameter for exactly that reason.
//
// THE ARGUMENT MUST BE NAMED, and that is a real constraint rather than a style rule. This census
// follows the repo's existing idiom and greps for `pathDisclosures:` inside the call's own argument
// window; the positional spelling it replaced would not have matched, so a census written against
// the old form would have passed while asserting nothing.
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// <c>SuiteTopology.StartAsync</c> passes the run's path-disclosure ledger to
/// <c>EnvironmentMapper.Map</c> (#473).
/// </summary>
public sealed class EnvironmentMapperLedgerHopCensusTests
{
    /// <summary>
    /// Mirrors <c>EnvironmentMapperSidecarDriftGuardTests.ResolveRepoRoot</c> — walk up from the
    /// test assembly's output directory to the repo root.
    /// </summary>
    private static string ResolveRepoRoot()
    {
        var assemblyDir =
            Path.GetDirectoryName(typeof(EnvironmentMapperLedgerHopCensusTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
    }

    private static string SuiteTopologySourcePath => Path.Combine(
        ResolveRepoRoot(), "src", "Engine", "Vouchfx.Engine.Orchestration", "SuiteTopology.cs");

    [Fact]
    public void SuiteTopology_PassesThePathLedgerToEnvironmentMapperMap()
    {
        var path = SuiteTopologySourcePath;
        Assert.True(File.Exists(path), $"SuiteTopology.cs not found at '{path}'; this guard cannot run.");

        // COMMENTS STRIPPED FIRST, and this is not defensive tidying. The call site carries a
        // comment that explains why the argument is named, and that comment necessarily contains
        // the literal `pathDisclosures:`. Scanning raw text would let the explanation satisfy the
        // guard for a call that had lost the argument — a census passing on its own documentation.
        var source = WithoutComments(File.ReadAllText(path));

        var offsets = CallOffsets(source, "EnvironmentMapper.Map(");

        // VACUITY FIRST. A census whose needle matches nothing cannot fail, so the expected shape
        // is asserted before anything is concluded from it.
        Assert.True(
            offsets.Count == 1,
            $"Expected exactly 1 EnvironmentMapper.Map( call in SuiteTopology.cs, found "
            + $"{offsets.Count}. If you ADDED one, it must also pass pathDisclosures: and this "
            + "count must be updated; if you REMOVED or RENAMED the call, re-point this guard "
            + "rather than leaving it matching nothing.");

        var window = ArgumentWindow(source, offsets[0]);

        Assert.True(
            window.Contains("pathDisclosures:", StringComparison.Ordinal),
            "SuiteTopology.StartAsync's call to EnvironmentMapper.Map (SuiteTopology.cs) does not "
            + "pass `pathDisclosures:`. That parameter is OPTIONAL on Map, so this compiles and "
            + "runs clean while every `security.serverArtifacts[].source` goes unrecorded on both "
            + "the `run` and `--watch` paths — and no other test sees it: SuiteProtocolTargetsTests "
            + "excludes this file by name, and the arms that prove the ledger reaches "
            + "ServerArtifactInjection.Plan call Map directly. Restore the argument, NAMED (a "
            + "positional argument satisfies the compiler but not this guard, deliberately). "
            + "Argument list found: (" + window.Trim() + ")");
    }

    /// <summary>
    /// Every offset at which <paramref name="needle"/> begins in <paramref name="source"/>.
    /// </summary>
    private static System.Collections.Generic.List<int> CallOffsets(string source, string needle)
    {
        var offsets = new System.Collections.Generic.List<int>();
        var index = source.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            offsets.Add(index);
            index = source.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return offsets;
    }

    /// <summary>
    /// The text between a call's own parentheses, matched by depth so a nested call cannot end the
    /// window early.
    /// </summary>
    /// <remarks>
    /// COUNTED WITHIN THE CALL'S OWN ARGUMENT LIST, never anywhere in the file — the same rule, for
    /// the same reason, as <c>SuiteProtocolTargetsTests.ArgumentWindow</c>: a whole-file search
    /// inflates in the FALSE-PASS direction, satisfying the guard from any other mention of the
    /// argument name.
    /// </remarks>
    private static string ArgumentWindow(string source, int callOffset)
    {
        var open = source.IndexOf('(', callOffset);
        Assert.True(open >= 0, "The matched call has no opening parenthesis; the scan is malformed.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '(')
            {
                depth++;
            }
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return source[(open + 1)..i];
                }
            }
        }

        Assert.Fail("The matched call's argument list is unbalanced; the scan is malformed.");
        return string.Empty;
    }

    /// <summary>
    /// Removes block comments and then each line's <c>//</c> tail.
    /// </summary>
    /// <remarks>
    /// Deliberately naive about <c>//</c> inside a string literal — <c>SuiteTopology.cs</c> holds no
    /// such literal on any line this guard reads, and the failure direction of over-stripping is a
    /// FALSE FAILURE (the guard reports a missing argument that is present), which is loud. The
    /// opposite simplification — not stripping at all — fails silently, which is the one this file
    /// exists because of.
    /// </remarks>
    private static string WithoutComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
    }
}
