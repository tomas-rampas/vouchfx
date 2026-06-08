// Platform.Sdk — reflective provider registry (§13).
// Performs a one-time scan of supplied assemblies or provider instances at
// startup and freezes the result into an immutable map.  Post-freeze mutation
// is structurally impossible: no public Add/Remove/Clear/Set* methods exist.
using System.Collections.ObjectModel;
using System.Reflection;

namespace Platform.Sdk;

/// <summary>
/// Raised when two providers resolve to the same <c>&lt;family&gt;.&lt;provider&gt;</c>
/// key during registry construction.
/// </summary>
/// <remarks>
/// Duplicate keys are always a configuration error — a provider binary must be
/// listed exactly once and no two assemblies may claim the same step kind.
/// The engine refuses to start when this exception is thrown.
/// </remarks>
/// <seealso cref="StepKindRegistry"/>
public sealed class DuplicateStepKindException : Exception
{
    /// <summary>
    /// Initialises a new instance with a message that names the colliding key
    /// and both conflicting provider type names.
    /// </summary>
    /// <param name="key">The duplicated <c>&lt;family&gt;.&lt;provider&gt;</c> key.</param>
    /// <param name="existingTypeName">
    /// The fully qualified name of the provider type already registered under
    /// <paramref name="key"/>.
    /// </param>
    /// <param name="conflictingTypeName">
    /// The fully qualified name of the second provider type that attempted to
    /// claim the same <paramref name="key"/>.
    /// </param>
    public DuplicateStepKindException(
        string key,
        string existingTypeName,
        string conflictingTypeName)
        : base(
            $"Duplicate step kind '{key}': already registered by '{existingTypeName}', " +
            $"conflicting registration from '{conflictingTypeName}'. " +
            "Each step kind may only be registered once.")
    {
    }
}

/// <summary>
/// An immutable snapshot of a single provider discovered by the
/// <see cref="StepKindRegistry"/>.
/// </summary>
/// <param name="Kind">
/// The unique identifier for this step kind (e.g. <c>Family="noop"</c>,
/// <c>Provider="echo"</c>).
/// </param>
/// <param name="Metadata">
/// Descriptive metadata including version, minimum engine version, licence,
/// and authors.
/// </param>
/// <param name="Instance">
/// The live provider instance created by the registry during discovery.
/// </param>
/// <param name="SchemaFragment">
/// The JSON Schema fragment contributed by the provider's
/// <see cref="IStepBinder{TModel}"/> implementation, or <see langword="null"/>
/// when the provider does not implement <see cref="IStepBinder{TModel}"/>.
/// </param>
public sealed record RegisteredProvider(
    StepKindId Kind,
    ProviderMetadata Metadata,
    IStepProvider Instance,
    JsonSchemaFragment? SchemaFragment);

/// <summary>
/// Reflective registry of all known step providers, frozen at startup.
/// </summary>
/// <remarks>
/// <para>
/// The registry performs a one-time reflective scan of the supplied assemblies
/// or provider instances at application startup and freezes the discovered
/// providers into an immutable map.  Two factory overloads are provided:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="BuildAndFreeze(IEnumerable{Assembly})"/> — the real
///       discovery path.  Scans each assembly for concrete classes decorated
///       with <see cref="StepProviderAttribute"/> and for implementations of
///       <see cref="IProviderModule"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="BuildAndFreeze(IReadOnlyCollection{IStepProvider})"/> — the
///       core indexing path.  Accepts an explicit provider list and is also
///       used directly from tests when provider instances are constructed
///       by hand.
///     </description>
///   </item>
/// </list>
/// <para>
/// Post-freeze mutation is structurally impossible: no public Add, Remove,
/// Clear, or Set* methods exist.  <see cref="All"/> is typed as
/// <see cref="IReadOnlyCollection{T}"/> over a snapshot taken from the
/// underlying dictionary, so callers cannot mutate the registry through the
/// collection reference either.
/// </para>
/// <para>
/// Providers are compile-time, source-level plugins — no runtime dynamic
/// loading or sandboxing (§13).
/// </para>
/// </remarks>
public sealed class StepKindRegistry
{
    // Closed generic type definition used when reflecting IStepBinder<TModel>.
    private static readonly Type s_stepBinderOpenType = typeof(IStepBinder<>);

    // The single underlying store.  Keyed by "<family>.<provider>".
    // Built once in the constructor; never exposed mutably.
    private readonly IReadOnlyDictionary<string, RegisteredProvider> _providers;

