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
    docs=[...],              # (source path, nav group, label) tuples, each with
                              # an OPTIONAL 4th "description" element — see
                              # "llms.txt generation" below
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

`llms_curated` (default `False`) selects between two `llms.txt` output
shapes — see "llms.txt generation" below for the full contract.

## llms.txt generation

Set BOTH `site_url` and `llms_summary` to have `build()` additionally
write `<out>/llms.txt`, following the [llms.txt](https://llmstxt.org/)
convention:

```
# {project name — derived from default_repo's owner/repo-name segment}

> {llms_summary}

- [{project name}]({bare site_url}): {meta_description_prefix}
- [Documentation]({site_url}docs.html): {meta_description_prefix} documentation portal

## {group}
- [{label}]({absolute page URL}): {description}
...
```

One `## {group}` heading per group in `docs` (sorted by group, then by
label within a group), listing that group's pages — `docs` is the site's
curated, labelled nav, not every auto-discovered stray markdown file
`build()` also renders. Every link is absolute on `site_url`, including
the `docs.html` portal page. Each page's description is that entry's
OPTIONAL 4th tuple element when present (see "Public API" above); when
absent, it falls back to the pre-existing generic
`"{meta_description_prefix} — {label}"` text — 3-tuple `docs` entries need
no changes to keep working.

Leaving either `site_url` or `llms_summary` unset emits no `llms.txt`
(byte-identical to today) — `llms_summary` alone, with `site_url` unset,
is not enough, since every link in the file must be site_url-absolute.

**Precedence, same as `robots.txt`/`sitemap.xml`:** `build()`'s initial
`shutil.copytree(site, out)` always copies a hand-authored `site/llms.txt`
companion verbatim first; when `write_llms_txt` then runs (both fields
set), its generated `llms.txt` unconditionally overwrites that companion.

### `llms_curated` — issue #299 refinements

Set `llms_curated=True` (on top of `site_url` + `llms_summary`) to opt
into three follow-up fixes from the July 2026 SEO fleet wave's reviews,
all additive — `False`, the default, reproduces the shape above
byte-for-byte:

1. **Grammatical portal bullet.** The bare
   `"{meta_description_prefix} documentation portal"` concatenation
   becomes a real sentence: `"The documentation portal for {project
   name} — every guide and reference on this site."`
2. **`## Overview` heading.** Both intro bullets (the site's own index
   page, and the portal) move under a new `## Overview` heading, so every
   file list in the document sits under an H2 — matching
   [llms.txt.org](https://llmstxt.org/)'s own grammar, rather than
   floating above the first real section.
3. **Curated order.** `## {group}` sections appear in `docs`' own
   first-appearance order, and the pages within each group appear in
   `docs`' own declaration order — instead of both being alphabetised.
   This lets a repo's `docs` list put a deliberate entry point (e.g. a
   "Start" group) first in the file, rather than wherever it happens to
   sort.

For example, the same `docs` set as above, curated:

```
# {project name}

> {llms_summary}

## Overview
- [{project name}]({bare site_url}): {meta_description_prefix}
- [Documentation]({site_url}docs.html): The documentation portal for {project name} — every guide and reference on this site.

## {first group in docs' declaration order}
- [{label}]({absolute page URL}): {description}
...
```

These three changes are one flag, not three switches, because they were
filed and reviewed together as a single llms.txt readability fix. Like
`site_url`, this is a strict opt-in: leaving it unset (or omitting
`site_url`/`llms_summary` entirely) never changes existing output.

**Reserved group name:** with `llms_curated=True`, a `docs` entry whose
group matches `"Overview"` under a **case- and whitespace-insensitive**
comparison (`group.strip().casefold() == "overview"` — so `"overview"`,
`"OVERVIEW"`, and `" Overview "` all collide, not just an exact literal
`"Overview"`) collides with the intro section's own new `## Overview`
heading above — `write_llms_txt` raises `ValueError` naming both the
offending group and the full `docs` entry (e.g. `"SiteConfig.docs group
'OVERVIEW' collides with the llms_curated intro section 'Overview'
(case- and whitespace-insensitive match) — rename the group (entry
('docs/x.md', 'OVERVIEW', 'Label'))"`) rather than silently emitting two
`## Overview` sections. Any such group name is harmless when
`llms_curated` is left at its `False` default.

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

Opting a satellite into `llms_curated=True` is a plain pin bump plus one
`SiteConfig` kwarg in that repo's own `scripts/build_site.py` wrapper —
unlike `semantic_headings`, it needs no paired `site/docs.css` change,
since it only touches `llms.txt` content. The kwarg and the pin bump MUST
land in the SAME satellite PR: setting `llms_curated=True` against an
older pin (from before this field existed) raises `TypeError:
SiteConfig.__init__() got an unexpected keyword argument 'llms_curated'`
at build time, not a graceful no-op.

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
(specs/seo-fleet-audit.md): both new fields unset produces a byte-identical
tree to omitting them entirely (no `llms.txt`, sidebar group labels still
`<h4>`); `semantic_headings=True` yields exactly one `<h1>` per rendered
page and zero `<h4>` sidebar group labels; `llms.txt` follows the
llms.txt.org shape (H1, `>` blockquote summary, `## {group}` sections, the
`docs.html` portal link, and the optional per-page description) and is
absent unless both `site_url` and `llms_summary` are set. The same file
also locks the additive `SiteConfig.llms_curated` contract (issue #299):
`llms_curated` unset reproduces the pre-#299 `llms.txt` text byte-for-byte
against a hard-coded expected string (a deliberately-unsorted, three-group
fixture including a root-level doc, so alphabetisation and the root/`docs/`
path split can't pass by coincidence); `llms_curated=True` is checked the
same way, plus targeted assertions for the grammatical portal bullet, the
`## Overview` heading, and the curated (declaration) group/page order; a
`config.docs` group literally named `"Overview"` raises `ValueError` only
when `llms_curated` is True; the flag is proven to have no effect on any
other build output, and no effect at all when `site_url`/`llms_summary` is
unset; and the cross-version `origin/main` comparison below is exercised
with `llms_summary` set and both a default and a curated-shaped `docs`
list, so it actually compares generated `llms.txt` content, not just
robots.txt/sitemap.xml/rendered pages.

The byte-identical claim above is backed by TWO tests, deliberately, not
one: `test_new_fields_unset_is_deterministic_and_defaults_are_pinned`
proves `build()` is deterministic and the dataclass defaults are what they
claim to be, but — because both sides of that comparison call the SAME,
already-changed module — it cannot by itself prove the new code left the
PRE-EXISTING module's default output unchanged. That is what
`test_new_fields_unset_matches_pre_merge_origin_main_byte_for_byte` proves:
it extracts this module exactly as it existed at `origin/main` via
`git show` into an isolated import, builds the same fixture with both the
current and the baseline module, and asserts the trees are byte-for-byte
identical (parametrised over `site_url` set/unset, crossed with a default
and a curated-shaped `docs` list — four cases total; `llms_summary` is set
in every case so `write_llms_txt` actually runs whenever `site_url` is
also set). `llms_curated` itself is never passed on either side — the
baseline module predates the field, so passing it would raise `TypeError`
on that side; leaving it unset on the current side exercises exactly the
`False` default this EDGE-002 claim is about. It `pytest.skip`s rather
than fails when `origin/main` cannot be resolved (e.g. a shallow CI
checkout).

**Pre-merge vs. post-merge, stated plainly (do not oversell this once
merged):** that last test is only meaningful while `origin/main` still
points at the commit this branch actually diverged from. The moment this
branch merges, `origin/main` becomes (or fast-forwards to) this same
commit, so the "baseline" it extracts is thereafter byte-identical to the
current module by construction — the comparison passes trivially from then
on, proving nothing, until this module changes again. This is deliberate
(no CI workflow change, no separately pinned/maintained baseline SHA or
tag to keep in sync) — its continued passing after merge is NOT an
enforced backward-compatibility guarantee for future changes to this
module; it only ever verified one PR's changes against that PR's own
starting point.

To run locally, install the package with the test extra and invoke pytest:

```
pip install -e "scripts/site-tools[test]"
python -m pytest scripts/site-tools/tests -q
```

CI runs the suite automatically via the `site-tools-tests` job in
`.github/workflows/build.yml` on every push and pull request.
