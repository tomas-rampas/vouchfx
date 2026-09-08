// S02-C-02 — shared internal helpers for schema loading and YAML→JSON conversion.
//
// Extracted from YamlSchemaValidator so that SchemaComposer can reuse both the
// embedded resource loader and the YAML→JsonDocument bridge without duplication.
using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vouchfx.Engine.Compilation.Schema;

/// <summary>
/// Shared internal helpers for loading embedded schema resources and converting
/// YAML documents to <see cref="JsonDocument"/> instances.
/// </summary>
/// <remarks>
/// Both <see cref="YamlSchemaValidator"/> and <see cref="SchemaComposer"/> use
/// these helpers.  Keeping them here avoids duplicating the YAML→JSON bridge and
/// the embedded-resource lookup pattern.
/// </remarks>
internal static class SchemaResources
{
    // ── YAML→JSON expansion budget (issue #505) ──────────────────────────────
    //
    // ConvertYamlToJsonDocument is a two-step bridge, and only the SECOND step can blow
    // up. Measured on the pinned YamlDotNet 16.3.0 with a chain of anchored sequences,
    // ten aliases per level, from a document of levels+1 lines:
    //
    //     levels=3   jsonChars =      8,052       95 ms   WS =  28 MB
    //     levels=4   jsonChars =     80,280       33 ms   WS =  39 MB
    //     levels=5   jsonChars =    802,508      374 ms   WS =  44 MB
    //     levels=6   jsonChars =  8,024,736    1,393 ms   WS =  80 MB
    //     levels=7   jsonChars = 80,246,964   13,795 ms   WS = 367 MB
    //
    // The levels=3 row is the FIRST measurement taken in that process, so its 95 ms carries the
    // JIT warm-up of the deserialiser and emitter — which is why it reads slower than levels=4.
    // Read the time trend from levels=4 down; the jsonChars column is unaffected by any of that.
    //
    // Ten times the output per LINE added, while the working set grows by well under
    // that. The asymmetry is the whole diagnosis: step 1's deserialiser binds ONE object
    // instance per anchor and hands back the SAME reference at every alias site, so the
    // graph it returns is a shared DAG whose size tracks the document. Step 2 walks that
    // DAG and re-materialises every alias into the output text, so it pays the full
    // expansion. Bound step 2 and the amplification is bounded; bounding step 1 would
    // measure a quantity that never grows.
    //
    // A visited set is NOT the fix, for the same reason it was rejected in
    // HttpRestProvider's sibling bound: `*defaults` under two keys is legitimate YAML
    // that MUST expand twice, and the object graph cannot distinguish that from the
    // amplifying case except by measuring the result. So the defence is a budget on the
    // characters PRODUCED — the exact quantity amplification blows up — enforced by a
    // counting TextWriter that throws as the count crosses the line. It is deliberately
    // NOT "serialise to a string, then check its length": building the string is the
    // fault, so measuring it afterwards prevents nothing.
    //
    // A suite author is trusted input — script.csharp hands them arbitrary C# — so this
    // is robustness and mistake-catching, NOT a security boundary, and nothing here
    // should be described as one. What it buys is a CATCHABLE throw on an ordinary
    // authoring channel: both callers (SchemaComposer.Validate, YamlSchemaValidator.Validate)
    // already wrap this method in `catch (Exception ex)` and return
    // SchemaValidationResult.Invalid("Failed to parse YAML: " + ex.Message), so the
    // refusal arrives as the same located diagnostic an unparseable document already
    // produced. No new error channel, no new exit code.

