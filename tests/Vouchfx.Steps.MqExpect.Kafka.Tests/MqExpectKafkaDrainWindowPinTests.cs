// Regression pin for #493 — the emitted mq-expect.kafka per-attempt drain window is an
// author-facing PUBLISHED number, and this file is the only thing keeping the code and
// that published number in step.
//
// #493 was resolved by documenting the single-shot drain rather than by changing it: the
// poll still stops at its loop guard regardless of a declared step `timeout`, and the
// contract is stated to authors instead.  That decision put the same number in three
// places no compiler relates to one another:
//
//   1. MqExpectKafkaProvider.SchemaFragment's root `description` — "bounded at about one
//      second".  Authors read it in editor completion and in the composed v1 schema.
//   2. docs/language-reference.md — GENERATED from that description, so it repeats the
//      claim verbatim.  It is never hand-edited.
//   3. The `drainWindowMs` key written into every Pass/Fail observation the step emits,
//      which lands in the terminal report and in the JSON Lines event stream.
//
// All three are backed by ONE emitted constant, MqExpectKafka_Helpers.DrainWindowMs.
// Change it to 5000 and nothing else moves: the build is green, the schema freeze golden
// is green, the language-reference golden is green — and the documentation quietly starts
// lying to authors about how long their step waits.  A constant standing behind three
// published claims with no gate between them is precisely the prose-expiry failure class,
// so the gate is here.
//
// WHAT THIS FILE IS NOT.  Every assertion below is a SOURCE-LEVEL assertion over the
// emitted helper TEXT.  They prove the emitted code reads one constant, and that the
// constant agrees with the published description.  They do NOT prove the drain actually
// stops after that many milliseconds: that is a wall-clock property of a live consumer
// against a real broker and would need a Docker-trait test.  Nothing here measures the
// window, and no failure message should be read as claiming otherwise.
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vouchfx.Sdk.Testing.Contexts;
using Xunit;

namespace Vouchfx.Steps.MqExpect.Kafka.Tests;

/// <summary>
/// Pins the emitted <c>MqExpectKafka_Helpers.DrainWindowMs</c> constant: that it is
/// declared exactly once, that every deadline and every observation reads it instead of a
/// literal, and that its value agrees with the drain window the provider's schema
/// <c>description</c> publishes to authors.
/// </summary>
public sealed partial class MqExpectKafkaDrainWindowPinTests
{
    /// <summary>
    /// The claim the constant backs, quoted from the provider's root schema
    /// <c>description</c>.  Pinning the phrase is deliberate and its cost is understood: a
    /// reword of that sentence reddens this test.  That is the mechanism, not a side
    /// effect — the phrase and the constant are one fact stored twice, and the only way a
    /// compiler-free coupling can be enforced is to assert both ends together.
    /// </summary>
    private const string PublishedWindowClaim = "bounded at about one second";

    /// <summary>The value <see cref="PublishedWindowClaim"/> commits the code to.</summary>
    private const int PublishedWindowMs = 1000;

    /// <summary>
    /// Number of consume paths in the emitted helper: the plain (string/string) path and
    /// the Avro (GenericRecord) path.  Each owns one deadline.
    /// </summary>
    private const int ConsumePaths = 2;

    /// <summary>
    /// Number of match-outcome observation writes: a Pass and a Fail branch on each of the
    /// <see cref="ConsumePaths"/> paths.  Every one of them reports the window.
    /// </summary>
    private const int ObservationWrites = 4;

