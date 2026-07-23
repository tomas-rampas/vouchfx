"""Regression tests pinning vouchfx_site_tools' ``SiteConfig.site_url`` contract.

Context (vouchfx engine issue #254): three satellite repositories
(vouchfx-providers, vouchfx-samples, vouchfx-telemetry-backend) pip-install
this package at a pinned SHA, so every property of ``site_url`` being
*optional and additive* has to hold across every future edit to this module —
today it is verified only by a manual recursive diff. These tests pin, on
both branches of ``site_url`` (unset vs. set), the exact contract documented
on ``SiteConfig.site_url`` and implemented by ``render_markdown()`` /
``write_robots_and_sitemap()`` / ``build()``:

``site_url`` unset (``None``, the dataclass default)
    * no ``robots.txt``, no ``sitemap.xml`` in the build output;
    * ``render_markdown()`` never constructs/passes a ``canonical`` kwarg to
      ``page_template.format(...)``;
    * the emitted tree is byte-identical between a config that omits
      ``site_url`` and one that passes ``site_url=None`` explicitly — both
      resolve to the same dataclass default (``None``); this pins
      ``build()``'s determinism and the default itself, not general
      falsy-value handling (see the test's own docstring below).

``site_url`` set
    * ``robots.txt``: an allow-all body plus a ``Sitemap:`` line pointing at
      the configured origin;
    * ``sitemap.xml``: every ``<loc>`` is on the configured origin, and the
      root page (``index.html``) is listed as the *bare* origin, never
      ``.../index.html``;
    * ``render_markdown()`` passes ``canonical=<absolute page URL>`` to
      ``page_template.format(...)``, and the value lands correctly in a
      template that references ``{canonical}``;
    * a ``page_template`` with **no** ``{canonical}`` placeholder still
      renders (the extra ``str.format()`` keyword argument is a harmless
      no-op, not a ``KeyError``/crash) — this is what every one of the three
      satellites' real templates look like today;
    * every one of the above is exercised for BOTH a bare-origin ``site_url``
      and one with a trailing slash — ``site_url_join()``/``_sitemap_loc()``/
      the robots.txt ``Sitemap:`` line all deliberately tolerate either form
      (``SiteConfig.site_url``'s own docstring example, ``"https://
      vouchfx.io/"``, carries one) — so a regression that doubles a slash
      (e.g. ``https://x//sitemap.xml``) cannot pass unnoticed.

Every test drives the module's real public API (``SiteConfig`` + ``build()``,
see scripts/site-tools/README.md) end to end against a throw-away repo
skeleton under ``tmp_path`` — never a private helper — so a regression here
would actually have broken a satellite's next site build.

All four of the module's ``DEFAULT_FACT_FETCHERS`` are overridden with fixed
values (via ``SiteConfig.fact_overrides``) so the suite performs no network
I/O and is fully deterministic offline.

Run (from the repo root):
    python -m pytest scripts/site-tools/tests -q
or (cwd scripts/site-tools, where ``[tool.pytest.ini_options]`` pins
``testpaths``):
    python -m pytest -q
"""

from __future__ import annotations

import re
from pathlib import Path

import pytest

from vouchfx_site_tools import SiteConfig, build

# ---------------------------------------------------------------------------
# Templates
# ---------------------------------------------------------------------------

# Deliberately WITHOUT a {canonical} placeholder — this is what every one of
# the three satellites' real page templates looks like today. Reused by the
# site_url-unset tests (where a {canonical} placeholder would raise KeyError,
# since format_kwargs never constructs that key on that branch) and by the
# site_url-set "no-placeholder-still-renders" assertion.
PAGE_TEMPLATE_NO_CANONICAL = (
    "<html><head><title>{title}</title>"
    '<meta name="description" content="{desc}"></head>'
    "<body>{sidebar}<main>{body}</main>{toc}{mermaid_script}</body></html>"
)

# A second template WITH the placeholder, used only by the test that asserts
# the *value* substituted into {canonical} when site_url is set.
PAGE_TEMPLATE_WITH_CANONICAL = (
    '<html><head><title>{title}</title><link rel="canonical" href="{canonical}">'
    '<meta name="description" content="{desc}"></head>'
    "<body>{sidebar}<main>{body}</main>{toc}{mermaid_script}</body></html>"
)

