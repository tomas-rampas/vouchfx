// Docker-free rows for BuiltCli.RelativeToRepoRoot, the one place every docker-lane test in this
// project derives a path it is about to print into a public CI log (#552).
//
// The cross-volume case cannot be produced with real paths on the Linux lane (a single root means
// Path.GetRelativePath can always relativise), so the rendering step is a pure function of the
// probed path and GetRelativePath's answer, and the rows below drive it with the exact answer that
// API documents for two paths that share no root: the target path, returned unchanged.

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
    public void RelativeToRepoRoot_PathOutsideTheRepository_NeverSpellsTheRepositoryRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), "vouchfx-drill-4711", "deployment.e2e.yaml");

        var rendered = BuiltCli.RelativeToRepoRoot(outside);

        // Fixed diagnostics throughout: every value in play here is a real host path, and the
        // failure mode being guarded is exactly "the rendered text is one", so no assertion may
        // print it.
        Assert.True(!Path.IsPathRooted(rendered), "a path outside the repository rendered as a rooted path");
        Assert.True(
            !rendered.Contains(BuiltCli.ResolveRepoRoot().Replace('\\', '/'), StringComparison.Ordinal),
            "a path outside the repository spelled the repository root");
        Assert.True(
            rendered.EndsWith("vouchfx-drill-4711/deployment.e2e.yaml", StringComparison.Ordinal),
            "the rendered path lost the probed path's own tail");
    }
}
