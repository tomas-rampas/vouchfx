// Tests for FileReportWriter — the shared file-report wiring seam (S09-D-01, T3).
//
// FileReportWriter.WriteFileReports is the single testable seam both runner paths
// (ScenarioRunner.RunSuiteAsync and ParallelSuiteRunner.RunParallelAsync) call AFTER
// their terminal render, writing the HTML and/or JUnit report from the SAME buffered
// JSON Lines event stream + the SAME diffLookup the terminal renderer already used —
// this IS the parity guarantee (all three renderers see identical input).
//
// These are the load-bearing wiring tests (Docker-free, no requires=docker trait):
//   • Both paths set  → both files created; HTML contains <!DOCTYPE html>, JUnit
//     parses as XML (XDocument.Parse succeeds).
//   • A null path     → no file written (today's behaviour: absent flag ⇒ no artifact).
//   • Parent dirs created when missing.
//   • UTF-8 WITHOUT BOM.
//   • The supplied diffLookup is threaded into the HTML render (HTML-only).

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Platform.Engine.Abstractions;
using Platform.Engine.Abstractions.Events;
using Platform.Engine.Reporting;
using Xunit;

namespace Platform.Engine.Reporting.Tests;

public sealed class FileReportWriterTests
{
    private static string Line<T>(T payload) => EventStreamJson.ToLine(payload);

