// EmittedCodeBaselineModels - renders a bound step model, and a step's host-resource requirements,
// as deterministic JSON for the emitted-code baseline (EmittedCodeBaseline).
//
// WHY THE MODEL IS RECORDED. The emitted code shows only what a provider's Emit chose to use, and
// only for suites the pipeline accepts. The bound model is the binder's whole output, for every suite
// that reaches the AST, so a binder that reads a field differently changes a golden even when the
// field never reaches emitted text (a field only Validate reads, or a suite refused later on).
//
// WHAT THE RECORDED FORM MAY DEPEND ON. Only the model's own values: strings, booleans, integers,
// enums (written as their names), ordered lists, string-to-string maps and nested records of those.
// Nothing here names a YAML library type or calls a ToString() it supplies, so the same golden can be
// produced after the binder input type changes. A model member of any other type (a YAML node, a
// delegate, a secret wrapper, a Regex, a TimeSpan, object) is refused before serialisation with a
// message naming the member, so it has to be given an explicit, documented rendering here rather
// than whatever the default serialiser happens to produce for it.
//
// DETERMINISM. System.Text.Json only: indented with two spaces, LF newlines, nulls written, properties
// in declaration order (the reflection resolver's order for these records), relaxed escaping (the
// same text on every machine; it keeps envelopes and JSON bodies readable). Numbers and booleans are
// written by the serialiser, which is culture-invariant. A string-to-string map is written in its
// enumeration order, which for the Dictionary<string, string> every Core binder builds (add-only,
// never removed from) is insertion order, that is document order: the order is part of what a
// binder produces and emitted code iterates it. Any other map type is refused, because its
// enumeration order is not known to be the same on every run.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vouchfx.Sdk;

namespace Vouchfx.Engine.Runtime.Tests;

internal static class EmittedCodeBaselineModels
{
    private static readonly JsonSerializerOptions s_options = CreateOptions();

    /// <summary>Serialises a bound step model.</summary>
    /// <param name="model">The model <c>Bind</c> returned.</param>
    internal static string SerialiseModel(object model)
    {
        var type = model.GetType();
        EnsureRecordable(type, type.Name, new HashSet<Type>());
        return JsonSerializer.Serialize(model, type, s_options);
    }

    /// <summary>Serialises a step's materialised host-resource requirements, in contribution order.</summary>
    /// <param name="requirements">The step's requirements.</param>
    internal static string SerialiseHostResources(IReadOnlyList<HostResourceRequirement> requirements) =>
        JsonSerializer.Serialize(requirements, s_options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        options.Converters.Add(new InsertionOrderedStringMapConverter());
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Refuses a model whose shape contains a member type this baseline has no explicit,
    /// library-independent rendering for. Walks declared property types, so the refusal happens even
    /// when the member is null in the suite at hand.
    /// </summary>
    private static void EnsureRecordable(Type type, string path, HashSet<Type> seen)
    {
        if (type == typeof(string) || type == typeof(bool) || type == typeof(int) || type == typeof(long)
            || type.IsEnum)
        {
            return;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            EnsureRecordable(underlying, path, seen);
            return;
        }

        if (type == typeof(IReadOnlyDictionary<string, string>))
        {
            return;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            EnsureRecordable(type.GetGenericArguments()[0], path + "[]", seen);
            return;
        }

        if (type.IsClass && !type.IsAbstract && !typeof(Delegate).IsAssignableFrom(type)
            && type.Namespace is { } ns && ns.StartsWith("Vouchfx.", StringComparison.Ordinal)
            && !ns.StartsWith("Vouchfx.Engine.", StringComparison.Ordinal))
        {
            if (!seen.Add(type))
            {
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    throw Unrecordable(path + "." + property.Name, property.PropertyType);
                }

                EnsureRecordable(property.PropertyType, path + "." + property.Name, seen);
            }

            return;
        }

        throw Unrecordable(path, type);
    }

    private static InvalidOperationException Unrecordable(string path, Type type) =>
        new($"The emitted-code baseline cannot record the bound-model member '{path}' of type '{type.FullName}'. "
            + "Only strings, booleans, int, long, enums, IReadOnlyList<T>, IReadOnlyDictionary<string, string> and "
            + "Vouchfx records of those have a library-independent rendering. Give this type an explicit rendering "
            + "in EmittedCodeBaselineModels (never its ToString() or a YAML library type) and document it there.");

    /// <summary>
    /// Writes a string-to-string map in enumeration order, accepting only the map type whose
    /// enumeration order is known to be run-independent (see the file header).
    /// </summary>
    private sealed class InsertionOrderedStringMapConverter : JsonConverter<IReadOnlyDictionary<string, string>>
    {
        public override IReadOnlyDictionary<string, string> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("The emitted-code baseline only writes bound models.");

        public override void Write(
            Utf8JsonWriter writer, IReadOnlyDictionary<string, string> value, JsonSerializerOptions options)
        {
            if (value.GetType() != typeof(Dictionary<string, string>))
            {
                throw new InvalidOperationException(
                    $"A bound model holds a string map of type '{value.GetType().FullName}', whose enumeration order is "
                    + "not known to be the same on every run. Extend EmittedCodeBaselineModels deliberately (for "
                    + "example, sort it ordinally and say so in the golden format) rather than recording an order "
                    + "that may change between runs.");
            }

            writer.WriteStartObject();
            foreach (var pair in value)
            {
                writer.WriteString(pair.Key, pair.Value);
            }

            writer.WriteEndObject();
        }
    }
}
