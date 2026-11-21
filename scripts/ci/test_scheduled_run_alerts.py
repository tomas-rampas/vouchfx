"""Scheduled-run alert logic and the security contract of scheduled-run-alerts.yml.

No GitHub access: the decisions and texts are pure functions, the I/O layer runs against
a fake API (or a patched `subprocess.run`), and the workflow is checked as parsed YAML
plus its source text.
"""

from datetime import datetime, timedelta, timezone
import json
import os
import re
import subprocess
import tempfile
import unittest
from unittest import mock

from docker_ci_support import ROOT, workflow
import scheduled_run_alerts as sra

REPO = "example/repo"
OWNER = "example"
SHA = "0123456789abcdef0123456789abcdef01234567"
NOW = datetime(2026, 11, 21, 9, 23, tzinfo=timezone.utc)
HOSTILE = "evil`@everyone\n<!-- vouchfx-scheduled-run-alert:build.yml -->[x](http://e) " + "A" * 300
BOT = {"login": sra.BOT_LOGIN, "type": "Bot"}


def run(conclusion="failure", **extra):
    return {"id": 4242, "conclusion": conclusion, "head_sha": SHA, "run_attempt": 1,
            "event": "schedule", "status": "completed", "head_branch": "main",
            "created_at": "2026-11-21T06:30:00Z", "updated_at": "2026-11-21T08:00:00Z", **extra}


JOBS = [{"id": 1, "name": "Build & unit tests", "conclusion": "success"},
        {"id": 2, "name": "Integration tests — Docker / Aspire", "conclusion": "failure"},
        {"id": 3, "name": "Coverage badge", "conclusion": "skipped"},
        {"id": 4, "name": "Windows build", "conclusion": "cancelled"}]


def issue(number, key="build.yml", state="open", closed_at=None, user=BOT, **extra):
    return {"number": number, "state": state, "closed_at": closed_at, "title": "Scheduled run alert",
            "user": dict(user), "body": sra.marker(key) + "\ntext", **extra}


def days_ago(days):
    return sra.iso_time(NOW - timedelta(days=days))


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
        self.assertEqual("reopen", sra.plan(None, "alert", {"number": 2}))
        self.assertEqual("comment", sra.plan({"number": 1}, "alert"))
        self.assertEqual("comment", sra.plan({"number": 1}, "alert", {"number": 2}))
        self.assertEqual("create", sra.plan(None, "reconcile"))
        self.assertEqual("reopen", sra.plan(None, "reconcile", {"number": 2}))
        self.assertEqual("none", sra.plan({"number": 1}, "reconcile"))
        self.assertEqual("close", sra.plan({"number": 1}, "recover"))
        self.assertEqual("none", sra.plan(None, "recover"))
        self.assertEqual("none", sra.plan({"number": 1}, "ignore"))

    def test_find_alert_issue(self):
        mark = sra.marker("build.yml")
        good = issue(9, title="renamed by a person")
        issues = [
            {**good, "number": 3, "user": {"login": "someone", "type": "User"}},   # spoofed author
            {**good, "number": 7, "user": {"login": sra.BOT_LOGIN, "type": "User"}},  # login without Bot type
            {**good, "number": 4, "pull_request": {}},                            # a PR
            {**good, "number": 5, "body": "text\n" + mark},                       # marker not first
            {**good, "number": 6, "body": sra.marker("dotnet-compatibility.yml")},  # other workflow
            {**good, "number": 8, "state": "closed"},                             # not open
            good, {**good, "number": 12}]
        self.assertEqual(9, sra.find_alert_issue(issues, "build.yml")["number"])
        self.assertIsNone(sra.find_alert_issue(issues[:6], "build.yml"))

    def test_find_recently_closed_issue(self):
        issues = [issue(1, state="closed", closed_at=days_ago(40)),               # too old
                  issue(2, state="closed", closed_at=days_ago(10)),
                  issue(3, state="closed", closed_at=days_ago(2)),                # newest
                  issue(4, state="closed", closed_at=days_ago(1), key="dotnet-compatibility.yml"),
                  issue(5, state="closed", closed_at=days_ago(1),
                        user={"login": "someone", "type": "User"}),
                  issue(6, state="closed", closed_at=None),
                  issue(7, state="open")]
        self.assertEqual(3, sra.find_recently_closed_issue(issues, "build.yml", NOW)["number"])
        self.assertIsNone(sra.find_recently_closed_issue(issues[:1], "build.yml", NOW))
        edge = issue(8, state="closed", closed_at=days_ago(30))
        self.assertEqual(8, sra.find_recently_closed_issue([edge], "build.yml", NOW)["number"])

    def test_is_newest_scheduled(self):
        self.assertTrue(sra.is_newest_scheduled(run("success"), []))
        self.assertTrue(sra.is_newest_scheduled(run("success"), [run("failure")]))           # same id
        later = run("failure", id=9, created_at="2026-11-21T06:31:00Z")
        self.assertFalse(sra.is_newest_scheduled(run("success"), [later]))
        earlier = run("failure", id=3, created_at="2026-11-20T06:31:00Z")
        self.assertTrue(sra.is_newest_scheduled(run("success"), [earlier]))
        self.assertFalse(sra.is_newest_scheduled({"id": 1}, [later]))                        # no created_at

    def test_owner_and_name_patterns_use_a_full_match(self):
        self.assertEqual("example", sra.valid_owner("example"))
        self.assertEqual("a-b-1", sra.valid_owner("a-b-1"))
        for bad in ("example\n", "-lead", "trail-", "a--b", "a b", "a" * 40, "", None, 7, "a[bot]",
                    "a\"],\"x\":[\""):
            self.assertIsNone(sra.valid_owner(bad), repr(bad))
        with self.assertRaises(SystemExit):
            sra.Gh("example/repo\n")
        self.assertIsNone(sra.Gh(REPO, owner="bad owner").owner)
        self.assertTrue(sra.commit_line(REPO, SHA).startswith("[`0123456789ab`]"))
        self.assertNotIn("](https", sra.commit_line(REPO, SHA + "\n"))


