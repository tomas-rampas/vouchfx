// StorageAssertS3Provider Docker-gated end-to-end execution tests against the "s3" dependency
// kind (#581), which EnvironmentMapper backs with RustFS (rustfs/rustfs 1.0.0, pinned by index
// digest). The scenarios are StorageAssertS3DockerTestsBase's, shared with the "minio" kind so
// both backends answer the same questions — see StorageAssertS3DockerTests.cs for the list and
// for the S3 operation each scenario drives.
//
// One scenario is specific to this kind: RustFS also reads alias credential variables
// (MINIO_ROOT_USER/MINIO_ROOT_PASSWORD, RUSTFS_ROOT_USER/RUSTFS_ROOT_PASSWORD,
// MINIO_ACCESS_KEY/MINIO_SECRET_KEY). None of them is in the engine's reserved set, so an author
// can set them through env:, and the scenario pins that doing so does not displace the engine's
// RUSTFS_ACCESS_KEY/RUSTFS_SECRET_KEY pair — the credentials in the connection string every
// scenario's steps receive. A pin moved to a RustFS build with a different alias precedence turns
// it red.
//
// Run with:  dotnet test --filter "requires=docker&FullyQualifiedName~StorageAssertS3Docker"
// Excluded from non-Docker CI: dotnet test --filter "requires!=docker"
using Amazon.Runtime;
using Amazon.S3;
using Vouchfx.Engine.Abstractions;
using Vouchfx.Sdk;
using Vouchfx.Steps.StorageAssert.S3;
using Xunit;
using Xunit.Abstractions;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Docker-gated end-to-end execution tests for <c>storage-assert.s3</c> against the <c>s3</c>
/// dependency kind. Requires a running Docker daemon able to pull <c>rustfs/rustfs</c> by the
/// digest <c>EnvironmentMapper.S3RustfsImageDigest</c> names.
/// </summary>
public sealed class StorageAssertS3DockerS3KindTests : StorageAssertS3DockerTestsBase
{
    public StorageAssertS3DockerS3KindTests(ITestOutputHelper output)
        : base(output)
    {
    }

    /// <inheritdoc />
    protected override string DependencyType => "s3";

    /// <inheritdoc />
    protected override string DependencyName => "tests3";

    /// <summary>
    /// Alias credential variables set through the dependency's <c>env:</c> neither replace nor
    /// join the engine's credentials: the step still passes with the engine's connection string,
    /// and a client signing with any alias pair is refused.
    /// </summary>
    [Fact]
    [Trait("requires", "docker")]
    public async Task Execute_AliasCredentialVariablesInEnv_DoNotDisplaceEngineCredentials()
    {
        var aliasPairs = new (string UserVar, string SecretVar, string User, string Secret)[]
        {
            ("MINIO_ROOT_USER", "MINIO_ROOT_PASSWORD", "alias-minio-root", "alias-minio-root-secret"),
            ("RUSTFS_ROOT_USER", "RUSTFS_ROOT_PASSWORD", "alias-rustfs-root", "alias-rustfs-root-secret"),
            ("MINIO_ACCESS_KEY", "MINIO_SECRET_KEY", "alias-minio-access", "alias-minio-access-secret"),
        };

        var dependencyEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (userVar, secretVar, user, secret) in aliasPairs)
        {
            dependencyEnv[userVar] = user;
            dependencyEnv[secretVar] = secret;
        }

        var env = BuildEnv(dependencyEnv);
        await using var suite = await SuiteTopology.StartAsync(
            environment: env, appHostAssemblyName: AppHostAssemblyName, startupTimeout: StartupTimeout);

        var connStr = suite.DiscoveredServices[DependencyName] as string;
        Assert.False(string.IsNullOrWhiteSpace(connStr));

        // The engine's credentials still authenticate: the fixture writes with them.
        var client = BuildClient(connStr!);
        try
        {
            await EnsureBucketAsync(client);
            await PutObjectAsync(client, "exports/alias-check.csv", "alias-check");
        }
        finally
        {
            client.Dispose();
        }

        var model = new StorageAssertS3Model(
            DependencyName, BucketName, "exports/alias-check.csv",
            new StorageExpectation(true, null, null, null, null, null, null));

        var vars = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VarKeys.Connection(DependencyName)] = connStr,
        };

        var outcome = await RunStepAsync(model, "assert-s3-alias-credentials", vars);

        Output.WriteLine($"Verdict: {outcome.Verdict}, Observation: {outcome.Observation}");
        Assert.Equal(Verdict.Pass, outcome.Verdict);

        // No alias pair became a second valid credential either.
        var (serviceUrl, _, _) = ParseConnectionString(connStr!);
        foreach (var (_, _, user, secret) in aliasPairs)
        {
            var aliasClient = new AmazonS3Client(
                new BasicAWSCredentials(user, secret),
                new AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true, MaxErrorRetry = 0 });
            try
            {
                var refused = await Assert.ThrowsAsync<AmazonS3Exception>(
                    () => aliasClient.ListBucketsAsync());
                Output.WriteLine($"{user}: {(int)refused.StatusCode} {refused.ErrorCode}");
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.StatusCode);
            }
            finally
            {
                aliasClient.Dispose();
            }
        }
    }
}
