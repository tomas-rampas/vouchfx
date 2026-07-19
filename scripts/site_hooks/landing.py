"""MkDocs hook: publish the bespoke landing page as the site root.

vouchfx's actual homepage (site/index.html plus its CSS and small companion
assets) is a hand-built static page, not a Material page — see
scripts/build_site.py for the landing page's own generator, which this
scaffold does not touch. There is deliberately no docs/index.md: Material
has nothing to say about the homepage, and creating one would collide with
site/index.html for the root URL.

Instead, on_post_build copies site/ verbatim over the already-built MkDocs
output so that site/index.html becomes <site_dir>/index.html, dragging its
companions (.nojekyll, styles.css, docs.css, favicon.svg,
facts-fallback.json) along with it. This hook runs after every other
plugin/page has been written, so nothing the docs build produces can shadow
the landing page.

Two safety rules, enforced loudly (a failed build beats a silently wrong
one):

  - No symlinks under site/. A symlink could resolve outside the
    repository, or to something unexpectedly large or sensitive, and this
    hook has no way to audit where it points before copying through it.
    Detected without following into symlinked directories (os.walk with
    followlinks=False), so a symlink cycle can't hang the build either.

  - No silent overwriting of a page or asset the *current* MkDocs build
    produced — but silent overwriting of *this hook's own leftovers from a
    previous build* is fine, and necessary. Here's why, precisely:

    MkDocs' own site_dir cleanup (mkdocs.utils.clean_directory, run once
    at the start of every build unless --dirty) inspects only the
    TOP-LEVEL entries of site_dir and skips deleting any whose name
    starts with "." — file or directory, left with its entire subtree
    untouched (its own comment: "We never copy files that are hidden, so
    we shouldn't delete them either"). MkDocs' own page/asset writers
    never emit a dot-prefixed top-level path into site_dir either.

    Put those two facts together: a pre-existing destination whose
    TOP-LEVEL path component starts with "." (e.g. .nojekyll) can only be
    this hook's own output surviving from a previous run — never
    something MkDocs itself wrote during the current build — so it is
    safe, and necessary, to overwrite it silently. Without this, running
    `mkdocs build` twice in a row with no manual _site cleanup between
    runs fails permanently on the second run: build #1's .nojekyll
    survives clean_directory into build #2's post_build phase and would
    otherwise trip the collision guard below on every subsequent build.

    A pre-existing destination that is NOT dot-prefixed at the top level,
    by contrast, was necessarily deleted by clean_directory at the start
    of THIS build, so its presence now means MkDocs wrote it back during
    this same run — a genuine collision. That case still fails loudly,
    naming every colliding path, exactly as before.

    mkdocs.yml:
        hooks:
            - scripts/site_hooks/landing.py
"""
from __future__ import annotations

import os
import shutil
from pathlib import Path
from typing import Any

# scripts/site_hooks/landing.py -> scripts/site_hooks -> scripts -> repo root
REPO_ROOT = Path(__file__).resolve().parents[2]
SITE_SOURCE = REPO_ROOT / "site"


def _find_symlinks(source: Path) -> list[Path]:
    """Return every symlink (file or directory) under `source`, without
    descending into symlinked directories (followlinks=False), so a
    symlink cycle can't hang this scan."""
    offenders: list[Path] = []
    for root, dirs, files in os.walk(source, followlinks=False):
        root_path = Path(root)
        for name in (*dirs, *files):
            candidate = root_path / name
            if candidate.is_symlink():
                offenders.append(candidate)
    return offenders


def _remove(path: Path) -> None:
    """Remove a stale destination (file or directory) before overwriting it."""
    if path.is_dir() and not path.is_symlink():
        shutil.rmtree(path)
    else:
        path.unlink()


def on_post_build(config: dict[str, Any], **kwargs: Any) -> None:
    """Copy the bespoke landing page (site/) into the built docs output."""
    if not SITE_SOURCE.is_dir():
        raise FileNotFoundError(
            f"landing hook: expected the static landing page at {SITE_SOURCE!s}, "
            "but it does not exist. It is the site root and must ship with every build."
        )

    symlinks = _find_symlinks(SITE_SOURCE)
    if symlinks:
        listing = "\n".join(f"  {p.relative_to(SITE_SOURCE)}" for p in symlinks)
        raise RuntimeError(
            f"landing hook: refusing to publish — {SITE_SOURCE} contains symlink(s), "
            "which this hook will not follow or copy (a symlink could resolve outside "
            "the repository, or to something unexpectedly large or sensitive, with no "
            f"way for this hook to audit it first):\n{listing}"
        )

    site_dir = Path(config["site_dir"])
    site_dir.mkdir(parents=True, exist_ok=True)

    # Plan every (source file -> destination) copy first. A pre-existing
    # destination is only a genuine collision if its TOP-LEVEL path component
    # does NOT start with "." — see the module docstring for why a dot-prefixed
    # top-level survivor can only be this hook's own prior output, never a
    # same-run MkDocs write.
    planned: list[tuple[Path, Path]] = []
    collisions: list[str] = []
    for item in sorted(SITE_SOURCE.rglob("*")):
        if not item.is_file():
            continue
        relative = item.relative_to(SITE_SOURCE)
        destination = site_dir / relative
        is_own_prior_output = relative.parts[0].startswith(".")
        if destination.exists() and not is_own_prior_output:
            collisions.append(relative.as_posix())
        planned.append((item, destination))

    if collisions:
        listing = "\n".join(f"  {name}" for name in sorted(collisions))
        raise RuntimeError(
            "landing hook: refusing to overwrite — the MkDocs build already produced "
            f"the following path(s) under {site_dir} that {SITE_SOURCE} would also "
            f"write, so copying would silently clobber a build output instead of "
            f"failing loudly:\n{listing}\n"
            "Rename the colliding page/asset under docs/, or the file under site/, so "
            "the two namespaces stay disjoint."
        )

    for source_file, destination in planned:
        destination.parent.mkdir(parents=True, exist_ok=True)
        if destination.exists():
            # Only reachable for dot-prefixed top-level survivors (see above);
            # overwrite silently rather than raising, and rather than shipping
            # a build that quietly reused a previous run's copy of this file.
            _remove(destination)
        shutil.copy2(source_file, destination)
