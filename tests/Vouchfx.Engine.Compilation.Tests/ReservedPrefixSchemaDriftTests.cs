// Reserved-prefix drift gate (feat/close-remaining-surfaces, Part D).
//
// root-language-schema.json's 'capture' and 'variables' propertyNames guards
// each carry a regex that DUPLICATES VarKeys' reserved-prefix list as a
// hand-written string, linked back to VarKeys only by prose in a comment —
// nothing forces the two to agree. If a prefix were ever added, renamed, or
// removed in VarKeys without the schema regex following, the schema-level
// guard (CaptureSchemaTests / this file) and AstBuilder's own
// CheckReservedPrefix would silently diverge: one would keep accepting (or
// rejecting) a name the other no longer does, and every existing test would
// stay green because they all assert against LITERAL prefix strings, not
// against VarKeys itself.
//
// This binds the regex ACTUALLY SHIPPED in the schema to a pattern built
// programmatically from VarKeys' own constants, so a future prefix change
// that misses either site fails here with a precise diff, instead of
// drifting silently — mirrors DependencyKindStepMapDriftTests' own
// independently-derived-then-compared pattern (Vouchfx.Engine.Planning.Tests).
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Engine.Compilation.Schema;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

public sealed class ReservedPrefixSchemaDriftTests
{
    /// <summary>
    /// The reserved prefixes, in the exact order the schema's regex lists
    /// them — <see cref="VarKeys"/> is the single authoritative source; every
    /// other site (AstBuilder.CheckReservedPrefix, both schema
    /// propertyNames guards, CaptureSchemaTests' Theory data) must agree
    /// with it.
    /// </summary>
    private static readonly string[] s_reservedPrefixesInSchemaOrder =
    {
        VarKeys.ServicesPrefix,
        VarKeys.ConnectionsPrefix,
        VarKeys.OutcomePrefix,
        VarKeys.CaptureStatusPrefix,
        VarKeys.AttemptsPrefix,
    };

    /// <summary>
    /// Builds the EXACT regex text the schema is expected to carry, purely
    /// from <see cref="VarKeys"/>'s own constants — one negative lookahead
    /// per reserved prefix, anchored front and back.
    /// </summary>
    /// <remarks>
    /// MINOR-10 (second-round gatekeeper finding): each prefix is
    /// <see cref="Regex.Escape(string)"/>d before interpolation. A no-op for
    /// every CURRENT prefix (none contain a regex metacharacter — <c>:</c>
    /// and <c>_</c> are both literal), so this changes no test outcome
    /// today; it exists so a FUTURE prefix containing one (e.g. a
    /// hypothetical <c>.internal::</c>) is escaped correctly by this
    /// derivation rather than silently corrupting the comparison.
    /// </remarks>
    private static string BuildExpectedPattern() =>
        "^" + string.Concat(s_reservedPrefixesInSchemaOrder.Select(p => $"(?!{Regex.Escape(p)})")) + ".*$";

    [Theory]
    [InlineData("capture")]
    [InlineData("variables")]
    public void PropertyNamesPattern_MatchesVarKeysDerivedRegex(string propertyName)
    {
        var schemaJson = SchemaResources.ReadRootLanguageSchemaJson();
        using var doc = JsonDocument.Parse(schemaJson);

        var stepOrRoot = propertyName == "capture"
            ? doc.RootElement.GetProperty("$defs").GetProperty("step").GetProperty("properties")
            : doc.RootElement.GetProperty("properties");

        var actualPattern = stepOrRoot
            .GetProperty(propertyName)
            .GetProperty("propertyNames")
            .GetProperty("pattern")
            .GetString();

        Assert.Equal(BuildExpectedPattern(), actualPattern);
    }

    /// <summary>
    /// The two guards ('capture' and 'variables') must carry the IDENTICAL
    /// pattern — both exist to enforce the SAME rule (VarKeys' reserved
    /// prefixes), so any divergence between them, even one that still
    /// individually matches <see cref="BuildExpectedPattern"/>, would itself
    /// be a bug this comparison catches directly.
    /// </summary>
    [Fact]
    public void CaptureAndVariablesPropertyNamesPatterns_AreIdentical()
    {
        var schemaJson = SchemaResources.ReadRootLanguageSchemaJson();
        using var doc = JsonDocument.Parse(schemaJson);

        var capturePattern = doc.RootElement.GetProperty("$defs").GetProperty("step")
            .GetProperty("properties").GetProperty("capture")
            .GetProperty("propertyNames").GetProperty("pattern").GetString();

        var variablesPattern = doc.RootElement.GetProperty("properties").GetProperty("variables")
            .GetProperty("propertyNames").GetProperty("pattern").GetString();

        Assert.Equal(capturePattern, variablesPattern);
    }
}
