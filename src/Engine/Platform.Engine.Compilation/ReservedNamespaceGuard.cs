// Platform.Engine.Compilation — ReservedNamespaceGuard (§5.6).
//
// Detects customer DLLs that squat on the reserved namespaces Platform.Engine.*
// or Platform.Steps.* and refuses them at suite start with a clear diagnostic.
//
// Design note: the guard is purely functional (no instance state, no side effects
// beyond throwing).  Find returns the raw violations for unit-testability;
// ThrowIfSquatting is the suite-start entry point.
using System.Reflection;

namespace Platform.Engine.Compilation;

/// <summary>
/// Carries the details of a single reserved-namespace violation discovered in a
/// customer DLL at suite start (§5.6).
/// </summary>
/// <param name="AssemblyName">
/// The simple name of the offending assembly (e.g. <c>"AcmeIntegrations"</c>).
/// </param>
/// <param name="TypeFullName">
/// The fully-qualified name of the type that squats on the reserved namespace
/// (e.g. <c>"Platform.Engine.Injected.Hack"</c>).
/// </param>
/// <param name="ReservedPrefix">
/// The specific reserved-namespace prefix that was matched
/// (e.g. <c>"Platform.Engine."</c> or <c>"Platform.Steps."</c>).
/// </param>
public sealed record ReservedNamespaceViolation(
    string AssemblyName,
    string TypeFullName,
    string ReservedPrefix);

/// <summary>
/// Thrown by <see cref="ReservedNamespaceGuard.ThrowIfSquatting"/> when one or more
/// customer DLLs declare types inside the engine-reserved namespaces
/// <c>Platform.Engine.*</c> or <c>Platform.Steps.*</c> (§5.6).
/// </summary>
/// <remarks>
/// <para>
/// This exception maps to the <em>Environment-error</em> verdict bucket (§12.1).
/// It is never a <em>Fail</em> — the defect is in the deployment configuration,
/// not in the system under test.
/// </para>
/// <para>
/// Callers that need to present structured output should enumerate
/// <see cref="Violations"/> rather than parsing the exception message.
/// </para>
/// </remarks>
public sealed class ReservedNamespaceSquatException : Exception
{
    /// <summary>
    /// Initialises a new <see cref="ReservedNamespaceSquatException"/> from a
    /// non-empty list of <see cref="ReservedNamespaceViolation"/> records.
    /// </summary>
    /// <param name="violations">
    /// The violations discovered during the suite-start namespace scan.  Must be
    /// non-null and non-empty.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="violations"/> is <see langword="null"/>.
    /// </exception>
    public ReservedNamespaceSquatException(IReadOnlyList<ReservedNamespaceViolation> violations)
        : base(BuildMessage(violations))
    {
        Violations = violations;
    }

    /// <summary>
    /// Gets the full list of violations that caused this exception to be thrown.
    /// Each entry names the offending assembly, the squatting type, and the
    /// reserved-namespace prefix that was matched.
    /// </summary>
    public IReadOnlyList<ReservedNamespaceViolation> Violations { get; }

    private static string BuildMessage(IReadOnlyList<ReservedNamespaceViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);

        var lines = violations.Select(
            v => $"  {v.AssemblyName} → {v.TypeFullName} (reserved prefix: {v.ReservedPrefix})");

        return
            $"Suite start refused: {violations.Count} customer DLL(s) squat on reserved namespaces " +
            $"(Platform.Engine.* / Platform.Steps.*). " +
            $"Rename the offending types or remove the assembly from the provider search path." +
            $"{Environment.NewLine}" +
            string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Guards the assembly graph against reserved-namespace squatting at suite start
/// (§5.6).
/// </summary>
/// <remarks>
/// <para>
/// Reserved namespaces are <c>Platform.Engine.*</c> (engine internals) and
/// <c>Platform.Steps.*</c> (provider contract types).  Customer DLLs that declare
/// types in these namespaces are refused at startup so that they cannot shadow or
/// corrupt the engine's own type identities at runtime.
/// </para>
/// <para>
/// Assemblies whose simple name is included in the <c>trustedAssemblySimpleNames</c>
/// argument to <see cref="Find"/> and <see cref="ThrowIfSquatting"/> are exempt from
/// the scan.  The caller (<see cref="AssemblyClosure"/>) populates that set with the
/// simple names of assemblies that legitimately own the reserved namespaces — i.e.
/// assemblies whose own simple name starts with <c>Platform.Engine.</c> or
/// <c>Platform.Steps.</c>, plus <c>Platform.Sdk</c>.  An assembly that impersonates
/// a <c>Platform.*</c> name is an assembly-name collision, handled by
/// <see cref="AssemblyGraphGuard"/>, not here.
/// </para>
/// <para>
/// <b>When to call:</b> once during suite initialisation via
/// <see cref="AssemblyClosure.GuardAtSuiteStart"/>, which runs this guard before the
/// version-conflict guard.  Never call it per-step or per-script-run.
/// </para>
/// <para>
/// <b>Failure mapping:</b> <see cref="ReservedNamespaceSquatException"/> maps to the
/// <em>Environment-error</em> verdict bucket (§12.1).  Only <em>Fail</em> breaks CI
/// by default; an Environment-error surfaces a separate exit code.
/// </para>
/// </remarks>
public static class ReservedNamespaceGuard
{
    /// <summary>
    /// The namespace prefixes reserved exclusively for engine and provider use.
    /// Customer DLLs may not declare types whose namespace begins with any of these
    /// strings.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes =
        new[] { "Platform.Engine.", "Platform.Steps." };

