using System.Text.Json;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Steps.HttpRest;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime;

/// <summary>Core fields actually consumed by runtime secret-template resolvers.</summary>
/// <remarks>
/// This is not a raw-node secret scan. Map names and unlisted literals/placeholder-only
/// fields remain opaque. PostgreSQL's historical collection is retained deliberately.
/// The independent emission census tests gate additions without changing the frozen SDK.
/// An http.rest structured body is read through the binder's own conversion, one JSON string
/// per field, so each text is exactly what the run-time resolver reads; a body the binder
/// refuses contributes nothing, and the refusal is reported once, by the provider's Validate.
/// </remarks>
internal static class StepTemplateCensus
{
    /// <summary>One field text the resolver reads, and where it is.</summary>
    /// <param name="Location">The field's path within the step.</param>
    /// <param name="Text">The text, as the run-time resolver reads it, exactly as written.</param>
    /// <param name="OpenTailJoinsLaterString">
    /// Set only for a structured http.rest body string whose open tail the serialised body
    /// joins into text ending inside a later string
    /// (<see cref="HttpRestBodyConversion.BodyString.OpenTailJoinsLaterString"/>). That tail
    /// is sent literally and is the one text <see cref="TryValidate"/> does not refuse. The
    /// tail itself is always found again in <see cref="Text"/>, so no derived copy of the text
    /// is stored that a later <c>with</c> could leave stale.
    /// </param>
    internal sealed record Field(string Location, string Text, bool OpenTailJoinsLaterString = false);

    internal sealed record Collection(IReadOnlyList<Field> Fields);

    internal static Collection Collect(StepNode node)
    {
        var fields = new List<Field>();
        var raw = node.RawNode;
        void Scalar(string path)
        {
            if (At(raw, path) is YamlScalarNode { Value: { Length: > 0 } value }) fields.Add(new Field(path, value));
        }
        void Values(string path)
        {
            if (At(raw, path) is not YamlMappingNode map) return;
            foreach (var value in map.Children.Values)
                if (value is YamlScalarNode { Value: { Length: > 0 } text }) fields.Add(new Field(path + "[*]", text));
        }
        void Items(string path, string property)
        {
            if (At(raw, path) is not YamlSequenceNode sequence) return;
            foreach (var item in sequence.Children)
                if (item is YamlMappingNode map && At(map, property) is YamlScalarNode { Value: { Length: > 0 } text })
                    fields.Add(new Field(path + "[*]." + property, text));
        }

        switch (node.CanonicalType)
        {
            case "http.rest":
                Scalar("path"); Values("headers");
                if (At(raw, "body") is { } body)
                {
                    // The binder's own conversion, compiled from the provider's source file
                    // (HttpRestBodyConversion.cs, linked into this assembly), so the text
                    // checked here is the text HttpRestProvider.Bind sends: merge keys
                    // applied, block scalars and tags honoured. A body it refuses contributes
                    // nothing and is not reported here: Bind carries the same refusal to
                    // Validate, which reports it once on the authoring channel, and the step
                    // is never compiled, so no reference in it is ever fetched or sent.
                    //
                    // A structured body is read one JSON string at a time, unescaped, because
                    // that is how the run-time resolver reads it: never as the serialised text,
                    // whose escapes would change a reference and whose structure could join two
                    // strings into one. A scalar body is still one text.
                    var converted = ConvertBody(body, raw);
                    if (converted?.Strings is { } strings)
                    {
                        foreach (var text in strings)
                            if (text.Value.Length > 0) fields.Add(new Field("body", text.Value, text.OpenTailJoinsLaterString));
                    }
                    else if (converted?.Text is { Length: > 0 } text)
                    {
                        fields.Add(new Field("body", text));
                    }
                }
                Values("expect.json"); Scalar("expect.bodyContains");
                break;
            case "http.soap":
                Scalar("path"); Scalar("action"); Scalar("envelope"); Items("expect.xpath", "value");
                break;
            case "mq-publish.kafka":
                Scalar("topic"); Scalar("key"); Values("headers");
                // BindAvro selects Avro only for an actual mapping; its helper
                // then returns before plain payload resolution. Inactive text is opaque.
                if (At(raw, "avro") is YamlMappingNode) Values("avro.record");
                else Scalar("payload");
                break;
            case "mq-expect.kafka":
                Scalar("match.key"); Scalar("match.payloadContains"); Values("match.headers"); Values("match.json");
                break;
            case "mq-publish.rabbitmq":
                Scalar("exchange"); Scalar("routingKey"); Scalar("payload"); Values("headers");
                break;
            case "mq-expect.rabbitmq":
                Scalar("match.payloadContains"); Values("match.headers"); Values("match.json");
                break;
            case "mq-publish.nats":
                Scalar("subject"); Scalar("payload");
                break;
            case "mq-expect.nats":
                Scalar("subject"); Scalar("match.payloadContains"); Values("match.json");
                break;
            case "mq-publish.redis":
                Scalar("stream"); Scalar("payload");
                break;
            case "mq-expect.redis":
                Scalar("stream"); Scalar("match.payloadContains"); Values("match.json");
                break;
            case "mq-publish.azureservicebus":
                Scalar("payload"); Values("properties");
                break;
            case "mq-expect.azureservicebus":
                Scalar("expectPayloadContains"); Values("expectProperties");
                break;
            case "webhook-listen.http":
                Scalar("match.method"); Scalar("match.path"); Scalar("match.bodyContains"); Values("match.headers");
                break;
            case "mail-expect.smtp":
                Scalar("expect.match.to"); Scalar("expect.match.subject-contains"); Scalar("expect.match.body-contains");
                break;
            case "metrics-assert.prometheus":
                Scalar("path"); Scalar("metric"); Values("labels"); Scalar("expect.value"); Scalar("expect.min"); Scalar("expect.max");
                break;
            case "storage-assert.s3":
                Scalar("bucket"); Scalar("key"); Scalar("expect.size"); Scalar("expect.minSize");
                Scalar("expect.contentType"); Scalar("expect.sha256"); Scalar("expect.contentContains"); Values("expect.metadata");
                break;
            case "trace-expect.otlp":
                Scalar("match.traceId"); Scalar("match.service"); Scalar("match.spanName"); Values("match.attributes");
                break;
            case "cache-assert.elasticsearch":
                Items("expect.fields", "value");
                break;
            case "db-assert.postgres":
                // Compatibility, NOT a claim that its helper resolves secret values.
                // Keep the historical query/parameter/row value order and scalar spelling.
                Scalar("query"); Values("parameters"); Values("expect.row");
                break;
        }
        return new Collection(fields);
    }