    // A minimal, hand-built event buffer: one passing scenario with one passing step.
    private static string[] SampleBuffer() => new[]
    {
        Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "checkout", File = "checkout.e2e.yaml" }),
        Line(new StepStartedEvent { RunId = "run-1", StepId = "place-order", Kind = "http.rest" }),
        Line(new StepCompletedEvent { RunId = "run-1", StepId = "place-order", Verdict = Verdict.Pass, DurationMs = 12 }),
        Line(new ScenarioCompletedEvent
        {
            RunId = "run-1",
            ScenarioId = "checkout",
            Verdict = Verdict.Pass,
            Counts = new VerdictCounts { Pass = 1 },
        }),
    };

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-frw-" + Guid.NewGuid().ToString("n"));
        return dir;
    }

    [Fact]
    public void BothPaths_WriteBothFiles_WithValidContent()
    {
        var dir = NewTempDir();
        var htmlPath = Path.Combine(dir, "report.html");
        var junitPath = Path.Combine(dir, "report.xml");

        try
        {
            FileReportWriter.WriteFileReports(SampleBuffer(), diffLookup: null, htmlPath, junitPath);

            Assert.True(File.Exists(htmlPath), "HTML report should have been written.");
            Assert.True(File.Exists(junitPath), "JUnit report should have been written.");

            var html = File.ReadAllText(htmlPath);
            Assert.Contains("<!DOCTYPE html>", html, StringComparison.Ordinal);
            Assert.Contains("checkout", html, StringComparison.Ordinal);

            // The JUnit file must be well-formed XML — XDocument.Parse throws otherwise.
            var xml = File.ReadAllText(junitPath);
            var doc = XDocument.Parse(xml);
            Assert.Equal("testsuites", doc.Root!.Name.LocalName);
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
    public void NullHtmlPath_DoesNotWriteHtml_ButWritesJunit()
    {
        var dir = NewTempDir();
        var junitPath = Path.Combine(dir, "report.xml");

        try
        {
            FileReportWriter.WriteFileReports(SampleBuffer(), diffLookup: null, htmlPath: null, junitPath);

            Assert.True(File.Exists(junitPath), "JUnit report should have been written.");

            // Nothing else should be in the directory — no stray HTML artifact.
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
            Assert.DoesNotContain(files, f => f!.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
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
    public void NullJunitPath_DoesNotWriteJunit_ButWritesHtml()
    {
        var dir = NewTempDir();
        var htmlPath = Path.Combine(dir, "report.html");

        try
        {
            FileReportWriter.WriteFileReports(SampleBuffer(), diffLookup: null, htmlPath, junitPath: null);

            Assert.True(File.Exists(htmlPath), "HTML report should have been written.");

            var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
            Assert.DoesNotContain(files, f => f!.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
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
    public void BothNull_WritesNothing_AndDoesNotCreateDirectory()
    {
        var dir = NewTempDir();

        try
        {
            FileReportWriter.WriteFileReports(SampleBuffer(), diffLookup: null, htmlPath: null, junitPath: null);

            // No path ⇒ no artifact ⇒ the directory is never even created (today's behaviour).
            Assert.False(Directory.Exists(dir), "No report path was supplied, so no directory should be created.");
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
        // A nested path whose parent directory does not yet exist.
        var htmlPath = Path.Combine(dir, "nested", "deep", "report.html");
        var junitPath = Path.Combine(dir, "other", "report.xml");

        try
        {
            FileReportWriter.WriteFileReports(SampleBuffer(), diffLookup: null, htmlPath, junitPath);

            Assert.True(File.Exists(htmlPath), "HTML report (and its parent dirs) should have been created.");
            Assert.True(File.Exists(junitPath), "JUnit report (and its parent dirs) should have been created.");
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
        var htmlPath = Path.Combine(dir, "report.html");
        var junitPath = Path.Combine(dir, "report.xml");

        try
        {
            FileReportWriter.WriteFileReports(SampleBuffer(), diffLookup: null, htmlPath, junitPath);

            AssertNoBom(htmlPath);
            AssertNoBom(junitPath);
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
    public void Overwrites_ExistingFile()
    {
        var dir = NewTempDir();
        var junitPath = Path.Combine(dir, "report.xml");

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(junitPath, "stale content that must be fully replaced");

            FileReportWriter.WriteFileReports(SampleBuffer(), diffLookup: null, htmlPath: null, junitPath);

            var xml = File.ReadAllText(junitPath);
            Assert.DoesNotContain("stale content", xml, StringComparison.Ordinal);
            // Must be valid XML, i.e. the old text was not appended to.
            _ = XDocument.Parse(xml);
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
    public void HtmlReport_UsesSuppliedDiffLookup_ForFailedStep()
    {
        // A FAILED step carrying an observation: the HTML renderer invokes diffLookup
        // (kind, observation) and splices the returned diff under the step.  This proves
        // WriteFileReports threads the SAME diffLookup into the HTML render (parity).
        var observation = JsonSerializer.SerializeToElement(new { column = "status" });
        var buffer = new[]
        {
            Line(new ScenarioStartedEvent { RunId = "run-1", ScenarioId = "checkout" }),
            Line(new StepStartedEvent { RunId = "run-1", StepId = "assert-status", Kind = "db-assert.postgres" }),
            Line(new StepCompletedEvent
            {
                RunId = "run-1",
                StepId = "assert-status",
                Verdict = Verdict.Fail,
                DurationMs = 5,
                Observation = observation,
            }),
            Line(new ScenarioCompletedEvent
            {
                RunId = "run-1",
                ScenarioId = "checkout",
                Verdict = Verdict.Fail,
                Counts = new VerdictCounts { Fail = 1 },
            }),
        };

        const string sentinel = "EXPECTED_SHIPPED_GOT_PENDING";
        Func<string, JsonElement, string?> diffLookup = (kind, _) =>
            string.Equals(kind, "db-assert.postgres", StringComparison.Ordinal) ? sentinel : null;

        var dir = NewTempDir();
        var htmlPath = Path.Combine(dir, "report.html");

        try
        {
            FileReportWriter.WriteFileReports(buffer, diffLookup, htmlPath, junitPath: null);

            var html = File.ReadAllText(htmlPath);
            Assert.Contains(sentinel, html, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ── Bad-path resilience (the load-bearing tests) ────────────────────────────────────
    //
    // FileReportWriter is called by BOTH runners AFTER the verdict is computed but BEFORE the
    // SuiteResult is returned.  A caller-supplied bad --html / --junit path must NOT escape the
    // seam: if it did, it would discard the already-computed verdict and crash the process with
    // a non-verdict exit code — a typo'd report path could manufacture a CI failure from a
    // passing suite, or mask a real Fail.  Report writing must NEVER change the run's verdict /
    // exit code, so each report is written independently and the file-system / path exception
    // family is caught PER FILE, emitting a type-name-only diagnostic (§17 redaction).

    // A path that reliably trips the path / file-system family on every OS: an embedded NUL
    // makes Path.GetFullPath throw ArgumentException before any I/O is attempted.
    private const string InvalidPath = "bad\0path.html";

    [Fact]
    public void BadHtmlPath_DoesNotThrow_StillWritesJunit_AndDiagnoses()
    {
        var dir = NewTempDir();
        var junitPath = Path.Combine(dir, "report.xml");
        var diagnostics = new StringWriter();

        try
        {
            // One bad path (HTML) + one valid path (JUnit): the bad path must NOT abort the
            // valid write, and must NOT throw out of the seam.
            var ex = Record.Exception(() => FileReportWriter.WriteFileReports(
                SampleBuffer(), diffLookup: null, htmlPath: InvalidPath, junitPath, diagnostics));

            // (a) It does NOT throw — the verdict / exit code is unaffected.
            Assert.Null(ex);

            // (b) A single diagnostic line names the failure + the exception TYPE (never the
            //     message, never the resolved path).
            var diag = diagnostics.ToString();
            Assert.Contains("Report write failed for 'html'", diag, StringComparison.Ordinal);
            Assert.Contains("verdict unaffected", diag, StringComparison.Ordinal);
            Assert.Contains(nameof(ArgumentException), diag, StringComparison.Ordinal);

            // (c) The VALID JUnit file WAS still written — one bad path does not abort the other.
            Assert.True(File.Exists(junitPath), "JUnit report should still have been written.");
            _ = XDocument.Parse(File.ReadAllText(junitPath));
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
    public void BothPathsBad_DoesNotThrow_AndDiagnosesBoth()
    {
        var diagnostics = new StringWriter();

        // Both paths bad ⇒ no throw, no file, two diagnostics — the run's verdict survives.
        var ex = Record.Exception(() => FileReportWriter.WriteFileReports(
            SampleBuffer(),
            diffLookup: null,
            htmlPath: "bad\0html.html",
            junitPath: "bad\0junit.xml",
            diagnostics));

        Assert.Null(ex);

        var diag = diagnostics.ToString();
        Assert.Contains("Report write failed for 'html'", diag, StringComparison.Ordinal);
        Assert.Contains("Report write failed for 'junit'", diag, StringComparison.Ordinal);

        // Two distinct diagnostic lines (one per report).
        var lines = diag.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void BadJunitPath_DoesNotThrow_StillWritesHtml_AndDiagnoses()
    {
        var dir = NewTempDir();
        var htmlPath = Path.Combine(dir, "report.html");
        var diagnostics = new StringWriter();

        try
        {
            // Symmetric to the HTML case: a bad JUnit path must not abort the valid HTML write.
            var ex = Record.Exception(() => FileReportWriter.WriteFileReports(
                SampleBuffer(), diffLookup: null, htmlPath, junitPath: "bad\0junit.xml", diagnostics));

            Assert.Null(ex);

            var diag = diagnostics.ToString();
            Assert.Contains("Report write failed for 'junit'", diag, StringComparison.Ordinal);
            Assert.Contains(nameof(ArgumentException), diag, StringComparison.Ordinal);

            Assert.True(File.Exists(htmlPath), "HTML report should still have been written.");
            Assert.Contains("<!DOCTYPE html>", File.ReadAllText(htmlPath), StringComparison.Ordinal);
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
    public void BadPath_WithNullDiagnostics_StillDoesNotThrow()
    {
        // The diagnostics sink is optional; a bad path must be swallowed even with no sink.
        var ex = Record.Exception(() => FileReportWriter.WriteFileReports(
            SampleBuffer(), diffLookup: null, htmlPath: InvalidPath, junitPath: null, diagnostics: null));

        Assert.Null(ex);
    }

    // ── Events stream (the raw JSON Lines writer, S10) ──────────────────────────────────
    //
    // --events <path> re-emits the EXISTING buffered JSON Lines event stream VERBATIM (one
    // object per line) so a downstream tool (VSCode Test Explorer) can consume the per-step
    // stream.  It is the SAME `eventLines` buffer the terminal / HTML / JUnit renderers see —
    // this is the parity guarantee at the raw level: the file is byte-identical to the buffer.

    [Fact]
    public void EventsPath_WritesBufferVerbatim_NewlineJoined_NoBom()
    {
        var buffer = SampleBuffer(); // 4 lines.
        var dir = NewTempDir();
        var eventsPath = Path.Combine(dir, "events.jsonl");

        try
        {
            FileReportWriter.WriteFileReports(
                buffer, diffLookup: null, htmlPath: null, junitPath: null, eventsPath: eventsPath);

            Assert.True(File.Exists(eventsPath), "The events stream should have been written.");

            // VERBATIM: the file content is EXACTLY the buffer lines joined by '\n'.  Byte-compare
            // the raw bytes so a stray '\r', a BOM, or a trailing newline would all fail the test.
            var expected = string.Join("\n", buffer);
            var actualBytes = File.ReadAllBytes(eventsPath);
            var expectedBytes = new UTF8Encoding(false).GetBytes(expected);
            Assert.Equal(expectedBytes, actualBytes);

            // Explicit no-BOM assertion (the byte-compare above already excludes one, but this is
            // the load-bearing §17 / consumer-compat invariant, so assert it directly too).
            AssertNoBom(eventsPath);
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
    public void EventsPath_Absent_WritesNoFile()
    {
        var dir = NewTempDir();
        var htmlPath = Path.Combine(dir, "report.html");

        try
        {
            // No eventsPath supplied (defaulted null): the HTML report is written, but no events
            // artifact and nothing with a .jsonl extension.
            FileReportWriter.WriteFileReports(
                SampleBuffer(), diffLookup: null, htmlPath, junitPath: null, eventsPath: null);

            Assert.True(File.Exists(htmlPath), "HTML report should have been written.");

            var files = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
            Assert.DoesNotContain(files, f => f!.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
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
    public void EventsPath_CreatesMissingParentDirectories_AndOverwrites()
    {
        var dir = NewTempDir();
        // A nested path whose parent directory does not yet exist.
        var eventsPath = Path.Combine(dir, "nested", "deep", "events.jsonl");

        try
        {
            FileReportWriter.WriteFileReports(
                SampleBuffer(), diffLookup: null, htmlPath: null, junitPath: null, eventsPath: eventsPath);
            Assert.True(File.Exists(eventsPath), "Events stream (and its parent dirs) should have been created.");

            // Overwrite: a second write with a smaller buffer must fully replace the first.
            var smaller = new[]
            {
                Line(new ScenarioStartedEvent { RunId = "run-2", ScenarioId = "tiny" }),
            };
            FileReportWriter.WriteFileReports(
                smaller, diffLookup: null, htmlPath: null, junitPath: null, eventsPath: eventsPath);

            var content = File.ReadAllText(eventsPath);
            Assert.Equal(smaller[0], content);
            Assert.DoesNotContain("checkout", content, StringComparison.Ordinal);
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
    public void BadEventsPath_DoesNotThrow_StillWritesHtmlAndJunit_AndDiagnoses()
    {
        var dir = NewTempDir();
        var htmlPath = Path.Combine(dir, "report.html");
        var junitPath = Path.Combine(dir, "report.xml");
        var diagnostics = new StringWriter();

        try
        {
            // A bad events path must NOT abort the valid HTML / JUnit writes, and must NOT throw
            // out of the seam (a typo'd --events path must never change the run's verdict).
            var ex = Record.Exception(() => FileReportWriter.WriteFileReports(
                SampleBuffer(), diffLookup: null, htmlPath, junitPath, diagnostics, eventsPath: InvalidPath));

            // (a) It does NOT throw — the verdict / exit code is unaffected.
            Assert.Null(ex);

            // (b) A single diagnostic line names the failed report + the exception TYPE only
            //     (never the message, never the resolved path — §17 redaction).
            var diag = diagnostics.ToString();
            Assert.Contains("Report write failed for 'events'", diag, StringComparison.Ordinal);
            Assert.Contains("verdict unaffected", diag, StringComparison.Ordinal);
            Assert.Contains(nameof(ArgumentException), diag, StringComparison.Ordinal);
            // No path text leaked: the NUL-bearing path fragment must not appear in the diagnostic.
            Assert.DoesNotContain("bad", diag, StringComparison.Ordinal);

            // (c) The VALID HTML + JUnit files WERE still written — one bad path aborts neither.
            Assert.True(File.Exists(htmlPath), "HTML report should still have been written.");
            Assert.True(File.Exists(junitPath), "JUnit report should still have been written.");
            _ = XDocument.Parse(File.ReadAllText(junitPath));
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
    public void BadEventsPath_WithNullDiagnostics_StillDoesNotThrow()
    {
        // The diagnostics sink is optional; a bad events path must be swallowed even with no sink.
        var ex = Record.Exception(() => FileReportWriter.WriteFileReports(
            SampleBuffer(), diffLookup: null, htmlPath: null, junitPath: null, diagnostics: null,
            eventsPath: InvalidPath));

        Assert.Null(ex);
    }

    private static void AssertNoBom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var bom = Encoding.UTF8.GetPreamble(); // EF BB BF
        var hasBom = bytes.Length >= bom.Length
            && bytes[0] == bom[0] && bytes[1] == bom[1] && bytes[2] == bom[2];
        Assert.False(hasBom, $"{Path.GetFileName(path)} must be UTF-8 without a BOM.");
    }
}
