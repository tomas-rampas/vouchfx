"""Blocking workflow structure and ALL inline Bash/sh checks, with bounded tools.

actionlint 1.7.12 writes subprocess stdin before starting ShellCheck (process.go),
which deadlocks on long scripts once the OS pipe fills. Disable only that transport
and run the same ShellCheck checks from TEMP files. Python syntax is also checked.
"""

import ast
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

from docker_ci_support import BASH, ROOT, sha, workflow


# Exactly actionlint 1.7.12's expression/env false-positive exclusions. The
# standalone pre-pull contract additionally uses ShellCheck with NO exclusions.
EXCLUSIONS = "SC1091,SC2194,SC2050,SC2153,SC2154,SC2157,SC2043"


def mask_expressions(script):
    return re.sub(r"\$\{\{.*?\}\}", lambda m: "".join("\n" if c == "\n" else "_" for c in m[0]),
                  script, flags=re.DOTALL)


def inline_shell(data, job, current):
    if "shell" in current:
        return current["shell"].split()[0]
    for owner in [job, data]:
        shell = owner.get("defaults", {}).get("run", {}).get("shell")
        if shell:
            return shell.split()[0]
    labels = job.get("runs-on", [])
    if isinstance(labels, str):
        labels = [labels]
    if isinstance(labels, dict):
        labels = labels.get("labels", [])
    if any(label.lower() == "windows" or label.lower().startswith("windows-") for label in labels):
        return "pwsh"
    return "bash"


def main():
    scratch = Path(tempfile.mkdtemp(prefix="vouchfx-workflow-lint-"))
    print(f"Retained workflow lint sources and integer exits: {scratch}", flush=True)
    results = []

    def run(label, command):
        result = subprocess.run(command, cwd=ROOT, stdin=subprocess.DEVNULL,
                                capture_output=True, timeout=30)
        (scratch / (label + ".stdout")).write_bytes(result.stdout)
        (scratch / (label + ".stderr")).write_bytes(result.stderr)
        (scratch / (label + ".exit")).write_text(str(result.returncode))
        results.append({"label": label, "command": command, "exit": result.returncode})
        if result.returncode:
            print(f"{label}: exit {result.returncode}\n{result.stdout.decode(errors='replace')}{result.stderr.decode(errors='replace')}", flush=True)
        return result

    actionlint = shutil.which("actionlint")
    shellcheck = shutil.which("shellcheck")
    if not actionlint or not shellcheck or not BASH:
        raise AssertionError("actionlint 1.7.12, ShellCheck 0.11.0 and Bash are mandatory")
    version = run("actionlint-version", [actionlint, "-version"])
    if version.returncode or version.stdout.splitlines()[0] != b"v1.7.12":
        raise AssertionError("Require actionlint v1.7.12")
    version = run("shellcheck-version", [shellcheck, "--version"])
    if version.returncode or b"version: 0.11.0" not in version.stdout:
        raise AssertionError("Require ShellCheck 0.11.0")
    workflow_dir = ROOT / ".github/workflows"
    files = sorted({*workflow_dir.glob("*.yml"), *workflow_dir.glob("*.yaml")})
    if not files:
        raise AssertionError("No workflows discovered")
    run("actionlint", [actionlint, "-shellcheck", "", "-pyflakes", "", *[str(p) for p in files]])
    sources = []
    for path in files:
        data = workflow(path)
        for job_name, job in data["jobs"].items():
            for index, current in enumerate(job.get("steps", [])):
                if "run" not in current:
                    continue
                shell = inline_shell(data, job, current)
                if shell not in ["bash", "sh"]:
                    continue  # actionlint's ShellCheck rule checks precisely these shells.
                label = f"{path.stem}-{job_name}-{index}"
                source = mask_expressions(current["run"])
                setup = "set -eo pipefail\n" if shell == "bash" else "set -e\n"
                script = scratch / (label + ".sh")
                script.write_text(setup + source + "\n", encoding="utf-8", newline="\n")
                sources.append({"workflow": path.relative_to(ROOT).as_posix(), "job": job_name,
                                "step": current.get("name", str(index)), "shell": shell,
                                "run_sha256": sha(current["run"].encode()), "lint_file": str(script)})
                run(label + "-syntax", [BASH, "-n", str(script)])
                run(label + "-shellcheck", [shellcheck, "--norc", "-x", "--shell", shell,
                                           "-e", EXCLUSIONS, str(script)])
    # Shipped installer is a standalone script, so no expression/env exclusions.
    installer = ROOT / "scripts/ci/install-workflow-tools.sh"
    run("installer-syntax", [BASH, "-n", str(installer)])
    run("installer-shellcheck", [shellcheck, "--norc", "--shell=bash", str(installer)])
    for path in (ROOT / "scripts/ci").glob("*.py"):
        ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    status = 1 if any(r["exit"] for r in results) else 0
    report = {"exit": status, "workflows": len(files), "inline_scripts": len(sources),
              "sources": sources, "commands": results}
    (scratch / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    (scratch / "lint.exit").write_text(str(status))
    print(f"{len(files)} workflows; {len(sources)} inline scripts; lint exit {status}", flush=True)
    return status


if __name__ == "__main__":
    raise SystemExit(main())
