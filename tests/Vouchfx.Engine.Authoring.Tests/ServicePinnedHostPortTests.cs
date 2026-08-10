// REQ-025 — the `"<host>:<container>"` form of a service's `ports:` entries, at the parser.
//
// WHY THE REQUIREMENT EXISTS, in one paragraph, because it is the only reason to widen a field
// that had worked for two releases. A service-form broker configures its own advertised address
// from `env:`, at container start-up — before any orchestrator-allocated host port exists. So a
// customer-supplied Kafka broker could name, in `advertised.listeners`, only an address the
// engine host cannot reach: a step bootstrapped over the staged secured endpoint, was handed
// metadata pointing somewhere else, and connected to nothing. Pinning the host port is what lets
// the broker's own configuration name an address that is true from outside the container.
//
// WHAT THIS FILE COVERS AND WHAT IT DOES NOT. Everything here is parse-level and needs no
// container: which forms are accepted, which are refused, and what the author is told. That the
// pinned port is actually PUBLISHED — the fact the requirement is about — is measured against a
// running container in the docker-gated REQ-025 drills, because no amount of parsing proves a
// port binding.
using System.Collections.Generic;
using Vouchfx.Engine.Authoring;
using Xunit;

namespace Vouchfx.Engine.Authoring.Tests;

/// <summary>
/// The accepted and rejected shapes of a <c>ports:</c> entry (REQ-025).
/// </summary>
public sealed class ServicePinnedHostPortTests
{
    private static string SuiteWithPorts(string portsList) =>
        $$"""
        environment:
          services:
            broker:
              image: confluentinc/cp-kafka:7.6.1
              ports: {{portsList}}
        steps:
          - id: noop
            type: script.csharp
            code: "return;"
        """;

    // ── The accepted forms ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pre-REQ-025 form. It must keep meaning exactly what it meant: a container port, with
    /// no host port pinned — which is what leaves the orchestrator free to allocate one.
    /// </summary>
    [Fact]
    public void BareInteger_DeclaresAContainerPortAndPinsNothing()
    {
        var service = ParseService("[9092, 9093]");

        Assert.Equal(new List<int> { 9092, 9093 }, service.Ports);

        // Null rather than empty, deliberately: the mapper reads this to decide whether to pass a
        // host port at all, and "the author pinned nothing" is the state every suite written
        // before this requirement is in.
        Assert.Null(service.PinnedHostPorts);
    }

    /// <summary>
    /// The REQ-025 form: the HOST port comes first, as docker-compose writes it.
    /// </summary>
    [Fact]
    public void MappingForm_DeclaresTheContainerPortAndPinsTheHostPort()
    {
        var service = ParseService("[\"19093:9093\"]");

        // The container port, and ONLY the container port, reaches `Ports` — so endpoint naming,
        // the security endpoint selector and the health-check cross-reference all continue to see
        // what they saw before, and none of them has to learn about a pair.
        Assert.Equal(new List<int> { 9093 }, service.Ports);
        Assert.Equal(new Dictionary<int, int> { [9093] = 19093 }, service.PinnedHostPorts);
    }

    /// <summary>
    /// The two forms mix, and pinning one port does not pin its siblings.
    /// </summary>
    [Fact]
    public void MixedForms_PinOnlyTheEntriesWrittenAsPairs()
    {
        var service = ParseService("[9092, \"19093:9093\"]");

        Assert.Equal(new List<int> { 9092, 9093 }, service.Ports);
        Assert.Equal(new Dictionary<int, int> { [9093] = 19093 }, service.PinnedHostPorts);
        Assert.False(service.PinnedHostPorts!.ContainsKey(9092));
    }

    /// <summary>
    /// Host and container may differ or match; nothing requires them to differ.
    /// </summary>
    [Fact]
    public void MappingForm_AcceptsAnIdenticalHostAndContainerPort()
    {
        var service = ParseService("[\"9093:9093\"]");

        Assert.Equal(new List<int> { 9093 }, service.Ports);
        Assert.Equal(new Dictionary<int, int> { [9093] = 9093 }, service.PinnedHostPorts);
    }

