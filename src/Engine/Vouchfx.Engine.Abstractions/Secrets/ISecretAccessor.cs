// Vouchfx.Engine.Abstractions — ISecretAccessor + SecretAccessor + NullSecretAccessor
// (S05-B-02, §17).
//
// ISecretAccessor is THE type the emitted CSX names at runtime: the script reaches
// the secrets subsystem only through ScriptGlobalVariables.Secrets (an instance),
// and that property's type is ISecretAccessor. RoslynScriptCompiler references
// Vouchfx.Engine.Abstractions, so the emitted script can name ISecretAccessor and
// SecretString directly — which is why these types live here, not in the Sdk.
//
// The accessor takes a reference token (raw "${secret:env/API}" or bare "env/API"),
// parses it via SecretReference, routes to the catalog's resolver, and returns a
// redacted SecretString. Resolution happens ONLY here, at execution time (§17).

using System;
using System.Collections.Generic;

namespace Vouchfx.Engine.Abstractions.Secrets;

/// <summary>
/// The execution-time entry point the emitted script uses to resolve a secret
/// reference into a redacted <see cref="SecretString"/> (§17).
/// </summary>
/// <remarks>
/// Reached from the script only via the instance property
/// <c>ScriptGlobalVariables.Secrets</c>; never via a static (which would root the
/// collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, §5).
/// </remarks>
public interface ISecretAccessor
{
    /// <summary>
    /// Resolves <paramref name="reference"/> into a redacted <see cref="SecretString"/>.
    /// </summary>
    /// <param name="reference">
    /// Either a raw token <c>${secret:source/path}</c> or a bare <c>source/path</c>.
    /// </param>
    /// <returns>The resolved value, redacted at the source.</returns>
    /// <exception cref="SecretResolutionException">
    /// Thrown when the reference is malformed, names an unknown source, or the value
    /// is absent at the source.
    /// </exception>
    SecretString Resolve(string reference);
}

/// <summary>
/// The default <see cref="ISecretAccessor"/>: parses a reference and routes it to
/// the matching resolver in an <see cref="ISecretSourceCatalog"/> (§17).
/// </summary>
/// <remarks>
/// A per-run instance built in the Default ALC and passed into the boundary by
/// reference. No static state (§5).
/// </remarks>
public sealed class SecretAccessor : ISecretAccessor
{
    private readonly ISecretSourceCatalog _catalog;

    /// <summary>
    /// The Default-ALC record of every value this accessor has revealed (§17, S11-B-01).
    /// Populated on each successful <see cref="Resolve"/> and consumed by the runner as a
    /// DEFENCE-IN-DEPTH scrub net for free-form provider observation text before it enters the
    /// event stream.  Type-based <see cref="SecretString"/> redaction remains the primary
    /// mechanism; this is a backstop for the one surface the engine cannot type-check (a
    /// provider-built observation / exception message).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives on the accessor (Default ALC), which the runner holds: recording from inside
    /// the collectible script's <c>accessor.Resolve(...)</c> call runs this Default-ALC
    /// method body and adds a plain <see cref="string"/> to a Default-ALC set — no static,
    /// no handle bridging the boundary, so nothing roots the collectible
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> (§5).
    /// </para>
    /// <para>
    /// <strong>The ledger's lifetime is the CALLER's choice, not this type's</strong>
    /// (client-key-password REQ-010).  Built through <see cref="SecretAccessor(ISecretSourceCatalog)"/>
    /// it is private to this accessor, which is the per-scenario shape the scrub net was
    /// introduced with.  Built through
    /// <see cref="SecretAccessor(ISecretSourceCatalog, ResolvedSecretLedger)"/> the caller
    /// supplies one it also hands to other accessors, so a value resolved through any of them
    /// is scrubbable from text emitted on any other's path.  The engine uses the second shape
    /// to give the topology probe and the per-scenario steps ONE ledger for a whole run, because
    /// those two have genuinely different lifetimes (one probe scope, N scenario scopes) and no
    /// single accessor can span both.
    /// </para>
    /// <para>
    /// <strong>KNOWN CONSEQUENCE — this property is a CONFIRMATION ORACLE for author script
    /// code, and run-scoping widens it.</strong>  <c>SecretAccessor</c> is public in a package
    /// that is in the emitted CSX script's reference closure, so a <c>script.csharp</c> body can
    /// write <c>((SecretAccessor)Vars.Secrets).ResolvedSecrets.Scrub(candidate)</c> and learn
    /// from whether the result changed whether <c>candidate</c> is a resolved secret value;
    /// <see cref="ResolvedSecretLedger.Count"/> likewise reveals how many distinct values a run
    /// has revealed.  It is CONFIRM-ONLY, not extraction: <see cref="ResolvedSecretLedger.Scrub"/>
    /// replaces exact occurrences and never returns a recorded value, so an attacker must already
    /// hold the candidate.  The oracle itself predates the shared ledger, but was previously
    /// scenario-local; sharing one ledger across a run makes it answer for values resolved in
    /// OTHER scenarios and — on the shared-topology suite path — for the topology probe's
    /// <c>clientKeyPassword</c>, which no step resolves and the script has no other route to.
    /// The accessibility is deliberately left unchanged here: this type ships in a released
    /// package, so narrowing it is a breaking change needing its own decision.
    /// </para>
    /// </remarks>
    public ResolvedSecretLedger ResolvedSecrets { get; }

