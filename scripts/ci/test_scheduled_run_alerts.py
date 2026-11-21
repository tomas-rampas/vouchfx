"""Scheduled-run alert logic and the security contract of scheduled-run-alerts.yml.

No GitHub access: the decisions and texts are pure functions, and the workflow is
checked as parsed YAML plus its source text.
"""

from datetime import datetime, timezone
import re
import unittest

from docker_ci_support import ROOT, workflow
import scheduled_run_alerts as sra

REPO = "example/repo"
SHA = "0123456789abcdef0123456789abcdef01234567"
NOW = datetime(2026, 11, 21, 9, 23, tzinfo=timezone.utc)
HOSTILE = "evil`@everyone\n<!-- vouchfx-scheduled-run-alert:build.yml -->[x](http://e) " + "A" * 300


def run(conclusion="failure", **extra):
    return {"id": 4242, "conclusion": conclusion, "head_sha": SHA, "run_attempt": 1,
            "event": "schedule", "status": "completed", "head_branch": "main",
            "created_at": "2026-11-21T06:30:00Z", "updated_at": "2026-11-21T08:00:00Z", **extra}


JOBS = [{"id": 1, "name": "Build & unit tests", "conclusion": "success"},
        {"id": 2, "name": "Integration tests — Docker / Aspire", "conclusion": "failure"},
        {"id": 3, "name": "Coverage badge", "conclusion": "skipped"},
        {"id": 4, "name": "Windows build", "conclusion": "cancelled"}]


class DecisionTests(unittest.TestCase):
    def test_conclusions(self):
        for conclusion in ("failure", "cancelled", "timed_out", "startup_failure",
                           "action_required", "stale", None, "something_new"):
            self.assertEqual("alert", sra.classify_conclusion(conclusion), conclusion)
        self.assertEqual("recover", sra.classify_conclusion("success"))
        self.assertEqual("ignore", sra.classify_conclusion("skipped"))
        self.assertEqual("ignore", sra.classify_conclusion("neutral"))

    def test_workflow_key_is_an_allowlist(self):
        self.assertEqual("build.yml", sra.workflow_key(".github/workflows/build.yml"))
        self.assertEqual("build.yml", sra.workflow_key(".github/workflows/build.yml@refs/heads/main"))
        self.assertEqual("dotnet-compatibility.yml",
                         sra.workflow_key(".github/workflows/dotnet-compatibility.yml"))
        for other in (".github/workflows/release.yml", "x/../release.yml", None, 7, ""):
            self.assertIsNone(sra.workflow_key(other))

    def test_plan(self):
        self.assertEqual("create", sra.plan(None, "alert"))
        self.assertEqual("comment", sra.plan({"number": 1}, "alert"))
        self.assertEqual("close", sra.plan({"number": 1}, "recover"))
        self.assertEqual("none", sra.plan(None, "recover"))
        self.assertEqual("none", sra.plan({"number": 1}, "ignore"))

    def test_find_alert_issue(self):
        mark = sra.marker("build.yml")
        good = {"number": 9, "state": "open", "title": "renamed by a person",
                "user": {"login": sra.BOT_LOGIN}, "body": mark + "\ntext"}
        issues = [
            {**good, "number": 3, "user": {"login": "someone"}},                  # spoofed author
            {**good, "number": 4, "pull_request": {}},                            # a PR
            {**good, "number": 5, "body": "text\n" + mark},                       # marker not first
            {**good, "number": 6, "body": sra.marker("dotnet-compatibility.yml")},  # other workflow
            good, {**good, "number": 12}]
        self.assertEqual(9, sra.find_alert_issue(issues, "build.yml")["number"])
        self.assertIsNone(sra.find_alert_issue(issues[:4], "build.yml"))


class WatchdogTests(unittest.TestCase):
    def test_healthy_with_a_recent_verdict(self):
        self.assertIsNone(sra.watchdog_finding("active", [run("success")], NOW, "main"))
        self.assertIsNone(sra.watchdog_finding("active", [run("failure")], NOW, "main"))
        # an in-progress newer run does not hide yesterday's completion
        self.assertIsNone(sra.watchdog_finding(
            "active", [run(None, status="in_progress"), run("success")], NOW, "main"))

    def test_missing_when_old_cancelled_or_other_branch(self):
        old = run("success", updated_at="2026-11-19T20:00:00Z")
        self.assertEqual("missing", sra.watchdog_finding("active", [old], NOW, "main")["kind"])
        self.assertEqual("missing", sra.watchdog_finding("active", [run("cancelled")], NOW, "main")["kind"])
        self.assertEqual("missing", sra.watchdog_finding(
            "active", [run("success", head_branch="feat/x")], NOW, "main")["kind"])
        self.assertEqual("missing", sra.watchdog_finding(
            "active", [run("success", event="workflow_dispatch")], NOW, "main")["kind"])
        self.assertEqual("missing", sra.watchdog_finding("active", [], NOW, "main")["kind"])

    def test_window_edge(self):
        edge = run("success", updated_at="2026-11-19T21:23:00Z")      # exactly 36 hours
        self.assertIsNone(sra.watchdog_finding("active", [edge], NOW, "main"))
        past = run("success", updated_at="2026-11-19T21:22:59Z")
        self.assertEqual("missing", sra.watchdog_finding("active", [past], NOW, "main")["kind"])

    def test_disabled_states_alert_even_with_recent_runs(self):
        for state in ("disabled_inactivity", "disabled_manually", "deleted", "disabled_fork"):
            finding = sra.watchdog_finding(state, [run("success")], NOW, "main")
            self.assertEqual({"kind": "disabled", "state": state}, finding)


