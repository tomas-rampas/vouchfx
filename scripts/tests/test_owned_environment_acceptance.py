"""Synthetic file-only controls for the hosted topology evidence admission gate."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

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

    def refusal(self):
        with self.assertRaises(ValueError) as caught:
            GATE.validate(self.directory, self.cli)
        return str(caught.exception)

    def change(self, kind, **fields):
        report = copy.deepcopy(self.reports[kind])
        report.update(fields)
        self.write(kind + ".json", report)

    def test_teardown_refusal_names_the_failing_case_and_the_evidence_read(self):
        self.change("warm-host-compiled-image", Completed=False, AttemptedCycles=7, CurrentCycle=7, Failure="boom in cycle 7")
        message = self.refusal()
        self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])
        self.assertIn("warm-host-compiled-image: case did not complete", message)
        for text in ("Completed=False", "FixtureCleanupCompleted=True", "AttemptedCycles=7", "ExpectedCycles=20", "boom in cycle 7"):
            self.assertIn(text, message)
        self.assertNotIn("compose-capabilities", message)
        self.assertNotIn("cold-host-compiler-profile", message)

    def test_teardown_refusal_names_the_failing_fixture_with_wait_diagnostics(self):
        wait = {"Purpose": "fixture-disposal", "Outcome": "bound-elapsed", "StartedUtc": "2026-01-01T00:00:00Z", "ElapsedMilliseconds": 30012,
                "BoundMilliseconds": 30000, "ContainersStillPresent": ["aspire-leftover"], "NetworksStillPresent": ["aspire-session-network-x"],
                "VolumesStillPresent": [], "BaselineContainersMissing": [], "DcpProcessesStillPresent": [], "JournalsStillPresent": [],
                "ContainerStates": ["aspire-leftover|abc|exited|Exited (0) 1 second ago"], "NetworkNames": ["aspire-session-network-x|def"], "StateReadError": None}
        self.change("cold-host-compiler-profile", FixtureCleanupCompleted=False, CleanupFailure="timed out", LastClosureWait=wait, FailedClosureWaits=[wait])
        message = self.refusal()
        self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])
        self.assertIn("cold-host-compiler-profile: fixture teardown did not complete", message)
        for text in ("FixtureCleanupCompleted=False", "CleanupFailure: timed out", "Outcome=bound-elapsed", "ElapsedMilliseconds=30012", "BoundMilliseconds=30000",
                     "aspire-leftover", "aspire-session-network-x", "Exited (0)", "non-converged baseline waits: 1"):
            self.assertIn(text, message)

    def test_teardown_refusal_names_every_failing_case_not_just_the_first(self):
        self.change("compose-capabilities", FixtureCleanupCompleted=False)
        self.change("cold-host-compiler-profile", Completed=False)
        message = self.refusal()
        self.assertIn("compose-capabilities: fixture teardown did not complete", message)
        self.assertIn("cold-host-compiler-profile: case did not complete", message)
        self.assertNotIn("warm-host-compiled-image", message)

    def test_evidence_without_the_new_fields_keeps_the_old_verdict(self):
        self.change("warm-host-compiled-image", FixtureCleanupCompleted=False)
        message = self.refusal()
        self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])
        self.assertIn("warm-host-compiled-image: fixture teardown did not complete", message)
        self.assertIn("last baseline wait: not recorded", message)
        self.assertNotIn("non-converged", message)

    def test_teardown_diagnostics_are_bounded_and_tolerate_malformed_new_fields(self):
        self.change("warm-host-compiled-image", Completed=False, Failure="x" * 5000, LastClosureWait="not-an-object",
                    FailedClosureWaits=[{"ContainersStillPresent": ["c%d" % n for n in range(100)]}, 7])
        message = self.refusal()
        self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])
        self.assertIn("(truncated)", message)
        self.assertIn("more]", message)
        self.assertLess(len(message), 6000)

    def test_passing_evidence_still_passes_with_the_new_fields_present(self):
        wait = {"Purpose": "cycle-release", "Outcome": "converged", "ElapsedMilliseconds": 400, "BoundMilliseconds": 30000}
        self.change("warm-host-compiled-image", LastClosureWait=wait, FailedClosureWaits=[])
        self.assertTrue(GATE.validate(self.directory, self.cli)["accepted"])

    def test_a_missing_report_before_an_incomplete_one_keeps_its_own_refusal(self):
        self.change("warm-host-compiled-image", Completed=False)
        (self.directory / "compose-capabilities.json").unlink()
        self.assertEqual("Missing, linked or oversized evidence: compose-capabilities.json", self.refusal().splitlines()[0])

    def test_a_missing_report_after_an_incomplete_one_does_not_hide_the_teardown_refusal(self):
        self.change("compose-capabilities", Completed=False)
        (self.directory / "warm-host-compiled-image.json").unlink()
        message = self.refusal()
        self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])
        self.assertIn("compose-capabilities: case did not complete", message)
        self.assertNotIn("warm-host-compiled-image", message)

    def test_an_earlier_checkout_mismatch_before_a_later_incomplete_case_wins(self):
        self.change("compose-capabilities", CheckoutCommit="c" * 40)
        self.change("cold-host-compiler-profile", Completed=False)
        self.assertEqual("Evidence belongs to another checkout", self.refusal().splitlines()[0])

    def test_an_earlier_first_attempt_failure_before_a_later_incomplete_case_wins(self):
        self.change("compose-capabilities", Failure="boom")
        self.change("cold-host-compiler-profile", FixtureCleanupCompleted=False)
        self.assertEqual("A first-attempt failure is present", self.refusal().splitlines()[0])

    def test_a_wrong_kind_on_an_incomplete_case_keeps_the_schema_refusal(self):
        self.change("warm-host-compiled-image", Kind="other", Completed=False)
        self.assertEqual("Wrong case/schema", self.refusal().splitlines()[0])

    def test_an_incomplete_case_before_a_later_failure_is_still_a_teardown_refusal(self):
        self.change("compose-capabilities", Completed=False)
        self.change("cold-host-compiler-profile", Failure="boom")
        message = self.refusal()
        self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])
        self.assertIn("compose-capabilities: case did not complete", message)

    def test_unhashable_or_mixed_closure_lists_degrade_instead_of_crashing(self):
        empty = {"Containers": [], "Networks": [], "Volumes": []}
        for closure in ({"Containers": [{"a": 1}], "Networks": [], "Volumes": []}, {"Containers": ["a", 1], "Networks": [], "Volumes": []},
                        {"Containers": [["x"]], "Networks": ["n"], "Volumes": []}):
            with self.subTest(closure=closure):
                self.change("warm-host-compiled-image", Completed=False, LastBefore=empty, FixtureClosureLastInventory=closure)
                message = self.refusal()
                self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])
                self.assertIn("warm-host-compiled-image: case did not complete", message)

    def test_describe_teardown_degrades_to_the_exception_type_when_anything_raises(self):
        class Hostile(dict):
            def get(self, *args):
                raise RuntimeError("hostile")
        self.assertEqual(["  warm: diagnostics unavailable: RuntimeError"], GATE.describe_teardown("warm", Hostile()))

    def test_new_wait_fields_and_a_bounded_number_of_events_are_printed(self):
        wait = {"Purpose": "cycle-release", "Outcome": "error", "ElapsedMilliseconds": 4100, "BoundMilliseconds": 30000, "Polls": 0, "LastInventoryUtc": None,
                "Error": "InvalidOperationException: Docker operation failed (exit 1) in container inspect; output withheld", "EventsReadError": None,
                "Events": ["1 container die c%d" % n for n in range(50)]}
        self.change("warm-host-compiled-image", FixtureCleanupCompleted=False, LastClosureWait=wait, FailedClosureWaits=[wait])
        message = self.refusal()
        for text in ("Polls=0", "LastInventoryUtc=null", "Error=InvalidOperationException", "in container inspect", "50 recorded, first 20 shown", "container die c19"):
            self.assertIn(text, message)
        self.assertNotIn("container die c20", message)

    def test_workflow_command_syntax_in_evidence_is_neutralised(self):
        hostile = "##[error]one ##[add-mask]two\n::error::three ::add-mask::four"
        self.change("warm-host-compiled-image", Completed=False, Failure=hostile, LastClosureWait={"Error": hostile, "Events": [hostile]})
        message = self.refusal()
        for command in ("##[", "::error", "::add-mask", "::"):
            self.assertNotIn(command, message)
        self.assertIn("##\u200b[error]one", message)
        self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])

    def test_a_later_report_that_cannot_be_parsed_does_not_change_the_refusal(self):
        # json.loads raises RecursionError (not ValueError) on this; the current case still refuses as before.
        self.change("compose-capabilities", Completed=False)
        (self.directory / "cold-host-compiler-profile.json").write_text("[" * 200000 + "]" * 200000, encoding="utf-8")
        message = self.refusal()
        self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])
        self.assertIn("compose-capabilities: case did not complete", message)
        self.assertNotIn("cold-host-compiler-profile", message)

    def test_a_second_read_that_fails_or_sees_a_complete_report_still_refuses(self):
        kind = "warm-host-compiled-image"
        self.change(kind, Completed=False)
        original = GATE.read_json
        complete = copy.deepcopy(self.reports[kind])
        for name, second in (("fails", OSError("transient")), ("sees complete", complete)):
            with self.subTest(second_read=name):
                calls = []

                def flaky(path, second=second):
                    if path.name == kind + ".json":
                        calls.append(path)
                        if len(calls) >= 2:
                            if isinstance(second, BaseException):
                                raise second
                            return copy.deepcopy(second)
                    return original(path)
                with mock.patch.object(GATE, "read_json", flaky):
                    message = self.refusal()
                self.assertEqual(1, len(calls), "the current case is described from the report already read")
                self.assertEqual("Case or fixture teardown did not complete", message.splitlines()[0])
                self.assertIn(kind + ": case did not complete", message)

    def test_the_refusal_helper_never_returns_and_raises_only_value_error(self):
        class Hostile(dict):
            def get(self, *args):
                raise RecursionError("hostile")
        for report in (copy.deepcopy(self.reports["compose-capabilities"]), Hostile()):
            with self.subTest(report=type(report).__name__):
                with self.assertRaises(ValueError) as caught:
                    GATE.refuse_incomplete_teardown(self.directory, "compose-capabilities", report)
                self.assertEqual("Case or fixture teardown did not complete", str(caught.exception).splitlines()[0])
        with mock.patch.object(GATE, "describe_teardown_lines", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                GATE.refuse_incomplete_teardown(self.directory, "compose-capabilities", {})
        with mock.patch.object(GATE, "read_json", side_effect=MemoryError):
            with self.assertRaises(ValueError):
                GATE.refuse_incomplete_teardown(self.directory, "compose-capabilities", {})

    def test_every_baseline_item_missing_from_the_wait_is_printed(self):
        wait = {"Purpose": "cycle-release", "Outcome": "bound-elapsed", "ElapsedMilliseconds": 30004, "BoundMilliseconds": 30000, "Polls": 212,
                "ContainersStillPresent": [], "NetworksStillPresent": [], "VolumesStillPresent": [], "BaselineContainersMissing": [],
                "BaselineMissing": ["dcp:4242:638000000000000000", "network:feed"], "DcpProcessesStillPresent": [], "JournalsStillPresent": [],
                "FailedInspect": None, "EventsSinceUtc": "2026-01-01T00:00:00Z", "EventsUntilUtc": "2026-01-01T00:01:31Z"}
        before = {"Containers": ["c"], "Networks": ["feed", "n"], "Volumes": [], "DcpProcesses": ["4242:638000000000000000"], "Journals": []}
        closure = {"Containers": ["c"], "Networks": ["n"], "Volumes": [], "DcpProcesses": [], "Journals": []}
        self.change("warm-host-compiled-image", Completed=False, LastBefore=before, FixtureClosureLastInventory=closure, LastClosureWait=wait, FailedClosureWaits=[wait])
        message = self.refusal()
        for text in ("BaselineMissing=[dcp:4242:638000000000000000, network:feed]", "Polls=212", "FailedInspect=null", "EventsSinceUtc=2026-01-01T00:00:00Z",
                     "EventsUntilUtc=2026-01-01T00:01:31Z", "Networks in the cycle baseline but absent at last closure check: [feed]",
                     "DcpProcesses in the cycle baseline but absent at last closure check: [4242:638000000000000000]",
                     "Containers in the cycle baseline but absent at last closure check: []"):
            self.assertIn(text, message)

    def test_a_malformed_wait_is_labelled_malformed_not_old_evidence(self):
        self.change("warm-host-compiled-image", Completed=False, LastClosureWait="not-an-object", FailedClosureWaits=[7, None])
        message = self.refusal()
        self.assertIn("last baseline wait: malformed (str instead of a wait record)", message)
        self.assertIn("non-converged wait 1: malformed (int instead of a wait record)", message)
        self.assertIn("non-converged wait 2: malformed (NoneType instead of a wait record)", message)


if __name__ == "__main__":
    unittest.main()
