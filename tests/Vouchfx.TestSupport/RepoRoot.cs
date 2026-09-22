// The one resolver every test project uses to find the repository checkout root (#551).
//
// Before this type existed, seven independent test methods each re-derived the repo root by
// walking a FIXED number of parent directories up from the test assembly's own build output
// (net8.0 -> <configuration> -> bin -> <project> -> tests -> repo root), with no check that the
// directory landed on actually WAS the repository:
//
//   Vouchfx.Engine.Runtime.Tests:       ExamplesCompileTests.ResolveRepoRoot,
//                                       Sprint11ReferenceCapstoneTests.ResolveRepoRoot,
//                                       Sprint11ReferenceCompileTests.ResolveRepoRoot,
//                                       SuiteProtocolTargetsTests.ResolveRepoRoot,
//                                       BuiltCli.ResolveRepoRoot,
//                                       DrillHostSweepTests (inline, one test method),
//                                       DrillHostHygiene.DrillHostSweep.ResolveCliBinRoot (inline)
//   Vouchfx.Engine.Orchestration.Tests: EnvironmentMapperSidecarDriftGuardTests.ResolveRepoRoot,
//                                       EnvironmentMapperLedgerHopCensusTests.ResolveRepoRoot
//
// A build-output layout change (an extra nesting level, a renamed project folder) would silently
// point every one of them at the wrong directory rather than fail loudly - the fixed walk has no
// way to notice it landed somewhere that is not the repository.
//
// Resolve() instead walks up from AppContext.BaseDirectory until it finds a directory containing
// vouchfx.sln, so it is correct regardless of how deep the build output nests, and it fails LOUDLY
// - naming the anchor, never a path - when no ancestor has one, rather than silently returning a
// directory that merely happens to exist.
//
// NOT folded in here: a further set of repo-root walks (FindRepoRoot in SdkContractFreezeTests,
// EventContractFreezeTests, DependencyEnvCensusTests, PlanReportContractFreezeTests,
// PlanSafetyTests, ListJsonGoldenTests, ValidateJsonGoldenTests, SdkTestingContractFreezeTests,
// SchemaConversionBudgetTests, SchemaAcceptedCorpusTests, CatalogueJsonGoldenTests,
// VsCodeShippedSchemaSyncTests, SchemaFreezeTests and LanguageReferenceGoldenTests) already
// anchor on vouchfx.sln by hand rather than exhibiting #551's actual bug (an ABSENT anchor); they
// were left alone to keep this change to the walks the issue reports, rather than additionally
// touching three more test projects that do not yet reference this library. Consolidating those
// onto this type too is a reasonable follow-up, not done here.
//
// GitChangeSetTests.FindRepoRoot is a different thing again: it anchors on `.git` (a directory or
// a file - a worktree's is a file) to test GIT'S OWN notion of a work tree for the RealGit_* smoke
// tests, not "where is the vouchfx checkout", and returns null rather than throwing so those rows
// can no-op outside one. Left alone for that reason too.

using System;
using System.IO;

namespace Vouchfx.TestSupport;

/// <summary>
/// Resolves the repository checkout root, anchored on <see cref="AnchorFileName"/> (#551).
/// </summary>
public static class RepoRoot
{
    /// <summary>The file every overload of <see cref="Resolve()"/> anchors on.</summary>
    public const string AnchorFileName = "vouchfx.sln";

    private static string? s_cached;

    /// <summary>
    /// Walks up from the calling process's own base directory
    /// (<see cref="AppContext.BaseDirectory"/> - a test assembly's build output, under
    /// <c>bin/&lt;configuration&gt;/net8.0</c>) until it finds a directory containing
    /// <see cref="AnchorFileName"/>, and returns that directory. Cached after the first call: the
    /// answer cannot change within one test run.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No ancestor of <see cref="AppContext.BaseDirectory"/> contains <see cref="AnchorFileName"/>.
    /// The message names the anchor only, never a directory, so it is safe for a caller to let it
    /// reach an assertion and print straight into a public CI log.
    /// </exception>
    public static string Resolve() => s_cached ??= Resolve(AppContext.BaseDirectory);

    /// <summary>
    /// The same walk as <see cref="Resolve()"/>, but starting from <paramref name="startDirectory"/>
    /// rather than <see cref="AppContext.BaseDirectory"/>, and never cached.
    /// </summary>
    /// <remarks>
    /// Exists for <see cref="Resolve()"/>'s own drill: a scratch directory tree built under a temp
    /// directory specifically so it deliberately lacks the anchor proves the loud-failure path
    /// without depending on - or risking a false pass from - the real repository layout. Not
    /// cached, unlike <see cref="Resolve()"/>: every caller of this overload supplies its own
    /// start directory, so there is no single stable answer to cache.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// No ancestor of <paramref name="startDirectory"/> contains <see cref="AnchorFileName"/>.
    /// </exception>
    public static string Resolve(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, AnchorFileName)))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root: no ancestor directory contains "
            + $"'{AnchorFileName}'.");
    }
}
