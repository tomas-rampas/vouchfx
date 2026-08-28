// Synthesised `project:`-form fixtures for the non-Docker orchestration tests.
//
// Aspire's AddProject(name, csprojPath) only requires the .csproj FILE to exist and reads
// `Properties/launchSettings.json` beside it; it never builds the project at the Configure
// phase. So a two-file temp directory exercises the real code path exactly, while keeping the
// repository free of a csproj that vouchfx.sln, `dotnet format`, and every tooling glob would
// then have to exclude. Compiling throwaway artefacts at test time is established practice in
// this assembly — see HeadlessTopologySelfHealTests, which emits synthetic host assemblies
// with Roslyn.
//
// One tracked instance per test class, disposed by xUnit: creation must never happen outside
// the tracker, or a throw between "directory created" and "try entered" leaks it.
using System;
using System.Collections.Generic;
using System.IO;

namespace Vouchfx.Engine.Orchestration.Tests;

/// <summary>
/// Creates minimal <c>.csproj</c> fixtures in temp directories and removes them on dispose.
/// </summary>
internal sealed class ProjectFixtures : IDisposable
{
    private readonly List<string> _directories = new();

    /// <summary>
    /// Writes a minimal .csproj into a fresh temp directory, optionally beside a launch profile,
    /// and returns the csproj's absolute path.
    /// </summary>
    /// <param name="applicationUrl">
    /// The profile's <c>applicationUrl</c>. <see langword="null"/> writes NO
    /// <c>launchSettings.json</c> at all; an empty string writes a profile that declares no
    /// <c>applicationUrl</c>. Measured under the pinned Aspire 13.4.2: both produce a
    /// ProjectResource carrying zero endpoint annotations, and an <c>http://…</c> URL produces
    /// exactly one named <c>"http"</c>.
    /// </param>
    public string Create(string? applicationUrl)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "vouchfx-project-fixture-" + Guid.NewGuid().ToString("N"));

        // Tracked BEFORE anything is written, so a failure part-way through still cleans up.
        _directories.Add(directory);
        Directory.CreateDirectory(directory);

        var csproj = Path.Combine(directory, "Fixture.csproj");
        File.WriteAllText(
            csproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");

        if (applicationUrl is not null)
        {
            Directory.CreateDirectory(Path.Combine(directory, "Properties"));
            File.WriteAllText(
                Path.Combine(directory, "Properties", "launchSettings.json"),
                "{\n  \"profiles\": {\n    \"Fixture\": {\n      \"commandName\": \"Project\""
                + (applicationUrl.Length == 0
                    ? string.Empty
                    : ",\n      \"applicationUrl\": \"" + applicationUrl + "\"")
                + "\n    }\n  }\n}\n");
        }

        return csproj;
    }

    /// <summary>
    /// A fixture whose project declares one plaintext HTTP endpoint — the ordinary
    /// system-under-test shape.
    /// </summary>
    public string CreateWithHttpEndpoint() => Create("http://localhost:5111");

    /// <summary>
    /// Removes every fixture. Best-effort: a temp directory that cannot be deleted must never
    /// redden a test that has already made its assertions.
    /// </summary>
    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _directories.Clear();
    }
}