    /// <summary>
    /// Maximum number of characters the YAML→JSON conversion's step 2 may emit before it
    /// is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// DERIVED, and the derivation is two measurements plus one existing cap. The largest
    /// real <c>.e2e.yaml</c> in this repository (<c>examples/security-mtls.e2e.yaml</c>,
    /// 23,528 characters) converts to <strong>8,293</strong> characters of JSON — every
    /// suite measured converts to FEWER characters than it occupies, because comments and
    /// block structure do not survive. The alias-free worst case is therefore governed by
    /// the input, not by the shape: measured against a synthetic, maximally dense but
    /// entirely legitimate document at the CLI's own 1 MiB single-document cap
    /// (<c>ScenarioDiscovery.MaxDocumentSizeBytes</c>), a flat mapping of short keys — the
    /// shape that maximises JSON punctuation per YAML character — expands by a factor of
    /// <strong>1.37</strong>, to 1,433,932 characters. 16 Mi characters is ~11.7x that
    /// figure and ~2,000x the largest suite the repository actually contains, so no
    /// document a person writes reaches it.  That 1 MiB premise is the CLI's, not the
    /// engine's: <c>ProviderTestHarness.RunSingleStepAsync</c> hands
    /// <c>DocumentValidator.Validate</c> an in-memory string with no size cap at all.  The
    /// budget still bounds that path, because it measures the conversion's OUTPUT rather than
    /// its input; what the 1 MiB figure bounds is only the headroom argument above.
    /// </para>
    /// <para>
    /// From the other side it is ~1/5 of the seven-level alias chain in the block comment
    /// above, which is what makes the refusal arrive early rather than after the fact. The
    /// bound is on characters, not bytes, and what it bounds is the CONVERSION, not the
    /// process.  The guard reserves before appending, so the builder never grows past 16 Mi
    /// characters (32 MiB of UTF-16) — but the conversion holds more than one copy of that
    /// text on its way out: the builder and <c>ToString()</c>'s copy exist together, then that
    /// string and <c>JsonDocument.Parse</c>'s UTF-8 buffer exist together.  Peak is a small
    /// multiple of the figure, briefly, not the figure.
    /// </para>
    /// </remarks>
    private const int MaxJsonChars = 16 * 1024 * 1024;

