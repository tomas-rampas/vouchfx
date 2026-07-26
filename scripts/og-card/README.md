# og-card

Committed source for the fleet's `og:image` social-share cards (issue #300
item 3).

Each of the five vouchfx Pages sites — the engine (`vouchfx.io`) and the
four satellites (`samples.vouchfx.io`, `providers.vouchfx.io`,
`telemetry.vouchfx.io`, `vouchfx-mcp.vouchfx.io`) — ships a hand-produced
1200×630 PNG at `site/og-image.png`, referenced by the landing/portal
page's `og:image` and `twitter:image` meta tags. The engine card's history
(corrected here — an earlier version of this paragraph got it wrong):
#297's card was already REQ-004-conformant on dimensions and size
(1200×630, 49,818 bytes) — its actual defect was that it had no literal
accent-hex pixel anywhere in it, only an anti-aliased gradient. PR #298
fixed this by RE-RENDERING the card with a literal accent swatch added
(49,818 → 40,744 bytes), not by hand-editing pixels in an image editor —
there was simply no reusable source to regenerate from at the time. This
directory is that source: a parameterised HTML template plus a render
script, so every future edit is a text diff instead of a binary replace.

## Contents

- `template.html` — the 1200×630 card markup/styling, with `{{PLACEHOLDER}}`
  tokens for the text and one design token that vary per site. See the
  header comment in that file for the exact placeholder list.
- `render.py` — fills the placeholders, screenshots the result with headless
  Chromium via Playwright, and self-checks the output PNG against the
  conformance constraints below.

## One-time setup

```sh
pip install playwright
playwright install chromium
```

No other dependency is required — `render.py` otherwise uses only the
Python standard library (including for the self-check's PNG pixel scan;
see "How the self-check works" below).

## Usage

Engine defaults closely approximate the current engine card — see
"Reproduction fidelity" below before assuming byte-for-byte identity:

```sh
python scripts/og-card/render.py --force
```

With no other flags this targets `site/og-image.png` at the repo root (i.e.
this repo's own card) — `--out`'s default. Since that path already has a
committed card, `--force` is required, as shown above; drop `--force` only
when `--out` points at a path that doesn't exist yet. Pass `--out` to
target a different file, and the other flags to target a different site.

`render.py` refuses to overwrite an existing `--out` file unless `--force`
is passed, so a bare invocation cannot silently clobber a committed card —
pass `--force` deliberately once you've reviewed the result (see "After
regenerating a card" below).

### Per-site examples

Every fleet site currently ships the identical brand palette (violet
`#818cf8` → cyan `#22d3ee`), so `--accent` is left at its default below —
it exists so a future site can pass its own token without touching
`template.html`. Only `--site-name`, `--tagline`, `--domain`, and `--out`
vary today.

Engine (`vouchfx`):

```sh
python scripts/og-card/render.py \
  --site-name "vouchfx" \
  --tagline "End-to-end testing for distributed systems, authored in YAML." \
  --domain vouchfx.io \
  --out /path/to/vouchfx/site/og-image.png \
  --force
```

Samples (`vouchfx-samples`):

```sh
python scripts/og-card/render.py \
  --site-name "vouchfx samples" \
  --tagline "Four production-shaped applications in four stacks — clone, run one command, and watch a complete end-to-end suite pass against real containers." \
  --domain samples.vouchfx.io \
  --out /path/to/vouchfx-samples/site/og-image.png \
  --force
```

Providers (`vouchfx-providers`):

```sh
python scripts/og-card/render.py \
  --site-name "vouchfx providers" \
  --tagline "The vouchfx community provider hub — step providers (plugins) extending the end-to-end integration testing framework." \
  --domain providers.vouchfx.io \
  --out /path/to/vouchfx-providers/site/og-image.png \
  --force
```

Telemetry backend (`vouchfx-telemetry-backend`):

```sh
python scripts/og-card/render.py \
  --site-name "vouchfx telemetry" \
  --tagline "Opt-in, allowlist-only by construction, deletable on request — and off until you say otherwise." \
  --domain telemetry.vouchfx.io \
  --out /path/to/vouchfx-telemetry-backend/site/og-image.png \
  --force
```

MCP server (`vouchfx-mcp`):

```sh
python scripts/og-card/render.py \
  --site-name "vouchfx-mcp" \
  --tagline "A local stdio Model Context Protocol server that wraps the packaged vouchfx CLI — six tools and two documentation resources, so an agent works with .e2e.yaml integration-test suites directly." \
  --domain vouchfx-mcp.vouchfx.io \
  --out /path/to/vouchfx-mcp/site/og-image.png \
  --force
```

Always reuse each site's *existing* tagline (its `og:image:alt` /
meta-description opening) — REQ-004 forbids writing new copy for the card.

`--force` is shown above because these examples target a path that already
has a committed card. Drop it on a first run against a fresh path, or if
you want the safety check to confirm you're not about to overwrite
something unexpectedly.

## Reproduction fidelity

`render.py` with engine defaults **closely approximates** the current
`site/og-image.png`; it is not a byte-for-byte reproduction, and no version
of this tooling can guarantee one. Two distinct sources of drift:

- **Font rendering varies by machine.** `template.html` uses the same
  `system-ui` font stack `site/styles.css` ships with (see its `--sans`
  token) — deliberately, so the card matches the site's own typography and
  needs no network font fetch. But `system-ui` resolves to whatever font
  Chromium finds installed on the machine running the render (Segoe UI on
  this Windows box; a different font on Linux CI or macOS). Glyph advance
  widths differ across those fonts, so the exact line-wrap point of a given
  `--tagline` is not guaranteed identical across machines, even though the
  markup and CSS are unchanged.
