// S08-B-01 — VaultSecretSource Docker-gated end-to-end resolution tests.
//
// Proves the headline acceptance for the Vault secret source against a REAL HashiCorp
// Vault dev-mode container (hashicorp/vault), started via HeadlessTopology (the AppHost
// fixture with Aspire.AppHost.Sdk, mirroring DbAssertPostgresDockerTests):
//   1. a credential WRITTEN to Vault's KV v2 store resolves end-to-end through
//      VaultSecretResolver → HttpVaultKvClient at EXECUTION TIME (§17);
//   2. redaction holds — the resolved SecretString never exposes its value via
//      ToString()/serialisation, only via the auditable Reveal();
//   3. the reproducibility envelope digest is the SHA-256 of the REFERENCE token,
//      never the resolved value (§17).
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~Vault"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
//
// Vault dev mode (the container's default entrypoint when VAULT_DEV_ROOT_TOKEN_ID is
// set) starts an in-memory, unsealed server with KV v2 mounted at "secret/" and the
// supplied root token — exactly the fixed-token dev container the task brief calls for.
// IPC_LOCK is granted so Vault can mlock; dev mode listens on 0.0.0.0:8200.
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using Platform.Engine.Abstractions.Reproducibility;
using Platform.Engine.Abstractions.Secrets;
using Platform.Engine.Abstractions.Secrets.Vault;
using Xunit;
using Xunit.Abstractions;

