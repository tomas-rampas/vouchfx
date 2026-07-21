// Tests for EventStreamAppender — the incremental, tailable `--events-stream` sink (#258).
//
// Contrast FileReportWriterTests (the END-OF-RUN, byte-frozen `--events` archive, left
// completely unchanged by this feature): these tests exercise the NEW, separate type that
// appends the SAME EventStreamJson.ToLine records INCREMENTALLY, keeping the file open across
// many AppendLines calls so a concurrent reader can tail it.
//
// Load-bearing behaviours asserted here:
//   • A concurrent reader can open the file with FileShare.ReadWrite WHILE the appender still
//     holds its own write handle open (no Windows sharing violation) — this is the entire
//     point of FileShare.Read on the appender's FileStream, contrasted with FileReportWriter's
//     FileShare.None.
//   • Every appended line ends with a literal '\n' (proper JSON Lines), including the last.
//   • Content is flushed and visible to another reader BEFORE Dispose is called.
//   • UTF-8 WITHOUT a BOM.
//   • A bad path is swallowed at construction (no throw) and a diagnostic is emitted; every
//     subsequent AppendLines call is then a harmless no-op.
//   • A bad path mid-append (file removed / handle failure) is swallowed the same way.

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Vouchfx.Engine.Reporting;
using Xunit;

namespace Vouchfx.Engine.Reporting.Tests;

