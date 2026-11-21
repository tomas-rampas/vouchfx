"""Alert issues for the scheduled runs of `CI` and `.NET compatibility`.

Used only by .github/workflows/scheduled-run-alerts.yml. Two modes:

  run       The `workflow_run` job. Reads the event payload from GITHUB_EVENT_PATH.
            A failed, cancelled or otherwise unsuccessful scheduled run opens or
            updates the one alert issue for that workflow; a successful one closes
            it.
  watchdog  The daily job. Opens or updates the same issue when the workflow is not
            `active` or has no completed scheduled run in the last 36 hours. It never
            closes an issue: only a successful run proves the workflow healthy again.

Every decision and every piece of text lives in a pure function so the tests need no
GitHub. Free text from the event or the API (job names, conclusions, workflow state)
is untrusted: it reaches an issue only inside a code span after `inline_code`, and
bodies travel to `gh api` as JSON on stdin, never as arguments. Titles and the
workflow display names come from the fixed WATCHED table, not from the payload. The
script runs from the default branch and executes nothing from the triggering run.
"""

import argparse
from datetime import datetime, timedelta, timezone
import json
import os
import posixpath
import re
import subprocess
import sys

# Workflow file name -> display name. The only workflows that are watched; a payload
# naming any other workflow is ignored.
WATCHED = {"build.yml": "CI", "dotnet-compatibility.yml": ".NET compatibility"}
BOT_LOGIN = "github-actions[bot]"
MARKER_PREFIX = "<!-- vouchfx-scheduled-run-alert:"
# A scheduled completion that proves the workflow ran to a verdict. Cancelled,
# skipped and similar conclusions do not count as a day with a completed run.
VERDICT_CONCLUSIONS = frozenset({"success", "failure", "timed_out"})
# A completed run with one of these conclusions raises no alert. Every other
# conclusion does, including values GitHub may add later.
NO_ALERT_CONCLUSIONS = frozenset({"success", "skipped", "neutral"})
JOB_QUIET_CONCLUSIONS = frozenset({"success", "skipped", "neutral"})
WINDOW = timedelta(hours=36)
MAX_JOBS_LISTED = 20
REPO_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
SHA_RE = re.compile(r"^[0-9a-f]{40}$")


def marker(key):
    return f"{MARKER_PREFIX}{key} -->"


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


def classify_conclusion(conclusion):
    """`alert`, `recover` or `ignore` for a completed scheduled run."""
    if conclusion == "success":
        return "recover"
    if conclusion in NO_ALERT_CONCLUSIONS:
        return "ignore"
    return "alert"


def parse_time(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(timezone.utc)


def run_url(repo, run_id):
    return f"https://github.com/{repo}/actions/runs/{int(run_id)}"


def commit_line(repo, sha):
    if isinstance(sha, str) and SHA_RE.match(sha):
        return f"[`{sha[:12]}`](https://github.com/{repo}/commit/{sha})"
    return inline_code(sha, 40)


def unsuccessful_jobs(jobs):
    return [j for j in jobs if j.get("conclusion") not in JOB_QUIET_CONCLUSIONS]


def job_lines(repo, run_id, jobs):
    bad = unsuccessful_jobs(jobs)
    if not bad:
        return ["- No individual job is recorded as unsuccessful; open the run for the details."]
    lines = []
    for job in bad[:MAX_JOBS_LISTED]:
        line = f"- {inline_code(job.get('name'))}: {inline_code(job.get('conclusion') or job.get('status'), 30)}"
        if isinstance(job.get("id"), int):
            line += f" ([log]({run_url(repo, run_id)}/job/{job['id']}))"
        lines.append(line)
    if len(bad) > MAX_JOBS_LISTED:
        lines.append(f"- … and {len(bad) - MAX_JOBS_LISTED} more.")
    return lines


def title_for(key):
    return f"Scheduled run alert: {WATCHED[key]}"


def issue_body(key, detail_lines):
    return "\n".join([
        marker(key),
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
        "- Jobs that did not succeed:", *job_lines(repo, run_id, jobs)]


def watchdog_finding(state, runs, now, branch):
    """None when healthy, else a dict describing why the watchdog alerts."""
    if state != "active":
        return {"kind": "disabled", "state": state}
    scheduled = [r for r in runs if r.get("event") == "schedule" and r.get("head_branch") == branch]
    for run in scheduled:
        if (run.get("status") == "completed" and run.get("conclusion") in VERDICT_CONCLUSIONS
                and now - parse_time(run["updated_at"]) <= WINDOW):
            return None
    last = max(scheduled, key=lambda r: r["created_at"], default=None)
    return {"kind": "missing", "last": last}


def watchdog_alert(repo, key, finding, branch):
    name = WATCHED[key]
    if finding["kind"] == "disabled":
        return [
            f"**Workflow not active.** **{name}** has the state {inline_code(finding['state'], 40)}, "
            "so it runs no scheduled jobs.",
            "- `disabled_inactivity` means GitHub switched scheduled workflows off after 60 days "
            "without repository activity. Re-enable the workflow from the Actions tab; "
            "nothing re-enables it automatically.",
            f"- Workflow: https://github.com/{repo}/actions/workflows/{key}"]
    last = finding["last"]
    lines = [
        f"**No completed scheduled run.** **{name}** has no successful or failed scheduled run "
        f"on `{branch}` that completed in the last 36 hours (GitHub can delay or drop "
        "scheduled runs under load)."]
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
        f"(commit {commit_line(repo, run.get('head_sha'))}). Closing this issue.",
    ])


