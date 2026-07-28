// Vouchfx.Cli.Tests — `scaffold` subcommand (Spec B). No Docker.

using System.Text;
using Vouchfx.Cli;
using Xunit;

namespace Vouchfx.Cli.Tests;

public sealed class ScaffoldCommandTests
{
    private static string WriteIntentFile(string dir, string json)
    {
        var path = Path.Combine(dir, "intent.json");
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }

    private static string MultiTypeIntentJson() =>
        """
        {
          "steps": [
            { "id": "get-api", "type": "http.rest", "label": "GET api" },
            { "id": "check-db", "type": "db-assert.postgres" }
          ],
          "services": [ { "name": "api", "image": "traefik/whoami" } ],
          "dependencies": [ { "name": "db", "type": "postgres" } ]
        }
        """;

    [Fact]
    public void Execute_MultiTypeIntent_Stdout_SuccessWithTypesAndIds()
    {
        // REQ-001
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(dir, MultiTypeIntentJson());
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = ScaffoldCommand.Execute(intentPath, outputPath: null, stdout, stderr);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Equal(string.Empty, stderr.ToString());
            var yaml = stdout.ToString();
            Assert.Contains("http.rest", yaml, StringComparison.Ordinal);
            Assert.Contains("db-assert.postgres", yaml, StringComparison.Ordinal);
            Assert.Contains("get-api", yaml, StringComparison.Ordinal);
            Assert.Contains("check-db", yaml, StringComparison.Ordinal);
            Assert.Contains("Machine-drafted", yaml, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_UnknownType_ReturnsEnvironmentErrorNamingType()
    {
        // REQ-002
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(
                dir,
                """
                { "steps": [ { "id": "x", "type": "nope.fake" } ] }
                """);
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = ScaffoldCommand.Execute(intentPath, null, stdout, stderr);

            Assert.Equal(ExitCodes.EnvironmentError, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("nope.fake", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_ThenValidate_MultiType_ExitsValid()
    {
        // REQ-003 — scaffold then ValidateCommand path.
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(dir, MultiTypeIntentJson());
            var suitePath = Path.Combine(dir, "scaffolded.e2e.yaml");
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var scaffoldExit = ScaffoldCommand.Execute(intentPath, suitePath, stdout, stderr);
            Assert.Equal(ExitCodes.Success, scaffoldExit);
            Assert.True(File.Exists(suitePath));

            var validateStdout = new StringWriter();
            var validateStderr = new StringWriter();
            var validateExit = ValidateCommand.Execute(
                suitePath,
                json: false,
                validateStdout,
                validateStderr);

            Assert.True(
                validateExit == ExitCodes.Success,
                "validate failed (exit " + validateExit + "): stdout="
                + validateStdout + " stderr=" + validateStderr);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_EmitsIdsNamesAndOrder()
    {
        // REQ-004
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(dir, MultiTypeIntentJson());
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            ScaffoldCommand.Execute(intentPath, null, stdout, stderr);
            var yaml = stdout.ToString();

            Assert.Contains("get-api", yaml, StringComparison.Ordinal);
            Assert.Contains("check-db", yaml, StringComparison.Ordinal);
            Assert.Contains("api:", yaml, StringComparison.Ordinal);
            Assert.Contains("db:", yaml, StringComparison.Ordinal);
            Assert.True(
                yaml.IndexOf("get-api", StringComparison.Ordinal)
                < yaml.IndexOf("check-db", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_ProvenanceHeaderPresent()
    {
        // REQ-005
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(dir, MultiTypeIntentJson());
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            ScaffoldCommand.Execute(intentPath, null, stdout, stderr);
            var firstLines = stdout.ToString()
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .Take(3)
                .ToList();

            Assert.All(firstLines, l => Assert.StartsWith("#", l, StringComparison.Ordinal));
            var header = string.Join(' ', firstLines);
            Assert.Contains("Machine-drafted", header, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("review", header, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_DoesNotLeakPlantedEnvSecret()
    {
        // REQ-006
        const string planted = "planted-secret-token-XYZ-7c2e";
        Environment.SetEnvironmentVariable("SCAFFOLD_LEAK_TEST", planted);
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var intentPath = WriteIntentFile(dir, MultiTypeIntentJson());
                var stdout = new StringWriter();
                var stderr = new StringWriter();

                var exit = ScaffoldCommand.Execute(intentPath, null, stdout, stderr);
                Assert.Equal(ExitCodes.Success, exit);
                Assert.DoesNotContain(planted, stdout.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCAFFOLD_LEAK_TEST", null);
        }
    }

    [Fact]
    public void Execute_DuplicateIds_FailsNonZeroNamingId()
    {
        // EDGE-001
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(
                dir,
                """
                {
                  "steps": [
                    { "id": "dup", "type": "http.rest" },
                    { "id": "dup", "type": "http.rest" }
                  ]
                }
                """);
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = ScaffoldCommand.Execute(intentPath, null, stdout, stderr);
            Assert.Equal(ExitCodes.EnvironmentError, exit);
            Assert.Contains("dup", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_EmptySteps_FailsNonZero()
    {
        // EDGE-002
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(dir, """{ "steps": [] }""");
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = ScaffoldCommand.Execute(intentPath, null, stdout, stderr);
            Assert.NotEqual(ExitCodes.Success, exit);
            Assert.Equal(ExitCodes.EnvironmentError, exit);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_OutputParentMissing_ReturnsUsageError()
    {
        // EDGE-003
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(dir, MultiTypeIntentJson());
            var missingParent = Path.Combine(
                Path.GetTempPath(),
                "vouchfx-scaffold-missing-" + Guid.NewGuid().ToString("N"),
                "nested",
                "out.e2e.yaml");

            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = ScaffoldCommand.Execute(intentPath, missingParent, stdout, stderr);

            Assert.Equal(ExitCodes.UsageError, exit);
            Assert.Equal(string.Empty, stdout.ToString());
            Assert.Contains("parent directory", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(missingParent));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_UnknownDependencyKind_FailsNamingKind()
    {
        // EDGE-004
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(
                dir,
                """
                {
                  "steps": [ { "id": "a", "type": "http.rest" } ],
                  "dependencies": [ { "name": "x", "type": "not-a-real-dep" } ]
                }
                """);
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = ScaffoldCommand.Execute(intentPath, null, stdout, stderr);
            Assert.Equal(ExitCodes.EnvironmentError, exit);
            Assert.Contains("not-a-real-dep", stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_TwoRunsIdenticalYaml()
    {
        // EDGE-005
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(dir, MultiTypeIntentJson());
            var aOut = new StringWriter();
            var bOut = new StringWriter();
            var err = new StringWriter();

            Assert.Equal(ExitCodes.Success, ScaffoldCommand.Execute(intentPath, null, aOut, err));
            Assert.Equal(ExitCodes.Success, ScaffoldCommand.Execute(intentPath, null, bOut, err));
            Assert.Equal(aOut.ToString(), bOut.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Build_ExposesIntentAndOutputOptions()
    {
        var command = ScaffoldCommand.Build();
        Assert.Equal("scaffold", command.Name);
        Assert.Contains(command.Options, o => o.Name == "--intent");
        Assert.Contains(command.Options, o => o.Name == "--output");
    }

    [Fact]
    public void Execute_MalformedJson_ReturnsUsageError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouchfx-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var intentPath = WriteIntentFile(dir, "{ not-json");
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = ScaffoldCommand.Execute(intentPath, null, stdout, stderr);
            Assert.Equal(ExitCodes.UsageError, exit);
            Assert.Contains("JSON", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Execute_MissingIntentFile_ReturnsUsageError()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "vouchfx-scaffold-no-file-" + Guid.NewGuid().ToString("N") + ".json");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = ScaffoldCommand.Execute(missing, null, stdout, stderr);
        Assert.Equal(ExitCodes.UsageError, exit);
    }
}