class WatchdogTests(unittest.TestCase):
    def test_healthy_with_a_run_created_in_the_window_whatever_its_status(self):
        self.assertIsNone(sra.watchdog_finding("active", [run("success")], NOW, "main"))
        self.assertIsNone(sra.watchdog_finding("active", [run("failure")], NOW, "main"))
        self.assertIsNone(sra.watchdog_finding("active", [run("cancelled")], NOW, "main"))
        self.assertIsNone(sra.watchdog_finding(
            "active", [run(None, status="in_progress", updated_at="2026-11-21T06:31:00Z")], NOW, "main"))
        self.assertIsNone(sra.watchdog_finding("active", [run(None, status="queued")], NOW, "main"))

    def test_a_single_dropped_day_is_reported(self):
        # Yesterday's run succeeded and finished 25.5 hours before the watchdog; today's
        # run never started.
        yesterday = run("success", id=1, created_at="2026-11-20T06:30:00Z",
                        updated_at="2026-11-20T07:50:00Z")
        finding = sra.watchdog_finding("active", [yesterday], NOW, "main")
        self.assertEqual("missing", finding["kind"])
        self.assertEqual(1, finding["last"]["id"])

    def test_old_runs_other_branches_and_other_events_do_not_count(self):
        old = run("success", created_at="2026-11-19T20:00:00Z")
        self.assertEqual("missing", sra.watchdog_finding("active", [old], NOW, "main")["kind"])
        self.assertEqual("missing", sra.watchdog_finding(
            "active", [run("success", head_branch="feat/x")], NOW, "main")["kind"])
        self.assertEqual("missing", sra.watchdog_finding(
            "active", [run("success", event="workflow_dispatch")], NOW, "main")["kind"])
        self.assertEqual("missing", sra.watchdog_finding("active", [], NOW, "main")["kind"])

    def test_a_rerun_of_an_old_run_does_not_count(self):
        # A re-run bumps updated_at but not created_at.
        rerun = run("success", created_at="2026-11-19T06:30:00Z", updated_at="2026-11-21T09:10:00Z")
        self.assertEqual("missing", sra.watchdog_finding("active", [rerun], NOW, "main")["kind"])

    def test_window_edge_is_on_creation_time(self):
        edge = run("success", created_at="2026-11-20T21:23:00Z")      # exactly 12 hours
        self.assertIsNone(sra.watchdog_finding("active", [edge], NOW, "main"))
        past = run("success", created_at="2026-11-20T21:22:59Z")
        self.assertEqual("missing", sra.watchdog_finding("active", [past], NOW, "main")["kind"])

    def test_disabled_states_alert_even_with_recent_runs(self):
        for state in ("disabled_inactivity", "disabled_manually", "deleted", "disabled_fork"):
            finding = sra.watchdog_finding(state, [run("success")], NOW, "main")
            self.assertEqual({"kind": "disabled", "state": state}, finding)

    def test_newest_completed(self):
        runs = [run(None, id=3, status="in_progress", created_at="2026-11-21T06:30:00Z"),
                run("failure", id=2, created_at="2026-11-20T06:30:00Z"),
                run("success", id=1, created_at="2026-11-19T06:30:00Z"),
                run("failure", id=9, head_branch="feat/x", created_at="2026-11-21T01:00:00Z")]
        self.assertEqual(2, sra.newest_completed(runs, "main")["id"])
        self.assertIsNone(sra.newest_completed([], "main"))


