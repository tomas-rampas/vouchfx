// Tests for #584: EventStreamJson.FromLine(string) and FromLine<T> must refuse two
// further classes of hostile input that #579 left unwrapped:
//
//   1. An exception thrown by the CONSUMER'S OWN T while System.Text.Json binds it — an
//      `init` accessor, a [JsonConstructor], or a member [JsonConverter]'s Read. Measured
//      (STJ 8.0 and 10.0.8, with an InvalidDataException and an ArgumentException):
//      System.Text.Json passes such a throw through unwrapped, so it escapes every consumer's
//      `catch (Exception ex) when (ex is JsonException or InvalidOperationException)`
//      filter (§14).
//   2. A line string containing an unpaired UTF-16 surrogate makes Deserialize throw
//      ArgumentException ("Cannot transcode invalid UTF-16 string to UTF-8 JSON text.")
//      with an inner EncoderFallbackException, from BOTH FromLine overloads. Reachable only
//      through the public API: the in-tree file readers decode with replacement, and the
//      renderers' in-memory lines come from ToLine.
//
// The filter's five exclusions — JsonException, InvalidOperationException,
// OutOfMemoryException, OperationCanceledException, TypeInitializationException — must keep
// passing through unwrapped; one probe per exclusion pins that a bare `catch (Exception)`
// cannot replace the filter.
//
// A null `line` is a caller error, not a malformed line: both overloads assert
// ArgumentNullException.ThrowIfNull(line) before attempting to deserialise.

using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vouchfx.Engine.Abstractions.Events;
using Xunit;

namespace Vouchfx.Engine.Abstractions.Tests.Events;

/// <summary>
/// #584: <see cref="EventStreamJson.FromLine(string)"/> and
/// <see cref="EventStreamJson.FromLine{T}"/> wrap every exception System.Text.Json or the
/// bound type itself throws — other than <see cref="JsonException"/>,
/// <see cref="InvalidOperationException"/>, <see cref="OutOfMemoryException"/>,
/// <see cref="OperationCanceledException"/> or <see cref="TypeInitializationException"/> — as a
/// <see cref="JsonException"/> carrying fixed text plus the type name, with the original
/// attached as <see cref="Exception.InnerException"/> and never inlined into the message.
/// </summary>
public sealed class EventStreamJsonHostileExceptionWrappingTests
{
    /// <summary>
    /// A value that must never appear in a wrapping <see cref="JsonException"/>'s own
    /// <see cref="Exception.Message"/> — only on the attached
    /// <see cref="Exception.InnerException"/> — pinning that the guard's message stays
    /// fixed text plus the type name, never a value the line or a thrown exception could
    /// carry (§17 redaction at source).
    /// </summary>
    private const string Marker = "HOSTILE-7f3a";

    // =========================================================================
    // A consumer type's `init` accessor throws while System.Text.Json binds it
    // =========================================================================

    private sealed record ThrowingInitAccessorProbe
    {
        private string _value = string.Empty;

        public string Value
        {
            get => _value;
            init => throw new InvalidDataException($"marker {Marker}");
        }
    }

    [Fact]
    public void FromLineT_InitAccessorThrows_WrapsAsJsonException()
    {
        const string line = """{"Value":"anything"}""";

        var ex = Assert.Throws<JsonException>(
            () => EventStreamJson.FromLine<ThrowingInitAccessorProbe>(line));

        Assert.Equal(
            $"Event-stream line cannot be deserialised as {nameof(ThrowingInitAccessorProbe)}.",
            ex.Message);
        Assert.DoesNotContain(Marker, ex.Message, StringComparison.Ordinal);

        // Measured (STJ 8.0 and 10.0.8): STJ passes this InvalidDataException through the
        // `init` accessor unwrapped, so the inner exception is the probe's own.
        var inner = Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Equal($"marker {Marker}", inner.Message);
    }

    // =========================================================================
    // A [JsonConstructor] throws while System.Text.Json binds it
    // =========================================================================

    private sealed record ThrowingJsonConstructorProbe
    {
        [JsonConstructor]
        public ThrowingJsonConstructorProbe(string value) =>
            throw new InvalidDataException($"marker {Marker}");

        public string Value { get; }
    }