PORTAL_HTML = "<html><body>Docs portal</body></html>"

# The canonical (no pun intended), no-trailing-slash form of the configured
# origin. Every expected value in the site_url-set tests is built from THIS
# constant — never from the parametrized `site_url` fixture value below — so
# that a regression which fails to strip a trailing slash before joining
# (producing e.g. "https://x//sitemap.xml") shows up as an exact-match
# failure regardless of which input form triggered it.
BARE_ORIGIN = "https://example-site.vouchfx.io"

# Every "site_url set" test below runs once per input form: bare origin, and
# with a trailing slash — both must normalise to the same output (see
# BARE_ORIGIN above). Reused as a decorator across those tests.
SITE_URL_CASES = pytest.mark.parametrize(
    "site_url",
    [BARE_ORIGIN, BARE_ORIGIN + "/"],
    ids=["no-trailing-slash", "trailing-slash"],
)


def _assert_no_doubled_slash(url: str) -> None:
    """Assert `url` has no doubled slash after its `scheme://` prefix — the
    exact failure mode a trailing-slash `site_url` must not trigger (e.g.
    `site_url_join`/`_sitemap_loc` failing to collapse "https://x/" + "/" +
    "sitemap.xml" down to a single separating slash)."""
    scheme, sep, rest = url.partition("://")
    assert sep, f"{url!r} has no scheme separator"
    assert "//" not in rest, f"doubled slash in {url!r}"


class RecordingTemplate(str):
    """A ``str`` subclass that records the kwargs of every ``.format()`` call.

    ``render_markdown()`` calls ``config.page_template.format(**format_kwargs)``
    on whatever object ``page_template`` holds — Python does not require it to
    be a plain ``str``. Wrapping the real template string this way lets a test
    assert exactly which kwargs the module constructs (in particular: whether
    ``canonical`` is present) by driving the real public API, rather than
    reaching into any private helper.
    """

    calls: list[dict[str, str]]

    def __new__(cls, value: str) -> "RecordingTemplate":
        obj = super().__new__(cls, value)
        obj.calls = []
        return obj

    def format(self, **kwargs: str) -> str:  # type: ignore[override]
        self.calls.append(dict(kwargs))
        return str.format(self, **kwargs)


# ---------------------------------------------------------------------------
# Fixtures / helpers
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _no_github_repository_env(monkeypatch: pytest.MonkeyPatch) -> None:
    """Keep ``github_url`` derivation deterministic: ``build()`` prefers the
    ``GITHUB_REPOSITORY`` env var over ``SiteConfig.default_repo`` when
    present, which would otherwise vary between a local run and a run inside
    this repository's own GitHub Actions."""
    monkeypatch.delenv("GITHUB_REPOSITORY", raising=False)


def _make_repo(tmp_path: Path) -> Path:
    """A minimal repo skeleton satisfying everything ``build()`` touches:
    ``site/facts-fallback.json`` (``fetch_facts()``'s fallback source),
    ``site/index.html`` (the landing page ``build()`` copies verbatim, and
    that ``write_robots_and_sitemap()``'s sitemap always lists by name), and
    one published markdown doc under ``docs/``.

    The doc is deliberately named ``getting-started.md``, not ``index.md`` —
    keeping it distinct from the root ``index.html`` (which comes from
    ``site/``, not from a rendered doc) removes any ambiguity from the
    "root page is the bare origin, not a rendered page" assertions below.
    """
    root = tmp_path / "repo"
    site = root / "site"
    site.mkdir(parents=True)
    (site / "facts-fallback.json").write_text("{}", encoding="utf-8")
    (site / "index.html").write_text("<html><body>Landing</body></html>", encoding="utf-8")

    docs = root / "docs"
    docs.mkdir()
    (docs / "getting-started.md").write_text("# Getting started\n\nHello world.\n", encoding="utf-8")

    return root