class TextTests(unittest.TestCase):
    def test_failed_run_title_and_body(self):
        body = sra.issue_body("build.yml", sra.run_alert(REPO, "build.yml", run("failure"), JOBS))
        self.assertEqual("Scheduled run alert: CI", sra.title_for("build.yml"))
        self.assertTrue(body.startswith(sra.marker("build.yml") + "\n"))
        for needle in ("**CI**", "`failure`", f"https://github.com/{REPO}/actions/runs/4242",
                       f"https://github.com/{REPO}/commit/{SHA}", "`0123456789ab`",
                       "  - `Integration tests — Docker / Aspire`: `failure`",
                       "  - `Windows build`: `cancelled`", "/job/2)"):
            self.assertIn(needle, body)
        self.assertNotIn("Build & unit tests", body)

    def test_skipped_jobs_are_listed_as_not_run(self):
        lines = sra.run_alert(REPO, "build.yml", run("failure"), JOBS)
        at = lines.index("- Jobs that did not run (skipped, so they gave no result for this run):")
        self.assertEqual(["  - `Coverage badge`"], lines[at + 1:])
        # the unsuccessful list is indented under its heading and stays separate
        heading = lines.index("- Jobs that did not succeed:")
        self.assertTrue(all(line.startswith("  - ") for line in lines[heading + 1:at]))
        self.assertNotIn("Coverage badge", "\n".join(lines[:at]))
        self.assertEqual([], sra.skipped_job_lines([{"name": "x", "conclusion": "success"}]))
        many = [{"name": f"s{n}", "conclusion": "skipped"} for n in range(25)]
        self.assertEqual(sra.MAX_JOBS_LISTED + 2, len(sra.skipped_job_lines(many)))
        self.assertIn("5 more", sra.skipped_job_lines(many)[-1])

    def test_cancelled_run_without_unsuccessful_jobs(self):
        text = "\n".join(sra.run_alert(REPO, "dotnet-compatibility.yml", run("cancelled"), []))
        self.assertIn("**.NET compatibility**", text)
        self.assertIn("`cancelled`", text)
        self.assertIn("  - No individual job is recorded as unsuccessful", text)
        self.assertNotIn("did not run", text)

    def test_missing_run_and_disabled_texts(self):
        text = "\n".join(sra.watchdog_alert(REPO, "build.yml", {"kind": "missing", "last": None}, "main"))
        self.assertIn("No scheduled run", text)
        self.assertIn("12 hours", text)
        self.assertIn("No scheduled run was found", text)
        text = "\n".join(sra.watchdog_alert(
            REPO, "build.yml", {"kind": "missing", "last": run("cancelled")}, "main"))
        self.assertIn("`cancelled`", text)
        text = "\n".join(sra.watchdog_alert(
            REPO, "build.yml", {"kind": "disabled", "state": "disabled_inactivity"}, "main"))
        self.assertIn("`disabled_inactivity`", text)
        self.assertIn("Re-enable", text)
        self.assertIn("disabled by hand", text)
        self.assertIn("cannot report that case", text)
        self.assertIn("RELEASING.md", text)

    def test_default_branch_name_is_contained(self):
        text = "\n".join(sra.watchdog_alert(
            REPO, "build.yml", {"kind": "missing", "last": None}, HOSTILE))
        outside = re.sub(r"`[^`]*`", "", text)
        self.assertNotIn("@", outside)
        self.assertNotIn("](http://e)", outside)
        self.assertNotIn(sra.MARKER_PREFIX, outside)

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
        text = sra.recovery_comment(REPO, "build.yml", run("success", run_attempt=3))
        self.assertIn("succeeded", text)
        self.assertIn("attempt 3", text)
        self.assertIn("Closing this issue", text)
        self.assertIn("#7", sra.duplicate_comment(7))


