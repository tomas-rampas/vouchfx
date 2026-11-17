using System.Text.Json;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Steps.HttpRest;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime;

/// <summary>Core fields actually consumed by runtime secret-template resolvers.</summary>
/// <remarks>
/// This is not a raw-node secret scan. Map names and unlisted literals/placeholder-only
/// fields remain opaque. PostgreSQL's historical collection is retained deliberately.
/// The independent emission census tests gate additions without changing the frozen SDK.
/// An http.rest structured body is read through the binder's own conversion, so its text
/// is exactly the body that is sent; a body the binder refuses contributes nothing, and the
/// refusal is reported once, by the provider's Validate.
/// </remarks>
internal static class StepTemplateCensus
{
    internal sealed record Field(string Location, string Text);
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
                    var converted = ConvertBody(body, raw);
                    if (converted is { Length: > 0 }) fields.Add(new Field("body", converted));
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
    /// The text <c>HttpRestProvider.Bind</c> sends for <paramref name="body"/>, or
    /// <see langword="null"/> when Bind sends none because the conversion refuses it.
    /// </summary>
    private static string? ConvertBody(YamlNode body, YamlMappingNode step)
    {
        try
        {
            return HttpRestBodyConversion.ToTemplate(body, step).Text;
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