    // ── The rejected forms ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every malformed shape is refused with a message naming the offending value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Naming the value is the whole point of testing these separately: a port list is written
    /// once and read by a broker's own configuration, so an author who mistypes one half needs to
    /// be told WHICH entry, not that "ports is invalid".
    /// </para>
    /// <para>
    /// <c>"1:2:3"</c> keeps its row, but NOT for the reason first written here. That reason —
    /// that without an explicit guard a colon-split would silently reinterpret it as a plausible
    /// pair — is false: measured, a split on either colon leaves one half containing a colon, and
    /// a half containing a colon is not a bare decimal integer, so it is refused with or without
    /// the guard. What the guard buys is the MESSAGE: "more than one ':'" names the actual
    /// mistake, where the fallback would complain about a half the author never meant to write.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("9093:", "a missing container half")]
    [InlineData(":9093", "a missing host half")]
    [InlineData("a:b", "non-numeric halves")]
    [InlineData("1:2:3", "more than one colon")]
    [InlineData("0:9093", "a zero host port")]
    [InlineData("9093:0", "a zero container port")]
    [InlineData("65536:9093", "an out-of-range host port")]
    [InlineData("9093:65536", "an out-of-range container port")]
    [InlineData("-1:9093", "a negative host port")]
    [InlineData("9093:-1", "a negative container port")]
    [InlineData("9093:9093:", "a trailing colon")]
    [InlineData("not-a-port", "a bare non-numeric string")]
    [InlineData("019093:9093", "a leading zero in the host half")]
    [InlineData("19093:09093", "a leading zero in the container half")]
    [InlineData("0123", "a leading zero in the bare form")]
    [InlineData("443:9093", "a privileged host port")]
    [InlineData("1023:9093", "the last host port below 1024")]
    public void MalformedEntry_IsRefusedAndTheMessageNamesTheValue(string entry, string why)
    {
        var exception = Assert.Throws<YamlParseException>(
            () => ParseService($"[\"{entry}\"]"));

        Assert.Contains(entry, exception.Message, System.StringComparison.Ordinal);
        Assert.Contains("broker", exception.Message, System.StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>
    /// A bare entry outside 1..65535 is told it is out of range — not that it should have been a
    /// <c>&lt;host&gt;:&lt;container&gt;</c> pair.
    /// </summary>
    /// <remarks>
    /// The first implementation tried the bare parse, and on failure fell through to the pair
    /// branch — so adding a range check made every out-of-range integer report the wrong shape.
    /// The range check is right; the diagnostic was not.
    /// </remarks>
    [Theory]
    [InlineData("70000")]
    [InlineData("65536")]
    [InlineData("0")]
    public void BareEntryOutsideTheRange_IsToldItIsOutOfRangeAndNotToldToWriteAPair(string entry)
    {
        var exception = Assert.Throws<YamlParseException>(() => ParseService($"[{entry}]"));

        Assert.Contains("outside the range 1..65535", exception.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain("<host>:<container>", exception.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The host port must be 1024 or above; the container port has no such floor.
    /// </summary>
    /// <remarks>
    /// Privileged and well-known host ports are refused with no escape hatch: pinning 443 or 5432
    /// squats a real service's port on every interface for the length of a run, and sub-1024 binds
    /// succeed on a CI runner running as root while failing on a developer's machine. The
    /// container half lives in the container's own namespace, where 80 is unremarkable.
    /// </remarks>
    [Fact]
    public void ContainerPortBelow1024_IsAcceptedWhileTheHostPortIsNot()
    {
        var service = ParseService("[\"18080:80\"]");

        Assert.Equal(new List<int> { 80 }, service.Ports);
        Assert.Equal(new Dictionary<int, int> { [80] = 18080 }, service.PinnedHostPorts);
    }

    /// <summary>
    /// One container port may be declared once, in either form — never twice across both.
    /// </summary>
    /// <remarks>
    /// The schema cannot see this: <c>uniqueItems</c> compares JSON values and an integer is never
    /// equal to a string, so <c>[9093, "19093:9093"]</c> is schema-valid. Unrefused it produces two
    /// endpoint declarations with one name, and the orchestrator rejects the topology with "an
    /// endpoint named 'tcp-9093' already exists" — an internal-sounding failure for an ordinary
    /// authoring mistake. Both orderings are tested because the check has to hold whichever form
    /// the author wrote first.
    /// </remarks>
    [Theory]
    [InlineData("[9093, \"19093:9093\"]")]
    [InlineData("[\"19093:9093\", 9093]")]
    public void SameContainerPortInBothForms_IsRefused(string portsList)
    {
        var exception = Assert.Throws<YamlParseException>(() => ParseService(portsList));

        Assert.Contains("declared more than once", exception.Message, System.StringComparison.Ordinal);
        Assert.Contains("9093", exception.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Two container ports may not pin the SAME host port.
    /// </summary>
    /// <remarks>
    /// Nothing else catches it. The schema sees two distinct strings; the container-port map sees
    /// two distinct keys; and the engine's pre-flight probe — which binds each pinned port and
    /// releases it before probing the next — cannot see its own siblings either. Left to the
    /// orchestrator it becomes the slow health-gate hang the fail-fast check exists to remove, for
    /// the one collision class the author caused themselves.
    /// </remarks>
    [Fact]
    public void TwoContainerPortsPinningOneHostPort_IsRefused()
    {
        var exception = Assert.Throws<YamlParseException>(
            () => ParseService("[\"19093:9093\", \"19093:9094\"]"));

        Assert.Contains("19093", exception.Message, System.StringComparison.Ordinal);
        Assert.Contains(
            "already pinned to container port 9093", exception.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// One container port cannot publish on two host ports.
    /// </summary>
    /// <remarks>
    /// Refused rather than last-one-wins, for the same reason two server artefacts may not claim
    /// one in-container path: which of them takes effect is not something this engine decides
    /// silently. Note that the schema's own <c>uniqueItems</c> cannot catch this — the two
    /// entries are different strings.
    /// </remarks>
    [Fact]
    public void SameContainerPortPinnedTwice_IsRefused()
    {
        var exception = Assert.Throws<YamlParseException>(
            () => ParseService("[\"19093:9093\", \"29093:9093\"]"));

        Assert.Contains("9093", exception.Message, System.StringComparison.Ordinal);
        Assert.Contains(
            "already pinned to host port 19093", exception.Message, System.StringComparison.Ordinal);
    }

    private static Vouchfx.Engine.Authoring.Model.ServiceSpec ParseService(string portsList)
    {
        var document = YamlDocumentParser.Parse(SuiteWithPorts(portsList));
        return document.Environment!.Services!["broker"];
    }
}