    /// <summary>
    /// Reads the embedded <c>root-language-schema.json</c> resource from the
    /// <see cref="Vouchfx.Engine.Compilation"/> assembly and returns its raw
    /// JSON text.
    /// </summary>
    /// <returns>
    /// The full JSON text of the embedded root-language schema.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the embedded resource is not found or cannot be opened.
    /// </exception>
    internal static string ReadRootLanguageSchemaJson()
    {
        var assembly = typeof(SchemaResources).Assembly;

        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("root-language-schema.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "Embedded resource 'root-language-schema.json' was not found in assembly " +
                $"'{assembly.FullName}'.  Verify the <EmbeddedResource> item in the project file.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Could not open manifest resource stream for '{resourceName}'.");

        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Converts a YAML document string to a <see cref="JsonDocument"/> via
    /// YamlDotNet's JSON-compatible serialisation bridge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller is responsible for disposing the returned
    /// <see cref="JsonDocument"/> to release its pooled UTF-8 buffer.
    /// Evaluation of the root element must occur inside the <c>using</c> scope.
    /// </para>
    /// <para>
    /// Type inference: plain YAML scalars that parse as <see cref="long"/>
    /// or <see cref="double"/> are emitted as JSON numbers (not quoted strings),
    /// preserving the type information required by the JSON Schema validator's
    /// <c>type: integer</c> and <c>type: number</c> constraints.
    /// </para>
    /// <para>
    /// <strong>Step 2 is budgeted at 16 Mi characters, and step 2 is the right
    /// measurement point (issue #505).</strong>  Step 1's deserialiser binds one object
    /// instance per anchor and returns the SAME reference at every alias site, so the
    /// graph it produces is a shared DAG whose size tracks the document; step 2 walks
    /// that DAG and re-materialises every alias into the output text, so it is the step
    /// an anchor/alias chain amplifies.  The budget is therefore applied to the
    /// characters step 2 emits, by a counting <see cref="TextWriter"/> that refuses as
    /// the count crosses the line rather than after the whole string exists.
    /// </para>
    /// <para>
    /// <strong>Robustness, not a security boundary.</strong>  A suite author is trusted
    /// input — <c>script.csharp</c> hands them arbitrary C# — so this bound catches a
    /// mistake and keeps the conversion's memory bounded; it defends nothing against an
    /// adversary and is not a control.
    /// </para>
    /// </remarks>
    /// <param name="yamlText">The raw YAML text to convert.</param>
    /// <returns>A <see cref="JsonDocument"/> representing the parsed document.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the YAML document is entirely empty, or when its conversion to JSON
    /// exceeds the 16 Mi-character budget described above.  Both callers
    /// (<see cref="SchemaComposer.Validate"/>, <see cref="YamlSchemaValidator.Validate"/>)
    /// already wrap this method in a <c>catch (Exception)</c> that returns
    /// <c>SchemaValidationResult.Invalid("Failed to parse YAML: …")</c>, so either
    /// refusal reaches the author as an ordinary located diagnostic.
    /// </exception>
    internal static JsonDocument ConvertYamlToJsonDocument(string yamlText)
    {
        // Step 1 — parse YAML to a plain object graph using the type-inferring
        // deserialiser so that untagged integer scalars (e.g. status: 200) are
        // returned as long/int rather than string.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .WithNodeTypeResolver(new YamlScalarTypeResolver())
            .Build();

        var graph = deserializer.Deserialize<object?>(yamlText);

        if (graph is null)
        {
            // An entirely empty YAML document deserialises to null; surface this
            // as an invalid result rather than letting the schema validator
            // receive a null node.
            throw new InvalidOperationException("The YAML document is empty.");
        }

        // Step 2 — re-serialise the object graph to a JSON string using
        // YamlDotNet's JSON-compatible emitter, which correctly renders
        // Dictionary<object,object> entries as JSON object properties and
        // emits long/double values as JSON number literals (not quoted strings).
        //
        // THE TextWriter OVERLOAD, NOT Serialize(object), and the difference is the whole
        // fix (#505).  The parameterless overload accumulates into a writer of its own that
        // this code cannot reach, so the count has nowhere to live and the first observable
        // moment is a finished string — 160 MB of it at seven levels, ten times that per
        // further line.  Writing THROUGH the budgeted writer is what makes the stop early
        // and the memory bounded.
        var jsonSerializer = new SerializerBuilder()
            .JsonCompatible()
            .Build();

        string json;
        using (var writer = new BudgetedJsonWriter())
        {
            jsonSerializer.Serialize(writer, graph);
            json = writer.ToString();
        }

        // Step 3 — parse the JSON string into a System.Text.Json JsonDocument.
        return JsonDocument.Parse(json);
    }

    // ── BudgetedJsonWriter — the step-2 output budget ────────────────────────

    /// <summary>
    /// A <see cref="TextWriter"/> that accumulates into a <see cref="StringBuilder"/> and
    /// throws once the accumulated character count would exceed
    /// <see cref="MaxJsonChars"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derives from <see cref="TextWriter"/> rather than <see cref="StringWriter"/> ON
    /// PURPOSE.  <see cref="StringWriter"/> overrides several write paths (notably the
    /// <c>ReadOnlySpan&lt;char&gt;</c> one) straight onto its own builder, so a subclass
    /// that guarded only <c>Write(char)</c>/<c>Write(string)</c> would leave holes through
    /// which output could reach the buffer uncounted.  <see cref="TextWriter"/>'s own
    /// defaults funnel every remaining overload into the three overridden here, so the
    /// count cannot be bypassed.
    /// </para>
    /// <para>
    /// The check RESERVES — it runs before the append, so the builder never holds more
    /// than the budget and the refusal costs no additional memory.
    /// </para>
    /// </remarks>
    private sealed class BudgetedJsonWriter : TextWriter
    {
        /// <summary>
        /// BOM-less UTF-16 — what <see cref="StringWriter"/> reports from
        /// <see cref="StringWriter.Encoding"/>, which <see cref="Encoding.Unicode"/> differs from
        /// only by carrying a byte-order-mark preamble.  YamlDotNet's emitter never reads this
        /// property, so nothing observes the difference; matching the ordinary string-writing
        /// TextWriter costs a line, so it is taken rather than reasoned about.
        /// </summary>
        private static readonly Encoding s_encoding =
            new UnicodeEncoding(bigEndian: false, byteOrderMark: false);

        private readonly StringBuilder _builder = new();

        /// <inheritdoc/>
        public override Encoding Encoding => s_encoding;

        /// <inheritdoc/>
        public override IFormatProvider FormatProvider => CultureInfo.InvariantCulture;

        /// <inheritdoc/>
        public override void Write(char value)
        {
            Reserve(1);
            _builder.Append(value);
        }

        /// <inheritdoc/>
        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            Reserve(value.Length);
            _builder.Append(value);
        }

        /// <inheritdoc/>
        public override void Write(char[] buffer, int index, int count)
        {
            Reserve(count);
            _builder.Append(buffer, index, count);
        }

        /// <inheritdoc/>
        public override string ToString() => _builder.ToString();

        private void Reserve(int count)
        {
            if (_builder.Length + (long)count > MaxJsonChars)
            {
                // InvariantCulture explicitly: an interpolated `:N0` formats under CurrentCulture,
                // so on a de-DE / fr-FR / pt-BR host this message would read "16.777.216" and the
                // test that asserts the grouped figure would fail there and only there. No
                // <InvariantGlobalization> is set in Directory.Build.props, and CI's Linux agents
                // resolve to invariant, so the omission would land as a developer-machine-only red.
                // The writer above already pins InvariantCulture for the same reason.
                throw new InvalidOperationException(
                    "The YAML document expands to more than "
                    + MaxJsonChars.ToString("N0", CultureInfo.InvariantCulture)
                    + " characters "
                    + "(16 Mi) of JSON during schema conversion. This is usually caused by YAML "
                    + "anchors and aliases (&anchor / *alias): the document stores an anchored "
                    + "node once but writes it out once per alias site, so the expansion "
                    + "multiplies at every level: a document of nine lines can reach "
                    + "gigabytes. Reduce the aliasing - or, if the suite really is this large, "
                    + "split it into several documents.");
            }
        }
    }

