// Platform.Engine.Compilation — ForbiddenScriptingApiScanner (§5).
//
// Scans a portable-executable image for any MemberReference whose method name is
// EvaluateAsync or RunAsync AND whose declaring type resolves to one of the Roslyn
// scripting APIs that are forbidden by the memory-model invariant (§5):
//
//   Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript
//   Microsoft.CodeAnalysis.Scripting.Script          (non-generic)
//   Microsoft.CodeAnalysis.Scripting.Script`1        (generic — Script<T>)
//
// The critical subtlety this class solves (vs. the original inline test scan) is
// TypeSpecification parents: when the call site's receiver is a generic instantiation
// such as Script<object>, the MemberReference parent is a HandleKind.TypeSpecification
// blob, not a plain TypeReference.  This class decodes that blob via
// BlobDecoder / BlobReader to recover the underlying generic type-definition's full name,
// and matches it against the forbidden list.
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Platform.Engine.Compilation;

/// <summary>
/// Scans a compiled .NET assembly for calls to Roslyn scripting methods that are
/// forbidden by the vouchfx memory model (§5).
/// </summary>
/// <remarks>
/// <para>
/// The forbidden methods are <c>EvaluateAsync</c> and <c>RunAsync</c> on any of:
/// <list type="bullet">
///   <item><description><c>Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript</c></description></item>
///   <item><description><c>Microsoft.CodeAnalysis.Scripting.Script</c> (non-generic)</description></item>
///   <item><description><c>Microsoft.CodeAnalysis.Scripting.Script`1</c> (generic instantiation)</description></item>
/// </list>
/// </para>
/// <para>
/// The scanner handles both <see cref="HandleKind.TypeReference"/> parents (direct
/// type references) and <see cref="HandleKind.TypeSpecification"/> parents (generic
/// instantiations such as <c>Script&lt;object&gt;</c>), so calls on closed generic
/// types are caught as well.
/// </para>
/// <para>
/// This is exposed as <see langword="internal"/> so the test project (via
/// <c>InternalsVisibleTo</c>) can exercise it with both positive (clean assembly) and
/// negative (synthetic assembly containing the forbidden calls) control tests.
/// </para>
/// </remarks>
internal static class ForbiddenScriptingApiScanner
{
    // The method names that are forbidden regardless of which overload is called.
    private static readonly HashSet<string> ForbiddenMethodNames =
        new(StringComparer.Ordinal) { "EvaluateAsync", "RunAsync" };

    // Full namespaces of the Roslyn scripting types whose members must not be called.
    private const string CSharpScriptingNamespace = "Microsoft.CodeAnalysis.CSharp.Scripting";
    private const string ScriptingNamespace = "Microsoft.CodeAnalysis.Scripting";

    // The simple type names to match within those namespaces.
    private static readonly HashSet<string> ForbiddenTypeNames =
        new(StringComparer.Ordinal) { "CSharpScript", "Script", "Script`1" };

    /// <summary>
    /// Scans <paramref name="assemblyImage"/> for forbidden Roslyn scripting API
    /// references and returns a description of every violation found.
    /// </summary>
    /// <param name="assemblyImage">
    /// The raw portable-executable bytes to inspect.  Must not be
    /// <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// A read-only list of human-readable violation strings, e.g.
    /// <c>"Microsoft.CodeAnalysis.Scripting.Script`1.RunAsync"</c>.
    /// An empty list means the assembly is clean.
    /// </returns>
    internal static IReadOnlyList<string> Find(byte[] assemblyImage)
    {
        ArgumentNullException.ThrowIfNull(assemblyImage);
        if (assemblyImage.Length == 0)
            throw new ArgumentException("Assembly image must not be empty.", nameof(assemblyImage));

        using var ms = new MemoryStream(assemblyImage, writable: false);
        using var pe = new PEReader(ms);
        var reader = pe.GetMetadataReader();

        var violations = new List<string>();

        foreach (var handle in reader.MemberReferences)
        {
            var memberRef = reader.GetMemberReference(handle);
            var memberName = reader.GetString(memberRef.Name);

            if (!ForbiddenMethodNames.Contains(memberName))
                continue;

            // Resolve the declaring type's full name from whichever parent kind
            // Roslyn chose to emit.
            var parentFullName = ResolveParentFullName(reader, memberRef.Parent);
            if (parentFullName is null)
                continue;

            if (IsForbiddenType(parentFullName))
                violations.Add($"{parentFullName}.{memberName}");
        }

        return violations;
    }