class FakeGh(sra.Gh):
    """The real `Gh` logic over an in-memory API: only `_call` is replaced."""

    def __init__(self, issues=(), runs=(), jobs=JOBS, state="active", owner=None, dry_run=False,
                 reject_status=None, race=None):
        super().__init__(REPO, dry_run=dry_run, owner=owner)
        self.issues = [dict(i) for i in issues]
        self.runs, self.jobs, self.state = list(runs), jobs, state
        self.reject_status, self.race = reject_status, race
        self.calls, self.comments = [], {}

    def writes(self):
        return [c for c in self.calls if c[0] != "GET"]

    def _call(self, method, path, params=None, payload=None):
        params = dict(params or {})
        self.calls.append((method, path, params, payload))
        return self._get(path, params) if method == "GET" else self._write(method, path, payload)

    def _get(self, path, params):
        if path == "":
            return {"default_branch": "main"}
        if path == "issues":
            hits = [i for i in self.issues if i["state"] == params["state"]
                    and i["user"]["login"] == params["creator"]]
            hits.sort(key=lambda i: i["number"], reverse=params["direction"] == "desc")
            size, page = int(params["per_page"]), int(params["page"])
            return hits[(page - 1) * size:page * size]
        if re.fullmatch(r"actions/workflows/[\w.-]+", path):
            return {"state": self.state}
        if re.fullmatch(r"actions/workflows/[\w.-]+/runs", path):
            return {"workflow_runs": self.runs[:int(params["per_page"])]}
        if re.fullmatch(r"actions/runs/\d+/jobs", path):
            return {"jobs": self.jobs if int(params["page"]) == 1 else []}
        raise AssertionError(f"unexpected GET {path}")

    def _reject(self, payload):
        if self.reject_status and "assignees" in payload:
            raise sra.GhError("gh api failed (1)", f"gh: Validation Failed (HTTP {self.reject_status})")

    def _write(self, method, path, payload):
        self._reject(payload)
        if (method, path) == ("POST", "issues"):
            if self.race and not any(i["number"] == self.race["number"] for i in self.issues):
                self.issues.append(dict(self.race))
            number = max([100] + [i["number"] for i in self.issues]) + 1
            self.issues.append(issue(number, body=payload["body"], title=payload["title"]))
            return {"number": number}
        match = re.fullmatch(r"issues/(\d+)", path)
        if method == "PATCH" and match:
            target = next(i for i in self.issues if i["number"] == int(match.group(1)))
            target["state"] = payload["state"]
            target["closed_at"] = sra.iso_time(NOW) if payload["state"] == "closed" else None
            return {"number": target["number"]}
        match = re.fullmatch(r"issues/(\d+)/comments", path)
        if method == "POST" and match:
            self.comments.setdefault(int(match.group(1)), []).append(payload["body"])
            return {}
        raise AssertionError(f"unexpected {method} {path}")


def write_event(payload):
    handle = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
    with handle:
        json.dump({"workflow_run": payload}, handle)
    return handle.name


def event_run(conclusion="failure", **extra):
    return {**run(conclusion), "path": ".github/workflows/build.yml", "name": "CI", **extra}


DETAIL = ["detail line"]