    // ── YamlScalarTypeResolver — infers integer and float types ──────────────

    /// <summary>
    /// A YamlDotNet node-type resolver that recognises plain (untagged) scalar
    /// values that represent integers or floating-point numbers and returns their
    /// CLR type (<see cref="long"/> or <see cref="double"/>) instead of the
    /// default <see cref="string"/>.  This preserves the JSON Schema <c>type:
    /// integer</c> / <c>type: number</c> constraints during YAML→JSON conversion.
    /// </summary>
    private sealed class YamlScalarTypeResolver : INodeTypeResolver
    {
        bool INodeTypeResolver.Resolve(
            NodeEvent? nodeEvent,
            ref Type currentType)
        {
            if (nodeEvent is Scalar scalar
                && scalar.Style == YamlDotNet.Core.ScalarStyle.Plain
                && currentType == typeof(object))
            {
                var value = scalar.Value;

                // Prefer long for integer-shaped values (covers status codes,
                // port numbers, counts, etc.).
                if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    currentType = typeof(long);
                    return true;
                }

                // Fall back to double for floating-point values.
                if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    currentType = typeof(double);
                    return true;
                }

                // Recognise plain true/false (case-insensitive) as CLR bool so
                // that the JSON Schema validator receives a JSON boolean literal
                // (true/false, unquoted) rather than the string "true"/"false".
                // Only strict true/false — YAML 1.1 aliases (yes/no/on/off) are
                // deliberately excluded to avoid coercing unrelated string fields.
                if (bool.TryParse(value, out _))
                {
                    currentType = typeof(bool);
                    return true;
                }
            }

            return false;
        }
    }
}
