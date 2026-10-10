using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vouchfx.Engine.Authoring.Ast;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime;

/// <summary>Core fields actually consumed by runtime secret-template resolvers.</summary>
/// <remarks>
/// This is not a raw-node secret scan. Map names and unlisted literals/placeholder-only
/// fields remain opaque. PostgreSQL's historical collection is retained deliberately.
/// The independent emission census tests gate additions without changing the frozen SDK.
/// </remarks>
internal static class StepTemplateCensus
{
    internal sealed record Field(string Location, string Text);
    internal sealed record Collection(IReadOnlyList<Field> Fields, string? Failure = null);

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
                    try
                    {
                        // Exactly the binder's representation, not a second Bind. The gate
                        // compares against its actual once-bound Body, including mapping keys.
                        var budget = 50_000;
                        var text = body is YamlScalarNode scalar ? scalar.Value ?? string.Empty
                            : JsonSerializer.Serialize(BodyNode(body, 1, ref budget));
                        if (text.Length > 0) fields.Add(new Field("body", text));
                    }
                    catch (Exception exception) when (exception is BodyLimitException or ArgumentException or JsonException or InvalidOperationException or NotSupportedException)
                    {
                        // Only this pure bounded conversion is guarded. No provider/process
                        // exception is classified here and no raw body enters the diagnostic.
                        return new Collection(fields, "http.rest body cannot be represented as bounded JSON (maximum depth 64 and 50000 nodes).");
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

    private sealed class BodyLimitException : Exception { }

    private static JsonNode? BodyNode(YamlNode node, int depth, ref int budget)
    {
        if (depth > 64 || --budget < 0) throw new BodyLimitException();
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var (key, value) in map.Children)
                    obj[key is YamlScalarNode scalar ? scalar.Value ?? string.Empty : key.ToString()] = BodyNode(value, depth + 1, ref budget);
                return obj;
            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var item in sequence.Children) array.Add(BodyNode(item, depth + 1, ref budget));
                return array;
            case YamlScalarNode scalar:
                var text = scalar.Value ?? string.Empty;
                if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted || text.Length == 0) return JsonValue.Create(text);
                if (text is "null" or "Null" or "NULL" or "~") return null;
                if (text is "true" or "True" or "TRUE") return JsonValue.Create(true);
                if (text is "false" or "False" or "FALSE") return JsonValue.Create(false);
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return JsonValue.Create(number);
                return JsonValue.Create(text);
            default:
                return JsonValue.Create(node.ToString());
        }
    }
}
