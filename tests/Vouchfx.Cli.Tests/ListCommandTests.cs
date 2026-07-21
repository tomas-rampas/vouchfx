// Vouchfx.Cli.Tests — `list` Execute orchestration tests (#260). No Docker.
//
// Exercises ListCommand.Execute directly (bypassing System.CommandLine parsing) against
// the REAL sealed Core registry — the same 25-provider registry `run` and `validate`
// freeze via ProviderRegistryFactory.BuildCoreRegistry(). Asserts:
//   • --json yields exactly the sealed registry's step types: count == 25 and a spot
//     check of well-known dotted keys, sorted ordinally by type.
//   • the human table renders a header, every step-type key, and a summary count line.
// This never starts a topology — StepKindRegistry reflection is entirely Docker-free.

using System.Text.Json;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ListCommandTests
{
    [Fact]
    public void Execute_Json_ReturnsSuccessExitCode()
    {
        var sw = new StringWriter();

        var exitCode = ListCommand.Execute(json: true, sw);

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void Execute_Json_YieldsExactlyTheSealedRegistrysStepTypes()
    {
        var sw = new StringWriter();
        ListCommand.Execute(json: true, sw);

        var document = JsonSerializer.Deserialize<ListJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);

        // The 25 Core providers across eleven families (CLAUDE.md "Planned repository
        // structure"). A drift in this count means a provider was added/removed from
        // ProviderRegistryFactory.CoreProviderAssemblies without this test's attention.
        Assert.Equal(25, document!.StepTypes.Count);

        var keys = document.StepTypes.Select(st => st.Type).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("http.rest", keys);
        Assert.Contains("http.soap", keys);
        Assert.Contains("db-assert.postgres", keys);
        Assert.Contains("mq-publish.kafka", keys);
        Assert.Contains("mq-expect.kafka", keys);
        Assert.Contains("script.csharp", keys);
        Assert.Contains("webhook-listen.http", keys);
        Assert.Contains("trace-expect.otlp", keys);
        Assert.Contains("storage-assert.s3", keys);

        // Every entry's Type is the dotted "family.provider" composition of its own
        // Family/Provider fields — never a coincidental match.
        Assert.All(document.StepTypes, st => Assert.Equal($"{st.Family}.{st.Provider}", st.Type));

        // Sorted ordinally by Type.
        var sorted = document.StepTypes.Select(st => st.Type).ToList();
        var expectedOrder = sorted.OrderBy(t => t, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedOrder, sorted);
    }

    [Fact]
    public void Execute_Json_SchemaVersionAndEngineVersionArePresent()
    {
        var sw = new StringWriter();
        ListCommand.Execute(json: true, sw);

        var document = JsonSerializer.Deserialize<ListJsonDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);
        Assert.Equal(1, document!.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(document.EngineVersion));
    }

    [Fact]
    public void Execute_Human_RendersHeaderEveryStepTypeAndSummaryLine()
    {
        var sw = new StringWriter();
        ListCommand.Execute(json: false, sw);

        var text = sw.ToString();
        Assert.Contains("TYPE", text, StringComparison.Ordinal);
        Assert.Contains("FAMILY", text, StringComparison.Ordinal);
        Assert.Contains("PROVIDER", text, StringComparison.Ordinal);
        Assert.Contains("http.rest", text, StringComparison.Ordinal);
        Assert.Contains("db-assert.postgres", text, StringComparison.Ordinal);
        Assert.Contains("25 step type(s).", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_Human_DoesNotEmitJson()
    {
        var sw = new StringWriter();
        ListCommand.Execute(json: false, sw);

        Assert.DoesNotContain("schemaVersion", sw.ToString(), StringComparison.Ordinal);
    }
}