namespace Platform.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end resolution tests for the <c>vault</c> secret source
/// (S08-B-01).  Requires a running Docker daemon.
/// </summary>
/// <remarks>
/// Every method carries <c>[Trait("requires","docker")]</c> so the class is excluded
/// from the non-Docker CI filter.  The test assembly carries
/// <c>&lt;IsAspireHost&gt;true&lt;/IsAspireHost&gt;</c> + <c>Aspire.AppHost.Sdk</c>,
/// which embed the <c>dcpclipath</c> metadata required by
/// <see cref="HeadlessTopology.StartAsync"/>.
/// </remarks>
public sealed class VaultSecretSourceDockerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Short name of this test assembly (carries DCP metadata).</summary>
    private const string AppHostAssemblyName = "Platform.Engine.Orchestration.Tests";

    /// <summary>Generous startup timeout: allows the image pull on first run.</summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Logical Aspire resource name for the Vault container.</summary>
    private const string VaultResource = "vault";

    /// <summary>The fixed dev-mode root token (a test fixture value, never a real secret).</summary>
    private const string RootToken = "vouchfx-vault-dev-root-token";

    public VaultSecretSourceDockerTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Independently computes lower-case hex SHA-256 of a string's UTF-8 bytes so the
    /// test does not merely echo the implementation's own helper.
    /// </summary>
    private static string Sha256Hex(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Starts a single Vault dev-mode container and returns the started topology plus
    /// the discovered base URL.  Dev mode mounts KV v2 at <c>secret/</c> with the fixed
    /// root token.
    /// </summary>
    private static async Task<(HeadlessTopology Topology, Uri Address)> StartVaultAsync(
        CancellationToken ct)
    {
        EndpointReference? endpoint = null;

        var topology = await HeadlessTopology.StartAsync(
            appHostAssemblyName: AppHostAssemblyName,
            configureResources: builder =>
            {
                var vault = builder
                    .AddContainer(VaultResource, "hashicorp/vault")
                    // Dev mode with a FIXED root token (the task brief's requirement) and an
                    // explicit dev listen address so the mapped 8200 endpoint serves HTTP.
                    .WithEnvironment("VAULT_DEV_ROOT_TOKEN_ID", RootToken)
                    .WithEnvironment("VAULT_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
                    .WithHttpEndpoint(targetPort: 8200, name: "http")
                    // Vault's own health endpoint reports 200 only once the dev server is
                    // initialised, unsealed and active — so the gate below waits for an
                    // actually-serving Vault, not merely a Running container.
                    .WithHttpHealthCheck(path: "/v1/sys/health", endpointName: "http");

                endpoint = vault.GetEndpoint("http");
            },
            cancellationToken: ct).ConfigureAwait(false);

        // Health-gate the container so its HTTP port is actually serving before we write.
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        gateCts.CancelAfter(StartupTimeout);
        await topology.Application.ResourceNotifications
            .WaitForResourceHealthyAsync(
                VaultResource, WaitBehavior.StopOnResourceUnavailable, gateCts.Token)
            .ConfigureAwait(false);

        var address = new Uri(endpoint!.Url, UriKind.Absolute);
        return (topology, address);
    }

    /// <summary>
    /// Writes a KV v2 secret (a field map) at <paramref name="kvPath"/> via Vault's
    /// HTTP API: <c>POST {address}/v1/secret/data/{path}</c> with <c>{"data": {...}}</c>.
    /// </summary>
    private static async Task WriteKvSecretAsync(
        Uri address,
        string kvPath,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken ct)
    {
        using var client = new HttpClient { BaseAddress = address };
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/secret/data/{kvPath}")
        {
            Content = JsonContent.Create(new { data = fields }),
        };
        request.Headers.TryAddWithoutValidation("X-Vault-Token", RootToken);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A credential written to Vault KV v2 resolves end-to-end through the
    /// <see cref="VaultSecretResolver"/> at execution time; redaction holds; and the
    /// reproducibility envelope digest is the hash of the REFERENCE, not the value.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Resolve_RealVaultDevContainer_ResolvesValueRedactsAndHashesReference()
    {
        var ct = CancellationToken.None;

        const string kvPath = "myapp/db";
        const string field = "password";
        const string secretValue = "S3cr3t-Vault-Value-must-never-leak-9f1c";

        var (topology, address) = await StartVaultAsync(ct);
        await using (topology)
        {
            _output.WriteLine($"Vault dev container address: {address}");

            await WriteKvSecretAsync(
                address,
                kvPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [field] = secretValue,
                    ["user"] = "svc-account",
                },
                ct);
            _output.WriteLine($"Wrote KV v2 secret at secret/{kvPath}.");

            // ── 1. Resolve end-to-end at EXECUTION TIME (§17) ─────────────────
            // Configuration carries the address + token as a reference/config — never a
            // YAML literal.  Mirror how the runtime wires it (HttpVaultKvClient over the
            // HttpVaultKvClient → VaultSecretResolver chain).
            var config = new VaultSecretConfiguration(
                address, RootToken, VaultSecretConfiguration.DefaultMount);
            using var httpClient = new HttpClient();
            var kvClient = new HttpVaultKvClient(httpClient, config);
            using var resolver = new VaultSecretResolver(kvClient);

            // The PATH segment of ${secret:vault/<path>#<field>} is "myapp/db#password".
            var referencePath = $"{kvPath}#{field}";
            var secret = resolver.Resolve(referencePath);

            Assert.Equal(secretValue, secret.Reveal());

            // ── 2. Redaction holds (§17) ──────────────────────────────────────
            Assert.Equal(SecretString.RedactedMarker, secret.ToString());
            Assert.DoesNotContain(secretValue, secret.ToString(), StringComparison.Ordinal);
            var secretJson = System.Text.Json.JsonSerializer.Serialize(secret);
            Assert.DoesNotContain(secretValue, secretJson, StringComparison.Ordinal);

            // ── 3. Reproducibility envelope hashes the REFERENCE, not the value ─
            var rawToken = $"${{secret:vault/{referencePath}}}";
            Assert.True(SecretReference.TryParse(rawToken, out var reference));
            Assert.NotNull(reference);
            Assert.Equal("vault", reference!.Source);

            var envelope = ReproducibilityEnvelope.Compute(
                new[] { reference }, Array.Empty<FixtureDigest>());

            var digest = Assert.Single(envelope.SecretReferences);
            Assert.Equal("vault", digest.Source);
            // The digest is SHA-256 of the verbatim reference TOKEN.
            Assert.Equal(Sha256Hex(rawToken), digest.ReferenceHash);
            // And — the headline guarantee — the resolved value appears NOWHERE.
            var envelopeJson = System.Text.Json.JsonSerializer.Serialize(envelope);
            Assert.DoesNotContain(secretValue, envelopeJson, StringComparison.Ordinal);
            Assert.DoesNotContain(RootToken, envelopeJson, StringComparison.Ordinal);

            _output.WriteLine(
                "VAULT PASS: value resolved from a real Vault container at execution time; " +
                "SecretString redacted everywhere; envelope carries the reference hash, " +
                "not the value or the token.");
        }
    }

    /// <summary>
    /// A reference to a field that does not exist at the KV path resolves to a
    /// <see cref="SecretResolutionException"/> — an environment/configuration problem
    /// (§12.1) — naming the field but never any value or the Vault token (§17).
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Resolve_MissingFieldAgainstRealVault_ThrowsWithoutLeaking()
    {
        var ct = CancellationToken.None;
        const string kvPath = "myapp/api";
        const string presentValue = "present-value-must-not-leak";

        var (topology, address) = await StartVaultAsync(ct);
        await using (topology)
        {
            await WriteKvSecretAsync(
                address,
                kvPath,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["token"] = presentValue,
                },
                ct);

            var config = new VaultSecretConfiguration(
                address, RootToken, VaultSecretConfiguration.DefaultMount);
            using var httpClient = new HttpClient();
            using var resolver = new VaultSecretResolver(new HttpVaultKvClient(httpClient, config));

            var ex = Assert.Throws<SecretResolutionException>(
                () => resolver.Resolve($"{kvPath}#absent-field"));

            Assert.Equal("vault", ex.SecretSource);
            Assert.Contains("absent-field", ex.Message, StringComparison.Ordinal);
            // Neither the present value nor the token may appear in the diagnostic.
            Assert.DoesNotContain(presentValue, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(RootToken, ex.Message, StringComparison.Ordinal);
        }
    }
}