def find_alert_issue(issues, key):
    """Oldest open, non-PR issue authored by the Actions bot whose body starts with the marker."""
    wanted = marker(key)
    hits = [i for i in issues
            if "pull_request" not in i and i.get("state", "open") == "open"
            and (i.get("user") or {}).get("login") == BOT_LOGIN
            and (i.get("body") or "").split("\n", 1)[0].strip() == wanted]
    return min(hits, key=lambda i: i["number"], default=None)


def plan(existing, action):
    """`create`, `comment`, `close` or `none` for an `alert` or `recover` action."""
    if action == "alert":
        return "comment" if existing else "create"
    if action == "recover":
        return "close" if existing else "none"
    return "none"


# --- GitHub access -----------------------------------------------------------------

class Gh:
    def __init__(self, repo, dry_run=False):
        if not REPO_RE.match(repo):
            raise SystemExit(f"Refusing unexpected repository name {repo!r}")
        self.repo, self.dry_run = repo, dry_run

    def _call(self, method, path, params=None, payload=None):
        command = ["gh", "api", "-X", method, f"repos/{self.repo}" + (f"/{path}" if path else "")]
        for k, v in (params or {}).items():
            command += ["-f", f"{k}={v}"]
        data = None
        if payload is not None:
            command += ["--input", "-"]
            data = json.dumps(payload)
        result = subprocess.run(command, input=data, capture_output=True, text=True, timeout=60)
        if result.returncode:
            raise SystemExit(f"gh api {method} {path} failed ({result.returncode}): {result.stderr.strip()}")
        return json.loads(result.stdout) if result.stdout.strip() else None

    def get(self, path, **params):
        return self._call("GET", path, params)

    def write(self, method, path, payload):
        if self.dry_run:
            print(f"DRY RUN {method} {path}\n{json.dumps(payload, indent=2, ensure_ascii=False)}")
            return {"number": 0}
        return self._call(method, path, payload=payload)

    def open_issues(self):
        issues = []
        for page in range(1, 11):
            batch = self.get("issues", state="open", per_page=100, page=page)
            issues += batch
            if len(batch) < 100:
                break
        return issues

    def run_jobs(self, run_id):
        jobs = []
        for page in range(1, 6):
            batch = self.get(f"actions/runs/{int(run_id)}/jobs", per_page=100, page=page)["jobs"]
            jobs += batch
            if len(batch) < 100:
                break
        return jobs


def apply(gh, key, action, detail_lines=None, closing_comment=None):
    existing = find_alert_issue(gh.open_issues(), key)
    step = plan(existing, action)
    if step == "create":
        gh.write("POST", "issues", {"title": title_for(key), "body": issue_body(key, detail_lines)})
    elif step == "comment":
        gh.write("POST", f"issues/{existing['number']}/comments", {"body": "\n".join(detail_lines)})
    elif step == "close":
        gh.write("POST", f"issues/{existing['number']}/comments", {"body": closing_comment})
        gh.write("PATCH", f"issues/{existing['number']}", {"state": "closed", "state_reason": "completed"})
    print(f"{WATCHED[key]}: {action} -> {step}")
    return step


def cmd_run(gh, event_path):
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
        apply(gh, key, action, closing_comment=recovery_comment(gh.repo, key, run))
    else:
        apply(gh, key, action, run_alert(gh.repo, key, run, gh.run_jobs(run["id"])))
    return 0


def cmd_watchdog(gh, key, now):
    if key not in WATCHED:
        raise SystemExit(f"{key!r} is not a watched workflow")
    branch = gh.get("")["default_branch"]
    state = gh.get(f"actions/workflows/{key}")["state"]
    runs = gh.get(f"actions/workflows/{key}/runs", event="schedule", branch=branch,
                  per_page=10)["workflow_runs"]
    finding = watchdog_finding(state, runs, now, branch)
    if finding is None:
        print(f"{WATCHED[key]}: a scheduled run completed within 36 hours; healthy.")
        return 0
    apply(gh, key, "alert", watchdog_alert(gh.repo, key, finding, branch))
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("mode", choices=("run", "watchdog"))
    parser.add_argument("--dry-run", action="store_true", help="print writes instead of calling them")
    args = parser.parse_args(argv)
    gh = Gh(os.environ["GITHUB_REPOSITORY"], dry_run=args.dry_run)
    if args.mode == "run":
        return cmd_run(gh, os.environ["GITHUB_EVENT_PATH"])
    return cmd_watchdog(gh, os.environ["WATCHED_FILE"], datetime.now(timezone.utc))


if __name__ == "__main__":
    sys.exit(main())
