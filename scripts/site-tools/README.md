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
`robots.txt` (allow-all, with a `Sitemap:` line pointing at the sitemap)
and `sitemap.xml`, whose root-page entry is listed as the bare origin URL
rather than `.../index.html`; it also provides a per-page `{canonical}`
value for use in `page_template`.
Unset, the pre-existing behaviour applies: the module itself generates no
SEO files — though any hand-authored companions under `site/` (including a
`site/robots.txt`) still pass through to the output unchanged, as they
always have. When both exist, the generated files win (they are written
after the companion copy).

Two further optional parameters were added for the fleet-wide SEO audit
(specs/seo-fleet-audit.md); both are additive — a consuming repo that
leaves them unset gets byte-identical output to today.

`semantic_headings` (default `False`) controls two independent things
about a rendered page's DOM, both aimed at fixing a page having no `<h1>`
and a sidebar navigation label wrongly appearing as a document heading:

- `False` (default, byte-identical to the pre-existing behaviour): a
  source Markdown page's own `# Title` renders `<h2>` (`TocExtension`'s
  `baselevel=2`), and each sidebar nav-group label renders `<h4>{group}</h4>`.
- `True`: a page's own `# Title` renders a real `<h1>` (`baselevel=1`), and
  each sidebar nav-group label renders the non-heading
  `<p class="doc-side__group">{group}</p>` instead — it is chrome (sidebar
  navigation), not page content, so it has no business in the heading
  outline.

Unlike `site_url`, flipping `semantic_headings` to `True` is a genuine
default-behaviour change the first time a consuming repo sets it: every
existing heading in a rendered page's DOM shifts down one level, and any
CSS in that repo's own `site/docs.css` targeting the sidebar group label
(e.g. `.doc-side h4`) must be renamed to match (`.doc-side__group`) in the
same change, or the visual result regresses. Roll it out satellite-by-
satellite, pairing the pin bump with that repo's own CSS rename in one PR.

`llms_summary` (default `None`) is the one-paragraph summary that goes
into `llms.txt` — see below.

## llms.txt generation

Set BOTH `site_url` and `llms_summary` to have `build()` additionally
write `<out>/llms.txt`, following the [llms.txt](https://llmstxt.org/)
convention: an H1 project name (derived from `default_repo`'s
`owner/repo-name` — the repo-name segment), the `llms_summary` paragraph,
then a linked list of `docs` (the site's curated, labelled nav — not every
auto-discovered stray markdown file `build()` also renders), every link
absolute on `site_url`. Leaving either field unset emits no `llms.txt`
(byte-identical to today) — `llms_summary` alone, with `site_url` unset,
is not enough, since every link in the file must be site_url-absolute.

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
- If engine history is ever rewritten, re-pin all four satellites to
  surviving commits immediately — a GC'd pinned commit fails every
  satellite's next `git+` install at once (fail-loud; the previously
  deployed sites stay live meanwhile).

## Local development

As of the Material for MkDocs migration, the engine's own
site is built via `mkdocs build` with hooks that import `fetch_facts` and
`apply_facts` directly from this package. `scripts/build_site.py` remains
in-tree as the authoritative DOCS-list source for the legacy-redirect table
but no longer runs in the engine's CI. The four satellite repositories
(vouchfx-providers, vouchfx-samples, vouchfx-telemetry-backend, vouchfx-mcp) continue
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
   layout: all five repos checked out side by side).
4. Otherwise the wrapper exits with the exact `pip install` command to run.

## Testing

A pytest suite at `scripts/site-tools/tests/test_site_url_contract.py` (nine test
functions, fifteen parametrised cases) locks the additive `SiteConfig.site_url`
contract against byte-identical mutation,
guarding the pinned-SHA dependency that each of the four satellite repositories
relies on (issue [#254](https://github.com/tomas-rampas/vouchfx/issues/254)).
The suite asserts both behavioural branches: when `site_url` is unset, no SEO
files are emitted and the module's output is byte-identical to omitting the
field; when set, `robots.txt` (allow-all with a sitemap reference), `sitemap.xml`
(with root entry as bare origin, not `.../index.html`), and per-page `canonical`
placeholders are generated and passed to templates.

A companion suite at
`scripts/site-tools/tests/test_semantic_headings_and_llms_txt.py` locks the
additive `SiteConfig.semantic_headings` and `SiteConfig.llms_summary` contracts
the same way (specs/seo-fleet-audit.md): both new fields unset produces a
byte-identical tree to omitting them entirely (no `llms.txt`, sidebar group
labels still `<h4>`); `semantic_headings=True` yields exactly one `<h1>` per
rendered page and zero `<h4>` sidebar group labels; `llms.txt` is emitted with
an H1 and site_url-absolute links only when both `site_url` and
`llms_summary` are set, and is absent if either is missing.

To run locally, install the package with the test extra and invoke pytest:

```
pip install -e "scripts/site-tools[test]"
python -m pytest scripts/site-tools/tests -q
```

CI runs the suite automatically via the `site-tools-tests` job in
`.github/workflows/build.yml` on every push and pull request.
