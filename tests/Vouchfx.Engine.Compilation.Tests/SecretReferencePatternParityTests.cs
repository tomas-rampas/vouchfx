// Schema-pattern ≡ SecretReference.TryParse parity gate (spec client-key-password, REQ-001).
//
// $defs/security's 'clientKeyPassword' field constrains its value with a JSON Schema
// 'pattern' so a literal passphrase is refused at authoring time (§17: references only,
// never literals). That pattern is a SECOND spelling of a grammar the engine already
// owns — SecretReference's own compiled Regex (SecretReference.cs) — and two spellings
// of one rule is exactly how the two drift, the failure mode SecurityArtifactPath's own
// header calls out.
//
// The drift is not hypothetical. A first draft of this spec wrote the pattern as
//   ^\$\{secret:[^{}/]+/[^{}]+\}$
// which diverges from the parser in BOTH directions at once:
//   • WIDER on 'source'   — the schema would accept '${secret:a.b/x}', which TryParse
//     rejects (its source class is [A-Za-z0-9_-], which admits no '.').
//   • NARROWER on 'path'  — the schema would reject a path containing '{', which
//     TryParse accepts (its path class is [^}], which admits '{').
// Either direction produces a value that passes one layer and fails the other: an
// author's suite validates and the engine then refuses it, or it fails validation for
// a value the engine would have resolved happily.
//
// This gate therefore asserts the two layers return the SAME verdict for every
// candidate in a fixed corpus, candidate by candidate. Two deliberate constraints on
// how it does that:
//   • The pattern is READ OUT of the freshly-composed schema at run time, never
//     restated here. A hard-coded second copy in this file would be a third spelling
//     of the same grammar and would go stale in precisely the way this gate exists to
//     prevent.
//   • The expected verdict per candidate is likewise not hard-coded. The reference
//     grammar is SecretReference.TryParse — the thing the engine actually runs — so the
//     assertion is equality of the two verdicts, with a separate non-vacuity check
//     proving the corpus contains both accepted and rejected shapes.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Sdk;
using Vouchfx.Steps.Script.Csharp;
using Xunit;

namespace Vouchfx.Engine.Compilation.Tests;

/// <summary>
/// Proves the composed schema's <c>clientKeyPassword</c> <c>pattern</c> and
/// <see cref="SecretReference.TryParse(string, out SecretReference?)"/> agree on every
/// candidate in a fixed corpus — see the file header for why a divergence in either
/// direction is a shipped defect rather than a cosmetic inconsistency.
/// </summary>
public sealed class SecretReferencePatternParityTests
{
    /// <summary>
    /// The JSON Pointer, inside the composed schema, of the pattern under test.
    /// </summary>
    private const string PatternLocation = "$defs/security/properties/clientKeyPassword/pattern";

