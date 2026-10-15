"""Synthetic file-only controls for the hosted topology evidence admission gate."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[1] / "assert-owned-environment-acceptance.py"
SPEC = importlib.util.spec_from_file_location("owned_environment_acceptance", MODULE)
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)


class EvidenceAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="synthetic-owned-environment-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.cli = self.directory / "synthetic-built-cli.bin"
        self.cli.write_bytes(b"inert file-only CLI checksum control")
        self.digest = hashlib.sha256(self.cli.read_bytes()).hexdigest().upper()
        self.invocation = {"schemaVersion": 1, "checkoutCommit": "a" * 40, "pullRequestHeadCommit": "b" * 40, "builtCliSha256": self.digest}
        self.write("invocation.json", self.invocation)
        self.reports = {}
        for kind, count in GATE.CASES.items():
            report = {"SchemaVersion": 1, "Kind": kind, "ExpectedCycles": count, "CompletedCycles": count, "AttemptedCycles": count, "CurrentCycle": count,
                      "Completed": True, "FixtureCleanupCompleted": True, "Failure": None, "CleanupFailure": None,
                      "CheckoutCommit": "a" * 40, "PullRequestHeadCommit": "b" * 40, "BuiltCliSha256": self.digest,
                      "BuiltCliInformationalVersion": "synthetic-1.0.2", "CliSha256": self.digest, "Cycles": []}
            for index in range(1, count + 1):
                before = {"Containers": ["foreign-container"], "Networks": ["foreign-network"], "Volumes": ["foreign-volume"],
                          "Created": {kind + ":foreign-" + kind: "synthetic-original-birth" for kind in ("container", "network", "volume")}, "DcpProcesses": [], "Journals": []}
                active = copy.deepcopy(before)
                active["Containers"] = ["foreign-container", "owned-container-" + str(index)]
                active["Networks"] = ["foreign-network", "owned-network-" + str(index)]
                cycle = {"index": index, "completed": True, "before": before, "active": active, "after": copy.deepcopy(before)}
                if kind == "compose-capabilities":
                    cycle.update(selectedServices=["web"], actualSelectedServices=["web"], excludedServices=["excluded"], expectedHostname="synthetic-last-file-hostname",
                                 actualHostname="synthetic-last-file-hostname", actualServiceEnvironment="CAPABILITY_VALUE=from-service-env-file", interpolationFiles=["first.env", "second.env"], serviceEnvFile="service.env")
                elif kind == "warm-host-compiled-image":
                    cycle.update(driver="inline" if index % 2 else "compose", ownerPid=100, ownerBirthUtc="synthetic-one-birth",
                                 compiledImageSha256="C" * 64, freshTableAbsent=True, mutationObservedByCompiledProvider=True)
                else:
                    driver = "inline" if index % 2 else "compose"
                    profile = "/synthetic/profiles/cold-" + str(index)
                    cycle.update(driver=driver, Pid=200 + index, BirthUtc="synthetic-birth-" + str(index), ExitCode=0,
                                 profile=profile, ObservedContainers=["owned-container-" + str(index)],
                                 ObservedDcpProcesses=["synthetic-dcp-birth"] if driver == "inline" else [],
                                 ObservedJournals=[profile + "/vouchfx/environments/vouchfx-owned.json"] if driver == "compose" else [])
                    raw = self.directory / "cold-cli" / ("cold-" + str(index))
                    raw.mkdir(parents=True)
                    captures = {"SchemaVersion": 1, "CaptureError": None}
                    for channel, payload in (("Stdout", b"synthetic retained stdout\n"), ("Stderr", b"")):
                        filename = channel.lower() + ".txt"
                        (raw / filename).write_bytes(payload)
                        binding = {"FileName": filename, "Bytes": len(payload), "Sha256": hashlib.sha256(payload).hexdigest().upper(),
                                   "Completed": True, "Error": None, "ObservedBytes": len(payload), "LimitBytes": 1024 * 1024}
                        captures[channel] = binding
                        cycle[channel] = copy.deepcopy(binding)
                    (raw / "captures.json").write_text(json.dumps(captures), encoding="utf-8")
                    (raw / "exit.txt").write_text("0\n", encoding="utf-8")
                    events = [{"type": "step-completed", "stepId": step, "verdict": "PASS"} for step in ("fresh-database", "ready-database")]
                    events.append({"type": "scenario-completed", "verdict": "PASS"})
                    (raw / "events.jsonl").write_text("\n".join(json.dumps(event) for event in events), encoding="utf-8")
                report["Cycles"].append(cycle)
            self.reports[kind] = report
            self.write(kind + ".json", report)

    def write(self, name, value):
        (self.directory / name).write_text(json.dumps(value), encoding="utf-8")

    def reject_report(self, kind, mutate):
        changed = copy.deepcopy(self.reports[kind])
        mutate(changed)
        self.write(kind + ".json", changed)
        with self.assertRaises((ValueError, TypeError, KeyError)):
            GATE.validate(self.directory, self.cli)
        self.write(kind + ".json", self.reports[kind])

    def test_valid_synthetic_packet_preserves_distinct_checkout_and_pr_head(self):
        result = GATE.validate(self.directory, self.cli)
        self.assertTrue(result["accepted"])
        self.assertNotEqual(result["checkoutCommit"], result["pullRequestHeadCommit"])
        self.assertEqual((20, 5), (result["warmCycles"], result["coldCycles"]))
        self.assertFalse(result["nativeExecutionPerformedByVerifier"])

    def test_cycle_counters_reject_booleans_strings_floats_negative_and_missing(self):
        for kind in GATE.CASES:
            for field in ("ExpectedCycles", "CompletedCycles", "AttemptedCycles", "CurrentCycle"):
                for value in (True, False, -1, 1.0, "1", None):
                    with self.subTest(kind=kind, field=field, value=value):
                        self.reject_report(kind, lambda report, f=field, v=value: report.update({f: v}))
                with self.subTest(kind=kind, field=field, value="missing"):
                    self.reject_report(kind, lambda report, f=field: report.pop(f))

    def test_missing_partial_or_failed_cases_are_refused(self):
        for kind in GATE.CASES:
            for field, value in (("Completed", False), ("FixtureCleanupCompleted", False), ("Failure", "first failure"), ("CleanupFailure", "teardown failure")):
                with self.subTest(kind=kind, field=field):
                    self.reject_report(kind, lambda report, f=field, v=value: report.update({f: v}))
            self.reject_report(kind, lambda report: report["Cycles"].pop())
        (self.directory / "compose-capabilities.json").unlink()
        with self.assertRaises(ValueError):
            GATE.validate(self.directory, self.cli)

    def test_foreign_creation_and_resource_and_native_closure_are_required(self):
        kind = "warm-host-compiled-image"
        for field in ("Containers", "Networks", "Volumes", "DcpProcesses", "Journals"):
            with self.subTest(field=field):
                self.reject_report(kind, lambda report, f=field: report["Cycles"][0]["after"][f].append("leaked-owned-identity"))
        self.reject_report(kind, lambda report: report["Cycles"][0]["after"]["Created"].update({"container:foreign-container": "replacement-birth"}))
        self.reject_report(kind, lambda report: report["Cycles"][0]["before"].update(Created={}))
        self.reject_report(kind, lambda report: report["Cycles"][0]["active"].update({"Containers": ["foreign-container"]}))

    def test_profile_and_env_file_and_path_coverage_cannot_be_waived(self):
        self.reject_report("compose-capabilities", lambda report: report["Cycles"][0].update(selectedServices=["web", "excluded"]))
        self.reject_report("compose-capabilities", lambda report: report["Cycles"][0].update(interpolationFiles=["second.env", "first.env"]))
        self.reject_report("compose-capabilities", lambda report: report["Cycles"][0].update(actualHostname="wrong-first-value"))
        self.reject_report("compose-capabilities", lambda report: report["Cycles"][0].update(actualServiceEnvironment="CAPABILITY_VALUE=wrong"))
        self.reject_report("compose-capabilities", lambda report: report["Cycles"][0].pop("serviceEnvFile"))
        self.reject_report("warm-host-compiled-image", lambda report: report["Cycles"][0].update(driver="compose"))
        self.reject_report("cold-host-compiler-profile", lambda report: report["Cycles"][0].update(driver="compose"))

    def test_warm_image_owner_state_and_fresh_container_identity_are_required(self):
        kind = "warm-host-compiled-image"
        for field, value in (("compiledImageSha256", "D" * 64), ("ownerPid", 101), ("freshTableAbsent", False), ("mutationObservedByCompiledProvider", False)):
            with self.subTest(field=field):
                self.reject_report(kind, lambda report, f=field, v=value: report["Cycles"][0].update({f: v}))
        self.reject_report(kind, lambda report: report["Cycles"][1]["active"].update(Containers=["foreign-container", "owned-container-1"]))

    def test_cold_cli_raw_exit_event_profile_and_native_observations_are_required(self):
        kind = "cold-host-compiler-profile"
        self.reject_report(kind, lambda report: report.update(CliSha256="F" * 64))
        for field, value in (("ExitCode", False), ("Pid", True), ("ObservedContainers", []), ("ObservedDcpProcesses", [])):
            with self.subTest(field=field):
                self.reject_report(kind, lambda report, f=field, v=value: report["Cycles"][0].update({f: v}))
        self.reject_report(kind, lambda report: report["Cycles"][1].update(ObservedJournals=["/foreign/profile/vouchfx-owned.json"]))
        raw = self.directory / "cold-cli" / "cold-1"
        (raw / "exit.txt").write_text("4", encoding="utf-8")
        with self.assertRaises(ValueError):
            GATE.validate(self.directory, self.cli)
        (raw / "exit.txt").write_text("0", encoding="utf-8")
        (raw / "events.jsonl").write_text(json.dumps({"type": "scenario-completed", "verdict": "PASS"}), encoding="utf-8")
        with self.assertRaises(ValueError):
            GATE.validate(self.directory, self.cli)

    def reject_capture(self, index, channel, mutate):
        kind = "cold-host-compiler-profile"
        changed = copy.deepcopy(self.reports[kind])
        raw = self.directory / "cold-cli" / ("cold-" + str(index))
        original = (raw / "captures.json").read_bytes()
        manifest = json.loads(original)
        mutate(changed["Cycles"][index - 1][channel])
        manifest[channel] = copy.deepcopy(changed["Cycles"][index - 1][channel])
        self.write(kind + ".json", changed)
        (raw / "captures.json").write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaises(ValueError):
            GATE.validate(self.directory, self.cli)
        self.write(kind + ".json", self.reports[kind])
        (raw / "captures.json").write_bytes(original)

    def test_each_of_ten_raw_files_is_required(self):
        for index in range(1, 6):
            for channel in ("stdout", "stderr"):
                with self.subTest(index=index, channel=channel):
                    file = self.directory / "cold-cli" / ("cold-" + str(index)) / (channel + ".txt")
                    original = file.read_bytes()
                    file.unlink()
                    with self.assertRaises(ValueError):
                        GATE.validate(self.directory, self.cli)
                    file.write_bytes(original)

    def test_raw_capture_checksum_size_type_completion_error_and_observed_bytes_are_strict(self):
        for channel in ("Stdout", "Stderr"):
            for field, value in (("Bytes", True), ("Bytes", -1), ("Bytes", 1.0), ("Bytes", "0"), ("Bytes", None),
                                 ("Bytes", 1024 * 1024 + 1), ("Sha256", "F" * 64), ("Sha256", None), ("Sha256", False),
                                 ("Completed", False), ("Completed", 1), ("Error", "IOException"), ("Error", False),
                                 ("ObservedBytes", True), ("ObservedBytes", -1), ("ObservedBytes", 1.0),
                                 ("LimitBytes", True), ("LimitBytes", 1024 * 1024 + 1), ("FileName", "../outside.txt")):
                with self.subTest(channel=channel, field=field, value=value):
                    self.reject_capture(1, channel, lambda binding, f=field, v=value: binding.update({f: v}))
            for field in ("Bytes", "Sha256", "Completed", "Error", "ObservedBytes", "LimitBytes", "FileName"):
                with self.subTest(channel=channel, field=field, value="missing"):
                    self.reject_capture(1, channel, lambda binding, f=field: binding.pop(f))

    def test_raw_tampering_partial_outputs_and_capture_manifest_are_refused(self):
        raw = self.directory / "cold-cli" / "cold-1"
        for name in ("stdout.txt", "stderr.txt"):
            original = (raw / name).read_bytes()
            for payload in (original + b"tamper", b"x" * (1024 * 1024 + 1)):
                with self.subTest(name=name, payload_bytes=len(payload)):
                    (raw / name).write_bytes(payload)
                    with self.assertRaises(ValueError):
                        GATE.validate(self.directory, self.cli)
            (raw / name).write_bytes(original)
        manifest = (raw / "captures.json").read_bytes()
        for changed in ({"SchemaVersion": 1, "CaptureError": "reader-failed", "Stdout": None, "Stderr": None},
                        {"SchemaVersion": True, "CaptureError": None, "Stdout": None, "Stderr": None}):
            (raw / "captures.json").write_text(json.dumps(changed), encoding="utf-8")
            with self.assertRaises(ValueError):
                GATE.validate(self.directory, self.cli)
        (raw / "captures.json").unlink()
        with self.assertRaises(ValueError):
            GATE.validate(self.directory, self.cli)
        (raw / "captures.json").write_bytes(manifest)
        self.reject_capture(1, "Stdout", lambda binding: binding.update(Completed=False, Error="OperationCanceledException"))

    def test_owned_regular_reader_refuses_directory_leaf_and_parent_symlink(self):
        target = self.directory / "ordinary.bin"
        target.write_bytes(b"inert reader control")
        directory_leaf = self.directory / "directory.bin"
        directory_leaf.mkdir()
        with self.assertRaises(ValueError):
            GATE.read_owned_regular(directory_leaf, self.directory, 100)
        linked = self.directory / "linked.bin"
        try:
            linked.symlink_to(target)
        except OSError as error:
            print("SYMLINK_CAPABILITY_UNAVAILABLE: " + type(error).__name__ + "; directory-leaf rejection measured, link checks source-reviewed only")
            return
        with self.assertRaises(ValueError):
            GATE.read_owned_regular(linked, self.directory, 100)
        linked.unlink()
        actual_directory = self.directory / "actual-subdirectory"
        actual_directory.mkdir()
        (actual_directory / "data.bin").write_bytes(b"inert parent-link control")
        alias = self.directory / "parent-link"
        alias.symlink_to(actual_directory, target_is_directory=True)
        with self.assertRaises(ValueError):
            GATE.read_owned_regular(alias / "data.bin", self.directory, 100)

    def test_correlated_size_and_checksum_and_manifest_tampering_are_refused(self):
        for index in range(1, 6):
            for channel in ("Stdout", "Stderr"):
                with self.subTest(index=index, channel=channel):
                    self.reject_capture(index, channel, lambda binding: binding.update(Sha256="0" * 64))
                    self.reject_capture(index, channel, lambda binding: binding.update(Bytes=binding["Bytes"] + 1, ObservedBytes=binding["ObservedBytes"] + 1))
        raw = self.directory / "cold-cli" / "cold-1"
        original = (raw / "captures.json").read_bytes()
        changed = json.loads(original)
        changed["Stdout"]["Bytes"] += 1
        (raw / "captures.json").write_text(json.dumps(changed), encoding="utf-8")
        with self.assertRaises(ValueError):
            GATE.validate(self.directory, self.cli)
        (raw / "captures.json").write_bytes(original)

    def test_zero_and_exactly_bounded_legitimate_raw_files_are_valid(self):
        raw = self.directory / "cold-cli" / "cold-1"
        self.assertEqual(b"", (raw / "stderr.txt").read_bytes())
        payload = b"x" * (1024 * 1024)
        (raw / "stdout.txt").write_bytes(payload)
        report = copy.deepcopy(self.reports["cold-host-compiler-profile"])
        binding = report["Cycles"][0]["Stdout"]
        binding.update(Bytes=len(payload), ObservedBytes=len(payload), Sha256=hashlib.sha256(payload).hexdigest().upper())
        self.write("cold-host-compiler-profile.json", report)
        manifest = json.loads((raw / "captures.json").read_text(encoding="utf-8"))
        manifest["Stdout"] = copy.deepcopy(binding)
        (raw / "captures.json").write_text(json.dumps(manifest), encoding="utf-8")
        self.assertTrue(GATE.validate(self.directory, self.cli)["accepted"])

    def test_build_and_source_identity_must_match(self):
        kind = "warm-host-compiled-image"
        for field, value in (("CheckoutCommit", "c" * 40), ("PullRequestHeadCommit", "c" * 40), ("BuiltCliSha256", "E" * 64), ("BuiltCliInformationalVersion", None)):
            with self.subTest(field=field):
                self.reject_report(kind, lambda report, f=field, v=value: report.update({f: v}))
        self.cli.write_bytes(b"changed inert checksum control")
        with self.assertRaises(ValueError):
            GATE.validate(self.directory, self.cli)


if __name__ == "__main__":
    unittest.main()
