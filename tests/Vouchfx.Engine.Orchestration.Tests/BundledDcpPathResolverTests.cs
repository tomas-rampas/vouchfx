using Vouchfx.Engine.Orchestration;
using Xunit;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>Installed distribution resolution, independent of Docker and NuGet state.</summary>
public sealed class BundledDcpPathResolverTests
{
    private const string Version = "13.4.2";
    private static readonly string HostDirectory = Path.Combine(Path.GetTempPath(), "installed-vouchfx");
    private static readonly string Manifest = Path.Combine(HostDirectory, "dcp", "supported-rids.txt");
    private const string Metadata = "/packaging-cache/aspire.hosting.orchestration.linux-x64/13.4.2/tools/dcp";

    [Theory]
    [InlineData("win-x64", "dcp.exe")]
    [InlineData("linux-x64", "dcp")]
    [InlineData("osx-x64", "dcp")]
    [InlineData("osx-arm64", "dcp")]
    [InlineData("ubuntu.22.04-x64", "dcp")]
    public void Installed_bundle_selects_current_RID_without_cache(string rid, string executable)
    {
        var portableRid = rid.StartsWith("ubuntu", StringComparison.Ordinal) ? "linux-x64" : rid;
        var tools = Path.Combine(HostDirectory, "dcp", Version, portableRid, "tools");
        var candidate = Path.Combine(tools, executable);
        var files = new HashSet<string> { Manifest, candidate, Path.Combine(tools, "dcptun_c") };

        var result = Resolve(rid, files.Contains);

        Assert.Equal(DcpPathResolutionKind.Override, result.Kind);
        Assert.Equal(candidate, result.OverridePath);
        Assert.True(result.IsBundled);
    }

    [Fact]
    public void Bundle_outranks_live_build_metadata_and_warmed_cache()
    {
        var tools = Path.Combine(HostDirectory, "dcp", Version, "win-x64", "tools");
        var candidate = Path.Combine(tools, "dcp.exe");
        var files = new HashSet<string>
        {
            Manifest, candidate, Path.Combine(tools, "dcptun_c"), Metadata,
            Path.Combine("user-cache", "aspire.hosting.orchestration.win-x64", Version, "tools", "dcp.exe"),
        };

        Assert.Equal(candidate, Resolve("win-x64", files.Contains).OverridePath);
    }

    [Theory]
    [InlineData("dcp.exe")]
    [InlineData("dcptun_c")]
    public void Incomplete_bundle_fails_instead_of_using_live_metadata_or_cache(string missing)
    {
        var tools = Path.Combine(HostDirectory, "dcp", Version, "win-x64", "tools");
        var missingPath = Path.Combine(tools, missing);
        var result = Resolve("win-x64", path => path != missingPath);

        Assert.Equal(DcpPathResolutionKind.Unresolvable, result.Kind);
        Assert.Contains(missingPath, result.UnresolvableDetail);
        Assert.Contains("Reinstall", result.UnresolvableDetail);
        Assert.Null(result.OverridePath);
    }

    [Theory]
    [InlineData("linux-arm64")]
    [InlineData("win-arm64")]
    [InlineData("win-x86")]
    [InlineData("linux-musl-x64")]
    public void Unsupported_bundle_RID_has_explicit_diagnostic(string rid)
    {
        var result = Resolve(rid, path => path == Manifest);

        Assert.Equal(DcpPathResolutionKind.Unresolvable, result.Kind);
        Assert.Contains(rid, result.UnresolvableDetail);
        Assert.Contains("Supported runtimes", result.UnresolvableDetail);
    }

    [Fact]
    public void Explicit_override_does_not_probe_an_incomplete_or_unsupported_bundle()
    {
        var result = Resolve("linux-arm64", _ => throw new InvalidOperationException("Must not probe"), "/custom/tools");

        Assert.Equal(DcpPathResolutionKind.UseEmbedded, result.Kind);
        Assert.False(result.IsBundled);
    }

    [Fact]
    public void Wrong_version_bundle_cannot_satisfy_the_loaded_Aspire_version()
    {
        // Live legacy metadata/cache must not conceal a distribution carrying only
        // another Aspire version. The manifest declares installed-bundle ownership.
        var expectedTools = Path.Combine(HostDirectory, "dcp", Version);
        var result = Resolve("win-x64", path => !path.StartsWith(expectedTools, StringComparison.Ordinal));

        Assert.Equal(DcpPathResolutionKind.Unresolvable, result.Kind);
        Assert.Contains(Version, result.UnresolvableDetail);
    }

    [Fact]
    public void Declared_bundle_without_a_known_version_fails_instead_of_borrowing_metadata()
    {
        var result = DcpPathResolver.Resolve("/unversioned/dcp", _ => true, "user-cache", "profile",
            "win-x64", aspireHostingInformationalVersion: null, hostAssemblyDirectory: HostDirectory);

        Assert.Equal(DcpPathResolutionKind.Unresolvable, result.Kind);
        Assert.Contains("version could not be determined", result.UnresolvableDetail);
    }

    private static DcpPathResolution Resolve(string rid, Func<string, bool> exists, string? explicitOverride = null) =>
        DcpPathResolver.Resolve(Metadata, exists, "user-cache", "profile", rid, Version + "+commit",
            aspireDcpPathEnvironmentVariable: explicitOverride, hostAssemblyDirectory: HostDirectory);
}