    /// <summary>
    /// Scans <paramref name="assemblies"/> for types that squat on the
    /// <see cref="ReservedPrefixes"/> and returns one
    /// <see cref="ReservedNamespaceViolation"/> per offending type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Assemblies whose simple name appears in <paramref name="trustedAssemblySimpleNames"/>
    /// are skipped entirely — they are the legitimate engine or provider assemblies that
    /// own those namespaces by design.
    /// </para>
    /// <para>
    /// Dynamic assemblies (those with no on-disk image) are always skipped.
    /// </para>
    /// <para>
    /// If <see cref="Assembly.GetTypes"/> raises a
    /// <see cref="ReflectionTypeLoadException"/> (e.g. because a dependency of the
    /// customer DLL could not be resolved), the successfully loaded types
    /// (<see cref="ReflectionTypeLoadException.Types"/>) are still scanned so that
    /// a partially loadable assembly is not silently ignored.
    /// </para>
    /// </remarks>
    /// <param name="assemblies">
    /// The assemblies to scan.
    /// </param>
    /// <param name="trustedAssemblySimpleNames">
    /// The set of assembly simple names that are exempt from the scan.  Comparison
    /// is ordinal case-insensitive.
    /// </param>
    /// <returns>
    /// A (possibly empty) read-only list of <see cref="ReservedNamespaceViolation"/>
    /// records, sorted by assembly name then type full name for deterministic output.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="assemblies"/> or
    /// <paramref name="trustedAssemblySimpleNames"/> is <see langword="null"/>.
    /// </exception>
    public static IReadOnlyList<ReservedNamespaceViolation> Find(
        IEnumerable<Assembly> assemblies,
        IReadOnlySet<string> trustedAssemblySimpleNames)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(trustedAssemblySimpleNames);

        var violations = new List<ReservedNamespaceViolation>();

        foreach (var assembly in assemblies)
        {
            // Dynamic assemblies have no persistent image and cannot squat on a namespace
            // in a meaningful way — skip them.
            if (assembly.IsDynamic)
                continue;

            var simpleName = assembly.GetName().Name ?? string.Empty;

            // Assemblies whose simple name is in the trusted set are the legitimate
            // engine / provider assemblies that own Platform.Engine.* / Platform.Steps.*.
            if (trustedAssemblySimpleNames.Contains(simpleName))
                continue;

            // Enumerate types, tolerating partial load failures (e.g. missing transitive
            // dependencies in a customer assembly).
            IEnumerable<Type> types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Use whichever types did load; null entries indicate a type whose
                // loader dependency was missing.
                types = ex.Types.Where(t => t is not null)!;
            }

            foreach (var type in types)
            {
                var ns = type.Namespace;
                if (ns is null)
                    continue;

                foreach (var prefix in ReservedPrefixes)
                {
                    if (ns.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        violations.Add(new ReservedNamespaceViolation(
                            simpleName,
                            type.FullName ?? $"{ns}.{type.Name}",
                            prefix));

                        // A single type can only match one prefix — stop checking
                        // further prefixes for this type.
                        break;
                    }
                }
            }
        }

        // Sort for a deterministic, human-readable output order.
        violations.Sort((a, b) =>
        {
            var cmp = string.Compare(a.AssemblyName, b.AssemblyName, StringComparison.OrdinalIgnoreCase);
            return cmp != 0
                ? cmp
                : string.Compare(a.TypeFullName, b.TypeFullName, StringComparison.OrdinalIgnoreCase);
        });

        return violations;
    }

    /// <summary>
    /// Throws <see cref="ReservedNamespaceSquatException"/> if any customer DLL in
    /// <paramref name="assemblies"/> declares types inside the
    /// <see cref="ReservedPrefixes"/>.
    /// </summary>
    /// <param name="assemblies">
    /// The assemblies to scan.
    /// </param>
    /// <param name="trustedAssemblySimpleNames">
    /// The set of assembly simple names that are exempt from the scan.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="assemblies"/> or
    /// <paramref name="trustedAssemblySimpleNames"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ReservedNamespaceSquatException">
    /// Thrown when one or more violations are found.  The exception message lists
    /// every offending assembly and type and states the corrective action.  Maps to
    /// the Environment-error verdict bucket (§12.1).
    /// </exception>
    public static void ThrowIfSquatting(
        IEnumerable<Assembly> assemblies,
        IReadOnlySet<string> trustedAssemblySimpleNames)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(trustedAssemblySimpleNames);

        var violations = Find(assemblies, trustedAssemblySimpleNames);
        if (violations.Count > 0)
            throw new ReservedNamespaceSquatException(violations);
    }
}