    // Private constructor — all creation goes through the factory methods.
    private StepKindRegistry(IReadOnlyDictionary<string, RegisteredProvider> providers)
    {
        _providers = providers;

        // Snapshot the values into a read-only collection once so that All
        // never allows a caller to obtain a mutable reference.
        All = new ReadOnlyCollection<RegisteredProvider>(
            providers.Values.ToList());
    }

    // ── Public surface ────────────────────────────────────────────────────────

    /// <summary>
    /// Gets an immutable snapshot of every provider registered in this registry.
    /// </summary>
    public IReadOnlyCollection<RegisteredProvider> All { get; }

    /// <summary>
    /// Attempts to retrieve the provider registered under the given
    /// <paramref name="typeKey"/>.
    /// </summary>
    /// <param name="typeKey">
    /// The composite step-kind key in the form <c>&lt;family&gt;.&lt;provider&gt;</c>,
    /// e.g. <c>"noop.echo"</c>.  The lookup is case-sensitive.
    /// </param>
    /// <param name="provider">
    /// When this method returns <see langword="true"/>, contains the
    /// <see cref="RegisteredProvider"/> for the requested key; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a provider is registered under
    /// <paramref name="typeKey"/>; <see langword="false"/> otherwise.
    /// </returns>
    public bool TryGet(string typeKey, out RegisteredProvider? provider)
    {
        return _providers.TryGetValue(typeKey, out provider);
    }

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Scans each of the supplied <paramref name="assemblies"/> for concrete
    /// classes decorated with <see cref="StepProviderAttribute"/> and for
    /// implementations of <see cref="IProviderModule"/>, then freezes the
    /// result into an immutable registry.
    /// </summary>
    /// <param name="assemblies">
    /// The set of assemblies to scan.  Passing the same assembly more than
    /// once is safe — types discovered by the <see cref="StepProviderAttribute"/>
    /// path are de-duplicated by CLR <see cref="Type"/> identity before
    /// instantiation, so the same provider type is only instantiated once.
    /// </param>
    /// <returns>
    /// A frozen <see cref="StepKindRegistry"/> containing every discovered
    /// provider.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a <see cref="StepProviderAttribute"/>-decorated type has
    /// no public parameterless constructor.
    /// </exception>
    /// <exception cref="DuplicateStepKindException">
    /// Thrown when two providers resolve to the same
    /// <c>&lt;family&gt;.&lt;provider&gt;</c> key.
    /// </exception>
    public static StepKindRegistry BuildAndFreeze(IEnumerable<Assembly> assemblies)
    {
        var collected = new List<IStepProvider>();

        // Track types already processed via [StepProvider] to guard against
        // the same assembly being listed more than once.
        var seenAttributeTypes = new HashSet<Type>();

        foreach (var assembly in assemblies)
        {
            // Tolerate partial load failures: a provider assembly that has an
            // unresolvable transitive dependency raises ReflectionTypeLoadException
            // from GetTypes().  Use whichever types did load so the scan degrades
            // gracefully rather than crashing startup with an opaque exception.
            IEnumerable<Type> assemblyTypes;
            try
            {
                assemblyTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                assemblyTypes = ex.Types.OfType<Type>();
            }

            foreach (var type in assemblyTypes)
            {
                if (!type.IsClass || type.IsAbstract)
                    continue;

                // ── [StepProvider]-decorated types ────────────────────────────
                if (Attribute.IsDefined(type, typeof(StepProviderAttribute), inherit: false)
                    && typeof(IStepProvider).IsAssignableFrom(type))
                {
                    if (seenAttributeTypes.Add(type))
                    {
                        collected.Add(InstantiateProvider(type));
                    }

                    continue;
                }

                // ── IProviderModule implementations ───────────────────────────
                // Only instantiate modules that expose a public parameterless
                // constructor.  Modules without one are not intended for
                // reflective discovery (they exist for other purposes, such as
                // unit-test fixtures that accept constructor arguments).
                if (typeof(IProviderModule).IsAssignableFrom(type)
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    var module = InstantiateModule(type);
                    foreach (var provider in module.Providers())
                    {
                        collected.Add(provider);
                    }
                }
            }
        }

        return BuildAndFreeze((IReadOnlyCollection<IStepProvider>)collected);
    }

