// Vouchfx.Engine.Runtime — SecurityConfigurationAccessor (authenticated-infrastructure-mtls,
// slice D — REQ-014).
//
// The production ISecurityConfigurationAccessor: projects the declared `security` blocks of
// one scenario's environment into the per-target client configuration an emitted step block
// reads through ScriptGlobalVariables.Security.
//
// Where this sits in the pipeline, and what it may therefore assume. Every path-valued
// security field has ALREADY been resolved against the suite directory, checked for
// containment (REQ-003) and checked for existence (REQ-004) by EnvironmentSecurityValidator,
// which runs in ProviderPipeline.Compile — pre-topology, before this class is constructed on
// any production path. This class re-resolves the same paths (it needs the absolute form) and
// re-checks containment as DEFENCE IN DEPTH, sharing EnvironmentSecurityValidator's own
// IsContainedWithin predicate rather than restating the rule (two spellings of one security
// rule is how the two drift). Understand precisely what that re-check does and does not buy:
// it is measured NOT to catch a base-directory divergence — a path resolved against the wrong
// base is still contained within THAT base — so it is a backstop against a future caller
// handing this class an unvalidated AST, never a substitute for handing it the same base the
// validator used. What this class DOES own is everything the validator cannot see, because it
// happens after: reading the declared files as actual certificate material, and reporting a
// file that exists but is not loadable as a SecurityMaterialException rather than as an opaque
// handshake failure three layers down.
//
// Diagnostics name the DECLARED (relative) path, never the resolved absolute one. A provider's
// general catch writes an exception message into Vars[outcomeKey], which reaches the §14 event
// stream and every renderer, and ScenarioRunner.ScrubDiagnostic is ResolvedSecrets.Scrub — a
// targeted net over values the run's SecretAccessor actually revealed — so a filesystem path
// there is never redacted and cannot be. The declared form is what the author wrote, is what
// they need in order to fix the problem, and discloses nothing about the host.
//
// Certificate loading is LAZY and CACHED per target. Lazy because a suite may declare security
// on a target no step in this scenario touches, and reading a private key has a real cost;
// cached because the SAME instance must be handed to every step that resolves the target —
// see ISecurityCertificateMaterial's own remarks on borrowing.
using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Vouchfx.Engine.Abstractions.Secrets;
using Vouchfx.Engine.Abstractions.Security;
using Vouchfx.Engine.Authoring.Ast;
using Vouchfx.Engine.Authoring.Model;

namespace Vouchfx.Engine.Runtime;

/// <summary>
/// Resolves the client security configuration declared on a scenario's
/// <c>environment.services</c> / <c>environment.dependencies</c> entries (REQ-014), owning the
/// certificate objects it loads for the lifetime of the scenario.
/// </summary>
internal sealed class SecurityConfigurationAccessor : ISecurityConfigurationAccessor, IDisposable
{
    private readonly IReadOnlyDictionary<string, SecurityConfiguration> _byTarget;
    private readonly IReadOnlySet<string> _ambiguousTargets;

    private SecurityConfigurationAccessor(
        IReadOnlyDictionary<string, SecurityConfiguration> byTarget, IReadOnlySet<string> ambiguousTargets)
    {
        _byTarget = byTarget;
        _ambiguousTargets = ambiguousTargets;
    }

    /// <summary>
    /// Builds the accessor for <paramref name="ast"/>'s declared <c>security</c> blocks, with
    /// every path-valued field resolved against <paramref name="suiteDirectory"/>.
    /// </summary>
    /// <param name="ast">The normalised scenario AST.</param>
    /// <param name="suiteDirectory">
    /// The directory containing THIS scenario's own <c>.e2e.yaml</c> file. It must be the same
    /// value the caller handed <c>ProviderPipeline.Compile</c> — and therefore the same one
    /// <c>EnvironmentSecurityValidator</c> checked containment and existence against — or the
    /// two stages resolve one declared path to two different files, and the trust decision is
    /// taken against an anchor the suite never named. On the multi-suite path that value is
    /// the per-scenario <c>scriptBaseDirectory</c>, NOT the suite-wide seed root.
    /// <see langword="null"/> falls back to the current directory, mirroring
    /// <c>ProviderPipeline.Compile</c>'s own default.
    /// </param>
    /// <param name="secrets">
    /// The run's secret accessor, used ONLY to resolve a declared <c>clientKeyPassword</c>
    /// reference — lazily, at first use of the certificate material, never here
    /// (client-key-password spec, REQ-009; §17). <see langword="null"/> is legal and keeps
    /// today's behaviour for every suite that declares no passphrase; a suite that DOES declare
    /// one then fails closed at load time rather than loading the key as though it were
    /// unencrypted (H2).
    /// <para>
    /// Deliberately a REQUIRED parameter with no default, however uninteresting a
    /// <see langword="null"/> argument is at most call sites. This branch already paid for the
    /// opposite choice once: <c>SuiteTopology.StartAsync</c>'s optional
    /// <c>securityConfiguration</c> was silently omitted by <c>WatchRunner</c>, which compiled
    /// and read correctly while making every secured suite unrunnable under <c>--watch</c> (see
    /// that call site's own remarks). A required parameter forces each caller to state which
    /// accessor this material resolves through.
    /// </para>
    /// </param>
    /// <returns>
    /// A disposable accessor, or <see cref="NullSecurityConfigurationAccessor.Instance"/> when
    /// the scenario declares no <c>security</c> block at all — the common path, which then
    /// allocates nothing and has nothing to dispose.
    /// </returns>
    /// <remarks>
    /// Never throws for an ambiguous target. An ambiguity is recorded and raised from
    /// <see cref="For(string)"/> instead, so it surfaces as a step-scoped environment error on
    /// the one step that actually names it, rather than as an exception escaping the runner's
    /// own construction sequence — which sits outside the <c>finally</c> that disposes the
    /// scenario's secret resolvers.
    /// </remarks>
    internal static ISecurityConfigurationAccessor Build(
        ScenarioAst ast, string? suiteDirectory, ISecretAccessor? secrets)
    {
        var services = ast.Environment?.Services;
        var dependencies = ast.Environment?.Dependencies;

        var byTarget = new Dictionary<string, SecurityConfiguration>(StringComparer.Ordinal);
        var ambiguousTargets = new HashSet<string>(StringComparer.Ordinal);
        var resolvedSuiteDirectory = Path.GetFullPath(suiteDirectory ?? Directory.GetCurrentDirectory());

        if (services is not null)
        {
            foreach (var (name, spec) in services)
            {
                if (spec.Security is { } security)
                {
                    byTarget[name] = Project(name, "services", security, resolvedSuiteDirectory, secrets);
                }
            }
        }

        if (dependencies is not null)
        {
            foreach (var (name, spec) in dependencies)
            {
                if (spec.Security is not { } security)
                {
                    continue;
                }

                if (byTarget.ContainsKey(name))
                {
                    // A service and a dependency both named `name`, BOTH declaring security.
                    // A step's `target` is a bare name with no kind discriminator, so there is
                    // no answer to give — and silently preferring one would hand a step the
                    // OTHER one's certificates. Recorded rather than thrown: raising it here
                    // would fail a whole scenario for an ambiguity no step may even reference,
                    // and would do so from a construction site the runner's disposal
                    // `finally` does not yet cover. Only the both-declare case is ambiguous:
                    // when just one side declares security the lookup is unambiguous.
                    //
                    // Note what this case is NOT. A name declared as both a service and a
                    // dependency is not tolerated anywhere in this engine —
                    // ProviderPipeline.BuildProjectContext rejects it outright with a
                    // ValidationFailure, before any builder mutation, so on every production
                    // path the suite has already failed by the time this class is constructed.
                    // This branch is therefore a fail-closed backstop for direct engine
                    // embedding that skips that stage, not evidence that the collision is
                    // acceptable elsewhere.
                    ambiguousTargets.Add(name);
                    continue;
                }

                byTarget[name] = Project(name, "dependencies", security, resolvedSuiteDirectory, secrets);
            }
        }

        return byTarget.Count == 0 && ambiguousTargets.Count == 0
            ? NullSecurityConfigurationAccessor.Instance
            : new SecurityConfigurationAccessor(byTarget, ambiguousTargets);
    }

    /// <inheritdoc />
    /// <exception cref="SecurityMaterialException">
    /// <paramref name="targetName"/> is declared BOTH as a service and as a dependency and both
    /// declare a <c>security</c> block, so it cannot be resolved unambiguously.
    /// </exception>
    public ISecurityConfiguration? For(string targetName)
    {
        if (targetName is null)
        {
            return null;
        }

        if (_ambiguousTargets.Contains(targetName))
        {
            throw new SecurityMaterialException(
                $"target '{targetName}' is declared BOTH as a service and as a dependency, and both " +
                "declare a 'security' block; a step's 'target' names one bare name and cannot select " +
                "between them. Rename one of the two declarations.");
        }

        return _byTarget.TryGetValue(targetName, out var configuration) ? configuration : null;
    }

