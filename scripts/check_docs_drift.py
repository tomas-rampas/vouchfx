#!/usr/bin/env python3
"""The vouchfx ecosystem drift sentinel.

Crawls the four live project sites (engine, provider hub, samples,
telemetry backend) and checks for the failure modes that a purely
build-time check (build_site.py) cannot catch, because they only manifest
once pages are actually deployed and cross-linked:

  (a) every internal link and every cross-site link among the four sites
      resolves with HTTP 200;
  (b) no page leaks internal-planning terminology (sprint/task IDs, the
      untracked internal docs, etc.) that must never reach a published page;
  (c) the version/count facts baked into each site's landing status line at
      its last build still match the live sources (a site that hasn't
      redeployed since the fact changed is "stale", not broken).

This script only reads the public internet and prints a report; it never
mutates anything and never files a GitHub issue itself — the calling
workflow (.github/workflows/docs-drift.yml) does that from the exit code
and captured output.

    python scripts/check_docs_drift.py

Exit 0: clean. Exit 1: findings were printed above.

Stdlib only — no dependencies to install.
"""
from __future__ import annotations

import json
import re
import sys
import time
from collections import deque
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin, urlparse
from urllib.request import Request, urlopen

UA = "vouchfx-docs-drift-sentinel"
TIMEOUT = 10
MIN_INTERVAL = 1.0  # polite ceiling: 1 request/second per host
MAX_DEPTH = 2
MAX_PAGES_PER_SITE = 25

SITES: dict[str, str] = {
    "vouchfx": "https://tomas-rampas.github.io/vouchfx/",
    "vouchfx-providers": "https://tomas-rampas.github.io/vouchfx-providers/",
    "vouchfx-samples": "https://tomas-rampas.github.io/vouchfx-samples/",
    "vouchfx-telemetry-backend": "https://tomas-rampas.github.io/vouchfx-telemetry-backend/",
}

# Internal-planning terminology that must never reach a published page.
FORBIDDEN_PATTERNS = [
    re.compile(r"S\d{2}-[A-Z]-\d{2}"),
    re.compile(r"\bS\d{2}-[A-Z]\d{2}\b"),
    re.compile(r"[Ss]print[- ]\d+"),
    re.compile(r"03_MVP"),
    re.compile(r"docs/reviews"),
    re.compile(r"HUMAN_TODO"),
    re.compile(r"vouchfx#\d+"),
    re.compile(r"Phase [A-Z0-9] of #\d+"),
]

HREF_RE = re.compile(r'href="([^"]+)"')

# Landing status lines are anchored to specific phrasing (e.g. "Live: engine
# vX.Y.Z ..." on every docs.html portal; "currently <code>vX.Y.Z</code>" on
# the engine's own roadmap). A bare "any dotted triplet on the page" scan is
# too broad — content pages are full of unrelated version-shaped numbers
# (dependency versions, ports, dates) — so each fact is matched only in its
# known context. (fact key, pattern with one capture group for the value).
_VER = r"(\d+\.\d+\.\d+(?:-[0-9A-Za-z.+-]*[0-9A-Za-z])?)"
FACT_LINE_PATTERNS: list[tuple[str, re.Pattern]] = [
    ("engine_release", re.compile(r"currently <code>v" + _VER + r"</code>")),
    ("engine_release", re.compile(r"Live: engine v" + _VER)),
    ("sdk_version", re.compile(r"Vouchfx\.Sdk</code>\s*" + _VER + r"\s*on NuGet")),
    ("community_jsonrpc_version", re.compile(r"Vouchfx\.Community\.JsonRpc</code>\s*" + _VER)),
    ("community_provider_count", re.compile(r"lists (\d+) community provider")),
]

_cache: dict[str, tuple[int | None, str | None]] = {}
_last_request: dict[str, float] = {}


