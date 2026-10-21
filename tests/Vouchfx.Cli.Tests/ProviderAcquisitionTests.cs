using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using Vouchfx.Cli;
using Vouchfx.Cli.Providers;
using Vouchfx.Sdk;
using Vouchfx.TestSupport;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ProviderAcquisitionTests
{
    private const string Provider = """
        using Vouchfx.Sdk;
        namespace Community.AcquisitionFixture;
        [StepProvider] public sealed class Probe : IStepProvider {
            public StepKindId Kind => new("acquisition", "fixture");
            public ProviderMetadata Metadata => new("1.0.0", "1.0.0", "Apache-2.0", new[] { "test" });
        }
        """;
    private static string Temp() => ProviderDirectoryTests.Temp();
    private static string Unique() => "Acquisition" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Local_feed_resolves_transitive_assets_prunes_host_and_never_executes_provider_code()
    {
        var feed = Temp(); var root = Temp(); var build = Temp(); var id = Unique();
        var leafId = Unique(); var middleId = Unique(); var sdkId = Unique();
        var leaf = Compile(build, leafId, "public class PrivateLeaf { }");
        var middle = Compile(build, middleId, "public class PrivateMiddle : PrivateLeaf { }", leaf);
        var provider = Compile(build, id, Provider.Replace(" : IStepProvider", " : PrivateMiddle, IStepProvider"), middle, leaf);
        Package(feed, leafId, "1.0.0", leaf);
        Package(feed, middleId, "1.0.0", middle, new[] { Dependency(leafId, "[1.0.0]") });
        Package(feed, sdkId, "1.0.0", typeof(IStepProvider).Assembly.Location);
        Package(feed, id, "1.0.0", provider, new[] { Dependency(middleId, "[1.0.0]"), Dependency(sdkId, "[1.0.0]") });
        var store = new ProviderStore(root);
        var installed = await store.InstallAsync(id, null, feed, CancellationToken.None);
        Assert.Equal("1.0.0", installed.Version);
        Assert.True(File.Exists(Path.Combine(installed.Directory, Path.GetFileName(leaf))));
        Assert.True(File.Exists(Path.Combine(installed.Directory, Path.GetFileName(middle))));
        Assert.False(File.Exists(Path.Combine(installed.Directory, "Vouchfx.Sdk.dll")));
        Assert.Equal(4, installed.PackageSha256!.Count);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => !assembly.IsDynamic && assembly.GetName().Name == id);
        Assert.Equal(id, Assert.Single(store.List()).Id);
        Assert.True(store.Uninstall(id));
        Assert.Empty(store.List());
        Assert.False(store.Uninstall(id));
    }

    [Fact]
    public async Task Dependency_solver_backtracks_to_satisfy_shared_ranges_and_selects_net8_assets()
    {
        var feed = Temp(); var build = Temp(); var id = Unique(); var a = Unique(); var b = Unique(); var shared = Unique();
        Package(feed, shared, "1.0.0", Compile(Temp(), shared, "public class SharedV1 { }"));
        Package(feed, shared, "2.0.0", Compile(Temp(), shared, "public class SharedV2 { }"));
        Package(feed, a, "1.0.0", Compile(Temp(), a, "public class A1 { }"), new[] { Dependency(shared, "[1.0.0]") });
        Package(feed, a, "2.0.0", Compile(Temp(), a, "public class A2 { }"), new[] { Dependency(shared, "[2.0.0]") });
        Package(feed, b, "1.0.0", Compile(build, b, "public class B { }"), new[] { Dependency(shared, "[2.0.0]") });
        var provider = Compile(build, id, Provider);
        Package(feed, id, "1.0.0", provider, new[] { Dependency(a, "[1.0.0,3.0.0)"), Dependency(b, "[1.0.0]") },
            extra: new[] { (Compile(Temp(), "WrongFramework", "public class Wrong { }"), "lib/netstandard2.0/WrongFramework.dll") });
        var installed = await new ProviderStore(Temp()).InstallAsync(id, null, feed, CancellationToken.None);
        Assert.Contains(installed.PackageSha256!.Keys, key => key.Contains(a + ".2.0.0", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(installed.Directory, "WrongFramework.dll")));
    }

    [Fact]
    public async Task Latest_stable_and_explicit_prerelease_are_selected_predictably()
    {
        var feed = Temp(); var id = Unique(); var provider = Compile(Temp(), id, Provider);
        Package(feed, id, "1.0.0", provider); Package(feed, id, "2.0.0-beta", provider);
        var store = new ProviderStore(Temp());
        Assert.Equal("1.0.0", (await store.InstallAsync(id, null, feed, CancellationToken.None)).Version);
        Assert.Equal("2.0.0-beta", (await store.InstallAsync(id, "2.0.0-beta", feed, CancellationToken.None)).Version);
    }

    [Fact]
    public async Task Incompatible_dependency_framework_is_skipped_when_a_compatible_version_satisfies_the_range()
    {
        var feed = Temp(); var id = Unique(); var dependency = Unique();
        Package(feed, dependency, "1.0.0", Compile(Temp(), dependency, "public class Net9Only { }"), framework: "net9.0");
        Package(feed, dependency, "2.0.0", Compile(Temp(), dependency, "public class Net8Compatible { }"));
        Package(feed, id, "1.0.0", Compile(Temp(), id, Provider), new[] { Dependency(dependency, "[1.0.0,3.0.0)") });
        var installed = await new ProviderStore(Temp()).InstallAsync(id, null, feed, CancellationToken.None);
        Assert.Contains(installed.PackageSha256!.Keys, key => key.Contains(dependency + ".2.0.0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Failed_promotion_restores_previous_version_and_removes_transaction_files()
    {
        var feed = Temp(); var id = Unique(); var provider = Compile(Temp(), id, Provider); var root = Temp();
        Package(feed, id, "1.0.0", provider); Package(feed, id, "2.0.0", provider);
        var store = new ProviderStore(root);
        var before = await store.InstallAsync(id, "1.0.0", feed, CancellationToken.None);
        var bytes = File.ReadAllBytes(Path.Combine(before.Directory, "provider-install.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(id, "2.0.0", feed, CancellationToken.None,
            beforePromotion: () => throw new InvalidOperationException("synthetic promotion failure")));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(before.Directory, "provider-install.json")));
        Assert.Empty(Directory.GetDirectories(Transactions(root)));
    }

    [Fact]
    public async Task Concurrent_scan_refuses_the_commit_gap_and_never_discovers_staging_or_backups()
    {
        var feed = Temp(); var id = Unique(); var root = Temp(); var binary = Compile(Temp(), id, Provider);
        Package(feed, id, "1.0.0", binary); Package(feed, id, "2.0.0", binary);
        var store = new ProviderStore(root); await store.InstallAsync(id, "1.0.0", feed, CancellationToken.None);
        await store.InstallAsync(id, "2.0.0", feed, CancellationToken.None, beforePromotion: () =>
        {
            Assert.Empty(Directory.GetDirectories(root));
            Assert.Contains("being updated", Assert.Throws<ProviderLoadException>(() => ProviderDirectoryScanner.Load(new[] { root })).Message);
            var transaction = Assert.Single(Directory.GetDirectories(Transactions(root)));
            Assert.True(Directory.Exists(Path.Combine(transaction, "stage")));
            Assert.True(Directory.Exists(Path.Combine(transaction, "backup")));
        });
        Assert.Single(ProviderDirectoryScanner.Load(new[] { root }));
        Assert.Empty(Directory.GetDirectories(Transactions(root)));
    }

    [Fact]
    public void Unmanaged_scan_does_not_create_lock_files_or_management_directories()
    {
        var parent = Temp(); var root = Directory.CreateDirectory(Path.Combine(parent, "read-only-provider")).FullName;
        Compile(root, Unique(), Provider);
        var before = Directory.GetFileSystemEntries(parent).Order().ToArray();
        Assert.Single(ProviderDirectoryScanner.Load(new[] { root }));
        Assert.Equal(before, Directory.GetFileSystemEntries(parent).Order().ToArray());
        Assert.False(File.Exists(Path.Combine(parent, ".read-only-provider.lock")));
    }

    [Fact]
    public async Task Conflicting_private_dependency_refuses_update_without_changing_active_provider()
    {
        var feed = Temp(); var id = Unique(); var dependencyId = Unique(); var root = Temp();
        var dependency = Compile(Temp(), dependencyId, "[assembly:System.Reflection.AssemblyVersion(\"1.0.0.0\")] public class RequiredBase { }");
        Package(feed, dependencyId, "2.0.0", Compile(Temp(), dependencyId, "[assembly:System.Reflection.AssemblyVersion(\"2.0.0.0\")] public class RequiredBase { }"));
        Package(feed, id, "1.0.0", Compile(Temp(), id, Provider));
        Package(feed, id, "2.0.0", Compile(Temp(), id, Provider.Replace(" : IStepProvider", " : RequiredBase, IStepProvider"), dependency), new[] { Dependency(dependencyId, "[2.0.0]") });
        var store = new ProviderStore(root);
        await store.InstallAsync(id, "1.0.0", feed, CancellationToken.None);
        await Assert.ThrowsAsync<ProviderLoadException>(() => store.InstallAsync(id, "2.0.0", feed, CancellationToken.None));
        Assert.Equal("1.0.0", Assert.Single(store.List()).Version);
        Assert.Empty(Directory.GetDirectories(Transactions(root)));
    }

    [Fact]
    public async Task Cancelled_transaction_preserves_active_provider()
    {
        var feed = Temp(); var id = Unique(); var provider = Compile(Temp(), id, Provider); var root = Temp();
        Package(feed, id, "1.0.0", provider); Package(feed, id, "2.0.0", provider);
        var store = new ProviderStore(root); await store.InstallAsync(id, "1.0.0", feed, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallAsync(id, "2.0.0", feed, cancellation.Token,
            beforePromotion: cancellation.Cancel));
        Assert.Equal("1.0.0", Assert.Single(store.List()).Version);
    }

    [Fact]
    public async Task Blocked_rollback_preserves_the_last_good_backup_and_never_deletes_a_foreign_destination()
    {
        var feed = Temp(); var id = Unique(); var root = Temp(); var binary = Compile(Temp(), id, Provider);
        Package(feed, id, "1.0.0", binary); Package(feed, id, "2.0.0", binary);
        var store = new ProviderStore(root); var old = await store.InstallAsync(id, "1.0.0", feed, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(id, "2.0.0", feed, CancellationToken.None,
            beforePromotion: () =>
            {
                Directory.CreateDirectory(old.Directory);
                File.WriteAllText(Path.Combine(old.Directory, "foreign.txt"), "keep");
                throw new InvalidOperationException("promotion blocked");
            }));
        var transaction = Assert.Single(Directory.GetDirectories(Transactions(root)));
        Assert.True(File.Exists(Path.Combine(transaction, "backup", "provider-install.json")));
        Assert.Throws<ProviderLoadException>(() => store.Uninstall("not-installed"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(old.Directory, "foreign.txt")));
        Assert.True(File.Exists(Path.Combine(transaction, "backup", "provider-install.json")));
    }

    [Fact]
    public async Task Interrupted_backup_is_recovered_before_the_next_operation()
    {
        var feed = Temp(); var id = Unique(); var root = Temp(); Package(feed, id, "1.0.0", Compile(Temp(), id, Provider));
        var store = new ProviderStore(root); var installed = await store.InstallAsync(id, null, feed, CancellationToken.None);
        var transaction = Directory.CreateDirectory(Path.Combine(Transactions(root), Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(transaction, "transaction.id"), id.ToLowerInvariant());
        Directory.Move(installed.Directory, Path.Combine(transaction, "backup"));
        Assert.False(store.Uninstall("not-installed"));
        Assert.Equal(id, Assert.Single(store.List()).Id);
        Assert.Empty(Directory.GetDirectories(Transactions(root)));
    }

    [Fact]
    public async Task Registry_alias_install_and_offline_listing_keep_the_installed_catalogue()
    {
        var feed = Temp(); var id = Unique(); var root = Temp();
        Package(feed, id, "1.0.0", Compile(Temp(), id, Provider));
        var store = new ProviderStore(root);
        var installed = await store.InstallAsync("rpc.fixture", null, feed, CancellationToken.None,
            registry: _ => Task.FromResult<IReadOnlyList<ProviderRegistryEntry>>(new[] { new ProviderRegistryEntry("rpc.fixture", id, null) }));
        Assert.Equal(id, installed.Id);
        var listing = await ProvidersCommand.ReadListAsync(root, false,
            _ => throw new HttpRequestException("offline-canary"), CancellationToken.None);
        Assert.Equal(id, Assert.Single(listing.Installed).Id);
        Assert.Empty(listing.Available);
        Assert.Contains("unavailable", listing.Note);
        Assert.DoesNotContain("offline-canary", listing.Note);
    }

    [Fact]
    public async Task A_feed_package_cannot_shadow_a_registered_step_kind_alias()
    {
        var feed = Temp(); var id = Unique(); var root = Temp(); var binary = Compile(Temp(), id, Provider);
        Package(feed, "rpc.fixture", "1.0.0", binary);
        Package(feed, id, "1.0.0", binary);
        var installed = await new ProviderStore(root).InstallAsync("rpc.fixture", null, feed, CancellationToken.None,
            registry: _ => Task.FromResult<IReadOnlyList<ProviderRegistryEntry>>(new[] { new ProviderRegistryEntry("rpc.fixture", id, null) }));
        Assert.Equal(id, installed.Id);
        Assert.False(Directory.Exists(Path.Combine(root, "rpc.fixture")));
        Assert.Equal(0, (await Cli("providers", "install", "rpc.fixture", "--package-id", "--source", feed, "--directory", Temp())).Exit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dependency_solver_skips_host_incompatible_versions(bool referenceConflict)
    {
        var feed = Temp(); var id = Unique(); var dependency = Unique();
        var host = typeof(Json.Path.JsonPath).Assembly.Location;
        var hostName = System.Reflection.AssemblyName.GetAssemblyName(host).Name!;
        var wrongHost = Compile(Temp(), hostName,
            "[assembly: System.Reflection.AssemblyVersion(\"99.0.0.0\")] public class WrongHostCopy { }");
        var lower = referenceConflict
            ? Compile(Temp(), dependency, "public class PrivateDependency : WrongHostCopy { }", wrongHost)
            : wrongHost;
        var higher = referenceConflict ? Compile(Temp(), dependency, "public class PrivateDependency { }") : host;
        Package(feed, dependency, "1.0.0", lower);
        Package(feed, dependency, "2.0.0", higher);
        Package(feed, id, "1.0.0", Compile(Temp(), id, Provider), new[] { Dependency(dependency, "[1.0.0,3.0.0)") });
        var root = Temp(); var store = new ProviderStore(root);
        var installed = await store.InstallAsync(id, null, feed, CancellationToken.None);
        Assert.Contains(dependency + ".2.0.0", installed.PackageSha256!.Keys);
        Assert.DoesNotContain(dependency + ".1.0.0", installed.PackageSha256.Keys);
        Assert.False(File.Exists(Path.Combine(installed.Directory, hostName + ".dll")));
        Assert.Equal(id, Assert.Single(store.List()).Id);
        Assert.Empty(Directory.GetFileSystemEntries(Transactions(root)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_host_compatible_graph_preserves_installation_and_cleans_transaction(bool existingInstallation)
    {
        var feed = Temp(); var id = Unique(); var dependency = Unique(); var root = Temp();
        var store = new ProviderStore(root);
        var provider = Compile(Temp(), id, Provider);
        if (existingInstallation)
        {
            Package(feed, id, "1.0.0", provider);
            await store.InstallAsync(id, "1.0.0", feed, CancellationToken.None);
        }
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes);
        Package(feed, dependency, "1.0.0", Compile(Temp(), "JsonPath.Net", "public class WrongHostCopy { }"));
        Package(feed, dependency, "2.0.0", Compile(Temp(), "JsonPath.Net", "public class AnotherWrongHostCopy { }"));
        Package(feed, id, "2.0.0", provider, new[] { Dependency(dependency, "[1.0.0,3.0.0)") });
        await Assert.ThrowsAsync<ProviderLoadException>(() => store.InstallAsync(id, "2.0.0", feed, CancellationToken.None));
        var after = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Assert.Equal(before.Count, after.Length);
        foreach (var path in after) Assert.Equal(before[Path.GetRelativePath(root, path)], File.ReadAllBytes(path));
        if (existingInstallation) Assert.Equal("1.0.0", Assert.Single(store.List()).Version);
        else Assert.Empty(store.List());
        Assert.Empty(Directory.GetFileSystemEntries(Transactions(root)));
    }

    [Fact]
    public async Task Incompatible_host_copy_is_refused_instead_of_blindly_pruned()
    {
        var feed = Temp(); var id = Unique(); var dependency = Unique();
        Package(feed, dependency, "1.0.0", Compile(Temp(), "JsonPath.Net", "public class WrongHostCopy { }"));
        Package(feed, id, "1.0.0", Compile(Temp(), id, Provider), new[] { Dependency(dependency, "[1.0.0]") });
        var store = new ProviderStore(Temp());
        await Assert.ThrowsAsync<ProviderLoadException>(() => store.InstallAsync(id, null, feed, CancellationToken.None));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Filesystem_links_cannot_redirect_installation_or_recursive_uninstall()
    {
        var parent = Temp(); var outside = Temp();
        var canary = Path.Combine(outside, "keep.txt"); File.WriteAllText(canary, "keep");
        var link = Path.Combine(parent, "linked-store");
        Link(link, outside);
        Assert.Throws<ProviderLoadException>(() => new ProviderStore(link));
        var root = Temp(); var provider = Directory.CreateDirectory(Path.Combine(root, "provider")).FullName;
        Link(Path.Combine(provider, "escape"), outside);
        Assert.Throws<ProviderLoadException>(() => new ProviderStore(root).Uninstall("provider"));
        Assert.Equal("keep", File.ReadAllText(canary));
    }

    private static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new System.Diagnostics.ProcessStartInfo("cmd") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task completion = Task.CompletedTask;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(capture.Token);
            var error = process.StandardError.ReadToEndAsync(capture.Token);
            completion = Task.WhenAll(process.WaitForExitAsync(), output, error);
            completion.WaitAsync(capture.Token).GetAwaiter().GetResult();
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            ChildProcess.KillTreeQuietly(process);
            capture.Cancel();
            try { completion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException) { }
            finally
            {
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("CON")]
    [InlineData("name.")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    public void Unsafe_ids_are_rejected(string id) => Assert.Throws<ProviderLoadException>(() => ProviderStore.ValidateId(id));

    [Theory]
    [InlineData("http://example.test/v3/index.json")]
    [InlineData("https://user:password@example.test/index.json")]
    [InlineData("https://example.test/index.json?token=secret")]
    public void Insecure_or_credential_bearing_sources_are_rejected(string source) =>
        Assert.Throws<ProviderLoadException>(() => ProviderPackages.ValidateSource(source));

    [Fact]
    public void Registry_resolution_requires_one_published_mapping()
    {
        var entries = new[] { new ProviderRegistryEntry("rpc.json-rpc", "Vouchfx.Community.JsonRpc", null) };
        Assert.Equal("Vouchfx.Community.JsonRpc", ProviderRegistry.Resolve("rpc.json-rpc", entries));
        Assert.Throws<ProviderLoadException>(() => ProviderRegistry.Resolve("rpc.missing", entries));
        Assert.Throws<ProviderLoadException>(() => ProviderRegistry.Resolve("rpc.json-rpc", entries.Concat(entries).ToArray()));
        Assert.Throws<ProviderLoadException>(() => ProviderRegistry.Resolve("rpc.source-only", new[] { new ProviderRegistryEntry("rpc.source-only", null, null) }));
    }

    [Fact]
    public async Task Http_endpoints_and_redirects_never_reach_an_insecure_transport()
    {
        using var inner = new StubHandler();
        using var client = new HttpClient(new ProviderHttpResource.HttpsHandler(inner));
        await Assert.ThrowsAsync<ProviderLoadException>(() => client.GetAsync("http://example.test/"));
        Assert.Equal(0, inner.Count);
        await Assert.ThrowsAsync<ProviderLoadException>(() => client.GetAsync("https://example.test/"));
        Assert.Equal(1, inner.Count);
        inner.Status = HttpStatusCode.NotModified;
        using var notModified = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        using var bounded = new BoundedStream(new MemoryStream(new byte[11]), 10);
        await Assert.ThrowsAsync<ProviderLoadException>(() => bounded.CopyToAsync(Stream.Null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_body_cancellation_after_headers_reaches_buffered_and_streaming_reads(bool streaming)
    {
        using var body = new StalledStream();
        using var client = new HttpClient(new BodyHandler(body));
        using var cancellation = new CancellationTokenSource();
        async Task Read()
        {
            using var response = await client.GetAsync("https://example.test/",
                streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellation.Token);
            if (streaming)
            {
                using var input = await response.Content.ReadAsStreamAsync(cancellation.Token);
                await input.CopyToAsync(Stream.Null, cancellation.Token);
            }
        }
        var read = Read();
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("lib\\net8.0\\escape.dll")]
    [InlineData("lib/net8.0/CON.dll")]
    [InlineData("/absolute.dll")]
    public void Archive_paths_cannot_escape_or_alias_the_installation(string name)
    {
        var path = Path.Combine(Temp(), "bad.nupkg");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry(name);
        Assert.Throws<ProviderLoadException>(() => ProviderArchive.Validate(path));
    }

    [Fact]
    public void Archive_symlinks_duplicate_paths_and_zip_bombs_are_refused()
    {
        var path = Path.Combine(Temp(), "bad.nupkg");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("link").ExternalAttributes = unchecked((int)0xa1ff0000);
        Assert.Throws<ProviderLoadException>(() => ProviderArchive.Validate(path));
        File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { zip.CreateEntry("lib/a.dll"); zip.CreateEntry("LIB/a.dll"); }
        Assert.Throws<ProviderLoadException>(() => ProviderArchive.Validate(path));
        File.Delete(path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var output = zip.CreateEntry("huge", CompressionLevel.Fastest).Open())
        { var block = new byte[64 * 1024]; for (var index = 0; index < 1025; index++) output.Write(block); }
        Assert.Throws<ProviderLoadException>(() => ProviderArchive.Validate(path));
    }

    [Fact]
    public async Task Real_cli_local_install_validation_isolated_compile_and_uninstall_are_hermetic()
    {
        var feed = Temp(); var root = Temp(); var id = "Vouchfx.Community.JsonRpc";
        var binary = Path.Combine(AppContext.BaseDirectory, "fixtures", "jsonrpc", id + ".dll");
        Package(feed, id, "1.0.0", binary);
        Assert.Equal(0, (await Cli("providers", "install", id, "--source", feed, "--directory", root)).Exit);
        var installed = Assert.Single(new ProviderStore(root).List());
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(binary)), SHA256.HashData(File.ReadAllBytes(Path.Combine(installed.Directory, id + ".dll"))));
        var suite = Path.Combine(Temp(), "rpc.e2e.yaml");
        File.WriteAllText(suite, "steps:\n  - id: rpc\n    type: rpc.json-rpc\n    url: http://127.0.0.1:1/\n    method: sum\n    capture: { result: '$.result.sum' }\n");
        var validation = await Cli("validate", suite, "--providers", root, "--no-default-providers", "--isolate-compile", "--json");
        Assert.True(validation.Exit == 0, validation.Output);
        using var listener = new HttpListener();
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellation.Token);
            using var request = await System.Text.Json.JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellation.Token);
            var response = System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + request.RootElement.GetProperty("id").GetRawText() + ",\"result\":{\"sum\":42}}");
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(response, cancellation.Token); context.Response.Close();
        }, cancellation.Token);
        File.WriteAllText(suite, $$"""
            steps:
              - id: acquired-rpc
                type: rpc.json-rpc
                url: http://127.0.0.1:{{port}}/
                method: sum
                capture: { answer: '$.result.sum' }
              - id: captured-value
                type: script.csharp
                code: 'if (Vars["answer"].ToString() != "42") throw new Exception("missing capture");'
            """);
        try
        {
            var run = await Cli("run", suite, "--providers", root, "--no-default-providers", "--isolate-compile", "--no-telemetry", "--fail-on-inconclusive", "--fail-on-env-error");
            Assert.True(run.Exit == 0, run.Output);
            Assert.Contains("pass=2 fail=0", run.Output);
        }
        finally
        {
            cancellation.Cancel(); listener.Stop();
            try { await serve; } catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException) { }
        }
        Assert.Equal(0, (await Cli("providers", "list", "--directory", root, "--offline", "--json")).Exit);
        Assert.Equal(0, (await Cli("providers", "uninstall", id, "--directory", root)).Exit);
        Assert.Empty(new ProviderStore(root).List());
    }

    private static async Task<(int Exit, string Output)> Cli(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(ProvidersCommand).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("VOUCHFX_PROVIDERS");
        using var process = System.Diagnostics.Process.Start(start)!;
        using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task completion = Task.CompletedTask;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(capture.Token);
            var error = process.StandardError.ReadToEndAsync(capture.Token);
            completion = Task.WhenAll(process.WaitForExitAsync(), output, error);
            await completion.WaitAsync(capture.Token);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            ChildProcess.KillTreeQuietly(process);
            capture.Cancel();
            try { await completion.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException) { }
            finally
            {
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
            }
        }
    }

    private sealed class BodyHandler : DelegatingHandler
    {
        internal BodyHandler(Stream body) : base(new ProviderHttpResource.HttpsHandler(new BodyResponseHandler(body))) { }
    }
    private sealed class BodyResponseHandler : HttpMessageHandler
    {
        private readonly Stream _body;
        internal BodyResponseHandler(Stream body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(_body) });
    }
    private sealed class StalledStream : Stream
    {
        private readonly CancellationTokenSource _lifetime = new();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { if (disposing) _lifetime.Cancel(); base.Dispose(disposing); }
    }
    private sealed class StubHandler : HttpMessageHandler
    {
        internal int Count { get; private set; }
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.Redirect;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return Task.FromResult(new HttpResponseMessage(Status)); }
    }
    private static string Transactions(string root) => Path.Combine(Path.GetDirectoryName(root)!, "." + Path.GetFileName(root) + ".transactions");
    private static PackageDependency Dependency(string id, string range) => new(id, VersionRange.Parse(range));

    private static void Package(string feed, string id, string version, string dll, PackageDependency[]? dependencies = null, (string Source, string Target)[]? extra = null, string framework = "net8.0")
    {
        var builder = new PackageBuilder { Id = id, Version = NuGetVersion.Parse(version), Description = "Acquisition regression fixture" };
        builder.Authors.Add("test");
        builder.Files.Add(new PhysicalPackageFile { SourcePath = dll, TargetPath = "lib/" + framework + "/" + Path.GetFileName(dll) });
        foreach (var file in extra ?? Array.Empty<(string, string)>()) builder.Files.Add(new PhysicalPackageFile { SourcePath = file.Source, TargetPath = file.Target });
        builder.DependencyGroups.Add(new PackageDependencyGroup(NuGetFramework.ParseFolder("net8.0"), dependencies ?? Array.Empty<PackageDependency>()));
        using var output = File.Create(Path.Combine(feed, id + "." + version + ".nupkg")); builder.Save(output);
    }

    private static string Compile(string directory, string name, string source, params string[] extra)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(IStepProvider).Assembly.Location).Concat(extra).Distinct().Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = Path.Combine(directory, name + ".dll"); var emitted = compilation.Emit(path);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return path;
    }
}
