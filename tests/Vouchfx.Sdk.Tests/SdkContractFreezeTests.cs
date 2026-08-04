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
// REGENERATION (when the v1 provider contract legitimately changes — additive only):
//   VOUCHFX_REGEN_SDK_CONTRACT=1 dotnet test tests/Vouchfx.Sdk.Tests \
//     --filter "FullyQualifiedName~SdkContractFreezeTests"
//   This rewrites Golden/vouchfx-sdk-public-api.v1.txt from the freshly-reflected
//   surface.  Review the diff (must be additive only), then commit.  Mirror of
//   SchemaFreezeTests.IsRegenRequested / VOUCHFX_REGEN_SCHEMA.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
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

        var golden = ReadGolden();

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
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

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

    // ── Helpers ────────────────────────────────────────────────────────────────

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
    /// Reads the committed golden artifact from the test assembly's output
    /// directory (shipped as a copied <c>Content</c> item under <c>Golden/</c>).
    /// </summary>
    private static string ReadGolden()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Golden", "vouchfx-sdk-public-api.v1.txt");

        Assert.True(
            File.Exists(path),
            $"Golden v1 provider-contract signature not found at '{path}'. The freeze "
            + "gate requires Golden/vouchfx-sdk-public-api.v1.txt to be committed and "
            + "copied to output.");

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