    /// <summary>
    /// Disposes every certificate this accessor loaded. Safe to call when nothing was ever
    /// loaded (the lazy views are simply never forced).
    /// </summary>
    public void Dispose()
    {
        foreach (var configuration in _byTarget.Values)
        {
            configuration.Certificates?.Dispose();
        }
    }

    private static SecurityConfiguration Project(
        string targetName,
        string ownerKindPlural,
        SecuritySpec security,
        string resolvedSuiteDirectory,
        ISecretAccessor? secrets)
    {
        var fieldPathPrefix = $"environment.{ownerKindPlural}.{targetName}.security";

        var ca = DeclaredPath.From(security.CaCert, resolvedSuiteDirectory);
        var clientCert = DeclaredPath.From(security.ClientCert, resolvedSuiteDirectory);
        var clientKey = DeclaredPath.From(security.ClientKey, resolvedSuiteDirectory);

        // No path-valued certificate field declared at all → no certificate VIEW, rather than
        // an empty one. REQ-001/REQ-024: an absent caCert is a normal configuration in which
        // the platform's own trust store applies, and the engine must synthesise nothing.
        //
        // `clientKeyPassword` DOES participate, and the earlier reading of this condition — that
        // it need not, because "materialising a view whose every path is null would answer it with
        // silence just the same" — stopped being true the moment ResolveClientKeyPassword grew
        // EDGE-004's missing-`clientKey` refusal. The view is no longer silent about that shape:
        // reading its passphrase raises the refusal by name, and reading its certificate forces the
        // same resolver rather than returning the "present nothing" null H2 forbids. Excluding the
        // field here would therefore leave EXACTLY ONE of EDGE-004's three shapes — a passphrase
        // with no path-valued field at all — dropped before any guard can see it, which is the
        // deferral-to-an-unwritten-guard this comment used to contain. All three are reachable only
        // by direct engine embedding (the schema's `mtls` branch requires the `clientCert`/
        // `clientKey` pair and its `tls` branch forbids the passphrase outright); no YAML-authored
        // suite changes shape because of this clause.
        var certificates = ca is null && clientCert is null && clientKey is null
                && security.ClientKeyPassword is null
            ? null
            : new SecurityCertificateMaterial(
                fieldPathPrefix,
                resolvedSuiteDirectory,
                ca,
                clientCert,
                clientKey,
                security.ClientKeyPassword,
                secrets);

        return new SecurityConfiguration(security.Profile ?? string.Empty, certificates);
    }

    /// <summary>
    /// One declared path-valued field, in BOTH forms: exactly as the author wrote it (the only
    /// form any diagnostic may name) and resolved against the suite directory (the form real
    /// client libraries and the certificate loaders need).
    /// </summary>
    /// <param name="Declared">The author's own text, relative to the suite directory.</param>
    /// <param name="Resolved">The absolute host path it resolves to.</param>
    private sealed record DeclaredPath(string Declared, string Resolved)
    {
        /// <summary>
        /// Resolves one declared path against the suite directory, or returns
        /// <see langword="null"/> when the field is absent.
        /// </summary>
        /// <remarks>
        /// A ROOTED declared path is already rejected outright by
        /// <c>EnvironmentSecurityValidator.ValidatePath</c> on every production path, so
        /// <see cref="Path.Combine(string, string)"/>'s documented "second argument rooted →
        /// first discarded" behaviour is unreachable here in practice.
        /// </remarks>
        internal static DeclaredPath? From(string? declaredPath, string resolvedSuiteDirectory) =>
            string.IsNullOrWhiteSpace(declaredPath)
                ? null
                : new DeclaredPath(
                    declaredPath, Path.GetFullPath(Path.Combine(resolvedSuiteDirectory, declaredPath)));
    }

    private sealed class SecurityConfiguration : ISecurityConfiguration
    {
        internal SecurityConfiguration(string profile, SecurityCertificateMaterial? certificates)
        {
            Profile = profile;
            Certificates = certificates;
        }

        public string Profile { get; }

        public SecurityCertificateMaterial? Certificates { get; }

        ISecurityCertificateMaterial? ISecurityConfiguration.Certificates => Certificates;
    }

    private sealed class SecurityCertificateMaterial : ISecurityCertificateMaterial, IDisposable
    {
        /// <summary>
        /// The <c>serverAuth</c> extended-key-usage OID. Applied to the rebuilt chain so the
        /// engine's replacement verdict is not weaker than the platform's own — see
        /// <see cref="TrustsRemoteCertificate"/>.
        /// </summary>
        private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

        private readonly string _fieldPathPrefix;
        private readonly string _resolvedSuiteDirectory;
        private readonly DeclaredPath? _ca;
        private readonly DeclaredPath? _clientCert;
        private readonly DeclaredPath? _clientKey;

        /// <summary>
        /// The <c>clientKeyPassword</c> exactly as DECLARED — a <c>${secret:source/path}</c>
        /// reference on any schema-validated path — or <see langword="null"/> when the author
        /// declared none. Never a passphrase value.
        /// </summary>
        /// <remarks>
        /// <strong>HAZARD H1 — this field, and never the resolved
        /// <see cref="ClientKeyPassword"/> member, is what "did the author declare a passphrase?"
        /// must be asked of.</strong> <see langword="null"/> on the interface member is
        /// OVERLOADED: it means both "no passphrase was declared" and "this implementor inherits
        /// <see cref="ISecurityCertificateMaterial.ClientKeyPassword"/>'s default and knows
        /// nothing about passphrases at all". Keyed off the member, a plumbing omission — a new
        /// material type that forgets the override, a <c>Build(…)</c> call handed no accessor —
        /// becomes indistinguishable from an author who declared nothing, and the REQ-006
        /// contradiction guard silently no-ops on exactly the case it exists to catch. Nothing in
        /// the type system enforces this; see <see cref="ResolveClientKeyPassword"/>'s own note at
        /// the guard.
        /// </remarks>
        private readonly string? _declaredClientKeyPassword;

        private readonly ISecretAccessor? _secrets;
        private readonly Lazy<X509Certificate2?> _caCertificate;
        private readonly Lazy<X509Certificate2?> _clientCertificate;
        private readonly Lazy<SecretString?> _clientKeyPassword;

