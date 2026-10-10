using System.Reflection;
using Aspire.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Vouchfx.Cli.Tests;

/// <summary>Validate the real CLI AppHost metadata without starting DCP or Docker.</summary>
public sealed class HeadlessHostOptionsTests
{
    [Fact]
    public void Headless_cli_has_valid_Dcp_options_without_unused_dashboard_binaries()
    {
        var host = typeof(ExitCodes).Assembly;
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = Array.Empty<string>(),
            AssemblyName = host.GetName().Name,
            DisableDashboard = true,
        });
        using var app = builder.Build();
        // DcpOptions is internal upstream. Resolve its registered IOptions<T> to
        // exercise Aspire's real configuration and validation, without StartAsync.
        var dcpOptions = typeof(DistributedApplication).Assembly.GetType("Aspire.Hosting.Dcp.DcpOptions", throwOnError: true)!;
        var optionsType = typeof(IOptions<>).MakeGenericType(dcpOptions);
        var options = app.Services.GetRequiredService(optionsType);
        var value = optionsType.GetProperty(nameof(IOptions<object>.Value))!.GetValue(options)!;

        Assert.False(string.IsNullOrWhiteSpace((string?)dcpOptions.GetProperty("CliPath")!.GetValue(value)));
        Assert.False(string.IsNullOrWhiteSpace((string?)dcpOptions.GetProperty("DashboardPath")!.GetValue(value)));
        Assert.Contains(host.GetCustomAttributes<AssemblyMetadataAttribute>(),
            metadata => metadata.Key == "aspiredashboardpath" && metadata.Value == "disabled-by-vouchfx-headless-host");
    }
}
