// Regression pin for #468 — the emitted mq-expect.kafka teardown must release its
// native librdkafka handle with Dispose() ALONE, on BOTH the plain and the Avro path.
//
// Why this is pinned rather than left to review: `consumer.Close()` reads like the
// courteous thing to do, so it is exactly the call a future edit re-adds "to leave the
// group cleanly".  It must not come back, and the reason is not a preference:
//
//   * IConsumer<K,V>.Close() in the pinned Confluent.Kafka 2.14.2 has NO CancellationToken
//     and NO TimeSpan overload, and the package binds rd_kafka_consumer_close (blocking),
//     not rd_kafka_consumer_close_queue.  The emitted consumer config sets no timeouts, so
//     every bound is a librdkafka default rather than anything this repo chose.
//   * Bounding that wait is FORBIDDEN by the §5 memory model, not merely unattractive.
//     Any Task.Run / Wait(timeout) shape compiles its lambda and display class into the
//     COLLECTIBLE submission assembly; RoslynScriptCompiler.RunIsolatedAsync calls
//     Unload() in the same finally the awaited body returns to, so an abandoned frame
//     defers that unload by exactly the unbounded interval the bound was meant to avoid.
//     Nothing observes the deferral, so the memory-leak CI gate goes red while production
//     merely grows — loud in CI, silent in production.  And an abandoned Close() would
//     race the finally's Dispose() on the same rd_kafka_t: a native use-after-free, which
//     disqualifies the shape without needing the §5 argument at all.  CsxAssembler states
//     the same rule ("no task abandonment").
//   * Dispose() is the vendor's supported teardown: for a consumer handle it routes to
//     rd_kafka_destroy_flags(handle, RD_KAFKA_DESTROY_F_NO_CONSUMER_CLOSE), which performs
//     NO leave-group round trip.  It is not free and is not claimed to be — it still
//     terminates the handle's internal threads and returns when they are joined, and that
//     cost is UNMEASURED for this path.  #367's 16 ms / 94-110 ms figures do NOT transfer:
//     they are the PRODUCER handle (the same ReleaseHandle takes plain rd_kafka_destroy for
//     a producer, destroy_flags only for a consumer) and both probes ran against brokers
//     the client never reached, so no fetcher or coordinator thread existed to join.
//   * Removing Close() is not a new code path.  Measured from the IL: Close() is
//     `ConsumerClose(); Dispose(true); GC.SuppressFinalize(this)`, and ConsumerClose()
//     throws on any non-zero ErrorCode from rd_kafka_consumer_close.  So whenever the
//     broker was uncooperative — the case #468 is about — the throw skipped Close()'s own
//     Dispose(true), the old bare `catch {}` swallowed it, and the handle was released by
//     the outer Dispose(), i.e. by exactly the mechanism that is now the only one.
//   * Unsubscribe() is the one real alternative and it was considered, not overlooked: the
//     pinned package's own Consumer`2.Dispose remarks name it alongside Close().  It is
//     rejected because rd_kafka_unsubscribe returns once the request is enqueued, so an
//     Unsubscribe() immediately followed by destroy_flags most likely tears the handle down
//     before the LeaveGroup is transmitted — inferred, not probed.  It carries none of
//     Close()'s disqualifiers, so it is the candidate to revisit if the joined-consumer
//     fault-injection harness ever lands.  Full adjudication in the provider's remarks.
//
// Re-adding Close() therefore re-adds an uncancellable native call to the step's critical
// path, and the only "bound" for it is one the memory model forbids.
//
// These tests are non-docker: they read the EMITTED CSX text, not a live broker.  The scan
// is line-scoped and never truncates a line (see AssertNoCloseCall), because a stripper
// that cut each line at its first "//" could delete a re-added Close() that shared a line
// with a "//" — a URL literal being the realistic shape — while consumer.Dispose(), on a
// different line, still passed.  Comment lines are skipped whole; the prose below and in
// the provider deliberately contains the text "Close()", so an unfiltered raw search would
// go RED on the pin's own documentation rather than green on it.
using System;
using System.Collections.Generic;
using System.Linq;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Steps.MqExpect.Kafka.Tests;

