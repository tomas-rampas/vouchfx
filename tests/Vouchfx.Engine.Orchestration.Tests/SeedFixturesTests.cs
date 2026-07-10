// Non-docker tests for S05-A-02: SeedFixtures.ComputeContentHash — the shared
// content-hash routine the seed applier uses and the reproducibility envelope
// (S05-B-03) will reuse.  Deterministic, byte-exact SHA-256; no database needed.

using System.Security.Cryptography;
using Vouchfx.Engine.Orchestration;
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

    [Fact]
    public void ComputeContentHash_MissingFile_ThrowsFileNotFoundNamingResolvedPath()
    {
        var dir = NewTempDir();
        try
        {
            var relative = Path.Combine("fixtures", "absent.json");
            var expectedFull = Path.GetFullPath(Path.Combine(dir, relative));

            // Act + Assert — a missing file raises a clear FileNotFoundException whose
            // message names the resolved absolute path.  The seed applier maps this to
            // a Provision (Environment) error.
            var ex = Assert.Throws<FileNotFoundException>(
                () => SeedFixtures.ComputeContentHash(dir, relative));
            Assert.Contains(expectedFull, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