public sealed class EventStreamAppenderTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-esa-" + Guid.NewGuid().ToString("n"));
        return dir;
    }

    private static void AssertNoBom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = Encoding.UTF8.GetPreamble(); // EF BB BF
        var hasBom = bytes.Length >= bom.Length
            && bytes[0] == bom[0] && bytes[1] == bom[1] && bytes[2] == bom[2];
        Assert.False(hasBom, $"{Path.GetFileName(path)} must be UTF-8 without a BOM.");
    }

    [Fact]
    public void AppendLines_WritesEachLine_TerminatedWithLineFeed()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            using var appender = new EventStreamAppender(path);

            var lines = new[] { "{\"a\":1}", "{\"b\":2}" };
            appender.AppendLines(lines);
            appender.Dispose();

            var text = File.ReadAllText(path);
            // Every line — INCLUDING the last — ends with '\n' (proper JSON Lines), unlike the
            // '\n'-JOINED, no-trailing-newline --events archive.
            Assert.Equal("{\"a\":1}\n{\"b\":2}\n", text);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void AppendLines_UsesLiteralLineFeed_NeverCarriageReturn()
    {
        // Regression guard: StreamWriter.WriteLine's NewLine is "\r\n" on Windows. The appender
        // must write a bare '\n' explicitly so the file matches the '\n'-only JSON Lines
        // contract regardless of platform.
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            using var appender = new EventStreamAppender(path);
            var lines = new[] { "{\"a\":1}" };
            appender.AppendLines(lines);
            appender.Dispose();

            var bytes = File.ReadAllBytes(path);
            Assert.DoesNotContain((byte)'\r', bytes);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void AppendLines_AcrossMultipleCalls_AccumulatesInOrder()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            using var appender = new EventStreamAppender(path);

            var first = new[] { "scenario-1-line-1", "scenario-1-line-2" };
            var second = new[] { "scenario-2-line-1" };
            appender.AppendLines(first);
            appender.AppendLines(second);
            appender.Dispose();

            var text = File.ReadAllText(path);
            Assert.Equal("scenario-1-line-1\nscenario-1-line-2\nscenario-2-line-1\n", text);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void AppendLines_FlushesBeforeDispose_SoAConcurrentReaderSeesItImmediately()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            using var appender = new EventStreamAppender(path);
            var lines = new[] { "{\"scenarioId\":\"checkout\"}" };
            appender.AppendLines(lines);

            // Read back WHILE the appender is still open (no Dispose yet) — proves the write
            // was flushed, not sitting in a buffered writer waiting for close.
            using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var textReader = new StreamReader(reader))
            {
                var content = textReader.ReadToEnd();
                Assert.Equal("{\"scenarioId\":\"checkout\"}\n", content);
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void ConcurrentReader_CanOpenWithFileShareReadWrite_WhileAppenderHoldsWriteHandle()
    {
        // The load-bearing contrast with FileReportWriter.CreateWriter (FileShare.None): a
        // `tail -f`-style reader must be able to open the SAME path for read access while the
        // appender's write handle is still open — this is the whole point of FileShare.Read.
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            using var appender = new EventStreamAppender(path);
            var lines = new[] { "{\"scenarioId\":\"first\"}" };
            appender.AppendLines(lines);

            // Opening for READ with FileShare.ReadWrite must NOT throw an IOException sharing
            // violation while the write handle above is still open.
            var ex = Record.Exception(() =>
            {
                using var reader = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var textReader = new StreamReader(reader);
                _ = textReader.ReadToEnd();
            });

            Assert.Null(ex);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void WritesUtf8WithoutBom()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            using var appender = new EventStreamAppender(path);
            var lines = new[] { "{\"a\":1}" };
            appender.AppendLines(lines);
            appender.Dispose();

            AssertNoBom(path);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void OpensOnce_TruncatingAStaleFileFromAPreviousRun()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, "stale content from a previous run that must not survive");

        try
        {
            using var appender = new EventStreamAppender(path);
            var lines = new[] { "{\"a\":1}" };
            appender.AppendLines(lines);
            appender.Dispose();

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("stale content", text, StringComparison.Ordinal);
            Assert.Equal("{\"a\":1}\n", text);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void CreatesMissingParentDirectories()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "nested", "deep", "events.jsonl");

        try
        {
            using var appender = new EventStreamAppender(path);
            var lines = new[] { "{\"a\":1}" };
            appender.AppendLines(lines);
            appender.Dispose();

            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void EmptyLines_IsANoOp()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            using var appender = new EventStreamAppender(path);
            appender.AppendLines(Array.Empty<string>());
            appender.Dispose();

            // The file was still opened/created (FileMode.Create), just with no content
            // appended.
            Assert.Equal(string.Empty, File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            var appender = new EventStreamAppender(path);
            var lines = new[] { "{\"a\":1}" };
            appender.AppendLines(lines);

            var ex = Record.Exception(() =>
            {
                appender.Dispose();
                appender.Dispose();
            });

            Assert.Null(ex);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_ClosesTheFile_SoALaterExclusiveOpenSucceeds()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            var appender = new EventStreamAppender(path);
            var lines = new[] { "{\"a\":1}" };
            appender.AppendLines(lines);
            await appender.DisposeAsync();

            // If the handle were still open, an exclusive open (FileShare.None) would throw.
            var ex = Record.Exception(() =>
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });

            Assert.Null(ex);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ── Bad-path resilience (the load-bearing tests, mirroring FileReportWriterTests) ──────
    //
    // A caller-supplied bad --events-stream path must never throw out of the constructor or
    // AppendLines, and must never affect the run's verdict / exit code — exactly the discipline
    // FileReportWriter already applies to --html / --junit / --events.

    // An embedded NUL makes Path.GetFullPath throw ArgumentException before any I/O happens —
    // reliable across OSes, mirroring FileReportWriterTests.InvalidPath.
    private const string InvalidPath = "bad\0path.jsonl";

    [Fact]
    public void BadPath_AtConstruction_DoesNotThrow_AndDiagnoses()
    {
        var diagnostics = new StringWriter();

        var ex = Record.Exception(() => new EventStreamAppender(InvalidPath, diagnostics));

        Assert.Null(ex);

        var diag = diagnostics.ToString();
        Assert.Contains("Events-stream open failed", diag, StringComparison.Ordinal);
        Assert.Contains("verdict unaffected", diag, StringComparison.Ordinal);
        Assert.Contains(nameof(ArgumentException), diag, StringComparison.Ordinal);
        // No path text leaked (§17 redaction — type name only).
        Assert.DoesNotContain("bad", diag, StringComparison.Ordinal);
    }

    [Fact]
    public void BadPath_WithNullDiagnostics_StillDoesNotThrow()
    {
        var ex = Record.Exception(() => new EventStreamAppender(InvalidPath, diagnostics: null));

        Assert.Null(ex);
    }

    [Fact]
    public void BadPath_SubsequentAppendLines_IsASilentNoOp()
    {
        var diagnostics = new StringWriter();
        using var appender = new EventStreamAppender(InvalidPath, diagnostics);

        // The constructor already diagnosed the failed open; AppendLines must not throw and
        // must not emit a second diagnostic for the same dead sink.
        var diagAfterConstruction = diagnostics.ToString();
        var lines = new[] { "{\"a\":1}" };

        var ex = Record.Exception(() => appender.AppendLines(lines));

        Assert.Null(ex);
        Assert.Equal(diagAfterConstruction, diagnostics.ToString());
    }

    [Fact]
    public void BadPath_Dispose_DoesNotThrow()
    {
        var appender = new EventStreamAppender(InvalidPath, diagnostics: null);

        var ex = Record.Exception(() => appender.Dispose());

        Assert.Null(ex);
    }

    // ── Mid-run failure: symmetric self-disable (peer-review follow-up, #258) ──────────────
    //
    // Before this fix, a write/flush failure AFTER a successful construction was diagnosed but
    // did NOT null out the writer, so a sink that died mid-suite (destination deleted, disk
    // full, removable media unplugged) was retried — and re-diagnosed — on every subsequent
    // scenario. The fix makes the self-disable SYMMETRIC with the construction-failure path:
    // exactly ONE diagnostic total, then every later AppendLines call is a silent no-op.
    //
    // Deterministic trigger: SimulateBrokenSinkForTests (internal, test-only) disposes the
    // underlying FileStream out from under the appender WITHOUT going through Dispose, so the
    // next AppendLines call reaches a real write against a closed stream and throws
    // ObjectDisposedException — exercising exactly the catch path a genuine mid-run I/O failure
    // (disk full, media removed, destination deleted) would take, without needing to fabricate
    // a real unrecoverable disk condition (which is not reliably triggerable cross-platform).

    [Fact]
    public void MidRunAppendFailure_EmitsExactlyOneDiagnostic_AcrossManyLaterCalls()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");
        var diagnostics = new StringWriter();

        try
        {
            using var appender = new EventStreamAppender(path, diagnostics);
            var firstLines = new[] { "{\"scenarioId\":\"first\"}" };
            appender.AppendLines(firstLines);
            Assert.Equal(string.Empty, diagnostics.ToString());

            // Break the sink out from under the appender, then attempt several MORE scenario
            // completions — as a live suite would keep calling AppendLines after the sink died.
            appender.SimulateBrokenSinkForTests();

            var second = new[] { "{\"scenarioId\":\"second\"}" };
            var third = new[] { "{\"scenarioId\":\"third\"}" };
            var fourth = new[] { "{\"scenarioId\":\"fourth\"}" };

            var ex1 = Record.Exception(() => appender.AppendLines(second));
            var ex2 = Record.Exception(() => appender.AppendLines(third));
            var ex3 = Record.Exception(() => appender.AppendLines(fourth));

            Assert.Null(ex1);
            Assert.Null(ex2);
            Assert.Null(ex3);

            var diag = diagnostics.ToString();
            Assert.Contains("Events-stream append failed", diag, StringComparison.Ordinal);
            Assert.Contains("verdict unaffected", diag, StringComparison.Ordinal);
            Assert.Contains(nameof(ObjectDisposedException), diag, StringComparison.Ordinal);

            // Exactly ONE diagnostic line, even though three MORE scenarios tried to append
            // after the sink broke — not one per remaining scenario.
            var lines = diag.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Single(lines);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void MidRunAppendFailure_LeavesNoTruncatedLineAbuttingALaterAppend()
    {
        // Regression guard for the "partial write abuts the next append" risk the fix closes:
        // once the sink self-disables, a later AppendLines call must contribute NO further bytes
        // to the file — so whatever was flushed before the break is exactly what remains, with a
        // clean line-feed boundary and nothing appended after it.
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            using var appender = new EventStreamAppender(path);
            var firstLines = new[] { "{\"scenarioId\":\"first\"}" };
            appender.AppendLines(firstLines);

            appender.SimulateBrokenSinkForTests();

            var laterLines = new[] { "{\"scenarioId\":\"later\"}" };
            appender.AppendLines(laterLines);
            appender.AppendLines(laterLines);

            var text = File.ReadAllText(path);
            Assert.Equal("{\"scenarioId\":\"first\"}\n", text);
            Assert.DoesNotContain("later", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void MidRunAppendFailure_DisposeAfterwards_StillDoesNotThrow()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "events.jsonl");

        try
        {
            var appender = new EventStreamAppender(path);
            var lines = new[] { "{\"a\":1}" };
            appender.AppendLines(lines);
            appender.SimulateBrokenSinkForTests();

            // The writer is already nulled out by the self-disable; Dispose must still be a
            // harmless no-op (mirrors Dispose_IsIdempotent / BadPath_Dispose_DoesNotThrow).
            var ex = Record.Exception(() => appender.Dispose());

            Assert.Null(ex);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
