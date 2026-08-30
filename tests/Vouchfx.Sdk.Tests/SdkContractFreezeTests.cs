// S08-F-02 (T3 — Freeze the v1 provider contract).
//
// The Vouchfx.Sdk public surface IS the v1 provider contract.  This
// golden-snapshot test reflects over the whole Vouchfx.Sdk assembly, emits a
// canonical, deterministic text signature of every public type and member, and
// asserts it is byte-for-byte (newline-normalised) identical to the committed
// golden artifact (Golden/vouchfx-sdk-public-api.v1.txt).
//
// Why this gate exists:
//   • The provider contract is FROZEN for the v1.x engine series (CLAUDE.md §13,
//     blueprint §13.8.1).  The frozen core interfaces — IStepModel, IStepProvider,
//     IStepBinder<TModel>, IStepValidator<TModel>, IStepCompiler<TModel>,
//     IResourceContributor<TModel> — and the supporting records/contexts MUST NOT
//     change.  Evolution is additive ONLY, via NEW optional interfaces (this is
//     exactly how S7 added IStepDiffRenderer / IHostResourceContributor); a v1
//     interface is never mutated.
//   • This test makes any change to the public surface a DELIBERATE, REVIEWED act:
//     it fails until the golden is regenerated and re-reviewed.
//
// Canonicalization / inclusion rule (mirror this when regenerating the golden):
//   • Types: every public type in the assembly (IsPublic || IsNestedPublic),
//     sorted by full reflection name ordinally.  Each type emits a "kind" header
//     (interface / record / class / struct / enum / delegate) + its generic
//     parameters + its declared base type and directly-implemented interfaces.
//   • Members: only members DECLARED on the type (BindingFlags.DeclaredOnly), public
//     instance + static, sorted ordinally by a stable per-member signature string.
//   • EXCLUDED as compiler-generated noise (a record's synthesised surface adds no
//     contract information and its formatting is unstable across SDKs):
//       - anything marked [CompilerGenerated],
//       - the record value-equality / copy surface:
//         EqualityContract, <Clone>$, Equals(object), Equals(<self>),
//         GetHashCode, ToString, PrintMembers, Deconstruct,
//         op_Equality, op_Inequality, and the copy-constructor (T(T)).
//     The positional record PROPERTIES and any author-declared members survive —
//     those ARE the contract.  Enum members are emitted as named constants.
//   • Type names are formatted by a single deterministic formatter (FormatType):
//     generic type parameters render by their own name (TModel), constructed
//     generics render as Name<Arg,Arg>, arrays/byref/nullable-annotations are
//     handled uniformly.  Never rely on reflection member ORDER (it is unstable);
//     everything is sorted with StringComparer.Ordinal / string.CompareOrdinal.
//
// Robustness: like SchemaFreezeTests, both sides are newline-normalised and have
// any trailing final newline trimmed, so a CRLF/LF checkout difference or an
// editor's insert_final_newline rewrite of the golden never produces spurious drift.
//
// The canonical signature builder (SdkPublicApiSignature) is shared from
// Vouchfx.TestSupport so that this gate and the Vouchfx.Sdk.Testing freeze gate
// emit byte-comparable goldens from ONE implementation (S08-F-02 / M4 follow-up).
//
// THE FROZEN SURFACE IS NOT ONLY THE SIGNATURE — IT INCLUDES CSX HELPER SOURCE BODIES (#361):
//   Vouchfx.Sdk exposes CSX helper classes (SecurityHelper, SecretHelper, SubstituteHelper,
//   KafkaSecurityHelper) whose whole public surface is one `public const string Source` holding
//   the text of a static class that providers splice into CsxFragment.RequiredHelpers.  A const
//   string's VALUE is INLINED into every consuming assembly at ITS compile time, and CsxAssembler
//   throws CsxAssemblyException when two fragments declare the same helper class with different
//   source text.  So an out-of-tree provider compiled against an OLDER SDK carries the OLD body
//   verbatim, and a suite mixing it with an in-tree provider on the NEW SDK fails to assemble.
//   Editing a helper BODY is therefore a breaking change to the v1 contract, exactly as editing
//   an interface would be — but the signature golden cannot see it: it records
//   `field const System.String Source`, which is byte-identical whichever text the const holds.
//   MEASURED while building this gate: a 13-character edit to a comment INSIDE
//   SubstituteHelper.Source left VouchfxSdkPublicApi_MatchesGolden_ByteForByte green and moved
//   no line of vouchfx-sdk-public-api.v1.txt, while the new hash gate went red on it.
//   CsxHelperSources_MatchGolden_ByteForByte closes that hole by pinning a SHA-256 of each
//   helper's Source VALUE in a companion golden, so a body edit reds the gate and must be
//   regenerated and reviewed like any other contract change.
//
// REGENERATION (when the v1 provider contract legitimately changes — additive only):
//   VOUCHFX_REGEN_SDK_CONTRACT=1 dotnet test tests/Vouchfx.Sdk.Tests \
//     --filter "FullyQualifiedName~SdkContractFreezeTests"
//   This rewrites BOTH Golden/vouchfx-sdk-public-api.v1.txt (from the freshly-reflected surface)
//   and Golden/vouchfx-sdk-helper-sources.v1.txt (from the freshly-read helper constants) — one
//   flag, because the two artifacts pin two halves of one contract and regenerating only half is
//   never the intent.  Review the diff (the signature must be additive only; a moved helper hash
//   means a body edit, which needs the compatibility argument above answered), then commit.
//   Mirror of SchemaFreezeTests.IsRegenRequested / VOUCHFX_REGEN_SCHEMA.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Vouchfx.Sdk;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Sdk.Tests;

