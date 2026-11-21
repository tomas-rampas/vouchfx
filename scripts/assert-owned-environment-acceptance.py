#!/usr/bin/env python3
"""Validate hosted owned-environment evidence. This script never starts native products."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import stat
import re
from pathlib import Path
from typing import NoReturn

CASES = {"compose-capabilities": 1, "warm-host-compiled-image": 20, "cold-host-compiler-profile": 5}


def read_json(path: Path) -> dict:
    if path.is_symlink() or not path.is_file() or path.stat().st_size > 4 * 1024 * 1024:
        raise ValueError(f"Missing, linked or oversized evidence: {path.name}")
    value = json.loads(path.read_text(encoding="utf-8"))
    if type(value) is not dict:
        raise ValueError("Evidence must be a JSON object")
    return value


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise ValueError(reason)


def integer(value: object, expected: int) -> bool:
    return type(value) is int and value == expected


def snapshot(value: object, *, require_creation: bool = True) -> dict:
    require(type(value) is dict, "Missing resource snapshot")
    for key in ("Containers", "Networks", "Volumes", "DcpProcesses", "Journals"):
        items = value.get(key)
        require(type(items) is list and all(type(item) is str and item for item in items), "Malformed resource identity list")
        require(items == sorted(set(items)), "Resource identities must be sorted and distinct")
    require(type(value.get("Created")) is dict, "Missing native creation identities")
    require(all(type(key) is str and type(item) is str and item for key, item in value["Created"].items()), "Malformed creation identities")
    if require_creation:
        expected = {kind + ":" + item for kind, key in (("container", "Containers"), ("network", "Networks"), ("volume", "Volumes")) for item in value[key]}
        require(set(value["Created"]) == expected, "A foreign/native creation identity is missing")
    return value


def released(cycle: dict) -> None:
    before, active, after = snapshot(cycle.get("before")), snapshot(cycle.get("active"), require_creation=False), snapshot(cycle.get("after"))
    require(before == after, "Foreign identities or owned release changed the baseline")
    for kind in ("Containers", "Networks", "Volumes"):
        require(set(before[kind]).issubset(active[kind]), "An active topology lost a foreign resource")
    require(bool(set(active["Containers"]) - set(before["Containers"])), "No actual product container was observed")
    require(bool(set(active["Networks"]) - set(before["Networks"])), "No actual product network was observed")
    require(set(before["DcpProcesses"]).issubset(active["DcpProcesses"]), "An active topology lost a foreign native process")


RAW_OUTPUT_LIMIT = 1024 * 1024


def read_owned_regular(path: Path, directory: Path, limit: int) -> bytes:
    root, candidate = directory.absolute(), path.absolute()
    require(candidate.is_relative_to(root), "Raw capture escapes its owned evidence root")
    parts = (root, *(root.joinpath(*candidate.relative_to(root).parts[:index]) for index in range(1, len(candidate.relative_to(root).parts) + 1)))
    for current in parts:
        try:
            info = current.stat(follow_symlinks=False)
        except OSError as error:
            raise ValueError("Missing/unreadable owned raw capture") from error
        require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 1024, "Linked/reparse raw capture path refused")
        require(stat.S_ISREG(info.st_mode) if current == candidate else stat.S_ISDIR(info.st_mode), "Raw capture path has the wrong file type")
    require(info.st_size <= limit, "Raw capture exceeds its declared bound")
    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        with os.fdopen(os.open(candidate, flags), "rb") as source:
            opened = os.fstat(source.fileno())
            require(stat.S_ISREG(opened.st_mode) and (opened.st_dev, opened.st_ino) == (info.st_dev, info.st_ino), "Raw capture changed while opening")
            value = source.read(limit + 1)
            final = os.fstat(source.fileno())
    except OSError as error:
        raise ValueError("Unreadable owned raw capture") from error
    require(len(value) <= limit and (opened.st_size, opened.st_mtime_ns) == (final.st_size, final.st_mtime_ns), "Raw capture changed or exceeded its bound while reading")
    return value


def require_raw_stream(directory: Path, raw: Path, channel: str, binding: object, manifest: dict) -> None:
    require(type(binding) is dict and manifest.get(channel) == binding, "Cycle and per-stream capture bindings differ")
    require({"FileName", "Bytes", "Sha256", "Completed", "Error", "ObservedBytes", "LimitBytes"}.issubset(binding), "Required raw stream fields are missing")
    filename = channel.lower() + ".txt"
    require(binding.get("FileName") == filename, "Raw stream filename is not its owned fixed name")
    require(binding.get("Completed") is True and binding.get("Error") is None, "Raw stream capture is incomplete or errored")
    require(integer(binding.get("LimitBytes"), RAW_OUTPUT_LIMIT), "Raw stream limit is missing or malformed")
    size = binding.get("Bytes")
    require(type(size) is int and 0 <= size <= RAW_OUTPUT_LIMIT, "Raw stream byte count is missing or malformed")
    require(integer(binding.get("ObservedBytes"), size), "A complete stream cannot omit observed bytes")
    digest = binding.get("Sha256")
    require(type(digest) is str and re.fullmatch(r"[0-9A-F]{64}", digest) is not None, "Raw stream checksum is missing or malformed")
    value = read_owned_regular(raw / filename, directory, RAW_OUTPUT_LIMIT)
    require(len(value) == size and hashlib.sha256(value).hexdigest().upper() == digest, "Raw stream bytes do not match their reported size/hash")


TEARDOWN_REFUSAL = "Case or fixture teardown did not complete"
FIELD_LIMIT = 400
LIST_LIMIT = 20
EVENT_LINE_LIMIT = 20
ZERO_WIDTH_SPACE = "\u200b"
PROGRESS_FIELDS = ("ExpectedCycles", "CompletedCycles", "AttemptedCycles", "CurrentCycle", "LastReleaseCompleted")
WAIT_FIELDS = ("Purpose", "Outcome", "StartedUtc", "ElapsedMilliseconds", "BoundMilliseconds", "ContainersStillPresent", "NetworksStillPresent",
               "VolumesStillPresent", "BaselineContainersMissing", "BaselineMissing", "DcpProcessesStillPresent", "JournalsStillPresent", "ContainerStates",
               "NetworkNames", "StateReadError", "Polls", "LastInventoryUtc", "FailedInspect", "Error", "EventsSinceUtc", "EventsUntilUtc", "EventsReadError")
INVENTORY_KEYS = ("Containers", "Networks", "Volumes", "DcpProcesses", "Journals")
# Interrupts and interpreter exits keep their meaning; every other failure while gathering diagnostics is absorbed.
EXITS = (KeyboardInterrupt, SystemExit)


def render(value: object) -> str:
    """Render one evidence field for a log line: bounded, single-line, and explicit about absence."""
    if value is None:
        return "null"
    if type(value) is list:
        shown = ", ".join(render(item) for item in value[:LIST_LIMIT])
        return "[" + shown + (f", ... +{len(value) - LIST_LIMIT} more" if len(value) > LIST_LIMIT else "") + "]"
    text = " ".join(str(value).split())
    # Neutralise runner workflow commands, in both the current (::) and the older (##[) syntax: evidence text
    # such as a container name or an exception message must never be readable as a command.
    text = text.replace("##[", "##" + ZERO_WIDTH_SPACE + "[").replace("::", ":" + ZERO_WIDTH_SPACE + ":")
    return text if len(text) <= FIELD_LIMIT else text[:FIELD_LIMIT] + "...(truncated)"


def describe_wait(label: str, wait: object, *, may_be_absent: bool) -> list[str]:
    if wait is None and may_be_absent:
        return [f"    {label}: not recorded (evidence predates wait diagnostics, or no wait ran)"]
    if type(wait) is not dict:
        return [f"    {label}: malformed ({type(wait).__name__} instead of a wait record)"]
    lines = [f"    {label}: " + " ".join(f"{name}={render(wait[name])}" for name in WAIT_FIELDS if name in wait)]
    events = wait.get("Events")
    if type(events) is list and events:
        lines.append(f"      Docker events during this wait ({len(events)} recorded, first {min(len(events), EVENT_LINE_LIMIT)} shown):")
        lines.extend(f"        {render(event)}" for event in events[:EVENT_LINE_LIMIT])
    return lines


def describe_teardown(kind: str, report: dict) -> list[str]:
    """Diagnostics must never replace the refusal: any failure to read the evidence degrades to one line."""
    try:
        return describe_teardown_lines(kind, report)
    except EXITS:
        raise
    except BaseException as error:  # noqa: BLE001 - the refusal itself is raised by the caller
        return [f"  {kind}: diagnostics unavailable: {type(error).__name__}"]


def describe_teardown_lines(kind: str, report: dict) -> list[str]:
    roles = []
    if report.get("Completed") is not True:
        roles.append("case did not complete")
    if report.get("FixtureCleanupCompleted") is not True:
        roles.append("fixture teardown did not complete")
    lines = [f"  {kind}: " + "; ".join(roles)]
    lines.append("    markers: " + " ".join(f"{name}={render(report.get(name, '<absent>'))}" for name in ("Completed", "FixtureCleanupCompleted")))
    lines.append("    progress: " + " ".join(f"{name}={render(report.get(name, '<absent>'))}" for name in PROGRESS_FIELDS))
    for name in ("Failure", "CleanupFailure"):
        lines.append(f"    {name}: {render(report.get(name))}")
    before, closure = report.get("LastBefore"), report.get("FixtureClosureLastInventory")
    if type(before) is dict and type(closure) is dict:
        for key in INVENTORY_KEYS:
            if all(type(items) is list and all(type(item) is str for item in items) for items in (before.get(key), closure.get(key))):
                lines.append(f"    {key} present at last closure check but absent from the cycle baseline: {render(sorted(set(closure[key]) - set(before[key])))}")
                lines.append(f"    {key} in the cycle baseline but absent at last closure check: {render(sorted(set(before[key]) - set(closure[key])))}")
    lines.extend(describe_wait("last baseline wait", report.get("LastClosureWait"), may_be_absent=True))
    failed = report.get("FailedClosureWaits")
    if type(failed) is list:
        lines.append(f"    non-converged baseline waits: {len(failed)}")
        for index, wait in enumerate(failed, 1):
            lines.extend(describe_wait(f"non-converged wait {index}", wait, may_be_absent=False))
    return lines


def refuse_incomplete_teardown(directory: Path, kind: str, report: dict) -> NoReturn:
    """Always raise ValueError whose first line is the teardown refusal; never return, never raise anything else.

    Called from the per-case loop, at the position the plain refusal always had, so an earlier refusal still wins.
    The current case is described from the report already in hand. The other cases are read again only to list
    any whose teardown also did not complete; a report that cannot be read or described is skipped, since it
    keeps its own refusal for when the loop reaches it.
    """
    lines: list[str] = []
    try:
        lines.extend(describe_teardown(kind, report))
        for other in CASES:
            if other == kind:
                continue
            try:
                other_report = read_json(directory / (other + ".json"))
                if other_report.get("Completed") is not True or other_report.get("FixtureCleanupCompleted") is not True:
                    lines.extend(describe_teardown(other, other_report))
            except EXITS:
                raise
            except BaseException:  # noqa: BLE001 - diagnostics only; the refusal below is unconditional
                continue
    except EXITS:
        raise
    except BaseException:  # noqa: BLE001 - diagnostics only; the refusal below is unconditional
        pass
    try:
        message = "\n".join([TEARDOWN_REFUSAL, *lines])
    except EXITS:
        raise
    except BaseException:  # noqa: BLE001 - diagnostics only; the refusal below is unconditional
        message = TEARDOWN_REFUSAL
    raise ValueError(message)


def validate(directory: Path, cli: Path) -> dict:
    invocation = read_json(directory / "invocation.json")
    require(integer(invocation.get("schemaVersion"), 1), "Malformed invocation schema")
    for name in ("checkoutCommit", "pullRequestHeadCommit"):
        require(type(invocation.get(name)) is str and re.fullmatch(r"[0-9a-f]{40}", invocation[name]) is not None, "Missing checkout/head identity")
    cli_digest = hashlib.sha256(cli.read_bytes()).hexdigest().upper()
    require(invocation.get("builtCliSha256") == cli_digest, "The built CLI changed after preparation")
    cases = {}
    for kind, count in CASES.items():
        report = read_json(directory / (kind + ".json"))
        require(integer(report.get("SchemaVersion"), 1) and report.get("Kind") == kind, "Wrong case/schema")
        if report.get("Completed") is not True or report.get("FixtureCleanupCompleted") is not True:
            refuse_incomplete_teardown(directory, kind, report)
        require(report.get("Failure") is None and report.get("CleanupFailure") is None, "A first-attempt failure is present")
        for name in ("ExpectedCycles", "CompletedCycles", "AttemptedCycles", "CurrentCycle"):
            require(integer(report.get(name), count), "Missing, partial or non-integer cycle count")
        require(report.get("CheckoutCommit") == invocation["checkoutCommit"], "Evidence belongs to another checkout")
        require(report.get("PullRequestHeadCommit") == invocation["pullRequestHeadCommit"], "Evidence belongs to another PR head")
        require(report.get("BuiltCliSha256") == cli_digest, "Case used another built CLI")
        require(type(report.get("BuiltCliInformationalVersion")) is str and report["BuiltCliInformationalVersion"], "Missing compiled CLI version")
        cycles = report.get("Cycles")
        require(type(cycles) is list and len(cycles) == count, "Missing cycle records")
        for index, cycle in enumerate(cycles, 1):
            require(type(cycle) is dict and integer(cycle.get("index"), index) and cycle.get("completed") is True, "Cycle is incomplete or out of order")
            released(cycle)
        cases[kind] = report
    capability = cases["compose-capabilities"]["Cycles"][0]
    require(capability.get("selectedServices") == ["web"] and capability.get("actualSelectedServices") == ["web"] and capability.get("excludedServices") == ["excluded"], "Profile inclusion/exclusion was not proved")
    require(type(capability.get("expectedHostname")) is str and capability["expectedHostname"] == capability.get("actualHostname"), "Ordered interpolation files did not affect the actual HTTP hostname")
    require(capability.get("actualServiceEnvironment") == "CAPABILITY_VALUE=from-service-env-file", "Service env-file did not affect the actual container environment")
    require(capability.get("interpolationFiles") == ["first.env", "second.env"] and capability.get("serviceEnvFile") == "service.env", "Interpolation order/service-file evidence is missing")
    warm = cases["warm-host-compiled-image"]["Cycles"]
    require([cycle.get("driver") for cycle in warm] == ["inline", "compose"] * 10, "Warm path coverage must be ten inline/ten Compose")
    require(len({(cycle.get("ownerPid"), cycle.get("ownerBirthUtc")) for cycle in warm}) == 1, "Warm cycles changed process owner")
    require(all(type(cycle.get("ownerPid")) is int and cycle["ownerPid"] > 0 and type(cycle.get("ownerBirthUtc")) is str for cycle in warm), "Malformed warm process birth identity")
    images = {cycle.get("compiledImageSha256") for cycle in warm}
    require(len(images) == 1 and all(type(image) is str and re.fullmatch(r"[0-9A-F]{64}", image) for image in images), "Warm cycles did not share one compiled image")
    require(all(cycle.get("freshTableAbsent") is True and cycle.get("mutationObservedByCompiledProvider") is True for cycle in warm), "Fresh/mutated database state is unproved")
    created = [next(iter(set(cycle["active"]["Containers"]) - set(cycle["before"]["Containers"]))) for cycle in warm]
    require(len(set(created)) == 20, "Warm cycles reused a container")
    require(cases["cold-host-compiler-profile"].get("CliSha256") == cli_digest, "Cold processes did not execute the bound CLI bytes")
    cold = cases["cold-host-compiler-profile"]["Cycles"]
    require([cycle.get("driver") for cycle in cold] == ["inline", "compose", "inline", "compose", "inline"], "Cold path coverage must be three inline/two Compose")
    births = set()
    profiles = set()
    for index, cycle in enumerate(cold, 1):
        require(type(cycle.get("Pid")) is int and cycle["Pid"] > 0 and type(cycle.get("BirthUtc")) is str, "Missing cold CLI birth identity")
        births.add((cycle["Pid"], cycle["BirthUtc"]))
        require(integer(cycle.get("ExitCode"), 0), "Cold CLI did not exit zero")
        require(type(cycle.get("profile")) is str and cycle["profile"], "Missing cold writable profile")
        profiles.add(cycle["profile"])
        require(type(cycle.get("ObservedContainers")) is list and bool(cycle["ObservedContainers"]) and all(type(item) is str and item for item in cycle["ObservedContainers"]), "Cold CLI never created an observed container")
        dcp, journals = cycle.get("ObservedDcpProcesses"), cycle.get("ObservedJournals")
        require(type(dcp) is list and type(journals) is list and all(type(item) is str and item for item in dcp + journals), "Missing native/profile observations")
        if cycle["driver"] == "compose":
            require(not dcp and bool(journals) and all(item.startswith(cycle["profile"].rstrip("/") + "/") for item in journals), "Compose did not use its isolated writable profile independently of DCP")
        else:
            require(bool(dcp), "Inline cold CLI did not observe its actual DCP host")
        raw = directory / "cold-cli" / ("cold-" + str(index))
        capture_data = json.loads(read_owned_regular(raw / "captures.json", directory, 64 * 1024))
        require(type(capture_data) is dict and integer(capture_data.get("SchemaVersion"), 1) and "CaptureError" in capture_data and capture_data["CaptureError"] is None, "Per-stream capture metadata is missing or errored")
        for channel in ("Stdout", "Stderr"):
            require_raw_stream(directory, raw, channel, cycle.get(channel), capture_data)
        require((raw / "exit.txt").read_text(encoding="utf-8").strip() == "0", "Raw CLI exit does not match")
        events = [json.loads(line) for line in (raw / "events.jsonl").read_text(encoding="utf-8").splitlines()]
        steps = [event for event in events if event.get("type") == "step-completed"]
        scenarios = [event for event in events if event.get("type") == "scenario-completed"]
        require(len(steps) == 2 and {event.get("stepId") for event in steps} == {"fresh-database", "ready-database"} and all(event.get("verdict") == "PASS" for event in steps), "Cold raw step evidence is incomplete")
        require(len(scenarios) == 1 and scenarios[0].get("verdict") == "PASS", "Cold raw scenario evidence is incomplete")
    require(len(births) == 5 and len(profiles) == 5, "Cold runs did not use five process/profile identities")
    return {"schemaVersion": 1, "accepted": True, "checkoutCommit": invocation["checkoutCommit"], "pullRequestHeadCommit": invocation["pullRequestHeadCommit"],
            "builtCliSha256": cli_digest, "cases": 3, "capabilityCycles": 1, "warmCycles": 20, "coldCycles": 5, "nativeExecutionPerformedByVerifier": False}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--prepare", action="store_true")
    parser.add_argument("--checkout-sha")
    parser.add_argument("--head-sha")
    args = parser.parse_args()
    if args.prepare:
        for sha in (args.checkout_sha, args.head_sha):
            require(type(sha) is str and re.fullmatch(r"[0-9a-f]{40}", sha) is not None, "Preparation requires exact checkout and PR-head SHA")
        args.directory.mkdir(parents=True, exist_ok=False)
        value = {"schemaVersion": 1, "checkoutCommit": args.checkout_sha, "pullRequestHeadCommit": args.head_sha,
                 "builtCliSha256": hashlib.sha256(args.cli.read_bytes()).hexdigest().upper(), "actualCyclesStarted": 0}
        (args.directory / "invocation.json").write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
        print("Prepared hosted topology evidence; native execution remains in the three Docker tests.")
    else:
        value = validate(args.directory, args.cli)
        (args.directory / "acceptance.json").write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
        print(json.dumps(value))


if __name__ == "__main__":
    main()
