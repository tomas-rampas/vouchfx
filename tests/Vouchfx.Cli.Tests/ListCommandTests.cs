// Vouchfx.Cli.Tests — `list` Execute orchestration tests (#260 + Spec A). No Docker.
//
// Exercises ListCommand.Execute directly (bypassing System.CommandLine parsing) against
// the REAL sealed Core registry — the same 25-provider registry `run` and `validate`
// freeze via ProviderRegistryFactory.BuildCoreRegistry(). Asserts:
//   • --json yields exactly the sealed registry's step types: count == 25 and a spot
//     check of well-known dotted keys, sorted ordinally by type, with bar-B fields.
//   • the human table renders a header, every step-type key, and a summary count line.
// This never starts a topology — StepKindRegistry reflection is entirely Docker-free.

using System.Text.Json;
using Vouchfx.Cli;
using Vouchfx.Engine.Compilation.Schema;
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
    public void Execute_Json_YieldsExactlyTheSealedRegistryStepTypes()
    {
        var sw = new StringWriter();
        ListCommand.Execute(json: true, sw);

        var document = JsonSerializer.Deserialize<StepCatalogueDocument>(
            sw.ToString(), CliJsonContract.Options);
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
    public void Execute_Json_EachEntryHasBarBCatalogueFields()
    {
        var sw = new StringWriter();
        ListCommand.Execute(json: true, sw);

        var document = JsonSerializer.Deserialize<StepCatalogueDocument>(
            sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);

        Assert.All(document!.StepTypes, st =>
        {
            // At least one of required/optional/exactly-one-of/at-least-one-of is non-empty
            // for every Core provider that ships a real model fragment (all Core do). A type
            // whose sole requirement lives inside a root oneOf/anyOf (script.csharp's
            // code/file; mq-publish.azureservicebus's queue/topic; mq-expect.azureservicebus's
            // expectPayloadContains/expectProperties) legitimately has an EMPTY flat
            // RequiredFields/OptionalFields pair — the constraint is expressed in the typed
            // groups instead (StepCatalogueEntry.ExactlyOneOfGroups/AtLeastOneOfGroups), not
            // omitted. Checking only the flat lists here would resurrect exactly the bug B1
            // fixed: a step type that looks unconstrained when it is not.
            var hasAnyFieldInfo =
                st.RequiredFields.Count > 0
                || st.OptionalFields.Count > 0
                || (st.ExactlyOneOfGroups?.Count ?? 0) > 0
                || (st.AtLeastOneOfGroups?.Count ?? 0) > 0;
            Assert.True(
                hasAnyFieldInfo,
                $"Step type '{st.Type}' has empty required, optional, exactly-one-of, and "
                + "at-least-one-of field lists.");
            Assert.True(st.CaptureSupported);
            Assert.False(string.IsNullOrWhiteSpace(st.FamilyIntent));
        });

        var httpRest = Assert.Single(document.StepTypes, st => st.Type == "http.rest");
        Assert.Contains("target", httpRest.RequiredFields);
        Assert.Contains("method", httpRest.RequiredFields);
        Assert.Contains("path", httpRest.RequiredFields);

        var pg = Assert.Single(document.StepTypes, st => st.Type == "db-assert.postgres");
        Assert.True(pg.RequiredFields.Count > 0 || pg.OptionalFields.Count > 0);
        Assert.Contains("data store", pg.FamilyIntent, StringComparison.OrdinalIgnoreCase);

        // script.csharp is the specific case that motivates the widened check above: empty
        // flat lists, but a real constraint expressed as a typed group (B1).
        var scriptCsharp = Assert.Single(document.StepTypes, st => st.Type == "script.csharp");
        Assert.Empty(scriptCsharp.RequiredFields);
        Assert.Empty(scriptCsharp.OptionalFields);
        Assert.NotNull(scriptCsharp.ExactlyOneOfGroups);
        var scriptGroup = Assert.Single(scriptCsharp.ExactlyOneOfGroups!);
        Assert.Equal(new[] { "code", "file" }, scriptGroup);
    }

    [Fact]
    public void Execute_Json_SchemaVersionAndEngineVersionArePresent()
    {
        var sw = new StringWriter();
        ListCommand.Execute(json: true, sw);

        var document = JsonSerializer.Deserialize<StepCatalogueDocument>(
            sw.ToString(), CliJsonContract.Options);
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