    [Fact]
    public void FromLineT_JsonConstructorThrows_WrapsAsJsonException()
    {
        const string line = """{"Value":"anything"}""";

        var ex = Assert.Throws<JsonException>(
            () => EventStreamJson.FromLine<ThrowingJsonConstructorProbe>(line));

        Assert.Equal(
            $"Event-stream line cannot be deserialised as {nameof(ThrowingJsonConstructorProbe)}.",
            ex.Message);
        Assert.DoesNotContain(Marker, ex.Message, StringComparison.Ordinal);

        // Measured (same probe run): a throwing [JsonConstructor] propagates unwrapped too.
        var inner = Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Equal($"marker {Marker}", inner.Message);
    }

    // =========================================================================
    // A member [JsonConverter]'s Read throws while System.Text.Json binds it
    // =========================================================================

    private sealed class ThrowingMemberConverter : JsonConverter<string>
    {
        public override string Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new ArgumentException($"bad {Marker}");

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    private sealed record ThrowingConverterMemberProbe
    {
        [JsonConverter(typeof(ThrowingMemberConverter))]
        public string Value { get; init; } = string.Empty;
    }

    [Fact]
    public void FromLineT_MemberConverterThrows_WrapsAsJsonException()
    {
        const string line = """{"Value":"anything"}""";

        var ex = Assert.Throws<JsonException>(
            () => EventStreamJson.FromLine<ThrowingConverterMemberProbe>(line));

        Assert.Equal(
            $"Event-stream line cannot be deserialised as {nameof(ThrowingConverterMemberProbe)}.",
            ex.Message);
        // The marker travels ONLY on the inner exception, never inlined into the outer
        // JsonException's own message.
        Assert.DoesNotContain(Marker, ex.Message, StringComparison.Ordinal);

        var inner = Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains(Marker, inner.Message, StringComparison.Ordinal);
    }

    // =========================================================================
    // The filter's exclusions: what must still pass through unwrapped
    // =========================================================================

    private sealed class InsufficientMemoryMemberConverter : JsonConverter<string>
    {
        public override string Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new InsufficientMemoryException(Marker);

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    private sealed record InsufficientMemoryConverterMemberProbe
    {
        [JsonConverter(typeof(InsufficientMemoryMemberConverter))]
        public string Value { get; init; } = string.Empty;
    }

    [Fact]
    public void FromLineT_MemberConverterThrowsOutOfMemoryException_PassesThroughUnwrapped()
    {
        // OutOfMemoryException is excluded from the filter deliberately: resource exhaustion is
        // never a malformed line. InsufficientMemoryException is its non-reserved subclass, so
        // the probe can throw it without tripping CA2201.
        var ex = Assert.Throws<InsufficientMemoryException>(
            () => EventStreamJson.FromLine<InsufficientMemoryConverterMemberProbe>("""{"Value":"anything"}"""));

        Assert.Equal(Marker, ex.Message);
    }

    private sealed record ThrowingInvalidOperationInitAccessorProbe
    {
        private string _value = string.Empty;

        public string Value
        {
            get => _value;
            init => throw new InvalidOperationException($"marker {Marker}");
        }
    }

    [Fact]
    public void FromLineT_InitAccessorThrowsInvalidOperationException_PassesThroughUnwrapped()
    {
        // InvalidOperationException is already inside every consumer's filter, so the wrap
        // leaves it alone.
        var ex = Assert.Throws<InvalidOperationException>(
            () => EventStreamJson.FromLine<ThrowingInvalidOperationInitAccessorProbe>("""{"Value":"anything"}"""));

        Assert.Equal($"marker {Marker}", ex.Message);
    }

    private sealed class JsonExceptionMemberConverter : JsonConverter<string>
    {
        public override string Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new JsonException($"converter {Marker}");

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    private sealed record JsonExceptionConverterMemberProbe
    {
        [JsonConverter(typeof(JsonExceptionMemberConverter))]
        public string Value { get; init; } = string.Empty;
    }

    [Fact]
    public void FromLineT_MemberConverterThrowsJsonException_IsNotWrappedAgain()
    {
        // A converter's own JsonException is already what consumers catch; the wrap must not
        // nest it inside a second JsonException carrying the fixed text. Its message stays the
        // converter's own — unredacted, as the <exception> docs say.
        var ex = Assert.Throws<JsonException>(
            () => EventStreamJson.FromLine<JsonExceptionConverterMemberProbe>("""{"Value":"anything"}"""));

        Assert.Null(ex.InnerException);
        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
    }

