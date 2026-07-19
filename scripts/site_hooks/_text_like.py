"""Shared definition of "text-like" built-output files.

Imported by both scripts/check_site.py (the publication gate) and
scripts/site_hooks/facts.py (fact substitution) so the two can never
silently drift apart on what counts as "worth scanning/substituting into".
Material serialises full page text into its search index JSON, so
json/js/xml/txt matter just as much as html — this has never been an
html-only concern for either consumer, even though the outgoing custom
generator (which never produced a search index) only ever needed html.
"""
from __future__ import annotations

from collections.abc import Iterator
from pathlib import Path

TEXT_LIKE_SUFFIXES = {".html", ".js", ".json", ".xml", ".txt"}


def iter_text_like_files(site_dir: Path) -> Iterator[Path]:
    for path in site_dir.rglob("*"):
        if path.is_file() and path.suffix.lower() in TEXT_LIKE_SUFFIXES:
            yield path