/// <summary>
/// Pins the emitted <c>MqExpectKafka_Helpers</c> teardown shape for #468: exactly one
/// <c>consumer.Dispose()</c> and no <c>Close()</c> call, on the plain path and the Avro
/// path alike.
/// </summary>
public sealed class MqExpectKafkaTeardownPinTests
{
    /// <summary>Declaration site of the plain (string/string) consume helper.</summary>
    private const string PlainDecl =
        "public static async System.Threading.Tasks.Task ExpectAsync(";

    /// <summary>Declaration site of the Avro (GenericRecord) consume helper.</summary>
    private const string AvroDecl =
        "private static async System.Threading.Tasks.Task ExpectAvroAsync(";

    /// <summary>
    /// The failure text every assertion in this file shares.  An assertion that only says
    /// "expected no Close()" teaches the next reader nothing and invites them to delete
    /// the test; this says why the call cannot come back.  It deliberately carries no
    /// timing figure — the numbers involved are librdkafka defaults this suite has not
    /// probed, and the argument does not need them.
    /// </summary>
    private const string WhyNoClose =
        "#468: the emitted mq-expect.kafka teardown must be Dispose() ONLY. " +
        "IConsumer<K,V>.Close() in the pinned Confluent.Kafka 2.14.2 takes no " +
        "CancellationToken and no TimeSpan (it binds the blocking rd_kafka_consumer_close), " +
        "and the emitted consumer config sets no timeouts — so a wedged broker holds the " +
        "step for a librdkafka default with nothing able to cut it. Bounding it is " +
        "forbidden, not merely unattractive: a Task.Run/Wait(timeout) shape abandons a " +
        "frame whose lambda lives in the COLLECTIBLE submission assembly, and " +
        "RunIsolatedAsync unloads that ALC in the same finally the body returns to, so the " +
        "unload is deferred by exactly the unbounded interval the bound was meant to avoid " +
        "— unobserved, so the leak gate reddens in CI while production merely grows — and " +
        "the abandoned Close() would additionally race the finally's Dispose() on the same " +
        "rd_kafka_t, a native use-after-free (§5). Dispose() routes to " +
        "rd_kafka_destroy_flags(handle, RD_KAFKA_DESTROY_F_NO_CONSUMER_CLOSE), which " +
        "performs no leave-group round trip; it still joins the handle's internal threads, " +
        "so it is bounded, not free, and its cost on this path is unmeasured (#367's " +
        "figures are the producer handle against a never-connected broker and do not " +
        "transfer). Removing Close() is not a new path: on the failure case #468 is about, " +
        "ConsumerClose() threw, the old bare catch swallowed it, and the handle was " +
        "already released by that same Dispose(). The trade is that the group keeps a " +
        "silent member until the broker evicts it, then empties as Close() would have made " +
        "it empty — a delay, no persistent state — and it is a throwaway single-member " +
        "group per attempt with auto-commit off, so Close() bought nothing. Unsubscribe() " +
        "was adjudicated and rejected (see the provider remarks), not overlooked. If you " +
        "need the group left promptly, that is a redesign, not a re-added call.";

    private readonly MqExpectKafkaProvider _provider = new();