        internal SecurityCertificateMaterial(
            string fieldPathPrefix,
            string resolvedSuiteDirectory,
            DeclaredPath? ca,
            DeclaredPath? clientCert,
            DeclaredPath? clientKey,
            string? declaredClientKeyPassword,
            ISecretAccessor? secrets)
        {
            _fieldPathPrefix = fieldPathPrefix;
            _resolvedSuiteDirectory = resolvedSuiteDirectory;
            _ca = ca;
            _clientCert = clientCert;
            _clientKey = clientKey;
            _declaredClientKeyPassword = declaredClientKeyPassword;
            _secrets = secrets;

            // ExecutionAndPublication: one load per target however many steps resolve it
            // concurrently under `--parallel`, and the SAME instance to all of them.
            //
            // This mode CACHES THE EXCEPTION as well as the value, and that is deliberate. A
            // transient IOException on the first read therefore poisons that target for the rest
            // of the scenario rather than being retried by the next step. Two reasons to want
            // that. Determinism: every step resolving one target must get one answer, and a
            // per-step retry would make a suite's verdict depend on which step happened to read
            // first. And the transient case is already narrow — EnvironmentSecurityValidator has
            // existence-checked the file pre-topology, so a failure here is a file that changed
            // or became unreadable mid-run, which is an environment fault worth reporting on
            // every step that depends on it rather than papering over on the second attempt. The
            // exception surfaces identically each time (a step-scoped EnvironmentError, §12.1),
            // so a poisoned target is loud, not silent.
            _caCertificate = new Lazy<X509Certificate2?>(LoadCa, LazyThreadSafetyMode.ExecutionAndPublication);
            _clientCertificate = new Lazy<X509Certificate2?>(
                LoadClient, LazyThreadSafetyMode.ExecutionAndPublication);

            // The passphrase resolves on FIRST USE of the material and never at Build time
            // (REQ-009, §17): construction of this object must not touch the secret subsystem, or
            // a suite would resolve a secret for a target no step ever reaches, and — worse — do
            // it before the topology the value may live in has started. Same caching rationale as
            // the two loaders above, including the deliberate caching of a failure.
            _clientKeyPassword = new Lazy<SecretString?>(
                ResolveClientKeyPassword, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        // The PATH view is guarded by the same containment backstop as the object view below,
        // and for the same reason. A consumer that reads only paths — librdkafka's
        // SslCaLocation/SslCertificateLocation/SslKeyLocation (REQ-015) accepts nothing else and
        // never touches CaCertificate/ClientCertificate at all — would otherwise reach this
        // class through the ONE view the backstop did not cover, which is precisely the caller
        // it was written for. Guarding the loaders alone made the guard's coverage an accident
        // of which view a consumer happened to want.
        public string? CaCertificatePath => ResolvedIfContained(_ca, "caCert");

        public string? ClientCertificatePath => ResolvedIfContained(_clientCert, "clientCert");

        public string? ClientKeyPath => ResolvedIfContained(_clientKey, "clientKey");

        public X509Certificate2? CaCertificate => _caCertificate.Value;

        public X509Certificate2? ClientCertificate => _clientCertificate.Value;

        /// <inheritdoc />
        /// <remarks>
        /// Resolved through the run's <see cref="ISecretAccessor"/> on FIRST READ — of this member
        /// or of <see cref="ClientCertificate"/>, whichever a consumer touches first — and cached
        /// for the scenario. Reading it is what performs the resolution, so it can throw
        /// (<see cref="SecurityMaterialException"/>) exactly like the two certificate views, and it
        /// raises REQ-006's contradiction refusal for a passphrase declared against a key that is
        /// not encrypted — deliberately, so that a consumer reading only the passphrase and the
        /// paths (librdkafka) is governed by the same guard as one reading the certificate.
        /// </remarks>
        public SecretString? ClientKeyPassword => _clientKeyPassword.Value;

        public bool TrustsRemoteCertificate(
            X509Certificate2? remoteCertificate, X509Chain? platformBuiltChain, SslPolicyErrors sslPolicyErrors)
        {
            var ca = CaCertificate;
            if (ca is null)
            {
                // No declared trust anchor: the platform's own verdict is the only one. The
                // engine narrows nothing and relaxes nothing. (In production this is also
                // unreachable — Security_Helpers installs no callback at all when no caCert is
                // declared — so nothing here can change an unsecured suite's behaviour.)
                return sslPolicyErrors == SslPolicyErrors.None;
            }

            // A declared caCert is a PIN. Deliberately NO `sslPolicyErrors == None -> true`
            // short-circuit: that shape trusted anything the MACHINE store accepted without
            // ever consulting the declared anchor, so an author who wrote "only this private
            // CA" got "this private CA, or any public one the host happens to trust". Measured
            // before this change: an unrelated CA's leaf, and a self-signed leaf, both returned
            // true when the platform reported None. Exposure today is nil — targets are
            // Aspire-staged loopback endpoints and no public CA issues for `localhost` — but
            // narrowing this after the 1.0 freeze is the direction that is not available, and
            // the configuration a pin breaks ("I declared a private anchor AND the peer chains
            // publicly") is incoherent rather than legitimate.
            //
            // Only chain errors are forgivable by supplying a private root. A name mismatch
            // (wrong host) or an absent certificate is never forgiven — see the interface's own
            // remarks for why widening this would turn "trust this CA" into "trust anyone".
            if ((sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            {
                return false;
            }

            if (remoteCertificate is null)
            {
                return false;
            }

            // A FRESH chain, never the platform-supplied one: the callback's `chain` argument
            // has already been built against the default trust store, and mutating an
            // already-built chain's policy is not a documented reset. `using` statement form,
            // not `using var` — this file is ordinary compiled C#, but the same disposal shape
            // the emitted-CSX rule forces is kept for consistency with the helper that calls it.
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);

                // NoCheck: a private enterprise CA of the kind this feature exists for
                // routinely publishes no CRL/OCSP responder at all, so an online revocation
                // check would fail every handshake for a reason unrelated to trust. The
                // engine is verifying an author-declared anchor inside a test topology, not
                // acting as a browser on the public web.
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                // RevocationMode.NoCheck suppresses CRL/OCSP but NOT AIA `caIssuers` fetching,
                // which is governed separately by this flag. Without it, a chain build against
                // a leaf carrying an AIA extension issues an outbound GET to a PEER-CONTROLLED
                // URL inside the handshake — measured, with Windows' 15-second-per-URL
                // retrieval timeout as the stall bound, and on the REJECTION path, so a peer
                // need not be trusted to trigger it.
                //
                // What this costs, stated honestly rather than waved away: an AIA fetch CAN
                // contribute a link the peer failed to send, so a server that omits its own
                // intermediate and expects clients to go and fetch it no longer validates here.
                // That is the intended trade. TLS requires a server to send its chain, so the
                // shape being refused is a misconfigured peer; the private enterprise CA this
                // feature exists for characteristically publishes no responder to fetch from
                // anyway (the same premise as the NoCheck above); and the alternative is an
                // outbound request to an address the peer chose, made by the test host, during
                // a handshake it is in the middle of rejecting.
                //
                // What this line does NOT close, because it covers HALF the traffic and reading
                // it as "no outbound request happens" is the expensive mistake: this ChainPolicy
                // governs the chain THIS method builds, and nothing else. Before the validation
                // callback is ever entered, `SslStream` has already built its own chain against
                // the default trust store — that build is what PRODUCES the `sslPolicyErrors`
                // argument — and it honours no policy this engine sets.
                //
                // MEASURED, with a counting listener standing in for the AIA `caIssuers` URL, a
                // fresh leaf and a fresh URL per process so no OS chain cache can mask a fetch,
                // three runs of each arm, identical every time:
                //   • a plain HttpClientHandler with NO custom callback — what any .NET client
                //     on this host does, and so what this traffic cost before the slice: 2.
                //   • this callback installed, fence sampled either side of the Build below:
                //     2 before, 2 after — 0 attributable to the rebuilt chain.
                // The residual is the platform's, unchanged by anything here.
                //
                // Closing it is possible and is deliberately not taken. It needs the handshake
                // driven through `SocketsHttpHandler.SslOptions.CertificateChainPolicy` (a real
                // net8.0 API) instead of `HttpClientHandler`, which changes the handler type in
                // the emitted Security_Helpers block and in every provider that calls it — a
                // materially larger change than the slice it would harden. What it buys back is a
                // GET to a peer-chosen URL disclosing "a host here validated your certificate",
                // plus a per-URL retrieval stall every .NET TLS client on this host already
                // carries. Recorded rather than fixed, so a later reader inherits the
                // measurement instead of the impression that the request is gone.
                chain.ChainPolicy.DisableCertificateDownloads = true;

                // The platform enforces `serverAuth` on a server certificate; a custom callback
                // replaces the platform's verdict wholesale, so an unset ApplicationPolicy
                // (which means NO extended-key-usage constraint) silently forgives what the
                // platform refuses. Measured before this line existed: a server certificate
                // issued by the declared CA carrying `EKU = clientAuth` only was trusted, and
                // connected with status 200 end to end. In mutual TLS the CA that signs the
                // server signs every client, so that is a server-impersonation path for any
                // holder of a client certificate whose SAN matches the target host. A leaf with
                // NO EKU extension at all stays trusted (absent means unconstrained), which is
                // what makes this safe for legitimate configurations.
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthOid));

                // The platform's built chain, contributed for path building ONLY. A two-tier PKI
                // (offline root declared as `caCert`, issuing intermediate sent by the server) is
                // the normal enterprise shape and cannot validate without the intermediate —
                // measured as a flat `false`, whose EnvironmentError then pushes an author
                // towards declaring the intermediate as their anchor or dropping `caCert`
                // altogether, both of which weaken their setup.
                //
                // Note what these elements ARE, because the obvious reading overstates how
                // tightly the input is scoped: ChainElements is the platform's own BUILT chain, a
                // SUPERSET of what the peer put on the wire — measured, a server that sent only
                // its leaf still produced a two-element chain, the root coming from a local
                // cache. Harmless here, and the reason it is harmless is the next sentence.
                //
                // These certificates are UNTRUSTED input and never become anchors whatever their
                // provenance: CustomRootTrust means the chain terminates only at the declared CA
                // in CustomTrustStore, so a self-signed root supplied here as an "intermediate"
                // is still rejected (measured, and pinned by test).
                if (platformBuiltChain is not null)
                {
                    foreach (var element in platformBuiltChain.ChainElements)
                    {
                        chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                    }
                }

                return chain.Build(remoteCertificate);
            }
        }

        public void Dispose()
        {
            if (_caCertificate.IsValueCreated)
            {
                _caCertificate.Value?.Dispose();
            }

            if (_clientCertificate.IsValueCreated)
            {
                _clientCertificate.Value?.Dispose();
            }
        }