    /// <summary>
    /// Indexes the supplied <paramref name="providers"/> by their
    /// <c>&lt;family&gt;.&lt;provider&gt;</c> key and freezes the result
    /// into an immutable registry.
    /// </summary>
    /// <param name="providers">
    /// The provider instances to register.  Each instance must have a unique
    /// key; duplicates cause a <see cref="DuplicateStepKindException"/>.
    /// </param>
    /// <returns>
    /// A frozen <see cref="StepKindRegistry"/> containing every supplied
    /// provider.
    /// </returns>
    /// <exception cref="DuplicateStepKindException">
    /// Thrown when two providers resolve to the same
    /// <c>&lt;family&gt;.&lt;provider&gt;</c> key.
    /// </exception>
    public static StepKindRegistry BuildAndFreeze(
        IReadOnlyCollection<IStepProvider> providers)
    {
        var map = new Dictionary<string, RegisteredProvider>(
            StringComparer.Ordinal);

        foreach (var provider in providers)
        {
            var key = $"{provider.Kind.Family}.{provider.Kind.Provider}";

            if (map.TryGetValue(key, out var existing))
            {
                throw new DuplicateStepKindException(
                    key,
                    existing.Instance.GetType().FullName ?? existing.Instance.GetType().Name,
                    provider.GetType().FullName ?? provider.GetType().Name);
            }

            var schema = ReflectSchemaFragment(provider);
            map[key] = new RegisteredProvider(
                Kind: provider.Kind,
                Metadata: provider.Metadata,
                Instance: provider,
                SchemaFragment: schema);
        }

        return new StepKindRegistry(map);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Instantiates a <see cref="StepProviderAttribute"/>-decorated type via
    /// its public parameterless constructor.  Wraps any activation failure in
    /// a clear <see cref="InvalidOperationException"/> that names the
    /// offending type.
    /// </summary>
    private static IStepProvider InstantiateProvider(Type type)
    {
        try
        {
            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException(
                    $"Activator.CreateInstance returned null for provider type '{type.FullName}'.");

            return (IStepProvider)instance;
        }
        catch (MissingMemberException ex)
        {
            throw new InvalidOperationException(
                $"Provider type '{type.FullName}' decorated with [StepProvider] " +
                "must have a public parameterless constructor.", ex);
        }
        catch (InvalidCastException ex)
        {
            throw new InvalidOperationException(
                $"Type '{type.FullName}' is decorated with [StepProvider] but " +
                "does not implement IStepProvider.", ex);
        }
    }

    /// <summary>
    /// Instantiates an <see cref="IProviderModule"/> implementation via its
    /// public parameterless constructor.
    /// </summary>
    private static IProviderModule InstantiateModule(Type type)
    {
        try
        {
            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException(
                    $"Activator.CreateInstance returned null for module type '{type.FullName}'.");

            return (IProviderModule)instance;
        }
        catch (MissingMemberException ex)
        {
            throw new InvalidOperationException(
                $"IProviderModule type '{type.FullName}' must have a public " +
                "parameterless constructor.", ex);
        }
    }

    /// <summary>
    /// Searches the provider's concrete type for a closed
    /// <see cref="IStepBinder{TModel}"/> interface implementation and, when
    /// found, reads the <see cref="IStepBinder{TModel}.SchemaFragment"/>
    /// property value via reflection.
    /// </summary>
    /// <param name="provider">The provider instance to inspect.</param>
    /// <returns>
    /// The <see cref="JsonSchemaFragment"/> from the provider's
    /// <see cref="IStepBinder{TModel}"/> implementation, or
    /// <see langword="null"/> when the provider does not implement the
    /// interface.
    /// </returns>
    private static JsonSchemaFragment? ReflectSchemaFragment(IStepProvider provider)
    {
        var providerType = provider.GetType();

        // Search implemented interfaces for a closed IStepBinder<TModel>.
        var binderInterface = providerType
            .GetInterfaces()
            .FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == s_stepBinderOpenType);

        if (binderInterface is null)
            return null;

        // Obtain the SchemaFragment PropertyInfo from the closed interface type
        // (not from the concrete class, to avoid ambiguity).
        var prop = binderInterface.GetProperty(
            nameof(IStepBinder<IStepModel>.SchemaFragment),
            BindingFlags.Public | BindingFlags.Instance);

        if (prop is null)
            return null;

        return prop.GetValue(provider) as JsonSchemaFragment;
    }
}
