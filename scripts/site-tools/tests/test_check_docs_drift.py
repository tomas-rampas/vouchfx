"""The drift sentinel's stdlib copy of the version rule agrees with the package.

``scripts/check_docs_drift.py`` installs nothing, so it carries its own copy of
``newest_version``. One table of cases runs against both copies here, so a
change to one copy that alters its answer on any of those cases fails.
"""

from __future__ import annotations

import importlib.util
from pathlib import Path

import pytest

import vouchfx_site_tools as tools
from vouchfx_site_tools import _newest_release_tag, newest_version

_DRIFT_PATH = Path(__file__).resolve().parents[2] / "check_docs_drift.py"


def _load_drift():
    spec = importlib.util.spec_from_file_location("_check_docs_drift_under_test", _DRIFT_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


drift = _load_drift()

_HUGE = "1." + "9" * 5000  # past Python's integer-string limit

CASES = [
    ((["1.0.0-alpha.2", "1.0.0-alpha.10"], None), "1.0.0-alpha.10"),
    ((["1.4.0", "1.5.0-rc.1"], None), "1.4.0"),
    ((["1.5.0-rc.1", "1.5.0"], None), "1.5.0"),
    ((["1.9.0", "1.10.0", "1.2.0"], None), "1.10.0"),
    ((["1.0.0-alpha.1", "1.0.0-alpha.beta"], None), "1.0.0-alpha.beta"),
    ((["1.0.0-alpha", "1.0.0-alpha.1"], None), "1.0.0-alpha.1"),
    ((["1.4.0", "1.5.0"], {"1.5.0": True}), "1.4.0"),
    ((["v1.4.0", "nightly"], None), "v1.4.0"),
    ((["1.4.0+b.5", "1.3.0"], None), "1.4.0+b.5"),
    ((["1.4.0+b-5", "1.3.0"], None), "1.4.0+b-5"),  # a "-" in build metadata is not a pre-release
    ((["1.4.0", "١.5.0"], None), "1.4.0"),  # a digit outside ASCII is not a version
    ((["1.4.0", _HUGE], None), "1.4.0"),
    ((["1.4.0", "1.5.0-" + "9" * 5000], None), "1.4.0"),
    (([" 1.4.0 ", "1.3.0"], None), "1.4.0"),
    ((["nightly"], None), ValueError),
    (([_HUGE], None), ValueError),
    (([], None), ValueError),
]


@pytest.mark.parametrize(("args", "expected"), CASES)
@pytest.mark.parametrize("impl", [newest_version, drift.newest_version], ids=["package", "drift"])
def test_both_copies_agree(impl, args, expected) -> None:
    if expected is ValueError:
        with pytest.raises(ValueError):
            impl(*args)
    else:
        assert impl(*args) == expected


def test_release_lists_agree() -> None:
    releases = [
        {"tag_name": "v1.5.0-rc.1", "prerelease": True},
        {"tag_name": "v1.6.0", "draft": True},
        {"tag_name": "v1.4.0"},
    ]
    assert _newest_release_tag(releases) == drift._newest_release_tag(releases) == "v1.4.0"


def test_release_lists_honour_the_prerelease_flag() -> None:
    # No "-" in the tag: only GitHub's flag says that v1.5.0 is a pre-release.
    releases = [{"tag_name": "v1.5.0", "prerelease": True}, {"tag_name": "v1.4.0"}]
    assert _newest_release_tag(releases) == drift._newest_release_tag(releases) == "v1.4.0"


def test_both_copies_read_the_same_grammar() -> None:
    assert drift._SEMVER_RE.pattern == tools._SEMVER_RE.pattern
    assert drift._SEMVER_RE.flags == tools._SEMVER_RE.flags


def test_drift_fact_sources_use_the_rule() -> None:
    sources = {key: extract for key, _, extract in drift.FACT_SOURCES}
    data = {"versions": ["1.4.0", "1.5.0-rc.1"]}
    assert sources["sdk_version"](data) == "1.4.0"
    assert sources["community_jsonrpc_version"](data) == "1.4.0"


def test_drift_release_url_asks_for_a_full_page() -> None:
    urls = {key: url for key, url, _ in drift.FACT_SOURCES}
    assert urls["engine_release"].endswith("/releases?per_page=100")