        /// <summary>
        /// Defence-in-depth containment re-check (REQ-003), sharing
        /// <see cref="EnvironmentSecurityValidator.IsContainedWithin"/> rather than restating
        /// the rule in a second spelling.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Be precise about what this buys. It is MEASURED not to catch a base-directory
        /// divergence — a path resolved against the wrong suite directory is still contained
        /// within THAT directory, so this predicate returns true in exactly the case that
        /// motivated it. Its value is as a fail-closed backstop for a future caller that
        /// reaches this class without the validator having run at all; the fix for a divergence
        /// is, and remains, handing this class the same base directory
        /// <c>ProviderPipeline.Compile</c> received.
        /// </para>
        /// <para>
        /// Applied on EVERY read of a declared path — both certificate views — so its coverage
        /// does not depend on which view a consumer happens to want. Checked at READ time
        /// rather than at construction so it surfaces the way every other content fault here
        /// does: a <see cref="SecurityMaterialException"/> inside a provider's guarded region,
        /// becoming a step-scoped environment error (§12.1). The construction site is the wrong
        /// place for it, and measurably so: <c>ScenarioRunner</c> builds this accessor inside a
        /// <c>try</c>/<c>finally</c> that carries NO <c>catch</c>, so a throw there is cleaned
        /// up after but still escapes the run instead of becoming a scenario's verdict.
        /// </para>
        /// </remarks>
        private void EnsureContained(DeclaredPath path, string fieldName)
        {
            if (EnvironmentSecurityValidator.IsContainedWithin(path.Resolved, _resolvedSuiteDirectory))
            {
                return;
            }

            // Names the DECLARED path only: this message reaches Vars and the §14 event stream
            // through a provider's general catch, where no scrubber can redact a host path. The
            // validation-time siblings now do the same — SecurityArtifactPath's remarks carry the
            // one rule and why it has no per-site exception.
            throw new SecurityMaterialException(
                $"{_fieldPathPrefix}.{fieldName}: '{path.Declared}' resolves outside the suite directory.");
        }

        /// <summary>
        /// The path view of one declared field: the resolved absolute path once
        /// <see cref="EnsureContained"/> has passed, <see langword="null"/> when the field is
        /// not declared at all.
        /// </summary>
        /// <remarks>
        /// An absent field is ABSENT, never a containment failure — REQ-001/REQ-024's rule that
        /// the engine synthesises nothing for an undeclared <c>caCert</c> is what makes the
        /// null-first ordering here load-bearing rather than defensive.
        /// </remarks>
        private string? ResolvedIfContained(DeclaredPath? path, string fieldName)
        {
            if (path is null)
            {
                return null;
            }

            EnsureContained(path, fieldName);
            return path.Resolved;
        }

