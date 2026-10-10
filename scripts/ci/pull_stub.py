"""Fixture executable: controlled native stdout/stderr/exits, never real Docker."""

import base64
import json
import os
from pathlib import Path
import subprocess
import sys


root = Path(os.environ["VOUCHFX_CI_CASE"])
kind, *args = sys.argv[1:]
with (root / "calls.jsonl").open("a", encoding="utf-8") as log:
    log.write(json.dumps({"command": kind, "args": args}) + "\n")
if kind == "sleep":
    if len(args) != 1 or args[0] not in ["15", "30"]:
        raise AssertionError(f"Unexpected backoff: {args}")
    sys.exit(0)
if kind == "head":
    if args != ["-c", "16", "/dev/urandom"]:
        raise AssertionError(f"Unexpected token source: {args}")
    sys.exit(1)  # Explicit hard-refusal fixture, not a predictable token.
if kind == "timeout":
    if args[:3] != ["120", "docker", "pull"] or len(args) != 4:
        raise AssertionError(f"Unexpected timeout invocation: {args}")
    sys.exit(subprocess.run([os.environ["VOUCHFX_CI_BASH"], str(root / "bin/docker"), *args[2:]]).returncode)
if kind != "docker" or len(args) != 2 or args[0] != "pull":
    raise AssertionError(f"Only fixture docker pull is permitted: {kind} {args}")
scenario = json.loads((root / "scenario.json").read_text(encoding="utf-8"))
image = args[1]
if image not in scenario:
    raise AssertionError(f"Unexpected image: {image!r}")
counter_path = root / "counts.json"
counts = json.loads(counter_path.read_text()) if counter_path.exists() else {}
index = counts.get(image, 0)
counts[image] = index + 1
counter_path.write_text(json.dumps(counts), encoding="utf-8")
responses = scenario[image]
response = responses[min(index, len(responses) - 1)]
sys.stdout.buffer.write(base64.b64decode(response["stdout"]))
sys.stderr.buffer.write(base64.b64decode(response["stderr"]))
sys.exit(response["exit"])
