"""Measure the workflow's assembly scheduling with two real .NET 8 test hosts.

Python 3.12, yq 4.53.2 and the .NET 8 SDK are mandatory. Uses cached test packages
at this repository's exact versions, no feeds or Docker. Retains all TEMP evidence.
"""

import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

from docker_ci_support import ROOT, TEST_NAME, sha, step


SOURCE = r'''using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class SchedulingProbe
{
    [Fact, Trait("requires", "docker")]
    public async Task RecordsRealHostInterval()
    {
        var root = Environment.GetEnvironmentVariable("VOUCHFX_CI_INTERVAL_DIR")!;
        var assembly = Assembly.GetExecutingAssembly().GetName().Name!;
        var started = Stopwatch.GetTimestamp();
        var startedUtc = DateTimeOffset.UtcNow;
        await Task.Delay(5000);
        var ended = Stopwatch.GetTimestamp();
        File.WriteAllText(Path.Combine(root, assembly + ".json"), JsonSerializer.Serialize(new
        {
            assembly, pid = Environment.ProcessId, started, ended,
            frequency = Stopwatch.Frequency, startedUtc, endedUtc = DateTimeOffset.UtcNow
        }));
    }

    [Fact]
    public void MustBeExcludedByTheActualDockerFilter() => Assert.Fail("requires=docker filter was lost");
}
'''


def main():
    scratch = Path(tempfile.mkdtemp(prefix="vouchfx-assembly-scheduling-"))
    print(f"Retained real .NET scheduling evidence: {scratch}", flush=True)
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise AssertionError(".NET 8 SDK is mandatory")
    workflow_command = step(TEST_NAME)["run"]
    arguments = shlex.split(workflow_command)
    if arguments[:3] != ["dotnet", "test", "vouchfx.sln"] or arguments.count("-m:1") != 1:
        raise AssertionError("Require the actual solution command and exactly one -m:1 lever")
    (scratch / "workflow-command.json").write_text(json.dumps({
        "run": workflow_command, "sha256": sha(workflow_command.encode()), "arguments": arguments,
    }, indent=2))
    (scratch / "global.json").write_bytes((ROOT / "global.json").read_bytes())
    (scratch / "NuGet.Config").write_text("<configuration><packageSources><clear /></packageSources></configuration>\n")
    packages = {p.attrib["Include"]: p.attrib["Version"] for p in
                ET.parse(ROOT / "Directory.Packages.props").getroot().iter("PackageVersion")}

    def run(label, command, env=None):
        result = subprocess.run(command, cwd=scratch, env=env, capture_output=True, timeout=180)
        (scratch / (label + ".stdout")).write_bytes(result.stdout)
        (scratch / (label + ".stderr")).write_bytes(result.stderr)
        (scratch / (label + ".exit")).write_text(str(result.returncode))
        if result.returncode:
            raise AssertionError(f"{label}: exit {result.returncode}; see {scratch}")
        return result

    sdk = run("sdk", [dotnet, "--version"]).stdout.decode().strip()
    if not sdk.startswith("8.0."):
        raise AssertionError(f"Expected actual .NET 8 SDK, got {sdk}")
    run("solution", [dotnet, "new", "sln", "--name", "Scheduling"])
    projects = []
    for name in ["ProbeA", "ProbeB"]:
        folder = scratch / name
        folder.mkdir()
        project = folder / (name + ".csproj")
        references = "\n".join(f'    <PackageReference Include="{p}" Version="{packages[p]}" />'
                               for p in ["Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio", "coverlet.collector"])
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable><IsTestProject>true</IsTestProject><IsPackable>false</IsPackable>
    <NuGetAudit>false</NuGetAudit>
    <VSTestLogger>trx%3bLogFilePrefix=$(MSBuildProjectName)</VSTestLogger>
  </PropertyGroup>
  <ItemGroup>
{references}
  </ItemGroup>
</Project>
''')
        (folder / "SchedulingProbe.cs").write_text(SOURCE)
        projects.append(str(project))
    run("solution-add", [dotnet, "sln", "Scheduling.sln", "add", *projects])
    run("restore", [dotnet, "restore", "Scheduling.sln"])
    run("build", [dotnet, "build", "Scheduling.sln", "-c", "Release", "--no-restore"])
    phases = {}
    for phase in ["control", "serial"]:
        intervals = scratch / phase / "intervals"
        intervals.mkdir(parents=True)
        command = arguments.copy()
        command[0] = dotnet
        command[2] = "Scheduling.sln"
        command[command.index("--results-directory") + 1] = str(scratch / phase / "results")
        if phase == "control":
            command.remove("-m:1")  # Only scheduling differs from the actual workflow.
        env = os.environ.copy()
        env["VOUCHFX_CI_INTERVAL_DIR"] = str(intervals)
        run(phase, command, env=env)
        measured = [json.loads(p.read_text()) for p in sorted(intervals.glob("*.json"))]
        if len(measured) != 2 or len({m["pid"] for m in measured}) != 2:
            raise AssertionError(f"{phase}: require intervals from two distinct actual test hosts")
        if any(m["ended"] <= m["started"] or m["frequency"] != measured[0]["frequency"] for m in measured):
            raise AssertionError(f"{phase}: invalid real process interval")
        overlap = max(0, min(m["ended"] for m in measured) - max(m["started"] for m in measured)) / measured[0]["frequency"]
        trx_counts = []
        for trx in sorted((scratch / phase / "results").glob("*.trx")):
            counters = ET.parse(trx).getroot().find(".//{*}Counters").attrib
            if any(int(counters[key]) != expected for key, expected in
                   [("total", 1), ("executed", 1), ("passed", 1), ("failed", 0)]):
                raise AssertionError(f"{phase}: incorrect selected test counters: {counters}")
            trx_counts.append({"path": str(trx), "counters": counters})
        if len(trx_counts) != 2:
            raise AssertionError(f"{phase}: require two distinct per-project TRX files")
        phases[phase] = {"command": command, "exit": 0, "overlap_seconds": overlap,
                         "intervals": measured, "trx": trx_counts}
    proved = phases["control"]["overlap_seconds"] > 0 and phases["serial"]["overlap_seconds"] == 0
    report = {"sdk": sdk, "root": str(scratch), "phases": phases, "proved": proved,
              "note": "Real local test hosts; no Docker, hosted CI or branch-protection proof."}
    (scratch / "report.json").write_text(json.dumps(report, indent=2))
    (scratch / "probe.exit").write_text("0" if proved else "1")
    print(json.dumps({"sdk": sdk, "control_overlap_seconds": phases["control"]["overlap_seconds"],
                      "serial_overlap_seconds": phases["serial"]["overlap_seconds"],
                      "proved": proved, "report": str(scratch / "report.json")}), flush=True)
    return 0 if proved else 1


if __name__ == "__main__":
    raise SystemExit(main())
