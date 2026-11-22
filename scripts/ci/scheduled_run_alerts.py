"""Alert issues for the scheduled runs of `CI` and `.NET compatibility`.

Used only by .github/workflows/scheduled-run-alerts.yml. Two modes:

  run       The `workflow_run` job. Reads the event payload from GITHUB_EVENT_PATH.
            A failed, cancelled or otherwise unsuccessful scheduled run opens or
            updates the one alert issue for that workflow; a successful one closes
            it, but only when it is the newest scheduled run (a re-run of an older
            run proves nothing about today).
  watchdog  The daily job. Opens or updates the same issue when the workflow is not
            `active` or has no scheduled run created in the last 12 hours, whatever
            that run's status. It also reconciles: when the newest completed
            scheduled run was unsuccessful, is also the newest scheduled run (none is
            still in progress) and no alert issue is open, it opens one, so a lost
            event or a failed Issues API call in the `run` job is made good the next
            morning; it does nothing when an alert issue for the workflow was closed at
            or after that run's last update. It never closes an issue: only a
            successful scheduled run does. For its own findings (not active, no recent
            run) it adds a comment to an open issue only when the newest finding it
            recorded there was of a different kind.
            Only a scheduled trigger (WATCHDOG_EVENT=schedule) lets the watchdog write.
            Any other trigger, such as a manual dispatch, makes it report only: it reads,
            prints its finding and the write it would have made, and writes nothing,
            because the 12-hour window is meaningful only around the scheduled time.

Every decision and every piece of text lives in a pure function so the tests need no
GitHub. Free text from the event or the API (job names, conclusions, workflow state,
the default branch name) is untrusted: it reaches an issue only inside a code span
after `inline_code`, and bodies travel to `gh api` as JSON on stdin, never as
arguments. Titles and the workflow display names come from the fixed WATCHED table,
not from the payload. The script runs from the default branch and executes nothing
from the triggering run.
"""

import argparse
from datetime import datetime, timedelta, timezone
import json
import os
import posixpath
import re
import subprocess
import sys
import time

# Workflow file name -> display name. The only workflows that are watched; a payload
# naming any other workflow is ignored.
WATCHED = {"build.yml": "CI", "dotnet-compatibility.yml": ".NET compatibility"}
BOT_LOGIN = "github-actions[bot]"
BOT_TYPE = "Bot"
MARKER_PREFIX = "<!-- vouchfx-scheduled-run-alert:"
# A completed run with one of these conclusions raises no alert. Every other
# conclusion does, including values GitHub may add later.
NO_ALERT_CONCLUSIONS = frozenset({"success", "skipped", "neutral"})
JOB_QUIET_CONCLUSIONS = frozenset({"success", "skipped", "neutral"})
# The watchdog is healthy when a scheduled run on the default branch was *created*
# within this window, whatever its status: a run that is in progress, cancelled or
# failed is the `run` job's responsibility (and the watchdog's reconcile step). The
# scheduled crons fire at 06:17 and 06:47 UTC (observed creation 06:30-07:04) and
# the watchdog's cron is 09:23 UTC, so today's runs are 2.5 to 3 hours old when the
# watchdog looks, and yesterday's are about 26 hours old. 12 hours leaves wide margins
# on both sides, and the contract test checks that it stays under 24 hours plus the gap
# between the latest watched cron and the watchdog cron, less any creation delay.
WATCHDOG_WINDOW = timedelta(hours=12)
# A closed alert issue is reopened (not replaced) when the workflow breaks again
# within this period, unless it was closed as `not_planned`.
REOPEN_WINDOW = timedelta(days=30)
# The watchdog's own findings carry a second hidden marker line naming the finding kind.
FINDING_PREFIX = "<!-- vouchfx-scheduled-run-alert-finding:"
FINDING_RE = re.compile(re.escape(FINDING_PREFIX) + r"([a-z]+) -->")
COMMENTS_PER_PAGE = 100
MAX_JOBS_LISTED = 20
MAX_OPEN_PAGES = 10
MAX_CLOSED_PAGES = 3
RETRY_DELAY_SECONDS = 2
REPO_RE = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")
SHA_RE = re.compile(r"[0-9a-f]{40}")
# A GitHub user or organisation login: alphanumerics and single hyphens, at most 39.
OWNER_RE = re.compile(r"[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}")


def marker(key):
    return f"{MARKER_PREFIX}{key} -->"