def fetch(url: str) -> tuple[int | None, str | None]:
    """GET url once, memoised, rate-limited to MIN_INTERVAL/host. Returns
    (status, html_body) — body is None for non-2xx or non-HTML responses."""
    if url in _cache:
        return _cache[url]
    host = urlparse(url).netloc
    wait = MIN_INTERVAL - (time.monotonic() - _last_request.get(host, 0.0))
    if wait > 0:
        time.sleep(wait)
    _last_request[host] = time.monotonic()
    try:
        req = Request(url, headers={"User-Agent": UA})
        with urlopen(req, timeout=TIMEOUT) as resp:  # nosec B310 - fixed https URLs only
            status = resp.status
            ctype = resp.headers.get("Content-Type", "")
            body = resp.read().decode("utf-8", errors="replace") if "html" in ctype else None
    except HTTPError as e:
        status, body = e.code, None
    except (URLError, TimeoutError, OSError):
        status, body = None, None
    _cache[url] = (status, body)
    return status, body


def is_tracked_site(url: str) -> str | None:
    """Return the site name if url falls under one of the four base URLs."""
    for name, base in SITES.items():
        if url.startswith(base):
            return name
    return None


def extract_hrefs(body: str) -> list[str]:
    out = []
    for href in HREF_RE.findall(body):
        if href.startswith(("#", "mailto:", "javascript:", "tel:")):
            continue
        out.append(href)
    return out


def crawl(name: str, base_url: str) -> tuple[dict[str, str], list[tuple[str, str]]]:
    """BFS-crawl one site up to MAX_DEPTH/MAX_PAGES_PER_SITE. Returns
    (fetched html pages by url, list of (referrer, target) links pointing at
    one of the four tracked sites — same-site or cross-site)."""
    pages: dict[str, str] = {}
    links: list[tuple[str, str]] = []
    seen: set[str] = set()
    queue: deque[tuple[str, int]] = deque([(base_url, 0), (urljoin(base_url, "docs.html"), 0)])
    while queue and len(pages) < MAX_PAGES_PER_SITE:
        url, depth = queue.popleft()
        url = url.split("#", 1)[0]
        if url in seen:
            continue
        seen.add(url)
        status, body = fetch(url)
        if status != 200 or body is None:
            continue
        pages[url] = body
        if depth >= MAX_DEPTH:
            continue
        for href in extract_hrefs(body):
            target = urljoin(url, href).split("#", 1)[0]
            if not target.startswith("http"):
                continue
            if is_tracked_site(target) is not None:
                links.append((url, target))
                if target.startswith(base_url) and target not in seen and len(pages) < MAX_PAGES_PER_SITE:
                    queue.append((target, depth + 1))
    return pages, links


def _fetch_json(url: str) -> object:
    headers = {"User-Agent": UA, "Accept": "application/json"}
    if url.startswith("https://api.github.com/"):
        headers["Accept"] = "application/vnd.github+json"
    req = Request(url, headers=headers)
    with urlopen(req, timeout=TIMEOUT) as resp:  # nosec B310 - fixed https URLs only
        return json.loads(resp.read().decode("utf-8"))


def _latest_registry_count(data: object) -> str:
    if not isinstance(data, list):
        raise ValueError("registry JSON is no longer a top-level array")
    return str(len(data))


# Same four sources fact injection uses (build_site.py): (fact key, source URL, extractor).
FACT_SOURCES = [
    ("engine_release", "https://api.github.com/repos/tomas-rampas/vouchfx/releases",
     lambda d: next(r["tag_name"] for r in d if not r.get("draft"))),
    ("sdk_version", "https://api.nuget.org/v3-flatcontainer/vouchfx.sdk/index.json",
     lambda d: d["versions"][-1]),
    ("community_jsonrpc_version", "https://api.nuget.org/v3-flatcontainer/vouchfx.community.jsonrpc/index.json",
     lambda d: d["versions"][-1]),
    ("community_provider_count",
     "https://raw.githubusercontent.com/tomas-rampas/vouchfx-providers/main/registry/community-providers.json",
     _latest_registry_count),
]