    /// <summary>
    /// Overload that reads the assembly from a file path.
    /// Convenience wrapper used by the production-assembly guard test.
    /// </summary>
    internal static IReadOnlyList<string> Find(string assemblyPath)
    {
        ArgumentNullException.ThrowIfNull(assemblyPath);
        return Find(File.ReadAllBytes(assemblyPath));
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="fullName"/> matches one of the
    /// forbidden Roslyn scripting type full names.
    /// </summary>
    private static bool IsForbiddenType(string fullName)
    {
        // e.g. "Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript"
        //      "Microsoft.CodeAnalysis.Scripting.Script"
        //      "Microsoft.CodeAnalysis.Scripting.Script`1"
        foreach (var typeName in ForbiddenTypeNames)
        {
            if (fullName == $"{CSharpScriptingNamespace}.{typeName}" ||
                fullName == $"{ScriptingNamespace}.{typeName}")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the full CLR type name (namespace + "." + name) from a
    /// <see cref="MemberReference"/> parent handle.  Returns <see langword="null"/>
    /// when the parent cannot be decoded (e.g. an unsupported handle kind).
    /// </summary>
    private static string? ResolveParentFullName(MetadataReader reader, EntityHandle parent)
    {
        return parent.Kind switch
        {
            HandleKind.TypeReference => ResolveTypeReference(reader, (TypeReferenceHandle)parent),
            HandleKind.TypeSpecification => ResolveTypeSpecification(reader, (TypeSpecificationHandle)parent),
            _ => null,
        };
    }

    /// <summary>
    /// Reads namespace and name from a plain <see cref="TypeReference"/>.
    /// </summary>
    private static string? ResolveTypeReference(MetadataReader reader, TypeReferenceHandle handle)
    {
        var typeRef = reader.GetTypeReference(handle);
        var ns = reader.GetString(typeRef.Namespace);
        var name = reader.GetString(typeRef.Name);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    /// <summary>
    /// Decodes the type-specification blob to recover the generic type definition's
    /// full name.  A TypeSpecification encodes a generic instantiation as a structured
    /// signature blob; the first element in that blob is
    /// <see cref="SignatureTypeCode.GenericTypeInstance"/> followed by a reference to the
    /// open generic type definition.
    /// </summary>
    private static string? ResolveTypeSpecification(MetadataReader reader, TypeSpecificationHandle handle)
    {
        var typeSpec = reader.GetTypeSpecification(handle);

        // Decode the signature using our minimal provider that only captures the
        // generic type definition's full name.  The provider returns the name string
        // directly so the generic instance decoding path yields it.
        try
        {
            var provider = new GenericTypeDefinitionNameProvider();
            // DecodeSignature drives the blob decoder; for a generic instantiation it
            // calls ISignatureTypeProvider.GetGenericInstantiation, which our provider
            // handles by returning the open generic type definition's full name.
            return typeSpec.DecodeSignature(provider, genericContext: null);
        }
        catch
        {
            // Malformed or unsupported signature; skip rather than crash.
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Minimal ISignatureTypeProvider implementation
    // -------------------------------------------------------------------------

    /// <summary>
    /// A minimal <see cref="ISignatureTypeProvider{TType, TGenericContext}"/>
    /// that decodes only what is needed to extract a generic type definition's full
    /// name from a TypeSpecification blob.  All methods return either the type's full
    /// name string or <see langword="null"/>; callers that do not need the result
    /// receive <c>"&lt;unsupported&gt;"</c>.
    /// </summary>
    /// <remarks>
    /// Only <see cref="GetTypeFromReference"/>, <see cref="GetGenericInstantiation"/>,
    /// and a handful of primitive primitives need to return meaningful values for our
    /// use-case.  All other members return a sentinel that is never matched.
    /// </remarks>
    private sealed class GenericTypeDefinitionNameProvider
        : ISignatureTypeProvider<string?, object?>
    {
        // The only two paths that matter for detecting Script<T>.RunAsync:
        //   1. GetGenericInstantiation is called with the open-generic typedef name.
        //   2. GetTypeFromReference is called to resolve that typedef.

        public string? GetGenericInstantiation(string? genericType, ImmutableArray<string?> typeArguments)
            => genericType; // Return the open generic type definition's full name.

        public string? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => ResolveTypeReference(reader, handle);

        public string? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var def = reader.GetTypeDefinition(handle);
            var ns = reader.GetString(def.Namespace);
            var name = reader.GetString(def.Name);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        // All remaining interface members return a sentinel; they are not exercised
        // when scanning for generic instantiation TypeSpecifications.
        public string? GetArrayType(string? elementType, ArrayShape shape) => null;
        public string? GetByReferenceType(string? elementType) => null;
        public string? GetFunctionPointerType(MethodSignature<string?> signature) => null;
        public string? GetGenericMethodParameter(object? genericContext, int index) => null;
        public string? GetGenericTypeParameter(object? genericContext, int index) => null;
        public string? GetModifiedType(string? modifier, string? unmodifiedType, bool isRequired) => null;
        public string? GetPinnedType(string? elementType) => null;
        public string? GetPointerType(string? elementType) => null;
        public string? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;
        public string? GetSZArrayType(string? elementType) => null;
        public string? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => ResolveTypeSpecification(reader, handle);
    }
}
