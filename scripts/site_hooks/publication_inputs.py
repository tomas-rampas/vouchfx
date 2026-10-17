"""Require explicitly reviewed source paths before MkDocs reads or publishes them."""
from __future__ import annotations

import sys
from pathlib import Path

from mkdocs.exceptions import PluginError
from mkdocs.plugins import event_priority

sys.path.insert(0, str(Path(__file__).resolve().parent))
from _publication_inputs import (  # noqa: E402
    PublicationInputError, validate_mkdocs_files, validate_source_inputs, validate_snippet_inputs,
)

REPO_ROOT = Path(__file__).resolve().parents[2]


def _validate(config):
    if Path(config["docs_dir"]) != REPO_ROOT / "docs":
        raise PluginError("publication inputs: docs_dir must be the repository docs/ directory")
    try:
        inputs = validate_source_inputs(REPO_ROOT)
        validate_snippet_inputs(REPO_ROOT, inputs)
        return inputs
    except PublicationInputError as exc:
        raise PluginError(f"publication inputs: {exc}") from None


@event_priority(1000)
def on_config(config, **kwargs):
    # Run before plugins consume inputs such as blog/.authors.yml and posts.
    _validate(config)
    return config


@event_priority(-1000)
def on_files(files, *, config, **kwargs):
    inputs = _validate(config)
    try:
        validate_mkdocs_files(files, REPO_ROOT, inputs, config["theme"].dirs)
    except PublicationInputError as exc:
        raise PluginError(f"publication inputs: {exc}") from None
    return files