class ApplyTests(unittest.TestCase):
    def setUp(self):
        patcher = mock.patch("builtins.print")
        patcher.start()
        self.addCleanup(patcher.stop)

    def apply(self, gh, action="alert", **kwargs):
        return sra.apply(gh, "build.yml", action, DETAIL, now=NOW, **kwargs)

    def test_creates_with_the_owner_assigned_and_no_login_in_the_body(self):
        gh = FakeGh(owner=OWNER)
        self.assertEqual("create", self.apply(gh))
        self.assertEqual(1, len(gh.writes()))
        method, path, params, payload = gh.writes()[0]
        self.assertEqual(("POST", "issues"), (method, path))
        self.assertEqual("Scheduled run alert: CI", payload["title"])
        self.assertEqual([OWNER], payload["assignees"])
        self.assertTrue(payload["body"].startswith(sra.marker("build.yml") + "\n"))
        self.assertNotIn(OWNER, payload["body"])

    def test_no_assignee_without_a_valid_owner(self):
        gh = FakeGh(owner=None)
        self.apply(gh)
        self.assertNotIn("assignees", gh.writes()[0][3])
        gh = FakeGh(owner="not a login")
        self.apply(gh)
        self.assertNotIn("assignees", gh.writes()[0][3])

    def test_a_422_for_the_assignee_is_retried_once_without_it(self):
        gh = FakeGh(owner=OWNER, reject_status="422")
        self.assertEqual("create", self.apply(gh))
        posts = [c for c in gh.writes() if c[:2] == ("POST", "issues")]
        self.assertEqual(2, len(posts))
        self.assertIn("assignees", posts[0][3])
        self.assertNotIn("assignees", posts[1][3])
        self.assertEqual(1, len([i for i in gh.issues if i["number"] > 100]))

    def test_other_assignee_errors_are_not_swallowed(self):
        gh = FakeGh(owner=OWNER, reject_status="500")
        with self.assertRaises(SystemExit):
            self.apply(gh)
        self.assertEqual(1, len(gh.writes()))

    def test_comments_on_the_open_issue(self):
        gh = FakeGh([issue(5), issue(2, state="closed", closed_at=days_ago(1))], owner=OWNER)
        self.assertEqual("comment", self.apply(gh))
        self.assertEqual([("POST", "issues/5/comments")], [c[:2] for c in gh.writes()])
        self.assertEqual(["detail line"], gh.comments[5])

    def test_reopens_the_newest_recently_closed_issue(self):
        gh = FakeGh([issue(2, state="closed", closed_at=days_ago(20)),
                     issue(3, state="closed", closed_at=days_ago(3)),
                     issue(4, state="closed", closed_at=days_ago(40))], owner=OWNER)
        self.assertEqual("reopen", self.apply(gh))
        self.assertEqual([("PATCH", "issues/3"), ("POST", "issues/3/comments")],
                         [c[:2] for c in gh.writes()])
        patch = gh.writes()[0][3]
        self.assertEqual({"state": "open", "state_reason": "reopened", "assignees": [OWNER]}, patch)
        self.assertEqual("open", next(i for i in gh.issues if i["number"] == 3)["state"])
        self.assertEqual(["detail line"], gh.comments[3])

    def test_reopen_retries_without_the_assignee_on_a_422(self):
        gh = FakeGh([issue(3, state="closed", closed_at=days_ago(3))], owner=OWNER, reject_status="422")
        self.assertEqual("reopen", self.apply(gh))
        patches = [c[3] for c in gh.writes() if c[0] == "PATCH"]
        self.assertEqual(2, len(patches))
        self.assertNotIn("assignees", patches[1])

    def test_creates_when_the_closed_issue_is_too_old_or_not_ours(self):
        gh = FakeGh([issue(2, state="closed", closed_at=days_ago(31)),
                     issue(3, key="dotnet-compatibility.yml", state="closed", closed_at=days_ago(1))])
        self.assertEqual("create", self.apply(gh))

    def test_close_sequence(self):
        gh = FakeGh([issue(5)])
        step = sra.apply(gh, "build.yml", "recover", closing_comment="closing text", now=NOW)
        self.assertEqual("close", step)
        self.assertEqual([("POST", "issues/5/comments", {"body": "closing text"}),
                          ("PATCH", "issues/5", {"state": "closed", "state_reason": "completed"})],
                         [(m, p, body) for m, p, _, body in gh.writes()])
        # a closed issue is never reopened by a recovery, and nothing open means no writes
        gh = FakeGh([issue(5, state="closed", closed_at=days_ago(1))])
        self.assertEqual("none", sra.apply(gh, "build.yml", "recover", closing_comment="x", now=NOW))
        self.assertEqual([], gh.writes())

    def test_reconcile_adds_nothing_to_an_open_issue(self):
        gh = FakeGh([issue(5)])
        self.assertEqual("none", self.apply(gh, "reconcile"))
        self.assertEqual([], gh.writes())
        self.assertEqual("create", self.apply(FakeGh(), "reconcile"))

    def test_racing_creation_keeps_the_older_issue_and_closes_the_newer(self):
        gh = FakeGh(race=issue(101))
        self.assertEqual("create", self.apply(gh))
        mine = next(i for i in gh.issues if i["number"] == 102)
        self.assertEqual("closed", mine["state"])
        self.assertEqual("open", next(i for i in gh.issues if i["number"] == 101)["state"])
        self.assertIn("#101", gh.comments[102][0])
        self.assertIn("duplicate", gh.comments[102][0])
        self.assertEqual(["detail line"], gh.comments[101])        # the alert is not lost
        patches = [c for c in gh.writes() if c[0] == "PATCH"]
        self.assertEqual([("PATCH", "issues/102", {"state": "closed", "state_reason": "not_planned"})],
                         [(m, p, b) for m, p, _, b in patches])

    def test_no_duplicate_handling_when_there_is_one_issue(self):
        gh = FakeGh()
        self.apply(gh)
        self.assertEqual([("POST", "issues")], [c[:2] for c in gh.writes()])

    def test_lookup_asks_for_the_bots_issues_oldest_first(self):
        gh = FakeGh()
        self.apply(gh)
        first = next(c for c in gh.calls if c[:2] == ("GET", "issues"))
        self.assertEqual({"state": "open", "creator": sra.BOT_LOGIN, "sort": "created",
                          "direction": "asc", "per_page": 100, "page": 1}, first[2])
        closed = [c for c in gh.calls if c[:2] == ("GET", "issues") and c[2]["state"] == "closed"]
        self.assertEqual(sra.BOT_LOGIN, closed[0][2]["creator"])
        self.assertEqual(sra.iso_time(NOW - sra.REOPEN_WINDOW), closed[0][2]["since"])

    def test_pagination_reaches_the_second_page(self):
        filler = [issue(n, key="other.yml") for n in range(1, 101)]
        gh = FakeGh(filler + [issue(150)])
        self.assertEqual("comment", self.apply(gh))
        pages = [c[2]["page"] for c in gh.calls if c[:2] == ("GET", "issues") and c[2]["state"] == "open"]
        self.assertEqual([1, 2], pages)
        self.assertEqual(["detail line"], gh.comments[150])

    def test_a_full_last_page_is_an_error_not_a_guess(self):
        gh = FakeGh([issue(n, key="other.yml") for n in range(1, sra.MAX_OPEN_PAGES * 100 + 1)])
        with self.assertRaises(SystemExit):
            self.apply(gh)
        self.assertEqual([], gh.writes())

    def test_dry_run_writes_nothing(self):
        gh = FakeGh(dry_run=True, owner=OWNER)
        with mock.patch("builtins.print"):
            self.apply(gh)
        self.assertEqual([], gh.writes())


