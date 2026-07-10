// S03-B-03 — Pre-compilation schema validation pass with line context (§8, §13.6).
//
// This file is the pre-compile gate that an author reaches before any Roslyn
// compilation is attempted.  It delegates to SchemaComposer.Validate so that
// the unified composed schema (root language schema + every registered
// provider's if/then fragment) is evaluated, then enriches each error's
// message with a best-effort YAML line number.
//
// Line resolution is best-effort: the JSON Pointer in InstanceLocation is split
// and walked against a YamlDotNet RepresentationModel parse of the same
// document.  When a pointer segment cannot be resolved (e.g. the document is
// unparseable, the pointer targets the root, or a numeric index is out of
// range) the prefix is omitted rather than throwing.
using Vouchfx.Sdk;
using YamlDotNet.RepresentationModel;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// Pre-compilation validation gate: validates a <c>.e2e.yaml</c> document
/// against the unified composed JSON Schema (root-language schema with every
/// registered provider's fragment) and enriches each error with a resolved
/// YAML line number.
/// </summary>
/// <remarks>
/// <para>
/// This class is a thin wrapper around <see cref="SchemaComposer.Validate"/>.
/// It does not duplicate schema-composition or YAML-to-JSON-conversion logic;
/// those concerns remain in <see cref="SchemaComposer"/> and
/// <see cref="SchemaResources"/> respectively.
/// </para>
/// <para>
/// <strong>Line resolution is best-effort.</strong>  The
/// <see cref="SchemaValidationError.InstanceLocation"/> field carries a JSON
/// Pointer (RFC 6901) that was produced from the JSON-serialised representation
/// of the YAML document.  Mapping that pointer back to a line number requires
/// walking the original YAML RepresentationModel.  When any segment of the walk
/// fails — unparseable YAML, root-level pointer, out-of-range index, absent
/// mapping key — the <c>(line N)</c> prefix is omitted and the raw schema
/// message is preserved unchanged.
/// </para>
/// <para>
/// <see cref="SchemaValidationError.InstanceLocation"/> is never modified;
/// only <see cref="SchemaValidationError.Message"/> gains the prefix.
/// </para>
/// </remarks>
public static class DocumentValidator
{
    /// <summary>
    /// Validates <paramref name="yamlText"/> against the unified composed JSON
    /// Schema built from <paramref name="registry"/> and enriches each error
    /// message with a <c>(line N)</c> prefix resolved from the original YAML
    /// source.
    /// </summary>
    /// <param name="yamlText">
    /// The raw contents of a <c>.e2e.yaml</c> file.
    /// </param>
    /// <param name="registry">
    /// The frozen provider registry whose JSON Schema fragments are included
    /// in the composed schema.  Passing an empty registry validates against
    /// the root-language schema only (no provider-specific constraints).
    /// </param>
    /// <returns>
    /// A <see cref="SchemaValidationResult"/> whose errors have been enriched
    /// with <c>(line N)</c> prefixes where the pointer-to-line resolution
    /// succeeded.
    /// </returns>
    public static SchemaValidationResult Validate(string yamlText, StepKindRegistry registry)
    {
        // Delegate to the composed-schema path; this is the authoritative
        // validation (root schema + provider fragments).
        var raw = SchemaComposer.Validate(registry, yamlText);

        if (raw.IsValid || raw.Errors.Count == 0)
            return raw;

        // Parse the YAML once into the RepresentationModel so we can resolve
        // JSON-Pointer segments back to concrete line numbers.  If parsing
        // fails (the document was already rejected as malformed) we skip
        // enrichment and return the raw result unchanged.
        YamlMappingNode? rootMapping = TryParseYamlRoot(yamlText);

        var enriched = new SchemaValidationError[raw.Errors.Count];
        for (var i = 0; i < raw.Errors.Count; i++)
        {
            var error = raw.Errors[i];
            var line = rootMapping is not null
                ? ResolveLineFromPointer(rootMapping, error.InstanceLocation)
                : null;

            var message = line.HasValue
                ? $"(line {line.Value}) {error.Message}"
                : error.Message;

            enriched[i] = new SchemaValidationError(error.InstanceLocation, message);
        }

        return new SchemaValidationResult(false, enriched);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to parse <paramref name="yamlText"/> into a
    /// <see cref="YamlMappingNode"/> using the YamlDotNet
    /// <see cref="YamlStream"/> RepresentationModel.
    /// Returns <see langword="null"/> when the YAML cannot be parsed or the
    /// root node is not a mapping (e.g. a bare scalar or sequence).
    /// </summary>
    private static YamlMappingNode? TryParseYamlRoot(string yamlText)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new System.IO.StringReader(yamlText);
            stream.Load(reader);

            if (stream.Documents.Count == 0)
                return null;

            return stream.Documents[0].RootNode as YamlMappingNode;
        }
        catch
        {
            // Any YamlDotNet parse exception means we cannot resolve lines;
            // return null so the caller skips enrichment gracefully.
            return null;
        }
    }

    /// <summary>
    /// Resolves a JSON Pointer (RFC 6901) to a YAML source line number by
    /// walking the <see cref="YamlMappingNode"/> RepresentationModel tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pointer is split on <c>/</c>; the leading empty segment (before the
    /// first slash) is discarded.  Each remaining segment is interpreted as
    /// either a mapping key (string) or a sequence index (integer).  The walk
    /// stops at the deepest node that can be reached; if any segment fails to
    /// match, <see langword="null"/> is returned.
    /// </para>
    /// <para>
    /// Per RFC 6901, <c>~1</c> decodes to <c>/</c> and <c>~0</c> decodes to
    /// <c>~</c>.  These are decoded before key lookup.
    /// </para>
    /// <para>
    /// An empty pointer (<c>""</c>) or a pointer consisting solely of <c>/</c>
    /// references the root node; no prefix is emitted for root-level errors
    /// because there is no single meaningful line for the whole document.
    /// </para>
    /// </remarks>
    /// <param name="root">The root mapping node of the YAML document.</param>
    /// <param name="pointer">A JSON Pointer string such as <c>/steps/0/method</c>.</param>
    /// <returns>
    /// The 1-based source line number of the resolved node, or
    /// <see langword="null"/> when the pointer cannot be resolved.
    /// </returns>
    private static long? ResolveLineFromPointer(YamlMappingNode root, string pointer)
    {
        if (string.IsNullOrEmpty(pointer) || pointer == "/")
            return null;

        // Split the pointer, discarding the empty leading segment produced by
        // the leading slash.
        var segments = pointer.Split('/');

        // segments[0] is always "" (from the leading slash); walk from index 1.
        YamlNode current = root;
        for (var i = 1; i < segments.Length; i++)
        {
            var seg = DecodePointerSegment(segments[i]);

            if (current is YamlMappingNode mapping)
            {
                var key = new YamlScalarNode(seg);
                if (!mapping.Children.TryGetValue(key, out var child))
                    return null;

                current = child;
            }
            else if (current is YamlSequenceNode sequence)
            {
                if (!int.TryParse(seg, out var index) ||
                    index < 0 ||
                    index >= sequence.Children.Count)
                {
                    return null;
                }

                current = sequence.Children[index];
            }
            else
            {
                // We hit a scalar or alias before exhausting all segments.
                return null;
            }
        }

        // YamlDotNet uses 1-based line numbers in Mark.Line.
        return current.Start.Line;
    }

    /// <summary>
    /// Decodes a single JSON Pointer segment per RFC 6901:
    /// <c>~1</c> → <c>/</c>, <c>~0</c> → <c>~</c>.
    /// The replacements must be applied in this order (tilde-one before
    /// tilde-zero) to avoid double-decoding.
    /// </summary>
    private static string DecodePointerSegment(string segment) =>
        segment.Replace("~1", "/", System.StringComparison.Ordinal)
               .Replace("~0", "~", System.StringComparison.Ordinal);
}