    // ── The pin ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Neither emitted consume path may contain a <c>Close()</c> CALL, and each must still
    /// release its consumer via an explicit <c>Dispose()</c> in the <c>finally</c>.
    /// </summary>
    /// <param name="path">Which emitted helper method to inspect.</param>
    /// <param name="expectRegistryDispose">
    /// Whether the region is expected to also dispose the schema-registry client.  This is
    /// the region-identity guard: it is true only for the Avro path, so a broken split
    /// (both cases silently reading the same region) fails rather than passes.
    /// </param>
    [Theory]
    [InlineData("plain", false)]
    [InlineData("avro", true)]
    public void EmittedTeardown_ReleasesConsumerWithDisposeAlone(
        string path,
        bool expectRegistryDispose)
    {
        var region = RegionFor(path);

        AssertNoMultiLineStringLiteral(region, path);
        AssertNoCloseCall(region, path);

        Assert.True(
            CodeLines(region).Any(l => l.Contains("consumer.Dispose();", StringComparison.Ordinal)),
            $"The emitted '{path}' path no longer disposes its consumer. The native " +
            "librdkafka handle must be released inside the step, before the collectible " +
            "AssemblyLoadContext unloads (§5); 'using var' is illegal in a Roslyn script " +
            "body, so it has to be an explicit Dispose() in the finally (§13.3.1).");

        var disposesRegistry = CodeLines(region)
            .Any(l => l.Contains("registry.Dispose();", StringComparison.Ordinal));

        Assert.True(
            disposesRegistry == expectRegistryDispose,
            $"Region-identity guard failed for '{path}': expected registry.Dispose() " +
            $"present={expectRegistryDispose} but found present={disposesRegistry}. Only " +
            "the Avro path builds a CachedSchemaRegistryClient, so this is how the test " +
            "proves the region split actually isolated one consume path. A mismatch means " +
            "the split is wrong — most likely a renamed or reordered helper method — and " +
            "every other assertion in this case is therefore reading the wrong text, " +
            "including the Close() check. Fix the split before trusting the run.");
    }

    /// <summary>
    /// Guards the pin above against going vacuous: if either helper method is renamed, the
    /// region split would silently return an empty or whole-helper slice and the
    /// <c>Close()</c> assertion would stop meaning anything.
    /// </summary>
    [Fact]
    public void EmittedHelper_DeclaresBothConsumePaths_ExactlyOnceEach()
    {
        var helper = HelperSource();

        Assert.Equal(1, CountOf(helper, PlainDecl));
        Assert.Equal(1, CountOf(helper, AvroDecl));
        Assert.True(
            helper.IndexOf(PlainDecl, StringComparison.Ordinal)
                < helper.IndexOf(AvroDecl, StringComparison.Ordinal),
            "ExpectAsync is expected to be declared before ExpectAvroAsync; the region " +
            "split in this file assumes that order.");
    }

    // ── Assertions ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fails if any CODE line in the region contains a <c>.Close(</c> call.
    /// </summary>
    /// <remarks>
    /// Line-scoped by construction: a line is either skipped whole (it is nothing but a
    /// comment) or examined in full.  No line is ever truncated, so no code can be hidden
    /// behind a <c>//</c> that shares its line — the defect a cut-at-first-<c>//</c>
    /// stripper would have.  A <c>.Close(</c> mentioned in a TRAILING comment on a code
    /// line therefore fails the pin; that is the safe direction for a regression pin, and
    /// it is why the provider keeps its <c>Close()</c> prose on whole comment lines.
    /// Residual limits, stated rather than hidden.  This is a textual pin over emitted
    /// source, so it cannot see a call reached through a delegate or reflection, nor one
    /// split across a line break between the receiver and the member name.  Its REACH is
    /// narrower than "the emitted CSX" too: the two regions run from
    /// <c>ExpectAsync</c> to the end of the helper class, so nothing declared BEFORE
    /// <c>ExpectAsync</c> is scanned, and <see cref="CsxFragment.StatementBlock"/> — the
    /// per-step block that calls into the helper — is never scanned at all.  A
    /// <c>Close()</c> re-added in either place would not be caught here.  Both are
    /// currently free of consumer handling, which is why the regions are drawn where they
    /// are; widen them if that stops being true.
    /// </remarks>
    private static void AssertNoCloseCall(string region, string path)
    {
        foreach (var line in CodeLines(region))
        {
            Assert.True(
                !line.Contains(".Close(", StringComparison.Ordinal),
                $"The emitted '{path}' path calls Close() on its consumer:\n" +
                $"    {line.Trim()}\n" +
                WhyNoClose);
        }
    }

