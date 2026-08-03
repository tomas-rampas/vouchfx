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
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    internal static ISecurityConfigurationAccessor Build(ScenarioAst ast, string? suiteDirectory)
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
                    byTarget[name] = Project(name, "services", security, resolvedSuiteDirectory);
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

                byTarget[name] = Project(name, "dependencies", security, resolvedSuiteDirectory);
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
        string targetName, string ownerKindPlural, SecuritySpec security, string resolvedSuiteDirectory)
    {
        var fieldPathPrefix = $"environment.{ownerKindPlural}.{targetName}.security";

        var ca = DeclaredPath.From(security.CaCert, resolvedSuiteDirectory);
        var clientCert = DeclaredPath.From(security.ClientCert, resolvedSuiteDirectory);
        var clientKey = DeclaredPath.From(security.ClientKey, resolvedSuiteDirectory);

        // No path-valued certificate field declared at all → no certificate VIEW, rather than
        // an empty one. REQ-001/REQ-024: an absent caCert is a normal configuration in which
        // the platform's own trust store applies, and the engine must synthesise nothing.
        var certificates = ca is null && clientCert is null && clientKey is null
            ? null
            : new SecurityCertificateMaterial(
                fieldPathPrefix, resolvedSuiteDirectory, ca, clientCert, clientKey);

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
        private readonly Lazy<X509Certificate2?> _caCertificate;
        private readonly Lazy<X509Certificate2?> _clientCertificate;

        internal SecurityCertificateMaterial(
            string fieldPathPrefix,
            string resolvedSuiteDirectory,
            DeclaredPath? ca,
            DeclaredPath? clientCert,
            DeclaredPath? clientKey)
        {
            _fieldPathPrefix = fieldPathPrefix;
            _resolvedSuiteDirectory = resolvedSuiteDirectory;
            _ca = ca;
            _clientCert = clientCert;
            _clientKey = clientKey;

            // ExecutionAndPublication: one load per target however many steps resolve it
            // concurrently under `--parallel`, and the SAME instance to all of them.
            _caCertificate = new Lazy<X509Certificate2?>(LoadCa, LazyThreadSafetyMode.ExecutionAndPublication);
            _clientCertificate = new Lazy<X509Certificate2?>(
                LoadClient, LazyThreadSafetyMode.ExecutionAndPublication);
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

        public bool TrustsRemoteCertificate(
            X509Certificate2? remoteCertificate, X509Chain? peerSuppliedChain, SslPolicyErrors sslPolicyErrors)
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

                // Peer-supplied intermediates, for path building ONLY. A two-tier PKI (offline
                // root declared as `caCert`, issuing intermediate sent by the server) is the
                // normal enterprise shape and cannot validate without them — measured as a flat
                // `false`, whose EnvironmentError then pushes an author towards declaring the
                // intermediate as their anchor or dropping `caCert` altogether, both of which
                // weaken their setup. These certificates are UNTRUSTED input and never become
                // anchors: CustomRootTrust means the chain terminates only at the declared CA in
                // CustomTrustStore, so a self-signed root supplied here as an "intermediate" is
                // still rejected (measured, and pinned by test).
                if (peerSuppliedChain is not null)
                {
                    foreach (var element in peerSuppliedChain.ChainElements)
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
            // through a provider's general catch, where no scrubber can redact a host path.
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

            X509Certificate2? pemPair = null;
            try
            {
                pemPair = X509Certificate2.CreateFromPemFile(_clientCert.Resolved, _clientKey.Resolved);

                // The PKCS#12 round trip is NOT ceremony. MEASURED on this repo's Windows host
                // against the mutual-TLS test bed: a certificate produced directly by
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
                    // is accepted, not traded for a certificate that cannot authenticate.
                    return new X509Certificate2(pkcs12, password, X509KeyStorageFlags.DefaultKeySet);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs12);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
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
    }
}
