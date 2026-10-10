"""Extract literal workflow source independently of yq's parsed representation."""

import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/build.yml"
PULL_NAME = "Pre-pull dependency images (warm Docker cache; fail fast on an unpullable pin)"
TEST_NAME = "Integration tests (requires=docker)"
YQ = os.environ.get("VOUCHFX_CI_YQ") or shutil.which("yq")
BASH = (os.environ.get("VOUCHFX_CI_BASH") or
        ("C:/Program Files/Git/bin/bash.exe" if os.name == "nt" else shutil.which("bash")))


def sha(data):
    return hashlib.sha256(data).hexdigest()


def workflow(path=WORKFLOW):
    if not YQ:
        raise AssertionError("yq v4.53.2 is mandatory")
    version = subprocess.run([YQ, "--version"], capture_output=True, timeout=10)
    if version.returncode or b"version v4.53.2" not in version.stdout:
        raise AssertionError(f"Require yq v4.53.2: {version.stdout!r} {version.stderr!r}")
    result = subprocess.run([YQ, "-o=json", str(path)], capture_output=True, timeout=10)
    if result.returncode:
        raise AssertionError(f"Workflow parse exit {result.returncode}: {result.stderr!r}")
    return json.loads(result.stdout)


def step(name, data=None):
    matches = [s for s in (data or workflow())["jobs"]["integration"]["steps"] if s.get("name") == name]
    if len(matches) != 1:
        raise AssertionError(f"Require one integration step named {name!r}")
    return matches[0]


def extract_pull(path=WORKFLOW):
    parsed = step(PULL_NAME, workflow(path))["run"].encode("utf-8")
    source = Path(path).read_bytes()
    if b"\r" in source:
        raise AssertionError("Workflow source must use LF")
    lines = source.splitlines(keepends=True)
    anchor = f"      - name: {PULL_NAME}\n".encode()
    if lines.count(anchor) != 1:
        raise AssertionError("Cannot identify the literal pre-pull step uniquely")
    start = lines.index(anchor) + 1
    while start < len(lines) and lines[start].startswith(b"        "):
        if lines[start] == b"        run: |\n":
            break
        start += 1
    if start == len(lines) or lines[start] != b"        run: |\n":
        raise AssertionError("Pre-pull must remain a literal run: | block")
    body = []
    for line in lines[start + 1:]:
        if line.strip() and not line.startswith(b"          "):
            break
        body.append(line[10:] if line.startswith(b"          ") else line)
    raw = b"".join(body)
    # Blank separator lines after the block are YAML chomping, not script bytes.
    raw = raw.rstrip(b"\n") + b"\n"
    if raw != parsed:
        raise AssertionError("Literal source extraction disagrees with the YAML parser")
    return raw


def image_boundary(script):
    match = re.search(rb"(?m)^for image in \\\n.*? ; do\n", script, re.DOTALL)
    if not match or re.search(rb"(?m)^for image in \\\n", script[match.end():]):
        raise AssertionError("Require one explicit image-list boundary")
    return match


def fixture_images(script, images):
    boundary = image_boundary(script)
    # ANSI-C literals keep control bytes in ARGUMENTS, not source line endings
    # (Git Bash can otherwise normalise a literal CRLF while reading a script).
    def quote(image):
        value = "".join("\\x%02x" % ord(c) if ord(c) < 32 else
                        "\\" + c if c in "\\'" else c for c in image)
        return "$'" + value + "'"

    replacement = ("for image in \\\n  " + " \\\n  ".join(quote(image) for image in images) + " ; do\n").encode()
    result = script[:boundary.start()] + replacement + script[boundary.end():]
    if result[:boundary.start()] != script[:boundary.start()] or not result.endswith(script[boundary.end():]):
        raise AssertionError("Fixture changed source outside the image-list boundary")
    return result