/// <summary>
/// S08-F-02: the frozen-v1-provider-contract golden-snapshot gate over the
/// whole <c>Vouchfx.Sdk</c> public surface.
/// </summary>
public sealed class SdkContractFreezeTests
{
    /// <summary>
    /// Banner-line-1 title for the <c>Vouchfx.Sdk</c> golden — the FROZEN v1 provider
    /// contract.  Kept byte-identical to <c>main</c> so the frozen provider golden does
    /// not churn (only its shared line-2 provenance note changed on this branch).
    /// </summary>
    private const string BannerTitle =
        "Vouchfx.Sdk v1 provider contract — FROZEN for the v1.x engine series.";

    /// <summary>
    /// The reflected public API of <c>Vouchfx.Sdk</c> must be byte-for-byte
    /// (newline-normalised) identical to the committed golden.  If this fails,
    /// the v1 provider contract has drifted.
    /// </summary>
    [Fact]
    public void VouchfxSdkPublicApi_MatchesGolden_ByteForByte()
    {
        var actual = SdkPublicApiSignature.Build(typeof(IStepProvider).Assembly, BannerTitle);

        // Regeneration mode: rewrite the committed golden from the freshly-reflected
        // surface and pass.  Mirror of SchemaFreezeTests.ComposedV1Schema_MatchesGolden_ByteForByte.
        if (IsRegenRequested())
        {
            var repoRoot = FindRepoRoot();
            var goldenPath = Path.Combine(
                repoRoot, "tests", "Vouchfx.Sdk.Tests",
                "Golden", "vouchfx-sdk-public-api.v1.txt");
            File.WriteAllText(goldenPath, actual);
            return;
        }

        var golden = ReadGolden("vouchfx-sdk-public-api.v1.txt");

        var actualNormalised = Normalise(actual);
        var goldenNormalised = Normalise(golden);

        Assert.True(
            string.Equals(actualNormalised, goldenNormalised, StringComparison.Ordinal),
            "The Vouchfx.Sdk v1 provider contract has DRIFTED. The v1 contract is "
            + "FROZEN for the v1.x engine series — extend via a NEW optional interface, "
            + "never mutate a v1 interface. If this addition is intentional, regenerate "
            + "Golden/vouchfx-sdk-public-api.v1.txt with VOUCHFX_REGEN_SDK_CONTRACT=1 and "
            + "get it reviewed."
            + Environment.NewLine
            + FirstDifference(goldenNormalised, actualNormalised));
    }