def _base_kwargs(root: Path, page_template: str) -> dict[str, object]:
    """``SiteConfig`` kwargs shared by every test, everything except
    ``site_url`` (which each test supplies, or omits, itself).

    All four ``DEFAULT_FACT_FETCHERS`` are overridden with fixed values —
    ``fetch_facts()`` merges ``config.fact_overrides`` over the module
    defaults keyed by name — so the suite never performs a network call.
    """
    return dict(
        root=root,
        default_repo="tomas-rampas/vouchfx",
        docs=[("docs/getting-started.md", "Guides", "Getting started")],
        page_template=page_template,
        portal_html=PORTAL_HTML,
        meta_description_prefix="vouchfx test site",
        fact_overrides={
            "engine_release": lambda: "v0.0.0-test",
            "sdk_version": lambda: "0.0.0-test",
            "community_jsonrpc_version": lambda: "0.0.0-test",
            "community_provider_count": lambda: "0",
        },
    )


def _tree(out: Path) -> dict[str, bytes]:
    """Every file under ``out``, relative-posix-path -> bytes — for a
    tree/byte comparison that does not care about filesystem traversal
    order. Both sides of any comparison built from this helper are always
    produced by the same code, in the same process, on the same OS, so a
    platform's newline translation (e.g. CRLF on Windows) can never cause a
    spurious mismatch between them."""
    return {p.relative_to(out).as_posix(): p.read_bytes() for p in sorted(out.rglob("*")) if p.is_file()}


# ---------------------------------------------------------------------------
# site_url unset (None, the default) — must be a strict no-op
# ---------------------------------------------------------------------------


def test_site_url_unset_emits_no_robots_or_sitemap(tmp_path: Path) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root, PAGE_TEMPLATE_NO_CANONICAL))
    assert config.site_url is None

    build(config, out)

    names = {p.name for p in out.rglob("*") if p.is_file()}
    assert "robots.txt" not in names
    assert "sitemap.xml" not in names


def test_site_url_unset_never_constructs_canonical_kwarg(tmp_path: Path) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    template = RecordingTemplate(PAGE_TEMPLATE_NO_CANONICAL)
    config = SiteConfig(**_base_kwargs(root, template))

    build(config, out)

    assert template.calls, "expected at least one page_template.format() call"
    for call_kwargs in template.calls:
        assert "canonical" not in call_kwargs


def test_site_url_unset_is_byte_identical_to_the_dataclass_default(tmp_path: Path) -> None:
    """Pins two things: (1) the ``SiteConfig.site_url`` dataclass default is
    still ``None`` — a silent default change would flip the sanity assertion
    below — and (2) ``build()`` is deterministic: two independent builds from
    equivalent configs (one omitting ``site_url``, one passing it explicitly
    as ``None``; both therefore resolve to the *same* value, ``None``)
    produce a byte-identical tree.

    This does NOT distinguish ``None`` from another falsy value such as
    ``""`` — both configs here evaluate to the identical value, so no
    empty-string inertness claim is made or tested here.
    """
    root = _make_repo(tmp_path)
    out_default = root / "_out_default"
    out_explicit_none = root / "_out_explicit_none"

    config_default = SiteConfig(**_base_kwargs(root, PAGE_TEMPLATE_NO_CANONICAL))
    assert config_default.site_url is None  # sanity: the field really defaults to None
    build(config_default, out_default)

    config_explicit_none = SiteConfig(**_base_kwargs(root, PAGE_TEMPLATE_NO_CANONICAL), site_url=None)
    build(config_explicit_none, out_explicit_none)

    tree_default = _tree(out_default)
    tree_explicit = _tree(out_explicit_none)
    assert set(tree_default) == set(tree_explicit)
    assert tree_default == tree_explicit


# ---------------------------------------------------------------------------
# site_url set — parametrized over both a bare-origin form and a
# trailing-slash form (SITE_URL_CASES), since site_url_join()/_sitemap_loc()/
# the robots.txt Sitemap: line all deliberately tolerate either.
# ---------------------------------------------------------------------------


