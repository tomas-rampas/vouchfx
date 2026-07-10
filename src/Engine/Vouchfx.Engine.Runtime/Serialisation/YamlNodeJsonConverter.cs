// Vouchfx.Engine.Runtime — YamlNodeJsonConverter (S11-B-02).
//
// Why a separate file in Serialisation/:
//   SerialiseEnvironment is a private helper on ScenarioRunner, a ~1600-line file.
//   Inlining a non-trivial recursive converter would obscure the surrounding logic.
//   Placing it in Serialisation/ follows the Secrets/ precedent already in this project
//   and makes it easy to test or evolve in isolation.
//
// Why System.Text.Json (not Newtonsoft):
//   Hard invariant — CLAUDE.md §Libraries: "System.Text.Json only (never return
//   Newtonsoft to providers)".
//
// Design:
//   YamlDotNet's YamlNode is an abstract base; the three concrete types in play are:
//     • YamlScalarNode   — a leaf string value
//     • YamlMappingNode  — a key→value map (key and value are both YamlNode)
//     • YamlSequenceNode — an ordered list of YamlNode
//   System.Text.Json cannot serialise any of them (no default converter exists) and
//   throws InvalidOperationException when it encounters a non-null YamlMappingNode held
//   by DependencySpec.Extra.
//
//   This converter handles YamlNode (the abstract base) and all three subtypes via a
//   single Write implementation:
//     • null           → JSON null
//     • YamlScalarNode → JSON string (the scalar's .Value, or "" for an empty anchor)
//     • YamlMappingNode→ JSON object with keys sorted ORDINALLY (for determinism within an
//                         Extra block — two Extra mappings with the same pairs in different
//                         declaration order produce identical JSON; top-level Services and
//                         Dependencies retain declaration order, serialised by STJ in
//                         enumeration order from the same parsed YAML)
//     • YamlSequenceNode→ JSON array (elements written recursively)
//     • any other type → JSON null (defensive; no other concrete subtypes exist today)
//
//   Mapping keys are sorted with StringComparer.Ordinal so that two DependencySpec.Extra
//   mappings that contain the same key-value pairs but in different declaration order
//   produce byte-for-byte identical JSON.  This sort applies only to YamlMappingNode
//   (i.e. Extra blocks); the top-level Services/Dependencies collections are C# lists
//   serialised by STJ in enumeration order and retain their YAML declaration order.
//
// Registration:
//   SerialiseEnvironment holds a private static JsonSerializerOptions that registers
//   this converter.  Allocating it once (static initialiser) avoids repeated reflection
//   on the hot path.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Runtime.Serialisation;

/// <summary>
/// <see cref="JsonConverter{T}"/> for <see cref="YamlNode"/> and its concrete subtypes
/// (<see cref="YamlScalarNode"/>, <see cref="YamlMappingNode"/>,
/// <see cref="YamlSequenceNode"/>).
/// </summary>
/// <remarks>
/// <para>
/// Registered on the <see cref="JsonSerializerOptions"/> used by
/// <c>ScenarioRunner.SerialiseEnvironment</c> so that a
/// <see cref="DependencySpec.Extra"/> field (type <see cref="YamlMappingNode"/>)
/// is serialised correctly rather than throwing
/// <see cref="InvalidOperationException"/> (S11-B-02).
/// </para>
/// <para>
/// Mapping keys are emitted in ordinal sort order to guarantee that two structurally
/// equal mappings with different declaration order produce identical JSON — and therefore
/// identical <c>ComputeEnvironmentHash</c> output.
/// </para>
/// </remarks>
internal sealed class YamlNodeJsonConverter : JsonConverter<YamlNode>
{
    /// <inheritdoc/>
    /// <remarks>
    /// Returns <see langword="true"/> for <see cref="YamlNode"/> itself and all its
    /// concrete subtypes so that a single converter registration covers every node type
    /// that can appear in the object graph.
    /// </remarks>
    public override bool CanConvert(Type typeToConvert) =>
        typeof(YamlNode).IsAssignableFrom(typeToConvert);

    /// <inheritdoc/>
    public override YamlNode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "YamlNodeJsonConverter is write-only; deserialisation of YamlNode is not needed.");

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        YamlNode value,
        JsonSerializerOptions options)
    {
        WriteNode(writer, value, options);
    }

    private static void WriteNode(
        Utf8JsonWriter writer,
        YamlNode? node,
        JsonSerializerOptions options)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;

            case YamlScalarNode scalar:
                // All YAML scalars — booleans, integers, strings — are emitted as JSON
                // strings.  Type information is intentionally flattened: the YAML
                // representation model does not carry a reliable type tag (a scalar's
                // Style is a formatting hint, not a type contract), and the only consumer
                // of this output is the in-process environment equality / topology-reuse
                // check, which only needs value stability, not type fidelity.
                // Value may be null when the node represents an empty anchor ("~").
                writer.WriteStringValue(scalar.Value ?? string.Empty);
                break;

            case YamlMappingNode mapping:
                writer.WriteStartObject();
                // Sort keys ordinally: two mappings with the same entries but different
                // declaration order must produce identical JSON (determinism guarantee).
                var sortedChildren = mapping.Children
                    .Select(kv => (Key: KeyToString(kv.Key), kv.Value))
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal);
                foreach (var (key, childValue) in sortedChildren)
                {
                    writer.WritePropertyName(key);
                    WriteNode(writer, childValue, options);
                }
                writer.WriteEndObject();
                break;

            case YamlSequenceNode sequence:
                writer.WriteStartArray();
                foreach (var item in sequence.Children)
                {
                    WriteNode(writer, item, options);
                }
                writer.WriteEndArray();
                break;

            default:
                // Defensive: no other concrete YamlNode subtypes exist in YamlDotNet 16,
                // but guard against future additions rather than throwing.
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// Converts a <see cref="YamlNode"/> mapping key to a string without throwing.
    /// </summary>
    /// <remarks>
    /// YAML legally permits complex (non-scalar) keys — a sequence or mapping used as a
    /// mapping key.  The straightforward cast <c>(YamlScalarNode)kv.Key</c> throws
    /// <see cref="InvalidCastException"/> for such keys, replicating the bug the converter
    /// was written to fix.  This helper handles the scalar case directly and falls back to
    /// <see cref="YamlNode.ToString"/> for any other node type, which is deterministic and
    /// never throws.
    /// </remarks>
    private static string KeyToString(YamlNode key) => key switch
    {
        YamlScalarNode s => s.Value ?? string.Empty,
        _ => key.ToString() ?? string.Empty,   // deterministic, never throws
    };
}