        private X509Certificate2? LoadCa()
        {
            if (_ca is not { } ca)
            {
                return null;
            }

            EnsureContained(ca, "caCert");

            try
            {
                // The file-path constructor, deliberately: it auto-detects PEM and DER, where
                // X509Certificate2.CreateFromPem would reject a DER-encoded anchor outright.
                // (Obsoleted as SYSLIB0057 in .NET 9 in favour of X509CertificateLoader, which
                // does not exist on this repo's pinned net8.0 target; swap it when the TFM moves.)
                //
                // No X509KeyStorageFlags decision to make here, unlike LoadClient below: a trust
                // anchor is a public certificate with no private key, so nothing is imported into
                // any key store and EphemeralKeySet would have nothing to apply to. Measured on
                // this host — loading the anchor adds zero files to the user CNG key store.
                return new X509Certificate2(ca.Resolved);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}.caCert: '{ca.Declared}' could not be read as a certificate " +
                    $"({ex.Message}).",
                    ex);
            }
        }

        private X509Certificate2? LoadClient()
        {
            if (_clientCert is null && _clientKey is null)
            {
                // No client identity declared at all — the `tls` profile. Absent, not missing.
                //
                // EDGE-004 shape (b)/(c) — a passphrase declared beside a bare `caCert`, or beside
                // nothing at all — is NOT that, and must not be answered with the null this line
                // returns: `ClientCertificate == null` is this engine's wire signal for "present no
                // client identity" (H2), so returning it here would let an incoherent declaration
                // resolve to silence on the certificate path while only the passphrase path refused
                // it. Forcing the resolver routes both shapes through the missing-`clientKey`
                // refusal, which is unconditional for them and so does not return.
                if (_declaredClientKeyPassword is not null)
                {
                    _ = _clientKeyPassword.Value;
                }

                return null;
            }

            if (_clientCert is null || _clientKey is null)
            {
                // HALF a pair. Returning null here (the previous behaviour) degraded a declared
                // `mtls` identity to "present nothing" at run time: measured against a listener
                // that requests but does not enforce a client certificate, the suite PASSED
                // while presenting no identity. The schema closes this at authoring time
                // (`profile: mtls` requires both fields), so this is unreachable from YAML — but
                // the only thing between an author and an unauthenticated pass must not be a
                // layer the runtime never consults, and direct engine embedding bypasses the
                // schema entirely.
                var declared = _clientCert is null ? "clientKey" : "clientCert";
                var missing = _clientCert is null ? "clientCert" : "clientKey";
                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}: '{declared}' is declared without a matching '{missing}'. A client " +
                    "identity needs BOTH the certificate and its private key; declare the missing field, or " +
                    "remove both and use 'profile: tls' to present no client identity.");
            }

            EnsureContained(_clientCert, "clientCert");
            EnsureContained(_clientKey, "clientKey");

            // Resolves the reference on first use of this material and caches it (REQ-009), and
            // raises REQ-006's contradiction refusal for a passphrase declared against a key that
            // is not encrypted — see ResolveClientKeyPassword, which owns both.
            var passphrase = _clientKeyPassword.Value;

            X509Certificate2? pemPair = null;
            try
            {
                // Reveal() at the sink and nowhere else (§17): the revealed string is handed
                // straight to the platform loader as its ReadOnlySpan<char> argument and never
                // bound to a local, a Vars key or a diagnostic.
                //
                // MEASURED against the pinned net8.0 ref pack, because the two overloads do NOT
                // take their arguments in the same order and the mistake compiles:
                //   CreateFromPemFile(string certPemFilePath, string? keyPemFilePath = null)
                //   CreateFromEncryptedPemFile(string certPemFilePath, ReadOnlySpan<char> password,
                //                              string? keyPemFilePath = null)
                // — the password sits BETWEEN the two paths.
                if (passphrase is null)
                {
                    pemPair = X509Certificate2.CreateFromPemFile(_clientCert.Resolved, _clientKey.Resolved);
                }
                else
                {
                    // SCOPED TO THE DECRYPTING LOAD, AND ONLY TO IT. The wrong-passphrase message
                    // below is an ATTRIBUTION, so what it is allowed to see decides whether it is
                    // true: raised from the method-wide catch it also fired for an unreadable
                    // `clientCert` and for a failure of the PKCS#12 re-import two statements later,
                    // and told the author "the likeliest cause is that the passphrase is wrong"
                    // about faults the passphrase had no part in.
                    //
                    // CryptographicException ONLY, and that is the narrowing rather than a
                    // side-effect of it. A wrong passphrase — and an encryption form .NET's PEM
                    // reader will not read — fails as a CryptographicException; a file that cannot
                    // be opened fails as an IOException or an UnauthorizedAccessException, which
                    // this filter lets through to the generic message that does not blame anything.
                    try
                    {
                        pemPair = X509Certificate2.CreateFromEncryptedPemFile(
                            _clientCert.Resolved, passphrase.Reveal(), _clientKey.Resolved);
                    }
                    catch (CryptographicException ex)
                    {
                        // HAZARD H3 — WHAT THIS MESSAGE MAY CONTAIN IS CONSTRAINED AT THIS THROW
                        // SITE, not downstream. SecuredEndpointProbe folds a
                        // SecurityMaterialException's Message into its probe-failure text, which
                        // becomes a §14 environment-error event, so by the time this string leaves
                        // the method it is already bound for the stream and for every renderer. It
                        // therefore quotes NEITHER the passphrase value NOR its length — no
                        // Reveal(), no Length, and not the platform's own text either — and every
                        // piece of untrusted text it does quote (the reference, and the declared
                        // `clientKey` path, which is an unconstrained schema string carrying the
                        // same C0/ANSI/newline hazard) goes through QuoteUntrusted rather than
                        // being concatenated raw.
                        throw new SecurityMaterialException(
                            $"{_fieldPathPrefix}.clientKey: {QuoteUntrusted(_clientKey.Declared)} could "
                            + "not be loaded with the passphrase 'clientKeyPassword' resolved from "
                            + $"{QuoteUntrusted(_declaredClientKeyPassword!)}. The likeliest cause is "
                            + "that the passphrase is wrong; the other is an encryption form this "
                            + $"runtime cannot read, since only the '{EncryptedPkcs8Label}' (PKCS#8) "
                            + "form is supported — convert an openssl legacy key marked "
                            + $"'{LegacyEncryptedPemHeader}' with 'openssl pkcs8 -topk8'. The "
                            + "passphrase itself is never reported.",
                            ex);
                    }
                }

                // The PKCS#12 round trip is NOT ceremony, and it applies UNCHANGED to BOTH branches
                // above. MEASURED on this repo's Windows host against the mutual-TLS test bed: a
                // certificate produced directly by
                // CreateFromPemFile reports HasPrivateKey=true and then FAILS the TLS client
                // authentication handshake with AuthenticationException, because its key is an
                // EPHEMERAL key SChannel cannot use for client auth; the identical certificate
                // exported to PKCS#12 and re-imported completes the same handshake and returns
                // 200. Re-importing with the default key-storage flags (never
                // EphemeralKeySet — that is the failure being worked around) is the portable fix.
                //
                // The transfer blob is ENCRYPTED with a single-use random password and zeroed
                // afterwards. Exported with no password, the blob is an unencrypted PKCS#8
                // private key sitting in a managed byte[] that nothing ever clears and the GC
                // may copy while compacting. The password itself is a string and so cannot be
                // zeroed — an accepted residual, and a far smaller one than the key: the
                // X509Certificate2(byte[], string, ...) overload is the only portable entry
                // point, its SecureString sibling is obsolete on non-Windows, and the password
                // protects a blob that lives for two statements.
                var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                var pkcs12 = pemPair.Export(X509ContentType.Pkcs12, password);
                try
                {
                    // DefaultKeySet, measured: the re-imported key lands in the USER key store
                    // (CNG IsMachineKey=false, IsEphemeral=false) and Dispose removes it —
                    // ScenarioRunner disposes this accessor in the same finally as the secret
                    // resolvers, on every exit path. A hard kill (Ctrl-C, a CI timeout, a
                    // test-host crash) still abandons one key file per loaded client
                    // certificate, and the GC never reclaims it (measured: unchanged after a
                    // forced collection). EphemeralKeySet would close that, and is exactly the
                    // defect this round trip exists to work around — so the abandonment window
                    // is accepted, not traded for a certificate that cannot authenticate. An
                    // encrypted-key load has no reason to be exempt from any of this: skipping the
                    // round trip on that branch would ship a path that authenticates on Linux and
                    // fails on Windows.
                    return new X509Certificate2(pkcs12, password, X509KeyStorageFlags.DefaultKeySet);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs12);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                // NAME THE CAUSE WHEN IT IS THE ONE AN ENTERPRISE PKI PRODUCES BY DEFAULT (peer
                // review, slice F). A bank handing its pilot an encrypted key and declaring NO
                // `clientKeyPassword` hits this catch first, and MEASURED on
                // .NET 8.0.29 / Windows 10.0.26200 the platform's own message carries no signal
                // whatever about encryption being the reason:
                //
                //   key shape                                        CreateFromPemFile result
                //   ──────────────────────────────────────────────── ──────────────────────────────
                //   unencrypted PKCS#8  (BEGIN PRIVATE KEY)          OK, HasPrivateKey=True
                //   unencrypted PKCS#1  (BEGIN RSA PRIVATE KEY)      OK, HasPrivateKey=True
                //   ENCRYPTED PKCS#8    (BEGIN ENCRYPTED PRIVATE KEY)CryptographicException
                //   openssl legacy enc  (Proc-Type: 4,ENCRYPTED)     CryptographicException
                //   outright garbage    ("not a pem at all")         CryptographicException
                //
                // All three failures raise the IDENTICAL type and the IDENTICAL text — "The key
                // contents do not contain a PEM, the content is malformed, or the key does not
                // match the certificate." — so an encrypted key is reported to the author as a
                // malformed one, and the platform message is not merely unhelpful but actively
                // misleading. The FILE is the only place the distinction survives, which is why
                // the classification below reads it rather than the exception.
                //
                // TWO markers, because measurement found two encrypted shapes and the brief for
                // this fix named one. `-----BEGIN ENCRYPTED PRIVATE KEY-----` is what .NET's own
                // RSA.ExportEncryptedPkcs8PrivateKeyPem writes and what `openssl pkcs8 -topk8`
                // writes; `openssl rsa -aes256 -traditional` instead keeps the ordinary
                // `-----BEGIN RSA PRIVATE KEY-----` label and marks the encryption with a
                // `Proc-Type: 4,ENCRYPTED` header, so a label-only check would classify that file
                // as garbage. Both were measured failing identically above, so covering both is a
                // strict improvement with no new false positive: neither marker can appear in a
                // file CreateFromPemFile would have loaded.
                //
                // IN THE CATCH, never before the load — on THIS path. The classification runs only
                // once the load has already failed, so every file that loads without a declared
                // passphrase still loads, on the same code path, with no extra I/O; if it cannot
                // read the file it falls back to the generic message below.
                //
                // That "no extra I/O" claim is now TRUE OF THE NO-PASSPHRASE PATH ONLY, and saying
                // otherwise would be the over-claim this file exists to avoid. A declared
                // passphrase reads the key file twice on a successful load: once eagerly in
                // ResolveClientKeyPassword, to raise REQ-006's contradiction refusal for a key that
                // is not encrypted, and once in the platform loader. That is the price of siting
                // REQ-006's guard on the MATERIAL rather than on the certificate load — librdkafka
                // never touches ClientCertificate, so a guard in the load would be absent from it —
                // and it is paid only by suites that declare a passphrase, against a file this
                // method already reads in full.
                //
                // GUARDED ON NO PASSPHRASE BEING DECLARED, and the guard is load-bearing. This
                // message's whole content is "the key is encrypted and you declared no
                // passphrase" — which, for an author who DID declare one, is false in its second
                // half and useless in its first: REQ-006's guard has already confirmed the key is
                // encrypted, and the decrypting load owns its own failure in its own catch above.
                // Reaching here with a passphrase declared therefore means the fault is in reading
                // the certificate file or in the PKCS#12 round trip, and the generic message below
                // — which attributes nothing — is the honest one for it.
                if (_declaredClientKeyPassword is null
                    && DescribeEncryptedPrivateKey(_clientKey.Resolved, out _) is { } encryption)
                {
                    throw new SecurityMaterialException(
                        $"{_fieldPathPrefix}.clientKey: '{_clientKey.Declared}' is an ENCRYPTED private "
                        + $"key ({encryption}) and no 'clientKeyPassword' is declared for it. Declare the "
                        + "passphrase beside 'clientKey' as a secret reference — "
                        + "clientKeyPassword: ${secret:env/CLIENT_KEY_PASS} — which is resolved when "
                        + "the key material is first used, after the topology is up, and never "
                        + "written to a report, an event or the compiled "
                        + "script; a literal passphrase is refused. Only the "
                        + $"'{EncryptedPkcs8Label}' (PKCS#8) form can be opened that way, so a key "
                        + $"marked '{LegacyEncryptedPemHeader}' must first be converted with "
                        + "'openssl pkcs8 -topk8'. Failing that, decrypt the key outright — "
                        + "'openssl pkcs8 -topk8 -nocrypt -in <encrypted> -out <plain>' — and point "
                        + "'clientKey' at the result, keeping the plaintext key inside the suite "
                        + "directory and out of version control.",
                        ex);
                }

                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}.clientCert/clientKey: '{_clientCert.Declared}' and " +
                    $"'{_clientKey.Declared}' could not be loaded as a certificate and matching private key " +
                    $"({ex.Message}).",
                    ex);
            }
            finally
            {
                pemPair?.Dispose();
            }
        }

        /// <summary>
        /// Resolves the declared <c>clientKeyPassword</c> reference through the run's secret
        /// accessor, or returns <see langword="null"/> when the author declared none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>HAZARD H2 — every failure here THROWS, and none returns
        /// <see langword="null"/>.</strong> <c>ClientCertificate == null</c> is this engine's wire
        /// signal for "profile: tls, present no client identity", so a passphrase path that yielded
        /// null instead of throwing would reproduce, exactly, the bypass this file already records
        /// for a half-declared pair (see <see cref="LoadClient"/>): measured against a listener
        /// that requests but does not enforce a client certificate, a declared <c>mtls</c> identity
        /// degraded to "present nothing" and the suite PASSED while presenting no identity. That
        /// rule binds all three failures below alike — a null accessor, an unresolvable reference,
        /// and a reference resolving to an empty string.
        /// </para>
        /// <para>
        /// <strong>HAZARD H3</strong> applies to every message raised here for the reason stated at
        /// <see cref="LoadClient"/>'s catch: these strings reach the §14 event stream through
        /// <c>SecuredEndpointProbe</c>, so none quotes a value or a length, and every piece of
        /// untrusted text each one quotes goes through <see cref="QuoteUntrusted"/> rather than
        /// being concatenated raw.
        /// </para>
        /// <para>
        /// <strong>THE GUARD ORDER BELOW IS LOAD-BEARING, not incidental, and each step is placed
        /// against a stated property rather than by convenience.</strong>
        /// </para>
        /// <list type="number">
        /// <item><description>
        /// <em>Nothing declared</em> — the overwhelmingly common path, answered before anything is
        /// examined at all.
        /// </description></item>
        /// <item><description>
        /// <em>Not a whole secret reference</em> — FIRST of the refusals, because it is the only one
        /// whose subject may be a SECRET. Every guard after it may quote the declared text; this one
        /// may not, and none of them could be made safe individually while a literal could still
        /// reach them. It is also the cheapest — one regex over one string, no I/O and no accessor —
        /// so placing it first costs nothing to place it correctly.
        /// </description></item>
        /// <item><description>
        /// <em>No <c>clientKey</c> to decrypt</em> (EDGE-004) — before the file read, because there
        /// is no file to read; a passphrase for a target with no key is incoherent whatever the
        /// filesystem says.
        /// </description></item>
        /// <item><description>
        /// <em>Key unreadable</em>, then <em>key not encrypted</em> (REQ-006) — one read of the key
        /// file, two distinct verdicts. Unreadable is checked first because the classifier cannot
        /// tell the two apart on its own and the wrong order reports a missing or ACL-denied file as
        /// a passphrase-against-plaintext contradiction.
        /// </description></item>
        /// <item><description>
        /// <em>No accessor</em>, then <em>resolution</em>, then <em>empty value</em> — last, so a
        /// target refused for any contradiction above never resolves its secret at all.
        /// </description></item>
        /// </list>
        /// </remarks>
        private SecretString? ResolveClientKeyPassword()
        {
            // ── 1. Nothing declared ───────────────────────────────────────────────────────────
            //
            // Keyed off the DECLARED TEXT (H1) — this is the one authority on whether the author
            // asked for a passphrase at all.
            if (_declaredClientKeyPassword is null)
            {
                return null;
            }

            // ── 2. The declared value must be a WHOLE secret reference NAMING A KNOWN SOURCE ──
            //
            // AND THE VALUE IS NOT REPORTED, which is the entire point of this guard rather than a
            // stylistic preference. `SecuritySpec.ClientKeyPassword`'s own remarks record that a
            // direct engine embedder bypassing the schema CAN bind a literal here, so no consumer
            // may assume the text is non-secret — and this class is a consumer. Without this guard
            // a literal reaches SecretAccessor.Resolve, which raises
            // "the secret reference '<literal>' is malformed…" (ISecretAccessor.cs), and the catch
            // below quotes that message beside the declared text — the passphrase, twice, into a
            // §14 environment-error event, the JSON stream, the HTML report and the terminal.
            //
            // NO LATER SCRUB CAN REACH IT. ResolvedSecrets.Record runs only after a SUCCESSFUL
            // resolve (ISecretAccessor.cs), and this diagnostic fires INSTEAD of one, so REQ-010's
            // ledger is empty at the moment the value would be emitted. Redaction on this path is
            // achievable only by not emitting the value in the first place.
            //
            // Placed BEFORE every other refusal for the same reason: each of them quotes the
            // declared text through QuoteUntrusted, which ESCAPES but does not WITHHOLD. Only
            // text that has passed THIS line is known to be a pointer rather than a secret, which
            // is what §17 permits quoting.
            //
            // ValidateSecretBearingField, NOT TryParse alone. TryParse asks only whether one
            // whole token spans the value, and that is not sufficient: a reference path runs to
            // the first closing brace and is otherwise unrestricted, so a value can satisfy
            // TryParse while still carrying a further lead-in swallowed inside the path, after
            // which the remaining text is arbitrary and unexamined. An earlier form of this
            // comment asserted that passing TryParse "earns" the downstream quoting sites their
            // right to quote; that was never true, and the sentence is deleted rather than
            // softened. ValidateSecretBearingField applies the whole-token rule AND the rule
            // TryParse omits, and it is the same predicate `vouchfx validate` applies — fed the
            // same source set, so the two layers refuse exactly the same inputs in one spelling
            // rather than two.
            if (!SecretReference.ValidateSecretBearingField(
                    _declaredClientKeyPassword, ScenarioRunner.KnownSecretSources, out _))
            {
                // "…naming a resolvable source" covers BOTH modes the predicate refuses, and the
                // clause was added WITH the predicate rather than after it: gating on
                // ValidateSecretBearingField made an unknown-source value refusable here for the
                // first time, and the previous wording ("not a single, whole secret reference")
                // would have been false for exactly that input — it IS whole. The message still
                // withholds the value, which is why an or-shaped reason is acceptable: the author
                // is told which RULES apply, never which one their text broke.
                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}.clientKeyPassword: the declared value is not a single, whole "
                    + "secret reference naming a resolvable source. IT IS DELIBERATELY NOT REPORTED "
                    + "HERE, because a value in "
                    + "this position may be the passphrase itself. Declare a reference of the form "
                    + "'${secret:<source>/<path>}' and nothing else — for example "
                    + "clientKeyPassword: ${secret:env/CLIENT_KEY_PASS} — which is resolved when "
                    + "the key material is first used, after the topology is up, and never "
                    + "written to a report, an event or the compiled "
                    + "script. A literal passphrase is refused by design (§17), as is a reference "
                    + "with any text around it.");
            }

            // ── 3. EDGE-004: a passphrase with no `clientKey` to decrypt ──────────────────────
            //
            // Reachable only by direct engine embedding — the schema's `mtls` branch requires the
            // `clientCert`/`clientKey` pair and its `tls` branch forbids this field outright — which
            // is precisely the argument for the guard rather than against it, and the same one the
            // half-a-pair refusal in LoadClient rests on: the only thing between an author and a
            // control that is not running must not be a layer the runtime never consults.
            //
            // Three shapes reach here, and only one of them was refused before this guard existed,
            // by accident: a passphrase beside `clientCert` alone (the certificate load's half-a-pair
            // check catches the CERTIFICATE fault, and is absent from a passphrase-only consumer such
            // as librdkafka); a passphrase beside `caCert` alone; and a passphrase beside nothing at
            // all. All three are now one refusal, sited where every consumer meets it.
            if (_clientKey is null)
            {
                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}: 'clientKeyPassword' is declared without a matching "
                    + "'clientKey'. A key passphrase decrypts a private key, and this target "
                    + "declares none, so the passphrase can only be a control that is not running: "
                    + "declare 'clientKey' (with its 'clientCert') for the encrypted key the "
                    + "passphrase belongs to, or remove 'clientKeyPassword'.");
            }

            // ── 4. The key file: readable, then encrypted (REQ-006) ───────────────────────────
            //
            // Containment is re-checked FIRST, because this reads the file and no read of a declared
            // path may precede that check.
            //
            // HAZARD H1, and it is not enforceable by the type system, so it is stated here at the
            // site it governs: the declared-passphrase condition at the top of this method asks
            // _declaredClientKeyPassword — the DECLARED TEXT from SecuritySpec at the authoring layer
            // — and must NEVER ask the resolved ClientKeyPassword member. `null` on that member is
            // OVERLOADED (see the field's own remarks): it means both "the author declared nothing"
            // AND "this implementor inherits the interface default", so keyed off the member, a
            // plumbing omission — a material type that forgets the override, a Build(…) call handed
            // no accessor — would make this guard silently no-op on precisely the case it exists to
            // catch.
            //
            // Silently loading the key unencrypted instead would let a suite pass while the
            // passphrase it declares does nothing at all, which is how a key rotated back to
            // plaintext goes unnoticed.
            //
            // IT LIVES HERE, in the resolver, rather than in LoadClient, so that it is a property of
            // the MATERIAL rather than of one code path. LoadClient is not the only consumer of a
            // passphrase — librdkafka reads the paths and the passphrase and never touches
            // ClientCertificate at all — and a guard that only the certificate load ran would be
            // absent from exactly the consumer that never loads a certificate.
            //
            // TWO VERDICTS OFF ONE READ, and separating them is a correctness fix rather than a
            // nicety. DescribeEncryptedPrivateKey answers `null` both for a file carrying no
            // encryption marker and for a file it could not read at all; that conflation is harmless
            // at its other call site, which is already inside a catch about to throw a well-formed
            // diagnostic, and terminal here, where the return value IS the diagnostic. A missing,
            // locked or ACL-denied key would otherwise be reported to a passphrase-only consumer as
            // "'client-key.pem' is NOT an encrypted private key" — an accusation about the author's
            // configuration for a fault in the filesystem, with no later load attempt to correct the
            // story.
            EnsureContained(_clientKey, "clientKey");

            var encryption = DescribeEncryptedPrivateKey(_clientKey.Resolved, out var readable);

            if (!readable)
            {
                // The platform's own message is deliberately NOT quoted: it carries the RESOLVED
                // absolute path, which this file's header forbids any diagnostic from naming (a
                // provider's general catch writes it into Vars and thence to the §14 stream, where
                // no scrubber can redact a host path).
                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}.clientKeyPassword: a key passphrase is declared, but "
                    + $"{QuoteUntrusted(_clientKey.Declared)} could not be READ, so whether it is an "
                    + "encrypted private key cannot be determined. Refused rather than guessed: "
                    + "reporting an unreadable key as an unencrypted one would blame the declaration "
                    + "for a filesystem fault. Check that the file exists inside the suite directory "
                    + "and is readable by this process.");
            }

            if (encryption is null)
            {
                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}.clientKeyPassword: a key passphrase is declared, but "
                    + $"{QuoteUntrusted(_clientKey.Declared)} is NOT an encrypted private key — it "
                    + $"carries neither the '{EncryptedPkcs8Label}' label nor a "
                    + $"'{LegacyEncryptedPemHeader}' header. A passphrase that decrypts nothing "
                    + "is a control that is not running: remove 'clientKeyPassword', or point "
                    + "'clientKey' at the encrypted key it belongs to.");
            }

            // ── 5. Resolution ─────────────────────────────────────────────────────────────────
            //
            // Ordered AFTER every guard above deliberately: a target refused for any contradiction
            // never resolves its secret at all.
            if (_secrets is null)
            {
                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}.clientKeyPassword: "
                    + $"{QuoteUntrusted(_declaredClientKeyPassword)} is declared, but this run has "
                    + "no secret accessor to resolve it through, so the passphrase cannot be obtained. "
                    + "Refused rather than attempting the key unencrypted: a client identity that "
                    + "silently fails to load presents nothing, which a listener that requests but "
                    + "does not enforce a client certificate accepts.");
            }

            SecretString resolved;
            try
            {
                resolved = _secrets.Resolve(_declaredClientKeyPassword);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                           and not SecurityMaterialException)
            {
                // Wrapped rather than allowed to propagate, deliberately: SecuredEndpointProbe
                // catches SecurityMaterialException ONLY (SecuredEndpointProbe.cs), so an escaping
                // exception would leave the probe's client-identity load by a path that has no
                // environment-error classification of its own — and that path is MEASURED in this
                // repository (SecuredEndpointProbe's own remarks): a stack trace instead of a
                // verdict, with no report artefacts and a non-taxonomy exit code.
                //
                // ANY exception, not only SecretResolutionException, because `_secrets` is the
                // INTERFACE: an ObjectDisposedException from a Vault client, anything
                // HttpVaultKvClient does not wrap, and every third-party ISecretAccessor an embedder
                // supplies all arrive here, and none of them is any better classified than the one
                // shape this catch used to name.
                //
                // TWO EXCLUSIONS, both for the same reason — they are already correctly classified.
                // OperationCanceledException is the run being cancelled and must reach the
                // cancellation plumbing as itself; SecurityMaterialException is this engine's own
                // verdict-bearing type, and re-wrapping one would bury a good diagnostic inside a
                // vaguer one.
                //
                // The subsystem's own message is preserved as the inner exception and quoted
                // through QuoteUntrusted — it is untrusted text from an arbitrary implementor, and
                // it is prose rather than a token, so it is given the wider cap.
                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}.clientKeyPassword: "
                    + $"{QuoteUntrusted(_declaredClientKeyPassword)} could not be resolved "
                    + $"({QuoteUntrusted(ex.Message, limit: ResolverMessageQuoteLimit)}).",
                    ex);
            }

            if (resolved.Reveal().Length == 0)
            {
                // An exported-but-empty environment variable is the ordinary way to reach this.
                // Attempting the load with it would surface the platform's undifferentiated
                // CryptographicException, which this file measures to be identical for an encrypted
                // key, a malformed key and outright garbage.
                throw new SecurityMaterialException(
                    $"{_fieldPathPrefix}.clientKeyPassword: "
                    + $"{QuoteUntrusted(_declaredClientKeyPassword)} resolved to an EMPTY value, "
                    + "which cannot decrypt a private key. Set the referenced secret, or remove "
                    + "'clientKeyPassword' and point 'clientKey' at an unencrypted key.");
            }

            return resolved;
        }

        /// <summary>
        /// Renders untrusted text — a declared secret reference, or a message built from one — safe
        /// to splice into a diagnostic that reaches a terminal, a CI log and the §14 event stream.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Named for what it does rather than for its first caller. It renders ARBITRARY untrusted
        /// text — a declared secret reference, a declared path, an exception message raised by a
        /// third-party resolver — and the previous name (<c>DescribeReference</c>) described only
        /// one of those and read as though the rest were outside its remit.
        /// </para>
        /// <para>
        /// Two properties, both required by HAZARD H3. First, the escaping is TOTAL: nothing that
        /// can forge a line of output, silently reorder one, or break a downstream renderer
        /// survives it — see the four numbered arms of the escaping loop, each pinned by its own
        /// test. Second, the result is CAPPED at <paramref name="limit"/>, because a reference's
        /// path class admits arbitrary length and a diagnostic is not a place to reproduce a
        /// megabyte of it.
        /// </para>
        /// <para>
        /// <strong>It ESCAPES; it does not WITHHOLD.</strong> Whatever it is handed is rendered,
        /// safely and legibly. Deciding that a piece of text must not be reported AT ALL — because
        /// it may be a secret rather than a pointer to one — is a caller's judgement, made before
        /// the call; see <see cref="ResolveClientKeyPassword"/>'s second guard, which exists
        /// precisely because handing a possible passphrase to this method would reproduce it
        /// faithfully.
        /// </para>
        /// <para>
        /// The output is quoted here rather than at each call site, so a reference can never be
        /// spliced in unquoted by an author who forgot the quotes.
        /// </para>
        /// </remarks>
        /// <param name="text">The untrusted text to render.</param>
        /// <param name="limit">
        /// How many characters of <paramref name="text"/> survive. The default,
        /// <see cref="ReferenceQuoteLimit"/>, suits a reference; a nested subsystem message, which
        /// is prose rather than a token, is given <see cref="ResolverMessageQuoteLimit"/>.
        /// </param>
        private static string QuoteUntrusted(string text, int limit = ReferenceQuoteLimit)
        {
            // Every control character named here is written as a C# escape, never as a raw literal
            // byte typed into this source file — the same rule DisplaySanitiser's own tests state.
            const char space = '\u0020';
            const char delete = '\u007f';
            const char c1First = '\u0080';
            const char c1Last = '\u009f';
            const char quote = '\'';

            var truncated = text.Length > limit;

            // NEVER CUT A SURROGATE PAIR IN HALF. `limit` counts UTF-16 code units, and a high
            // surrogate (U+D800–U+DBFF) sits ABOVE the C1 range escaped below, so a lone one
            // would pass through raw into this message — which HAZARD H3 records as reaching the
            // §14 event stream and the JUnit XML renderer, where a lone surrogate throws
            // InvalidOperationException at GetString (see the renderer's own XML-safety
            // handling). Backing off one unit is the whole fix: the low surrogate that would
            // have completed the pair is being dropped anyway, so nothing legible is lost.
            //
            // This back-off covers ONLY the lone surrogate truncation CREATES. One already present
            // in the INPUT is a different case with the same consequence, and arm 3 of the loop
            // below is what covers that.
            var kept = limit;
            if (truncated && kept > 0 && char.IsHighSurrogate(text[kept - 1]))
            {
                kept--;
            }

            var span = truncated ? text.AsSpan(0, kept) : text.AsSpan();

            var builder = new StringBuilder(span.Length + 8);
            builder.Append(quote);

            // AN INDEXED LOOP, not `foreach`, because arm 3 must look AHEAD one unit to tell a
            // legitimate surrogate pair from a lone half.
            for (var index = 0; index < span.Length; index++)
            {
                var character = span[index];

                // ARM 1 — THE DELIMITER ITSELF. This method supplies the surrounding quotes, so an
                // apostrophe inside the text closes them early and renders ONE quoted token as two
                // apparently separate ones. A reader then cannot tell where the untrusted text
                // ended and the engine's own prose resumed, which is the confusion every other arm
                // here exists to prevent — reached with a printable character rather than a
                // control one.
                if (character == quote)
                {
                    AppendEscape(builder, character);
                    continue;
                }

                // ARM 2 — C0, DELETE and C1. Including tab, carriage return and newline, which
                // DisplaySanitiser deliberately preserves and which are exactly what would let a
                // crafted reference forge a line of engine output.
                if (character < space || character == delete || (character >= c1First && character <= c1Last))
                {
                    AppendEscape(builder, character);
                    continue;
                }

                // ARM 3 — SURROGATES ALREADY IN THE INPUT. A well-formed pair passes through whole
                // (an astral character is ordinary text and there is nothing to hide in it); an
                // unpaired half, in EITHER direction, is escaped. A lone surrogate sits above the
                // C1 range, so no earlier arm sees it; it cannot be encoded as UTF-8, and it throws
                // InvalidOperationException at GetString in the JUnit XML renderer — a diagnostic
                // that destroys the report carrying it.
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < span.Length && char.IsLowSurrogate(span[index + 1]))
                    {
                        builder.Append(character).Append(span[index + 1]);
                        index++;
                        continue;
                    }

                    AppendEscape(builder, character);
                    continue;
                }

                if (char.IsLowSurrogate(character))
                {
                    AppendEscape(builder, character);
                    continue;
                }

                // ARM 4 — THE NON-C0 LINE BREAKS AND THE INVISIBLE FORMAT CONTROLS, which sit far
                // above the C1 range and so survive every arm above.
                //
                // U+2028 LINE SEPARATOR is the measured one, and it is YAML-REACHABLE: the schema
                // pattern's path class is `[^}]+`, which admits it. It is a LINE BREAK in HTML —
                // where #371 records HtmlRenderer.HtmlEscape passing control characters straight
                // through — so a reference carrying it forges an apparent line of engine output in
                // the report, which is arm 2's hazard reached by a character arm 2 cannot see.
                // U+2029 is its paragraph-separating sibling.
                //
                // The bidi and format controls (U+202A–U+202E, U+2066–U+2069, U+200E/U+200F)
                // reorder the text a reader SEES without changing the text a machine reads, so a
                // reference can be made to display as something other than what was declared.
                //
                // Stated as CATEGORIES rather than as a list of code points, deliberately: a
                // hand-written enumeration freezes what its author happened to know, and
                // CharUnicodeInfo already carries the answer. Cf, Zl and Zp are exactly the
                // invisible-or-breaking categories not already covered above.
                switch (CharUnicodeInfo.GetUnicodeCategory(character))
                {
                    case UnicodeCategory.Format:
                    case UnicodeCategory.LineSeparator:
                    case UnicodeCategory.ParagraphSeparator:
                        AppendEscape(builder, character);
                        break;
                    default:
                        builder.Append(character);
                        break;
                }
            }

            if (truncated)
            {
                builder.Append("...");
            }

            return builder.Append(quote).ToString();
        }

        /// <summary>
        /// Appends one <c>\uXXXX</c> escape for <paramref name="character"/> — the single spelling
        /// every arm of <see cref="QuoteUntrusted"/>'s loop uses, so no arm can render its own
        /// escape differently from the rest.
        /// </summary>
        private static void AppendEscape(StringBuilder builder, char character) =>
            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));

        /// <summary>
        /// Classifies a private-key file: returns a short phrase naming the encryption marker found
        /// in it, or <see langword="null"/> when the file carries neither marker — with
        /// <paramref name="readable"/> telling the two REASONS for that <see langword="null"/>
        /// apart.
        /// </summary>
        /// <param name="resolvedKeyPath">The absolute path of the declared key file.</param>
        /// <param name="readable">
        /// <see langword="true"/> when the file was opened and its prefix decoded — so a
        /// <see langword="null"/> return means "no marker found"; <see langword="false"/> when it
        /// could not be read at all, so a <see langword="null"/> return means nothing about
        /// encryption either way.
        /// </param>
        /// <remarks>
        /// <para>
        /// Reads a BOUNDED prefix — <see cref="EncryptionScanByteLimit"/> — rather than the whole
        /// file. A 2048-bit encrypted PKCS#8 PEM measures about 1.7 KB and both markers sit in its
        /// first two lines, so the cap is generous by more than thirty times against any realistic
        /// key; and it caps what a mis-declared <c>clientKey</c> pointing at a multi-gigabyte file
        /// can make the engine read while it is already on a failure path. A marker beyond the cap
        /// simply yields the generic message, which is the behaviour without this method at all.
        /// </para>
        /// <para>
        /// <strong>WHAT EACH OF THE TWO CALLERS GETS, because they want different things and the
        /// single-return shape this method used to have served only one of them.</strong> Read
        /// failures are still SWALLOWED rather than thrown — both callers are on a path that must
        /// end in one legible diagnostic, and a second fault raised while classifying would replace
        /// it with an illegible one — but they are now REPORTED, through
        /// <paramref name="readable"/>.
        /// </para>
        /// <para>
        /// <see cref="LoadClient"/>'s catch calls this to IMPROVE a diagnostic it is already about
        /// to throw. It passes <c>out _</c> and is right to: a read failure there simply leaves the
        /// generic message in place, which is the behaviour without this method at all.
        /// </para>
        /// <para>
        /// <see cref="ResolveClientKeyPassword"/> calls it to DECIDE, outside any catch, and there
        /// the return value IS the diagnostic. Conflating the two nulls for that caller reported a
        /// missing, locked or ACL-denied key file as "… is NOT an encrypted private key" — an
        /// accusation about the author's declaration for a fault in the filesystem, and terminal
        /// for a passphrase-only consumer, which has no later load attempt to correct the story.
        /// It therefore checks <paramref name="readable"/> first and raises its own message.
        /// </para>
        /// </remarks>
        private static string? DescribeEncryptedPrivateKey(string resolvedKeyPath, out bool readable)
        {
            readable = false;
            string prefix;
            try
            {
                var stream = new FileStream(
                    resolvedKeyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                try
                {
                    var buffer = new byte[EncryptionScanByteLimit];
                    var read = 0;
                    int chunk;
                    while (read < buffer.Length &&
                           (chunk = stream.Read(buffer, read, buffer.Length - read)) > 0)
                    {
                        read += chunk;
                    }

                    // PEM is US-ASCII by definition (RFC 7468 §3), so a byte-wise decode cannot
                    // corrupt either marker, and a truncated multi-byte sequence at the cap
                    // boundary cannot throw the way a strict UTF-8 decode would.
                    prefix = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                }
                finally
                {
                    stream.Dispose();
                }
            }
            catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException
                                           or ArgumentException)
            {
                return null;
            }

            readable = true;

            if (prefix.Contains(EncryptedPkcs8Label, StringComparison.Ordinal))
            {
                return $"its PEM block is labelled '{EncryptedPkcs8Label}'";
            }

            if (prefix.Contains(LegacyEncryptedPemHeader, StringComparison.Ordinal))
            {
                return $"it carries the '{LegacyEncryptedPemHeader}' header";
            }

            return null;
        }

        /// <summary>
        /// The PEM label .NET's own <c>RSA.ExportEncryptedPkcs8PrivateKeyPem</c> and
        /// <c>openssl pkcs8 -topk8</c> both write for a password-protected key — measured, on the
        /// pinned runtime and on the openssl on this host.
        /// </summary>
        private const string EncryptedPkcs8Label = "-----BEGIN ENCRYPTED PRIVATE KEY-----";

        /// <summary>
        /// The header the legacy openssl form (<c>openssl rsa -aes256 -traditional</c>) writes
        /// instead: the block keeps the ordinary <c>-----BEGIN RSA PRIVATE KEY-----</c> label, so
        /// the label alone does not identify it — measured.
        /// </summary>
        private const string LegacyEncryptedPemHeader = "Proc-Type: 4,ENCRYPTED";

        /// <summary>
        /// How much of a failed key file is read when classifying it. See
        /// <see cref="DescribeEncryptedPrivateKey"/>'s own remarks for why it is bounded and why
        /// the bound is safe.
        /// </summary>
        private const int EncryptionScanByteLimit = 64 * 1024;

        /// <summary>
        /// How many characters of a quoted secret REFERENCE survive
        /// <see cref="QuoteUntrusted"/>'s cap. A reference is a token, and enough of one to
        /// recognise is the whole reason to quote it at all.
        /// </summary>
        private const int ReferenceQuoteLimit = 120;

        /// <summary>
        /// How many characters of a quoted RESOLVER MESSAGE survive
        /// <see cref="QuoteUntrusted"/>'s cap — wider than
        /// <see cref="ReferenceQuoteLimit"/> because a nested subsystem message is prose rather
        /// than a token, and truncating it at a token's length would cut it mid-sentence.
        /// </summary>
        /// <remarks>
        /// Named rather than written at its one call site so that both caps are pinnable by test.
        /// The default was; this one was not, which left the wider of the two — the one applied to
        /// text from an arbitrary third-party <c>ISecretAccessor</c> — as the only unpinned
        /// bound on this class's output.
        /// </remarks>
        private const int ResolverMessageQuoteLimit = 512;
    }
}
