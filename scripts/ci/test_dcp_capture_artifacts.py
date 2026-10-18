"""Real-files/native-process tests of the current YAML DCP capture steps."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from docker_ci_support import BASH, ROOT, TEST_NAME, workflow


SCRATCH = Path(tempfile.mkdtemp(prefix="vouchfx-ci-capture-fixtures-"))
SNAPSHOT = "Snapshot default-root DCP captures"
COLLECT = "Collect new default-root DCP captures"
UPLOAD = "Upload DCP capture artefact"
CAPTURE = "dcp-capture-20261011T010203004Z.log"
OLD = "dcp-capture-20261010T010203004Z.log"
BODY = b"vouchfx DCP flight recorder capture\nwritten: 2026-10-11T01:02:03.004Z\npassword=ephemeral-test-only\n"


class DcpCaptureArtefactTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.yaml = workflow()
        cls.steps = cls.yaml["jobs"]["integration"]["steps"]
        print(f"Retained real capture fixtures and integer exits: {SCRATCH}", flush=True)

    def case(self):
        root = Path(tempfile.mkdtemp(prefix="case-", dir=SCRATCH))
        source = root / "data/vouchfx"
        source.mkdir(parents=True)
        runner = root / "runner"
        runner.mkdir()
        env = os.environ.copy()
        env.pop("VOUCHFX_DCP_CAPTURE_DIR", None)
        env.pop("VOUCHFX_DCP_CAPTURE", None)
        env.update(XDG_DATA_HOME=source.parent.as_posix(), RUNNER_TEMP=runner.as_posix(),
                   GITHUB_RUN_ID="477", GITHUB_RUN_ATTEMPT="1", GITHUB_JOB="integration",
                   VOUCHFX_CI_TEST_OUTCOME="success")
        # Real Python is resolved explicitly; Git Bash's /usr/bin PATH prefix
        # must not select Windows' Store alias or a different interpreter.
        env["VOUCHFX_CI_FIXTURE_PYTHON"] = Path(sys.executable).as_posix()
        return root, source, runner, env

    def named(self, name):
        matches = [step for step in self.steps if step.get("name") == name]
        self.assertEqual(1, len(matches), f"Current workflow lacks unique {name!r}")
        return matches[0]

    def execute(self, name, case, expected=0, override=None):
        root, _, _, env = case
        command = self.named(name)["run"] if override is None else override
        # This only resolves the interpreter identity; the current YAML script
        # itself is passed unchanged to the native shell via positional argument.
        launcher = 'python() { "$VOUCHFX_CI_FIXTURE_PYTHON" "$@"; }; export -f python; eval "$1"'
        result = subprocess.run([BASH, "--noprofile", "--norc", "-c", launcher, "capture-fixture", command],
                                cwd=ROOT, env=env, capture_output=True, timeout=30)
        label = "snapshot" if name == SNAPSHOT else "collect"
        (root / (label + ".stdout")).write_bytes(result.stdout)
        (root / (label + ".stderr")).write_bytes(result.stderr)
        (root / (label + ".exit")).write_text(str(result.returncode))
        (root / (label + ".script")).write_text(command, encoding="utf-8", newline="\n")
        self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
        return result

    def artefact(self, case):
        upload = self.named(UPLOAD)
        value = upload["with"]["path"]
        self.assertEqual("${{ runner.temp }}/vouchfx-dcp-captures", value)
        destination = Path(value.replace("${{ runner.temp }}", case[3]["RUNNER_TEMP"]))
        return destination, json.loads((destination / "manifest.json").read_text())

    def refuse_collection(self, case, reason):
        result = self.execute(COLLECT, case, expected=1)
        self.assertIn(b"::error::DCP capture collection", result.stdout)
        self.assertIn(reason.encode(), result.stdout)
        self.assertFalse((case[2] / "vouchfx-dcp-captures").exists())
        return result

    def link_directory(self, case, path, target):
        if os.name == "nt":
            command = ["pwsh", "-NoProfile", "-NonInteractive", "-Command",
                       "New-Item -ItemType Junction -Path '" + str(path).replace("'", "''") +
                       "' -Target '" + str(target).replace("'", "''") + "' -ErrorAction Stop | Out-Null"]
            result = subprocess.run(command, capture_output=True, timeout=30)
            (case[0] / "reparse-create.exit").write_text(str(result.returncode))
            (case[0] / "reparse-create.stderr").write_bytes(result.stderr)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertTrue(path.lstat().st_file_attributes & 0x400)
        else:
            path.symlink_to(target, target_is_directory=True)
            self.assertTrue(path.is_symlink())

    def test_current_workflow_reaches_real_capture_after_failed_test(self):
        case = self.case()
        # The pre-fix control reaches a real capture before requiring the missing
        # workflow steps, so absence cannot be mistaken for a no-capture success.
        proof = case[1] / CAPTURE
        proof.write_bytes(BODY)
        (case[0] / "real-capture-before-contract.json").write_text(json.dumps({
            "name": CAPTURE, "bytes": proof.stat().st_size, "sha256": hashlib.sha256(BODY).hexdigest(),
            "current_upload_paths": [s.get("with", {}).get("path") for s in self.steps if "actions/upload-artifact@" in s.get("uses", "")]}))
        self.named(SNAPSHOT)
        # Use a distinct fresh case for the real before/test/after sequence.
        case = self.case()
        self.execute(SNAPSHOT, case)
        failed = subprocess.run([sys.executable, "-c", "import sys; sys.exit(7)"], capture_output=True)
        (case[0] / "test-step.exit").write_text(str(failed.returncode))
        self.assertEqual(7, failed.returncode)
        case[3]["VOUCHFX_CI_TEST_OUTCOME"] = "failure"
        (case[1] / CAPTURE).write_bytes(BODY)
        self.execute(COLLECT, case)
        destination, manifest = self.artefact(case)
        self.assertEqual(BODY, (destination / CAPTURE).read_bytes())
        self.assertEqual(7, int((case[0] / "test-step.exit").read_text()))
        self.assertEqual("failure", manifest["test_outcome"])
        self.assertEqual(1, manifest["capture_count"])
        self.assertEqual(hashlib.sha256(BODY).hexdigest(), manifest["files"][0]["sha256"])
        self.assertEqual({"schema_version", "run_id", "run_attempt", "job", "test_outcome", "source_root",
                          "baseline_capture_count", "capture_count", "files"}, set(manifest))
        self.assertEqual("${XDG_DATA_HOME:-$HOME/.local/share}/vouchfx", manifest["source_root"])
        text = (destination / "manifest.json").read_text()
        self.assertNotIn(str(case[0]), text)
        self.assertNotIn("ephemeral-test-only", text)
        self.assertEqual([CAPTURE, "manifest.json"], sorted(p.name for p in destination.iterdir()))

    def test_workflow_preserves_default_guard_and_always_collection(self):
        job = self.yaml["jobs"]["integration"]
        serialised = json.dumps({"env": self.yaml.get("env", {}), "job": job})
        self.assertNotIn("secrets.", serialised)
        self.assertNotIn("VOUCHFX_DCP_CAPTURE_DIR", serialised)
        self.assertNotIn('"VOUCHFX_DCP_CAPTURE"', serialised)
        self.assertNotIn("continue-on-error", job)
        before, after, upload = self.named(SNAPSHOT), self.named(COLLECT), self.named(UPLOAD)
        test = self.named(TEST_NAME)
        self.assertEqual("docker-tests", test["id"])
        self.assertNotIn("continue-on-error", test)
        self.assertEqual("always()", before["if"])
        self.assertEqual("always()", after["if"])
        self.assertEqual("dcp-captures", after["id"])
        self.assertEqual({"VOUCHFX_CI_TEST_OUTCOME": "${{ steps.docker-tests.outcome }}"}, after["env"])
        self.assertEqual("always() && steps.dcp-captures.outcome == 'success'", upload["if"])
        self.assertLess(self.steps.index(before), self.steps.index(test))
        self.assertLess(self.steps.index(test), self.steps.index(after))
        self.assertLess(self.steps.index(after), self.steps.index(upload))
        self.assertRegex(upload["uses"], r"\Aactions/upload-artifact@[0-9a-f]{40}\Z")
        self.assertEqual(7, upload["with"]["retention-days"])
        self.assertEqual("error", upload["with"]["if-no-files-found"])
        self.assertEqual("integration-dcp-captures", upload["with"]["name"])

    def test_no_capture_is_explicit_zero_manifest(self):
        case = self.case()
        # Missing default root is normal; do not create it just to collect zero.
        case[3]["XDG_DATA_HOME"] = (case[0] / "absent-data").as_posix()
        self.execute(SNAPSHOT, case)
        self.execute(COLLECT, case)
        destination, manifest = self.artefact(case)
        self.assertEqual(0, manifest["capture_count"])
        self.assertEqual([], manifest["files"])
        self.assertEqual(["manifest.json"], [p.name for p in destination.iterdir()])
        self.assertFalse((case[0] / "absent-data").exists())

    def test_unchanged_old_capture_is_excluded_without_modification(self):
        case = self.case()
        old = case[1] / OLD
        old.write_bytes(BODY)
        self.execute(SNAPSHOT, case)
        (case[1] / CAPTURE).write_bytes(BODY)
        self.execute(COLLECT, case)
        destination, manifest = self.artefact(case)
        self.assertEqual(1, manifest["baseline_capture_count"])
        self.assertFalse((destination / OLD).exists())
        self.assertEqual(BODY, old.read_bytes())

    def test_changed_old_capture_is_refused(self):
        case = self.case()
        old = case[1] / OLD
        old.write_bytes(BODY)
        self.execute(SNAPSHOT, case)
        old.write_bytes(BODY + b"changed\n")
        self.refuse_collection(case, "pre-existing capture changed")
        self.assertEqual(BODY + b"changed\n", old.read_bytes())

    def test_unrelated_names_and_nested_files_are_not_uploaded(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        for name in ["credentials.env", "dcp-capture-not-a-timestamp.log", "dcp-capture-20269999T999999999Z.log", ".hidden.log"]:
            (case[1] / name).write_bytes(b"private non-capture")
        nested = case[1] / "nested"
        nested.mkdir()
        (nested / CAPTURE).write_bytes(BODY)
        self.execute(COLLECT, case)
        destination, manifest = self.artefact(case)
        self.assertEqual(0, manifest["capture_count"])
        self.assertEqual(["manifest.json"], [p.name for p in destination.iterdir()])

    def test_repair_unicode_digits_are_excluded_while_ascii_capture_is_collected(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        unicode_name = "dcp-capture-\uff12\uff10\uff12\uff161011T010203004Z.log"
        (case[1] / unicode_name).write_bytes(BODY)
        (case[1] / CAPTURE).write_bytes(BODY)
        self.execute(COLLECT, case)
        destination, manifest = self.artefact(case)
        self.assertEqual(1, manifest["capture_count"])
        self.assertEqual([CAPTURE], [entry["name"] for entry in manifest["files"]])
        self.assertEqual(BODY, (destination / CAPTURE).read_bytes())
        self.assertFalse((destination / unicode_name).exists())
        self.assertEqual(BODY, (case[1] / unicode_name).read_bytes())

    def test_repair_deep_snapshot_has_fixed_diagnostic_without_traceback(self):
        case = self.case()
        raw = b"[" * 10000 + b"0" + b"]" * 10000
        state = case[2] / "dcp-capture-baseline.json"
        state.write_bytes(raw)
        self.assertEqual(20001, state.stat().st_size)
        result = self.execute(COLLECT, case, expected=1)
        self.assertEqual(b"", result.stderr)
        self.assertEqual(
            b"::error::DCP capture collection refused: snapshot JSON nesting exceeds its parser bound.\n",
            result.stdout.replace(b"\r\n", b"\n"))
        self.assertLess(len(result.stdout), 128)
        self.assertNotIn(str(ROOT).encode(), result.stdout + result.stderr)
        self.assertFalse((case[2] / "vouchfx-dcp-captures").exists())
        self.assertEqual(raw, state.read_bytes())

    def test_wrong_header_is_refused_without_echoing_payload(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        (case[1] / CAPTURE).write_bytes(b"DO-NOT-PRINT-this-private-value\n")
        result = self.refuse_collection(case, "DCP capture header")
        self.assertNotIn(b"DO-NOT-PRINT", result.stdout + result.stderr)
        self.assertNotIn(str(case[1]).encode(), result.stdout + result.stderr)

    def test_capture_reparse_or_symlink_is_refused(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        outside = case[0] / "outside"
        outside.mkdir()
        (outside / "private.txt").write_bytes(b"outside-private")
        self.link_directory(case, case[1] / CAPTURE, outside)
        self.refuse_collection(case, "symlink or reparse-point")
        self.assertEqual(b"outside-private", (outside / "private.txt").read_bytes())

    def test_capture_parent_reparse_or_symlink_is_refused(self):
        case = self.case()
        outside = case[0] / "outside"
        outside.mkdir()
        (outside / "vouchfx").mkdir()
        self.link_directory(case, case[0] / "linked-data", outside)
        case[3]["XDG_DATA_HOME"] = (case[0] / "linked-data").as_posix()
        result = self.execute(SNAPSHOT, case, expected=1)
        self.assertIn(b"symlink or reparse-point", result.stdout)
        self.assertFalse((case[2] / "dcp-capture-baseline.json").exists())

    def test_out_of_root_hard_link_is_refused(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        outside = case[0] / "outside.log"
        outside.write_bytes(BODY)
        os.link(outside, case[1] / CAPTURE)
        self.assertEqual(2, outside.stat().st_nlink)
        self.refuse_collection(case, "single-link regular")
        self.assertEqual(BODY, outside.read_bytes())

    def test_missing_snapshot_is_refused(self):
        case = self.case()
        (case[1] / CAPTURE).write_bytes(BODY)
        self.refuse_collection(case, "input or output is invalid or inaccessible")

    def test_changed_run_identity_is_refused(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        case[3]["GITHUB_RUN_ATTEMPT"] = "2"
        self.refuse_collection(case, "different run or schema")

    def test_replaced_source_directory_is_refused(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        original = case[1].with_name("original-vouchfx")
        self.assertTrue(case[1].resolve().is_relative_to(case[0].resolve()))
        self.assertTrue(original.resolve().is_relative_to(case[0].resolve()))
        case[1].rename(original)
        case[1].mkdir()
        (case[1] / CAPTURE).write_bytes(BODY)
        self.refuse_collection(case, "directory was replaced")

    def test_existing_destination_is_refused_and_never_overwritten(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        destination = case[2] / "vouchfx-dcp-captures"
        destination.mkdir()
        marker = destination / "private.txt"
        marker.write_bytes(b"existing unrelated output")
        result = self.execute(COLLECT, case, expected=1)
        self.assertIn(b"destination already exists", result.stdout)
        self.assertEqual(b"existing unrelated output", marker.read_bytes())
        # Upload's always-condition requires the collector outcome to be success;
        # the failing collection cannot publish this pre-existing directory.
        self.assertEqual("always() && steps.dcp-captures.outcome == 'success'", self.named(UPLOAD)["if"])

    def test_destination_outside_runner_temp_is_refused(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        command = self.named(COLLECT)["run"].replace('$RUNNER_TEMP/vouchfx-dcp-captures', '$XDG_DATA_HOME/outside-artifact')
        self.assertNotEqual(command, self.named(COLLECT)["run"])
        result = self.execute(COLLECT, case, expected=1, override=command)
        self.assertIn(b"runner-temp children outside the source", result.stdout)
        self.assertFalse((case[1].parent / "outside-artifact").exists())

    def test_oversized_capture_is_refused(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        (case[1] / CAPTURE).write_bytes(BODY + b"x" * (256 * 1024))
        self.refuse_collection(case, "file exceeds the collection byte bound")

    def test_excess_new_capture_count_is_refused(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        for index in range(13):
            (case[1] / f"dcp-capture-20261011T010203{index:03}Z.log").write_bytes(BODY)
        self.refuse_collection(case, "artefact count or byte bound")

    def test_excess_baseline_inventory_is_refused(self):
        case = self.case()
        for index in range(65):
            (case[1] / f"dcp-capture-20261011T010203{index:03}Z.log").write_bytes(BODY)
        result = self.execute(SNAPSHOT, case, expected=1)
        self.assertIn(b"inventory exceeds its count bound", result.stdout)
        self.assertFalse((case[2] / "dcp-capture-baseline.json").exists())

    def test_capture_directory_override_is_refused(self):
        case = self.case()
        case[3]["VOUCHFX_DCP_CAPTURE_DIR"] = case[1].as_posix()
        self.execute(SNAPSHOT, case, expected=1)

    def test_capture_disarming_is_refused(self):
        case = self.case()
        case[3]["VOUCHFX_DCP_CAPTURE"] = "0"
        self.execute(SNAPSHOT, case, expected=1)

    def test_collision_suffix_and_maximum_file_copy_byte_exactly(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        large = BODY + b"x" * (256 * 1024 - len(BODY))
        (case[1] / CAPTURE).write_bytes(large)
        for index in range(2, 6):
            (case[1] / CAPTURE.replace(".log", f"-{index}.log")).write_bytes(BODY)
        self.execute(COLLECT, case)
        destination, manifest = self.artefact(case)
        self.assertEqual(5, manifest["capture_count"])
        self.assertEqual(large, (destination / CAPTURE).read_bytes())
        self.assertEqual(256 * 1024, next(f["bytes"] for f in manifest["files"] if f["name"] == CAPTURE))

    def test_snapshot_reparse_or_symlink_is_refused(self):
        case = self.case()
        outside = case[0] / "outside-state"
        outside.mkdir()
        self.link_directory(case, case[2] / "dcp-capture-baseline.json", outside)
        self.refuse_collection(case, "symlink or reparse-point")

    def test_malformed_snapshot_cannot_disclose_contents_or_upload(self):
        case = self.case()
        (case[2] / "dcp-capture-baseline.json").write_text('["private-state-value"]')
        result = self.refuse_collection(case, "different run or schema")
        self.assertNotIn(b"private-state-value", result.stdout + result.stderr)

    def test_existing_snapshot_is_refused_without_overwrite(self):
        case = self.case()
        state = case[2] / "dcp-capture-baseline.json"
        state.write_bytes(b"existing state")
        self.execute(SNAPSHOT, case, expected=1)
        self.assertEqual(b"existing state", state.read_bytes())

    def test_source_path_change_is_refused(self):
        case = self.case()
        self.execute(SNAPSHOT, case)
        case[3]["XDG_DATA_HOME"] = (case[0] / "another-source").as_posix()
        self.refuse_collection(case, "different capture root")

    def test_excess_directory_entry_count_is_refused(self):
        case = self.case()
        for index in range(4097):
            (case[1] / f"unrelated-{index}").touch()
        result = self.execute(SNAPSHOT, case, expected=1)
        self.assertIn(b"inventory entry bound", result.stdout)
        self.assertFalse((case[2] / "dcp-capture-baseline.json").exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