    /// <summary>
    /// The candidate corpus. Every entry carries the reason it is present, so a future
    /// reader can tell which divergence each one guards against rather than inferring
    /// it from the string. No expected verdict is recorded: the verdict is whatever
    /// <see cref="SecretReference.TryParse(string, out SecretReference?)"/> says, and the
    /// schema is required to agree with it.
    /// </summary>
    /// <remarks>
    /// The eight cases REQ-001 names by hand are all present (a valid <c>env</c>
    /// reference; a <c>vault</c> reference with a multi-segment path; a dotted source; a
    /// source containing <c>/</c>; a path containing <c>{</c>; a reference with
    /// surrounding literal text; a bare literal; the empty string), plus five further
    /// boundary shapes that cost nothing to carry and close the obvious gaps around them.
    /// </remarks>
    public static TheoryData<string, string> Corpus() => new()
    {
        {
            "${secret:env/CLIENT_KEY_PASS}",
            "The ordinary shape: a valid 'env' reference, exactly as REQ-001's own accepted " +
            "corpus fixture declares it. If this is ever rejected the field is unusable."
        },
        {
            "${secret:vault/kv/data/mtls#clientKeyPassword}",
            "A valid 'vault' reference whose path is multi-segment and carries punctuation. " +
            "The path class is [^}], so '/' and '#' inside the PATH are ordinary characters — " +
            "only the FIRST '/' is structural."
        },
        {
            "${secret:a.b/x}",
            "A dotted source. The divergence the first spec draft shipped in the WIDER " +
            "direction: a source class of [^{}/] would accept this, while the parser's " +
            "[A-Za-z0-9_-] does not."
        },
        {
            "${secret:a/b/c}",
            "A source an author might intend to contain '/'. Neither layer supports that: " +
            "the first '/' terminates the source, so both read source 'a' and path 'b/c'. " +
            "Present to pin that the two layers REINTERPRET it identically, rather than one " +
            "accepting the author's intent and the other refusing it."
        },
        {
            "${secret:env/A{B}",
            "A path containing '{'. The divergence the first spec draft shipped in the " +
            "NARROWER direction: a path class of [^{}] would reject this, while the parser's " +
            "[^}] accepts it."
        },
        {
            "Bearer ${secret:env/T}",
            "A well-formed reference embedded in surrounding literal text. TryParse requires " +
            "the match to span the WHOLE token (Index == 0 && Length == token.Length), which " +
            "is what the schema pattern's anchors reproduce. An unanchored schema pattern " +
            "would accept this and smuggle a literal into the field."
        },
        {
            "hunter2",
            "A bare literal passphrase — the shape REQ-001 exists to refuse. §17 admits no " +
            "secret literal anywhere in the language."
        },
        {
            "",
            "The empty string. TryParse short-circuits on IsNullOrEmpty; the anchored pattern " +
            "cannot match zero characters. Both must refuse."
        },
        {
            "${secret:my-vault_2/kv/data/app}",
            "A source exercising the full [A-Za-z0-9_-] class (letter, digit, hyphen, " +
            "underscore) — the positive control for the source class the dotted case above " +
            "probes negatively."
        },
        {
            "${secret:env/T",
            "An unterminated reference: no closing brace. Both layers must refuse, rather " +
            "than one of them treating the missing brace as optional."
        },
        {
            "${secret:env/}",
            "An empty path. The path class is [^}]+ — one or more — in both layers."
        },
        {
            "${secret:/PATH}",
            "An empty source. The source class is [A-Za-z0-9_-]+ — one or more — in both layers."
        },
        {
            "${secret:env/A}B",
            "A well-formed reference with trailing literal text. The mirror of the 'Bearer …' " +
            "case above, guarding the closing anchor specifically."
        },
        {
            "${secret:env/CLIENT_KEY_PASS}" + "\n",
            "A reference with a trailing newline, which a '|' block scalar produces (measured, " +
            "YamlDotNet 15.1.6). .NET's '$' matches at end-of-input OR immediately before a " +
            "single trailing '\\n', while TryParse's whole-token rule (Index == 0 && Length == " +
            "token.Length) does not — so a bare '$' anchor ACCEPTS this here and the engine " +
            "then REFUSES it. Guards the closing anchor against exactly that divergence."
        },
    };

    /// <summary>
    /// The core parity assertion: for every candidate, the composed schema's
    /// <c>clientKeyPassword</c> pattern and <see cref="SecretReference.TryParse(string, out SecretReference?)"/>
    /// must return the same verdict.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void SchemaPattern_AndTryParse_AgreeOnEveryCandidate(string candidate, string why)
    {
        var parserAccepts = SecretReference.TryParse(candidate, out _);
        var schemaAccepts = SchemaPatternAccepts(candidate);

        Assert.True(
            parserAccepts == schemaAccepts,
            $"DIVERGENCE for candidate '{candidate}'.{Environment.NewLine}" +
            $"  SecretReference.TryParse : {(parserAccepts ? "ACCEPTS" : "REJECTS")}{Environment.NewLine}" +
            $"  schema pattern           : {(schemaAccepts ? "ACCEPTS" : "REJECTS")}{Environment.NewLine}" +
            $"  pattern under test       : {ReadClientKeyPasswordPattern()}{Environment.NewLine}" +
            $"  why this candidate is in the corpus: {why}{Environment.NewLine}" +
            "The schema pattern must be the ANCHORED form of SecretReference's own grammar, " +
            "character class for character class. A value that passes one layer and fails " +
            "the other is a defect in whichever layer was changed last.");
    }

    /// <summary>
    /// Non-vacuity guard. The parity assertion above compares two verdicts, so it would
    /// pass trivially against a corpus every layer rejects (or accepts) — including one
    /// accidentally emptied by a future edit. Pins that the corpus exercises both sides.
    /// </summary>
    [Fact]
    public void Corpus_ExercisesBothAcceptedAndRejectedShapes()
    {
        var candidates = Corpus().Select(row => (string)row[0]!).ToList();

        var accepted = candidates.Count(c => SecretReference.TryParse(c, out _));
        var rejected = candidates.Count - accepted;

        Assert.True(
            accepted >= 3,
            $"Expected at least 3 ACCEPTED candidates in the parity corpus, found {accepted}. " +
            "A corpus of rejections alone would let a pattern that accepts nothing pass this gate.");

        Assert.True(
            rejected >= 3,
            $"Expected at least 3 REJECTED candidates in the parity corpus, found {rejected}. " +
            "A corpus of acceptances alone would let a pattern that accepts everything pass this gate.");
    }