class CmdRunTests(unittest.TestCase):
    def go(self, gh, payload):
        path = write_event(payload)
        self.addCleanup(os.unlink, path)
        with mock.patch("builtins.print"):
            return sra.cmd_run(gh, path, now=NOW)

    def test_non_schedule_or_unwatched_payloads_make_no_calls(self):
        for payload in (event_run(event="workflow_dispatch"), event_run(event="push"),
                        event_run(event="pull_request"),
                        event_run(path=".github/workflows/release.yml"),
                        event_run(path=None), event_run(conclusion="success", event="push")):
            gh = FakeGh([issue(5)])
            self.assertEqual(0, self.go(gh, payload))
            self.assertEqual([], gh.calls, payload)

    def test_skipped_and_neutral_make_no_calls(self):
        for conclusion in ("skipped", "neutral"):
            gh = FakeGh([issue(5)])
            self.go(gh, event_run(conclusion))
            self.assertEqual([], gh.calls)

    def test_any_other_conclusion_alerts_with_the_jobs(self):
        for conclusion in ("failure", "cancelled", "timed_out", None, "something_new"):
            gh = FakeGh(owner=OWNER)
            self.go(gh, event_run(conclusion))
            self.assertEqual("POST", gh.writes()[0][0], conclusion)
            body = gh.writes()[0][3]["body"]
            self.assertIn("`Integration tests — Docker / Aspire`", body)
            self.assertIn("Coverage badge", body)
            self.assertTrue(any(c[1] == "actions/runs/4242/jobs" for c in gh.calls))

    def test_a_failure_comments_on_the_open_issue(self):
        gh = FakeGh([issue(5)])
        self.go(gh, event_run("failure"))
        self.assertEqual([("POST", "issues/5/comments")], [c[:2] for c in gh.writes()])

    def test_a_success_that_is_the_newest_run_closes_the_issue(self):
        gh = FakeGh([issue(5)], runs=[run("success", id=4242)])
        self.go(gh, event_run("success", run_attempt=2))
        self.assertEqual([("POST", "issues/5/comments"), ("PATCH", "issues/5")],
                         [c[:2] for c in gh.writes()])
        self.assertIn("attempt 2", gh.comments[5][0])
        self.assertEqual("closed", gh.issues[0]["state"])
        newest_call = next(c for c in gh.calls if c[1] == "actions/workflows/build.yml/runs")
        self.assertEqual({"event": "schedule", "branch": "main", "per_page": 1}, newest_call[2])

    def test_a_success_of_an_older_run_posts_nothing(self):
        newer = run("failure", id=9000, created_at="2026-11-21T06:45:00Z")
        gh = FakeGh([issue(5)], runs=[newer])
        self.go(gh, event_run("success", id=77, created_at="2026-11-20T06:30:00Z"))
        self.assertEqual([], gh.writes())
        self.assertEqual("open", gh.issues[0]["state"])

    def test_a_success_with_nothing_open_is_quiet(self):
        gh = FakeGh(runs=[run("success", id=4242)])
        self.go(gh, event_run("success"))
        self.assertEqual([], gh.writes())

    def test_api_errors_fail_the_job(self):
        class Broken(FakeGh):
            def _get(self, path, params):
                raise sra.GhError("gh api GET failed (1): boom", "boom")
        for conclusion in ("failure", "success"):
            with self.assertRaises(SystemExit):
                self.go(Broken(), event_run(conclusion))