    /// <summary>
    /// Asserts the ONE precondition <see cref="CodeLines"/> depends on: that a line whose
    /// trimmed text starts with <c>//</c> really is a comment.  That can only be false if a
    /// string literal spans lines, so this checks the equivalent, checkable property —
    /// every line of the emitted helper closes every quote it opens.  Asserting it turns
    /// the scan's safety from "true of today's emitted text" into a property this suite
    /// enforces on every future edit.
    /// </summary>
    private static void AssertNoMultiLineStringLiteral(string region, string path)
    {
        var number = 0;
        foreach (var line in region.Split('\n'))
        {
            number++;
            var quotes = 0;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '\\')
                {
                    i++;    // skip whatever this escape covers, including \" and \\
                    continue;
                }

                if (line[i] == '"')
                    quotes++;
            }

            Assert.True(
                quotes % 2 == 0,
                $"Line {number} of the emitted '{path}' path leaves a string literal open:\n" +
                $"    {line.Trim()}\n" +
                "A multi-line string literal breaks this file's comment detection: a " +
                "continuation line beginning with '//' would be skipped as a comment even " +
                "though it is literal text, and a re-added Close() could hide there. " +
                "Either keep emitted string literals on one line, or replace CodeLines " +
                "with a real lexer before relaxing this.");
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// The region's lines with whole-line comments removed.  Lines are never truncated —
    /// see <see cref="AssertNoCloseCall"/> for why that matters.
    /// </summary>
    private static IEnumerable<string> CodeLines(string region)
        => region.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

    /// <summary>
    /// Emits the fragment and returns the provider-local <c>MqExpectKafka_Helpers</c> class
    /// source.  The helper is interpolation-free (§13.3.1), so the emitted text is the same
    /// for every model — one Emit covers both paths.
    /// </summary>
    private string HelperSource()
    {
        var model = new MqExpectKafkaModel(
            Target: "events-bus",
            Topic: "orders.created",
            Match: new KafkaMatch(Key: null, Headers: null, PayloadContains: "x", Json: null));

        var fragment = _provider.Emit(model, new StubCompileContext("pin-step"));

        return fragment.RequiredHelpers.Single(
            h => h.StartsWith("static class MqExpectKafka_Helpers", StringComparison.Ordinal));
    }

    /// <summary>Slices the helper source down to one consume path's body.</summary>
    private string RegionFor(string path)
    {
        var helper = HelperSource();
        var avroStart = helper.IndexOf(AvroDecl, StringComparison.Ordinal);
        Assert.True(avroStart >= 0, "ExpectAvroAsync declaration not found in the helper.");

        if (path == "avro")
            return helper[avroStart..];

        var plainStart = helper.IndexOf(PlainDecl, StringComparison.Ordinal);
        Assert.True(plainStart >= 0, "ExpectAsync declaration not found in the helper.");
        return helper[plainStart..avroStart];
    }

    /// <summary>Counts non-overlapping ordinal occurrences of <paramref name="needle"/>.</summary>
    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>Minimal <see cref="ICompileContext"/> for emit-only inspection.</summary>
    private sealed class StubCompileContext : ICompileContext
    {
        public StubCompileContext(string stepId) => StepId = stepId;

        /// <inheritdoc />
        public string SuiteDirectory => System.IO.Directory.GetCurrentDirectory();

        /// <inheritdoc />
        public string StepId { get; }

        /// <inheritdoc />
        public string SuiteNamespace => "Generated";

        /// <inheritdoc />
        public IReadOnlyDictionary<string, string> Captures { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, CaptureExpr> CaptureExprs { get; } =
            new Dictionary<string, CaptureExpr>(StringComparer.Ordinal);
    }
}