    private sealed class CancellingMemberConverter : JsonConverter<string>
    {
        public override string Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new OperationCanceledException(Marker);

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    private sealed record CancellingConverterMemberProbe
    {
        [JsonConverter(typeof(CancellingMemberConverter))]
        public string Value { get; init; } = string.Empty;
    }

    [Fact]
    public void FromLineT_MemberConverterThrowsOperationCanceledException_PassesThroughUnwrapped()
    {
        // FromLine takes no cancellation token, so a cancellation a consumer's converter observes
        // belongs to the caller's own flow; wrapping it would keep the caller's read loop running
        // after it was cancelled.
        var ex = Assert.Throws<OperationCanceledException>(
            () => EventStreamJson.FromLine<CancellingConverterMemberProbe>("""{"Value":"anything"}"""));

        Assert.Equal(Marker, ex.Message);
    }

    private sealed record ThrowingStaticConstructorProbe
    {
        // An explicit static constructor, not a field initialiser: it removes beforefieldinit, so
        // the runtime runs it before the first instance is created rather than at some later first
        // static-field access, which a probe with no static field would never reach.
        static ThrowingStaticConstructorProbe()
        {
            throw new InvalidDataException($"marker {Marker}");
        }

        public string Value { get; init; } = string.Empty;
    }

    [Fact]
    public void FromLineT_StaticConstructorThrows_PassesThroughUnwrapped()
    {
        // A failing static constructor of T is the type's fault, not the line's: the same class of
        // fault the Options hoist keeps loud for EventStreamJson's own initialiser.
        var ex = Assert.Throws<TypeInitializationException>(
            () => EventStreamJson.FromLine<ThrowingStaticConstructorProbe>("""{"Value":"anything"}"""));

        var inner = Assert.IsType<InvalidDataException>(ex.InnerException);
        Assert.Equal($"marker {Marker}", inner.Message);
    }

    // =========================================================================
    // A line string with an unpaired UTF-16 surrogate — both overloads
    // =========================================================================

    [Fact]
    public void FromLine_UnpairedSurrogate_WrapsAsJsonExceptionWithArgumentExceptionInner()
    {
        // \uD800 is a lone high surrogate with no following low surrogate.
        var line = "{\"type\":\"t\",\"runId\":\"\uD800\"}";

        var ex = Assert.Throws<JsonException>(() => EventStreamJson.FromLine(line));

        Assert.Equal(
            $"Event-stream line cannot be deserialised as {nameof(EventEnvelope)}.",
            ex.Message);

        // Measured (STJ 8.0 and 10.0.8): Deserialize throws ArgumentException ("Cannot transcode
        // invalid UTF-16 string to UTF-8 JSON text.") wrapping an EncoderFallbackException, for
        // a line containing an unpaired UTF-16 surrogate — before any object-shape or
        // required-field check runs. Pinned structurally, not by STJ's resource text.
        var inner = Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.IsType<EncoderFallbackException>(inner.InnerException);
    }

    [Fact]
    public void FromLineT_UnpairedSurrogate_WrapsAsJsonExceptionWithArgumentExceptionInner()
    {
        var line = "{\"type\":\"t\",\"runId\":\"\uD800\"}";

        var ex = Assert.Throws<JsonException>(
            () => EventStreamJson.FromLine<ScenarioStartedEvent>(line));

        Assert.Equal(
            $"Event-stream line cannot be deserialised as {nameof(ScenarioStartedEvent)}.",
            ex.Message);

        var inner = Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.IsType<EncoderFallbackException>(inner.InnerException);
    }

    // =========================================================================
    // A null line is a caller error, not a malformed line
    // =========================================================================

    [Fact]
    public void FromLine_NullLine_ThrowsArgumentNullException()
    {
        // Green before and after the #584 change: System.Text.Json's own
        // JsonSerializer.Deserialize(string, ...) already rejects a null json argument with
        // ArgumentNullException. The explicit ArgumentNullException.ThrowIfNull(line) added
        // by #584 makes that independent of System.Text.Json's own null handling, and keeps
        // the broad catch filter below it from ever seeing (and mis-wrapping) a null-argument
        // error as a malformed line.
        Assert.Throws<ArgumentNullException>(() => EventStreamJson.FromLine(null!));
    }

    [Fact]
    public void FromLineT_NullLine_ThrowsArgumentNullException()
    {
        // Green before and after, for the same reason as the untyped overload above.
        Assert.Throws<ArgumentNullException>(() => EventStreamJson.FromLine<ScenarioStartedEvent>(null!));
    }
}