    private static YamlNode? At(YamlMappingNode root, string path)
    {
        YamlNode current = root;
        foreach (var part in path.Split('.'))
        {
            if (current is not YamlMappingNode map || !map.Children.TryGetValue(new YamlScalarNode(part), out var next)) return null;
            current = next;
        }
        return current;
    }

    /// <summary>
    /// The secret-reference check of one field: <see cref="SecretReference.ValidateField"/>
    /// over <see cref="Field.Text"/>, with one exemption.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exemption: when <see cref="Field.OpenTailJoinsLaterString"/> is set, the string's
    /// open tail (its first <c>${secret:</c> with no <c>}</c> after it in the string) is the
    /// first half of text that reads as a reference only across two strings. Such text is
    /// sent literally and never resolved, so the tail is not checked; the rest of the string,
    /// which holds every reference the resolver reads in it, is. Any other open tail, in a
    /// body string or in any other field, is checked with the whole text and refused as a
    /// malformed reference.
    /// </para>
    /// <para>
    /// Every diagnostic quotes what the author wrote. The unknown-source diagnostic quotes the
    /// reference, which is the same in the checked part as in the whole string. The malformed
    /// diagnostic quotes the field, so when the checked part is malformed the whole string is
    /// validated instead for its message: it is malformed too, because it holds the same
    /// references (none reaches into the tail, which has no <c>}</c>) and more sigils.
    /// </para>
    /// </remarks>
    internal static bool TryValidate(Field field, IReadOnlyCollection<string> knownSources, out string? error)
    {
        var tail = field.OpenTailJoinsLaterString ? HttpRestBodyConversion.OpenTailStart(field.Text) : -1;
        if (tail < 0)
            return SecretReference.ValidateField(field.Text, knownSources, out error);

        var checkedPart = field.Text[..tail];
        if (SecretReference.ValidateField(checkedPart, knownSources, out error))
            return true;

        // ValidateField's own malformed test: more sigils than well-formed references.
        if (CountSigils(checkedPart) > SecretReference.FindAll(checkedPart).Count)
            SecretReference.ValidateField(field.Text, knownSources, out error);
        return false;
    }

    private static int CountSigils(string text)
    {
        var count = 0;
        for (var index = text.IndexOf(SecretReference.Sigil, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(SecretReference.Sigil, index + SecretReference.Sigil.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// The conversion of <paramref name="body"/> that <c>HttpRestProvider.Bind</c> sends, or
    /// <see langword="null"/> when its conversion fails with an exception. A refusal comes back
    /// as a result with no text and no strings, so it contributes nothing.
    /// </summary>
    private static HttpRestBodyConversion.Result? ConvertBody(YamlNode body, YamlMappingNode step)
    {
        try
        {
            return HttpRestBodyConversion.ToTemplate(body, step);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or NotSupportedException)
        {
            // The conversion RETURNS its authoring refusals, so an exception here is a defect
            // in it. Bind meets the identical exception, and the pipeline reports that as a
            // provider fault before anything is compiled; contributing nothing here keeps it
            // one report and keeps this walk from aborting the pre-topology door. No body
            // text enters any diagnostic from here.
            return null;
        }
    }
}