    /// <summary>
    /// A structural check alongside the behavioural corpus: the schema pattern must be
    /// anchored at both ends, and must carry the parser's own character classes, in the
    /// parser's own order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are DERIVED at run time — the grammar from
    /// <see cref="SecretReference"/>'s compiled regex (by reflection over its single static
    /// <see cref="Regex"/> field), the end-anchoring from a probe through the real
    /// evaluator — never restated here; this file must not become a third spelling of the
    /// grammar.
    /// </para>
    /// <para>
    /// The end-anchor half is stated as the PROPERTY it must have, never as the literal
    /// spelling that happens to provide it, and that distinction is the whole reason this
    /// assertion was rewritten. It previously asserted <c>EndsWith('$')</c>, which a bare
    /// <c>$</c> satisfies while NOT reproducing TryParse's whole-token rule: .NET's
    /// <c>$</c> matches at end-of-input OR immediately before a single trailing
    /// <c>\n</c>, so a <c>$</c>-anchored pattern accepted a reference carrying the trailing
    /// newline a YAML <c>|</c> block scalar produces, which TryParse then refused. A
    /// literal-string assertion is what let that through; asserting the behaviour cannot.
    /// </para>
    /// <para>
    /// Why in addition to the corpus: the corpus proves agreement on the shapes somebody
    /// thought to write down, which is a sampling argument. This proves the two are the
    /// same grammar by construction, so a widening of the parser's source class that no
    /// corpus entry happens to probe still fails here, naming exactly which class moved.
    /// </para>
    /// </remarks>
    [Fact]
    public void SchemaPattern_IsTheAnchoredFormOfTheParserGrammar_CharacterClassForCharacterClass()
    {
        var schemaPattern = ReadClientKeyPasswordPattern();
        var parserPattern = ParserGrammar();

        Assert.True(
            schemaPattern.StartsWith('^'),
            $"The '{PatternLocation}' pattern must be anchored at its START — TryParse " +
            "requires its match to begin at index 0, and a pattern without '^' would accept " +
            $"a reference preceded by literal text. Got: '{schemaPattern}'.");

        const string wellFormedReference = "${secret:env/CLIENT_KEY_PASS}";

        Assert.True(
            SchemaPatternAccepts(wellFormedReference),
            $"The '{PatternLocation}' pattern must accept the ordinary well-formed reference " +
            $"'{wellFormedReference}'; the end-anchoring assertion below is vacuous against a " +
            $"pattern that accepts nothing. Got pattern: '{schemaPattern}'.");

        Assert.False(
            SchemaPatternAccepts(wellFormedReference + "\n"),
            $"The '{PatternLocation}' pattern must be anchored at its END in the sense " +
            "TryParse means — no character of ANY kind may follow the closing brace. It " +
            $"accepted '{wellFormedReference}' with a trailing newline, which TryParse " +
            "refuses (its match must span the whole token), so the two layers disagree on a " +
            "value ordinary YAML produces: a '|' block scalar appends exactly this newline. " +
            "A bare '$' has this defect in .NET and is why this is asserted as a behaviour " +
            $"rather than as a spelling. Got pattern: '{schemaPattern}'.");

        var parserClasses = CharacterClasses(parserPattern);
        var schemaClasses = CharacterClasses(WithoutLookarounds(schemaPattern));

        Assert.True(
            parserClasses.SequenceEqual(schemaClasses, StringComparer.Ordinal),
            "The schema pattern's character classes have DIVERGED from " +
            $"SecretReference's own grammar.{Environment.NewLine}" +
            $"  parser grammar : {parserPattern}{Environment.NewLine}" +
            $"  parser classes : [{string.Join(", ", parserClasses)}]{Environment.NewLine}" +
            $"  schema pattern : {schemaPattern}{Environment.NewLine}" +
            $"  schema classes : [{string.Join(", ", schemaClasses)}]{Environment.NewLine}" +
            "Whichever layer moved, move the other with it — or the two accept different " +
            "sets of values and an author hits the difference at run time.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates <paramref name="candidate"/> against a minimal subschema carrying ONLY
    /// the pattern read out of the composed schema, through JsonSchema.Net — the same
    /// evaluator a real document hits — rather than through a bare
    /// <see cref="Regex"/>.<see cref="Regex.IsMatch(string, string)"/>, so the verdict is
    /// the one an author's suite would actually get.
    /// </summary>
    private static bool SchemaPatternAccepts(string candidate)
    {
        var subschemaJson = new JsonObject
        {
            ["type"] = "string",
            ["pattern"] = ReadClientKeyPasswordPattern(),
        }.ToJsonString();

        var subschema = JsonSchema.FromText(subschemaJson);

        // Evaluated as a JsonElement, matching the overload the real validation path uses
        // (see SchemaSecuritySurfaceClosureTests) — the candidate is round-tripped through
        // JSON so escaping is the serialiser's problem, never this file's.
        using var instance = JsonDocument.Parse(JsonSerializer.Serialize(candidate));

        return subschema.Evaluate(instance.RootElement).IsValid;
    }

    /// <summary>
    /// The <c>clientKeyPassword</c> pattern, read out of the freshly-composed schema.
    /// Composed once for the whole fixture: composition is deterministic, so recomposing
    /// per candidate would buy nothing.
    /// </summary>
    /// <remarks>
    /// A minimal (single-provider) registry suffices: <c>$defs/security</c> belongs to the
    /// ROOT language schema and is not provider-composed, exactly as
    /// <c>SchemaSecuritySurfaceClosureTests</c> already relies on.
    /// </remarks>
    private static readonly Lazy<string> s_clientKeyPasswordPattern = new(() =>
    {
        var registry = StepKindRegistry.BuildAndFreeze(new[] { typeof(ScriptCsharpProvider).Assembly });
        var composed = SchemaComposer.ComposeSchemaJson(registry);

        using var document = JsonDocument.Parse(composed);

        var node = document.RootElement;
        foreach (var segment in PatternLocation.Split('/'))
        {
            var found = node.ValueKind == JsonValueKind.Object
                && node.TryGetProperty(segment, out _);

            Assert.True(
                found,
                $"The composed schema has no '{PatternLocation}'. REQ-001 requires " +
                "$defs/security to declare a 'clientKeyPassword' property constrained by a " +
                "'pattern'; without it this parity gate has nothing to compare against. " +
                $"Resolution stopped at segment '{segment}'.");

            node = node.GetProperty(segment);
        }

        Assert.True(
            node.ValueKind == JsonValueKind.String,
            $"'{PatternLocation}' must be a string. Got: {node.ValueKind}.");

        return node.GetString()!;
    });

    private static string ReadClientKeyPasswordPattern() => s_clientKeyPasswordPattern.Value;

    /// <summary>
    /// Extracts <see cref="SecretReference"/>'s compiled grammar by reflection over its
    /// single static <see cref="Regex"/> field — the authoritative source, read rather
    /// than copied.
    /// </summary>
    private static string ParserGrammar()
    {
        var fields = typeof(SecretReference)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(Regex))
            .ToList();

        Assert.True(
            fields.Count == 1,
            $"Expected SecretReference to declare exactly one static Regex field (its own " +
            $"grammar); found {fields.Count}. This gate reads that field rather than " +
            "restating the grammar, so an additional (or removed) one needs a deliberate " +
            "decision about which is authoritative.");

        return ((Regex)fields[0].GetValue(null)!).ToString();
    }

    /// <summary>
    /// Returns every bracketed character class in <paramref name="pattern"/>, in
    /// left-to-right order — e.g. <c>[A-Za-z0-9_-]</c> then <c>[^}]</c>. Named-group
    /// syntax and quantifiers are ignored, so the parser's
    /// <c>(?&lt;source&gt;[A-Za-z0-9_-]+)</c> and the schema's bare <c>[A-Za-z0-9_-]+</c>
    /// compare equal on the part that decides which characters are legal.
    /// </summary>
    private static List<string> CharacterClasses(string pattern) =>
        Regex.Matches(pattern, @"\[\^?(?:\\.|[^\]\\])+\]")
            .Select(m => m.Value)
            .ToList();

    /// <summary>
    /// Removes zero-width lookaround groups — <c>(?=…)</c>, <c>(?!…)</c>, <c>(?&lt;=…)</c>,
    /// <c>(?&lt;!…)</c> — from <paramref name="pattern"/>, so that
    /// <see cref="CharacterClasses(string)"/> sees only the classes that decide which
    /// characters a MATCHED value may contain.
    /// </summary>
    /// <remarks>
    /// A lookaround consumes nothing, so a character class inside one is anchoring
    /// machinery rather than grammar. Concretely: the schema's end anchor is
    /// <c>(?![\s\S])</c> — "no character of any kind follows" — whose <c>[\s\S]</c> would
    /// otherwise be compared against a parser class that has no counterpart to it, and the
    /// class-for-class assertion would fail for a pattern that is in fact correct. Handles
    /// the non-nested case only, which is all any anchor of this shape needs; a nested
    /// lookaround would leave a residue and fail loudly rather than pass silently.
    /// </remarks>
    private static string WithoutLookarounds(string pattern) =>
        Regex.Replace(pattern, @"\(\?<?[=!](?:\\.|[^()\\])*\)", string.Empty);
}
