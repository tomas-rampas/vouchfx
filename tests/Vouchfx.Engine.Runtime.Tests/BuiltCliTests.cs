// Docker-free rows for BuiltCli.RelativeToRepoRoot, the one place every docker-lane test in this
// project derives a path it is about to print into a public CI log (#552).
//
// The cross-volume case cannot be produced with real paths on the Linux lane (a single root means
// Path.GetRelativePath can always relativise), so the rendering step is a pure function of the
// probed path and GetRelativePath's answer, and the rows below drive it with the exact answer that
// API documents for two paths that share no root: the target path, returned unchanged.
//
// Paths under the system temp directory use a separate marker (#591), keeping the tail below
// temp without exposing the host profile. Explicit-root fixtures are synthetic; current-host
// probes use fixed failure messages so a failed privacy guard cannot print the path it protects.

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
        // Where every drill's materialised suite lands. Temp gets a withheld prefix on any volume;
        // a checkout under temp still takes precedence for its own repository-relative paths.
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

    [Theory]
    [InlineData("/tmp/a/../x/deployment.e2e.yaml", "/tmp/x/deployment.e2e.yaml")]
    [InlineData("/tmp/x/deployment.e2e.yaml", "/private/x/deployment.e2e.yaml")]
    public void Render_RootedNormalisedResult_OnlyRendersTheMarkerAndLeaf(string original, string relative)
    {
        Assert.Equal(
            BuiltCli.OutsideRepositoryVolumeMarker + "/deployment.e2e.yaml",
            BuiltCli.Render(original, relative));
    }

    [Theory]
    [InlineData("vouchfx-drill-4711/deployment.e2e.yaml")]
    [InlineData("discard/../vouchfx-drill-4711/deployment.e2e.yaml")]
    public void RelativeToRepoRoot_SyntheticSameVolumeTempPath_RedactsProfilePrefix(string tail)
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var repo = Path.Combine(root, "src", "private-checkout");
        var temp = Path.Combine(root, "Users", "private-profile", "AppData", "Local", "Temp");
        var probe = Path.Combine(temp, tail.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(BuiltCli.TempDirectoryMarker + "/vouchfx-drill-4711/deployment.e2e.yaml",
            BuiltCli.RelativeToRepoRoot(probe, repo, temp));
    }

    [Fact]
    public void RelativeToRepoRoot_RepositoryInsideTemp_PreservesUsefulRepositoryRelativePath()
    {
        var temp = Path.GetTempPath();
        var repo = Path.Combine(temp, "synthetic-checkout");
        var probe = Path.Combine(repo, "tests", "Fixture.cs");

        Assert.True(string.Equals("tests/Fixture.cs",
            BuiltCli.RelativeToRepoRoot(probe, repo, temp), StringComparison.Ordinal),
            "a repository path under temp lost its repository-relative form");
    }

    [Fact]
    public void RelativeToRepoRoot_TempPrefixSibling_IsNotMistakenForTemp()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var repo = Path.Combine(root, "src", "checkout");
        var temp = Path.Combine(root, "synthetic-temp");
        var probe = Path.Combine(root, "synthetic-temp-sibling", "Fixture.cs");

        Assert.Equal("../../synthetic-temp-sibling/Fixture.cs",
            BuiltCli.RelativeToRepoRoot(probe, repo, temp));
    }

    [Fact]
    public void RelativeToRepoRoot_CurrentTempPath_UsesTheTempMarkerWithoutHostPrefixes()
    {
        var temp = Path.GetTempPath();
        var syntheticRepo = Path.Combine(Path.GetPathRoot(temp)!, "synthetic-repo");
        var probe = Path.Combine(temp, "vouchfx-drill-4711", "deployment.e2e.yaml");

        Assert.True(string.Equals(BuiltCli.TempDirectoryMarker + "/vouchfx-drill-4711/deployment.e2e.yaml",
            BuiltCli.RelativeToRepoRoot(probe, syntheticRepo, temp), StringComparison.Ordinal),
            "a real temp-directory path did not use the temp marker and relative tail");
    }

    [Fact]
    public void RelativeToRepoRoot_WindowsDifferentVolumeNormalisedPath_WithholdsTheAbsolutePrefix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repo = BuiltCli.ResolveRepoRoot();
        var drive = string.Equals(Path.GetPathRoot(repo), @"Z:\", StringComparison.OrdinalIgnoreCase) ? "Y:" : "Z:";
        var probe = drive + "/private-profile/a/../deployment.e2e.yaml";

        Assert.True(string.Equals(BuiltCli.OutsideRepositoryVolumeMarker + "/deployment.e2e.yaml",
            BuiltCli.RelativeToRepoRoot(probe, repo, Path.GetTempPath()), StringComparison.Ordinal),
            "a normalised cross-volume path did not withhold its absolute prefix");
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