def finding_marker(kind):
    return f"{FINDING_PREFIX}{kind} -->"


def last_finding_kind(texts):
    """Kind of the newest finding marker in `texts` (newest text first), or None."""
    for text in texts:
        for line in reversed((text or "").splitlines()):
            found = FINDING_RE.fullmatch(line.strip())
            if found:
                return found.group(1)
    return None


def with_finding(lines, kind):
    return list(lines) + ([finding_marker(kind)] if kind else [])


def may_write(event):
    """Only a scheduled watchdog run writes; a manual dispatch is a health report."""
    return event == "schedule"


def inline_code(value, limit=100):
    """Return untrusted text as one Markdown code span: no backticks, no newlines."""
    text = "" if value is None else str(value)
    text = "".join(c if c.isprintable() else " " for c in text)
    text = " ".join(text.replace("`", "'").split())
    if len(text) > limit:
        text = text[:limit - 1] + "…"
    return f"`{text or '(empty)'}`"


def workflow_key(path):
    """`.github/workflows/build.yml@refs/heads/main` -> `build.yml`, or None if not watched."""
    if not isinstance(path, str):
        return None
    name = posixpath.basename(path.split("@", 1)[0])
    return name if name in WATCHED else None


def valid_owner(value):
    """The repository owner login if it has the shape of one, else None."""
    return value if isinstance(value, str) and OWNER_RE.fullmatch(value) else None


def classify_conclusion(conclusion):
    """`alert`, `recover` or `ignore` for a completed scheduled run."""
    if conclusion == "success":
        return "recover"
    if conclusion in NO_ALERT_CONCLUSIONS:
        return "ignore"
    return "alert"


