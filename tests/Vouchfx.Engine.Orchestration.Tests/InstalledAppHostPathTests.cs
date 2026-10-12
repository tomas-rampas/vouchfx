using System.Reflection;
using System.Runtime.Loader;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

#pragma warning disable ASPIREFILESYSTEM001 // Pinned Aspire store contract under regression test.

public sealed class InstalledAppHostPathTests
{
    [Theory]
    [InlineData(@"C:\build-host\vouchfx\src\Cli\obj\")]
    [InlineData("/build-host/vouchfx/src/Cli/obj/")]
    public async Task InstalledHost_StaleBuildPaths_UsesIsolatedRuntimeStoreWithoutWritingToInstallation(string stalePath)
    {
        using var host = new HostFixture(stalePath, packaged: true);
        var options = host.Options();
        HeadlessTopology.ApplyInstalledAppHostDirectory(options);
        var builder = DistributedApplication.CreateBuilder(options);
        var directories = HeadlessTopology.ApplyInstalledAspireStorePath(builder)!;
        var storeRoot = builder.Configuration["Aspire:Store:Path"]!;
        var installationBefore = host.Snapshot();
        try
        {
            var app = builder.Build();
            var topology = HeadlessTopology.ForTestingDisposal(app, aspireStoreDirectories: directories);
            await using (topology)
            {
                var store = app.Services.GetRequiredService<IAspireStore>();
                Assert.Equal(host.Directory, builder.AppHostDirectory);
                Assert.Equal(Path.Combine(storeRoot, ".aspire"), store.BasePath);
                Assert.True(Path.IsPathFullyQualified(store.BasePath));
                Assert.True(HeadlessTopology.IsEngineOwnedTempDirectory(storeRoot));
                Assert.True(System.IO.Directory.Exists(store.BasePath));
                Assert.Equal(installationBefore, host.Snapshot());
            }
            Assert.False(System.IO.Directory.Exists(storeRoot));
            await topology.DisposeAsync();
            Assert.False(System.IO.Directory.Exists(storeRoot));
        }
        finally
        {
            HeadlessTopology.DeleteEngineOwnedTempDirectories(directories);
        }
    }

    [Fact]
    public async Task InstalledHost_ConfigureFailure_RemovesOwnedStoreBeforeRethrowing()
    {
        using var host = new HostFixture(@"C:\stale-build\obj\", packaged: true);
        string? storeRoot = null;
        var exception = await Assert.ThrowsAsync<FixtureException>(() => HeadlessTopology.StartAsync(
            host.AssemblyName,
            builder =>
            {
                storeRoot = builder.Configuration["Aspire:Store:Path"];
                Assert.True(System.IO.Directory.Exists(storeRoot));
                throw new FixtureException();
            }));

        Assert.NotNull(exception);
        Assert.NotNull(storeRoot);
        Assert.False(System.IO.Directory.Exists(storeRoot));
    }

    [Fact]
    public async Task InstalledHost_BuildValidationFailure_RemovesOwnedStoreBeforeRethrowing()
    {
        using var host = new HostFixture(@"C:\stale-build\obj\", packaged: true);
        string? storeRoot = null;
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => HeadlessTopology.StartAsync(
            host.AssemblyName,
            builder =>
            {
                storeRoot = builder.Configuration["Aspire:Store:Path"];
                // Direct collection addition bypasses Aspire's AddResource validation.
                // Build validates it, exercising the build-failure cleanup boundary.
                builder.Resources.Add(new ContainerResource("invalid resource name"));
            }));

        Assert.Equal("name", exception.ParamName);
        Assert.NotNull(storeRoot);
        Assert.False(System.IO.Directory.Exists(storeRoot));
    }

    [Fact]
    public void SourceHost_WithoutDistributionManifest_PreservesExplicitAndEmbeddedPaths()
    {
        using var host = new HostFixture(Path.GetTempPath(), packaged: false);
        var options = host.Options();
        var embeddedDirectory = options.ProjectDirectory;
        HeadlessTopology.ApplyInstalledAppHostDirectory(options);
        Assert.Equal(embeddedDirectory, options.ProjectDirectory);
        options.ProjectDirectory = host.Directory;
        HeadlessTopology.ApplyInstalledAppHostDirectory(options);
        var builder = DistributedApplication.CreateBuilder(options);
        var embeddedStore = builder.Configuration["Aspire:Store:Path"];

        Assert.Null(HeadlessTopology.ApplyInstalledAspireStorePath(builder));
        Assert.Equal(host.Directory, builder.AppHostDirectory);
        Assert.Equal(Path.GetTempPath(), embeddedStore);
        Assert.Equal(embeddedStore, builder.Configuration["Aspire:Store:Path"]);
    }

    [Fact]
    public void InstalledHost_ExplicitDcpOverride_RemainsAuthoritativeWithRelocatedStore()
    {
        using var host = new HostFixture(@"C:\stale-build\obj\", packaged: true);
        var previous = Environment.GetEnvironmentVariable("ASPIRE_DCP_PATH");
        var explicitPath = Path.Combine(Path.GetTempPath(), "vouchfx-user-dcp-" + Guid.NewGuid().ToString("N"));
        TempDirectoryLedger? directories = null;
        try
        {
            Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", explicitPath);
            var options = host.Options();
            HeadlessTopology.ApplyInstalledAppHostDirectory(options);
            var builder = DistributedApplication.CreateBuilder(options);
            HeadlessTopology.ApplyDcpPathSelfHeal(builder, host.AssemblyName);
            directories = HeadlessTopology.ApplyInstalledAspireStorePath(builder);

            Assert.Null(builder.Configuration["DcpPublisher:CliPath"]);
            Assert.Equal(explicitPath, Environment.GetEnvironmentVariable("ASPIRE_DCP_PATH"));
            Assert.NotNull(directories);
            Assert.True(HeadlessTopology.IsEngineOwnedTempDirectory(builder.Configuration["Aspire:Store:Path"]!));
        }
        finally
        {
            if (directories is not null) HeadlessTopology.DeleteEngineOwnedTempDirectories(directories);
            Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", previous);
        }
    }

    [Fact]
    public void InstalledHosts_ConcurrentBuilders_HaveIndependentOwnedStoreDirectories()
    {
        using var host = new HostFixture(@"C:\stale-build\obj\", packaged: true);
        var firstOptions = host.Options();
        var secondOptions = host.Options();
        HeadlessTopology.ApplyInstalledAppHostDirectory(firstOptions);
        HeadlessTopology.ApplyInstalledAppHostDirectory(secondOptions);
        var first = DistributedApplication.CreateBuilder(firstOptions);
        var second = DistributedApplication.CreateBuilder(secondOptions);
        var firstLedger = HeadlessTopology.ApplyInstalledAspireStorePath(first)!;
        var secondLedger = HeadlessTopology.ApplyInstalledAspireStorePath(second)!;
        var firstPath = first.Configuration["Aspire:Store:Path"]!;
        var secondPath = second.Configuration["Aspire:Store:Path"]!;
        try
        {
            Assert.NotEqual(firstPath, secondPath);
            HeadlessTopology.DeleteEngineOwnedTempDirectories(firstLedger);
            Assert.False(System.IO.Directory.Exists(firstPath));
            Assert.True(System.IO.Directory.Exists(secondPath));
        }
        finally
        {
            HeadlessTopology.DeleteEngineOwnedTempDirectories(firstLedger);
            HeadlessTopology.DeleteEngineOwnedTempDirectories(secondLedger);
        }
    }

    private sealed class FixtureException : Exception { }

    private sealed class HostFixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vouchfx-installed-host-test-" + Guid.NewGuid().ToString("N"));
        internal string AssemblyName { get; } = "VouchfxInstalledHost_" + Guid.NewGuid().ToString("N");

        internal HostFixture(string stalePath, bool packaged)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var metadata = typeof(InstalledAppHostPathTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>();
            var dcp = metadata.First(m => string.Equals(m.Key, "DcpCliPath", StringComparison.OrdinalIgnoreCase)).Value!;
            var dashboard = metadata.First(m => string.Equals(m.Key, "AspireDashboardPath", StringComparison.OrdinalIgnoreCase)).Value!;
            static string Attribute(string key, string value) =>
                "[assembly: System.Reflection.AssemblyMetadata(\"" + key + "\", @\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\")]";
            var source = string.Join(Environment.NewLine,
                Attribute("AppHostProjectPath", stalePath),
                Attribute("AppHostProjectBaseIntermediateOutputPath", stalePath),
                Attribute("DcpCliPath", dcp),
                Attribute("AspireDashboardPath", dashboard));
            var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
            var references = tpa.Split(Path.PathSeparator)
                .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll")
                .Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create(AssemblyName,
                new[] { CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var assemblyPath = Path.Combine(Directory, AssemblyName + ".dll");
            var emitted = compilation.Emit(assemblyPath);
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            if (packaged)
            {
                System.IO.Directory.CreateDirectory(Path.Combine(Directory, "dcp"));
                File.WriteAllText(Path.Combine(Directory, "dcp", "supported-rids.txt"), "fixture manifest");
                var rid = (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsMacOS() ? "osx-" : "linux-")
                    + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
                var version = typeof(DistributedApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                    .InformationalVersion.Split('+')[0];
                var tools = Path.Combine(Directory, "dcp", version, rid, "tools");
                System.IO.Directory.CreateDirectory(tools);
                File.Copy(dcp, Path.Combine(tools, Path.GetFileName(dcp)));
                File.Copy(Path.Combine(Path.GetDirectoryName(dcp)!, "dcptun_c"), Path.Combine(tools, "dcptun_c"));
                if (!OperatingSystem.IsWindows())
                {
                    foreach (var file in System.IO.Directory.GetFiles(tools))
                    {
                        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    }
                }
                foreach (var file in System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
                }
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(Path.Combine(Directory, "dcp"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }
            }
        }

        internal DistributedApplicationOptions Options() => new()
        {
            DisableDashboard = true,
            Args = Array.Empty<string>(),
            AssemblyName = AssemblyName,
        };

        internal string[] Snapshot() => System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(p => Path.GetRelativePath(Directory, p) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray();

        public void Dispose()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    if (System.IO.Directory.Exists(Path.Combine(Directory, "dcp")))
                    {
                        File.SetUnixFileMode(Path.Combine(Directory, "dcp"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }
                foreach (var file in System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                }
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