def fetch_live_facts() -> dict[str, str]:
    facts: dict[str, str] = {}
    for key, url, extract in FACT_SOURCES:
        try:
            facts[key] = extract(_fetch_json(url))
        except Exception as e:
            print(f"  warning: could not resolve live {key}: {e}")
    return facts


def main() -> int:
    findings = 0

    print("== crawling the four sites ==")
    all_pages: dict[str, dict[str, str]] = {}
    all_links: list[tuple[str, str]] = []
    for name, base in SITES.items():
        pages, links = crawl(name, base)
        all_pages[name] = pages
        all_links.extend(links)
        print(f"  {name}: {len(pages)} page(s) fetched")

    print("\n== link resolution (internal + cross-site) ==")
    broken: list[tuple[str, str, int | None]] = []
    checked: set[str] = set()
    for referrer, target in all_links:
        if target in checked:
            status, _ = _cache.get(target, (None, None))
        else:
            status, _ = fetch(target)
            checked.add(target)
        if status != 200:
            broken.append((referrer, target, status))
    if broken:
        findings += len(broken)
        for referrer, target, status in broken:
            print(f"  BROKEN [{status}] {target}  (linked from {referrer})")
    else:
        print("  all links resolved (HTTP 200)")

    print("\n== forbidden internal-planning terms ==")
    leaked = 0
    for name, pages in all_pages.items():
        for url, body in pages.items():
            for pattern in FORBIDDEN_PATTERNS:
                m = pattern.search(body)
                if m:
                    leaked += 1
                    findings += 1
                    print(f"  LEAK [{name}] {url}: matched /{pattern.pattern}/ -> {m.group(0)!r}")
    if leaked == 0:
        print("  none found")

    print("\n== hand-drawn diagrams in rendered code blocks ==")
    # Hand-aligned box-drawing diagrams render broken (misaligned columns) and
    # are banned on the published sites — structured diagrams use ```mermaid
    # fences instead. A <pre> block with four or more box-drawing lines is
    # diagram-scale; short CLI-output excerpts (tree glyphs etc.) stay legal.
    broken_art = 0
    box_chars = re.compile(r"[─│┌┐└┘├┤┬┴]")
    pre_block = re.compile(r"<pre[^>]*>.*?</pre>", re.DOTALL)
    for name, pages in all_pages.items():
        for url, body in pages.items():
            for block in pre_block.findall(body):
                art_lines = sum(1 for line in block.splitlines() if box_chars.search(line))
                if art_lines >= 4:
                    broken_art += 1
                    findings += 1
                    print(f"  DIAGRAM [{name}] {url}: a rendered code block has {art_lines} box-drawing lines (convert to a mermaid fence)")
    if broken_art == 0:
        print("  none found")

    print("\n== live facts vs. rendered landing status lines ==")
    live_facts = fetch_live_facts()
    stale = 0
    if live_facts:
        for name, pages in all_pages.items():
            for url, body in pages.items():
                if not (url == SITES[name] or url.rstrip("/").endswith("docs.html")):
                    continue  # only the landing status lines, not every crawled page
                for fact_key, pattern in FACT_LINE_PATTERNS:
                    if fact_key not in live_facts:
                        continue
                    m = pattern.search(body)
                    if not m:
                        continue  # this site's page doesn't carry that status line
                    found, live = m.group(1), live_facts[fact_key].lstrip("v")
                    if found != live:
                        stale += 1
                        findings += 1
                        print(f"  STALE [{name}] {url}: {fact_key} = {found!r} (live: {live!r})")
        if stale == 0:
            print("  all rendered landing facts match live sources")
    else:
        print("  no live facts resolved (all four sources failed) - skipping staleness check")

    print(f"\n== summary: {findings} finding(s) ==")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
