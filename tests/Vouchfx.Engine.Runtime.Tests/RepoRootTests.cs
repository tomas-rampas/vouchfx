// Pins Vouchfx.TestSupport.RepoRoot (#551): the repo-root walk collapsed from nine independent,
// UNANCHORED copies (see that type's own remarks for the full list) into this one function.
//
// Three claims, each its own row. The positive case against the REAL checkout: walking up from
// this test assembly's own build output finds a directory that actually holds vouchfx.sln — not
// merely a directory that happens to be five levels up, which is all any of the nine copies this
// replaced ever checked. The positive case against a SCRATCH tree: the walk-up itself, proven
// independently of the real repository layout, so this row cannot start silently lying if the
// checkout is ever restructured. The negative case: started below a directory tree with no
// vouchfx.sln anywhere in its ancestry, the walk fails LOUDLY, naming the anchor, and the failure
// message never spells out a directory it searched — built under a scratch temp tree rather than
// relying on the real layout, per #551's own drill instruction.
//
// Docker-free throughout; nothing here starts a process or a container.

using System;
using System.IO;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Runtime.Tests;

public sealed class RepoRootTests : IDisposable
{
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(), "vouchfx-repo-root-drill-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratchRoot))
        {
            Directory.Delete(_scratchRoot, recursive: true);
        }
    }

    /// <summary>
    /// Against the real checkout: <see cref="RepoRoot.Resolve()"/> finds a directory that
    /// genuinely holds <see cref="RepoRoot.AnchorFileName"/> — not merely one that happens to sit
    /// a fixed number of levels above the test assembly's build output, which is what every walk
    /// this type replaced actually checked.
    /// </summary>
    [Fact]
    public void Resolve_FindsADirectoryThatActuallyHoldsTheAnchor()
    {
        var root = RepoRoot.Resolve();

        // Fixed messages, never the resolved directory: this row runs against the real checkout,
        // so interpolating `root` would print a host path into a public CI log.
        Assert.True(
            Directory.Exists(root),
            "RepoRoot.Resolve() answered a directory that does not exist.");
        Assert.True(
            File.Exists(Path.Combine(root, RepoRoot.AnchorFileName)),
            "RepoRoot.Resolve() answered a directory that does not contain "
            + $"'{RepoRoot.AnchorFileName}' — the walk landed somewhere that is not the repository "
            + "root.");
    }

    /// <summary>Cached: repeated calls answer the exact same directory instance.</summary>
    [Fact]
    public void Resolve_IsCachedAcrossCalls()
    {
        // ReferenceEquals under a fixed message rather than Assert.Same, whose failure text prints
        // both values — here, the real checkout's absolute path.
        Assert.True(
            ReferenceEquals(RepoRoot.Resolve(), RepoRoot.Resolve()),
            "RepoRoot.Resolve() answered a different string instance on a repeat call; it is meant "
            + "to be cached.");
    }

    /// <summary>
    /// The walk itself, proven against a tree this row builds and controls completely: it stops
    /// at the NEAREST ancestor holding the anchor, starting several levels below it — the same
    /// shape as a real <c>bin/&lt;configuration&gt;/net8.0</c> build output several levels below
    /// the repository root.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="Resolve_FindsADirectoryThatActuallyHoldsTheAnchor"/>: that row
    /// depends on this checkout's own layout, so a change to how deep tests/ nests would move
    /// what it measures without this row noticing. This one does not depend on the real layout at
    /// all, per #551's own drill instruction.
    /// </remarks>
    [Fact]
    public void ResolveFromStart_FindsTheNearestAncestorHoldingTheAnchor()
    {
        var anchorDirectory = Directory.CreateDirectory(Path.Combine(_scratchRoot, "repo")).FullName;
        File.WriteAllText(Path.Combine(anchorDirectory, RepoRoot.AnchorFileName), string.Empty);
        var leaf = Directory.CreateDirectory(
            Path.Combine(anchorDirectory, "tests", "Some.Tests", "bin", "Release", "net8.0")).FullName;

        var resolved = RepoRoot.Resolve(leaf);

        // A fixed message rather than Assert.Equal, whose failure text prints both directories —
        // and the system temp directory can name the host's user (on Windows it sits under the
        // user profile).
        Assert.True(
            string.Equals(anchorDirectory, resolved, StringComparison.Ordinal),
            "RepoRoot.Resolve(start) did not answer the nearest ancestor holding the anchor.");
    }

    /// <summary>
    /// Started below a directory tree with no <see cref="RepoRoot.AnchorFileName"/> anywhere in
    /// its ancestry, the walk fails LOUDLY with a fixed, anchor-naming message — never silently
    /// returning a directory that merely happens to exist, and never spelling out a directory it
    /// searched.
    /// </summary>
    /// <remarks>
    /// Built under a scratch tree under the system temp directory rather than relying on the real
    /// repository layout: a negative case anchored on the real checkout could pass today and start
    /// silently lying the moment the checkout moves. The message assertion is the same discipline
    /// <c>BuiltCli.RelativeToRepoRoot</c> already holds for a resolved path — an assertion here is
    /// exactly the kind of place that would otherwise print a real host directory into a public CI
    /// log.
    /// </remarks>
    [Fact]
    public void ResolveFromStart_WithNoAnchorInAncestry_FailsLoudlyNamingTheAnchorAndNoPath()
    {
        var leaf = Directory.CreateDirectory(Path.Combine(_scratchRoot, "a", "b", "c")).FullName;

        var thrown = Assert.Throws<InvalidOperationException>(() => RepoRoot.Resolve(leaf));

        // Fixed messages throughout: the failure this row exists to catch is a message that names
        // a directory, and Assert.Equal or Assert.DoesNotContain would print that message, and the
        // directory with it, while reporting it.
        Assert.True(
            string.Equals(
                "Could not locate the repository root: no ancestor directory contains 'vouchfx.sln'.",
                thrown.Message,
                StringComparison.Ordinal),
            "RepoRoot.Resolve(start) failed with a message other than the fixed, anchor-naming "
            + "sentence.");
        Assert.True(
            !thrown.Message.Contains(_scratchRoot, StringComparison.Ordinal)
                && !thrown.Message.Contains(leaf, StringComparison.Ordinal),
            "RepoRoot.Resolve(start) failed with a message that names a directory it searched.");
    }
}