    /// <summary>
    /// The failure text every assertion in this file shares.  An assertion that only says
    /// "expected 1000" tells the next reader nothing about why they cannot simply change
    /// the number; this names the other two artefacts that move with it.
    /// </summary>
    private const string WhatElseMoves =
        "#493: MqExpectKafka_Helpers.DrainWindowMs is the single source of the mq-expect.kafka "
        + "per-attempt drain window, and that window is PUBLISHED in three places. Changing the "
        + "constant means changing all of them, in this order: (1) the root `description` in "
        + "MqExpectKafkaProvider.SchemaFragment, which currently tells authors the drain is "
        + $"\"{PublishedWindowClaim}\"; (2) the composed schema golden — regenerate with "
        + "VOUCHFX_REGEN_SCHEMA=1 dotnet test tests/Vouchfx.Engine.Compilation.Tests, WITHOUT a "
        + "--filter and WITHOUT --no-build; (3) docs/language-reference.md, which is GENERATED "
        + "from that description and must never be hand-edited — regenerate with "
        + "VOUCHFX_REGEN_LANGUAGE_REFERENCE=1. The emitted observation's `drainWindowMs` key "
        + "follows the constant automatically and needs no edit. If you are changing the number "
        + "because a declared step `timeout` should widen the window: that is not a constant "
        + "change, it is a reversal of #493's decision — the drain is single-shot across the "
        + "whole mq-expect family, and waiting is verifyMode: RETRY's job.";

    private readonly MqExpectKafkaProvider _provider = new();

    // ── The pin ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The helper declares the drain window exactly once, and its value is the one the
    /// provider's schema <c>description</c> publishes.
    /// </summary>
    /// <remarks>
    /// Both halves are asserted in one test on purpose.  Separately, each is satisfiable
    /// while the pair is inconsistent — a constant of 5000 passes an "is declared once"
    /// check, and the description passes a "says one second" check — and it is exactly
    /// that inconsistency, between the emitted code and the published claim, that #493
    /// exists to prevent.
    /// </remarks>
    [Fact]
    public void EmittedHelper_DeclaresOneDrainWindow_AgreeingWithThePublishedDescription()
    {
        var declarations = DrainWindowDeclarationPattern().Matches(HelperSource());

        Assert.True(
            declarations.Count == 1,
            $"Expected exactly ONE DrainWindowMs declaration in the emitted helper, found "
            + $"{declarations.Count}. A second declaration means two paths can drain for "
            + $"different lengths of time while one description speaks for both.\n{WhatElseMoves}");

        var literal = declarations[0].Groups["ms"].Value;

        Assert.True(
            int.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var declared),
            $"The emitted DrainWindowMs literal '{literal}' is not a readable int, so this "
            + $"pin cannot compare it with the published window.\n{WhatElseMoves}");

        Assert.True(
            declared == PublishedWindowMs,
            $"The emitted helper drains for {declared} ms, but the provider's schema "
            + $"description still publishes \"{PublishedWindowClaim}\" "
            + $"({PublishedWindowMs} ms) to authors.\n{WhatElseMoves}");