class CmdWatchdogTests(unittest.TestCase):
    def go(self, gh, key="build.yml"):
        with mock.patch("builtins.print"):
            return sra.cmd_watchdog(gh, key, NOW)

    def test_healthy_makes_no_writes(self):
        gh = FakeGh(runs=[run("success")])
        self.assertEqual(0, self.go(gh))
        self.assertEqual([], gh.writes())

    def test_a_single_dropped_day_opens_the_issue(self):
        yesterday = run("success", id=1, created_at="2026-11-20T06:30:00Z",
                        updated_at="2026-11-20T07:50:00Z")
        gh = FakeGh(runs=[yesterday], owner=OWNER)
        self.go(gh)
        self.assertEqual("POST", gh.writes()[0][0])
        self.assertIn("No scheduled run", gh.writes()[0][3]["body"])

    def test_a_disabled_workflow_opens_the_issue(self):
        gh = FakeGh(runs=[run("success")], state="disabled_manually")
        self.go(gh)
        self.assertIn("Workflow not active", gh.writes()[0][3]["body"])

    def test_a_failed_run_without_an_issue_is_reconciled(self):
        gh = FakeGh(runs=[run("failure", id=4242)], owner=OWNER)
        self.go(gh)
        self.assertEqual([("POST", "issues")], [c[:2] for c in gh.writes()])
        self.assertIn("Unsuccessful scheduled run", gh.writes()[0][3]["body"])
        self.assertIn("`Integration tests — Docker / Aspire`", gh.writes()[0][3]["body"])

    def test_a_failed_run_with_an_open_issue_adds_nothing(self):
        gh = FakeGh([issue(5)], runs=[run("failure")])
        self.go(gh)
        self.assertEqual([], gh.writes())

    def test_reconcile_reopens_a_recently_closed_issue(self):
        gh = FakeGh([issue(5, state="closed", closed_at=days_ago(1))], runs=[run("cancelled")])
        self.go(gh)
        self.assertEqual([("PATCH", "issues/5"), ("POST", "issues/5/comments")],
                         [c[:2] for c in gh.writes()])

    def test_an_in_progress_run_does_not_hide_yesterdays_failure(self):
        today = run(None, id=2, status="in_progress", created_at="2026-11-21T06:30:00Z")
        yesterday = run("failure", id=1, created_at="2026-11-20T06:30:00Z")
        gh = FakeGh(runs=[today, yesterday])
        self.go(gh)
        self.assertEqual("POST", gh.writes()[0][0])

    def test_a_newer_success_is_not_reconciled(self):
        gh = FakeGh(runs=[run("success", id=2), run("failure", id=1, created_at="2026-11-20T06:30:00Z")])
        self.go(gh)
        self.assertEqual([], gh.writes())

    def test_unwatched_file_is_refused(self):
        with self.assertRaises(SystemExit):
            self.go(FakeGh(), "release.yml")


class FakeProcess:
    def __init__(self, results):
        self.results, self.calls = list(results), []

    def __call__(self, command, **kwargs):
        self.calls.append((command, kwargs))
        returncode, stdout, stderr = self.results.pop(0)
        return subprocess.CompletedProcess(command, returncode, stdout, stderr)


