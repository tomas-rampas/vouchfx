# vouchfx-site-tools

Shared static-site generator for the vouchfx ecosystem's four GitHub Pages
sites (`vouchfx`, `vouchfx-providers`, `vouchfx-samples`,
`vouchfx-telemetry-backend`). Each repository publishes a site that is the
same shape — a static landing page in `site/`, plus the repository's own
markdown rendered to matching styled HTML on every push — and previously
carried four near-identical copies of the rendering machinery in its own
`scripts/build_site.py`. This package is that machinery, consolidated once
(vouchfx issue [#200](https://github.com/tomas-rampas/vouchfx/issues/200)).

Each repository keeps its own thin `scripts/build_site.py` wrapper: it
supplies a `SiteConfig` (the doc set, the page/portal HTML templates, the
publication scoping) and calls `build(config, out)`. The doc set and
templates stay in the consuming repository — this package never contains
repository-specific content.

## Public API

```python
from vouchfx_site_tools import SiteConfig, build

config = SiteConfig(
    root=...,               # the repo root
    default_repo=...,       # "owner/repo" fallback for GitHub links
    docs=[...],              # (source path, nav group, label) tuples
    page_template=...,       # the per-page HTML template
    portal_html=...,         # the docs.html portal template
    meta_description_prefix=...,
    # optional: extra, skip, skip_prefixes, fact_overrides,
    # delete_facts_fallback, site_url — see the SiteConfig docstring.
)
build(config, out_dir)
```

The `site_url` optional parameter, when set, directs `build()` to emit
`robots.txt` and `sitemap.xml` with the site root as a bare origin URL; it
also provides a per-page `{canonical}` value for use in `page_template`.
Unset, the pre-existing behaviour applies: the module itself generates no
SEO files — though any hand-authored companions under `site/` (including a
`site/robots.txt`) still pass through to the output unchanged, as they
always have. When both exist, the generated files win (they are written
after the companion copy).

## How the satellite repos consume this package

In CI, a satellite repo's `pages.yml` installs it straight from the engine
repository, pinned to a specific commit:

```
python -m pip install markdown==3.10.2 pygments==2.20.0 \
  "vouchfx-site-tools @ git+https://github.com/tomas-rampas/vouchfx.git@<sha>#subdirectory=scripts/site-tools"
```

Bump the pinned `<sha>` deliberately when this package changes — it is not
tracked automatically, by design, so a breaking change in the engine repo
cannot silently break every satellite site's next build.

Two operational rules for the pin:

- Validate before merging: satellite `pages.yml` has no `pull_request`
  trigger, so run its `workflow_dispatch` on the PR branch and confirm a
  green build before merging any pin bump.
- If engine history is ever rewritten, re-pin all three satellites to
  surviving commits immediately — a GC'd pinned commit fails every
  satellite's next `git+` install at once (fail-loud; the previously
  deployed sites stay live meanwhile).

## Local development

As of the Material for MkDocs migration, the engine's own
site is built via `mkdocs build` with hooks that import `fetch_facts` and
`apply_facts` directly from this package. `scripts/build_site.py` remains
in-tree as the authoritative DOCS-list source for the legacy-redirect table
but no longer runs in the engine's CI. The three satellite repositories
(vouchfx-providers, vouchfx-samples, vouchfx-telemetry-backend) continue
using their thin `scripts/build_site.py` wrappers unchanged — their SHA pins
and wrapper logic are unaffected by the engine's MkDocs cutover.

A satellite repo's wrapper resolves the package in this order:

1. A plain `import vouchfx_site_tools` — this is what CI's pip install above
   satisfies. Caveat for local development: a previously pip-installed copy
   wins this step and silently shadows an edited sibling checkout — when
   iterating on this package itself, `pip uninstall vouchfx-site-tools` first
   (or work through the engine repo, which always imports off disk).
2. If that fails and the `VOUCHFX_SITE_TOOLS` environment variable is set, its
   value is inserted onto `sys.path` and the import is retried (point it at
   this directory's `src/`).
3. If that also fails, the sibling checkout `../vouchfx/scripts/site-tools/src`
   relative to the satellite repo is tried (the maintainer's usual local
   layout: all four repos checked out side by side).
4. Otherwise the wrapper exits with the exact `pip install` command to run.
