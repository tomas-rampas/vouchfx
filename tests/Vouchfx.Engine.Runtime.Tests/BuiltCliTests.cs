// Docker-free rows for BuiltCli.RelativeToRepoRoot, the one place every docker-lane test in this
// project derives a path it is about to print into a public CI log (#552).
//
// The cross-volume case cannot be produced with real paths on the Linux lane (a single root means
// Path.GetRelativePath can always relativise), so the rendering step is a pure function of the
// probed path and GetRelativePath's answer, and the rows below drive it with the exact answer that
// API documents for two paths that share no root: the target path, returned unchanged.
//
// The two RelativeToRepoRoot rows that probe outside the repository split on that same fact
// (#567). One probes under the checkout's own path root (its volume, on Windows), so it always
// takes the relativised branch and can assert the path's whole tail. The other probes the system
// temp directory, where the drills really write, and asserts only what holds on either branch:
// on a Windows host whose temp directory is on another volume it takes the cross-volume branch
// instead.

using System;
using System.IO;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class BuiltCliTests
{
    private const string CrossVolumeArtefact = @"D:\tmp\vouchfx-drill-4711\deployment.e2e.yaml";

    [Fact]
    public void Render_WhenGetRelativePathCouldNotRelativise_NeverEchoesTheAbsolutePath()
    {
        // Path.GetRelativePath returns the target UNCHANGED when the two paths share no root —
        // on Windows, a temp artefact on another volume than the checkout.
        var rendered = BuiltCli.Render(CrossVolumeArtefact, CrossVolumeArtefact);

        Assert.Equal(
            BuiltCli.OutsideRepositoryVolumeMarker + "/deployment.e2e.yaml",
            rendered);
        Assert.DoesNotContain("D:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("vouchfx-drill-4711", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_CrossVolumeDirectoryWithTrailingSeparator_StillRendersItsLeaf()
    {
        const string directory = @"D:\tmp\vouchfx-drill-4711\";

        var rendered = BuiltCli.Render(directory, directory);

        Assert.Equal(BuiltCli.OutsideRepositoryVolumeMarker + "/vouchfx-drill-4711", rendered);
    }

    [Fact]
    public void Render_OrdinaryRelativeResult_IsPassedThroughWithForwardSlashes()
    {
        var rendered = BuiltCli.Render(@"C:\repo\tests\Some.Tests\Row.cs", @"tests\Some.Tests\Row.cs");

        Assert.Equal("tests/Some.Tests/Row.cs", rendered);
    }

    [Fact]
    public void RelativeToRepoRoot_PathInsideTheRepository_IsRelativeToIt()
    {
        var inside = Path.Combine(BuiltCli.ResolveRepoRoot(), "tests");

        // Fixed diagnostic, not Assert.Equal: on failure xUnit prints the actual value, which here
        // would be the absolute repository path this helper exists to keep out of a public log.
        Assert.True(
            string.Equals("tests", BuiltCli.RelativeToRepoRoot(inside), StringComparison.Ordinal),
            "a path inside the repository did not render as its repository-relative form");
    }

    [Fact]
    public void RelativeToRepoRoot_PathOutsideTheRepositoryOnItsVolume_KeepsItsWholeTail()
    {
        // Under the checkout's own path root (its volume, on Windows), so Path.GetRelativePath can
        // always relativise and this row always takes the branch it asserts. The system temp
        // directory cannot promise that (#567).
        var repoRoot = BuiltCli.ResolveRepoRoot();
        var outside = Path.Combine(Path.GetPathRoot(repoRoot)!, "vouchfx-drill-4711", "deployment.e2e.yaml");

        // The premise, asserted rather than assumed: a checkout at a path root (/, a drive or a
        // share root) would put the probe INSIDE the repository, so this row fails there by
        // design; no path outside the repository shares its root.
        Assert.True(
            Path.GetRelativePath(repoRoot, outside).StartsWith("..", StringComparison.Ordinal),
            "the probe is not outside the repository (is the checkout at a path root?)");

        var rendered = BuiltCli.RelativeToRepoRoot(outside);

        // Fixed diagnostics throughout: every value in play here is a real host path, and the
        // failure mode being guarded is exactly "the rendered text is one", so no assertion may
        // print it.
        Assert.True(!Path.IsPathRooted(rendered), "a path outside the repository rendered as a rooted path");
        Assert.True(
            !SpellsRepositoryRoot(rendered, repoRoot),
            "a path outside the repository spelled the repository root");
        Assert.True(
            rendered.EndsWith("vouchfx-drill-4711/deployment.e2e.yaml", StringComparison.Ordinal),
            "the rendered path lost the probed path's own tail");
    }

    [Fact]
    public void RelativeToRepoRoot_PathUnderTheTempDirectory_NeverSpellsTheRepositoryRoot()
    {
        // Where every drill's materialised suite lands. Which branch Render takes depends on the
        // host: relativised when the temp directory shares the checkout's volume, the marker plus
        // the last segment when it does not. Only what holds on both is asserted.
        var repoRoot = BuiltCli.ResolveRepoRoot();
        var underTemp = Path.Combine(Path.GetTempPath(), "vouchfx-drill-4711", "deployment.e2e.yaml");

        var rendered = BuiltCli.RelativeToRepoRoot(underTemp);

        // Fixed diagnostics, for the reason given in the row above.
        Assert.True(!Path.IsPathRooted(rendered), "a temp-directory path rendered as a rooted path");
        Assert.True(
            !SpellsRepositoryRoot(rendered, repoRoot),
            "a temp-directory path spelled the repository root");
        Assert.True(
            rendered.EndsWith("/deployment.e2e.yaml", StringComparison.Ordinal),
            "a temp-directory path lost its own file name");
    }

    // Whether `rendered` spells the repository root as whole path segments. A plain substring
    // test fails falsely on some checkout layouts: a checkout at /vouchfx "occurs" inside
    // ../vouchfx-drill-4711/..., and a checkout at / occurs in every path with a separator.
    // Neither is a leak. For a checkout at / nothing is compared: the temp-directory row's
    // IsPathRooted assertion is the guard there, and the other row's premise already fails.
    private static bool SpellsRepositoryRoot(string rendered, string repoRoot)
    {
        var root = repoRoot.Replace('\\', '/').TrimEnd('/');
        return root.Length > 0
            && ("/" + rendered.Replace('\\', '/') + "/").Contains(root + "/", StringComparison.Ordinal);
    }
}
