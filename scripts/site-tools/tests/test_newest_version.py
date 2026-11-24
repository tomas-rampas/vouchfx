"""The sites name the newest stable version; a pre-release only before the first stable one."""

from __future__ import annotations

import pytest

import vouchfx_site_tools as tools
from vouchfx_site_tools import _newest_release_tag, newest_version


def _release(tag: str, *, prerelease: bool = False, draft: bool = False) -> dict:
    return {"tag_name": tag, "prerelease": prerelease, "draft": draft}


def test_only_prereleases_published_uses_the_newest_prerelease() -> None:
    assert newest_version(["1.0.0-alpha.1", "1.0.0-alpha.10", "1.0.0-alpha.2"]) == "1.0.0-alpha.10"


def test_newer_prerelease_beside_older_stable_names_the_stable_version() -> None:
    assert newest_version(["1.3.0", "1.4.0", "1.5.0-rc.1"]) == "1.4.0"


def test_stable_newer_than_every_prerelease() -> None:
    assert newest_version(["1.5.0-rc.1", "1.5.0", "1.4.0"]) == "1.5.0"


def test_unsorted_input_and_numeric_ordering() -> None:
    assert newest_version(["1.9.0", "1.10.0", "1.2.0", "1.10.0-rc.1"]) == "1.10.0"


def test_prerelease_identifier_ordering() -> None:
    assert newest_version(["1.5.0-rc.2", "1.5.0-rc.10", "1.5.0-beta.1"]) == "1.5.0-rc.10"


def test_prerelease_flag_from_the_source_is_honoured() -> None:
    assert newest_version(["1.4.0", "1.5.0"], {"1.5.0": True}) == "1.4.0"


def test_empty_list_raises_so_the_fallback_is_used() -> None:
    with pytest.raises(ValueError):
        newest_version([])


def test_release_tags_skip_drafts_and_prefer_stable() -> None:
    releases = [
        _release("v1.5.0-rc.1", prerelease=True),
        _release("v1.6.0", draft=True),
        _release("v1.4.0"),
        _release("v1.3.0"),
    ]
    assert _newest_release_tag(releases) == "v1.4.0"


def test_release_tags_use_a_prerelease_when_no_stable_exists() -> None:
    releases = [_release("v1.0.0-alpha.2", prerelease=True), _release("v1.0.0-alpha.3", prerelease=True)]
    assert _newest_release_tag(releases) == "v1.0.0-alpha.3"


def test_release_tags_empty_raises() -> None:
    with pytest.raises(ValueError):
        _newest_release_tag([_release("v1.0.0", draft=True)])


def test_nuget_fetchers_name_the_stable_version(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(tools, "_fetch_json", lambda *a, **k: {"versions": ["1.4.0", "1.5.0-rc.1"]})
    assert tools._fetch_sdk_version() == "1.4.0"
    assert tools._fetch_community_jsonrpc_version() == "1.4.0"


def test_engine_fetcher_names_the_stable_version(monkeypatch: pytest.MonkeyPatch) -> None:
    urls: list[str] = []

    def fake(url, *a, **k):
        urls.append(url)
        return [_release("v1.5.0-rc.1", prerelease=True), _release("v1.4.0")]

    monkeypatch.setattr(tools, "_fetch_json", fake)
    assert tools._fetch_engine_release() == "v1.4.0"
    # One page of 100: the newest stable release must be in the page that is read.
    assert urls == ["https://api.github.com/repos/tomas-rampas/vouchfx/releases?per_page=100"]


def test_failure_falls_back_to_the_checked_in_value(monkeypatch: pytest.MonkeyPatch, tmp_path) -> None:
    (tmp_path / "facts-fallback.json").write_text(
        '{"engine_release": "v1.4.0", "sdk_version": "1.4.0", "community_jsonrpc_version": "1.0.0", '
        '"community_provider_count": "1"}',
        encoding="utf-8",
    )

    def boom(*a, **k):
        raise OSError("network down")

    monkeypatch.setattr(tools, "_fetch_json", boom)
    config = tools.SiteConfig.__new__(tools.SiteConfig)
    config.fact_overrides = {"community_provider_count": lambda: "1"}
    facts = tools.fetch_facts(config, tmp_path)
    assert facts["engine_release"] == "v1.4.0"
    assert facts["sdk_version"] == "1.4.0"