        Assert.True(
            RootDescription().Contains(PublishedWindowClaim, StringComparison.Ordinal),
            $"The provider's root schema description no longer contains "
            + $"\"{PublishedWindowClaim}\", so this pin can no longer prove the emitted "
            + "constant matches what authors are told. Either restore the claim or update "
            + $"PublishedWindowClaim/PublishedWindowMs here to the new one.\n{WhatElseMoves}");
    }

    /// <summary>
    /// The <c>AddSeconds(</c> literal the constant replaced may not return.
    /// </summary>
    /// <remarks>
    /// The scan is over the RAW emitted text, comments included, and that is a deliberate
    /// choice rather than an oversight: no prose in the emitted helper needs to name
    /// <c>AddSeconds</c>, so there is nothing legitimate for a raw scan to trip over, and a
    /// raw scan cannot be defeated by moving the call onto a line that also carries a
    /// <c>//</c>.  If a future comment genuinely needs the token, that is the moment to
    /// narrow this to code lines — not to delete the assertion.
    /// </remarks>
    [Fact]
    public void EmittedHelper_ContainsNoAddSecondsLiteral()
    {
        Assert.True(
            Occurrences(HelperSource(), "AddSeconds(") == 0,
            "The emitted helper calls AddSeconds( — the hard-coded deadline the "
            + "DrainWindowMs constant replaced. A deadline expressed as a literal cannot be "
            + $"reported as `drainWindowMs`, so the two silently diverge.\n{WhatElseMoves}");
    }

    /// <summary>
    /// Every deadline and every observation write reads the constant — and there are still
    /// as many of each as this pin was written to cover.
    /// </summary>
    /// <remarks>
    /// The counts are asserted, not just the reads, and that is the point of the test: a
    /// pin that only checked "some deadline uses the constant" would keep passing after a
    /// third consume path arrived with a literal of its own, covering less than it did
    /// while looking exactly as green.  The identity guard on the two method declarations
    /// is what stops the counts being satisfied by the wrong text.
    /// </remarks>
    [Fact]
    public void EmittedHelper_EveryDeadlineAndObservation_ReadsTheConstant()
    {
        var helper = HelperSource();

        // Identity guard: both consume paths really are in the text being counted.
        Assert.Contains("public static async System.Threading.Tasks.Task ExpectAsync(", helper, StringComparison.Ordinal);
        Assert.Contains("private static async System.Threading.Tasks.Task ExpectAvroAsync(", helper, StringComparison.Ordinal);

        AssertCount(helper, "var deadline", ConsumePaths, "poll deadline");
        AssertCount(helper, "AddMilliseconds(DrainWindowMs)", ConsumePaths, "deadline derived from the constant");

        // The escaped-quote form is searched deliberately: it matches only the JSON key as
        // written into the observation, never the prose in the emitted helper's own
        // comments, which do mention drainWindowMs.
        AssertCount(helper, "\\\"drainWindowMs\\\":", ObservationWrites, "drainWindowMs observation key");
        AssertCount(helper, "DrainWindowMs.ToString(", ObservationWrites, "observation value read from the constant");
    }

    // ── Machinery ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Matches the emitted drain-window declaration, tolerating any whitespace between its
    /// tokens so reformatting the emitted helper does not read as a deleted constant.
    /// </summary>
    [GeneratedRegex(@"private\s+const\s+int\s+DrainWindowMs\s*=\s*(?<ms>\d+)\s*;")]
    private static partial Regex DrainWindowDeclarationPattern();

    /// <summary>
    /// Asserts <paramref name="needle"/> appears in <paramref name="helper"/> exactly
    /// <paramref name="expected"/> times, naming what the occurrences are.
    /// </summary>
    private static void AssertCount(string helper, string needle, int expected, string what)
    {
        var actual = Occurrences(helper, needle);

        Assert.True(
            actual == expected,
            $"Expected {expected} occurrence(s) of '{needle}' ({what}) in the emitted "
            + $"helper, found {actual}. If a consume path was added or removed, update "
            + "ConsumePaths/ObservationWrites here so this pin covers the new shape — do "
            + $"not relax the count.\n{WhatElseMoves}");
    }

    /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/>.</summary>
    private static int Occurrences(string haystack, string needle)
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

    /// <summary>
    /// Emits the fragment and returns the provider-local <c>MqExpectKafka_Helpers</c> class
    /// source.  The helper is interpolation-free (§13.3.1), so the emitted text is the same
    /// for every model — one Emit covers both consume paths.
    /// </summary>
    private string HelperSource()
    {
        var model = new MqExpectKafkaModel(
            Target: "events-bus",
            Topic: "orders.created",
            Match: new KafkaMatch(Key: null, Headers: null, PayloadContains: "x", Json: null));

        var fragment = _provider.Emit(model, new TestCompileContext("pin-step"));

        return fragment.RequiredHelpers.Single(
            h => h.StartsWith("static class MqExpectKafka_Helpers", StringComparison.Ordinal));
    }

    /// <summary>
    /// The root <c>description</c> of the provider's schema fragment — the author-facing
    /// sentence the drain-window constant is asserted against.
    /// </summary>
    private string RootDescription()
    {
        using var document = JsonDocument.Parse(_provider.SchemaFragment.Json);

        return document.RootElement.GetProperty("description").GetString()
            ?? throw new InvalidOperationException(
                "The mq-expect.kafka schema fragment has no root 'description'.");
    }
}