class SubprocessTests(unittest.TestCase):
    def call(self, results, method="GET", payload=None, params=None):
        fake = FakeProcess(results)
        with mock.patch.object(sra.subprocess, "run", fake), mock.patch.object(sra.time, "sleep"):
            try:
                value = sra.Gh(REPO)._call(method, "issues/3", params, payload)
            except SystemExit as error:
                value = error
        return fake, value

    def test_a_write_sends_the_body_on_stdin_only(self):
        secret = "BODY-" + HOSTILE
        fake, value = self.call([(0, '{"number": 3}', "")], "PATCH", {"body": secret})
        command, kwargs = fake.calls[0]
        self.assertEqual({"number": 3}, value)
        self.assertEqual(["gh", "api", "-X", "PATCH", f"repos/{REPO}/issues/3", "--input", "-"], command)
        self.assertEqual({"body": secret}, json.loads(kwargs["input"]))
        self.assertFalse(any("BODY-" in part for part in command))
        self.assertNotIn("-f", command)
        self.assertNotIn("shell", kwargs)

    def test_a_read_passes_parameters_as_fields_and_no_input(self):
        fake, _ = self.call([(0, "[]", "")], params={"state": "open", "page": 2})
        command, kwargs = fake.calls[0]
        self.assertEqual(["gh", "api", "-X", "GET", f"repos/{REPO}/issues/3",
                          "-f", "state=open", "-f", "page=2"], command)
        self.assertIsNone(kwargs["input"])
        self.assertNotIn("--input", command)

    def test_a_failed_write_raises_and_is_not_retried(self):
        fake, value = self.call([(1, "", "boom")], "POST", {"a": 1})
        self.assertIsInstance(value, SystemExit)
        self.assertIn("failed (1): boom", str(value))
        self.assertEqual(1, len(fake.calls))

    def test_a_failed_read_is_retried_once_then_raises(self):
        fake, value = self.call([(1, "", "HTTP 502"), (1, "", "HTTP 502")])
        self.assertIsInstance(value, SystemExit)
        self.assertEqual(2, len(fake.calls))

    def test_a_read_that_succeeds_on_the_retry_is_healthy(self):
        fake, value = self.call([(1, "", "HTTP 502"), (0, '{"ok": true}', "")])
        self.assertEqual({"ok": True}, value)
        self.assertEqual(2, len(fake.calls))

    def test_empty_output_is_none(self):
        self.assertIsNone(self.call([(0, "  \n", "")], "PATCH", {})[1])


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
        self.assertEqual(["main"], on["workflow_run"]["branches"])
        self.assertEqual(["CI", ".NET compatibility"], on["workflow_run"]["workflows"])
        for file in sra.WATCHED:
            self.assertEqual(sra.WATCHED[file], workflow(ROOT / ".github/workflows" / file)["name"])
        self.assertEqual(sorted(sra.WATCHED.values()), sorted(on["workflow_run"]["workflows"]))

    def test_watchdog_cron_follows_both_scheduled_runs_and_the_window_fits(self):
        def minutes(cron):
            minute, hour = cron.split()[:2]
            return int(hour) * 60 + int(minute)
        crons = [minutes(workflow(ROOT / ".github/workflows" / f)["on"]["schedule"][0]["cron"])
                 for f in sra.WATCHED]
        entries = self.data["on"]["schedule"]
        self.assertEqual(1, len(entries))
        watchdog = minutes(entries[0]["cron"])
        self.assertGreaterEqual(watchdog, max(crons) + 120)
        self.assertLess(watchdog, 12 * 60)
        gap = timedelta(minutes=watchdog - max(crons))
        # Yesterday's run is at least 24 hours minus the gap old: it can never satisfy today's check.
        self.assertLess(sra.WATCHDOG_WINDOW, timedelta(hours=24) - gap)
        # Today's runs, created after their cron and delayed by up to two hours, stay inside it.
        self.assertGreater(sra.WATCHDOG_WINDOW, gap + timedelta(hours=2))

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
        self.assertEqual("(github.event_name == 'schedule' || github.event_name == 'workflow_dispatch') && "
                         "github.ref == format('refs/heads/{0}', github.event.repository.default_branch)",
                         self.jobs["watchdog"]["if"])

    def test_runner_and_no_ambient_configuration(self):
        for key in ("env", "defaults", "container", "services"):
            self.assertNotIn(key, self.data, key)
        for name, job in self.jobs.items():
            self.assertEqual("ubuntu-latest", job["runs-on"], name)
            for key in ("env", "defaults", "container", "services", "outputs", "needs"):
                self.assertNotIn(key, job, f"{name}.{key}")
            checkout, script = job["steps"]
            self.assertEqual({"name", "uses", "with"}, set(checkout), name)
            self.assertEqual({"name", "env", "run"}, set(script), name)

    def test_no_expression_in_run_and_only_known_env(self):
        for name, job in self.jobs.items():
            for step in job["steps"]:
                if "run" in step:
                    self.assertNotIn("${{", step["run"], name)
                    self.assertRegex(step["run"], r"^python3 -I -B scripts/ci/scheduled_run_alerts\.py "
                                                  r"(run|watchdog)$")
                for key, value in step.get("env", {}).items():
                    self.assertIn((key, value), {("GH_TOKEN", "${{ github.token }}"),
                                                 ("WATCHED_FILE", "${{ matrix.file }}")})

    def test_nothing_from_the_triggering_run_is_checked_out_or_executed(self):
        code = "\n".join(self.code_lines())
        for forbidden in ("workflow_run.head_", "pull_request.head", "head_branch", "head_sha",
                          "head_commit", "head_repository", "display_title", "actor",
                          "github.head_ref", "github.event.workflow_run.pull_requests",
                          "download-artifact", "gh run download"):
            self.assertNotIn(forbidden, code)
        for job in self.jobs.values():
            for step in job["steps"]:
                if "uses" in step:
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
        self.assertEqual("scheduled-run-alerts-${{ github.event.workflow_run.event }}-"
                         "${{ github.event.workflow_run.name }}",
                         self.jobs["alert-on-run"]["concurrency"]["group"])
        self.assertEqual("scheduled-run-alerts-watchdog-${{ matrix.name }}",
                         self.jobs["watchdog"]["concurrency"]["group"])
        self.assertEqual({(m["name"], m["file"]) for m in self.jobs["watchdog"]["strategy"]["matrix"]["include"]},
                         {(v, k) for k, v in sra.WATCHED.items()})

    def test_script_never_uses_a_shell(self):
        source = (ROOT / "scripts/ci/scheduled_run_alerts.py").read_text(encoding="utf-8")
        for forbidden in ("shell=True", "os.system", "eval(", "exec("):
            self.assertNotIn(forbidden, source)


if __name__ == "__main__":
    unittest.main()