- **Layout constants were tuned empirically, not derived from the font.**
  `.card__tagline`'s `width`/`font-size` in `template.html` were sized by
  measuring the committed `site/og-image.png`'s pixel bounding boxes (see
  the comment on that rule) and then verified with a Playwright MCP browser
  screenshot on this machine — they reproduce the committed engine card's
  exact wrap ("...authored in" / "YAML.") *here*, but a `--tagline` far
  longer or shorter than the five current fleet taglines, or a render on a
  machine with different font metrics, can wrap differently.

In practice this means: treat a freshly rendered card as "matches the
design, verify the wrap visually" rather than "guaranteed pixel-identical
to the last commit". `render.py`'s overflow guard (see below) catches the
failure mode that actually matters for REQ-004 — text silently clipped off
the card — but does not guarantee a specific wrap point.

## Conformance constraints (REQ-004)

`scripts/check_site.py`'s publication gate (`check_og_image_asset`) and
`specs/seo-fleet-audit.md` REQ-004 require every card to be:

- a static PNG, exactly **1200×630** pixels;
- **≤ 300 KB**;
- built from the accent colour defined as a hex value in that repo's
  `site/styles.css` token definitions (background is template-fixed and
  not configurable);
- referenced by `og:image` (absolute, same-domain URL), `og:image:width`,
  `og:image:height`, `og:image:alt`, and `twitter:image` with
  `twitter:card` set to `summary_large_image`.

`render.py` only produces the PNG; it does not touch the meta tags (those
live in each site's landing/portal HTML). It screenshots to a TEMPORARY
sibling file next to `--out` first, self-checks the first three
constraints against that temporary file, and only on success promotes it
to `--out` (an atomic `os.replace()`). Any failure exits non-zero with a
clear message, removes the temporary file, and leaves `--out` completely
untouched — a non-conforming render can never overwrite (or partially
overwrite) a previously committed, conformant card at `--out`, `--force`
or not.

### How the self-check works

1. **Dimensions** — parses the PNG's `IHDR` chunk directly (`struct` +
   `zlib`, no imaging library) and requires exactly 1200×630.
2. **Size** — compares the written file's byte size against the 300 KB cap.
3. **Accent presence** — decodes every `IDAT` scanline (un-filtering Sub/Up/
   Average/Paeth per the PNG spec) and scans **every pixel** — not a sample
   — for the `--accent` colour. This is deliberately a full scan: it is the
   exact check that would have caught the #298 regression (a card with the
   right background but no literal token-coloured element anywhere in it).

The decoder covers 8-bit, non-interlaced RGB/RGBA PNGs, which is what
Chromium's `page.screenshot()` produces; anything else fails the check with
an explicit "unsupported PNG format" message rather than mis-reading it.

A fourth check runs earlier, inside the browser, before the screenshot is
even taken: `template.html`'s `html`/`body` have `overflow: hidden`, which
would otherwise silently *clip* any content that doesn't fit the 1200×630
box (an over-length `--site-name`/`--tagline`/`--domain`, or a wrap that
runs past the bottom on an unusual font) instead of visibly breaking. The
dimension/size/accent checks above only ever see the already-clipped
1200×630 screenshot, so they cannot catch that on their own.
`render.py` asserts `document.body.scrollHeight <= 630` and
`document.body.scrollWidth <= 1200` via Playwright immediately before
screenshotting and fails loudly if content overflowed. This deliberately
reads `document.body`'s scroll dimensions, not `document.documentElement`'s
(i.e. `<html>`'s): `html` and `body` both have an explicit `height: 630px`
+ `overflow: hidden`, which fixes `<html>`'s own box at exactly 630px
regardless of what overflows *inside* `<body>` — an earlier version of this
guard read `document.documentElement.scrollHeight` and stayed pinned at
630 even when content massively overflowed (verified with a 29-line
tagline: `documentElement.scrollHeight` stayed at 630, `body.scrollHeight`
correctly reported 2229). `<body>`'s own `overflow: hidden` only affects
rendering, not what its `scrollHeight`/`scrollWidth` report, so reading
those instead correctly reflects the actual extent of its
absolutely-positioned children.

Note: the pixel scan proves the accent colour was successfully rendered
(catching the #298 regression where it was missing entirely); it does not
verify the hex value came from `site/styles.css`. That is the author's
responsibility when filling the `{{ACCENT}}` placeholder.

## After regenerating a card

1. Run the command above for the target site, writing to that repo's
   `site/og-image.png`.
2. Confirm `render.py` printed `OK` (dimensions, size, and accent all
   passed) — a non-zero exit means the card is not REQ-004 conformant and
   must not be committed.
3. Rebuild the site and re-run its publication gate:
   - **This repo (the engine):** `py -3.12 -m mkdocs build --strict` then
     `py -3.12 scripts/check_site.py _site` — NOT `scripts/build_site.py`;
     `.github/workflows/pages.yml` builds with MkDocs directly, and
     `build_site.py` no longer runs in CI. `check_og_image_asset` confirms
     the built landing page still references the asset correctly (REQ-004
     presence, dimensions, size — see "Conformance constraints" above).
   - **A satellite repo** (vouchfx-samples/providers/telemetry-backend/mcp):
     rebuild that repo's own site as it does today. None of the four
     satellites has a `check_site.py`-equivalent og-image gate yet — that
     is tracked as issue #304 — so this step is presence-only there until
     it lands.
4. Commit the regenerated `site/og-image.png` alongside any template change
   that produced it.
