"""Collect new default-root DCP captures for this integration job, without deletion."""

import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import tempfile


NAME = re.compile(r"dcp-capture-([0-9]{8}T[0-9]{9}Z)(?:-[2-5])?\.log\Z")
HEADER = b"vouchfx DCP flight recorder capture\n"
ROOT_TOKEN = "${XDG_DATA_HOME:-$HOME/.local/share}/vouchfx"
MAX_ENTRIES = 4096
MAX_INVENTORY = 64
MAX_FILE = 256 * 1024
MAX_CAPTURES = 12
MAX_TOTAL = 3 * 1024 * 1024


class Refusal(ValueError):
    """Messages are fixed diagnostics, never paths or capture/environment contents."""


def digest(data):
    return hashlib.sha256(data).hexdigest()


def linked(info):
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, "st_file_attributes", 0) & 0x400)


def safe_path(value):
    path = Path(value)
    if not path.is_absolute() or ".." in path.parts:
        raise Refusal("paths must be absolute without parent traversal")
    for part in [*reversed(path.parents), path]:
        try:
            info = part.lstat()
        except FileNotFoundError:
            continue
        if linked(info):
            raise Refusal("symlink or reparse-point paths are not collectable")
    return path


def owned_path(value, source):
    path = safe_path(value)
    runner_temp = safe_path(os.environ["RUNNER_TEMP"])
    if path.parent != runner_temp or path.is_relative_to(source):
        raise Refusal("state and artefact paths must be direct runner-temp children outside the source")
    return path


def identity(info):
    return [info.st_dev, info.st_ino]


def signature(info):
    # Python 3.12 Windows lstat/fstat disagree on deprecated st_ctime; their
    # birth-time fields agree. POSIX ctime still detects metadata changes.
    created_or_changed = info.st_birthtime_ns if os.name == "nt" else info.st_ctime_ns
    return [info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, created_or_changed]


def read_regular(path, limit):
    safe_path(path)
    before = path.lstat()
    if linked(before) or not stat.S_ISREG(before.st_mode) or before.st_nlink != 1:
        raise Refusal("only single-link regular files are collectable")
    if before.st_size > limit:
        raise Refusal("file exceeds the collection byte bound")
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    with os.fdopen(descriptor, "rb") as stream:
        if signature(os.fstat(stream.fileno())) != signature(before):
            raise Refusal("file changed before collection")
        body = stream.read(limit + 1)
        after = os.fstat(stream.fileno())
    safe_path(path)
    if len(body) > limit or signature(after) != signature(before) or signature(path.lstat()) != signature(before):
        raise Refusal("file changed during collection or exceeded its bound")
    return body, signature(before)


def valid_name(name):
    match = NAME.fullmatch(name)
    if not match:
        return False
    try:
        datetime.strptime(match[1], "%Y%m%dT%H%M%S%fZ")
    except ValueError:
        return False
    return True


def inventory(source):
    safe_path(source)
    try:
        root_info = source.lstat()
    except FileNotFoundError:
        return None, {}, {}
    if not stat.S_ISDIR(root_info.st_mode):
        raise Refusal("capture source is not a directory")
    names = []
    with os.scandir(source) as entries:
        for index, entry in enumerate(entries):
            if index >= MAX_ENTRIES:
                raise Refusal("directory exceeds the inventory entry bound")
            if valid_name(entry.name):
                names.append(entry.name)
                if len(names) > MAX_INVENTORY:
                    raise Refusal("capture inventory exceeds its count bound")
    records, bodies = {}, {}
    for name in sorted(names):
        body, stamp = read_regular(source / name, MAX_FILE)
        if not body.startswith(HEADER):
            raise Refusal("matching file does not have the DCP capture header")
        records[name] = {"signature": stamp, "bytes": len(body), "sha256": digest(body)}
        bodies[name] = body
    safe_path(source)
    if identity(source.lstat()) != identity(root_info):
        raise Refusal("capture source directory changed during inventory")
    return identity(root_info), records, bodies


