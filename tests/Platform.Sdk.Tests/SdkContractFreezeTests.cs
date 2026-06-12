// S08-F-02 (T3 — Freeze the v1 provider contract).
//
// The Platform.Sdk public surface IS the v1 provider contract.  This
// golden-snapshot test reflects over the whole Platform.Sdk assembly, emits a
// canonical, deterministic text signature of every public type and member, and
// asserts it is byte-for-byte (newline-normalised) identical to the committed
// golden artifact (Golden/platform-sdk-public-api.v1.txt).
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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Platform.Sdk;
using Xunit;

namespace Platform.Sdk.Tests;

/// <summary>
/// S08-F-02: the frozen-v1-provider-contract golden-snapshot gate over the
/// whole <c>Platform.Sdk</c> public surface.
/// </summary>
public sealed class SdkContractFreezeTests
{
    /// <summary>
    /// The reflected public API of <c>Platform.Sdk</c> must be byte-for-byte
    /// (newline-normalised) identical to the committed golden.  If this fails,
    /// the v1 provider contract has drifted.
    /// </summary>
    [Fact]
    public void PlatformSdkPublicApi_MatchesGolden_ByteForByte()
    {
        var actual = SdkPublicApiSignature.Build(typeof(IStepProvider).Assembly);
        var golden = ReadGolden();

        var actualNormalised = Normalise(actual);
        var goldenNormalised = Normalise(golden);

        Assert.True(
            string.Equals(actualNormalised, goldenNormalised, StringComparison.Ordinal),
            "The Platform.Sdk v1 provider contract has DRIFTED. The v1 contract is "
            + "FROZEN for the v1.x engine series — extend via a NEW optional interface, "
            + "never mutate a v1 interface. If this addition is intentional, regenerate "
            + "Golden/platform-sdk-public-api.v1.txt and get it reviewed."
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
            "Platform.Sdk.IStepModel",
            "Platform.Sdk.IStepProvider",
            "Platform.Sdk.IStepBinder`1",
            "Platform.Sdk.IStepValidator`1",
            "Platform.Sdk.IStepCompiler`1",
            "Platform.Sdk.IResourceContributor`1",
        };

        var present = assembly.GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .Select(t => t.FullName ?? t.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in requiredCore)
        {
            Assert.True(
                present.Contains(name),
                $"Frozen v1 core provider interface '{name}' is missing from Platform.Sdk. "
                + "The v1 contract is FROZEN — a core interface may never be renamed or removed.");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    // Collapse CRLF/CR → LF and drop any trailing final newline(s): the freeze
    // contract compares signature CONTENT, immune to line-ending style and to an
    // editor's insert_final_newline rewrite of the golden file (mirrors
    // SchemaFreezeTests.Normalise).
    private static string Normalise(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');

    /// <summary>
    /// Reads the committed golden artifact from the test assembly's output
    /// directory (shipped as a copied <c>Content</c> item under <c>Golden/</c>).
    /// </summary>
    private static string ReadGolden()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Golden", "platform-sdk-public-api.v1.txt");

        Assert.True(
            File.Exists(path),
            $"Golden v1 provider-contract signature not found at '{path}'. The freeze "
            + "gate requires Golden/platform-sdk-public-api.v1.txt to be committed and "
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

/// <summary>
/// Builds the canonical, deterministic text signature of a public assembly
/// surface used by <see cref="SdkContractFreezeTests"/> (and reusable by any
/// other contract-freeze gate).  See the inclusion rule documented at the top of
/// <c>SdkContractFreezeTests.cs</c>.
/// </summary>
internal static class SdkPublicApiSignature
{
    // Record / value-equality synthesised members that carry no contract
    // information and whose formatting is unstable across SDKs.  Excluded by name.
    private static readonly HashSet<string> s_syntheticMemberNames = new(StringComparer.Ordinal)
    {
        "EqualityContract",
        "<Clone>$",
        "PrintMembers",
        "Deconstruct",
        "op_Equality",
        "op_Inequality",
        "GetHashCode",
        "ToString",
    };

    /// <summary>
    /// Reflects over every public type in <paramref name="assembly"/> and returns
    /// a deterministic, newline-joined signature of the whole public surface.
    /// </summary>
    public static string Build(Assembly assembly)
    {
        var types = assembly.GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .OrderBy(t => t.FullName ?? t.Name, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("# Platform.Sdk v1 provider contract — FROZEN for the v1.x engine series.\n");
        sb.Append("# Generated by SdkContractFreezeTests; do not hand-edit. Regenerate + review on intentional change.\n");

        foreach (var type in types)
        {
            AppendType(sb, type);
        }

        return sb.ToString();
    }

    private static void AppendType(StringBuilder sb, Type type)
    {
        sb.Append('\n');
        sb.Append(FormatTypeHeader(type));
        sb.Append('\n');

        if (type.IsEnum)
        {
            foreach (var line in EnumMemberLines(type))
            {
                sb.Append("  ");
                sb.Append(line);
                sb.Append('\n');
            }

            return;
        }

        foreach (var line in MemberLines(type))
        {
            sb.Append("  ");
            sb.Append(line);
            sb.Append('\n');
        }
    }

    // ── Type header ──────────────────────────────────────────────────────────

    private static string FormatTypeHeader(Type type)
    {
        var kind = TypeKind(type);
        var name = FormatType(type);

        // Declared, directly-implemented interfaces + base type, sorted ordinally,
        // so the header captures the type's contract relationships deterministically.
        var supertypes = new List<string>();

        if (type.BaseType is not null
            && type.BaseType != typeof(object)
            && type.BaseType != typeof(ValueType)
            && type.BaseType != typeof(Enum))
        {
            supertypes.Add(FormatType(type.BaseType));
        }

        foreach (var iface in DirectlyImplementedInterfaces(type))
        {
            supertypes.Add(FormatType(iface));
        }

        supertypes.Sort(StringComparer.Ordinal);

        var header = supertypes.Count == 0
            ? $"{kind} {name}"
            : $"{kind} {name} : {string.Join(", ", supertypes)}";

        return header;
    }

    private static string TypeKind(Type type)
    {
        if (type.IsEnum)
        {
            return "enum";
        }

        if (type.IsInterface)
        {
            return "interface";
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return "delegate";
        }

        if (type.IsValueType)
        {
            return IsRecord(type) ? "record struct" : "struct";
        }

        return IsRecord(type) ? "record" : "class";
    }

    // A record is identified structurally by its compiler-synthesised
    // EqualityContract property (classes/structs do not have it).
    private static bool IsRecord(Type type) =>
        type.GetProperty(
            "EqualityContract",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not null;

    // Only the interfaces this type DECLARES directly (not those inherited via a
    // base type or via another interface), so the header is stable and minimal.
    private static IEnumerable<Type> DirectlyImplementedInterfaces(Type type)
    {
        var all = type.GetInterfaces().ToHashSet();
        var inherited = new HashSet<Type>();

        if (type.BaseType is not null)
        {
            foreach (var i in type.BaseType.GetInterfaces())
            {
                inherited.Add(i);
            }
        }

        foreach (var i in all)
        {
            foreach (var nested in i.GetInterfaces())
            {
                inherited.Add(nested);
            }
        }

        return all.Where(i => !inherited.Contains(i) && (i.IsPublic || i.IsNestedPublic));
    }

    // ── Members ──────────────────────────────────────────────────────────────

    private static List<string> MemberLines(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        var lines = new List<string>();

        foreach (var ctor in type.GetConstructors(flags))
        {
            if (IsExcluded(ctor) || IsCopyConstructor(type, ctor))
            {
                continue;
            }

            lines.Add($".ctor({FormatParameters(ctor.GetParameters())})");
        }

        foreach (var prop in type.GetProperties(flags))
        {
            if (IsExcluded(prop))
            {
                continue;
            }

            var accessors = new List<string>();
            if (prop.GetMethod is { IsPublic: true })
            {
                accessors.Add("get");
            }

            if (prop.SetMethod is { IsPublic: true })
            {
                accessors.Add(IsInitOnly(prop.SetMethod) ? "init" : "set");
            }

            var indexerParams = prop.GetIndexParameters();
            var nameAndIndex = indexerParams.Length == 0
                ? prop.Name
                : $"{prop.Name}[{FormatParameters(indexerParams)}]";

            lines.Add(
                $"property {FormatType(prop.PropertyType)} {nameAndIndex} {{ {string.Join("; ", accessors)} }}");
        }

        foreach (var field in type.GetFields(flags))
        {
            if (IsExcluded(field))
            {
                continue;
            }

            var modifier = field.IsLiteral ? "const " : field.IsStatic ? "static " : string.Empty;
            lines.Add($"field {modifier}{FormatType(field.FieldType)} {field.Name}");
        }

        foreach (var method in type.GetMethods(flags))
        {
            if (method.IsSpecialName || IsExcluded(method) || s_syntheticMemberNames.Contains(method.Name))
            {
                continue;
            }

            var staticMod = method.IsStatic ? "static " : string.Empty;
            var generics = method.IsGenericMethodDefinition
                ? $"<{string.Join(",", method.GetGenericArguments().Select(a => a.Name))}>"
                : string.Empty;

            lines.Add(
                $"method {staticMod}{FormatType(method.ReturnType)} {method.Name}{generics}"
                + $"({FormatParameters(method.GetParameters())})");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static List<string> EnumMemberLines(Type type)
    {
        var names = Enum.GetNames(type).ToList();
        names.Sort(StringComparer.Ordinal);
        return names.Select(n => $"enum-member {n}").ToList();
    }

    // ── Exclusion of compiler-generated / synthetic surface ──────────────────

    private static bool IsExcluded(MemberInfo member)
    {
        if (s_syntheticMemberNames.Contains(member.Name))
        {
            return true;
        }

        return member.GetCustomAttribute<CompilerGeneratedAttribute>() is not null
            && member is not PropertyInfo; // positional record properties carry [CompilerGenerated] but ARE the contract
    }

    // The record copy-constructor — a single parameter of the declaring type.
    private static bool IsCopyConstructor(Type type, ConstructorInfo ctor)
    {
        var ps = ctor.GetParameters();
        return ps.Length == 1 && ps[0].ParameterType == type;
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    // ── Deterministic type-name formatter ────────────────────────────────────

    private static string FormatParameters(ParameterInfo[] parameters) =>
        string.Join(
            ", ",
            parameters.Select(p =>
            {
                var prefix = p.ParameterType.IsByRef
                    ? (p.IsOut ? "out " : p.IsIn ? "in " : "ref ")
                    : string.Empty;
                return $"{prefix}{FormatType(p.ParameterType)} {p.Name}";
            }));

    private static string FormatType(Type type)
    {
        if (type.IsByRef)
        {
            return FormatType(type.GetElementType()!);
        }

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            var commas = rank > 1 ? new string(',', rank - 1) : string.Empty;
            return $"{FormatType(type.GetElementType()!)}[{commas}]";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var baseName = StripArity(def.FullName ?? def.Name);
            var args = type.GetGenericArguments().Select(FormatType);
            return $"{baseName}<{string.Join(",", args)}>";
        }

        return type.FullName ?? type.Name;
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }
}
