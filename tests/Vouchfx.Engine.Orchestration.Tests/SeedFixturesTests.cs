// Non-docker tests for S05-A-02: SeedFixtures.ComputeContentHash — the shared
// content-hash routine the seed applier uses and the reproducibility envelope
// (S05-B-03) will reuse.  Deterministic, byte-exact SHA-256; no database needed.

using System.Security.Cryptography;
using Vouchfx.Engine.Orchestration;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Tests for <see cref="SeedFixtures.ComputeContentHash"/> (S05-A-02).
/// </summary>
public sealed class SeedFixturesTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-fixhash-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ComputeContentHash_KnownContent_MatchesSha256HexLowercase()
    {
        var dir = NewTempDir();
        try
        {
            // Arrange — write known bytes and compute the expected hash independently.
            var bytes = "the quick brown fox"u8.ToArray();
            File.WriteAllBytes(Path.Combine(dir, "f.json"), bytes);
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            // Act
            var actual = SeedFixtures.ComputeContentHash(dir, "f.json");

            // Assert — 64-char lower-case hex, equal to the independent computation.
            Assert.Equal(64, actual.Length);
            Assert.Equal(expected, actual);
            Assert.DoesNotContain(actual, c => char.IsUpper(c));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ComputeContentHash_SameContent_ProducesSameHash()
    {
        var dir = NewTempDir();
        try
        {
            // Arrange — two files with byte-identical content.
            var bytes = "identical-payload"u8.ToArray();
            File.WriteAllBytes(Path.Combine(dir, "a.json"), bytes);
            File.WriteAllBytes(Path.Combine(dir, "b.json"), bytes);

            // Act
            var hashA = SeedFixtures.ComputeContentHash(dir, "a.json");
            var hashB = SeedFixtures.ComputeContentHash(dir, "b.json");

            // Assert — determinism: same content ⇒ same hash.
            Assert.Equal(hashA, hashB);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ComputeContentHash_DifferentContent_ProducesDifferentHash()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.json"), "one"u8.ToArray());
            File.WriteAllBytes(Path.Combine(dir, "b.json"), "two"u8.ToArray());

            var hashA = SeedFixtures.ComputeContentHash(dir, "a.json");
            var hashB = SeedFixtures.ComputeContentHash(dir, "b.json");

            Assert.NotEqual(hashA, hashB);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A missing fixture raises <see cref="FileNotFoundException"/> naming the author's DECLARED
    /// path and the concept it resolves against — never the resolved absolute host path, and never
    /// in <see cref="FileNotFoundException.FileName"/> either (#357's rule, applied by #473).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The absence is asserted BEFORE the presence</strong>, so a break fails on the leak
    /// rather than on a missing phrase — the convention
    /// <c>SecurityDiagnosticPathDisclosureTests</c> established, and the reason the shared
    /// <see cref="HostPathDisclosure.AssertNoAbsoluteHostPath"/> is used here rather than a third
    /// hand-written variant. (There were TWO before #473 — one in
    /// <c>SecurityDiagnosticPathDisclosureTests</c> and one in
    /// <c>SecurityPathDisclosureLedgerTests</c>, and they had diverged; both are now delegating
    /// wrappers over the shared one.)
    /// </para>
    /// <para>
    /// <strong><see cref="FileNotFoundException.FileName"/> is asserted separately, and NOT via
    /// <c>ToString()</c>.</strong> That property is appended to the exception's full text as
    /// "File name: '…'", so a message that lost its resolved path while the <c>fileName</c>
    /// constructor argument kept one would still disclose it to any sink that formats the whole
    /// exception. Running the property assertion over <c>ToString()</c> was the first attempt and
    /// it is unusable — measured: the full text also carries the STACK TRACE, whose frames name
    /// this repository's own compile-time source paths, so the check fails on
    /// <c>…\SeedFixtures.cs:line</c> for every exception ever thrown. A stack frame is not a
    /// runtime disclosure of the author's environment; the <c>FileName</c> property is the part
    /// this code chooses, so that is the part asserted.
    /// </para>
    /// <para>
    /// This test remains the ONLY observer of the message: the single production call site,
    /// <c>ScenarioRunner.HashFixtureOrNull</c>, catches and swallows it, and the seed applier never
    /// calls this at all, doing its own existence check instead. The rule is applied here anyway
    /// because "unreachable" is a fact about today's caller rather than a property of the method —
    /// see the throw's own comment.
    /// </para>
    /// </remarks>
    [Fact]
    public void ComputeContentHash_MissingFile_ThrowsFileNotFoundNamingDeclaredPathOnly()
    {
        var dir = NewTempDir();
        try
        {
            var relative = Path.Combine("fixtures", "absent.json");

            var ex = Assert.Throws<FileNotFoundException>(
                () => SeedFixtures.ComputeContentHash(dir, relative));

            HostPathDisclosure.AssertNoAbsoluteHostPath(
                "the seed-fixture not-found message", ex.Message, dir);
            HostPathDisclosure.AssertNoAbsoluteHostPath(
                "the seed-fixture not-found exception's FileName", ex.FileName ?? string.Empty, dir);

            Assert.Contains(relative, ex.Message, StringComparison.Ordinal);
            Assert.Equal(relative, ex.FileName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