def parse_time(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(timezone.utc)


def iso_time(value):
    return value.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def run_url(repo, run_id):
    return f"https://github.com/{repo}/actions/runs/{int(run_id)}"


def commit_line(repo, sha):
    if isinstance(sha, str) and SHA_RE.fullmatch(sha):
        return f"[`{sha[:12]}`](https://github.com/{repo}/commit/{sha})"
    return inline_code(sha, 40)


def unsuccessful_jobs(jobs):
    return [j for j in jobs if j.get("conclusion") not in JOB_QUIET_CONCLUSIONS]


def skipped_jobs(jobs):
    return [j for j in jobs if j.get("conclusion") == "skipped"]


def job_lines(repo, run_id, jobs):
    bad = unsuccessful_jobs(jobs)
    if not bad:
        return ["  - No individual job is recorded as unsuccessful; open the run for the details."]
    lines = []
    for job in bad[:MAX_JOBS_LISTED]:
        line = f"  - {inline_code(job.get('name'))}: {inline_code(job.get('conclusion') or job.get('status'), 30)}"
        if isinstance(job.get("id"), int):
            line += f" ([log]({run_url(repo, run_id)}/job/{job['id']}))"
        lines.append(line)
    if len(bad) > MAX_JOBS_LISTED:
        lines.append(f"  - … and {len(bad) - MAX_JOBS_LISTED} more.")
    return lines


def skipped_job_lines(jobs):
    """A 'did not run' list: a skipped job (for example one that `needs` a failed job) gave no result."""
    skipped = skipped_jobs(jobs)
    if not skipped:
        return []
    lines = ["- Jobs that did not run (skipped, so they gave no result for this run):"]
    lines += [f"  - {inline_code(job.get('name'))}" for job in skipped[:MAX_JOBS_LISTED]]
    if len(skipped) > MAX_JOBS_LISTED:
        lines.append(f"  - … and {len(skipped) - MAX_JOBS_LISTED} more.")
    return lines


def title_for(key):
    return f"Scheduled run alert: {WATCHED[key]}"


def issue_body(key, detail_lines, kind=None):
    return "\n".join([
        marker(key),
        *([finding_marker(kind)] if kind else []),
        f"The scheduled runs of the **{WATCHED[key]}** workflow need attention. This issue is "
        "opened and updated automatically (`scheduled-run-alerts.yml`); an open issue means "
        "the workflow is currently broken. It closes itself when a later scheduled run "
        "succeeds.", "", *detail_lines])


def run_alert(repo, key, run, jobs):
    """Detail lines for an unsuccessful scheduled run (shared by issue body and comment)."""
    run_id = int(run["id"])
    return [
        f"**Unsuccessful scheduled run.** Workflow: **{WATCHED[key]}**.",
        f"- Conclusion: {inline_code(run.get('conclusion'), 30)}",
        f"- Run: {run_url(repo, run_id)} (attempt {int(run.get('run_attempt') or 1)})",
        f"- Commit: {commit_line(repo, run.get('head_sha'))}",
        "- Jobs that did not succeed:", *job_lines(repo, run_id, jobs),
        *skipped_job_lines(jobs)]


def scheduled_runs(runs, branch):
    return [r for r in runs if r.get("event") == "schedule" and r.get("head_branch") == branch]


def watchdog_finding(state, runs, now, branch):
    """None when healthy, else a dict describing why the watchdog alerts."""
    if state != "active":
        return {"kind": "disabled", "state": state}
    scheduled = scheduled_runs(runs, branch)
    for run in scheduled:
        if now - parse_time(run["created_at"]) <= WATCHDOG_WINDOW:
            return None
    last = max(scheduled, key=lambda r: r["created_at"], default=None)
    return {"kind": "missing", "last": last}


def newest_completed(runs, branch):
    """The most recently created completed scheduled run on the branch, or None."""
    done = [r for r in scheduled_runs(runs, branch) if r.get("status") == "completed"]
    return max(done, key=lambda r: r["created_at"], default=None)


def newest_scheduled(runs, branch):
    """The most recently created scheduled run on the branch, whatever its status, or None."""
    return max(scheduled_runs(runs, branch), key=lambda r: r["created_at"], default=None)


def is_newest_scheduled(run, newest_runs):
    """True when `run` is the newest scheduled run (or at least as new as it)."""
    if not newest_runs:
        return True
    newest = newest_runs[0]
    if run.get("id") == newest.get("id"):
        return True
    try:
        return parse_time(run["created_at"]) >= parse_time(newest["created_at"])
    except (KeyError, TypeError, ValueError):
        return False


def watchdog_alert(repo, key, finding, branch):
    name = WATCHED[key]
    if finding["kind"] == "disabled":
        return [
            f"**Workflow not active.** **{name}** has the state {inline_code(finding['state'], 40)}, "
            "so it runs no scheduled jobs.",
            "- This check catches a workflow that was disabled by hand. Re-enable it from the "
            "Actions tab; nothing re-enables it automatically.",
            "- GitHub also switches scheduled workflows off after 60 days without repository "
            "activity (`disabled_inactivity`), and it does so for the workflow that raised this "
            "alert as well, so this check cannot report that case: the release checklist in "
            "`RELEASING.md` is what covers it.",
            f"- Workflow: https://github.com/{repo}/actions/workflows/{key}"]
    last = finding["last"]
    lines = [
        f"**No scheduled run.** **{name}** has no scheduled run on {inline_code(branch)} that "
        "was created in the last 12 hours (GitHub can delay or drop scheduled runs under load)."]
    if last:
        lines += [f"- Most recent scheduled run: {run_url(repo, last['id'])}, status "
                  f"{inline_code(last.get('status'), 30)}, conclusion "
                  f"{inline_code(last.get('conclusion') or 'none', 30)}, created "
                  f"{inline_code(last.get('created_at'), 30)}"]
    else:
        lines += ["- No scheduled run was found for this workflow on the default branch."]
    lines += [f"- Workflow: https://github.com/{repo}/actions/workflows/{key}"]
    return lines


def recovery_comment(repo, key, run):
    return "\n".join([
        f"A later scheduled run of **{WATCHED[key]}** succeeded: {run_url(repo, run['id'])} "
        f"(attempt {int(run.get('run_attempt') or 1)}, commit {commit_line(repo, run.get('head_sha'))}). "
        "Closing this issue.",
    ])


def duplicate_comment(number):
    return (f"Another open alert issue for this workflow exists (#{int(number)}), created "
            "at the same moment. Closing this one as a duplicate; updates continue there.")


def is_bot(user):
    user = user or {}
    return user.get("login") == BOT_LOGIN and user.get("type") == BOT_TYPE


def is_alert_issue(issue, key):
    """A non-PR issue authored by the Actions bot whose body starts with the marker."""
    return ("pull_request" not in issue and is_bot(issue.get("user"))
            and (issue.get("body") or "").split("\n", 1)[0].strip() == marker(key))


def open_alert_issues(issues, key):
    return sorted((i for i in issues if is_alert_issue(i, key) and i.get("state", "open") == "open"),
                  key=lambda i: int(i["number"]))


def find_alert_issue(issues, key):
    """Oldest open alert issue for the workflow, or None."""
    hits = open_alert_issues(issues, key)
    return hits[0] if hits else None


def find_recently_closed_issue(issues, key, now):
    """Most recently closed alert issue for the workflow, closed within REOPEN_WINDOW.

    An issue closed as `not_planned` is skipped: it is either a duplicate this script
    closed or an alert a maintainer chose to mute, and neither is reopened.
    """
    hits = []
    for issue in issues:
        if not is_alert_issue(issue, key) or issue.get("state") != "closed":
            continue
        if issue.get("state_reason") == "not_planned":
            continue
        try:
            closed = parse_time(issue["closed_at"])
        except (KeyError, TypeError, ValueError, AttributeError):
            continue
        if now - closed <= REOPEN_WINDOW:
            hits.append((closed, int(issue["number"]), issue))
    return max(hits, key=lambda h: h[:2])[2] if hits else None


def closed_since(issues, key, since):
    """True when any alert issue for the workflow (whatever its close reason) was closed at or after `since`."""
    for issue in issues:
        if is_alert_issue(issue, key) and issue.get("state") == "closed":
            try:
                if parse_time(issue["closed_at"]) >= since:
                    return True
            except (KeyError, TypeError, ValueError, AttributeError):
                continue
    return False


def plan(existing, action, closed=None):
    """`create`, `comment`, `reopen`, `close` or `none`.

    `action` is `alert` (comment on the open issue), `reconcile` (make sure an issue is
    open, but add nothing to one that is), or `recover` (close the open issue).
    """
    if action in ("alert", "reconcile"):
        if existing:
            return "comment" if action == "alert" else "none"
        return "reopen" if closed else "create"
    if action == "recover":
        return "close" if existing else "none"
    return "none"


# --- GitHub access -----------------------------------------------------------------

class GhError(SystemExit):
    """A failed `gh api` call. A SystemExit, so an uncaught one fails the job."""

    def __init__(self, message, stderr=""):
        super().__init__(message)
        self.stderr = stderr


def is_validation_error(error):
    """True for a `gh api` failure whose HTTP status is 422 (not any text containing '422')."""
    return "(HTTP 422)" in error.stderr


class Gh:
    def __init__(self, repo, dry_run=False, owner=None):
        if not REPO_RE.fullmatch(repo):
            raise SystemExit(f"Refusing unexpected repository name {repo!r}")
        self.repo, self.dry_run, self.owner = repo, dry_run, valid_owner(owner)

    def _call(self, method, path, params=None, payload=None):
        command = ["gh", "api", "-X", method, f"repos/{self.repo}" + (f"/{path}" if path else "")]
        for k, v in (params or {}).items():
            command += ["-f", f"{k}={v}"]
        data = None
        if payload is not None:
            command += ["--input", "-"]
            data = json.dumps(payload)
        attempts = 2 if method == "GET" else 1      # a read is safe to repeat; a write is not
        for attempt in range(attempts):
            result = subprocess.run(command, input=data, capture_output=True, text=True, timeout=60)
            if not result.returncode:
                break
            if attempt + 1 < attempts:
                time.sleep(RETRY_DELAY_SECONDS)
        else:
            raise GhError(f"gh api {method} {path} failed ({result.returncode}): {result.stderr.strip()}",
                          result.stderr)
        return json.loads(result.stdout) if result.stdout.strip() else None

    def get(self, path, **params):
        return self._call("GET", path, params)

    def write(self, method, path, payload):
        if self.dry_run:
            print(f"DRY RUN {method} {path}\n{json.dumps(payload, indent=2, ensure_ascii=False)}")
            return {"number": 0}
        return self._call(method, path, payload=payload)

    def create_assigned(self, payload):
        """Create an issue with the repository owner as assignee; retry once without it on a 422.

        An organisation cannot be assigned, and the API answers 422 for it. The login is
        never put in a body.
        """
        if self.owner:
            try:
                return self.write("POST", "issues", {**payload, "assignees": [self.owner]})
            except GhError as error:
                if not is_validation_error(error):
                    raise
                print(f"The API rejected the assignee {self.owner!r}; retrying without one.")
        return self.write("POST", "issues", payload)

    def add_owner(self, number):
        """Add the owner to an issue's assignees (best effort: a 422 is logged and ignored).

        Unlike a PATCH with `assignees`, this keeps whoever a maintainer assigned earlier.
        """
        if not self.owner:
            return
        try:
            self.write("POST", f"issues/{int(number)}/assignees", {"assignees": [self.owner]})
        except GhError as error:
            if not is_validation_error(error):
                raise
            print(f"The API rejected the assignee {self.owner!r}; leaving the assignees as they are.")

    def recent_comments(self, number, count):
        """Bot-authored comments on the newest page of an issue, newest first.

        The API lists an issue's comments oldest first, so the last page is derived from
        the issue's comment count.
        """
        if not count:
            return []
        page = (int(count) - 1) // COMMENTS_PER_PAGE + 1
        batch = self.get(f"issues/{int(number)}/comments", per_page=COMMENTS_PER_PAGE, page=page)
        return [c for c in reversed(batch) if is_bot(c.get("user"))]

    def _alert_issue_pages(self, max_pages, **params):
        """Issues created by the Actions bot, page by page. Returns (issues, last_page_was_full)."""
        issues = []
        for page in range(1, max_pages + 1):
            batch = self.get("issues", creator=BOT_LOGIN, per_page=100, page=page, **params)
            issues += batch
            if len(batch) < 100:
                return issues, False
        return issues, True

    def open_issues(self):
        """Open issues created by the Actions bot, oldest first."""
        issues, full = self._alert_issue_pages(MAX_OPEN_PAGES, state="open", sort="created",
                                               direction="asc")
        if full:
            raise SystemExit(f"More than {MAX_OPEN_PAGES * 100} open issues by {BOT_LOGIN}; "
                             "refusing to guess whether an alert issue exists.")
        return issues

    def closed_issues(self, now):
        """Issues by the Actions bot closed (or updated) in the reopen window, newest first."""
        issues, _ = self._alert_issue_pages(MAX_CLOSED_PAGES, state="closed", sort="updated",
                                            direction="desc", since=iso_time(now - REOPEN_WINDOW))
        return issues

    def run_jobs(self, run_id):
        jobs = []
        for page in range(1, 6):
            batch = self.get(f"actions/runs/{int(run_id)}/jobs", per_page=100, page=page)["jobs"]
            jobs += batch
            if len(batch) < 100:
                break
        return jobs


def settle_duplicates(gh, key, mine, detail_lines):
    """Two open alert issues for one workflow (a race): keep the older, close the newer."""
    hits = open_alert_issues(gh.open_issues(), key)
    if len(hits) < 2:
        return
    keep, extras = int(hits[0]["number"]), hits[1:]
    for extra in extras:
        number = int(extra["number"])
        gh.write("POST", f"issues/{number}/comments", {"body": duplicate_comment(keep)})
        gh.write("PATCH", f"issues/{number}", {"state": "closed", "state_reason": "not_planned"})
        if number == mine and detail_lines:
            gh.write("POST", f"issues/{keep}/comments", {"body": "\n".join(detail_lines)})


def apply(gh, key, action, detail_lines=None, closing_comment=None, now=None,
          finding_kind=None, reconcile_after=None):
    """Carry out the plan for one workflow.

    `finding_kind` marks the text as one of the watchdog's own findings (see
    `last_finding_kind`). `reconcile_after` is the last update time of the run being
    reconciled: an alert issue closed at or after it means someone dealt with that run.
    """
    now = now or datetime.now(timezone.utc)
    existing = find_alert_issue(gh.open_issues(), key)
    closed, handled = None, False
    if existing is None and action in ("alert", "reconcile"):
        closed_issues = gh.closed_issues(now)
        closed = find_recently_closed_issue(closed_issues, key, now)
        handled = (action == "reconcile" and reconcile_after is not None
                   and closed_since(closed_issues, key, reconcile_after))
    step = "none" if handled else plan(existing, action, closed)
    lines = with_finding(detail_lines, finding_kind) if detail_lines else detail_lines
    if step == "comment" and finding_kind and last_recorded_kind(gh, existing) == finding_kind:
        step = "none"
    if step == "create":
        created = gh.create_assigned({"title": title_for(key),
                                      "body": issue_body(key, detail_lines, finding_kind)})
        settle_duplicates(gh, key, int(created["number"]), lines)
    elif step == "reopen":
        number = int(closed["number"])
        gh.write("PATCH", f"issues/{number}", {"state": "open", "state_reason": "reopened"})
        gh.write("POST", f"issues/{number}/comments", {"body": "\n".join(lines)})
        gh.add_owner(number)
        settle_duplicates(gh, key, number, lines)
    elif step == "comment":
        gh.write("POST", f"issues/{int(existing['number'])}/comments", {"body": "\n".join(lines)})
    elif step == "close":
        number = int(existing["number"])
        gh.write("POST", f"issues/{number}/comments", {"body": closing_comment})
        gh.write("PATCH", f"issues/{number}", {"state": "closed", "state_reason": "completed"})
    print(f"{WATCHED[key]}: {action} -> {step}")
    return step


def last_recorded_kind(gh, issue):
    """Kind of the newest finding the watchdog recorded on an open alert issue, or None.

    Reads the newest page of bot comments, then the issue body, which is the oldest
    record; the body counts only when that page holds every comment.
    """
    count = int(issue.get("comments") or 0)
    texts = [c.get("body") for c in gh.recent_comments(issue["number"], count)]
    if count <= COMMENTS_PER_PAGE:
        texts.append(issue.get("body"))
    return last_finding_kind(texts)


def cmd_run(gh, event_path, now=None):
    with open(event_path, encoding="utf-8") as handle:
        run = json.load(handle)["workflow_run"]
    key = workflow_key(run.get("path"))
    if run.get("event") != "schedule" or key is None:
        print("Not a scheduled run of a watched workflow; nothing to do.")
        return 0
    action = classify_conclusion(run.get("conclusion"))
    if action == "ignore":
        print(f"Conclusion {run.get('conclusion')!r} raises no alert and proves nothing; nothing to do.")
        return 0
    if action == "recover":
        branch = gh.get("")["default_branch"]
        newest = gh.get(f"actions/workflows/{key}/runs", event="schedule", branch=branch,
                        per_page=1)["workflow_runs"]
        if not is_newest_scheduled(run, newest):
            print("A newer scheduled run exists; this success proves nothing about it. Nothing to do.")
            return 0
        apply(gh, key, action, closing_comment=recovery_comment(gh.repo, key, run), now=now)
    else:
        apply(gh, key, action, run_alert(gh.repo, key, run, gh.run_jobs(run["id"])), now=now)
    return 0


def cmd_watchdog(gh, key, now):
    if key not in WATCHED:
        raise SystemExit(f"{key!r} is not a watched workflow")
    if gh.dry_run:
        print("Report only (a manual run or --dry-run): this run reads and prints but writes no issue or "
              "comment. A 'no scheduled run in 12 hours' finding is meaningful only around the "
              "scheduled time.")
    branch = gh.get("")["default_branch"]
    state = gh.get(f"actions/workflows/{key}")["state"]
    runs = gh.get(f"actions/workflows/{key}/runs", event="schedule", branch=branch,
                  per_page=10)["workflow_runs"]
    finding = watchdog_finding(state, runs, now, branch)
    if finding is not None:
        apply(gh, key, "alert", watchdog_alert(gh.repo, key, finding, branch), now=now,
              finding_kind=finding["kind"])
        return 0
    latest = newest_completed(runs, branch)
    if latest is not None and classify_conclusion(latest.get("conclusion")) == "alert":
        if latest.get("id") != newest_scheduled(runs, branch).get("id"):
            print(f"{WATCHED[key]}: a newer scheduled run is still in progress; its own "
                  "completion raises the alert, so nothing is reconciled.")
            return 0
        apply(gh, key, "reconcile", run_alert(gh.repo, key, latest, gh.run_jobs(latest["id"])), now=now,
              reconcile_after=parse_time(latest.get("updated_at") or latest["created_at"]))
        return 0
    print(f"{WATCHED[key]}: a scheduled run was created within 12 hours and nothing needs "
          "reconciling; healthy.")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("mode", choices=("run", "watchdog"))
    parser.add_argument("--dry-run", action="store_true", help="print writes instead of calling them")
    args = parser.parse_args(argv)
    report_only = args.mode == "watchdog" and not may_write(os.environ.get("WATCHDOG_EVENT"))
    gh = Gh(os.environ["GITHUB_REPOSITORY"], dry_run=args.dry_run or report_only,
            owner=os.environ.get("GITHUB_REPOSITORY_OWNER"))
    if args.mode == "run":
        return cmd_run(gh, os.environ["GITHUB_EVENT_PATH"])
    return cmd_watchdog(gh, os.environ["WATCHED_FILE"], datetime.now(timezone.utc))


if __name__ == "__main__":
    sys.exit(main())