    /// <summary>
    /// Initialises a new accessor over <paramref name="catalog"/>, with a fresh
    /// <see cref="ResolvedSecrets"/> ledger private to this accessor.
    /// </summary>
    /// <param name="catalog">The run's source catalog; must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="catalog"/> is <see langword="null"/>.
    /// </exception>
    public SecretAccessor(ISecretSourceCatalog catalog)
        : this(catalog, new ResolvedSecretLedger())
    {
    }

    /// <summary>
    /// Initialises a new accessor over <paramref name="catalog"/> that records every value it
    /// reveals into <paramref name="ledger"/> — a ledger the caller may share with other
    /// accessors so one scrub net covers them all (client-key-password REQ-010).
    /// </summary>
    /// <param name="catalog">The run's source catalog; must not be <see langword="null"/>.</param>
    /// <param name="ledger">
    /// The ledger to record into; must not be <see langword="null"/>.  Sharing one instance
    /// across accessors is the supported way to make a value resolved on one code path
    /// scrubbable from text emitted on another — the ledger is internally locked, so concurrent
    /// resolves through different accessors are safe.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="catalog"/> or <paramref name="ledger"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// Purely additive and binary-compatible: the pre-existing single-argument constructor
    /// delegates here with a fresh ledger, so every already-compiled caller keeps its exact
    /// previous behaviour and no existing member changed shape.  (<c>Vouchfx.Engine.Abstractions</c>
    /// ships as a NuGet package but carries no public-API freeze gate — that gap is tracked as
    /// issue #393 and is not addressed here.)
    /// </remarks>
    public SecretAccessor(ISecretSourceCatalog catalog, ResolvedSecretLedger ledger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ResolvedSecrets = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    /// <inheritdoc />
    public SecretString Resolve(string reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            throw new SecretResolutionException(
                string.Empty,
                string.Empty,
                "an empty string was passed where a secret reference " +
                "'${secret:source/path}' was expected.");
        }

        // Accept both the raw token "${secret:env/API}" and the bare "env/API".
        // TryParse handles a complete, standalone token; a bare "source/path" is
        // normalised into a token before parsing so the single grammar (S05-B-01)
        // is the only place the syntax is defined.
        var token = reference.StartsWith(SecretReference.Sigil, StringComparison.Ordinal)
            ? reference
            : SecretReference.Sigil + reference + "}";

        if (!SecretReference.TryParse(token, out var parsed) || parsed is null)
        {
            throw new SecretResolutionException(
                string.Empty,
                string.Empty,
                $"the secret reference '{reference}' is malformed; the expected form is " +
                "'${secret:source/path}' (for example '${secret:env/API_TOKEN}').");
        }

        if (!_catalog.TryGet(parsed.Source, out var resolver) || resolver is null)
        {
            throw new SecretResolutionException(
                parsed.Source,
                parsed.Path,
                $"the secret reference '{parsed.Raw}' names an unknown source " +
                $"'{parsed.Source}'; configured sources are: {FormatSources(_catalog.Sources)}.");
        }

        var resolved = resolver.Resolve(parsed.Path);

        // Defence-in-depth (§17, S11-B-01): record the revealed value so the runner can
        // scrub it from any free-form provider observation text before that text enters the
        // event stream.  Reveal() here is the SAME single, greppable escape hatch the
        // injection sinks use; the value is read once, handed to the Default-ALC ledger, and
        // not otherwise retained by this method.  This is a BACKSTOP, not the primary
        // redaction path — every structured surface is already redacted by the SecretString
        // carrier itself.
        ResolvedSecrets.Record(resolved.Reveal());

        return resolved;
    }

    private static string FormatSources(IReadOnlyCollection<string> sources)
        => sources.Count == 0 ? "(none configured)" : string.Join(", ", sources);
}

/// <summary>
/// An <see cref="ISecretAccessor"/> for runs with no secret sources configured: any
/// attempt to resolve a secret throws <see cref="SecretResolutionException"/>.
/// </summary>
/// <remarks>
/// Backs the legacy <c>ScriptGlobalVariables</c> constructors so existing call sites
/// and tests keep compiling and behave safely — a scenario that references a secret
/// without a configured source fails loudly rather than silently exposing nothing.
/// </remarks>
public sealed class NullSecretAccessor : ISecretAccessor
{
    /// <summary>
    /// A shared, stateless instance for the legacy <c>ScriptGlobalVariables</c>
    /// constructors to reuse instead of allocating a new accessor per call.
    /// </summary>
    /// <remarks>
    /// This static lives on <see cref="NullSecretAccessor"/> (a plain abstraction
    /// type), NOT on the host↔script boundary type <c>ScriptGlobalVariables</c>: the
    /// boundary must declare no static state, or it would root the collectible
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> and defeat the memory
    /// model (§5). <see cref="NullSecretAccessor"/> is itself stateless, so a shared
    /// instance is safe.
    /// </remarks>
    public static readonly NullSecretAccessor Instance = new();

    /// <inheritdoc />
    /// <exception cref="SecretResolutionException">Always.</exception>
    public SecretString Resolve(string reference)
        => throw new SecretResolutionException(
            string.Empty,
            string.Empty,
            "no secret sources are configured for this run; a step referenced a " +
            "'${secret:source/path}' value but the engine was started without a " +
            "secret source catalogue.");
}
