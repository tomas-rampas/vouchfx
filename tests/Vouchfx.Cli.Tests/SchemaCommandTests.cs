// Vouchfx.Cli.Tests — `schema` subcommand (Spec A). No Docker.

using System.Text.Json;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class SchemaCommandTests
{
    [Fact]
    public void Execute_Stdout_ReturnsSuccessAndValidJsonWithCoreTypes()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = SchemaCommand.Execute(outputPath: null, stdout, stderr);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(string.Empty, stderr.ToString());

        var json = stdout.ToString();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.Contains("http.rest", json, StringComparison.Ordinal);
        Assert.Contains("db-assert.postgres", json, StringComparison.Ordinal);
        Assert.Contains("x-vouchfx-schema-version", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_OutputFile_WritesSchemaAndReturnsSuccess()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-schema-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "composed.json");
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = SchemaCommand.Execute(path, stdout, stderr);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.Contains("http.rest", text, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(text);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_OutputParentMissing_ReturnsUsageErrorAndNamesDirectory()
    {
        var missingParent = Path.Combine(
            Path.GetTempPath(),
            "vouchfx-schema-missing-" + Guid.NewGuid().ToString("N"),
            "nested",
            "schema.json");

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = SchemaCommand.Execute(missingParent, stdout, stderr);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("parent directory", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(missingParent));
    }

    [Fact]
    public void Build_ExposesOutputOption()
    {
        var command = SchemaCommand.Build();
        Assert.Equal("schema", command.Name);
        Assert.Contains(command.Options, o => o.Name == "--output");
    }
}
