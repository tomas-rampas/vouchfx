// Vouchfx.Cli.Tests — `list` Execute orchestration tests (#260 + Spec A). No Docker.
//
// Exercises ListCommand.Execute directly (bypassing System.CommandLine parsing) against
// the REAL sealed Core registry — the same 25-provider registry `run` and `validate`
// freeze via ProviderRegistryFactory.BuildCoreRegistry(). Asserts:
//   • --json yields exactly the sealed registry's step types: count == 25 and a spot
//     check of well-known dotted keys, sorted ordinally by type, with bar-B fields.
//   • the human table renders a header, every step-type key, and a summary count line.
//   • #556: every entry is tier "core" with a language-reference link, both verify modes and
//     an example; every example passes the SAME pipeline `vouchfx validate` runs (schema, AST,
//     provider bind/Validate/emit, Roslyn compile) by driving ValidateCommand.Execute itself;
//     each example's dependency agrees with the planner's DependencyKindStepMap wherever that
//     map speaks; the 25 examples compose into one suite that still validates; and the human
//     table is unchanged.
// This never starts a topology — StepKindRegistry reflection is entirely Docker-free.

using System.Text.Json;
using Vouchfx.Cli;
using Vouchfx.Engine.Compilation.Schema;
using Vouchfx.Engine.Planning;
using Xunit;
using YamlDotNet.RepresentationModel;

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

    // ── #556: tier, supportedVerifyModes, docsUrl, example ───────────────────

    [Fact]
    public void Execute_Json_EveryEntryIsCore_WithAReferenceLinkBothVerifyModesAndAnExample()
    {
        // Also proves the four init-only members survive a round trip through the CLI's own
        // options (System.Text.Json sets them after the positional constructor runs).
        var document = ListJson();

        Assert.Equal(25, document.StepTypes.Count);
        Assert.All(document.StepTypes, st =>
        {
            Assert.Equal(EngineExport.CoreTier, st.Tier);
            Assert.Equal(new[] { "IMMEDIATE", "RETRY" }, st.SupportedVerifyModes);
            Assert.NotNull(st.DocsUrl);
            Assert.StartsWith("https://vouchfx.io/language-reference/#", st.DocsUrl, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(st.Example), $"{st.Type} has no example.");
        });

        // The two anchors the issue names, as the published page already links them.
        Assert.Equal(
            "https://vouchfx.io/language-reference/#httprest",
            Assert.Single(document.StepTypes, st => st.Type == "http.rest").DocsUrl);
        Assert.Equal(
            "https://vouchfx.io/language-reference/#db-assertpostgres",
            Assert.Single(document.StepTypes, st => st.Type == "db-assert.postgres").DocsUrl);
    }

    [Fact]
    public void Execute_Json_EveryExample_PassesTheValidatePipeline()
    {
        var document = ListJson();

        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-list-examples-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var st in document.StepTypes)
            {
                File.WriteAllText(Path.Combine(dir, st.Type + ".e2e.yaml"), st.Example);
            }

            var (exitCode, failures, scenarioCount) = Validate(dir);

            Assert.True(
                failures.Count == 0,
                "Catalogue examples failed `vouchfx validate`:\n" + string.Join("\n", failures));
            Assert.Equal(document.StepTypes.Count, scenarioCount);
            Assert.Equal(ExitCodes.Success, exitCode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_Json_ExampleDependencies_AgreeWithTheKindThePlannerMapsEachTypeTo()
    {
        // The example's environment is derived inside Vouchfx.Engine.Compilation, which cannot
        // reference the planner; this is the one place that sees both, so it pins the two against
        // each other for every type DependencyKindStepMap maps (it maps only asserting/observing
        // types — mq-publish.* are absent from it by design, not by omission here).
        var byType = ListJson().StepTypes.ToDictionary(st => st.Type, StringComparer.Ordinal);

        var checkedTypes = 0;
        foreach (var (kind, stepTypes) in DependencyKindStepMap.CandidateStepTypes)
        {
            foreach (var stepType in stepTypes)
            {
                var root = LoadYamlMapping(byType[stepType].Example!);
                var dependencies = (YamlMappingNode)((YamlMappingNode)root.Children[new YamlScalarNode("environment")])
                    .Children[new YamlScalarNode("dependencies")];
                var dependency = Assert.Single(dependencies.Children);
                var step = (YamlMappingNode)Assert.Single(((YamlSequenceNode)root.Children[new YamlScalarNode("steps")]).Children);

                Assert.Equal(kind, Scalar((YamlMappingNode)dependency.Value, "type"));
                Assert.Equal(((YamlScalarNode)dependency.Key).Value, Scalar(step, "target"));
                checkedTypes++;
            }
        }

        // Never vacuous: the map is non-empty today (and DependencyKindStepMapDriftTests holds
        // every entry it names to the current registration).
        Assert.NotEqual(0, checkedTypes);
    }

    [Fact]
    public void Execute_Json_TheExamplesComposeIntoOneSuiteThatStillValidates()
    {
        // StepCatalogueEntry.Example's documentation claims the examples compose: a dependency is
        // named after its kind (so same-kind examples declare the SAME resource) and a step id is
        // the type (so no two collide). Merge all 25, requiring every same-named resource to be
        // declared identically, and the result must still pass `vouchfx validate`.
        var services = new YamlMappingNode();
        var dependencies = new YamlMappingNode();
        var steps = new YamlSequenceNode();

        foreach (var st in ListJson().StepTypes)
        {
            var root = LoadYamlMapping(st.Example!);
            if (root.Children.TryGetValue(new YamlScalarNode("environment"), out var environment))
            {
                MergeResources((YamlMappingNode)environment, "services", services, st.Type);
                MergeResources((YamlMappingNode)environment, "dependencies", dependencies, st.Type);
            }

            foreach (var step in ((YamlSequenceNode)root.Children[new YamlScalarNode("steps")]).Children)
            {
                steps.Add(step);
            }
        }

        Assert.Equal(25, steps.Children.Count);
        var composed = new YamlMappingNode
        {
            {
                "environment",
                new YamlMappingNode { { "services", services }, { "dependencies", dependencies } }
            },
            { "steps", steps },
        };

        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-list-compose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "composed.e2e.yaml"), ToYaml(composed));

            var (exitCode, failures, scenarioCount) = Validate(dir);

            Assert.True(
                failures.Count == 0,
                "The composed catalogue examples failed `vouchfx validate`:\n" + string.Join("\n", failures));
            Assert.Equal(1, scenarioCount);
            Assert.Equal(ExitCodes.Success, exitCode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_Human_TableIsUnchanged_ByTheFourNewFields()
    {
        var sw = new StringWriter();
        ListCommand.Execute(json: false, sw);
        var text = sw.ToString();

        Assert.Equal(
            $"{"TYPE",-32} {"FAMILY",-16} {"PROVIDER",-16} VERSION",
            text.Split('\n')[0].TrimEnd('\r'));
        Assert.DoesNotContain("https://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RETRY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("scaffolded-suite", text, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StepCatalogueDocument ListJson()
    {
        var sw = new StringWriter();
        Assert.Equal(ExitCodes.Success, ListCommand.Execute(json: true, sw));

        var document = JsonSerializer.Deserialize<StepCatalogueDocument>(sw.ToString(), CliJsonContract.Options);
        Assert.NotNull(document);
        return document!;
    }

    /// <summary>
    /// Runs <see cref="ValidateCommand.Execute"/> — the whole `vouchfx validate` orchestration,
    /// discovery included — over <paramref name="dir"/> and returns the exit code, one line per
    /// invalid scenario, and the scenario count. Failure lines name the FILE only and blank the
    /// temporary directory out of every message, so an assertion never prints a host path.
    /// </summary>
    private static (int ExitCode, List<string> Failures, int ScenarioCount) Validate(string dir)
    {
        var stdout = new StringWriter();
        var exitCode = ValidateCommand.Execute(dir, json: true, stdout, new StringWriter());

        using var report = JsonDocument.Parse(stdout.ToString());
        var scenarios = report.RootElement.GetProperty("scenarios").EnumerateArray().ToList();
        var failures = scenarios
            .Where(s => !s.GetProperty("valid").GetBoolean())
            .Select(s => Path.GetFileName(s.GetProperty("path").GetString()) + ": " + string.Join(
                " | ",
                s.GetProperty("diagnostics").EnumerateArray().Select(d =>
                    d.GetProperty("stage").GetString() + " "
                    + d.GetProperty("message").GetString()!.Replace(dir, "<dir>", StringComparison.Ordinal))))
            .ToList();

        return (exitCode, failures, scenarios.Count);
    }

    private static void MergeResources(YamlMappingNode environment, string section, YamlMappingNode into, string stepType)
    {
        if (!environment.Children.TryGetValue(new YamlScalarNode(section), out var node))
        {
            return;
        }

        foreach (var (name, declaration) in ((YamlMappingNode)node).Children)
        {
            if (into.Children.TryGetValue(name, out var existing))
            {
                Assert.True(
                    string.Equals(ToYaml(existing), ToYaml(declaration), StringComparison.Ordinal),
                    $"{stepType}'s example declares {section}.{name} differently from an earlier example.");
                continue;
            }

            into.Add(name, declaration);
        }
    }

    private static YamlMappingNode LoadYamlMapping(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)Assert.Single(stream.Documents).RootNode;
    }

    private static string? Scalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) ? ((YamlScalarNode)node).Value : null;

    private static string ToYaml(YamlNode node)
    {
        var writer = new StringWriter();
        new YamlStream(new YamlDocument(node)).Save(writer, assignAnchors: false);
        return writer.ToString();
    }
}