@SITE_URL_CASES
def test_site_url_set_emits_allow_all_robots_txt_with_sitemap_line(tmp_path: Path, site_url: str) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root, PAGE_TEMPLATE_NO_CANONICAL), site_url=site_url)

    build(config, out)

    robots = (out / "robots.txt").read_text(encoding="utf-8")
    assert "User-agent: *" in robots
    assert "Allow: /" in robots

    match = re.search(r"^Sitemap: (\S+)$", robots, re.MULTILINE)
    assert match, "expected a Sitemap: line in robots.txt"
    sitemap_url = match.group(1)
    assert sitemap_url == f"{BARE_ORIGIN}/sitemap.xml"
    _assert_no_doubled_slash(sitemap_url)


@SITE_URL_CASES
def test_site_url_set_emits_sitemap_with_every_loc_on_configured_origin(tmp_path: Path, site_url: str) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root, PAGE_TEMPLATE_NO_CANONICAL), site_url=site_url)

    build(config, out)

    sitemap = (out / "sitemap.xml").read_text(encoding="utf-8")
    locs = re.findall(r"<loc>(.*?)</loc>", sitemap)
    assert locs, "expected at least one <loc> entry"
    for loc in locs:
        assert loc.startswith(BARE_ORIGIN + "/"), f"{loc!r} is not on the configured origin"
        _assert_no_doubled_slash(loc)


@SITE_URL_CASES
def test_site_url_set_lists_root_page_as_bare_origin_not_index_html(tmp_path: Path, site_url: str) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root, PAGE_TEMPLATE_NO_CANONICAL), site_url=site_url)

    build(config, out)

    sitemap = (out / "sitemap.xml").read_text(encoding="utf-8")
    locs = set(re.findall(r"<loc>(.*?)</loc>", sitemap))
    assert f"{BARE_ORIGIN}/" in locs
    assert f"{BARE_ORIGIN}/index.html" not in locs
    # The rendered doc page and the docs.html portal must still be present,
    # proving the bare-origin substitution is index.html-specific rather
    # than e.g. an accidental truncation of every entry.
    assert f"{BARE_ORIGIN}/docs.html" in locs
    assert f"{BARE_ORIGIN}/docs/getting-started.html" in locs
    for loc in locs:
        _assert_no_doubled_slash(loc)


@SITE_URL_CASES
def test_site_url_set_passes_canonical_kwarg_to_page_template(tmp_path: Path, site_url: str) -> None:
    root = _make_repo(tmp_path)
    out = root / "_out"
    template = RecordingTemplate(PAGE_TEMPLATE_NO_CANONICAL)
    config = SiteConfig(**_base_kwargs(root, template), site_url=site_url)

    build(config, out)

    assert template.calls, "expected at least one page_template.format() call"
    for call_kwargs in template.calls:
        assert "canonical" in call_kwargs
        canonical = call_kwargs["canonical"]
        assert canonical.startswith(BARE_ORIGIN + "/")
        _assert_no_doubled_slash(canonical)


@SITE_URL_CASES
def test_site_url_set_canonical_value_is_substituted_correctly(tmp_path: Path, site_url: str) -> None:
    """A template that DOES reference ``{canonical}`` gets the expected
    absolute per-page URL substituted in."""
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root, PAGE_TEMPLATE_WITH_CANONICAL), site_url=site_url)

    build(config, out)

    page = (out / "docs" / "getting-started.html").read_text(encoding="utf-8")
    match = re.search(r'rel="canonical" href="([^"]+)"', page)
    assert match, "expected a canonical <link> in the rendered page"
    canonical_url = match.group(1)
    assert canonical_url == f"{BARE_ORIGIN}/docs/getting-started.html"
    _assert_no_doubled_slash(canonical_url)


@SITE_URL_CASES
def test_site_url_set_template_without_canonical_placeholder_still_renders(tmp_path: Path, site_url: str) -> None:
    """The additive contract's central promise: a template with NO
    ``{canonical}`` placeholder must not crash when ``site_url`` IS set —
    the extra ``str.format()`` keyword argument is silently ignored."""
    root = _make_repo(tmp_path)
    out = root / "_out"
    config = SiteConfig(**_base_kwargs(root, PAGE_TEMPLATE_NO_CANONICAL), site_url=site_url)

    build(config, out)  # must not raise (e.g. KeyError from a bad format() call)

    page = (out / "docs" / "getting-started.html").read_text(encoding="utf-8")
    assert "Hello world" in page
