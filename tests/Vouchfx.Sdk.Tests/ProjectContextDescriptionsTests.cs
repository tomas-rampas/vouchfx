// Vouchfx.Sdk.Tests — ProjectContextDescriptions (G-C, gatekeeper, fix round 3).
//
// ProjectContextDescriptions.DescribeDeclaredSurfaces had no dedicated test file at all before
// this one — it replaced five byte-identical private copies (HttpRestProvider,
// HttpSoapProvider, MetricsAssertPrometheusProvider, MqExpectKafkaProvider,
// MqPublishKafkaProvider) with a single public static helper on Vouchfx.Sdk, but only ever
// picked up incidental coverage through those five providers' own validation tests.

using Vouchfx.Sdk;
using Xunit;

namespace Vouchfx.Sdk.Tests;

/// <summary>
/// A minimal in-file <see cref="IProjectContext"/> stand-in — the same shape as the ~22
/// existing test-only implementations across this repository's suites (see
/// <c>IProjectContext.DeclaredServices</c>'s own remarks).
/// </summary>
file sealed class StubProjectContext : IProjectContext
{
    public System.Collections.Generic.IReadOnlyDictionary<string, string> DeclaredDependencies { get; init; } =
        new System.Collections.Generic.Dictionary<string, string>();

    public System.Collections.Generic.IReadOnlyDictionary<string, DeclaredServiceInfo> DeclaredServices { get; init; } =
        new System.Collections.Generic.Dictionary<string, DeclaredServiceInfo>();

    public string SuiteDirectory { get; init; } = ".";
}

public sealed class ProjectContextDescriptionsTests
{
    /// <summary>
    /// G-C (gatekeeper, fix round 3): unlike the five private copies this helper replaced,
    /// <c>DescribeDeclaredSurfaces</c> is a PUBLIC API on a contract frozen for the whole v1.x
    /// engine series — a null <c>ctx</c> argument must fail fast with a located
    /// <see cref="ArgumentNullException"/>, not an NRE from whichever line happens to
    /// dereference it first.
    /// </summary>
    [Fact]
    public void DescribeDeclaredSurfaces_NullContext_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => ProjectContextDescriptions.DescribeDeclaredSurfaces(null!));

        Assert.Equal("ctx", ex.ParamName);
    }

    /// <summary>
    /// Both surfaces empty renders as <c>"(none)"</c> for each, rather than an empty list.
    /// </summary>
    [Fact]
    public void DescribeDeclaredSurfaces_EmptySurfaces_RendersNoneForBoth()
    {
        var ctx = new StubProjectContext();

        var description = ProjectContextDescriptions.DescribeDeclaredSurfaces(ctx);

        Assert.Equal("Declared dependencies: (none). Declared services: (none).", description);
    }

    /// <summary>
    /// Populated surfaces render each map's keys, ordinally sorted, comma-joined.
    /// </summary>
    [Fact]
    public void DescribeDeclaredSurfaces_PopulatedSurfaces_RendersSortedKeys()
    {
        var ctx = new StubProjectContext
        {
            DeclaredDependencies = new System.Collections.Generic.Dictionary<string, string>
            {
                ["orders-db"] = "postgres",
                ["bus"] = "kafka",
            },
            DeclaredServices = new System.Collections.Generic.Dictionary<string, DeclaredServiceInfo>
            {
                ["web"] = new DeclaredServiceInfo(new System.Collections.Generic.List<string> { "http" }),
                ["api"] = new DeclaredServiceInfo(new System.Collections.Generic.List<string> { "http" }),
            },
        };

        var description = ProjectContextDescriptions.DescribeDeclaredSurfaces(ctx);

        Assert.Equal(
            "Declared dependencies: bus, orders-db. Declared services: api, web.",
            description);
    }
}
