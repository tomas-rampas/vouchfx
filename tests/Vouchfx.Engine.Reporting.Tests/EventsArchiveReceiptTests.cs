using System.Text;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

public sealed class EventsArchiveReceiptTests
{
    private static readonly string[] Lines = { "{\"type\":\"synthetic-receipt-control\"}" };

    [Fact]
    public void SuccessfulRawWrite_ReturnsReceipt_AfterFileIsClosed_WithExactBytes()
    {
        var root = Directory.CreateTempSubdirectory("vouchfx-receipt-").FullName;
        var archive = Path.Combine(root, "events.jsonl");
        try
        {
            var receipt = FileReportWriter.WriteFileReports(
                Lines, null, null, null, eventsPath: archive);

            Assert.True(receipt);
            // Exclusive reopen proves the successful receipt follows writer disposal.
            using var exclusive = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.None);
            var bytes = new byte[exclusive.Length];
            exclusive.ReadExactly(bytes);
            Assert.Equal(new UTF8Encoding(false).GetBytes(Lines[0]), bytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedRawWrite_OldReadableFileAndSuccessfulOtherReports_DoNotProvideReceipt()
    {
        var root = Directory.CreateTempSubdirectory("vouchfx-receipt-").FullName;
        var archive = Path.Combine(root, "events.jsonl");
        const string sentinel = "old-readable-archive";
        File.WriteAllText(archive, sentinel);
        try
        {
            using (var held = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var receipt = FileReportWriter.WriteFileReports(
                    Lines, null, Path.Combine(root, "report.html"), Path.Combine(root, "report.xml"),
                    eventsPath: archive);

                Assert.False(receipt);
                Assert.True(File.Exists(Path.Combine(root, "report.html")));
                Assert.True(File.Exists(Path.Combine(root, "report.xml")));
                Assert.Equal(sentinel, File.ReadAllText(archive));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad\0archive")]
    public void NoSuccessfulRawWriter_ReturnsFalse(string? archive)
    {
        Assert.False(FileReportWriter.WriteFileReports(
            Lines, null, null, null, eventsPath: archive));
    }

    [Fact]
    public void SuccessfullyDisposedEmptyArchive_ReturnsReceipt()
    {
        var root = Directory.CreateTempSubdirectory("vouchfx-receipt-").FullName;
        try
        {
            var archive = Path.Combine(root, "events.jsonl");
            Assert.True(FileReportWriter.WriteFileReports(
                Array.Empty<string>(), null, null, null, eventsPath: archive));
            Assert.Empty(File.ReadAllBytes(archive));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