class TextTests(unittest.TestCase):
    def test_failed_run_title_and_body(self):
        body = sra.issue_body("build.yml", sra.run_alert(REPO, "build.yml", run("failure"), JOBS))
        self.assertEqual("Scheduled run alert: CI", sra.title_for("build.yml"))
        self.assertTrue(body.startswith(sra.marker("build.yml") + "\n"))
        for needle in ("**CI**", "`failure`", f"https://github.com/{REPO}/actions/runs/4242",
                       f"https://github.com/{REPO}/commit/{SHA}", "`0123456789ab`",
                       "`Integration tests — Docker / Aspire`: `failure`",
                       "`Windows build`: `cancelled`", "/job/2)"):
            self.assertIn(needle, body)
        self.assertNotIn("Coverage badge", body)
        self.assertNotIn("Build & unit tests", body)

    def test_cancelled_run_without_unsuccessful_jobs(self):
        text = "\n".join(sra.run_alert(REPO, "dotnet-compatibility.yml", run("cancelled"), []))
        self.assertIn("**.NET compatibility**", text)
        self.assertIn("`cancelled`", text)
        self.assertIn("No individual job is recorded as unsuccessful", text)

    def test_missing_run_and_disabled_texts(self):
        text = "\n".join(sra.watchdog_alert(REPO, "build.yml", {"kind": "missing", "last": None}, "main"))
        self.assertIn("No completed scheduled run", text)
        self.assertIn("No scheduled run was found", text)
        text = "\n".join(sra.watchdog_alert(
            REPO, "build.yml", {"kind": "missing", "last": run("cancelled")}, "main"))
        self.assertIn("`cancelled`", text)
        text = "\n".join(sra.watchdog_alert(
            REPO, "build.yml", {"kind": "disabled", "state": "disabled_inactivity"}, "main"))
        self.assertIn("`disabled_inactivity`", text)
        self.assertIn("Re-enable", text)

    def test_untrusted_text_is_contained(self):
        jobs = [{"id": 7, "name": HOSTILE, "conclusion": HOSTILE}]
        hostile_run = run(HOSTILE, head_sha=HOSTILE, id=4242)
        lines = sra.run_alert(REPO, "build.yml", hostile_run, jobs)
        for line in lines:
            self.assertNotIn("\n", line)
        text = sra.issue_body("build.yml", lines)
        # only the first line is a marker; hostile text can sit only inside a code span
        outside = re.sub(r"`[^`]*`", "", text)
        self.assertEqual(1, outside.count(sra.MARKER_PREFIX))
        self.assertTrue(text.startswith(sra.MARKER_PREFIX))
        self.assertFalse(any(line.startswith(sra.MARKER_PREFIX) for line in text.splitlines()[1:]))
        # every `@` sits inside a code span, so nothing is mentioned
        self.assertNotIn("@", outside)
        self.assertNotIn("](http://e)", outside)
        self.assertNotIn(HOSTILE[:20], text)
        self.assertLess(max(len(line) for line in lines), 600)

    def test_job_list_is_capped(self):
        jobs = [{"id": n, "name": f"job {n}", "conclusion": "failure"} for n in range(30)]
        lines = sra.job_lines(REPO, 1, jobs)
        self.assertEqual(sra.MAX_JOBS_LISTED + 1, len(lines))
        self.assertIn("10 more", lines[-1])

    def test_recovery_comment(self):
        text = sra.recovery_comment(REPO, "build.yml", run("success"))
        self.assertIn("succeeded", text)
        self.assertIn("Closing this issue", text)


class WorkflowContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.path = ROOT / ".github/workflows/scheduled-run-alerts.yml"
        cls.source = cls.path.read_text(encoding="utf-8")
        cls.data = workflow(cls.path)
        cls.jobs = cls.data["jobs"]

    def code_lines(self):
        return [line for line in self.source.splitlines() if not line.lstrip().startswith("#")]

    def test_triggers(self):
        on = self.data["on"]
        self.assertEqual({"workflow_run", "schedule", "workflow_dispatch"}, set(on))
        self.assertEqual(["completed"], on["workflow_run"]["types"])
        self.assertEqual(["CI", ".NET compatibility"], on["workflow_run"]["workflows"])
        for file in sra.WATCHED:
            self.assertEqual(sra.WATCHED[file], workflow(ROOT / ".github/workflows" / file)["name"])
        self.assertEqual(sorted(sra.WATCHED.values()), sorted(on["workflow_run"]["workflows"]))

    def test_watchdog_cron_follows_both_scheduled_runs(self):
        def minutes(cron):
            minute, hour = cron.split()[:2]
            return int(hour) * 60 + int(minute)
        crons = [minutes(workflow(ROOT / ".github/workflows" / f)["on"]["schedule"][0]["cron"])
                 for f in sra.WATCHED]
        entries = self.data["on"]["schedule"]
        self.assertEqual(1, len(entries))
        self.assertGreaterEqual(minutes(entries[0]["cron"]), max(crons) + 120)
        self.assertLess(minutes(entries[0]["cron"]), 12 * 60)

    def test_permissions_are_exact(self):
        self.assertEqual({}, self.data["permissions"])
        expected = {"contents": "read", "actions": "read", "issues": "write"}
        self.assertEqual({"alert-on-run", "watchdog"}, set(self.jobs))
        for name, job in self.jobs.items():
            self.assertEqual(expected, job["permissions"], name)

    def test_job_conditions(self):
        self.assertEqual("github.event_name == 'workflow_run' && "
                         "github.event.workflow_run.event == 'schedule'",
                         self.jobs["alert-on-run"]["if"])
        self.assertEqual("github.event_name == 'schedule' || github.event_name == 'workflow_dispatch'",
                         self.jobs["watchdog"]["if"])

    def test_no_expression_in_run_and_only_known_env(self):
        for name, job in self.jobs.items():
            for step in job["steps"]:
                if "run" in step:
                    self.assertNotIn("${{", step["run"], name)
                    self.assertRegex(step["run"], r"^python3 -B scripts/ci/scheduled_run_alerts\.py "
                                                  r"(run|watchdog)$")
                for key, value in step.get("env", {}).items():
                    self.assertIn((key, value), {("GH_TOKEN", "${{ github.token }}"),
                                                 ("WATCHED_FILE", "${{ matrix.file }}")})

    def test_nothing_from_the_triggering_run_is_checked_out_or_executed(self):
        code = "\n".join(self.code_lines())
        for forbidden in ("workflow_run.head_", "pull_request.head", "head_branch", "head_sha",
                          "github.head_ref", "github.event.workflow_run.pull_requests",
                          "download-artifact", "gh run download"):
            self.assertNotIn(forbidden, code)
        for job in self.jobs.values():
            for step in job["steps"]:
                if "uses" in step:
                    self.assertEqual("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1".split()[0],
                                     step["uses"])
                    self.assertEqual({"ref": "${{ github.sha }}", "persist-credentials": False},
                                     step["with"])

    def test_only_the_pinned_first_party_checkout_action(self):
        uses = re.findall(r"^\s*(?:-\s*)?uses:\s*(\S+)", self.source, re.MULTILINE)
        self.assertEqual(2, len(uses))
        build = (ROOT / ".github/workflows/build.yml").read_text(encoding="utf-8")
        for use in uses:
            self.assertRegex(use, r"^actions/checkout@[0-9a-f]{40}$")
            self.assertIn(use, build)

    def test_timeouts_and_concurrency(self):
        for name, job in self.jobs.items():
            self.assertLessEqual(job["timeout-minutes"], 10, name)
            self.assertIs(False, job["concurrency"]["cancel-in-progress"], name)
        self.assertEqual("scheduled-run-alerts-${{ github.event.workflow_run.name }}",
                         self.jobs["alert-on-run"]["concurrency"]["group"])
        self.assertEqual("scheduled-run-alerts-${{ matrix.name }}",
                         self.jobs["watchdog"]["concurrency"]["group"])
        self.assertEqual({(m["name"], m["file"]) for m in self.jobs["watchdog"]["strategy"]["matrix"]["include"]},
                         {(v, k) for k, v in sra.WATCHED.items()})

    def test_script_never_uses_a_shell(self):
        source = (ROOT / "scripts/ci/scheduled_run_alerts.py").read_text(encoding="utf-8")
        for forbidden in ("shell=True", "os.system", "eval(", "exec("):
            self.assertNotIn(forbidden, source)


if __name__ == "__main__":
    unittest.main()