    /// <summary>
    /// The six frozen CORE provider interfaces (and the discovery attribute) must
    /// remain present in the public surface.  This is a coarse structural guard
    /// that complements the byte-for-byte golden: if a core interface is renamed
    /// or removed, this fails with a contract-specific message even before the
    /// golden diff is read.
    /// </summary>
    [Fact]
    public void FrozenCoreProviderInterfaces_RemainPresent()
    {
        var assembly = typeof(IStepProvider).Assembly;

        // The six frozen core provider interfaces (CLAUDE.md §13, blueprint §13.8.1).
        // Generic interfaces are matched by their open-generic reflection name.
        string[] requiredCore =
        {
            "Vouchfx.Sdk.IStepModel",
            "Vouchfx.Sdk.IStepProvider",
            "Vouchfx.Sdk.IStepBinder`1",
            "Vouchfx.Sdk.IStepValidator`1",
            "Vouchfx.Sdk.IStepCompiler`1",
            "Vouchfx.Sdk.IResourceContributor`1",
        };

        var present = assembly.GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Select(t => t.FullName ?? t.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in requiredCore)
        {
            Assert.True(
                present.Contains(name),
                $"Frozen v1 core provider interface '{name}' is missing from Vouchfx.Sdk. "
                + "The v1 contract is FROZEN — a core interface may never be renamed or removed.");
        }
    }