def run_identity():
    run_id = os.environ.get("GITHUB_RUN_ID", "")
    attempt = os.environ.get("GITHUB_RUN_ATTEMPT", "")
    job = os.environ.get("GITHUB_JOB", "")
    if not re.fullmatch(r"[0-9]{1,20}", run_id) or not re.fullmatch(r"[0-9]{1,10}", attempt):
        raise Refusal("run identity is missing or malformed")
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_-]{0,99}", job):
        raise Refusal("job identity is missing or malformed")
    return {"run_id": run_id, "run_attempt": attempt, "job": job}


def json_bytes(value):
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode()


def snapshot(source, state):
    root, records, _ = inventory(source)
    data = {"schema_version": 1, "run": run_identity(), "source_sha256": digest(str(source).encode()),
            "root_identity": root, "inventory": records}
    with state.open("xb") as stream:
        stream.write(json_bytes(data))
    print(f"DCP capture baseline: {len(records)} existing files; default root preserved.")


def collect(source, state, destination, outcome):
    raw, _ = read_regular(state, 128 * 1024)
    try:
        baseline = json.loads(raw)
    except RecursionError:
        raise Refusal("snapshot JSON nesting exceeds its parser bound") from None
    if not isinstance(baseline, dict) or baseline.get("schema_version") != 1 or baseline.get("run") != run_identity():
        raise Refusal("snapshot belongs to a different run or schema")
    if baseline.get("source_sha256") != digest(str(source).encode()):
        raise Refusal("snapshot belongs to a different capture root")
    previous = baseline["inventory"]
    if not isinstance(previous, dict) or len(previous) > MAX_INVENTORY or not all(valid_name(n) for n in previous):
        raise Refusal("snapshot inventory is malformed")
    root, records, bodies = inventory(source)
    if baseline["root_identity"] is not None and root != baseline["root_identity"]:
        raise Refusal("capture source directory was replaced after snapshot")
    for name in previous.keys() & records.keys():
        if previous[name] != records[name]:
            raise Refusal("a pre-existing capture changed after snapshot")
    names = sorted(records.keys() - previous.keys())
    if len(names) > MAX_CAPTURES or sum(records[n]["bytes"] for n in names) > MAX_TOTAL:
        raise Refusal("new captures exceed the artefact count or byte bound")
    if destination.exists():
        raise Refusal("artefact destination already exists")
    manifest = {"schema_version": 1, **run_identity(), "test_outcome": outcome,
                "source_root": ROOT_TOKEN, "baseline_capture_count": len(previous),
                "capture_count": len(names),
                "files": [{"name": n, "bytes": records[n]["bytes"], "sha256": records[n]["sha256"]} for n in names]}
    # Nothing under the upload path exists until every input and staged output
    # passed. A failed write may leave owned TEMP staging, never a partial upload.
    staging = Path(tempfile.mkdtemp(prefix="dcp-capture-staging-", dir=destination.parent))
    for name in names:
        with (staging / name).open("xb") as stream:
            stream.write(bodies[name])
    with (staging / "manifest.json").open("xb") as stream:
        stream.write(json_bytes(manifest))
    safe_path(destination)
    if destination.exists():
        raise Refusal("artefact destination appeared during staging")
    staging.rename(destination)
    print(f"DCP capture artefact: {len(names)} new files; test outcome {outcome}; existing captures excluded.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["snapshot", "collect"])
    parser.add_argument("--source", required=True)
    parser.add_argument("--state", required=True)
    parser.add_argument("--destination")
    parser.add_argument("--test-outcome", choices=["success", "failure", "cancelled", "skipped"])
    args = parser.parse_args()
    try:
        if os.environ.get("VOUCHFX_DCP_CAPTURE_DIR") or os.environ.get("VOUCHFX_DCP_CAPTURE") == "0":
            raise Refusal("default-root capture recording must remain enabled and unredirected")
        source = safe_path(args.source)
        state = owned_path(args.state, source)
        if args.mode == "snapshot":
            snapshot(source, state)
        else:
            if not args.destination or not args.test_outcome:
                raise Refusal("collection requires a destination and test outcome")
            destination = owned_path(args.destination, source)
            collect(source, state, destination, args.test_outcome)
        return 0
    except Refusal as error:
        print(f"::error::DCP capture collection refused: {error}.")
    except (OSError, ValueError, KeyError, TypeError):
        print("::error::DCP capture collection failed: input or output is invalid or inaccessible.")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