    /// <summary>
    /// The DEFAULT-ness of a default-implemented interface member is part of the frozen contract,
    /// and the byte-for-byte golden cannot see it. This is the missing half of that gate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What the golden misses, measured rather than argued.</strong>
    /// <c>SdkPublicApiSignature.MemberLines</c> emits a property's type, name and accessor KINDS
    /// (<c>get</c>/<c>set</c>/<c>init</c>) and never inspects <c>IsAbstract</c>. The committed
    /// golden proves it: <c>ICompileContext.DeclaredServices</c> (which HAS a default
    /// implementation) and <c>IProjectContext.DeclaredServices</c> (which does not) render as the
    /// SAME byte sequence in <c>Golden/vouchfx-sdk-public-api.v1.txt</c> —
    /// <c>property …&lt;System.String,Vouchfx.Sdk.DeclaredServiceInfo&gt; DeclaredServices
    /// { get }</c>, once under each interface's own header. Deleting the
    /// <c>=&gt; NoDeclaredServices</c> therefore leaves the golden byte-identical and this gate
    /// silent — confirmed by doing exactly that and watching
    /// <see cref="VouchfxSdkPublicApi_MatchesGolden_ByteForByte"/> pass unchanged.
    /// </para>
    /// <para>
    /// <strong>Why that silence matters.</strong> <c>DeclaredServices</c> was added to a v1
    /// interface at all ONLY because it carries a default: the default is what keeps every existing
    /// implementor compiling — 82 type declarations implement <c>ICompileContext</c> in this
    /// repository alone (measured), almost all of them test stand-ins, and every out-of-tree
    /// provider is free to carry more — and it is therefore the entire basis on which the addition
    /// is legal under the v1 freeze (see that member's own "Why a DEFAULT implementation" remarks).
    /// Removing it turns an additive change into a source-breaking one for every consumer —
    /// silently, since nothing else in this assembly's contract surface records that the member is
    /// defaulted.
    /// </para>
    /// <para>
    /// Asserted here rather than emitted into the signature on purpose: a change to
    /// <c>SdkPublicApiSignature</c> would move BOTH goldens (this one and the
    /// <c>Vouchfx.Sdk.Testing</c> one) and require the documented regen flags for a gate that adds
    /// no contract information a reader of the golden was missing. A standalone assertion pins the
    /// property and leaves the frozen artefacts untouched.
    /// </para>
    /// <para>
    /// The census in the second half is the same guard pointed the other way: a NEW default
    /// implementation added to a v1 interface is also a contract change the golden cannot see, and
    /// it must be a deliberate, reviewed act rather than a silent one.
    /// </para>
    /// </remarks>
    [Fact]
    public void DefaultImplementedInterfaceMembers_AreExactlyTheKnownSet()
    {
        // NonPublic and Static are in the census flags deliberately (m4 — gatekeeper m3 /
        // security NIT-2, fix round five). The census once read Public | Instance | DeclaredOnly,
        // which left the same blind spot this gate exists to close, one axis over: a
        // `static virtual` / `static abstract` member, or a non-public default implementation,
        // added to a v1 interface would escape BOTH the byte-for-byte golden (whose emitter takes
        // public members only) and this census, and a non-abstract one of either kind is a default
        // implementation by any other name. Widened and the expected set RE-DERIVED by measurement
        // rather than predicted: the widening added no member, so the v1 surface carries no static
        // or non-public interface member at all today — which is itself the fact worth pinning,
        // since the first one added must now be a deliberate, reviewed act.
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var compileContextGetter = typeof(ICompileContext)
            .GetProperty(nameof(ICompileContext.DeclaredServices), flags)?
            .GetMethod;

        Assert.NotNull(compileContextGetter);
        Assert.False(
            compileContextGetter!.IsAbstract,
            "ICompileContext.DeclaredServices has lost its default implementation. That default is "
            + "the only reason adding the member to a FROZEN v1 interface was legal: it keeps every "
            + "existing implementor — 82 in-repo type declarations, almost all test stand-ins, plus "
            + "every out-of-tree provider's own doubles — compiling and behaving exactly as before. "
            + "Removing it is a "
            + "source-breaking change to the v1 provider contract, and the byte-for-byte golden "
            + "cannot see it (the signature emitter never inspects IsAbstract).");

        // The census: which members of the frozen interfaces carry a default implementation at all.
        var defaulted = typeof(IStepProvider).Assembly.GetTypes()
            .Where(t => t.IsInterface && (t.IsPublic || t.IsNestedPublic))
            .SelectMany(
                t => t.GetMethods(flags),
                (t, m) => (Type: t, Method: m))
            .Where(x => !x.Method.IsAbstract)
            .Select(x => $"{x.Type.FullName}.{x.Method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] expected = { "Vouchfx.Sdk.ICompileContext.get_DeclaredServices" };

        Assert.Equal(expected, defaulted);
    }

    /// <summary>
    /// Every CSX helper's <c>Source</c> BODY is pinned by hash. A body edit is a breaking change
    /// to the v1 contract that the signature golden structurally cannot see (#361).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a helper body is contract, not implementation.</strong> A CSX helper's whole
    /// public surface is <c>public const string Source</c>. Two facts make its VALUE binding.
    /// First, a <c>const</c> is INLINED: a provider assembly that wrote <c>SecurityHelper.Source</c>
    /// carries the text as it stood when THAT assembly was compiled, and never reads the shipped
    /// <c>Vouchfx.Sdk</c> again for it. Second, <c>CsxAssembler</c> deduplicates helpers by exact
    /// source text and throws <c>CsxAssemblyException</c> when one helper class arrives with two
    /// different bodies (§13.3.1). Together: edit a body, and any suite mixing an out-of-tree
    /// provider built against the previous SDK with an in-tree provider built against this one
    /// stops assembling. That is precisely the class of breakage the v1 freeze exists to prevent,
    /// so the body belongs inside the frozen surface.
    /// </para>
    /// <para>
    /// <strong>What the signature golden misses, measured rather than argued.</strong>
    /// <c>SdkPublicApiSignature.MemberLines</c> emits a field as
    /// <c>field const System.String Source</c> — the literal's TYPE, never its VALUE. The
    /// committed <c>vouchfx-sdk-public-api.v1.txt</c> shows the consequence directly: all four
    /// helpers render as that identical two-line stanza, distinguished only by their type header.
    /// Measured while building this gate — a 13-character edit to a comment inside
    /// <c>SubstituteHelper.Source</c> left <see cref="VouchfxSdkPublicApi_MatchesGolden_ByteForByte"/>
    /// green and moved no line of the signature golden, while this test failed on it with the
    /// hash and <c>chars</c> both moved. That is the hole, demonstrated rather than argued.
    /// </para>
    /// <para>
    /// <strong>Discovery is by SHAPE, not by name.</strong> The three helpers #361 named
    /// (<c>SecurityHelper</c>, <c>SecretHelper</c>, <c>SubstituteHelper</c>) are not the whole set
    /// — <c>KafkaSecurityHelper</c> is a fourth, already shipped, and a hard-coded list would have
    /// left it unpinned on the day it was written. Any public type in <c>Vouchfx.Sdk</c> exposing a
    /// <c>public const string Source</c> is pinned, so the FIFTH helper is covered the moment it
    /// compiles — <em>provided it follows the convention</em>, which is the whole of what shape
    /// discovery can promise. Discovery keys on the const NAME, so a helper that spells its
    /// constant differently is invisible to it; the known-set assertion beside the vacuity guard
    /// is what turns a departure from the convention red rather than silent. The net is
    /// deliberately wider than "public static class": a helper declared some other way must not
    /// escape the gate on a technicality.
    /// </para>
    /// <para>
    /// <strong>Scope: this gate scans <c>Vouchfx.Sdk</c> only.</strong> <c>Vouchfx.Sdk.Testing</c>
    /// is a second SHIPPED, golden-gated package whose own gate
    /// (<c>SdkTestingContractFreezeTests</c>) records field SIGNATURES for the same reason this one
    /// does, and would be equally blind to a body — so a CSX helper constant added THERE would need
    /// its own pin, which does not exist. Verified rather than assumed: that package's golden
    /// contains no <c>Helper</c> type and no <c>field const</c> line at all, so nothing is unpinned
    /// there today.
    /// </para>
    /// <para>
    /// The value is read with <see cref="FieldInfo.GetRawConstantValue"/> off the reflected
    /// <c>Vouchfx.Sdk</c> assembly rather than by naming <c>SecurityHelper.Source</c> in C#.
    /// Naming it would inline THIS test assembly's own copy at ITS compile time — the very
    /// mechanism the gate exists to guard — so the gate would then compare a constant against
    /// itself. Reflection reads the shipped assembly's metadata, which is what a downstream
    /// consumer's next compilation will read.
    /// </para>
    /// <para>
    /// A separate golden rather than extra lines in the signature golden: the signature is emitted
    /// by the SHARED <c>SdkPublicApiSignature</c>, which also emits the <c>Vouchfx.Sdk.Testing</c>
    /// golden. Teaching it this SDK-specific helper convention would move a second frozen artifact
    /// for a rule that does not apply to it. Same directory, same banner style, same regen flag.
    /// </para>
    /// </remarks>
    [Fact]
    public void CsxHelperSources_MatchGolden_ByteForByte()
    {
        var actual = BuildHelperSourceSignature(typeof(IStepProvider).Assembly);

        if (IsRegenRequested())
        {
            var repoRoot = FindRepoRoot();
            var goldenPath = Path.Combine(
                repoRoot, "tests", "Vouchfx.Sdk.Tests", "Golden", HelperSourcesGoldenFileName);
            File.WriteAllText(goldenPath, actual);
            return;
        }

        var golden = ReadGolden(HelperSourcesGoldenFileName);

        var actualNormalised = Normalise(actual);
        var goldenNormalised = Normalise(golden);

        Assert.True(
            string.Equals(actualNormalised, goldenNormalised, StringComparison.Ordinal),
            "A Vouchfx.Sdk CSX helper's Source BODY has changed. A helper body is part of the "
            + "FROZEN v1 surface: the const inlines into every provider compiled against this SDK, "
            + "and CsxAssembler refuses a suite in which one helper class arrives with two "
            + "different source texts — so an edited body breaks every out-of-tree provider still "
            + "built against the previous SDK, at assembly time, in a suite that mixes them. If "
            + "this change is intentional and that compatibility cost is accepted, regenerate "
            + "Golden/" + HelperSourcesGoldenFileName + " with VOUCHFX_REGEN_SDK_CONTRACT=1 and "
            + "get it reviewed."
            + Environment.NewLine
            + FirstDifference(goldenNormalised, actualNormalised));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>The companion golden pinning CSX helper <c>Source</c> bodies by hash (#361).</summary>
    private const string HelperSourcesGoldenFileName = "vouchfx-sdk-helper-sources.v1.txt";

    /// <summary>
    /// Builds the deterministic text signature of every CSX helper <c>Source</c> constant in
    /// <paramref name="assembly"/>: one <c>&lt;type&gt;.Source sha256=&lt;hex&gt; chars=&lt;n&gt;</c>
    /// line per helper, sorted ordinally by type name.
    /// </summary>
    /// <remarks>
    /// The hash is taken over the constant's UTF-8 bytes with NO newline normalisation, unlike the
    /// file-level comparison in <see cref="Normalise"/>. That asymmetry is deliberate and is the
    /// point: <c>CsxAssembler</c> dedupes on EXACT text, so a body whose line endings changed is a
    /// different body to it and must red this gate — while the golden FILE, an ordinary tracked
    /// text file, must survive a CRLF/LF checkout. <c>chars</c> is diagnostic only; it moves only
    /// when the hash does, and tells a reviewer at a glance whether a body grew or was rewritten.
    /// </remarks>
    private static string BuildHelperSourceSignature(Assembly assembly)
    {
        var helpers = assembly.GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Select(t => (Type: t, Field: t.GetField(
                "Source", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            .Where(x => x.Field is { IsLiteral: true, IsInitOnly: false }
                && x.Field.FieldType == typeof(string))
            .Select(x => (Name: x.Type.FullName ?? x.Type.Name,
                Source: (string)x.Field!.GetRawConstantValue()!))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        // Never let the gate pass vacuously. If a refactor renamed the convention out of
        // existence, the discovery above silently returns nothing — and an empty computed set
        // would then either match an empty golden (regen mode having written one) or produce a
        // diff nobody reads as "the gate stopped looking". Say so instead.
        Assert.True(
            helpers.Count > 0,
            "No CSX helper constants were discovered in Vouchfx.Sdk. This gate pins every public "
            + "type exposing a `public const string Source`; finding none means the helper "
            + "convention moved and the gate is now inert, not that there is nothing to pin.");

        // Pin the MEMBERSHIP, not merely the count. Discovery keys on the const NAME `Source`, so
        // it is blind by construction to a helper that stops using it: rename
        // SecurityHelper.Source to .Body and discovery quietly returns three, which the golden
        // comparison would then report as an unexplained missing line rather than as what it is.
        // This known set is what makes that convention drift red AND names it. A helper is pinned
        // automatically WHEN IT FOLLOWS THE CONVENTION; extending this list is the deliberate act
        // that admits a new one, and departing from the convention is what this assertion catches.
        //
        // Residual, stated rather than implied: a brand-new helper that never adopts the
        // convention at all — say `FooHelper.HelperSource` — is invisible to discovery AND to this
        // set, because neither can see a const neither looks for. Its own introduction review is
        // the only gate on that case, which is exactly why the convention is written down in this
        // file's header instead of being left to be inferred from the code.
        //
        // NOT ENFORCED IN REGEN MODE, and that is the whole point of the exemption. Adding a fifth
        // helper is precisely when a maintainer runs VOUCHFX_REGEN_SDK_CONTRACT=1, and an
        // unconditional check here would throw from inside the builder BEFORE the golden is
        // written — leaving the signature golden rewritten by the sibling test and this one not,
        // i.e. the half-regenerated state this file's header explicitly promises against ("one
        // flag, because the two artifacts pin two halves of one contract"). Nothing is weakened by
        // skipping it: in regen mode the membership change lands as an added or removed line in
        // the golden diff, which `**/Golden/` CODEOWNERS puts in front of a reviewer anyway. The
        // check exists to stop drift arriving UNANNOUNCED in an ordinary run, not to stop a
        // maintainer from deliberately regenerating.
        string[] expectedHelpers =
        {
            "Vouchfx.Sdk.KafkaSecurityHelper",
            "Vouchfx.Sdk.SecretHelper",
            "Vouchfx.Sdk.SecurityHelper",
            "Vouchfx.Sdk.SubstituteHelper",
        };

        if (!IsRegenRequested())
        {
            var found = helpers.Select(h => h.Name).ToArray();

            Assert.True(
                expectedHelpers.SequenceEqual(found, StringComparer.Ordinal),
                "The set of CSX helpers discovered in Vouchfx.Sdk is not the known set. Discovery "
                + "keys on the const NAME `Source`, so a helper that renames or drops that constant "
                + "simply disappears from this list rather than failing loudly on its own."
                + Environment.NewLine
                + "  expected: " + string.Join(", ", expectedHelpers)
                + Environment.NewLine
                + "  found:    " + string.Join(", ", found)
                + Environment.NewLine
                + "If a helper was legitimately added or removed, update expectedHelpers in this "
                + "file and regenerate both goldens with VOUCHFX_REGEN_SDK_CONTRACT=1. If it was "
                + "not, a helper has left the `public const string Source` convention and its body "
                + "is no longer pinned by anything.");
        }

        var sb = new StringBuilder();
        sb.Append("# Vouchfx.Sdk CSX helper Source bodies — FROZEN for the v1.x engine series (#361).\n");
        sb.Append("# A const inlines into consuming assemblies and CsxAssembler dedupes helpers by EXACT text, so a body edit breaks providers built against the previous SDK. Generated by SdkContractFreezeTests; do not hand-edit — regenerate via the freeze test and review, or revert.\n");

        foreach (var (name, source) in helpers)
        {
            sb.Append('\n');
            sb.Append(name);
            sb.Append(".Source sha256=");
            sb.Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant());
            sb.Append(" chars=");
            sb.Append(source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('\n');
        }

        return sb.ToString();
    }

    // Collapse CRLF/CR → LF and drop any trailing final newline(s): the freeze
    // contract compares signature CONTENT, immune to line-ending style and to an
    // editor's insert_final_newline rewrite of the golden file (mirrors
    // SchemaFreezeTests.Normalise).
    private static string Normalise(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

    /// <summary>
    /// Set <c>VOUCHFX_REGEN_SDK_CONTRACT=1</c> to make the gate REWRITE the committed
    /// golden <c>Golden/vouchfx-sdk-public-api.v1.txt</c> from the freshly-reflected
    /// surface instead of asserting against it.  Mirror of
    /// <c>SchemaFreezeTests.RegenEnvVar</c> / <c>VOUCHFX_REGEN_SCHEMA</c>.
    /// </summary>
    private const string RegenEnvVar = "VOUCHFX_REGEN_SDK_CONTRACT";

    private static bool IsRegenRequested()
    {
        var value = Environment.GetEnvironmentVariable(RegenEnvVar);
        return !string.IsNullOrEmpty(value)
            && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Walks up from the test assembly's base directory until it finds the
    /// directory containing <c>vouchfx.sln</c> — the repo root.  Mirrors
    /// <c>SchemaFreezeTests.FindRepoRoot</c>.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vouchfx.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of "
            + $"'{AppContext.BaseDirectory}' contains 'vouchfx.sln').  This gate must locate "
            + "the source-tree golden to rewrite it.");
    }

    /// <summary>
    /// Reads a committed golden artifact from the test assembly's output
    /// directory (shipped as a copied <c>Content</c> item under <c>Golden/</c>).
    /// </summary>
    private static string ReadGolden(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Golden", fileName);

        Assert.True(
            File.Exists(path),
            $"Golden v1 provider-contract artifact not found at '{path}'. The freeze "
            + $"gate requires Golden/{fileName} to be committed and copied to output.");

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Produces a short description of the first differing line between golden and
    /// actual, so a drift failure is diagnosable from the test output alone.
    /// </summary>
    private static string FirstDifference(string golden, string actual)
    {
        var goldenLines = golden.Split('\n');
        var actualLines = actual.Split('\n');
        var max = Math.Max(goldenLines.Length, actualLines.Length);

        for (var i = 0; i < max; i++)
        {
            var g = i < goldenLines.Length ? goldenLines[i] : "<EOF>";
            var a = i < actualLines.Length ? actualLines[i] : "<EOF>";
            if (!string.Equals(g, a, StringComparison.Ordinal))
            {
                return $"First difference at line {i + 1}:"
                    + $"{Environment.NewLine}  golden: {g}"
                    + $"{Environment.NewLine}  actual: {a}";
            }
        }

        return "(no line-level difference detected; check trailing whitespace or length)";
    }
}
